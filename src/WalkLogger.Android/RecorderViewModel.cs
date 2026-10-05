using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WalkLogger.Core;
using WalkLogger.Recording;

namespace WalkLogger.Mobile;

public sealed class RecorderViewModel : INotifyPropertyChanged
{
    private readonly RecordingEngine engine;
    private readonly IRecordingStore store;
    private readonly AndroidGpsControl gps;
    private readonly AndroidPhotoCapture camera;
    private bool busy;
    private string message = "";
    private string photoNote = "";
    private string fixStatus = "GPS未取得。屋外で「記録開始」を押してください。";
    private readonly Command primary, pause, finish, photo, refresh, exportZip, exportGpx;
    public event PropertyChangedEventHandler? PropertyChanged;
    public RecordedWalk? Current => engine.Current;
    public ObservableCollection<RecordedWalk> History { get; } = [];
    public RecordedWalk? Selected { get; set; }
    public bool Busy { get => busy; private set { busy = value; Notify(); NotifyCommands(); } }
    public string Message { get => message; private set { message = value; Notify(); } }
    public string PhotoNote { get => photoNote; set { photoNote = value; Notify(); } }
    public string FixStatus { get => fixStatus; private set { fixStatus = value; Notify(); } }
    public string Status => Current?.Info.Phase switch
    {
        RecordingPhase.Recording => Current.Points.Count == 0 ? "GPSを待っています" : "街歩きを記録中",
        RecordingPhase.Paused => "記録を一時停止中",
        RecordingPhase.Finished => "記録を保存しました",
        _ => "次の街歩きを残そう"
    };
    public string Distance => $"{WalkAnalysis.Calculate(Current?.Points ?? []).DistanceKm:F2} km";
    public string Elapsed => WalkAnalysis.FormatDuration(WalkAnalysis.Calculate(Current?.Points ?? []).Elapsed);
    public string Counts => Current is null ? "5秒または10秒間隔でGPSを保存します。"
        : $"{Current.Points.Count}点 / 写真{Current.Photos.Count}枚 / {Current.Info.IntervalSeconds}秒間隔";
    public string PrimaryLabel => Current?.Info.Phase switch
    {
        RecordingPhase.Paused => "記録を再開",
        RecordingPhase.Recording => "記録中",
        _ => "記録開始"
    };
    public bool CanPrimary => !Busy && Current?.Info.Phase != RecordingPhase.Recording;
    public bool CanPause => !Busy && Current?.Info.Phase == RecordingPhase.Recording;
    public bool CanFinish => !Busy && Current?.Info.Phase is RecordingPhase.Recording or RecordingPhase.Paused;
    public bool CanPhoto => !Busy && CanFinish && Current?.Points.Count > 0;
    public ICommand PrimaryCommand => primary;
    public ICommand PauseCommand => pause;
    public ICommand FinishCommand => finish;
    public ICommand PhotoCommand => photo;
    public ICommand RefreshCommand => refresh;
    public ICommand ExportZipCommand => exportZip;
    public ICommand ExportGpxCommand => exportGpx;
    public int IntervalSeconds
    {
        get => Preferences.Default.Get("RecordingInterval", 10);
        set
        {
            if (value is not (5 or 10)) throw new ArgumentOutOfRangeException(nameof(value));
            Preferences.Default.Set("RecordingInterval", value);
            Notify();
        }
    }

