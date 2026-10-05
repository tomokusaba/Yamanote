using System.IO;
using Microsoft.Extensions.Logging;
using WalkLogger.Application;
using WalkLogger.Presentation;

namespace WalkLogger.App;

internal sealed class WpfWorkspaceDiagnostics(ILogger<MainWindow> logger, IWorkspaceFiles files,
    ISettingsStore settings, WpfWorkspaceDialogs dialogs, WorkspaceLaunchOptions options) : IWorkspaceDiagnostics
{
    public async Task<string> ReportAsync(Exception exception, string? context = null, IProgress<string>? progress = null)
    {
        logger.LogError(exception, "{Context}", context ?? "処理に失敗しました。");
        var text = (context is null ? "" : context + "\n") + exception.Message;
        var status = text;
        progress?.Report(status);
        files.CreateDirectory(settings.LocalRoot);
        try
        {
            await files.AppendDiagnosticsAsync(settings.LocalRoot, $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (IOException logError)
        {
            status += " / 診断ログの保存にも失敗しました: " + logError.Message;
            progress?.Report(status);
        }
        if (!options.Smoke) dialogs.ShowError(text);
        return status;
    }
}
