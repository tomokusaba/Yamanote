#pragma once
#include <stdint.h>

enum class GpsState {
    NoUart, UartStale, BadNmea, NoFix, FixStale, DateWait, DateStale,
    TimeWait, TimeStale, SatellitesWait, SatellitesLow, HdopWait, HdopHigh, Ready
};

struct GpsSnapshot {
    uint32_t received;
    uint32_t checksums;
    uint32_t rxAge;
    bool locationValid;
    uint32_t locationAge;
    bool dateValid;
    uint32_t dateAge;
    uint16_t year;
    bool timeValid;
    uint32_t timeAge;
    bool satellitesValid;
    uint32_t satellites;
    bool hdopValid;
    int32_t hdop;
};

constexpr GpsState gpsState(const GpsSnapshot &s) {
    return s.received == 0 ? GpsState::NoUart :
        s.rxAge >= 2000 ? GpsState::UartStale :
        s.checksums == 0 ? GpsState::BadNmea :
        !s.locationValid ? GpsState::NoFix :
        s.locationAge >= 2000 ? GpsState::FixStale :
        !s.dateValid || s.year < 2024 ? GpsState::DateWait :
        s.dateAge >= 2000 ? GpsState::DateStale :
        !s.timeValid ? GpsState::TimeWait :
        s.timeAge >= 2000 ? GpsState::TimeStale :
        !s.satellitesValid ? GpsState::SatellitesWait :
        s.satellites < 4 ? GpsState::SatellitesLow :
        !s.hdopValid ? GpsState::HdopWait :
        s.hdop > 500 ? GpsState::HdopHigh : GpsState::Ready;
}

inline const char *gpsStateText(GpsState state) {
    switch (state) {
        case GpsState::NoUart: return "NO UART";
        case GpsState::UartStale: return "UART STALE";
        case GpsState::BadNmea: return "BAD NMEA";
        case GpsState::NoFix: return "NO FIX";
        case GpsState::FixStale: return "FIX STALE";
        case GpsState::DateWait: return "UTC DATE WAIT";
        case GpsState::DateStale: return "UTC DATE STALE";
        case GpsState::TimeWait: return "UTC TIME WAIT";
        case GpsState::TimeStale: return "UTC TIME STALE";
        case GpsState::SatellitesWait: return "SAT WAIT";
        case GpsState::SatellitesLow: return "SAT LOW";
        case GpsState::HdopWait: return "HDOP WAIT";
        case GpsState::HdopHigh: return "HDOP HIGH";
        case GpsState::Ready: return "FIX READY";
    }
    return "GPS STATE ERROR";
}