    public RecorderViewModel(RecordingEngine engine, IRecordingStore store,
        AndroidGpsControl gps, AndroidPhotoCapture camera)
    {
        this.engine = engine;
        this.store = store;
        this.gps = gps;
        this.camera = camera;
        primary = Make(StartOrResumeAsync, () => CanPrimary);
        pause = Make(() => gps.PauseAsync(), () => CanPause);
        finish = Make(FinishAsync, () => CanFinish);
        photo = Make(TakePhotoAsync, () => CanPhoto);
        refresh = Make(RefreshHistoryAsync);
        exportZip = Make(() => ShareAsync(true));
        exportGpx = Make(() => ShareAsync(false));
        engine.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(Update);
        gps.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() => FixStatus = status);
    }

    private Command Make(Func<Task> operation, Func<bool>? canExecute = null) =>
        new(async () => await RunAsync(operation), () => !Busy && (canExecute?.Invoke() ?? true));

    private async Task RunAsync(Func<Task> operation)
    {
        if (Busy) return;
        Busy = true;
        Message = "";
        try { await operation(); }
        catch (Exception ex) { Message = ex.Message; System.Diagnostics.Trace.TraceError(ex.ToString()); }
        finally { Busy = false; Update(); }
    }

    public Task InitializeAsync() => RunAsync(async () =>
    {
        await engine.InitializeAsync();
        if (Current?.Info.Phase == RecordingPhase.Recording && !GpsRecordingService.IsRunning && !gps.StartRequested)
        {
            await engine.PauseAsync();
            Message = "前回の記録を復元しました。「記録を再開」で別のGPS区間として続けられます。";
        }
        Update();
    });

    private async Task StartOrResumeAsync()
    {
        await gps.CheckPermissionsAsync();
        if (Current?.Info.Phase == RecordingPhase.Paused) await engine.ResumeAsync();
        else await engine.StartAsync(IntervalSeconds);
        try { gps.Start(); }
        catch
        {
            await engine.PauseAsync();
            throw;
        }
    }

    private async Task FinishAsync()
    {
        if (await Shell.Current.DisplayAlertAsync("記録を終了", "GPS記録を終了し、端末内に保存しますか？", "終了する", "続ける"))
        {
            await gps.FinishAsync();
            await RefreshHistoryAsync();
        }
    }

    private async Task TakePhotoAsync()
    {
        var id = Current?.Info.Id ?? throw new InvalidOperationException("進行中の記録がありません。");
        if (await camera.CaptureAsync() is not { } capture) return;
        await using (capture)
        {
            var note = PhotoNote;
            if (capture.EstimatedTime) note = (note + "\n撮影日時は写真返却時刻による推定。").Trim();
            await engine.AddPhotoAsync(id, capture.Content, capture.Time, note);
        }
        PhotoNote = "";
        Message = "写真と撮影位置を保存しました。";
    }

    public Task LoadHistoryAsync() => RunAsync(RefreshHistoryAsync);
    public void ShowMapError(string error) => Message = error;
    private async Task RefreshHistoryAsync()
    {
        var records = await store.ListAsync();
        History.Clear();
        foreach (var item in records) History.Add(item);
    }

    private async Task ShareAsync(bool includePhotos)
    {
        var record = Selected ?? throw new InvalidOperationException("共有する記録を選択してください。");
        if (!await Shell.Current.DisplayAlertAsync("位置情報を共有",
                includePhotos ? "GPSと写真をZIPにまとめ、選択した共有先へ渡します。OneDriveも選択できます。"
                    : "位置情報と時刻を含むGPXを、選択した共有先へ渡します。",
                "共有する", "戻る")) return;
        var path = await store.ExportAsync(record.Info.Id, includePhotos);
        await Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = includePhotos ? "街歩きのGPSと写真" : "街歩きのGPX",
            File = new ShareFile(path, includePhotos ? "application/zip" : "application/gpx+xml")
        });
    }

    private void Update()
    {
        foreach (var property in new[] { nameof(Current), nameof(Status), nameof(Distance), nameof(Elapsed),
                     nameof(Counts), nameof(PrimaryLabel), nameof(CanPrimary), nameof(CanPause), nameof(CanFinish), nameof(CanPhoto) })
            Notify(property);
        if (engine.LastError is { } error) Message = error;
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        foreach (var command in new[] { primary, pause, finish, photo, refresh, exportZip, exportGpx })
            command.ChangeCanExecute();
    }
    private void Notify([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new(property));
}
