using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WalkLogger.Application;

namespace WalkLogger.Core;

public static class TrackImporter
{
    public static async Task<WalkSession> ReadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("ログファイルが見つかりません。", path);
        var points = Path.GetExtension(path).Equals(".gpx", StringComparison.OrdinalIgnoreCase)
            ? ReadGpx(path) : await ReadJsonLinesAsync<TrackPoint>(path, ct);
        if (points.Count == 0) throw new InvalidDataException("有効なGPS点がありません。");
        for (var i = 0; i < points.Count; i++)
        {
            points[i].Validate();
            if (i > 0 && points[i].Time <= points[i - 1].Time)
                throw new InvalidDataException($"GPS点 {i + 1}: 時刻が重複、または逆行しています。");
        }
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var photoPath = Path.Combine(folder, "photos.ndjson");
        var photos = File.Exists(photoPath) ? await ReadJsonLinesAsync<PhotoRecord>(photoPath, ct) : [];
        foreach (var photo in photos)
        {
            new TrackPoint(photo.Time, photo.Lat, photo.Lon).Validate();
            if (Path.GetFileName(photo.Image) != photo.Image || string.IsNullOrWhiteSpace(photo.Image) ||
                !Path.GetExtension(photo.Image).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("写真名はフォルダーを含まないJPEGファイル名である必要があります。");
            if (!File.Exists(Path.Combine(folder, photo.Image)))
                throw new InvalidDataException($"写真が不足しています: {photo.Image}");
        }
        var canonical = JsonSerializer.Serialize(new { points, photos }, Json.Options);
        return new WalkSession
        {
            Title = points[0].Time.ToLocalTime().ToString("yyyy年M月d日") + "の街歩き",
            SourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))),
            Points = points,
            Photos = photos
        };
    }

    public static async Task<List<T>> ReadJsonLinesAsync<T>(string path, CancellationToken ct)
    {
        List<T> result = [];
        var lineNumber = 0;
        await foreach (var line in File.ReadLinesAsync(path, Encoding.UTF8, ct))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                result.Add(JsonSerializer.Deserialize<T>(line, Json.Options) ??
                           throw new JsonException("nullレコード"));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} の {lineNumber}行目が不正です。途中書き込みの行がある場合は元ファイルを保管して修復してください。", ex);
            }
        }
        return result;
    }

    private static List<TrackPoint> ReadGpx(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        var doc = XDocument.Load(reader);
        if (doc.Root?.Name.LocalName != "gpx") throw new InvalidDataException("GPXではありません。");
        var ns = doc.Root.Name.Namespace;
        List<TrackPoint> result = [];
        var segment = 0;
        foreach (var trkseg in doc.Descendants(ns + "trkseg"))
        {
            foreach (var p in trkseg.Elements(ns + "trkpt"))
            {
                var time = p.Element(ns + "time")?.Value ??
                           throw new InvalidDataException("GPX点にtimeがありません。");
                if (!HasTimezone(time)) throw new InvalidDataException("GPXの時刻にはZまたはUTCオフセットが必要です。");
                if (!DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ||
                    !double.TryParse(p.Attribute("lat")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(p.Attribute("lon")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                    throw new InvalidDataException("GPXの日時または座標が不正です。");
                double? altitude = null;
                if (p.Element(ns + "ele") is { } ele)
                {
                    if (!double.TryParse(ele.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        throw new InvalidDataException("GPX標高が不正です。");
                    altitude = value;
                }
                result.Add(new(timestamp, lat, lon, Altitude: altitude, Segment: segment));
            }
            segment++;
        }
        return result;
    }

    public static bool HasTimezone(string time) => TimestampText.HasTimezone(time);

    public static XDocument ToGpx(WalkSession session)
    {
        XNamespace ns = "http://www.topografix.com/GPX/1/1";
        return new(new XDeclaration("1.0", "utf-8", null),
            new XElement(ns + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "WalkLogger"),
                new XElement(ns + "metadata", new XElement(ns + "name", session.Title)),
                new XElement(ns + "trk", new XElement(ns + "name", session.Title),
                    WalkAnalysis.Segments(session.Points).Select(segment =>
                        new XElement(ns + "trkseg", segment.Select(p =>
                            new XElement(ns + "trkpt",
                                new XAttribute("lat", p.Lat.ToString("R", CultureInfo.InvariantCulture)),
                                new XAttribute("lon", p.Lon.ToString("R", CultureInfo.InvariantCulture)),
                                p.Altitude.HasValue ? new XElement(ns + "ele", p.Altitude.Value.ToString("R", CultureInfo.InvariantCulture)) : null,
                                new XElement(ns + "time", p.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")))))))));
    }
}
