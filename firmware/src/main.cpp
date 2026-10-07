#include <M5CoreS3.h>
#include <TinyGPS++.h>
#include <ArduinoJson.h>
#include <SPI.h>
#include <SD.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <esp_camera.h>
#include <esp_heap_caps.h>
#include <vector>
#include "protocol.h"
#include "gps_diagnostics.h"
#include "touch_control.h"

#if __has_include("config.local.h")
#include "config.local.h"
#endif

static_assert(LOG_INTERVAL_SECONDS == 5 || LOG_INTERVAL_SECONDS == 10, "Use a 5 or 10 second log interval.");

TinyGPSPlus gps;
HardwareSerial gpsSerial(1);
SemaphoreHandle_t sdMutex;
GpsDiagnostics gpsDiagnostics(sdMutex);
volatile uint32_t uartErrors = 0;
volatile uint32_t lastUartError = 0;
uint32_t lastDiagnosticMillis = 0;
GpsState lastGpsState = GpsState::NoUart;
BLECharacteristic *metaChar;
BLECharacteristic *dataChar;
BLEServer *bleServer;
bool sdReady = false;
bool cameraReady = false;
String cameraStatus = "Not initialized";
bool recording = false;
volatile bool syncEnabled = false;
volatile bool connected = false;
uint32_t syncStarted = 0;
uint32_t lastLogMillis = 0;
uint32_t lastDrawMillis = 0;
uint32_t touchPresses = 0;
uint32_t lastTouchMillis = 0;
int touchX = -1;
int touchY = -1;
int touchRawX = -1;
int touchRawY = -1;
const char *lastControl = "NONE";
bool messageIsError = false;
uint32_t pointCount = 0;
uint32_t photoCount = 0;
uint32_t segment = 0;
String sessionFolder;
String lastTimestamp;
String message = "Waiting for GPS UTC fix";
String catalog;
std::vector<String> catalogPaths;
File transferFile;
bool transferCatalog = false;
uint32_t transferSize = 0;
uint32_t transferOffset = 0;

uint32_t crcUpdate(uint32_t crc, const uint8_t *data, size_t size) {
    for (size_t i = 0; i < size; ++i) {
        crc ^= data[i];
        for (int j = 0; j < 8; ++j) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320u : 0u);
    }
    return crc;
}

bool goodFix() {
    return gpsState(gpsDiagnostics.snapshot(gps)) == GpsState::Ready;
}

String timestamp() {
    char text[25];
    snprintf(text, sizeof(text), "%04d-%02d-%02dT%02d:%02d:%02dZ",
             gps.date.year(), gps.date.month(), gps.date.day(),
             gps.time.hour(), gps.time.minute(), gps.time.second());
    return String(text);
}

void fail(const String &text) {
    message = text;
    messageIsError = true;
    gpsDiagnostics.noteError(text);
    if (Serial) Serial.println("ERROR: " + text);
}

void reportGps(const char *event) {
    GpsRuntime runtime = {
        event, recording ? "recording" : (syncEnabled ? "ble_sync" : "paused"),
        pointCount, photoCount, segment, sessionFolder.c_str(), message.c_str(),
        cameraReady, cameraStatus.c_str(), uartErrors, lastUartError,
        GPS_RX_PIN, GPS_TX_PIN, GPS_BAUD, LOG_INTERVAL_SECONDS,
        CoreS3.Touch.isEnabled(), touchPresses, lastTouchMillis,
        touchX, touchY, touchRawX, touchRawY, lastControl
    };
    gpsDiagnostics.report(gps, runtime);
    lastDiagnosticMillis = millis();
}

bool appendRecord(const String &path, JsonDocument &doc) {
    File file = SD.open(path, FILE_APPEND);
    if (!file) {
        fail("SD open failed; recording stopped");
        recording = false;
        return false;
    }
    String line;
    serializeJson(doc, line);
    line += '\n';
    bool success = file.write(reinterpret_cast<const uint8_t *>(line.c_str()), line.length()) == line.length();
    file.flush();
    file.close();
    if (!success) {
        fail("SD write failed; recording stopped");
        recording = false;
    }
    return success;
}

