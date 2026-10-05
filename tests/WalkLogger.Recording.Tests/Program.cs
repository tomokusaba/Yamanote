using System.IO.Compression;
using System.Text.Json;
using WalkLogger.Application;
using WalkLogger.Core;
using WalkLogger.Infrastructure;
using WalkLogger.Recording;
using WalkLogger.Recording.Infrastructure;

var checks = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new InvalidOperationException("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    checks++;
}
async Task Reject<T>(Func<Task> operation, string name) where T : Exception
{
    try { await operation(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL: " + name);
}
var start = DateTimeOffset.Parse("2026-10-05T08:00:00Z");
var clock = new TestClock(start);
var files = new WorkspaceFiles();
RecordingEngine Engine(IRecordingStore store) => new(store, clock, new PhotoAttachmentUseCase(files));
TrackPoint Point(int second, int segment = 0) => new(start.AddSeconds(second), 35.68 + second * .000001,
    139.76, 4.5, 15, segment);
MemoryStream Jpeg() => new([0xff, 0xd8, 1, 2, 3, 0xff, 0xd9]);
var memory = new MemoryStore();
var engine = Engine(memory);
Check(engine.Current is null, "Empty initial recording");
await engine.InitializeAsync();
await engine.InitializeAsync();
await Reject<ArgumentOutOfRangeException>(() => engine.StartAsync(1), "Reject unsupported cadence");
await Reject<InvalidOperationException>(engine.PauseAsync, "Reject pause without recording");
await Reject<InvalidOperationException>(engine.FinishAsync, "Reject finish without recording");
await Reject<InvalidOperationException>(engine.ResumeAsync, "Reject resume without paused recording");
Check(!await engine.RecordAsync(Point(0)), "No recording outside session");
await engine.StartAsync(10);
await Reject<InvalidOperationException>(() => engine.StartAsync(5), "Protect unfinished recording");
Check(!await engine.RecordAsync(Point(-1)), "Reject measurement preceding start");
Check(await engine.RecordAsync(Point(0)), "Accept first valid fix");
Check(!await engine.RecordAsync(Point(0)), "Reject duplicate timestamp");
Check(!await engine.RecordAsync(Point(-1)), "Reject backward timestamp");
Check(!await engine.RecordAsync(Point(9)), "Enforce ten-second cadence");
Check(await engine.RecordAsync(Point(10)), "Inclusive ten-second interval");
Check(engine.Current!.Points[^1].Speed == 4.5 && engine.Current.Points[^1].Altitude == 15,
    "Preserve km/h speed and meter altitude");
await Reject<InvalidDataException>(() => engine.RecordAsync(Point(11) with { Lat = double.NaN }),
    "Reject invalid GPS");
await Reject<InvalidOperationException>(() => engine.AddPhotoAsync(Guid.NewGuid().ToString("N"),
    Jpeg(), start, ""), "Reject photo for different session");
await Reject<InvalidOperationException>(() => engine.AddPhotoAsync(engine.Current.Info.Id,
    Jpeg(), start.AddSeconds(71), ""), "Reject photo more than sixty seconds from fix");
await engine.AddPhotoAsync(engine.Current.Info.Id, Jpeg(), start.AddSeconds(70), "店の看板を撮影");
Check(engine.Current.Photos.Count == 1 && engine.Current.Photos[0].Lat == Point(10).Lat,
    "Inclusive sixty-second photo match and observed note");
await engine.AddPhotoAsync(engine.Current.Info.Id, Jpeg(), start.AddSeconds(5), "");
Check(engine.Current.Photos[^1].Lat == Point(0).Lat, "Photo ties select earlier fix");
await engine.PauseAsync();
Check(!await engine.RecordAsync(Point(20)), "Pause blocks GPS writes");
await engine.ResumeAsync();
Check(await engine.RecordAsync(Point(11)), "Resume writes first fix without cadence suppression");
Check(engine.Current.Points[^1].Segment != engine.Current.Points[^2].Segment,
    "Resume starts separate GPS segment");
var expectedDistance = WalkAnalysis.DistanceMeters(Point(0).Lat, Point(0).Lon, Point(10).Lat, Point(10).Lon);
Check(Math.Abs(WalkAnalysis.Calculate(engine.Current.Points).DistanceKm * 1000 - expectedDistance) < .001,
    "Distance excludes paused gap");
engine.ReportError(new IOException("disk full"));
Check(engine.LastError == "disk full", "Recording failures surface explicitly");
await engine.PauseAsync();
await engine.ResumeAsync();
Check(engine.LastError is null, "Explicit resume clears previous error");
clock.Now = start.AddSeconds(30);
await engine.FinishAsync();
Check(engine.Current.Info.FinishedAt == clock.Now, "Persist completion timestamp");
await Reject<InvalidOperationException>(engine.PauseAsync, "Reject pause after completion");
await Reject<InvalidOperationException>(engine.ResumeAsync, "Reject resume after completion");
memory.FailSave = true;
await Reject<IOException>(() => engine.StartAsync(5), "Surface metadata write failure");
Check(engine.Current.Info.Phase == RecordingPhase.Finished, "Failed start preserves previous recording");
memory.FailSave = false;
await engine.StartAsync(5);
memory.FailAppend = true;
await Reject<IOException>(() => engine.RecordAsync(Point(30)), "Surface GPS write failure");
Check(engine.Current!.Points.Count == 0, "Do not publish unwritten GPS point");
memory.FailAppend = false;
Check(await engine.RecordAsync(Point(30)) && await engine.RecordAsync(Point(35)), "Five-second cadence");
await engine.PauseAsync();
memory.FailSave = true;
await Reject<IOException>(engine.ResumeAsync, "Surface resume write failure");
Check(engine.Current.Info.Phase == RecordingPhase.Paused, "Failed resume stays paused");
memory.FailSave = false;
await engine.ResumeAsync();
var recovered = Engine(memory);
await recovered.InitializeAsync();
Check(recovered.Current?.Points.Count == 2, "Recover committed GPS points");
Check(await recovered.RecordAsync(Point(40)) &&
      recovered.Current!.Points[^1].Segment > recovered.Current.Points[^2].Segment,
    "Recovery does not connect process-interruption gap");
for (var second = 45; second <= 28830; second += 5) await recovered.RecordAsync(Point(second));
Check(recovered.Current!.Points.Count == 5761, "Eight-hour recording has expected five-second point count");
await recovered.FinishAsync();

var root = Path.Combine(Path.GetTempPath(), "WalkLogger-recording-tests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new FileRecordingStore(Path.Combine(root, "records"), Path.Combine(root, "exports"));
    Check((await store.ListAsync()).Count == 0 && await store.LoadActiveAsync() is null, "Empty file store");
    clock.Now = start;
    var fileEngine = Engine(store);
    await fileEngine.StartAsync(5);
    var id = fileEngine.Current!.Info.Id;
    var folder = Path.Combine(root, "records", id);
    Check((await store.ListAsync()).Single().Points.Count == 0, "Restore session before first fix");
    Check(fileEngine.Current.Title == start.ToLocalTime().ToString("yyyy/MM/dd HH:mm") &&
          fileEngine.Current.Details.Contains("写真0枚"), "Recording history shows real local time and counts");
    await File.WriteAllTextAsync(Path.Combine(folder, "photos.ndjson"), "");
    await Reject<InvalidDataException>(() => store.ListAsync(), "Reject photo log without GPS source");
    File.Delete(Path.Combine(folder, "photos.ndjson"));
    await Reject<InvalidOperationException>(() => store.ExportAsync(id, true), "Do not export active recording");
    await fileEngine.PauseAsync();
    await Reject<InvalidOperationException>(() => store.ExportAsync(id, false), "Do not export no-fix GPX");
    await fileEngine.ResumeAsync();
    await fileEngine.RecordAsync(Point(0));
    await fileEngine.RecordAsync(Point(5));
    await fileEngine.PauseAsync();
    await fileEngine.ResumeAsync();
    await fileEngine.RecordAsync(Point(6));
    await fileEngine.AddPhotoAsync(id, Jpeg(), start.AddSeconds(5), "写真の実測メモ");
    await Reject<InvalidDataException>(() => store.AppendPhotoAsync(id,
        new(start, 35.68, 139.76, "..\\bad.jpg"), Jpeg()), "Reject unsafe photo path");
    await Reject<InvalidDataException>(() => store.AppendPhotoAsync(id,
        new(start, 35.68, 139.76, "bad.png"), Jpeg()), "Reject non-JPEG filename");
    await Reject<InvalidDataException>(() => store.AppendPhotoAsync(id,
        new(start, 35.68, 139.76, "bad.jpg"), new MemoryStream([1, 2, 3])), "Reject non-JPEG content");
    Check(!File.Exists(Path.Combine(folder, "bad.jpg.tmp")), "Remove failed photo temporary file");
    var fileRecovered = Engine(store);
    await fileRecovered.InitializeAsync();
    Check(fileRecovered.Current!.Points.Count == 3 && fileRecovered.Current.Photos.Count == 1,
        "Reload actual NDJSON and photos");
    await fileEngine.FinishAsync();
    Check(await store.LoadActiveAsync() is null, "Completed record not recovered as active");
    var gpx = await store.ExportAsync(id, false);
    Check((await TrackImporter.ReadAsync(gpx)).Points.Count == 3, "GPX accepted by Windows importer");
    var zipped = await store.ExportAsync(id, true);
    var extracted = Path.Combine(root, "extracted");
    ZipFile.ExtractToDirectory(zipped, extracted);
    var imported = await TrackImporter.ReadAsync(Path.Combine(extracted, "track.ndjson"));
    Check(imported.Points.SequenceEqual(fileEngine.Current!.Points), "ZIP preserves timestamps, speed, altitude, segments");
    Check(imported.Photos.SequenceEqual(fileEngine.Current.Photos), "ZIP preserves photos and observations");
    var archive = new ArchiveStore(Path.Combine(root, "windows-archive"));
    var windowsWalk = await archive.ImportAsync(Path.Combine(extracted, "track.ndjson"));
    Check((await archive.LoadAsync()).Single().Photos.Count == 1 &&
          File.Exists(Path.Combine(archive.SessionFolder(windowsWalk), windowsWalk.Photos[0].Image)),
        "Windows archive imports actual Android ZIP data and JPEG");
    var secondZip = await store.ExportAsync(id, true);
    Check(secondZip == zipped && new FileInfo(zipped).Length > 0, "Repeated export atomically replaces ZIP");
    await Reject<InvalidDataException>(() => store.ExportAsync("..\\escape", false), "Reject unsafe record ID");
    var photoFile = Path.Combine(folder, fileEngine.Current.Photos[0].Image);
    File.Delete(photoFile);
    await Reject<InvalidDataException>(() => store.ListAsync(), "Surface missing JPEG on recovery");
    await File.WriteAllBytesAsync(photoFile, [0xff, 0xd8, 0xff, 0xd9]);
    await File.AppendAllTextAsync(Path.Combine(folder, "track.ndjson"), "{\"time\":");
    await Reject<InvalidDataException>(() => store.ListAsync(), "Surface torn NDJSON without silently discarding data");
    await File.WriteAllTextAsync(Path.Combine(folder, "track.ndjson"),
        JsonSerializer.Serialize(Point(0), Json.Options).Replace("\n", "").Replace("\r", "") + "\n" +
        JsonSerializer.Serialize(Point(0), Json.Options).Replace("\n", "").Replace("\r", "") + "\n");
    await Reject<InvalidDataException>(() => store.ListAsync(), "Reject duplicate times in restored file");
    await File.WriteAllTextAsync(Path.Combine(folder, "track.ndjson"), "");
    var secondId = Guid.NewGuid().ToString("N");
    await store.SaveInfoAsync(new(id, start, 5, RecordingPhase.Paused));
    await store.SaveInfoAsync(new(secondId, start.AddHours(1), 10, RecordingPhase.Paused));
    await Reject<InvalidDataException>(() => store.LoadActiveAsync(), "Reject multiple active records");
    Check((await store.ListAsync())[0].Info.Id == secondId, "History sorted newest first");
    await File.WriteAllTextAsync(Path.Combine(folder, "recording.json"), "null");
    await Reject<InvalidDataException>(() => store.ListAsync(), "Reject null recording metadata");
    await store.SaveInfoAsync(new(id, default, 7, RecordingPhase.Paused));
    await Reject<InvalidDataException>(() => store.ListAsync(), "Reject invalid recording metadata");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}
Console.WriteLine($"{checks} recording checks passed.");

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class MemoryStore : IRecordingStore
{
    private RecordingInfo? info;
    private readonly List<TrackPoint> points = [];
    private readonly List<PhotoRecord> photos = [];
    public bool FailSave { get; set; }
    public bool FailAppend { get; set; }
    public Task<RecordedWalk?> LoadActiveAsync() => Task.FromResult<RecordedWalk?>(
        info is null || info.Phase == RecordingPhase.Finished ? null : new(info, points.ToArray(), photos.ToArray()));
    public async Task<IReadOnlyList<RecordedWalk>> ListAsync() =>
        await LoadActiveAsync() is { } current ? [current] : [];
    public Task SaveInfoAsync(RecordingInfo value)
    {
        if (FailSave) throw new IOException("disk full");
        if (info?.Id != value.Id) { points.Clear(); photos.Clear(); }
        info = value;
        return Task.CompletedTask;
    }
    public Task AppendPointAsync(string id, TrackPoint point)
    {
        if (FailAppend) throw new IOException("disk full");
        points.Add(point);
        return Task.CompletedTask;
    }
    public Task AppendPhotoAsync(string id, PhotoRecord photo, Stream jpeg)
    {
        photos.Add(photo);
        return Task.CompletedTask;
    }
    public Task<string> ExportAsync(string id, bool includePhotos) => throw new NotSupportedException();
}
