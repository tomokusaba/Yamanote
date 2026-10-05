namespace WalkLogger.Application;

public static class TimestampText
{
    public static bool HasTimezone(string time) =>
        time.EndsWith('Z') || (time.Length >= 6 && time[^3] == ':' && time[^6] is '+' or '-');
}