bool prepareSession() {
    if (!sessionFolder.isEmpty()) return true;
    String name = timestamp();
    name.replace("-", "");
    name.replace(":", "");
    sessionFolder = "/walks/" + name;
    if (SD.exists(sessionFolder)) {
        sessionFolder = "";
        fail("Session exists; wait one second");
        return false;
    }
    if (!SD.mkdir(sessionFolder)) {
        sessionFolder = "";
        fail("Cannot create SD session");
        return false;
    }
    return true;
}

void logPoint() {
    messageIsError = false;
    if (!goodFix()) {
        message = String("Not logging: ") + gpsStateText(gpsState(gpsDiagnostics.snapshot(gps)));
        return;
    }
    String now = timestamp();
    if (!lastTimestamp.isEmpty() && now <= lastTimestamp) {
        message = "GPS UTC not advancing";
        return;
    }
    SdLock lock(sdMutex);
    if (!prepareSession()) { recording = false; return; }
    StaticJsonDocument<256> doc;
    doc["time"] = now;
    doc["lat"] = gps.location.lat();
    doc["lon"] = gps.location.lng();
    doc["speed"] = gps.speed.isValid() ? gps.speed.kmph() : 0;
    doc["segment"] = segment;
    if (gps.altitude.isValid()) doc["altitude"] = gps.altitude.meters();
    if (appendRecord(sessionFolder + "/track.ndjson", doc)) {
        ++pointCount;
        lastTimestamp = now;
        message = "Saved GPS point";
    }
}

void takePhoto() {
    if (!recording || !goodFix() || sessionFolder.isEmpty()) {
        fail("Start logging with GPS before photo");
        return;
    }
    if (!cameraReady || !CoreS3.Camera.get()) {
        fail("Camera unavailable");
        return;
    }
    uint8_t *jpeg = nullptr;
    size_t size = 0;
    bool converted = frame2jpg(CoreS3.Camera.fb, 80, &jpeg, &size);
    CoreS3.Camera.free();
    if (!converted || jpeg == nullptr || size == 0) {
        if (jpeg) free(jpeg);
        fail("JPEG conversion failed");
        return;
    }
    SdLock lock(sdMutex);
    char filename[32];
    snprintf(filename, sizeof(filename), "IMG_%06lu.jpg", static_cast<unsigned long>(photoCount + 1));
    String path = sessionFolder + "/" + filename;
    File file = SD.open(path, FILE_WRITE);
    bool ok = file && file.write(jpeg, size) == size;
    if (file) { file.flush(); file.close(); }
    free(jpeg);
    if (!ok) {
        fail("Photo SD write failed");
        return;
    }
    StaticJsonDocument<256> doc;
    doc["time"] = timestamp();
    doc["lat"] = gps.location.lat();
    doc["lon"] = gps.location.lng();
    doc["image"] = filename;
    if (appendRecord(sessionFolder + "/photos.ndjson", doc)) {
        ++photoCount;
        message = "Photo saved with GPS";
    }
}

void reply(bool ok, const String &error = "", uint32_t size = 0, uint32_t crc = 0) {
    StaticJsonDocument<256> doc;
    doc["ok"] = ok;
    doc["version"] = 1;
    if (ok) { doc["size"] = size; doc["crc32"] = crc; }
    else doc["error"] = error;
    String text;
    serializeJson(doc, text);
    metaChar->setValue(text.c_str());
}

