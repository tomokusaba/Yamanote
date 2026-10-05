using System.Globalization;

namespace WalkLogger.Core;

public sealed record TrackPoint(DateTimeOffset Time, double Lat, double Lon, double Speed = 0,
    double? Altitude = null, int Segment = 0)
{
    public void Validate()
    {
        if (!double.IsFinite(Lat) || !double.IsFinite(Lon) || Lat is < -90 or > 90 || Lon is < -180 or > 180 ||
            !double.IsFinite(Speed) || Speed < 0 || (Altitude.HasValue && !double.IsFinite(Altitude.Value)) ||
            Time == default || Segment < 0)
            throw new InvalidDataException("GPS点の日時・座標・速度・区間番号が不正です。");
    }
}

public sealed record PhotoRecord(DateTimeOffset Time, double Lat, double Lon, string Image, string Note = "");
public sealed record PlaceLabel(double Lat, double Lon, string Name);

public sealed class WalkSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceHash { get; set; } = "";
    public string Title { get; set; } = "街歩き";
    public bool IsDemo { get; set; }
    public bool CompletedLoop { get; set; }
    public string Notes { get; set; } = "";
    public string Blog { get; set; } = "";
    public List<TrackPoint> Points { get; set; } = [];
    public List<PhotoRecord> Photos { get; set; } = [];
    public List<PlaceLabel> Places { get; set; } = [];
    public WalkStats Stats => WalkAnalysis.Calculate(Points);
    public string DisplayTitle => (IsDemo ? "[サンプル] " : "") + Title;
    public string DisplayDate => Points.Count == 0 ? "" : Points[0].Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string DisplayStats => $"{Stats.DistanceKm:F2} km  /  {WalkAnalysis.FormatDuration(Stats.Elapsed)}  /  写真 {Photos.Count}枚";
}

public sealed record WalkStats(double DistanceKm, TimeSpan Elapsed, TimeSpan Recorded, TimeSpan Moving,
    int PointCount, int SegmentCount);

public static class WalkAnalysis
{
    public static bool Connected(TrackPoint a, TrackPoint b) =>
        a.Segment == b.Segment && b.Time > a.Time && (b.Time - a.Time).TotalSeconds <= 60 &&
        DistanceMeters(a.Lat, a.Lon, b.Lat, b.Lon) / (b.Time - a.Time).TotalSeconds <= 12;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double radians = Math.PI / 180;
        var p1 = lat1 * radians;
        var p2 = lat2 * radians;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * radians / 2), 2) +
                Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin((lon2 - lon1) * radians / 2), 2);
        return 6371008.8 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
    }

    public static List<List<TrackPoint>> Segments(IReadOnlyList<TrackPoint> points)
    {
        List<List<TrackPoint>> result = [];
        for (var i = 0; i < points.Count; i++)
        {
            if (i == 0 || !Connected(points[i - 1], points[i])) result.Add([]);
            result[^1].Add(points[i]);
        }
        return result;
    }

    public static WalkStats Calculate(IReadOnlyList<TrackPoint> points)
    {
        if (points.Count == 0) return new(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, 0);
        double distance = 0, recorded = 0, moving = 0;
        for (var i = 1; i < points.Count; i++)
        {
            var a = points[i - 1];
            var b = points[i];
            if (!Connected(a, b)) continue;
            var seconds = (b.Time - a.Time).TotalSeconds;
            var meters = DistanceMeters(a.Lat, a.Lon, b.Lat, b.Lon);
            distance += meters;
            recorded += seconds;
            if (meters / seconds >= 0.5) moving += seconds;
        }
        return new(distance / 1000, points[^1].Time - points[0].Time, TimeSpan.FromSeconds(recorded),
            TimeSpan.FromSeconds(moving), points.Count, Segments(points).Count);
    }

    public static string FormatDuration(TimeSpan value) => $"{(int)value.TotalHours}時間{value.Minutes:00}分";

    public static string Summary(WalkSession session)
    {
        var s = session.Stats;
        var first = session.Points[0];
        var last = session.Points[^1];
        string Position(TrackPoint p) => FormattableString.Invariant($"{p.Lat:F5}, {p.Lon:F5}");
        return $"""
            記録: {session.DisplayTitle}
            開始日時: {first.Time:O}
            終了日時: {last.Time:O}
            開始座標: {Position(first)}
            終了座標: {Position(last)}
            地名（逆ジオコーディングの参考情報）: {string.Join(" → ", session.Places.Select(p => p.Name))}
            実測区間距離: {s.DistanceKm.ToString("F2", CultureInfo.InvariantCulture)} km
            経過時間（休止・欠測を含む）: {FormatDuration(s.Elapsed)}
            記録区間時間: {FormatDuration(s.Recorded)}
            推定移動時間: {FormatDuration(s.Moving)}
            区間数: {s.SegmentCount}（区間間の欠測距離は加算しない）
            一周完了: {(session.CompletedLoop ? "ユーザー確認済み" : "未確認")}
            写真: {session.Photos.Count}枚
            観察メモ:
            {session.Notes}
            写真メモ:
            {string.Join(Environment.NewLine, session.Photos.Select(p => $"{p.Time:O} / {p.Image} / {p.Note}"))}
            """;
    }
}
