#include "gps_state.h"

#define GPS_CHECK(expected, ...) \
    static_assert(gpsState(GpsSnapshot{__VA_ARGS__}) == GpsState::expected, "GPS state check: " #expected)

GPS_CHECK(NoUart, 0, 0, 0, false, 0, false, 0, 0, false, 0, false, 0, false, 0);
GPS_CHECK(UartStale, 100, 2, 2000, true, 0, true, 0, 2026, true, 0, true, 4, true, 500);
GPS_CHECK(BadNmea, 100, 0, 0, false, 0, false, 0, 0, false, 0, false, 0, false, 0);
GPS_CHECK(NoFix, 100, 2, 0, false, 0, true, 0, 2026, true, 0, true, 4, true, 500);
GPS_CHECK(FixStale, 100, 2, 0, true, 2000, true, 0, 2026, true, 0, true, 4, true, 500);
GPS_CHECK(DateWait, 100, 2, 0, true, 0, false, 0, 2026, true, 0, true, 4, true, 500);
GPS_CHECK(DateWait, 100, 2, 0, true, 0, true, 0, 2023, true, 0, true, 4, true, 500);
GPS_CHECK(DateStale, 100, 2, 0, true, 0, true, 2000, 2026, true, 0, true, 4, true, 500);
GPS_CHECK(TimeWait, 100, 2, 0, true, 0, true, 0, 2026, false, 0, true, 4, true, 500);
GPS_CHECK(TimeStale, 100, 2, 0, true, 0, true, 0, 2026, true, 2000, true, 4, true, 500);
GPS_CHECK(SatellitesWait, 100, 2, 0, true, 0, true, 0, 2026, true, 0, false, 4, true, 500);
GPS_CHECK(SatellitesLow, 100, 2, 0, true, 0, true, 0, 2026, true, 0, true, 3, true, 500);
GPS_CHECK(HdopWait, 100, 2, 0, true, 0, true, 0, 2026, true, 0, true, 4, false, 500);
GPS_CHECK(HdopHigh, 100, 2, 0, true, 0, true, 0, 2026, true, 0, true, 4, true, 501);
GPS_CHECK(Ready, 100, 2, 1999, true, 1999, true, 1999, 2024, true, 1999, true, 4, true, 500);
GPS_CHECK(Ready, 100, 2, 0, true, 0, true, 0, 2026, true, 0, true, 9, true, 90);

#undef GPS_CHECK
