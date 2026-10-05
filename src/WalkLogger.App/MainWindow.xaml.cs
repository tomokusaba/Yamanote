using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using WalkLogger.Application;
using WalkLogger.Presentation;
using WalkLogger.Core;

namespace WalkLogger.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel model;
    private readonly RouteMapPresenter map;
    private readonly WorkspaceSmokeRunner smokeRunner;
    private readonly WorkspaceLaunchOptions options;
    private bool allowClose;

    public MainWindow(MainWindowViewModel model, WpfWorkspaceDialogs dialogs,
        IFileExporter exporter, WorkspaceLaunchOptions options)
    {
        this.model = model;
        this.options = options;
        InitializeComponent();
        dialogs.Attach(this);
        DataContext = model;
        map = new(MapView, options.BaseDirectory);
        smokeRunner = new(exporter, options);
        map.Ready += model.RefreshMap;
        map.Error += text => model.Status = text;
        model.MapChanged += map.Render;
        model.RecordsChanged += (_, _) => WalkList.Items.Refresh();
        model.SettingsApplied += EnsureMapAsync;
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.ApiKey)) ApiKeyBox.Password = model.ApiKey;
        };
        if (options.SmallWindow) { Width = 1080; Height = 720; }
    }

    public MainWindow(IBlogService blogService, IPlaceService placeService, ILogger<MainWindow> logger,
        IArchiveStoreFactory archives, IFileExporter exporter, IWorkspaceFiles files, ISettingsStore settingsStore,
        IBleTransferFactory ble, IPhotoMetadataReader photoMetadata, ArchiveImportUseCase archiveImports)
        : this(LegacyMainWindowFactory.Create(blogService, placeService, logger, archives, exporter, files,
            settingsStore, ble, photoMetadata, archiveImports)) { }

    private MainWindow(MainWindowServices services)
        : this(services.Model, services.Dialogs, services.Exporter, services.Options) { }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await model.InitializeAsync();
        if (model.Initialized) await EnsureMapAsync();
        if (!options.Smoke) return;
        var success = await smokeRunner.RunAsync(model, map, this);
        allowClose = true;
        map.Dispose();
        Environment.ExitCode = success ? 0 : 1;
        Close();
    }

    private async Task EnsureMapAsync()
    {
        if (map.Initialized) return;
        try { await map.InitializeAsync(model.WebViewDataFolder); }
        catch (Exception ex)
        {
            await model.ReportErrorAsync(ex, "地図の初期化に失敗しました。Microsoft Edge WebView2 Runtimeをインストールしてください。");
        }
    }

    private async void WalkList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await model.SelectSessionAsync(WalkList.SelectedItem as WalkSession);

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) => model.ApiKey = ApiKeyBox.Password;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (model.Busy)
        {
            model.Cancel();
            model.Status = "処理をキャンセルしています。完了後にもう一度閉じてください。";
            return;
        }
        try
        {
            await model.SaveCurrentAsync();
            allowClose = true;
            map.Dispose();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex) { await model.ReportErrorAsync(ex, "記録を保存できないため終了していません。"); }
    }
}
