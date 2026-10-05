using System.Text;
using System.Text.Json;
using WalkLogger.Application;

namespace WalkLogger.Core;

public sealed class ArchiveStore(string root) : IArchiveStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public string SessionFolder(WalkSession session)
    {
        if (!Guid.TryParseExact(session.Id, "N", out _)) throw new InvalidDataException("記録IDが不正です。");
        return Path.Combine(Root, "walks", session.Id);
    }

    public async Task<List<WalkSession>> LoadAsync(CancellationToken ct = default)
    {
        var folder = Path.Combine(Root, "walks");
        if (!Directory.Exists(folder)) return [];
        List<WalkSession> walks = [];
        foreach (var file in Directory.EnumerateFiles(folder, "walk.json", SearchOption.AllDirectories))
        {
            var walk = JsonSerializer.Deserialize<WalkSession>(await File.ReadAllTextAsync(file, ct), Json.Options) ??
                       throw new InvalidDataException($"記録が不正です: {file}");
            if (walk.Points.Count == 0) throw new InvalidDataException($"GPS点がありません: {file}");
            _ = SessionFolder(walk);
            foreach (var p in walk.Points) p.Validate();
            for (var i = 1; i < walk.Points.Count; i++)
                if (walk.Points[i].Time <= walk.Points[i - 1].Time)
                    throw new InvalidDataException($"時刻順が不正です: {file}");
            foreach (var photo in walk.Photos)
                if (Path.GetFileName(photo.Image) != photo.Image || !File.Exists(Path.Combine(SessionFolder(walk), photo.Image)))
                    throw new InvalidDataException($"写真が不正または不足しています: {photo.Image}");
            walks.Add(walk);
        }
        return walks.OrderByDescending(w => w.Points[0].Time).ToList();
    }

    public async Task<WalkSession> ImportAsync(string path, CancellationToken ct = default)
    {
        var walk = await TrackImporter.ReadAsync(path, ct);
        var existing = (await LoadAsync(ct)).FirstOrDefault(w => w.SourceHash == walk.SourceHash);
        if (existing is not null) return existing;
        var folder = SessionFolder(walk);
        Directory.CreateDirectory(folder);
        foreach (var p in walk.Photos)
            File.Copy(Path.Combine(Path.GetDirectoryName(path)!, p.Image), Path.Combine(folder, p.Image), false);
        await SaveAsync(walk, ct);
        return walk;
    }

    public async Task SaveAsync(WalkSession walk, CancellationToken ct = default)
    {
        var folder = SessionFolder(walk);
        Directory.CreateDirectory(folder);
        await AtomicWriteAsync(Path.Combine(folder, "route.gpx"), TrackImporter.ToGpx(walk).ToString(), ct);
        await AtomicWriteAsync(Path.Combine(folder, "summary.txt"), WalkAnalysis.Summary(walk), ct);
        await AtomicWriteAsync(Path.Combine(folder, "blog.md"), walk.Blog, ct);
        await AtomicWriteAsync(Path.Combine(folder, "walk.json"), JsonSerializer.Serialize(walk, Json.Options), ct);
    }

    public static async Task AtomicWriteAsync(string path, string content, CancellationToken ct = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), ct);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string AnnualReport(IEnumerable<WalkSession> sessions, int year) =>
        WalkReports.AnnualReport(sessions, year);
}
