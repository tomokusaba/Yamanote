using Microsoft.Extensions.Logging;
using WalkLogger.Application;
using WalkLogger.Infrastructure;
using WalkLogger.Recording;
using WalkLogger.Recording.Infrastructure;

namespace WalkLogger.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IWorkspaceFiles, WorkspaceFiles>();
        builder.Services.AddSingleton<PhotoAttachmentUseCase>();
        builder.Services.AddSingleton<IRecordingStore>(_ => new FileRecordingStore(
            Path.Combine(FileSystem.AppDataDirectory, "Recordings"),
            Path.Combine(FileSystem.CacheDirectory, "Exports")));
        builder.Services.AddSingleton<RecordingEngine>();
        builder.Services.AddSingleton<AndroidGpsControl>();
        builder.Services.AddSingleton<AndroidPhotoCapture>();
        builder.Services.AddSingleton<RecorderViewModel>();
        builder.Services.AddSingleton<MainPage>();
        builder.Services.AddSingleton<HistoryPage>();
        builder.Services.AddSingleton<SettingsPage>();
        builder.Services.AddSingleton<AppShell>();
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
