using Microsoft.Extensions.Logging;
using WalkLogger.Application;
using WalkLogger.Presentation;

namespace WalkLogger.App;

internal sealed record MainWindowServices(MainWindowViewModel Model, WpfWorkspaceDialogs Dialogs,
    IFileExporter Exporter, WorkspaceLaunchOptions Options);

internal static class LegacyMainWindowFactory
{
    internal static WorkspaceLaunchOptions LaunchOptions()
    {
        var args = Environment.GetCommandLineArgs();
        return new(AppContext.BaseDirectory, args.Contains("--smoke"), args.Contains("--smoke-small"),
            args.FirstOrDefault(a => a.StartsWith("--smoke-output=", StringComparison.Ordinal))?["--smoke-output=".Length..]);
    }

    // Preserve the existing public constructor for callers outside the composition root.
    internal static MainWindowServices Create(IBlogService blogs, IPlaceService places, ILogger<MainWindow> logger,
        IArchiveStoreFactory archives, IFileExporter exporter, IWorkspaceFiles files, ISettingsStore settings,
        IBleTransferFactory ble, IPhotoMetadataReader metadata, ArchiveImportUseCase imports)
    {
        var options = LaunchOptions();
        var dialogs = new WpfWorkspaceDialogs();
        var diagnostics = new WpfWorkspaceDiagnostics(logger, files, settings, dialogs, options);
        var model = new MainWindowViewModel(archives, exporter, files, settings, metadata, imports,
            new SessionEditingUseCase(), new BleTransferUseCase(ble, settings, imports),
            new PhotoAttachmentUseCase(files), new WalkEnrichmentUseCase(blogs, places, settings),
            new SettingsChangeUseCase(blogs, archives, files, settings), new DemoImportUseCase(),
            dialogs, diagnostics, options);
        return new(model, dialogs, exporter, options);
    }
}
