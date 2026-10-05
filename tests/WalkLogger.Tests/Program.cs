using System.Globalization;
using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using WalkLogger.Core;

var folder = Path.Combine(Path.GetTempPath(), "WalkLogger-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var passed = 0;
void Check(bool ok, string label)
{
    if (!ok) throw new Exception("FAIL: " + label);
    Console.WriteLine("PASS: " + label);
    passed++;
}
async Task Reject(Func<Task> action, string label)
{
    try { await action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException or InvalidOperationException)
    {
        Check(true, label);
        return;
    }
    throw new Exception("FAIL: accepted " + label);
}
try
{
    var start = DateTimeOffset.Parse("2026-10-05T02:00:00Z", CultureInfo.InvariantCulture);
    var walk = new WalkSession
    {
        Title = "テスト",
        Points = [new(start, 35, 139), new(start.AddSeconds(10), 35.0001, 139),
            new(start.AddHours(1), 36, 140), new(start.AddHours(1).AddSeconds(10), 36.0001, 140)]
    };
    var stats = walk.Stats;
    Check(stats.DistanceKm is > 0.022 and < 0.0224, "Haversine distance excludes gaps");
    Check(stats.Recorded.TotalSeconds == 20 && stats.SegmentCount == 2, "Gap segmentation and recorded time");
    Check(stats.Elapsed.TotalSeconds == 3610, "Elapsed time includes gaps");
    var gpx = Path.Combine(folder, "test.gpx");
    walk.Points[0] = walk.Points[0] with { Altitude = 18.5 };
    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
    TrackImporter.ToGpx(walk).Save(gpx);
    var imported = await TrackImporter.ReadAsync(gpx);
    Check(imported.Points.Count == 4 && imported.Points[0].Altitude == 18.5, "GPX invariant-culture roundtrip and altitude");
    Check(imported.Stats.SegmentCount == 2 && imported.Points[0].Time.Offset == TimeSpan.Zero, "GPX UTC and segments");
    var log = Path.Combine(folder, "track.ndjson");
    await File.WriteAllTextAsync(log, string.Join("\n", walk.Points.Select(p => JsonSerializer.Serialize(p, Json.Options).Replace("\n", "").Replace("\r", ""))));
    var store = new ArchiveStore(Path.Combine(folder, "archive"));
    var first = await store.ImportAsync(log);
    first.Notes = "実際の観察";
    await store.SaveAsync(first);
    var second = await store.ImportAsync(log);
    Check(first.Id == second.Id && second.Notes == first.Notes, "Reimport is idempotent and preserves notes");
    Check(File.Exists(Path.Combine(store.SessionFolder(first), "route.gpx")), "GPX persisted in archive");
    var bad = Path.Combine(folder, "bad.ndjson");
    await File.WriteAllTextAsync(bad, """{"time":"2026-10-05T02:00:00","lat":35,"lon":139}""");
    await Reject(async () => { await TrackImporter.ReadAsync(bad); }, "Timezone-less timestamp rejected");
    await File.WriteAllTextAsync(bad, """{"time":"2026-10-05T02:00:00Z","lon":139}""");
    await Reject(async () => { await TrackImporter.ReadAsync(bad); }, "Missing coordinates rejected");
    await File.WriteAllTextAsync(bad, """{"time":"2026-10-05T02:00:00Z","lat":95,"lon":139}""");
    await Reject(async () => { await TrackImporter.ReadAsync(bad); }, "Invalid coordinates rejected");
    await File.WriteAllTextAsync(bad, """{"time":""");
    await Reject(async () => { await TrackImporter.ReadAsync(bad); }, "Truncated SD row explicitly rejected");
    await File.WriteAllTextAsync(bad, File.ReadAllText(log) + "\n" + JsonSerializer.Serialize(walk.Points[0], Json.Options).Replace("\n", "").Replace("\r", ""));
    await Reject(async () => { await TrackImporter.ReadAsync(bad); }, "Nonmonotonic times rejected");
    await File.WriteAllTextAsync(Path.Combine(folder, "photos.ndjson"), """{"time":"2026-10-05T02:00:00Z","lat":35,"lon":139,"image":"../secret.jpg"}""");
    await Reject(async () => { await TrackImporter.ReadAsync(log); }, "Photo traversal rejected");
    File.Delete(Path.Combine(folder, "photos.ndjson"));
    Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926, "CRC32 matches firmware standard");
    var payload = Enumerable.Range(0, 725).Select(i => (byte)(i % 251)).ToArray();
    var info = new TransferMetadata(true, 1, (uint)payload.Length, Crc32.Compute(payload), null);
    Task<byte[]> Packet(uint offset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var length = Math.Min(16, payload.Length - (int)offset);
        var packet = new byte[4 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, offset);
        payload.AsSpan((int)offset, length).CopyTo(packet.AsSpan(4));
        return Task.FromResult(packet);
    }
    using (var receiver = new MemoryStream())
    {
        await BleProtocol.ReceiveAsync(receiver, info, Packet, null, default);
        Check(receiver.ToArray().SequenceEqual(payload), "BLE default-MTU transfer and CRC validation");
    }
    using (var receiver = new MemoryStream())
    {
        receiver.Write(payload.AsSpan(0, 71));
        var offsets = new List<uint>();
        await BleProtocol.ReceiveAsync(receiver, info, async (offset, ct) =>
        {
            offsets.Add(offset);
            return await Packet(offset, ct);
        }, null, default);
        Check(offsets[0] == 71 && receiver.ToArray().SequenceEqual(payload), "BLE resumes exact byte offset");
    }
    using (var receiver = new MemoryStream())
    {
        await Reject(() => BleProtocol.ReceiveAsync(receiver, info, (offset, ct) => Packet(offset + 1, ct), null, default),
            "BLE mismatched offset rejected");
    }
    using (var receiver = new MemoryStream())
    {
        await Reject(() => BleProtocol.ReceiveAsync(receiver, info with { Crc32 = 0 }, Packet, null, default),
            "BLE damaged file rejected");
        Check(receiver.Length == 0, "BLE CRC failure discards corrupt partial data");
    }
    using (var receiver = new MemoryStream())
    using (var cancellation = new CancellationTokenSource())
    {
        try
        {
            await BleProtocol.ReceiveAsync(receiver, info, async (offset, ct) =>
            {
                if (offset >= 32) cancellation.Cancel();
                return await Packet(offset, ct);
            }, null, cancellation.Token);
            throw new Exception("Expected cancellation");
        }
        catch (OperationCanceledException)
        {
            Check(receiver.Length == 32, "BLE cancellation preserves completed chunks");
        }
    }
    Check(BleProtocol.ValidatePath(new(0, "20261005T020000Z/track.ndjson", 50)).EndsWith("track.ndjson"),
        "BLE catalog valid path");
    await Reject(() => { BleProtocol.ValidatePath(new(0, "../secret.jpg", 1)); return Task.CompletedTask; }, "BLE catalog traversal rejected");
    await Reject(() => { BleProtocol.ValidatePath(new(0, "CON/track.ndjson", 1)); return Task.CompletedTask; }, "BLE reserved device path rejected");
    await Reject(() => { BleProtocol.ValidatePath(new(0, "20261005T020000Z/IMG_x.jpg", BleProtocol.MaxFileBytes + 1)); return Task.CompletedTask; },
        "BLE oversized file rejected");
    var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var protocolHeader = await File.ReadAllTextAsync(Path.Combine(repo, "firmware", "include", "protocol.h"));
    Check(new[] { BleProtocol.ServiceId, BleProtocol.CommandId, BleProtocol.MetaId, BleProtocol.DataId }
        .All(id => protocolHeader.Contains(id.ToString(), StringComparison.Ordinal)), "BLE UUID contract matches firmware");
    await ArchitectureChecks.RunAsync(repo, Check);
    await WorkspaceChecks.RunAsync(Check);
    await InfrastructureChecks.RunAsync(folder, Check);
    using (var persisted = JsonDocument.Parse(JsonSerializer.Serialize(first, Json.Options)))
    {
        Check(!persisted.RootElement.TryGetProperty("stats", out _) &&
              !persisted.RootElement.TryGetProperty("displayTitle", out _) &&
              persisted.RootElement.TryGetProperty("sourceHash", out _),
            "Persistence JSON contract preserved outside Domain");
    }
    Check(!WalkAnalysis.Connected(walk.Points[0], walk.Points[1] with { Segment = 1 }), "Manual pause splits segments");
    Check(!WalkAnalysis.Connected(walk.Points[0], walk.Points[1] with { Lat = 38 }), "GPS spikes do not add distance");
    first.CompletedLoop = true;
    var sample = new WalkSession { IsDemo = true, Points = first.Points, CompletedLoop = true };
    Check(ArchiveStore.AnnualReport([first, sample], 2026).Contains("街歩き: 1回"), "Annual report excludes demo");
    await Reject(() => { AzureBlogService.ValidateEndpoint("http://resource.openai.azure.com"); return Task.CompletedTask; }, "Azure endpoint requires HTTPS");
    await Reject(() => { AzureBlogService.ValidateEndpoint("https://example.com"); return Task.CompletedTask; }, "Azure endpoint restricts hosts");
    using var http = new HttpClient(new FakeHandler());
    var blog = await new AzureBlogService(http).GenerateAsync("https://resource.openai.azure.com/", "test", "test",
        first, second, folder, false, default);
    Check(blog == "# 草稿", "Azure v1 request and completion response");
    using var filteredHttp = new HttpClient(new StaticHandler("""{"choices":[{"finish_reason":"content_filter","message":{"content":null}}]}"""));
    await Reject(() => new AzureBlogService(filteredHttp).GenerateAsync("https://resource.openai.azure.com/", "test", "test",
        first, null, folder, false, default), "Azure content-filter response is not success");
    using var truncatedHttp = new HttpClient(new StaticHandler("""{"choices":[{"finish_reason":"length","message":{"content":"unfinished"}}]}"""));
    await Reject(() => new AzureBlogService(truncatedHttp).GenerateAsync("https://resource.openai.azure.com/", "test", "test",
        first, null, folder, false, default), "Azure truncated response is not success");
    Console.WriteLine($"{passed} scenarios passed.");
}
finally
{
    Directory.Delete(folder, true);
}

sealed class StaticHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}

sealed class FakeHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsolutePath != "/openai/v1/chat/completions" || !request.Headers.Contains("api-key"))
            throw new Exception("Wrong Azure API request");
        var body = await request.Content!.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.GetProperty("model").GetString() != "test" || !body.Contains("max_completion_tokens"))
            throw new Exception("Wrong request body");
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"finish_reason":"stop","message":{"content":"# 草稿"}}]}""", Encoding.UTF8, "application/json")
        };
    }
}
