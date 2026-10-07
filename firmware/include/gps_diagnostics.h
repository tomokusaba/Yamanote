#pragma once
#include <Arduino.h>
#include <TinyGPS++.h>
#include "gps_state.h"
#include "sd_lock.h"

struct GpsRuntime {
    const char *event;
    const char *mode;
    uint32_t points;
    uint32_t photos;
    uint32_t segment;
    const char *session;
    const char *message;
    bool cameraReady;
    const char *cameraStatus;
    uint32_t uartErrors;
    uint32_t lastUartError;
    int rxPin;
    int txPin;
    uint32_t baud;
    uint32_t intervalSeconds;
    bool touchEnabled;
    uint32_t touchPresses;
    uint32_t lastTouchMillis;
    int touchX;
    int touchY;
    int touchRawX;
    int touchRawY;
    const char *lastControl;
};

class GpsDiagnostics {
public:
    explicit GpsDiagnostics(SemaphoreHandle_t &mutex) : mutex_(mutex) {}
    bool begin(bool sdReady);
    void received(uint8_t byte);
    void noteError(const String &error);
    GpsSnapshot snapshot(TinyGPSPlus &gps) const;
    void report(TinyGPSPlus &gps, const GpsRuntime &runtime);
    bool ready() const { return active_; }
    const String &folder() const { return folder_; }
    const String &error() const { return diagnosticError_; }
private:
    bool append(const String &path, const uint8_t *bytes, size_t size);
    void flushRaw();
    void stop(const String &error);
    SemaphoreHandle_t &mutex_;
    bool active_ = false;
    String folder_;
    String diagnosticError_;
    String lastError_;
    uint32_t errorCount_ = 0;
    uint32_t lastErrorMillis_ = 0;
    uint32_t lastRxMillis_ = 0;
    uint64_t rawSaved_ = 0;
    uint64_t rawDropped_ = 0;
    uint8_t raw_[4096];
    size_t rawSize_ = 0;
};
