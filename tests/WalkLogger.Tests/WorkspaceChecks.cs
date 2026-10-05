using WalkLogger.Application;
using WalkLogger.Core;
using WalkLogger.Presentation;

internal static class WorkspaceChecks
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-05T02:00:00Z");
    private static WalkSession Walk(string title = "walk") => new()
    {
        Title = title, Points = [new(Start, 35, 139)],
        Photos = [new(Start, 35, 139, "IMG_1.jpg", "original")]
    };

    public static async Task RunAsync(Action<bool, string> check)
    {
        var ports = new FakePorts();
        var photos = new PhotoAttachmentUseCase(ports);
        var walk = Walk();
        check(photos.MatchPoint(walk, Start.AddSeconds(60)) == walk.Points[0] &&
              photos.MatchPoint(walk, Start.AddSeconds(-60)) == walk.Points[0] &&
              photos.MatchPoint(walk, Start.AddSeconds(60.001)) is null,
            "Photo matching includes exactly 60 seconds and excludes 60.001 seconds");
        walk.Points.Add(new(Start.AddSeconds(120), 36, 140));
        check(photos.MatchPoint(walk, Start.AddSeconds(60)) == walk.Points[0] &&
              photos.MatchPoint(walk, Start.ToOffset(TimeSpan.FromHours(9))) == walk.Points[0],
            "Photo matching preserves first-point tie breaking and timestamp offsets");
        await photos.AttachAsync(ports.Initial, walk, "image.jpg", Start.AddSeconds(60), default);
        check(walk.Photos[^1].Lat == 35 && walk.Photos[^1].Image == "copied.jpg" && ports.Copies == 1 &&
              ports.Initial.Saves == 1, "Photo attachment copies and saves through fake ports");

        ports = new();
        var transfer = new BleTransferUseCase(ports, ports, new(ports));
        var received = await transfer.TransferAsync(ports.Initial, new(42, "CoreS3"), new IgnoreProgress<string>(), default);
        check(received.FileCount == 2 && ports.Events.SequenceEqual(new[]
        {
            "connect:42", "catalog", "download:1", "download:2", "import:a.gpx", "import:z.gpx", "dispose"
        }), "BLE use case downloads before sorted import and disposes its session");
        check(ports.DownloadRoots.All(p => p == Path.Combine(ports.LocalRoot, "Imports", "00000000002A")),
            "BLE resume directory retains its device-address naming");
        ports = new() { Catalog = [] };
        try
        {
            await new BleTransferUseCase(ports, ports, new(ports)).TransferAsync(ports.Initial, new(42, "CoreS3"),
                new IgnoreProgress<string>(), default);
            throw new Exception("Empty BLE catalog unexpectedly succeeded");
        }
        catch (InvalidDataException)
        {
            check(ports.Events.SequenceEqual(new[] { "connect:42", "catalog", "dispose" }),
                "Empty BLE catalog is reported and disposed without import");
        }
        ports = new();
        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            try
            {
                await new BleTransferUseCase(ports, ports, new(ports)).TransferAsync(ports.Initial, new(42, "CoreS3"),
                    new IgnoreProgress<string>(), source.Token);
                throw new Exception("Cancelled BLE use case unexpectedly succeeded");
            }
            catch (OperationCanceledException)
            {
                check(ports.Events.Last() == "dispose" && !ports.Events.Any(e => e.StartsWith("import:")),
                    "BLE cancellation disposes the connection without importing incomplete data");
            }
        }

        ports = new();
        var model = ports.Model();
        await model.InitializeAsync();
        check(model.Current == ports.Initial.Records[0] && model.PreviousWalks.Count == 1 && model.Idle,
            "ViewModel initializes and selects records without WPF");
        model.Title = "  edited title  ";
        model.Notes = "edited notes";
        model.Blog = "edited blog";
        model.CompletedLoop = true;
        model.PhotoEditors[0].Note = "edited photo";
        var original = model.Current!;
        await model.SelectSessionAsync(ports.Initial.Records[1]);
        check(original.Title == "edited title" && original.Notes == "edited notes" &&
              original.Blog == "edited blog" && original.CompletedLoop && original.Photos[0].Note == "edited photo" &&
              ports.Initial.Saves == 1, "Selection saves all edits and photo notes before switching");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        original = model.Current!;
        ports.Initial.SaveFailureAt = 1;
        await model.SelectSessionAsync(ports.Initial.Records[1]);
        check(model.Current == original && ports.Errors.Count == 1 && model.Idle,
            "Failed save retains selected record and reports the error");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        model.Title = " ";
        await model.SelectSessionAsync(ports.Initial.Records[1]);
        check(model.Current == ports.Initial.Records[0] && ports.Initial.Saves == 0 &&
              ports.Errors.Single().Contains("タイトル"), "Invalid title blocks saving and selection");

        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = model.RunAsync("running", ct => gate.Task.WaitAsync(ct));
        var secondRan = false;
        await model.RunAsync("second", _ => { secondRan = true; return Task.CompletedTask; });
        check(model.Busy && !model.Idle && !secondRan && model.Status.Contains("処理中"),
            "ViewModel prevents concurrent operations");
        model.Cancel();
        await running;
        check(model.Idle && model.Status.Contains("キャンセル") && ports.Errors.Count == 0,
            "Cancellation restores idle state without an error dialog");

        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        ports.PhotoTime = Start.AddSeconds(60.001);
        await model.AddPhotoAsync();
        check(ports.Copies == 0 && ports.Initial.Saves == 0 && model.Status.Contains("60秒"),
            "Out-of-range photo displays the existing warning without copying or saving");
        ports.PhotoTime = Start.AddSeconds(60);
        await model.AddPhotoAsync();
        check(ports.Copies == 1 && ports.Initial.Saves == 2 && model.PhotoEditors.Count == 2,
            "Photo command saves edits before attachment and refreshes its editor");

        ports = new() { Consent = false };
        model = ports.Model();
        await model.InitializeAsync();
        await model.ResolvePlacesAsync();
        await model.GenerateAsync();
        check(ports.PlaceCalls == 0 && ports.BlogCalls == 0 && ports.Initial.Saves == 0,
            "Declined location and Azure confirmations never call external ports");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        model.Previous = model.PreviousWalks[0];
        model.SendPhotos = true;
        await model.GenerateAsync();
        check(ports.BlogCalls == 1 && ports.LastPrevious == model.Previous && ports.LastIncludePhotos &&
              model.Blog == "generated" && ports.Initial.Saves == 2 && model.Current!.Blog == "generated",
            "Blog generation preserves selected comparison, photo opt-in and save order");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        ports.Initial.SaveFailureAt = 2;
        await model.GenerateAsync();
        check(model.Blog == "generated" && model.Current!.Blog == "generated" && ports.Errors.Count == 1,
            "Generated draft stays editable when its persistence fails");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        await model.ResolvePlacesAsync();
        check(model.PlacesText == "test place" && ports.Initial.Saves == 2 &&
              model.Current!.Places.Count == 1, "Place command saves edits before resolving and persisting labels");

        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        original = model.Current!;
        model.ArchiveRoot = ports.NewRoot;
        ports.UnreadableRoot = ports.NewRoot;
        await model.SaveSettingsAsync();
        check(model.Current == original && ports.SettingsSaves == 0 && ports.Errors.Count == 1,
            "Unreadable new archive never replaces the active store or saves settings");
        ports.UnreadableRoot = null;
        await model.SaveSettingsAsync();
        check(model.Current is null && model.NoSession && ports.SettingsSaves == 1 &&
              ports.Configuration.ArchiveRoot == ports.NewRoot,
            "Successful archive change switches stores without moving old records");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        model.ArchiveRoot = "relative";
        await model.SaveSettingsAsync();
        check(ports.Initial.Saves == 0 && ports.SettingsSaves == 0 && ports.Errors.Count == 1,
            "Invalid archive path is rejected before saving current edits");

        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        await model.LoadDemoAsync();
        check(model.Current!.IsDemo && model.Current.Title == "上野から東京へ（合成ルート）" &&
              model.Current.Notes.Contains("合成データ"), "Demo use case preserves synthetic-data marking and notes");
        model.Year = "2026";
        await model.ReportAsync(false);
        check(model.Report.Contains("街歩き: 2回"), "ViewModel report excludes demo records");
        model.Year = "1999";
        await model.ReportAsync(false);
        check(ports.Errors.Last().Contains("2000"), "Report year retains the 2000 to 9999 input range");
        await model.ExportGpxAsync();
        check(ports.WrittenText == "gpx:" + model.Current.Title && ports.Initial.Saves >= 4,
            "Export saves edits before producing and writing its text");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        model.Previous = model.PreviousWalks[0];
        check(model.ComparisonText.Contains("今回:") && model.ComparisonText.Contains("過去:"),
            "Comparison presentation uses the two measured records");
        await model.ScanAsync();
        check(model.SelectedDevice?.Address == 42 && model.Devices.Count == 1, "Scan selects the first discovered device");
        await model.TransferAsync();
        check(model.Status.Contains("BLE転送完了") && model.Walks.Count == 4, "Transfer command refreshes the imported archive");
        await model.ImportFileAsync();
        await model.ImportFolderAsync();
        check(model.Walks.Count == 7, "File and folder commands save edits and refresh imported selections");
        await model.ExportBlogAsync();
        check(model.Status.Contains("草稿"), "Empty blog export displays the draft warning");
        model.Blog = "written draft";
        await model.ExportBlogAsync();
        check(ports.WrittenText == "written draft", "Blog export writes the edited draft");
        await model.ReportAsync(true);
        check(ports.WrittenText == model.Report, "Report export writes the generated report");
        model.SaveCommand.Execute(null);
        check(model.SaveCommand.CanExecute(null) && model.Status.Contains("草稿・GPX"), "Async ICommand executes its save action");
        model.ShowBleCommand.Execute(null);
        check(model.BleVisible && model.ShowBleCommand.CanExecute(null), "Action command shows the BLE panel");
        model.HideBleCommand.Execute(null);
        model.ClearPreviousCommand.Execute(null);
        model.ChooseArchiveCommand.Execute(null);
        model.OpenArchiveCommand.Execute(null);
        check(!model.BleVisible && model.Previous is null && model.ArchiveRoot.EndsWith("WalkLogger"),
            "Presentation-only commands clear selection, choose a root and open the archive through ports");
        ports = new() { CancelDialogs = true, NoFiles = true, NoDevices = true };
        model = ports.Model();
        await model.InitializeAsync();
        await model.ImportFileAsync();
        await model.ImportFolderAsync();
        await model.AddPhotoAsync();
        await model.ExportGpxAsync();
        await model.ScanAsync();
        await model.TransferAsync();
        model.OpenArchiveCommand.Execute(null);
        model.ChooseArchiveCommand.Execute(null);
        check(ports.Initial.Saves == 0 && model.SelectedDevice is null,
            "Cancelled dialogs and absent device leave the archive unchanged");
        ports = new() { MetadataFailure = true };
        model = ports.Model();
        await model.InitializeAsync();
        await model.AddPhotoAsync();
        check(ports.Errors.Single().Contains("撮影情報"), "Metadata read error is reported before photo time selection");
        ports = new() { CancelPhotoTime = true };
        model = ports.Model();
        await model.InitializeAsync();
        await model.AddPhotoAsync();
        check(ports.Copies == 0, "Photo-time cancellation does not copy an image");
        ports = new();
        model = ports.Model();
        await model.InitializeAsync();
        model.ArchiveRoot = ports.NewRoot;
        await model.SaveSettingsAsync();
        await model.ExportGpxAsync();
        await model.ExportBlogAsync();
        await model.GenerateAsync();
        await model.ResolvePlacesAsync();
        await model.AddPhotoAsync();
        check(model.Current is null && model.NoSession, "Commands without a selected record do not create fictitious data");
    }

    private sealed class IgnoreProgress<T> : IProgress<T> { public void Report(T value) { } }

    internal sealed class FakeArchive(FakePorts ports, string root) : IArchiveStore
    {
        public List<WalkSession> Records { get; } = [];
        public int Saves { get; private set; }
        public int SaveFailureAt { get; set; }
        public string Root => root;
        public string SessionFolder(WalkSession session) => Path.Combine(root, session.Id);
        public Task<List<WalkSession>> LoadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (root == ports.UnreadableRoot) throw new IOException("Unreadable archive");
            return Task.FromResult(Records.ToList());
        }
        public Task SaveAsync(WalkSession walk, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Saves++;
            if (Saves == SaveFailureAt) throw new IOException("Save failed");
            return Task.CompletedTask;
        }
        public Task<WalkSession> ImportAsync(string path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ports.Events.Add("import:" + Path.GetFileName(path));
            var walk = Walk("imported");
            Records.Add(walk);
            return Task.FromResult(walk);
        }
    }

    internal sealed class FakePorts : IArchiveStoreFactory, ISettingsStore, IWorkspaceFiles,
        IBlogService, IPlaceService, IFileExporter, IPhotoMetadataReader, IBleTransferFactory,
        IWorkspaceDialogs, IWorkspaceDiagnostics
    {
        private readonly Dictionary<string, FakeArchive> stores = [];
        public FakePorts(string? localRoot = null)
        {
            LocalRoot = localRoot ?? Path.Combine(Path.GetTempPath(), "WalkLogger-fake-ports");
            Configuration = new() { ArchiveRoot = Path.Combine(LocalRoot, "Archive") };
            Initial = (FakeArchive)Create(Configuration.ArchiveRoot);
            Initial.Records.Add(Walk("current"));
            var earlier = Walk("previous");
            earlier.Points = [new(Start.AddDays(-1), 35, 139)];
            Initial.Records.Add(earlier);
        }
        public string LocalRoot { get; }
        public string NewRoot => Path.Combine(LocalRoot, "NewArchive");
        public FakeArchive Initial { get; }
        public SettingsData Configuration { get; private set; }
        public List<string> Events { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> DownloadRoots { get; } = [];
        public List<RemoteFile> Catalog { get; init; } = [new(1, "track.ndjson", 1), new(2, "IMG_1.jpg", 1)];
        public int Copies { get; private set; }
        public int SettingsSaves { get; private set; }
        public int PlaceCalls { get; private set; }
        public int BlogCalls { get; private set; }
        public WalkSession? LastPrevious { get; private set; }
        public bool LastIncludePhotos { get; private set; }
        public bool Consent { get; init; } = true;
        public bool CancelDialogs { get; init; }
        public bool CancelPhotoTime { get; init; }
        public bool MetadataFailure { get; init; }
        public bool NoDevices { get; init; }
        public bool NoFiles { get; init; }
        public DateTimeOffset PhotoTime { get; set; } = Start;
        public string? UnreadableRoot { get; set; }
        public string? WrittenText { get; private set; }
        public MainWindowViewModel Model() => new(this, this, this, this, this, new(this),
            new(), new(this, this, new(this)), new(this), new(this, this, this), new(this, this, this, this),
            new(), this, this, new(AppContext.BaseDirectory));
        public IArchiveStore Create(string root)
        {
            if (!stores.TryGetValue(root, out var store)) stores.Add(root, store = new(this, root));
            return store;
        }
        public Task<SettingsData> LoadAsync() => Task.FromResult(Configuration);
        public Task SaveAsync(SettingsData settings, string key)
        {
            SettingsSaves++;
            Configuration = settings;
            return Task.CompletedTask;
        }
        public string LoadKey() => "fake-key";
        public string[] FindImportFiles(string folder) => [Path.Combine(folder, "z.gpx"), Path.Combine(folder, "a.gpx")];
        public bool DirectoryExists(string folder) => !NoFiles;
        public void CreateDirectory(string folder) { }
        public string CopyPhoto(string source, string destinationFolder) { Copies++; return "copied.jpg"; }
        public Task AppendDiagnosticsAsync(string localRoot, string text) => Task.CompletedTask;
        public Uri ValidateEndpoint(string endpoint) => new(endpoint);
        public Task<string> GenerateAsync(string endpoint, string deployment, string apiKey, WalkSession walk,
            WalkSession? previous, string photoFolder, bool includePhotos, CancellationToken ct)
        {
            BlogCalls++;
            LastPrevious = previous;
            LastIncludePhotos = includePhotos;
            return Task.FromResult("generated");
        }
        public Task<List<PlaceLabel>> ResolveAsync(WalkSession walk, string contact, CancellationToken ct)
        {
            PlaceCalls++;
            return Task.FromResult(new List<PlaceLabel> { new(35, 139, "test place") });
        }
        public string ToGpx(WalkSession walk) => "gpx:" + walk.Title;
        public Task WriteTextAsync(string path, string text, CancellationToken ct = default)
        {
            WrittenText = text;
            return Task.CompletedTask;
        }
        public Task WriteBytesAsync(string path, byte[] bytes, CancellationToken ct = default) => Task.CompletedTask;
        public DateTime? ReadCaptureTime(string path) => MetadataFailure ? throw new IOException("Metadata failed") : null;
        public Task<List<DeviceInfo>> ScanAsync(CancellationToken ct) => Task.FromResult(NoDevices ? new List<DeviceInfo>() : [new(42, "CoreS3")]);
        public IBleTransferSession Create() => new FakeBleSession(this);
        public string? OpenLogFile() => CancelDialogs ? null : "route.gpx";
        public string? OpenImportFolder() => CancelDialogs ? null : LocalRoot;
        public string? OpenPhotoFile() => CancelDialogs ? null : "photo.jpg";
        public string? ChooseArchiveFolder() => CancelDialogs ? null : LocalRoot;
        public string? SaveFile(string filename, string filter) => CancelDialogs ? null : Path.Combine(LocalRoot, filename);
        public bool Confirm(string text, string title) => Consent;
        public DateTimeOffset? ChoosePhotoTime(DateTime initialTime) => CancelPhotoTime ? null : PhotoTime;
        public void OpenFolder(string folder) { }
        public Task<string> ReportAsync(Exception exception, string? context = null, IProgress<string>? status = null)
        {
            var message = (context ?? "") + exception.Message;
            Errors.Add(message);
            status?.Report(message);
            return Task.FromResult(message);
        }
    }

    private sealed class FakeBleSession(FakePorts ports) : IBleTransferSession
    {
        public Task ConnectAsync(ulong address, CancellationToken ct)
        {
            ports.Events.Add("connect:" + address);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<List<RemoteFile>> CatalogAsync(CancellationToken ct)
        {
            ports.Events.Add("catalog");
            return Task.FromResult(ports.Catalog);
        }
        public Task DownloadAsync(RemoteFile file, string root, IProgress<string> progress, CancellationToken ct)
        {
            ports.Events.Add("download:" + file.Id);
            ports.DownloadRoots.Add(root);
            return Task.CompletedTask;
        }
        public void Dispose() => ports.Events.Add("dispose");
    }
}