bool buildCatalog() {
    catalog = "";
    catalogPaths.clear();
    File root = SD.open("/walks");
    if (!root || !root.isDirectory()) return false;
    File dir = root.openNextFile();
    while (dir) {
        if (dir.isDirectory()) {
            File file = dir.openNextFile();
            while (file) {
                String name = file.name();
                int slash = name.lastIndexOf('/');
                if (slash >= 0) name = name.substring(slash + 1);
                if (!file.isDirectory() && (name.endsWith(".ndjson") || name.endsWith(".jpg"))) {
                    if (catalogPaths.size() >= 4096 || catalog.length() > 512000) return false;
                    String path = file.path();
                    catalogPaths.push_back(path);
                    StaticJsonDocument<256> doc;
                    doc["id"] = catalogPaths.size() - 1;
                    doc["path"] = path.substring(7); // Strip "/walks/"; Windows validates every component.
                    doc["size"] = file.size();
                    String line;
                    serializeJson(doc, line);
                    catalog += line + '\n';
                }
                file.close();
                file = dir.openNextFile();
            }
        }
        dir.close();
        dir = root.openNextFile();
    }
    return true;
}

class CommandCallbacks : public BLECharacteristicCallbacks {
    void onWrite(BLECharacteristic *characteristic) override {
        SdLock lock(sdMutex);
        if (!syncEnabled || recording) { reply(false, "Enable BLE while paused"); return; }
        std::string bytes = characteristic->getValue();
        String command(bytes.c_str());
        if (command == "CAT") {
            transferFile.close();
            transferCatalog = false;
            if (!buildCatalog()) { reply(false, "Catalog failed or exceeds limits"); return; }
            transferCatalog = true;
            transferSize = catalog.length();
            transferOffset = 0;
            reply(true, "", transferSize, ~crcUpdate(0xffffffff, reinterpret_cast<const uint8_t *>(catalog.c_str()), transferSize));
        } else if (command.startsWith("GET ")) {
            char *end;
            unsigned long id = strtoul(command.c_str() + 4, &end, 10);
            if (*end != 0 || command.length() <= 4 || id >= catalogPaths.size()) { reply(false, "Invalid file id"); return; }
            transferFile.close();
            transferCatalog = false;
            transferFile = SD.open(catalogPaths[id], FILE_READ);
            if (!transferFile) { reply(false, "File open failed"); return; }
            transferSize = transferFile.size();
            uint8_t buffer[1024];
            uint32_t crc = 0xffffffff;
            size_t count;
            uint32_t total = 0;
            while ((count = transferFile.read(buffer, sizeof(buffer))) > 0) {
                crc = crcUpdate(crc, buffer, count);
                total += count;
            }
            if (total != transferSize || !transferFile.seek(0)) { reply(false, "SD checksum read failed"); return; }
            transferOffset = 0;
            reply(true, "", transferSize, ~crc);
        } else if (command.startsWith("READ ")) {
            char *end;
            unsigned long offset = strtoul(command.c_str() + 5, &end, 10);
            if (*end != 0 || command.length() <= 5 || offset > transferSize ||
                (!transferCatalog && !transferFile)) { reply(false, "Invalid offset or no file"); return; }
            transferOffset = offset;
            uint8_t packet[184];
            for (int i = 0; i < 4; ++i) packet[i] = (offset >> (8 * i)) & 0xff;
            // One ATT read fits even the default MTU. Larger negotiated MTUs reduce round trips.
            uint16_t mtu = bleServer->getPeerMTU(bleServer->getConnId());
            size_t payload = min(static_cast<size_t>(180), static_cast<size_t>(mtu > 5 ? mtu - 5 : 16));
            size_t expected = min(payload, static_cast<size_t>(transferSize - offset));
            size_t count = 0;
            if (transferCatalog) {
                memcpy(packet + 4, catalog.c_str() + offset, expected);
                count = expected;
            } else if (transferFile.seek(offset)) {
                count = transferFile.read(packet + 4, expected);
            }
            if (count != expected) { reply(false, "SD data read failed"); return; }
            dataChar->setValue(packet, count + 4);
            reply(true, "", transferSize);
        } else {
            reply(false, "Unknown command");
        }
    }
};

class ServerCallbacks : public BLEServerCallbacks {
    void onConnect(BLEServer *) override { connected = true; }
    void onDisconnect(BLEServer *) override {
        connected = false;
        if (syncEnabled) BLEDevice::startAdvertising();
    }
};

