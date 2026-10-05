using WalkLogger.Core;

namespace WalkLogger.Application;

public sealed class ArchiveImportUseCase(IWorkspaceFiles files)
{
    public async Task<string?> ImportDirectoryAsync(IArchiveStore archive, string folder,
        IProgress<string> progress, CancellationToken ct)
    {
        var paths = files.FindImportFiles(folder);
        if (paths.Length == 0)
            throw new InvalidDataException("track.ndjsonまたはGPXがありません。記録フォルダーを選択してください。");
        string? selected = null;
        foreach (var path in paths.OrderBy(p => p))
        {
            ct.ThrowIfCancellationRequested();
            progress.Report("取り込み: " + Path.GetFileName(Path.GetDirectoryName(path)));
            var walk = await archive.ImportAsync(path, ct);
            selected = walk.Id;
        }
        return selected;
    }
}
