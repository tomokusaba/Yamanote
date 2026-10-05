using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WalkLogger.Application;
using WalkLogger.Core;
using WalkLogger.Infrastructure;
using WalkLogger.Infrastructure.Windows;
using WalkLogger.Presentation;

namespace WalkLogger.App;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var app = new App();
        app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            try
            {
                await app.StartHostAsync(CreateHost());
            }
            catch (Exception ex)
            {
                await app.HandleStartupFailureAsync(ex);
            }
        };
        return app.Run();
    }

    private static IHost CreateHost()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Services.AddSingleton<IHostLifetime, WpfHostLifetime>();
        builder.Services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(3) });
        builder.Services.AddSingleton<IBlogService, AzureBlogService>();
        builder.Services.AddSingleton<IPlaceService>(sp => new PlaceService(sp.GetRequiredService<HttpClient>(),
            Path.Combine(SettingsStore.LocalRoot, "places-cache.json")));
        builder.Services.AddSingleton<IArchiveStoreFactory, ArchiveStoreFactory>();
        builder.Services.AddSingleton<IFileExporter, FileExporter>();
        builder.Services.AddSingleton<IWorkspaceFiles, WorkspaceFiles>();
        builder.Services.AddSingleton<ISettingsStore, WindowsSettingsStore>();
        builder.Services.AddSingleton<IBleTransferFactory, WindowsBleTransferFactory>();
        builder.Services.AddSingleton<IPhotoMetadataReader, PhotoMetadataReader>();
        builder.Services.AddSingleton<ArchiveImportUseCase>();
        builder.Services.AddSingleton<SessionEditingUseCase>();
        builder.Services.AddSingleton<BleTransferUseCase>();
        builder.Services.AddSingleton<PhotoAttachmentUseCase>();
        builder.Services.AddSingleton<WalkEnrichmentUseCase>();
        builder.Services.AddSingleton<SettingsChangeUseCase>();
        builder.Services.AddSingleton<DemoImportUseCase>();
        builder.Services.AddSingleton(LegacyMainWindowFactory.LaunchOptions());
        builder.Services.AddSingleton<WpfWorkspaceDialogs>();
        builder.Services.AddSingleton<IWorkspaceDialogs>(sp => sp.GetRequiredService<WpfWorkspaceDialogs>());
        builder.Services.AddSingleton<IWorkspaceDiagnostics, WpfWorkspaceDiagnostics>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton(sp => new MainWindow(sp.GetRequiredService<MainWindowViewModel>(),
            sp.GetRequiredService<WpfWorkspaceDialogs>(), sp.GetRequiredService<IFileExporter>(),
            sp.GetRequiredService<WorkspaceLaunchOptions>()));
        if (Environment.GetCommandLineArgs().Contains("--smoke"))
            builder.Services.AddHostedService<HostSmokeProbe>();
        return builder.Build();
    }
}