void initializeCamera() {
    Serial.printf("[camera] PSRAM found=%d total=%lu free=%lu largest=%lu; internal free=%lu largest=%lu\n",
        psramFound(), static_cast<unsigned long>(ESP.getPsramSize()),
        static_cast<unsigned long>(ESP.getFreePsram()), static_cast<unsigned long>(ESP.getMaxAllocPsram()),
        static_cast<unsigned long>(heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT)),
        static_cast<unsigned long>(heap_caps_get_largest_free_block(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT)));
    if (!psramFound() || ESP.getPsramSize() == 0) {
        cameraStatus = "PSRAM unavailable";
        fail("Camera needs PSRAM; see startup log");
        return;
    }
    cameraReady = CoreS3.Camera.begin();
    if (!cameraReady) {
        cameraStatus = "Init failed";
        fail("Camera init failed; GPS still usable");
        return;
    }
    if (!CoreS3.Camera.get()) {
        cameraReady = false;
        cameraStatus = "Capture failed";
        fail("Camera frame test failed; GPS still usable");
        return;
    }
    Serial.printf("[camera] Frame OK: %ux%u format=%d bytes=%lu\n",
        static_cast<unsigned int>(CoreS3.Camera.fb->width), static_cast<unsigned int>(CoreS3.Camera.fb->height),
        CoreS3.Camera.fb->format, static_cast<unsigned long>(CoreS3.Camera.fb->len));
    CoreS3.Camera.free();
    cameraStatus = "Ready";
}

void setup() {
    Serial.begin(115200);
    uint32_t serialStarted = millis();
    while (!Serial && millis() - serialStarted < 3000) delay(10);
    auto cfg = M5.config();
    cfg.serial_baudrate = 115200;
    CoreS3.begin(cfg);
    Serial.printf("[boot] WalkLogger gps-touch-20261007-115200; board=%u GPS baud=%u\n",
        static_cast<unsigned int>(M5.getBoard()), static_cast<unsigned int>(GPS_BAUD));
    CoreS3.Display.setRotation(1);
    CoreS3.Display.setTextSize(1);
    CoreS3.Display.setTextColor(WHITE, BLACK);
    sdMutex = xSemaphoreCreateMutex();
    if (sdMutex == nullptr) {
        fail("SD mutex allocation failed");
        return;
    }
    SPI.begin(36, 35, 37, 4);
    sdReady = SD.begin(4, SPI, 25000000);
    if (!sdReady || SD.cardType() == CARD_NONE || (!SD.exists("/walks") && !SD.mkdir("/walks"))) {
        sdReady = false;
        fail("Insert FAT32 SD card and restart");
    }
    gpsDiagnostics.begin(sdReady);
    initializeCamera();
    if (gpsSerial.setRxBufferSize(2048) != 2048) fail("GPS RX buffer configuration failed");
    gpsSerial.begin(GPS_BAUD, SERIAL_8N1, GPS_RX_PIN, GPS_TX_PIN);
    if (!gpsSerial) fail("GPS UART initialization failed");
    gpsSerial.onReceiveError([](hardwareSerial_error_t error) {
        ++uartErrors;
        lastUartError = static_cast<uint32_t>(error);
    });
    BLEDevice::init("WalkLogger-CoreS3");
    BLEDevice::setMTU(247);
    bleServer = BLEDevice::createServer();
    bleServer->setCallbacks(new ServerCallbacks());
    BLEService *service = bleServer->createService(WALK_SERVICE_UUID);
    auto command = service->createCharacteristic(WALK_COMMAND_UUID, BLECharacteristic::PROPERTY_WRITE);
    command->setCallbacks(new CommandCallbacks());
    metaChar = service->createCharacteristic(WALK_META_UUID, BLECharacteristic::PROPERTY_READ);
    dataChar = service->createCharacteristic(WALK_DATA_UUID, BLECharacteristic::PROPERTY_READ);
    reply(false, "Enable BLE on device");
    service->start();
    auto advertising = BLEDevice::getAdvertising();
    advertising->addServiceUUID(WALK_SERVICE_UUID);
    advertising->setScanResponse(true);
    reportGps("boot");
}

