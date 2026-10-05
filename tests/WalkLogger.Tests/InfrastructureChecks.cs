using System.Net;
using System.Text;
using System.Text.Json;
using WalkLogger.Core;
using WalkLogger.Infrastructure;
using WalkLogger.Application;
using System.Buffers.Binary;

static class InfrastructureChecks
{
    public static async Task RunAsync(string folder, Action<bool, string> check)
    {
        var root = Path.Combine(folder, "infrastructure");
        var files = new WorkspaceFiles();
        files.CreateDirectory(root);
        check(files.DirectoryExists(root), "WorkspaceFiles creates and detects directories");
        var route = Path.Combine(root, "route.gpx");
        var walk = new WalkSession
        {
            Points = [new(DateTimeOffset.Parse("2026-10-05T02:00:00Z"), 35, 139)]
        };
        var exporter = new FileExporter();
        var compact = new JsonSerializerOptions(Json.Options) { WriteIndented = false };
        await exporter.WriteTextAsync(route, exporter.ToGpx(walk));
        check(files.FindImportFiles(root).SequenceEqual(new[] { route }), "Import discovery falls back to GPX");
        var track = Path.Combine(root, "track.ndjson");
        await exporter.WriteTextAsync(track, JsonSerializer.Serialize(walk.Points[0], compact));
        check(files.FindImportFiles(root).SequenceEqual(new[] { track }), "Import discovery prefers NDJSON over GPX");
        var image = Path.Combine(root, "original.jpg");
        await exporter.WriteBytesAsync(image, [1, 2, 3]);
        var copied = files.CopyPhoto(image, root);
        check(copied.StartsWith("IMG_") && File.ReadAllBytes(Path.Combine(root, copied)).SequenceEqual(new byte[] { 1, 2, 3 }),
            "Photo copy assigns a new archive name and preserves bytes");
        await files.AppendDiagnosticsAsync(root, "diagnostic");
        check(await File.ReadAllTextAsync(Path.Combine(root, "diagnostics.log")) == "diagnostic", "Filesystem diagnostic appends text");
        var store = new ArchiveStoreFactory().Create(Path.Combine(root, "archive"));
        await exporter.WriteTextAsync(Path.Combine(root, "photos.ndjson"), JsonSerializer.Serialize(
            new PhotoRecord(walk.Points[0].Time, 35, 139, copied), compact));
        var imported = await store.ImportAsync(track);
        check((await store.LoadAsync()).Count == 1 && File.Exists(Path.Combine(store.SessionFolder(imported), copied)),
            "Archive import copies photos and validates their persisted metadata");
        await Throws(() => store.SessionFolder(new() { Id = "invalid" }), check, "Invalid archive ID is rejected");
        var persisted = Path.Combine(store.SessionFolder(imported), "walk.json");
        var valid = await File.ReadAllTextAsync(persisted);
        foreach (var (bad, label) in new[]
        {
            ("null", "Null archive record is rejected"),
            ("""{"id":"00000000000000000000000000000000","points":[]}""", "Empty archive is rejected"),
            (valid.Replace(copied, "missing.jpg"), "Missing archived photo is rejected")
        })
        {
            await File.WriteAllTextAsync(persisted, bad);
            await Reject(() => store.LoadAsync(), check, label);
        }
        await File.WriteAllTextAsync(persisted, valid);
        await File.WriteAllTextAsync(track, "\nnull\n");
        await Reject(() => TrackImporter.ReadAsync(track), check, "Null NDJSON row includes its line number");
        await File.WriteAllTextAsync(track, "\n");
        await Reject(() => TrackImporter.ReadAsync(track), check, "Empty NDJSON is rejected");
        await Reject(() => TrackImporter.ReadAsync(Path.Combine(root, "absent.gpx")), check, "Missing import is rejected");
        File.Delete(Path.Combine(root, "photos.ndjson"));
        foreach (var (gpx, label) in new[]
        {
            ("<notgpx/>", "Non-GPX XML is rejected"),
            ("<gpx><trk><trkseg><trkpt lat='35' lon='139'/></trkseg></trk></gpx>", "Missing GPX time is rejected"),
            ("<gpx><trk><trkseg><trkpt lat='bad' lon='139'><time>2026-10-05T02:00:00Z</time></trkpt></trkseg></trk></gpx>", "Malformed GPX coordinate is rejected"),
            ("<gpx><trk><trkseg><trkpt lat='35' lon='139'><time>2026-10-05T02:00:00Z</time><ele>bad</ele></trkpt></trkseg></trk></gpx>", "Malformed GPX altitude is rejected")
        })
        {
            await File.WriteAllTextAsync(route, gpx);
            await Reject(() => TrackImporter.ReadAsync(route), check, label);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var atomic = Path.Combine(root, "atomic.txt");
        try { await ArchiveStore.AtomicWriteAsync(atomic, "cancelled", cancellation.Token); }
        catch (OperationCanceledException) { }
        check(!Directory.GetFiles(root, "atomic.txt.*.tmp").Any(), "Cancelled atomic write leaves no temporary file");

        var cache = Path.Combine(root, "cache", "places.json");
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, """{"display_name":"test place"}"""));
        var placeService = new PlaceService(http, cache);
        var places = await placeService.ResolveAsync(walk, "contact@example.com", default);
        check(places.Count == 1 && places[0].Name == "test place", "Nominatim resolves and persists a cache with fake HTTP");
        var distant = new WalkSession { Points = Enumerable.Range(0, 12).Select(i => walk.Points[0] with { Lat = 35 + i * .01 }).ToList() };
        var keys = distant.Points.ToDictionary(p => FormattableString.Invariant($"{p.Lat:F4},{p.Lon:F4}"), _ => "same place");
        await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(keys, Json.Options));
        check((await placeService.ResolveAsync(distant, "contact@example.com", default)).Count == 1,
            "Nominatim samples up to twelve points and collapses consecutive identical names");
        await Reject(() => placeService.ResolveAsync(walk, "invalid contact", default), check, "Nominatim rejects an invalid contact");
        await File.WriteAllTextAsync(cache, "null");
        await Reject(() => placeService.ResolveAsync(walk, "contact@example.com", default), check, "Null place cache is rejected");
        File.Delete(cache);
        foreach (var (status, body, label) in new[]
        {
            (HttpStatusCode.TooManyRequests, "{}", "Nominatim HTTP failure is reported"),
            (HttpStatusCode.OK, "{}", "Missing place display name is rejected"),
            (HttpStatusCode.OK, """{"display_name":null}""", "Null place display name is rejected")
        })
        {
            using var failedHttp = new HttpClient(new ResponseHandler(status, body));
            await Reject(() => new PlaceService(failedHttp, cache).ResolveAsync(walk, "contact@example.com", default), check, label);
        }
        using var azureHttp = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            """{"choices":[{"finish_reason":"stop","message":{"content":"photo draft"}}]}"""));
        var blogs = new AzureBlogService(azureHttp);
        walk.Photos = [new(walk.Points[0].Time, 35, 139, copied, "observed")];
        check(await blogs.GenerateAsync("https://example.services.ai.azure.com/", "deployment", "fake-key", walk,
                distant, root, true, default) == "photo draft", "Azure embeds opted-in photo bytes and comparison with fake HTTP");
        await Reject(() => blogs.GenerateAsync("https://example.openai.azure.com/", "", "", walk, null, root, false, default),
            check, "Azure requires deployment and key");
        walk.Photos = Enumerable.Repeat(walk.Photos[0], 9).ToList();
        await Reject(() => blogs.GenerateAsync("https://example.openai.azure.com/", "d", "k", walk, null, root, true, default),
            check, "Azure rejects more than eight opted-in photos");
        walk.Photos = [new(walk.Points[0].Time, 35, 139, "..\\secret.jpg")];
        await Reject(() => blogs.GenerateAsync("https://example.openai.azure.com/", "d", "k", walk, null, root, true, default),
            check, "Azure rejects a photo path containing directories");
        var large = Path.Combine(root, "large.jpg");
        await using (var stream = File.Create(large)) stream.SetLength(4 * 1024 * 1024 + 1);
        walk.Photos = [new(walk.Points[0].Time, 35, 139, "large.jpg")];
        await Reject(() => blogs.GenerateAsync("https://example.openai.azure.com/", "d", "k", walk, null, root, true, default),
            check, "Azure rejects an image above four MB");
        foreach (var (status, body, label) in new[]
        {
            (HttpStatusCode.Unauthorized, "{}", "Azure HTTP error is reported"),
            (HttpStatusCode.OK, """{"choices":[{"finish_reason":"stop","message":{"content":null}}]}""", "Null Azure completion is rejected")
        })
        {
            using var failedHttp = new HttpClient(new ResponseHandler(status, body));
            await Reject(() => new AzureBlogService(failedHttp).GenerateAsync("https://example.openai.azure.com/", "d", "k",
                walk, null, root, false, default), check, label);
        }
        check(new WalkSession().Stats.PointCount == 0 && new WalkSession().DisplayDate == "" &&
              new TrackPoint(walk.Points[0].Time, 35, 139).Speed == 0, "Empty model presentation and default point speed");
        check(((IBlogService)blogs).ValidateEndpoint("https://example.openai.azure.com/").Scheme == "https",
            "Azure endpoint validation is also exposed through its application port");
        walk.Places = places;
        check(WalkAnalysis.Summary(walk).Contains("test place"), "Walk summary includes resolved place labels");
        using var received = new MemoryStream();
        var info = new TransferMetadata(true, 1, 1, Crc32.Compute(new byte[] { 42 }), null);
        var reports = new List<uint>();
        await BleProtocol.ReceiveAsync(received, info, async (offset, ct) =>
        {
            await Task.Delay(300, ct);
            var packet = new byte[5];
            BinaryPrimitives.WriteUInt32LittleEndian(packet, offset);
            packet[4] = 42;
            return packet;
        }, new CaptureProgress<uint>(reports), default);
        check(reports.Count == 2 && reports.All(p => p == 1), "BLE reports throttled progress and final completion");
        await Reject(() => BleProtocol.ReceiveAsync(new MemoryStream(), info with { Ok = false },
            (_, _) => throw new InvalidOperationException("Packet must not be read"), null, default),
            check, "Invalid BLE transfer metadata is rejected before reading packets");
    }

    private static Task Throws(Action action, Action<bool, string> check, string label) =>
        Reject(() => { action(); return Task.CompletedTask; }, check, label);

    private static async Task Reject(Func<Task> action, Action<bool, string> check, string label)
    {
        try { await action(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException or
                                   InvalidOperationException or FileNotFoundException or HttpRequestException)
        {
            check(true, label);
            return;
        }
        throw new Exception("FAIL: " + label);
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
    private sealed class CaptureProgress<T>(List<T> values) : IProgress<T>
    {
        public void Report(T value) => values.Add(value);
    }
}
