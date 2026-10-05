using WalkLogger.Core;

namespace WalkLogger.Application;

public interface IArchiveStore
{
    string Root { get; }
    string SessionFolder(WalkSession session);
    Task<List<WalkSession>> LoadAsync(CancellationToken ct = default);
    Task<WalkSession> ImportAsync(string path, CancellationToken ct = default);
    Task SaveAsync(WalkSession walk, CancellationToken ct = default);
}

public interface IArchiveStoreFactory
{
    IArchiveStore Create(string root);
}

public interface IBlogService
{
    Uri ValidateEndpoint(string endpoint);
    Task<string> GenerateAsync(string endpoint, string deployment, string apiKey, WalkSession walk,
        WalkSession? previous, string photoFolder, bool includePhotos, CancellationToken ct);
}

public interface IPlaceService
{
    Task<List<PlaceLabel>> ResolveAsync(WalkSession walk, string contact, CancellationToken ct);
}

public interface IFileExporter
{
    string ToGpx(WalkSession walk);
    Task WriteTextAsync(string path, string text, CancellationToken ct = default);
    Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct = default);
}

public interface IWorkspaceFiles
{
    string[] FindImportFiles(string folder);
    bool DirectoryExists(string folder);
    void CreateDirectory(string folder);
    string CopyPhoto(string source, string destinationFolder);
    Task AppendDiagnosticsAsync(string localRoot, string text);
}

public interface IPhotoMetadataReader
{
    DateTime? ReadCaptureTime(string path);
}

public interface ISettingsStore
{
    string LocalRoot { get; }
    Task<SettingsData> LoadAsync();
    Task SaveAsync(SettingsData settings, string key);
    string LoadKey();
}

public class SettingsData
{
    public string ArchiveRoot { get; set; } = "";
    public string AzureEndpoint { get; set; } = "";
    public string AzureDeployment { get; set; } = "";
    public string NominatimContact { get; set; } = "";
}

public sealed record DeviceInfo(ulong Address, string Name)
{
    public override string ToString() => $"{Name}  ({Address:X12})";
}

public interface IBleTransferSession : IDisposable
{
    Task ConnectAsync(ulong address, CancellationToken ct);
    Task<List<RemoteFile>> CatalogAsync(CancellationToken ct);
    Task DownloadAsync(RemoteFile file, string root, IProgress<string> progress, CancellationToken ct);
}

public interface IBleTransferFactory
{
    Task<List<DeviceInfo>> ScanAsync(CancellationToken ct);
    IBleTransferSession Create();
}