void draw() {
    CoreS3.Display.fillScreen(BLACK);
    CoreS3.Display.setCursor(10, 10);
    CoreS3.Display.setTextSize(2);
    CoreS3.Display.println("WalkLogger");
    CoreS3.Display.setTextSize(1);
    const auto state = gpsState(gpsDiagnostics.snapshot(gps));
    CoreS3.Display.setTextColor(state == GpsState::Ready ? GREEN : YELLOW, BLACK);
    CoreS3.Display.setCursor(10, 36);
    CoreS3.Display.printf("GPS: %s   RX: %lu", gpsStateText(state), static_cast<unsigned long>(gps.charsProcessed()));
    CoreS3.Display.setTextColor(WHITE, BLACK);
    CoreS3.Display.setCursor(10, 48);
    CoreS3.Display.printf("NMEA OK:%lu BAD:%lu UARTerr:%lu",
        static_cast<unsigned long>(gps.passedChecksum()), static_cast<unsigned long>(gps.failedChecksum()),
        static_cast<unsigned long>(uartErrors));
    CoreS3.Display.setCursor(10, 60);
    CoreS3.Display.printf("SAT:%s HDOP:%s  RX%d TX%d %dbaud",
        gps.satellites.isValid() ? String(gps.satellites.value()).c_str() : "--",
        gps.hdop.isValid() ? String(gps.hdop.hdop(), 1).c_str() : "--",
        GPS_RX_PIN, GPS_TX_PIN, GPS_BAUD);
    CoreS3.Display.setCursor(10, 72);
    CoreS3.Display.printf("Mode: %s   Points %lu   Photos %lu", recording ? "RECORD" : (syncEnabled ? "BLE SYNC" : "PAUSED"),
        static_cast<unsigned long>(pointCount), static_cast<unsigned long>(photoCount));
    CoreS3.Display.setCursor(10, 84);
    CoreS3.Display.printf("Battery: %d%%  Interval: %ds  BLE:%s", CoreS3.Power.getBatteryLevel(), LOG_INTERVAL_SECONDS,
        connected ? "connected" : (syncEnabled ? "visible" : "off"));
    CoreS3.Display.setCursor(10, 96);
    CoreS3.Display.printf("Camera: %s  SD:%s DIAG:%s", cameraStatus.c_str(),
        sdReady ? "OK" : "ERR", gpsDiagnostics.ready() ? "OK" : "ERR");
    CoreS3.Display.setCursor(10, 108);
    if (!gpsDiagnostics.error().isEmpty()) {
        CoreS3.Display.setTextColor(RED, BLACK);
        CoreS3.Display.print(gpsDiagnostics.error().substring(0, 49));
        CoreS3.Display.setTextColor(WHITE, BLACK);
    } else if (gpsDiagnostics.ready()) {
        CoreS3.Display.printf("Logs: %s", gpsDiagnostics.folder().c_str());
    }
    if (goodFix()) {
        CoreS3.Display.setCursor(10, 120);
        CoreS3.Display.printf("%.6f, %.6f", gps.location.lat(), gps.location.lng());
        CoreS3.Display.setCursor(10, 132);
        CoreS3.Display.printf("UTC %s", timestamp().c_str());
    }
    CoreS3.Display.setCursor(10, 144);
    CoreS3.Display.printf("Touch:%lu %s (%d,%d)", static_cast<unsigned long>(touchPresses), lastControl, touchX, touchY);
    CoreS3.Display.setCursor(10, 156);
    CoreS3.Display.setTextSize(2);
    CoreS3.Display.setTextColor(messageIsError ? RED : WHITE, BLACK);
    CoreS3.Display.print(message.substring(0, 49));
    CoreS3.Display.setTextSize(1);
    CoreS3.Display.setTextColor(WHITE, BLACK);
    const char *labels[] = {recording ? "PAUSE" : "START", "PHOTO", "NEW", syncEnabled ? "BLE OFF" : "BLE ON"};
    for (int i = 0; i < 4; ++i) {
        CoreS3.Display.drawRect(i * 80, 190, 80, 48, syncEnabled && i == 3 ? GREEN : WHITE);
        CoreS3.Display.drawString(labels[i], i * 80 + 10, 208);
    }
}

