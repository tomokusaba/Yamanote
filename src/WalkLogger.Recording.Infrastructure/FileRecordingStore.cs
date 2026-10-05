using System.IO.Compression;
using System.Text;
using System.Text.Json;
using WalkLogger.Core;
using WalkLogger.Recording;

namespace WalkLogger.Recording.Infrastructure;

public sealed class FileRecordingStore(string root, string exportRoot) : IRecordingStore
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string Folder(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("記録IDが不正です。");
        return Path.Combine(root, id);
    }

    public async Task<RecordedWalk?> LoadActiveAsync()
    {
        var sessions = await ListAsync();
        var active = sessions.Where(s => s.Info.Phase != RecordingPhase.Finished).ToArray();
        if (active.Length > 1) throw new InvalidDataException("未終了の記録が複数あります。元データを保管して修復してください。");
        return active.SingleOrDefault();
    }

    public async Task<IReadOnlyList<RecordedWalk>> ListAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (!Directory.Exists(root)) return [];
            List<RecordedWalk> result = [];
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var path = Path.Combine(directory, "recording.json");
                if (File.Exists(path)) result.Add(await ReadAsync(Path.GetFileName(directory)));
            }
            return result.OrderByDescending(s => s.Info.StartedAt).ToArray();
        }
        finally { gate.Release(); }
    }

    private async Task<RecordedWalk> ReadAsync(string id)
    {
        var folder = Folder(id);
        var info = JsonSerializer.Deserialize<RecordingInfo>(
            await File.ReadAllTextAsync(Path.Combine(folder, "recording.json")), Json.Options) ??
            throw new InvalidDataException("記録情報がnullです。");
        if (info.Id != id || info.StartedAt == default || info.IntervalSeconds is not (5 or 10) ||
            !Enum.IsDefined(info.Phase))
            throw new InvalidDataException("記録情報が不正です。");
        var trackPath = Path.Combine(folder, "track.ndjson");
        if (!File.Exists(trackPath))
        {
            if (File.Exists(Path.Combine(folder, "photos.ndjson")))
                throw new InvalidDataException("写真記録がありますがGPSログが不足しています。");
            return new(info, [], []);
        }
        var points = await TrackImporter.ReadJsonLinesAsync<TrackPoint>(trackPath, default);
        for (var i = 0; i < points.Count; i++)
        {
            points[i].Validate();
            if (i > 0 && points[i].Time <= points[i - 1].Time)
                throw new InvalidDataException("GPS時刻が重複または逆行しています。");
        }
        var photoPath = Path.Combine(folder, "photos.ndjson");
        var photos = File.Exists(photoPath)
            ? await TrackImporter.ReadJsonLinesAsync<PhotoRecord>(photoPath, default) : [];
        foreach (var photo in photos)
        {
            new TrackPoint(photo.Time, photo.Lat, photo.Lon).Validate();
            ValidateImage(photo.Image);
            if (!File.Exists(Path.Combine(folder, photo.Image)))
                throw new InvalidDataException($"写真が不足しています: {photo.Image}");
        }
        return new(info, points, photos);
    }

    public async Task SaveInfoAsync(RecordingInfo info)
    {
        await gate.WaitAsync();
        try
        {
            var folder = Folder(info.Id);
            Directory.CreateDirectory(folder);
            var destination = Path.Combine(folder, "recording.json");
            var temporary = destination + ".tmp";
            try
            {
                await using (var stream = File.Create(temporary))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(info, Json.Options)));
                    stream.Flush(true);
                }
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    public async Task AppendPointAsync(string id, TrackPoint point)
    {
        await gate.WaitAsync();
        try { await AppendAsync(Path.Combine(Folder(id), "track.ndjson"), point); }
        finally { gate.Release(); }
    }

    public async Task AppendPhotoAsync(string id, PhotoRecord photo, Stream jpeg)
    {
        ValidateImage(photo.Image);
        await gate.WaitAsync();
        try
        {
            var destination = Path.Combine(Folder(id), photo.Image);
            var temporary = destination + ".tmp";
            try
            {
                await using (var target = File.Create(temporary))
                {
                    var signature = new byte[2];
                    await jpeg.ReadExactlyAsync(signature);
                    if (signature[0] != 0xff || signature[1] != 0xd8)
                        throw new InvalidDataException("JPEG形式の写真が必要です。");
                    await target.WriteAsync(signature);
                    await jpeg.CopyToAsync(target);
                    target.Flush(true);
                }
                File.Move(temporary, destination);
                await AppendAsync(Path.Combine(Folder(id), "photos.ndjson"), photo);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    private static void ValidateImage(string image)
    {
        if (Path.GetFileName(image) != image || image.Contains('\\') || image.Contains('/') ||
            !image.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("写真名はJPEGのファイル名だけを指定してください。");
    }

    private static async Task AppendAsync<T>(string path, T value)
    {
        var text = JsonSerializer.Serialize(value, Json.Options).Replace("\r", "").Replace("\n", "");
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text + "\n"));
        stream.Flush(true);
    }

    public async Task<string> ExportAsync(string id, bool includePhotos)
    {
        await gate.WaitAsync();
        try
        {
            var record = await ReadAsync(id);
            if (record.Info.Phase == RecordingPhase.Recording)
                throw new InvalidOperationException("一時停止または終了してから共有してください。");
            if (record.Points.Count == 0) throw new InvalidOperationException("GPS未取得の記録は共有できません。");
            Directory.CreateDirectory(exportRoot);
            var basename = $"WalkLogger_{record.Info.StartedAt:yyyyMMdd_HHmmss}_{id[..8]}";
            var path = Path.Combine(exportRoot, basename + (includePhotos ? ".zip" : ".gpx"));
            var temporary = path + ".tmp";
            try
            {
                var walk = new WalkSession { Points = record.Points.ToList(), Photos = record.Photos.ToList() };
                if (!includePhotos) TrackImporter.ToGpx(walk).Save(temporary);
                else
                {
                    using var zip = ZipFile.Open(temporary, ZipArchiveMode.Create);
                    zip.CreateEntryFromFile(Path.Combine(Folder(id), "track.ndjson"), "track.ndjson");
                    zip.CreateEntryFromFile(Path.Combine(Folder(id), "recording.json"), "recording.json");
                    if (record.Photos.Count > 0)
                    {
                        zip.CreateEntryFromFile(Path.Combine(Folder(id), "photos.ndjson"), "photos.ndjson");
                        foreach (var photo in record.Photos)
                            zip.CreateEntryFromFile(Path.Combine(Folder(id), photo.Image), photo.Image, CompressionLevel.NoCompression);
                    }
                    await using var output = zip.CreateEntry("track.gpx").Open();
                    await TrackImporter.ToGpx(walk).SaveAsync(output, System.Xml.Linq.SaveOptions.None, default);
                }
                File.Move(temporary, path, true);
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }
}
