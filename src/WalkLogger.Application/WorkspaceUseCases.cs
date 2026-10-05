using WalkLogger.Core;

namespace WalkLogger.Application;

public sealed record PhotoNoteEdit(PhotoRecord Photo, string Note);
public sealed record SessionEdits(string Title, string Notes, string Blog, bool CompletedLoop,
    IReadOnlyList<PhotoNoteEdit> Photos);

public sealed class SessionEditingUseCase
{
    public async Task SaveAsync(IArchiveStore store, WalkSession walk, SessionEdits edits, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(edits.Title))
            throw new ArgumentException("記録のタイトルを入力してください。");
        walk.Title = edits.Title.Trim();
        walk.Notes = edits.Notes;
        walk.Blog = edits.Blog;
        walk.CompletedLoop = edits.CompletedLoop;
        walk.Photos = edits.Photos.Select(p => p.Photo with { Note = p.Note }).ToList();
        await store.SaveAsync(walk, ct);
    }
}

public sealed record BleImportResult(string? SelectedId, int FileCount);

public sealed class BleTransferUseCase(IBleTransferFactory ble, ISettingsStore settings,
    ArchiveImportUseCase imports)
{
    public Task<List<DeviceInfo>> ScanAsync(CancellationToken ct) => ble.ScanAsync(ct);

    public async Task<BleImportResult> TransferAsync(IArchiveStore store, DeviceInfo device,
        IProgress<string> progress, CancellationToken ct)
    {
        using var client = ble.Create();
        await client.ConnectAsync(device.Address, ct);
        var catalog = await client.CatalogAsync(ct);
        if (catalog.Count == 0) throw new InvalidDataException("CoreS3に記録がありません。");
        var root = Path.Combine(settings.LocalRoot, "Imports", device.Address.ToString("X12"));
        foreach (var file in catalog) await client.DownloadAsync(file, root, progress, ct);
        var selected = await imports.ImportDirectoryAsync(store, root, progress, ct);
        return new(selected, catalog.Count);
    }
}

public sealed class PhotoAttachmentUseCase(IWorkspaceFiles files)
{
    public TrackPoint? MatchPoint(WalkSession walk, DateTimeOffset time)
    {
        var point = walk.Points.MinBy(p => Math.Abs((p.Time - time).TotalSeconds));
        return point is not null && Math.Abs((point.Time - time).TotalSeconds) <= 60 ? point : null;
    }

    public async Task AttachAsync(IArchiveStore store, WalkSession walk, string path,
        DateTimeOffset time, CancellationToken ct)
    {
        var point = MatchPoint(walk, time) ??
            throw new ArgumentException("撮影時刻の60秒以内にGPS点がありません。撮影時刻や時差を確認してください。");
        var filename = files.CopyPhoto(path, store.SessionFolder(walk));
        walk.Photos.Add(new(time, point.Lat, point.Lon, filename));
        await store.SaveAsync(walk, ct);
    }
}

public sealed class WalkEnrichmentUseCase(IBlogService blogs, IPlaceService places, ISettingsStore settings)
{
    public async Task ResolvePlacesAsync(IArchiveStore store, WalkSession walk, string contact, CancellationToken ct)
    {
        walk.Places = await places.ResolveAsync(walk, contact, ct);
        await store.SaveAsync(walk, ct);
    }

    public async Task GenerateBlogAsync(IArchiveStore store, WalkSession walk, WalkSession? previous,
        SettingsData configuration, bool includePhotos, IProgress<string> draft, CancellationToken ct)
    {
        var text = await blogs.GenerateAsync(configuration.AzureEndpoint, configuration.AzureDeployment,
            settings.LoadKey(), walk, previous, store.SessionFolder(walk), includePhotos, ct);
        walk.Blog = text;
        draft.Report(text);
        await store.SaveAsync(walk, ct);
    }
}

public sealed record SettingsChangeResult(SettingsData Settings, IArchiveStore Store);

public sealed class SettingsChangeUseCase(IBlogService blogs, IArchiveStoreFactory archives,
    IWorkspaceFiles files, ISettingsStore settings)
{
    public SettingsData Prepare(string root, string endpoint, string deployment, string contact)
    {
        root = root.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("保存先には絶対パスを指定してください。");
        if (!string.IsNullOrWhiteSpace(endpoint)) _ = blogs.ValidateEndpoint(endpoint);
        return new()
        {
            ArchiveRoot = root,
            AzureEndpoint = endpoint.Trim(),
            AzureDeployment = deployment.Trim(),
            NominatimContact = contact.Trim()
        };
    }

    public async Task<SettingsChangeResult> ChangeAsync(SettingsData next, string key, CancellationToken ct)
    {
        next.ArchiveRoot = Path.GetFullPath(next.ArchiveRoot);
        var nextStore = archives.Create(next.ArchiveRoot);
        files.CreateDirectory(nextStore.Root);
        _ = await nextStore.LoadAsync(ct);
        await settings.SaveAsync(next, key);
        return new(next, nextStore);
    }
}

public sealed class DemoImportUseCase
{
    public async Task<WalkSession> ImportAsync(IArchiveStore store, string path, CancellationToken ct)
    {
        var walk = await store.ImportAsync(path, ct);
        walk.IsDemo = true;
        walk.Title = "上野から東京へ（合成ルート）";
        if (string.IsNullOrWhiteSpace(walk.Notes))
            walk.Notes = "画面確認用の合成データです。実際の歩行・街の観察ではありません。";
        await store.SaveAsync(walk, ct);
        return walk;
    }
}
