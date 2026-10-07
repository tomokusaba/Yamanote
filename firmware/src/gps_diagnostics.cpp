#include "gps_diagnostics.h"
#include <ArduinoJson.h>
#include <SD.h>
#include <M5Unified.h>
#include <esp_system.h>

bool GpsDiagnostics::begin(bool sdReady) {
    if (!sdReady || mutex_ == nullptr) {
        stop("SD unavailable; GPS diagnostics not saved");
        return false;
    }
    SdLock lock(mutex_);
    if (!SD.exists("/diagnostics") && !SD.mkdir("/diagnostics")) {
        stop("Cannot create /diagnostics");
        return false;
    }
    for (uint32_t id = 1; id <= 999999; ++id) {
        char path[40];
        snprintf(path, sizeof(path), "/diagnostics/boot-%06lu", static_cast<unsigned long>(id));
        if (SD.exists(path)) continue;
        if (!SD.mkdir(path)) {
            stop("Cannot create GPS diagnostic folder");
            return false;
        }
        folder_ = path;
        active_ = true;
        return true;
    }
    stop("GPS diagnostic folder numbers exhausted");
    return false;
}

void GpsDiagnostics::noteError(const String &error) {
    lastError_ = error;
    lastErrorMillis_ = millis();
    ++errorCount_;
}

void GpsDiagnostics::stop(const String &error) {
    active_ = false;
    diagnosticError_ = error;
    noteError(error);
    if (Serial) Serial.println("GPS DIAG ERROR: " + error);
}

bool GpsDiagnostics::append(const String &path, const uint8_t *bytes, size_t size) {
    File file = SD.open(path, FILE_APPEND);
    if (!file) {
        stop("Cannot open GPS diagnostic file");
        return false;
    }
    bool success = file.write(bytes, size) == size;
    file.flush();
    file.close();
    if (!success) stop("GPS diagnostic SD write failed");
    return success;
}

void GpsDiagnostics::flushRaw() {
    if (rawSize_ == 0) return;
    SdLock lock(mutex_);
    if (append(folder_ + "/uart.nmea", raw_, rawSize_)) rawSaved_ += rawSize_;
    else rawDropped_ += rawSize_;
    rawSize_ = 0;
}

void GpsDiagnostics::received(uint8_t byte) {
    lastRxMillis_ = millis();
    if (!active_) { ++rawDropped_; return; }
    raw_[rawSize_++] = byte;
    if (rawSize_ == sizeof(raw_)) flushRaw();
}

GpsSnapshot GpsDiagnostics::snapshot(TinyGPSPlus &gps) const {
    return {
        gps.charsProcessed(), gps.passedChecksum(), millis() - lastRxMillis_,
        gps.location.isValid(), gps.location.age(),
        gps.date.isValid(), gps.date.age(), gps.date.year(),
        gps.time.isValid(), gps.time.age(),
        gps.satellites.isValid(), gps.satellites.value(),
        gps.hdop.isValid(), gps.hdop.value()
    };
}

void GpsDiagnostics::report(TinyGPSPlus &gps, const GpsRuntime &runtime) {
    if (active_) flushRaw();
    const auto s = snapshot(gps);
    StaticJsonDocument<3072> doc;
    doc["schema"] = 1;
    doc["firmware"] = "gps-touch-20261007-115200";
    doc["event"] = runtime.event;
    doc["uptime_ms"] = millis();
    doc["reset_reason"] = static_cast<int>(esp_reset_reason());
    doc["rx_pin"] = runtime.rxPin;
    doc["tx_pin"] = runtime.txPin;
    doc["baud"] = runtime.baud;
    doc["interval_seconds"] = runtime.intervalSeconds;
    doc["gps_state"] = gpsStateText(gpsState(s));
    doc["fix_ready"] = gpsState(s) == GpsState::Ready;
    doc["rx_bytes"] = s.received;
    if (s.received > 0) doc["rx_age_ms"] = s.rxAge;
    doc["checksum_ok"] = s.checksums;
    doc["checksum_bad"] = gps.failedChecksum();
    doc["sentences_with_fix"] = gps.sentencesWithFix();
    doc["uart_errors"] = runtime.uartErrors;
    doc["last_uart_error"] = runtime.lastUartError;
    doc["location_valid"] = s.locationValid;
    if (s.locationValid) {
        doc["location_age_ms"] = s.locationAge;
        doc["last_lat"] = gps.location.lat();
        doc["last_lon"] = gps.location.lng();
    }
    doc["date_valid"] = s.dateValid;
    if (s.dateValid) {
        doc["date_age_ms"] = s.dateAge;
        doc["year"] = s.year;
        doc["month"] = gps.date.month();
        doc["day"] = gps.date.day();
    }
    doc["time_valid"] = s.timeValid;
    if (s.timeValid) {
        doc["time_age_ms"] = s.timeAge;
        doc["hour_utc"] = gps.time.hour();
        doc["minute_utc"] = gps.time.minute();
        doc["second_utc"] = gps.time.second();
    }
    if (s.satellitesValid) {
        doc["satellites"] = s.satellites;
        doc["satellites_age_ms"] = gps.satellites.age();
    }
    if (s.hdopValid) {
        doc["hdop"] = s.hdop / 100.0;
        doc["hdop_age_ms"] = gps.hdop.age();
    }
    doc["mode"] = runtime.mode;
    doc["points_saved"] = runtime.points;
    doc["photos_saved"] = runtime.photos;
    doc["segment"] = runtime.segment;
    doc["walk_folder"] = runtime.session;
    doc["message"] = runtime.message;
    doc["camera_ready"] = runtime.cameraReady;
    doc["camera_status"] = runtime.cameraStatus;
    doc["touch_enabled"] = runtime.touchEnabled;
    doc["touch_presses"] = runtime.touchPresses;
    doc["last_control"] = runtime.lastControl;
    if (runtime.touchPresses > 0) {
        doc["last_touch_uptime_ms"] = runtime.lastTouchMillis;
        doc["touch_x"] = runtime.touchX;
        doc["touch_y"] = runtime.touchY;
        doc["touch_raw_x"] = runtime.touchRawX;
        doc["touch_raw_y"] = runtime.touchRawY;
    }
    doc["psram_bytes"] = ESP.getPsramSize();
    doc["free_heap"] = ESP.getFreeHeap();
    doc["battery_percent"] = M5.Power.getBatteryLevel();
    doc["diagnostic_folder"] = folder_;
    doc["diagnostic_ready"] = active_;
    doc["diagnostic_error"] = diagnosticError_;
    doc["raw_bytes_saved"] = rawSaved_;
    doc["raw_bytes_dropped"] = rawDropped_;
    doc["error_count"] = errorCount_;
    doc["last_error"] = lastError_;
    doc["last_error_uptime_ms"] = lastErrorMillis_;
    if (doc.overflowed()) {
        stop("GPS diagnostic JSON capacity exceeded");
        return;
    }
    String line;
    serializeJson(doc, line);
    if (Serial) Serial.println("[gps] " + line);
    if (active_) {
        line += '\n';
        SdLock lock(mutex_);
        append(folder_ + "/status.jsonl", reinterpret_cast<const uint8_t *>(line.c_str()), line.length());
    }
}
