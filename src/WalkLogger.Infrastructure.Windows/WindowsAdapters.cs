using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using WalkLogger.App;
using WalkLogger.Application;

namespace WalkLogger.Infrastructure.Windows;

public sealed class WindowsSettingsStore : ISettingsStore
{
    public WindowsSettingsStore() : this(SettingsStore.LocalRoot) { }
    public WindowsSettingsStore(string localRoot) => LocalRoot = Path.GetFullPath(localRoot);
    public string LocalRoot { get; }
    public async Task<SettingsData> LoadAsync() => await SettingsStore.LoadAsync(LocalRoot);
    public string LoadKey() => SettingsStore.LoadKey(LocalRoot);
    public Task SaveAsync(SettingsData settings, string key) => SettingsStore.SaveAsync(LocalRoot, new AppSettings
    {
        ArchiveRoot = settings.ArchiveRoot,
        AzureEndpoint = settings.AzureEndpoint,
        AzureDeployment = settings.AzureDeployment,
        NominatimContact = settings.NominatimContact
    }, key);
}

public sealed class WindowsBleTransferFactory : IBleTransferFactory
{
    public async Task<List<DeviceInfo>> ScanAsync(CancellationToken ct) =>
        (await BleClient.ScanAsync(ct)).Select(d => new DeviceInfo(d.Address, d.Name)).ToList();
    public IBleTransferSession Create() => new BleClient();
}

public sealed class PhotoMetadataReader : IPhotoMetadataReader
{
    public DateTime? ReadCaptureTime(string path)
    {
        using var input = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad);
        if (decoder.Frames[0].Metadata is BitmapMetadata metadata)
        {
            var date = metadata.GetQuery("/app1/ifd/exif/{ushort=36867}") as string;
            if (DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
                return value;
        }
        return null;
    }
}
