using Android.Content;
using Android.Locations;
using System.Runtime.Versioning;
using WalkLogger.Recording;

namespace WalkLogger.Mobile;

public sealed class AndroidGpsControl(RecordingEngine engine)
{
    public event Action<string>? StatusChanged;
    public bool StartRequested { get; private set; }
    internal void Status(string message) => StatusChanged?.Invoke(message);
    internal void Started() => StartRequested = false;

    public async Task CheckPermissionsAsync()
    {
        if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            throw new InvalidOperationException("正確な位置情報を許可してください。Androidのアプリ設定から変更できます。");
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            await Permissions.RequestAsync<NotificationPermission>() != PermissionStatus.Granted)
            throw new InvalidOperationException("画面消灯中の記録状態を表示するため、通知を許可してください。");
        var manager = (LocationManager?)Android.App.Application.Context.GetSystemService(Context.LocationService);
        if (manager is null || !manager.IsProviderEnabled(LocationManager.GpsProvider))
            throw new InvalidOperationException("GPSが無効です。Androidの位置情報をオンにして、屋外で再度開始してください。");
    }
    public void Start()
    {
        Status("GPSを待っています。測位は屋外で行ってください。");
        var context = Android.App.Application.Context;
        StartRequested = true;
        try { context.StartForegroundService(new Intent(context, typeof(GpsRecordingService))); }
        catch { StartRequested = false; throw; }
    }
    public async Task PauseAsync()
    {
        await engine.PauseAsync();
        Stop();
        Status("一時停止中。再開時は新しいGPS区間として保存します。");
    }
    public async Task FinishAsync()
    {
        await engine.FinishAsync();
        Stop();
        Status("GPS記録を終了しました。");
    }
    private static void Stop()
    {
        var context = Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(GpsRecordingService)));
    }

    [SupportedOSPlatform("android33.0")]
    private sealed class NotificationPermission : Permissions.BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            [(Android.Manifest.Permission.PostNotifications, true)];
    }
}
