#pragma once
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>

class SdLock {
public:
    explicit SdLock(SemaphoreHandle_t mutex) : mutex_(mutex) { xSemaphoreTake(mutex_, portMAX_DELAY); }
    ~SdLock() { xSemaphoreGive(mutex_); }
    SdLock(const SdLock &) = delete;
    SdLock &operator=(const SdLock &) = delete;
private:
    SemaphoreHandle_t mutex_;
};
