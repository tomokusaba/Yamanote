using WalkLogger.Core;
using WalkLogger.Application;

namespace WalkLogger.Recording;

public enum RecordingPhase { Recording, Paused, Finished }

public sealed record RecordingInfo(string Id, DateTimeOffset StartedAt, int IntervalSeconds,
    RecordingPhase Phase, DateTimeOffset? FinishedAt = null);

public sealed record RecordedWalk(RecordingInfo Info, IReadOnlyList<TrackPoint> Points,
    IReadOnlyList<PhotoRecord> Photos)
{
    public string Title => Info.StartedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string Details => $"{WalkAnalysis.Calculate(Points).DistanceKm:F2} km / {Points.Count}点 / 写真{Photos.Count}枚";
}

public interface IRecordingStore
{
    Task<RecordedWalk?> LoadActiveAsync();
    Task<IReadOnlyList<RecordedWalk>> ListAsync();
    Task SaveInfoAsync(RecordingInfo info);
    Task AppendPointAsync(string id, TrackPoint point);
    Task AppendPhotoAsync(string id, PhotoRecord photo, Stream jpeg);
    Task<string> ExportAsync(string id, bool includePhotos);
}

public sealed class RecordingEngine(IRecordingStore store, TimeProvider clock, PhotoAttachmentUseCase photoMatcher)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private RecordingInfo? info;
    private List<TrackPoint> points = [];
    private List<PhotoRecord> photos = [];
    private int segment;
    private bool initialized;
    public event EventHandler? Changed;
    public RecordedWalk? Current { get; private set; }
    public string? LastError { get; private set; }

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (initialized) return;
            if (await store.LoadActiveAsync() is { } recovered)
            {
                info = recovered.Info;
                points = recovered.Points.ToList();
                photos = recovered.Photos.ToList();
                segment = points.Count == 0 ? 0 : checked(points[^1].Segment + 1);
                Publish();
            }
            initialized = true;
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(int intervalSeconds)
    {
        if (intervalSeconds is not (5 or 10))
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "記録間隔は5秒または10秒です。");
        await InitializeAsync();
        await gate.WaitAsync();
        try
        {
            if (info is { Phase: not RecordingPhase.Finished })
                throw new InvalidOperationException("現在の記録を終了してから新しく開始してください。");
            var next = new RecordingInfo(Guid.NewGuid().ToString("N"), clock.GetUtcNow(),
                intervalSeconds, RecordingPhase.Recording);
            await store.SaveInfoAsync(next);
            info = next;
            points = [];
            photos = [];
            segment = 0;
            LastError = null;
            Publish();
        }
        finally { gate.Release(); }
    }

    public async Task PauseAsync() => await ChangePhaseAsync(RecordingPhase.Paused);
    public async Task FinishAsync() => await ChangePhaseAsync(RecordingPhase.Finished);

    public async Task ResumeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (info is not { Phase: RecordingPhase.Paused })
                throw new InvalidOperationException("一時停止中の記録だけ再開できます。");
            var next = info with { Phase = RecordingPhase.Recording };
            await store.SaveInfoAsync(next);
            info = next;
            segment = checked(segment + 1);
            LastError = null;
            Publish();
        }
        finally { gate.Release(); }
    }

    private async Task ChangePhaseAsync(RecordingPhase phase)
    {
        await gate.WaitAsync();
        try
        {
            if (info is null || info.Phase == RecordingPhase.Finished)
                throw new InvalidOperationException("進行中の記録がありません。");
            var next = info with { Phase = phase, FinishedAt = phase == RecordingPhase.Finished ? clock.GetUtcNow() : null };
            await store.SaveInfoAsync(next);
            info = next;
            Publish();
        }
        finally { gate.Release(); }
    }

    public async Task<bool> RecordAsync(TrackPoint measurement)
    {
        measurement.Validate();
        await gate.WaitAsync();
        try
        {
            if (info is not { Phase: RecordingPhase.Recording }) return false;
            if (measurement.Time < info.StartedAt) return false;
            if (points.Count > 0)
            {
                var last = points[^1];
                if (measurement.Time <= last.Time) return false;
                if (last.Segment == segment && (measurement.Time - last.Time).TotalSeconds < info.IntervalSeconds)
                    return false;
            }
            var point = measurement with { Segment = segment };
            await store.AppendPointAsync(info.Id, point);
            points.Add(point);
            Publish();
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task AddPhotoAsync(string sessionId, Stream jpeg, DateTimeOffset capturedAt, string note)
    {
        await gate.WaitAsync();
        try
        {
            if (info?.Id != sessionId)
                throw new InvalidOperationException("撮影中に記録が切り替わりました。写真を現在の記録へ追加できません。");
            var point = photoMatcher.MatchPoint(new WalkSession { Points = points }, capturedAt) ??
                throw new InvalidOperationException("撮影時刻の前後60秒にGPS記録がありません。屋外で測位してから撮影してください。");
            var photo = new PhotoRecord(capturedAt.ToUniversalTime(), point.Lat, point.Lon,
                $"IMG_{Guid.NewGuid():N}.jpg", note);
            await store.AppendPhotoAsync(sessionId, photo, jpeg);
            photos.Add(photo);
            Publish();
        }
        finally { gate.Release(); }
    }

    public void ReportError(Exception error)
    {
        LastError = error.Message;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Publish()
    {
        Current = info is null ? null : new(info, points.ToArray(), photos.ToArray());
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
