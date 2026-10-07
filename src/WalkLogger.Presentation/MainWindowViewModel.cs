using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WalkLogger.Application;
using WalkLogger.Core;

namespace WalkLogger.Presentation;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly IArchiveStoreFactory archives;
    private readonly IFileExporter exporter;
    private readonly IWorkspaceFiles files;
    private readonly ISettingsStore settingsStore;
    private readonly IPhotoMetadataReader photoMetadata;
    private readonly ArchiveImportUseCase imports;
    private readonly SessionEditingUseCase edits;
    private readonly BleTransferUseCase transfers;
    private readonly PhotoAttachmentUseCase photos;
    private readonly WalkEnrichmentUseCase enrichment;
    private readonly SettingsChangeUseCase settingsChanges;
    private readonly DemoImportUseCase demos;
    private readonly IWorkspaceDialogs dialogs;
    private readonly IWorkspaceDiagnostics diagnostics;
    private readonly WorkspaceLaunchOptions options;
    private IArchiveStore store;
    private SettingsData settings = new();
    private CancellationTokenSource? operation;
    private bool loadingSelection;
    private bool archiveLoaded;
    private bool sessionShown;
    private WalkSession? previous;
    private string status = "ログを取り込むか、サンプルで試してください。";
    private bool bleVisible;
    private string title = "", notes = "", blog = "", archiveRoot = "", endpoint = "", deployment = "", contact = "", apiKey = "";
    private string year = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
    private bool completedLoop, sendPhotos;
    private DeviceInfo? selectedDevice;

    public MainWindowViewModel(IArchiveStoreFactory archives, IFileExporter exporter, IWorkspaceFiles files,
        ISettingsStore settingsStore, IPhotoMetadataReader photoMetadata, ArchiveImportUseCase imports,
        SessionEditingUseCase edits, BleTransferUseCase transfers, PhotoAttachmentUseCase photos,
        WalkEnrichmentUseCase enrichment, SettingsChangeUseCase settingsChanges, DemoImportUseCase demos,
        IWorkspaceDialogs dialogs, IWorkspaceDiagnostics diagnostics, WorkspaceLaunchOptions options)
    {
        this.archives = archives;
        this.exporter = exporter;
        this.files = files;
        this.settingsStore = settingsStore;
        this.photoMetadata = photoMetadata;
        this.imports = imports;
        this.edits = edits;
        this.transfers = transfers;
        this.photos = photos;
        this.enrichment = enrichment;
        this.settingsChanges = settingsChanges;
        this.demos = demos;
        this.dialogs = dialogs;
        this.diagnostics = diagnostics;
        this.options = options;
        store = archives.Create(Path.Combine(settingsStore.LocalRoot, "Archive"));
        SaveCommand = Command(SaveAsync);
        ImportFileCommand = Command(ImportFileAsync);
        ImportFolderCommand = Command(ImportFolderAsync);
        ScanCommand = Command(ScanAsync);
        TransferCommand = Command(TransferAsync);
        DemoCommand = Command(LoadDemoAsync);
        PlacesCommand = Command(ResolvePlacesAsync);
        GenerateCommand = Command(GenerateAsync);
        AddPhotoCommand = Command(AddPhotoAsync);
        ReportCommand = Command(() => ReportAsync(false));
        ExportReportCommand = Command(() => ReportAsync(true));
        ExportBlogCommand = Command(ExportBlogAsync);
        ExportGpxCommand = Command(ExportGpxAsync);
        SettingsSaveCommand = Command(SaveSettingsAsync);
        OpenArchiveCommand = new ActionCommand(OpenArchive);
        ChooseArchiveCommand = new ActionCommand(ChooseArchive);
        ShowBleCommand = new ActionCommand(() => BleVisible = true);
        HideBleCommand = new ActionCommand(() => BleVisible = false);
        ClearPreviousCommand = new ActionCommand(() => Previous = null);
        CancelCommand = new ActionCommand(Cancel);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? RecordsChanged;
    public event Action<RouteMapState>? MapChanged;
    public event Func<Task>? SettingsApplied;
    public bool Initialized { get; private set; }
    public bool Busy => operation is not null;
    public bool Idle => !Busy;
    public string? SmokeRoot { get; private set; }
    public string WebViewDataFolder => Path.Combine(SmokeRoot ?? settingsStore.LocalRoot, "WebView2");
    public List<WalkSession> Walks { get; private set; } = [];
    public WalkSession? Current { get; private set; }
    public bool HasSession => Current is not null;
    public bool NoSession => !HasSession;
    public List<PhotoEditorState> PhotoEditors { get; private set; } = [];
    public bool NoPhotos => PhotoEditors.Count == 0;
    public List<WalkSession> PreviousWalks { get; private set; } = [];
    public List<DeviceInfo> Devices { get; private set; } = [];
    public DeviceInfo? SelectedDevice { get => selectedDevice; set => Set(ref selectedDevice, value); }
    public string Title { get => title; set => Set(ref title, value); }
    public string Notes { get => notes; set => Set(ref notes, value); }
    public string Blog { get => blog; set => Set(ref blog, value); }
    public bool CompletedLoop { get => completedLoop; set => Set(ref completedLoop, value); }
    public bool SendPhotos { get => sendPhotos; set => Set(ref sendPhotos, value); }
    public string Year { get => year; set => Set(ref year, value); }
    public string Report { get; private set; } = "";
    public string ArchiveRoot { get => archiveRoot; set => Set(ref archiveRoot, value); }
    public string Endpoint { get => endpoint; set => Set(ref endpoint, value); }
    public string Deployment { get => deployment; set => Set(ref deployment, value); }
    public string Contact { get => contact; set => Set(ref contact, value); }
    public string ApiKey { get => apiKey; set => Set(ref apiKey, value); }
    public string PlacesText => Current is null ? "" : string.Join("\n→ ", Current.Places.Select(p => p.Name));
    public string ArchiveCount => archiveLoaded ?
        $"{Walks.Count(w => !w.IsDemo)}件の記録 / サンプル {Walks.Count(w => w.IsDemo)}件" : "まだ記録がありません";
    public string RouteHeading => Current?.DisplayTitle ?? "最初の街歩きを取り込む";
    public string RouteMetrics
    {
        get
        {
            if (Current is null) return "CoreS3のログ、またはGPXファイルから始められます。";
            var stats = Current.Stats;
            return $"{stats.DistanceKm:F2} km  ·  経過 {WalkAnalysis.FormatDuration(stats.Elapsed)}  ·  写真 {Current.Photos.Count}枚\n" +
                   $"移動推定 {WalkAnalysis.FormatDuration(stats.Moving)} / 実測 {stats.PointCount:N0}点 / {stats.SegmentCount}区間";
        }
    }
    public string ComparisonText
    {
        get
        {
            if (Current is null || Previous is null)
                return sessionShown ?
                    "過去の記録を選ぶと実測値を比較します。地図では過去ルートを灰色の破線で表示します。" :
                    "過去の記録を選ぶと、実測値を並べて比較できます。";
            var now = Current.Stats;
            var old = Previous.Stats;
            return $"今回: {Current.DisplayDate} / {now.DistanceKm:F2} km / 経過 {WalkAnalysis.FormatDuration(now.Elapsed)} / 写真 {Current.Photos.Count}枚\n" +
                   $"過去: {Previous.DisplayDate} / {old.DistanceKm:F2} km / 経過 {WalkAnalysis.FormatDuration(old.Elapsed)} / 写真 {Previous.Photos.Count}枚\n" +
                   $"実測距離の差: {now.DistanceKm - old.DistanceKm:+0.00;-0.00;0.00} km。店舗の変化・混雑は数値から推測しません。";
        }
    }
    public WalkSession? Previous
    {
        get => previous;
        set { previous = value; Notify(); Notify(nameof(ComparisonText)); RefreshMap(); }
    }
    public string Status { get => status; set { status = value; Notify(); } }
    public bool BleVisible { get => bleVisible; set { bleVisible = value; Notify(); } }

    public ICommand SaveCommand { get; }
    public ICommand ImportFileCommand { get; }
    public ICommand ImportFolderCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand TransferCommand { get; }
    public ICommand DemoCommand { get; }
    public ICommand PlacesCommand { get; }
    public ICommand GenerateCommand { get; }
    public ICommand AddPhotoCommand { get; }
    public ICommand ReportCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand ExportBlogCommand { get; }
    public ICommand ExportGpxCommand { get; }
    public ICommand SettingsSaveCommand { get; }
    public ICommand OpenArchiveCommand { get; }
    public ICommand ChooseArchiveCommand { get; }
    public ICommand ShowBleCommand { get; }
    public ICommand HideBleCommand { get; }
    public ICommand ClearPreviousCommand { get; }
    public ICommand CancelCommand { get; }

    private ICommand Command(Func<Task> action) => new AsyncCommand(action, ex => ReportErrorAsync(ex));
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name);
    }
    private IProgress<string> Progress() => new Progress<string>(text => Status = text);

    public async Task InitializeAsync()
    {
        await RunAsync("アーカイブを読み込んでいます…", async ct =>
        {
            if (options.Smoke)
            {
                SmokeRoot = Path.Combine(Path.GetTempPath(), "WalkLogger-smoke-" + Guid.NewGuid().ToString("N"));
                settings = new() { ArchiveRoot = SmokeRoot };
            }
            else settings = await settingsStore.LoadAsync();
            store = archives.Create(settings.ArchiveRoot);
            ArchiveRoot = settings.ArchiveRoot;
            Endpoint = settings.AzureEndpoint;
            Deployment = settings.AzureDeployment;
            Contact = settings.NominatimContact;
            ApiKey = options.Smoke ? "" : settingsStore.LoadKey();
            foreach (var name in new[] { nameof(ArchiveRoot), nameof(Endpoint), nameof(Deployment), nameof(Contact), nameof(ApiKey) })
                Notify(name);
            await RefreshAsync(null, ct);
            Initialized = true;
            Status = $"保存先: {store.Root}";
        });
    }

    public async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (Busy) { Status = "処理中です。完了を待つかキャンセルしてください。"; return; }
        using var source = new CancellationTokenSource();
        operation = source;
        Notify(nameof(Busy)); Notify(nameof(Idle));
        Status = message;
        try { await action(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Status = "キャンセルしました。BLEの受信済み部分とSDの原本は残しています。";
        }
        catch (Exception ex) { await ReportErrorAsync(ex); }
        finally { operation = null; Notify(nameof(Busy)); Notify(nameof(Idle)); }
    }

    public async Task ReportErrorAsync(Exception ex, string? context = null) =>
        Status = await diagnostics.ReportAsync(ex, context, new InlineProgress<string>(text => Status = text));

    public void Cancel() => operation?.Cancel();

    private async Task RefreshAsync(string? selectedId, CancellationToken ct)
    {
        loadingSelection = true;
        try
        {
            Walks = await store.LoadAsync(ct);
            archiveLoaded = true;
            Notify(nameof(Walks));
            ShowSession(Walks.FirstOrDefault(w => w.Id == selectedId) ?? Walks.FirstOrDefault());
            Notify(nameof(ArchiveCount));
        }
        finally { loadingSelection = false; }
    }

    private void ShowSession(WalkSession? walk)
    {
        Current = walk;
        sessionShown |= walk is not null;
        Title = walk?.Title ?? "";
        Notes = walk?.Notes ?? "";
        Blog = walk?.Blog ?? "";
        if (walk is not null) CompletedLoop = walk.CompletedLoop;
        PhotoEditors = walk is null ? [] : walk.Photos.Select(p => new PhotoEditorState
        {
            Photo = p, ImagePath = Path.Combine(store.SessionFolder(walk), p.Image), Note = p.Note
        }).ToList();
        PreviousWalks = walk is null ? [] :
            Walks.Where(w => w.Id != walk.Id && w.Points[0].Time < walk.Points[0].Time).ToList();
        Previous = null;
        foreach (var name in new[] { nameof(Current), nameof(HasSession), nameof(NoSession), nameof(Title), nameof(Notes), nameof(Blog),
                     nameof(CompletedLoop), nameof(PhotoEditors), nameof(NoPhotos), nameof(PreviousWalks),
                     nameof(RouteHeading), nameof(RouteMetrics), nameof(PlacesText) })
            Notify(name);
        RefreshMap();
    }

    public async Task SelectSessionAsync(WalkSession? requested)
    {
        if (loadingSelection || !Initialized || Current == requested) return;
        await RunAsync("記録を切り替えています…", async ct =>
        {
            await SaveCurrentAsync(ct);
            ShowSession(requested);
            Status = requested is null ? "記録を選択してください。" : requested.DisplayTitle;
        });
        if (Current != requested)
        {
            loadingSelection = true;
            try { Notify(nameof(Current)); }
            finally { loadingSelection = false; }
        }
    }

    public async Task SaveCurrentAsync(CancellationToken ct = default)
    {
        if (Current is null) return;
        await edits.SaveAsync(store, Current, new(Title, Notes, Blog, CompletedLoop,
            PhotoEditors.Select(p => new PhotoNoteEdit(p.Photo, p.Note)).ToList()), ct);
        RecordsChanged?.Invoke(this, EventArgs.Empty);
        Notify(nameof(RouteHeading));
        RefreshMap();
    }

    public void RefreshMap()
    {
        if (Current is not null) MapChanged?.Invoke(new(Current, Previous, store.SessionFolder(Current)));
    }

    private Task SaveAsync() => RunAsync("記録を保存しています…", async ct =>
    {
        await SaveCurrentAsync(ct);
        Status = "記録・写真メモ・草稿・GPXを保存しました。";
    });

    public async Task ImportFileAsync()
    {
        var path = dialogs.OpenLogFile();
        if (path is null) return;
        await RunAsync("ログを取り込んでいます…", async ct =>
        {
            await SaveCurrentAsync(ct);
            var walk = await store.ImportAsync(path, ct);
            await RefreshAsync(walk.Id, ct);
            Status = "取り込みました。元のファイルは変更していません。";
        });
    }

    public async Task ImportFolderAsync()
    {
        var path = dialogs.OpenImportFolder();
        if (path is null) return;
        await RunAsync("フォルダーを取り込んでいます…", async ct =>
        {
            await SaveCurrentAsync(ct);
            var selected = await imports.ImportDirectoryAsync(store, path, Progress(), ct);
            await RefreshAsync(selected, ct);
            Status = "フォルダーを取り込みました。重複した記録は追加しません。";
        });
    }

    public Task ScanAsync() => RunAsync("BLE機器を8秒間検索しています…", async ct =>
    {
        Devices = await transfers.ScanAsync(ct);
        Notify(nameof(Devices));
        SelectedDevice = Devices.FirstOrDefault();
        Notify(nameof(SelectedDevice));
        Status = Devices.Count == 0 ? "機器が見つかりません。CoreS3でBLE ONにし、PCのBluetoothを確認してください。" :
            $"{Devices.Count}台見つかりました。転送する機器を選択してください。";
    });

    public async Task TransferAsync()
    {
        if (SelectedDevice is not { } device)
        {
            Status = "機器を検索してから、転送するCoreS3を選択してください。";
            return;
        }
        await RunAsync("CoreS3に接続しています…", async ct =>
        {
            await SaveCurrentAsync(ct);
            var result = await transfers.TransferAsync(store, device, Progress(), ct);
            await RefreshAsync(result.SelectedId, ct);
            Status = $"BLE転送完了: {result.FileCount}ファイルを検証して取り込みました。SD原本は残しています。";
        });
    }

    public Task LoadDemoAsync() => RunAsync("合成サンプルを開いています…", async ct =>
    {
        await SaveCurrentAsync(ct);
        var walk = await demos.ImportAsync(store,
            Path.Combine(options.BaseDirectory, "Samples", "ueno-tokyo", "route.gpx"), ct);
        await RefreshAsync(walk.Id, ct);
        Status = "合成サンプルを表示しています。年次集計には含めません。";
    });

    public async Task ResolvePlacesAsync()
    {
        if (Current is null || !dialogs.Confirm(
                "代表地点の座標をNominatim（OpenStreetMapの地名サービス）へ送信します。\n続けますか？", "位置情報の送信確認")) return;
        await RunAsync("地名を取得しています（最大12地点）…", async ct =>
        {
            await SaveCurrentAsync(ct);
            await enrichment.ResolvePlacesAsync(store, Current, settings.NominatimContact, ct);
            Notify(nameof(PlacesText));
            RefreshMap();
            Status = "地名を保存しました。地名は近隣の参考情報で、通過施設を保証しません。";
        });
    }

    public async Task GenerateAsync()
    {
        if (Current is not { } current) return;
        var selected = Previous;
        var includePhotos = SendPhotos;
        var missingPlaces = new[] { current, selected }.OfType<WalkSession>()
            .Where(w => w.Places.Count == 0).Distinct().ToArray();
        if (!string.IsNullOrWhiteSpace(Blog) && !dialogs.Confirm(
                "現在の草稿を生成結果で置き換えます。続けますか？", "草稿の置き換え")) return;
        if (missingPlaces.Length > 0 && !dialogs.Confirm(
                $"地名が未取得の記録{missingPlaces.Length}件から、最大12地点ずつの代表座標と設定済みの連絡先メールアドレスをNominatim（OpenStreetMapの地名サービス）へ送信します（キャッシュ済みの地点は再送しません）。\n" +
                "取得した近傍地名をルートの順に並べ、ブログの資料に使います。取得に失敗した場合はブログ生成を中止します。\n地名を取得して草稿を生成しますか？",
                "ブログ用の地名取得")) return;
        if (!dialogs.Confirm("今回のルートの代表座標・地名・観察メモ" + (selected is null ? "" : "・過去の記録の代表座標・地名・要約") +
                (includePhotos ? "・今回の写真" : "") + "を、設定済みのAzure OpenAIに送信します。\nAPI利用料金が発生することがあります。続けますか？",
                "Azureへの送信確認")) return;
        await RunAsync("ブログ生成の資料を準備しています…", async ct =>
        {
            await SaveCurrentAsync(ct);
            foreach (var walk in missingPlaces)
            {
                Status = $"ブログ用の地名を取得しています（1記録につき最大12地点）: {walk.DisplayTitle}";
                await enrichment.ResolvePlacesAsync(store, walk, settings.NominatimContact, ct);
            }
            if (missingPlaces.Length > 0)
            {
                Notify(nameof(PlacesText));
                RefreshMap();
            }
            ct.ThrowIfCancellationRequested();
            Status = "Azure OpenAIがルートの地名を使って草稿を生成しています…";
            await enrichment.GenerateBlogAsync(store, current, selected, settings, includePhotos,
                new InlineProgress<string>(text => { Blog = text; Notify(nameof(Blog)); }), ct);
            Status = "AI草稿を保存しました。事実・推測・公開してよい位置情報を確認してください。";
        });
    }

    public async Task AddPhotoAsync()
    {
        if (Current is null) return;
        var path = dialogs.OpenPhotoFile();
        if (path is null) return;
        DateTime? captured;
        try { captured = photoMetadata.ReadCaptureTime(path); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            await ReportErrorAsync(ex, "JPEGの撮影情報を読み取れませんでした。");
            return;
        }
        var time = dialogs.ChoosePhotoTime(captured ?? Current.Points[0].Time.ToLocalTime().DateTime);
        if (time is null) return;
        if (photos.MatchPoint(Current, time.Value) is null)
        {
            Status = "撮影時刻の60秒以内にGPS点がありません。撮影時刻や時差を確認してください。";
            return;
        }
        await RunAsync("写真をGPS地点へ紐付けています…", async ct =>
        {
            await SaveCurrentAsync(ct);
            await photos.AttachAsync(store, Current, path, time.Value, ct);
            ShowSession(Current);
            Status = "写真を追加しました。撮影地点の推定は最寄り時刻のGPS点です。";
        });
    }

    public Task ReportAsync(bool export) => RunAsync(export ? "年次レポートを書き出しています…" : "年次記録を集計しています…", async ct =>
    {
        if (!int.TryParse(Year, out var year) || year is < 2000 or > 9999)
            throw new ArgumentException("対象年は2000〜9999で入力してください。");
        await SaveCurrentAsync(ct);
        Report = WalkReports.AnnualReport(Walks, year);
        Notify(nameof(Report));
        if (!export) { Status = $"{year}年の実記録を集計しました。"; return; }
        var path = dialogs.SaveFile($"WalkLogger-{year}.md", "Markdown|*.md");
        if (path is null) { Status = "書き出しをキャンセルしました。"; return; }
        await exporter.WriteTextAsync(path, Report, ct);
        Status = "年次レポートを書き出しました: " + path;
    });

    public Task ExportBlogAsync()
    {
        if (Current is null || string.IsNullOrWhiteSpace(Blog))
        {
            Status = "書き出す草稿を入力または生成してください。";
            return Task.CompletedTask;
        }
        return ExportTextAsync(() => Blog, Current.Points[0].Time.ToLocalTime().ToString("yyyyMMdd") + "-walk.md", "Markdown|*.md");
    }

    public Task ExportGpxAsync() => Current is null ? Task.CompletedTask :
        ExportTextAsync(() => exporter.ToGpx(Current), Current.Points[0].Time.ToLocalTime().ToString("yyyyMMdd") + "-walk.gpx", "GPX|*.gpx");

    private async Task ExportTextAsync(Func<string> text, string filename, string filter)
    {
        var path = dialogs.SaveFile(filename, filter);
        if (path is null) return;
        await RunAsync("ファイルを書き出しています…", async ct =>
        {
            await SaveCurrentAsync(ct);
            await exporter.WriteTextAsync(path, text(), ct);
            Status = "書き出しました: " + path;
        });
    }

    private void OpenArchive()
    {
        var folder = Current is null ? store.Root : store.SessionFolder(Current);
        if (!files.DirectoryExists(folder)) { Status = "保存先はまだ作成されていません。"; return; }
        dialogs.OpenFolder(folder);
    }

    private void ChooseArchive()
    {
        var folder = dialogs.ChooseArchiveFolder();
        if (folder is not null) { ArchiveRoot = Path.Combine(folder, "WalkLogger"); Notify(nameof(ArchiveRoot)); }
    }

    public Task SaveSettingsAsync() => RunAsync("設定を保存しています…", async ct =>
    {
        var next = settingsChanges.Prepare(ArchiveRoot, Endpoint, Deployment, Contact);
        await SaveCurrentAsync(ct);
        var result = await settingsChanges.ChangeAsync(next, ApiKey, ct);
        settings = result.Settings;
        store = result.Store;
        await RefreshAsync(null, ct);
        Initialized = true;
        if (SettingsApplied is not null)
            foreach (var handler in SettingsApplied.GetInvocationList().Cast<Func<Task>>()) await handler();
        Status = $"設定を保存しました。保存先: {store.Root}（旧保存先の記録は移動していません）";
    });
}
