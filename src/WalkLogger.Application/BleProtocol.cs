using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace WalkLogger.Core;

public sealed record RemoteFile(int Id, string Path, uint Size);
public sealed record TransferMetadata(bool Ok, int Version, uint Size, uint Crc32, string? Error);

public static partial class BleProtocol
{
    public static readonly Guid ServiceId = Guid.Parse("af736e10-70c0-4d31-a213-8d82b0047100");
    public static readonly Guid CommandId = Guid.Parse("af736e11-70c0-4d31-a213-8d82b0047100");
    public static readonly Guid MetaId = Guid.Parse("af736e12-70c0-4d31-a213-8d82b0047100");
    public static readonly Guid DataId = Guid.Parse("af736e13-70c0-4d31-a213-8d82b0047100");
    public const uint MaxFileBytes = 32 * 1024 * 1024;

    [GeneratedRegex(@"^\d{8}T\d{6}Z$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionPattern();
    [GeneratedRegex(@"^IMG_[A-Za-z0-9_]+\.jpg$", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    public static string ValidatePath(RemoteFile file)
    {
        var parts = file.Path.Split('/');
        if (parts.Length != 2 || !SessionPattern().IsMatch(parts[0]) ||
            !(parts[1] is "track.ndjson" or "photos.ndjson" || ImagePattern().IsMatch(parts[1])) ||
            file.Size > MaxFileBytes || file.Id < 0)
            throw new InvalidDataException("BLEファイル名・サイズが不正です。");
        return Path.Combine(parts[0], parts[1]);
    }

    public static async Task ReceiveAsync(Stream target, TransferMetadata info,
        Func<uint, CancellationToken, Task<byte[]>> readPacket, IProgress<uint>? progress, CancellationToken ct)
    {
        if (!info.Ok || info.Version != 1 || info.Size > MaxFileBytes ||
            target.Length > info.Size || target.Position != target.Length)
            throw new InvalidDataException("BLE仕様・ファイルサイズ・再開位置が不正です。");
        uint offset = checked((uint)target.Position);
        var lastReport = DateTime.UtcNow;
        while (offset < info.Size)
        {
            ct.ThrowIfCancellationRequested();
            var packet = await readPacket(offset, ct);
            if (packet.Length <= 4 || packet.Length > 184 ||
                BinaryPrimitives.ReadUInt32LittleEndian(packet) != offset || packet.Length - 4 > info.Size - offset)
                throw new InvalidDataException("BLE受信位置・データ長が一致しません。再転送すると続きから読み込みます。");
            await target.WriteAsync(packet.AsMemory(4), ct);
            offset += (uint)packet.Length - 4;
            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 250)
            {
                progress?.Report(offset);
                lastReport = DateTime.UtcNow;
            }
        }
        await target.FlushAsync(ct);
        target.Position = 0;
        using var copy = new MemoryStream();
        await target.CopyToAsync(copy, ct);
        if (target.Length != info.Size || Crc32.Compute(copy.GetBuffer().AsSpan(0, checked((int)copy.Length))) != info.Crc32)
        {
            target.SetLength(0);
            throw new InvalidDataException("CRC検証に失敗しました。受信済みデータを破棄しました。再転送してください。");
        }
        progress?.Report(offset);
    }
}
