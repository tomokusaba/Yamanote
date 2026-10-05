using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using WalkLogger.Core;
using WalkLogger.Application;

namespace WalkLogger.App;

public sealed record BleDeviceInfo(ulong Address, string Name)
{
    public override string ToString() => $"{Name}  ({Address:X12})";
}

public sealed class BleClient : IBleTransferSession
{
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattCharacteristic? command;
    private GattCharacteristic? meta;
    private GattCharacteristic? data;

    public static async Task<List<BleDeviceInfo>> ScanAsync(CancellationToken ct)
    {
        ConcurrentDictionary<ulong, BleDeviceInfo> devices = new();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        BluetoothError? stoppedError = null;
        watcher.Received += (_, e) =>
        {
            if (e.Advertisement.ServiceUuids.Contains(BleProtocol.ServiceId))
                devices[e.BluetoothAddress] = new(e.BluetoothAddress,
                    string.IsNullOrWhiteSpace(e.Advertisement.LocalName) ? "WalkLogger-CoreS3" : e.Advertisement.LocalName);
        };
        watcher.Stopped += (_, e) => stoppedError = e.Error;
        try
        {
            watcher.Start();
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            if (stoppedError.HasValue && stoppedError != BluetoothError.Success)
                throw new IOException($"BLE検索に失敗しました: {stoppedError}。Bluetoothをオンにしてください。");
            return devices.Values.OrderBy(d => d.Address).ToList();
        }
        finally { watcher.Stop(); }
    }

    public async Task ConnectAsync(ulong address, CancellationToken ct)
    {
        device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(20), ct) ??
                 throw new IOException("BLE機器に接続できません。CoreS3のBLE ONとPCのBluetoothを確認してください。");
        var services = await device.GetGattServicesForUuidAsync(BleProtocol.ServiceId, BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
        Ensure(services.Status, "サービス取得");
        service = services.Services.SingleOrDefault() ?? throw new IOException("WalkLoggerのBLEサービスがありません。");
        command = await FindAsync(BleProtocol.CommandId, ct);
        meta = await FindAsync(BleProtocol.MetaId, ct);
        data = await FindAsync(BleProtocol.DataId, ct);
    }

    private async Task<GattCharacteristic> FindAsync(Guid id, CancellationToken ct)
    {
        var result = await service!.GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
        Ensure(result.Status, "特性取得");
        return result.Characteristics.SingleOrDefault() ?? throw new IOException("必要なBLE特性がありません。");
    }

    private async Task CommandAsync(string value, CancellationToken ct)
    {
        using var writer = new DataWriter();
        writer.WriteBytes(Encoding.ASCII.GetBytes(value));
        var result = await command!.WriteValueWithResultAsync(writer.DetachBuffer(), GattWriteOption.WriteWithResponse).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
        Ensure(result.Status, "コマンド送信");
    }

    private static async Task<byte[]> ReadAsync(GattCharacteristic characteristic, CancellationToken ct)
    {
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).WaitAsync(TimeSpan.FromSeconds(20), ct);
        Ensure(result.Status, "データ読込");
        using var reader = DataReader.FromBuffer(result.Value);
        byte[] bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task<TransferMetadata> OpenAsync(string value, CancellationToken ct)
    {
        await CommandAsync(value, ct);
        var info = JsonSerializer.Deserialize<TransferMetadata>(await ReadAsync(meta!, ct), Json.Options) ??
                   throw new InvalidDataException("BLE転送情報が空です。");
        if (!info.Ok) throw new IOException("CoreS3: " + info.Error);
        if (info.Version != 1 || info.Size > BleProtocol.MaxFileBytes)
            throw new InvalidDataException("未対応のBLE仕様、または32MBを超えるファイルです。");
        return info;
    }

    public async Task<List<RemoteFile>> CatalogAsync(CancellationToken ct)
    {
        var info = await OpenAsync("CAT", ct);
        if (info.Size > 512 * 1024) throw new InvalidDataException("BLE一覧が512KBを超えています。SD取り込みを使ってください。");
        using var stream = new MemoryStream();
        await ReceiveAsync(stream, info, null, ct);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var files = lines.Select(l => JsonSerializer.Deserialize<RemoteFile>(l, Json.Options) ??
                                     throw new InvalidDataException("BLEファイル一覧が不正です。")).ToList();
        if (files.Count > 4096 || files.Select(f => f.Id).Distinct().Count() != files.Count)
            throw new InvalidDataException("BLEファイル一覧の件数またはIDが不正です。");
        foreach (var file in files) BleProtocol.ValidatePath(file);
        return files;
    }

    public async Task DownloadAsync(RemoteFile file, string root, IProgress<string> progress, CancellationToken ct)
    {
        var path = Path.Combine(Path.GetFullPath(root), BleProtocol.ValidatePath(file));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var info = await OpenAsync($"GET {file.Id}", ct);
        if (info.Size != file.Size) throw new InvalidDataException("記録が転送中に変更されました。BLEを再検索してください。");
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct);
            if (existing.Length == info.Size && Crc32.Compute(existing) == info.Crc32) return;
        }
        var partial = path + ".part";
        await using (var stream = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            if (stream.Length > info.Size) stream.SetLength(0);
            stream.Position = stream.Length;
            await ReceiveAsync(stream, info, new Progress<uint>(p => progress.Report($"{file.Path}  {p:N0} / {info.Size:N0} bytes")), ct);
        }
        File.Move(partial, path, true);
    }

    private Task ReceiveAsync(Stream target, TransferMetadata info, IProgress<uint>? progress, CancellationToken ct) =>
        BleProtocol.ReceiveAsync(target, info, async (offset, token) =>
    {
        await CommandAsync($"READ {offset}", token);
        var status = JsonSerializer.Deserialize<TransferMetadata>(await ReadAsync(meta!, token), Json.Options) ??
                     throw new InvalidDataException("BLE読込情報が空です。");
        if (!status.Ok) throw new IOException("CoreS3: " + status.Error);
        return await ReadAsync(data!, token);
    }, progress, ct);

    private static void Ensure(GattCommunicationStatus status, string operation)
    {
        if (status != GattCommunicationStatus.Success)
            throw new IOException($"BLE {operation}: {status}。接続とCoreS3のBLE ONを確認して再試行してください。");
    }

    public void Dispose()
    {
        service?.Dispose();
        device?.Dispose();
    }
}