void loop() {
    CoreS3.update();
    M5.Power.setBatteryCharge(true);
    while (gpsSerial.available()) {
        int received = gpsSerial.read();
        if (received < 0) break;
        gps.encode(static_cast<char>(received));
        gpsDiagnostics.received(static_cast<uint8_t>(received));
    }
    if (CoreS3.Touch.getCount()) {
        auto touch = CoreS3.Touch.getDetail();
        if (touch.wasPressed()) {
            ++touchPresses;
            lastTouchMillis = millis();
            touchX = touch.x;
            touchY = touch.y;
            auto raw = CoreS3.Touch.getTouchPointRaw();
            touchRawX = raw.x;
            touchRawY = raw.y;
            int action = controlAt(touch.x, touch.y);
            const char *controls[] = {recording ? "PAUSE" : "START", "PHOTO", "NEW", syncEnabled ? "BLE OFF" : "BLE ON"};
            lastControl = action < 0 ? "OUTSIDE" : controls[action];
            if (action >= 0) messageIsError = false;
            if (action == 0) {
                if (syncEnabled) fail("Turn BLE off before recording");
                else if (!sdReady) fail("SD unavailable");
                else if (!recording && !goodFix())
                    fail(String("Cannot start: ") + gpsStateText(gpsState(gpsDiagnostics.snapshot(gps))));
                else {
                    recording = !recording;
                    if (recording) { ++segment; lastLogMillis = millis() - LOG_INTERVAL_SECONDS * 1000; }
                    message = recording ? "Recording" : "Paused; SD records flushed";
                }
            } else if (action == 1) {
                takePhoto();
            } else if (action == 2) {
                if (recording || syncEnabled) fail("Pause and turn BLE off first");
                else { sessionFolder = ""; pointCount = photoCount = segment = 0; lastTimestamp = ""; message = "New walk ready"; }
            } else if (action == 3) {
                if (recording || !sdReady) fail("Pause recording before BLE sync");
                else {
                    SdLock lock(sdMutex);
                    syncEnabled = !syncEnabled;
                    if (syncEnabled) { syncStarted = millis(); BLEDevice::startAdvertising(); message = "BLE visible; use trusted PC"; }
                    else {
                        BLEDevice::stopAdvertising();
                        if (connected) bleServer->disconnect(bleServer->getConnId());
                        transferFile.close();
                        transferCatalog = false;
                        message = "BLE disabled";
                    }
                }
            }
            lastDrawMillis = millis();
            draw();
            reportGps(action < 0 ? "touch" : "control");
        }
    }
    if (syncEnabled && millis() - syncStarted >= 30UL * 60 * 1000) {
        SdLock lock(sdMutex);
        syncEnabled = false;
        BLEDevice::stopAdvertising();
        if (connected) bleServer->disconnect(bleServer->getConnId());
        transferFile.close();
        transferCatalog = false;
        message = "BLE window expired; enable again";
        messageIsError = false;
    }
    if (recording && millis() - lastLogMillis >= LOG_INTERVAL_SECONDS * 1000) {
        lastLogMillis = millis();
        logPoint();
    }
    const auto currentGpsState = gpsState(gpsDiagnostics.snapshot(gps));
    if (currentGpsState != lastGpsState || millis() - lastDiagnosticMillis >= 5000) {
        const bool changed = currentGpsState != lastGpsState;
        lastGpsState = currentGpsState;
        reportGps(changed ? "gps_state_changed" : "status");
    }
    if (millis() - lastDrawMillis >= 3000) { lastDrawMillis = millis(); draw(); }
    delay(5);
}
