using WalkLogger.Application;
using WalkLogger.Core;

namespace WalkLogger.Infrastructure;

public sealed class ArchiveStoreFactory : IArchiveStoreFactory
{
    public IArchiveStore Create(string root) => new ArchiveStore(root);
}

public sealed class FileExporter : IFileExporter
{
    public string ToGpx(WalkSession walk) => TrackImporter.ToGpx(walk).ToString();
    public Task WriteTextAsync(string path, string text, CancellationToken ct = default) =>
        ArchiveStore.AtomicWriteAsync(path, text, ct);
    public Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct = default) =>
        File.WriteAllBytesAsync(path, bytes, ct);
}

public sealed class WorkspaceFiles : IWorkspaceFiles
{
    public string[] FindImportFiles(string folder)
    {
        var files = Directory.GetFiles(folder, "track.ndjson", SearchOption.AllDirectories);
        return files.Length > 0 ? files : Directory.GetFiles(folder, "*.gpx", SearchOption.AllDirectories);
    }

    public bool DirectoryExists(string folder) => Directory.Exists(folder);
    public void CreateDirectory(string folder) => Directory.CreateDirectory(folder);

    public string CopyPhoto(string source, string destinationFolder)
    {
        var filename = "IMG_" + Guid.NewGuid().ToString("N") + ".jpg";
        File.Copy(source, Path.Combine(destinationFolder, filename));
        return filename;
    }

    public Task AppendDiagnosticsAsync(string localRoot, string text) =>
        File.AppendAllTextAsync(Path.Combine(localRoot, "diagnostics.log"), text);
}
