using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.OS;
using AndroidX.Core.App;
using WalkLogger.Core;
using WalkLogger.Recording;
using Location = Android.Locations.Location;

namespace WalkLogger.Mobile;

[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeLocation)]
public sealed class GpsRecordingService : Service
{
    private const string Channel = "walklogger_gps";
    private const int NotificationId = 7100;
    private const string PauseAction = "WalkLogger.Pause";
    private RecordingEngine engine = null!;
    private AndroidGpsControl control = null!;
    private LocationManager? manager;
    private LocationListener? listener;
    private PowerManager.WakeLock? wakeLock;
    private bool destroyed;
    private bool starting;
    private bool failed;
    public static bool IsRunning { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        var services = IPlatformApplication.Current!.Services;
        engine = services.GetRequiredService<RecordingEngine>();
        control = services.GetRequiredService<AndroidGpsControl>();
        var notifications = (NotificationManager)GetSystemService(NotificationService)!;
        notifications.CreateNotificationChannel(new NotificationChannel(Channel,
            "街歩きのGPS記録", NotificationImportance.Low));
    }
    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == PauseAction)
        {
            _ = PauseFromNotificationAsync();
            return StartCommandResult.NotSticky;
        }
        var notification = BuildNotification("GPSを待っています");
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                StartForeground(NotificationId, notification, ForegroundService.TypeLocation);
            else StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            _ = FailAsync(ex);
            return StartCommandResult.NotSticky;
        }
        IsRunning = true;
        control.Started();
        if (!starting)
        {
            starting = true;
            _ = StartGpsAsync();
        }
        return StartCommandResult.Sticky;
    }
    private async Task PauseFromNotificationAsync()
    {
        try { await control.PauseAsync(); }
        catch (Exception ex) { await FailAsync(ex); }
    }
    private async Task StartGpsAsync()
    {
        try
        {
            await engine.InitializeAsync();
            if (destroyed) return;
            if (engine.Current is not { Info.Phase: RecordingPhase.Recording } record)
            {
                StopSelf();
                return;
            }
            manager = (LocationManager?)GetSystemService(LocationService) ??
                throw new InvalidOperationException("位置情報サービスを使用できません。");
            listener = new(this);
            manager.RequestLocationUpdates(LocationManager.GpsProvider,
                record.Info.IntervalSeconds * 1000L, 0, listener, Looper.MainLooper);
            var power = (PowerManager)GetSystemService(PowerService)!;
            wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "WalkLogger:GPS");
            wakeLock!.Acquire();
        }
        catch (Exception ex) { await FailAsync(ex); }
    }
    internal async Task LocationAsync(Location location)
    {
        if (destroyed) return;
        try
        {
            var age = SystemClock.ElapsedRealtimeNanos() - location.ElapsedRealtimeNanos;
            if (age < 0 || age > 20_000_000_000)
            {
                control.Status("古い測位は保存せず、新しいGPSを待っています。");
                return;
            }
            var point = new TrackPoint(DateTimeOffset.FromUnixTimeMilliseconds(location.Time),
                location.Latitude, location.Longitude, location.HasSpeed ? location.Speed * 3.6 : 0,
                location.HasAltitude ? location.Altitude : null);
            var text = location.HasAccuracy ? $"GPS精度 ±{location.Accuracy:F0} m" : "GPS取得済み（精度不明）";
            if (await engine.RecordAsync(point))
            {
                if (destroyed || engine.Current?.Info.Phase != RecordingPhase.Recording) return;
                control.Status(text);
                var notification = BuildNotification($"{engine.Current!.Points.Count}点 / {text}");
                ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, notification);
            }
        }
        catch (Exception ex) { await FailAsync(ex); }
    }
    private async Task FailAsync(Exception error)
    {
        if (failed || destroyed) return;
        failed = true;
        System.Diagnostics.Trace.TraceError(error.ToString());
        try
        {
            if (engine.Current?.Info.Phase == RecordingPhase.Recording) await engine.PauseAsync();
        }
        catch (Exception persistenceError)
        {
            error = new AggregateException("記録と一時停止状態の保存に失敗しました。保存容量を確認してください。", error, persistenceError);
            System.Diagnostics.Trace.TraceError(persistenceError.ToString());
        }
        engine.ReportError(error);
        control.Status("記録を停止しました。アプリでエラーを確認してください。");
        var notification = BuildNotification("記録を停止しました: " + error.Message, false);
        StopForeground(StopForegroundFlags.Remove);
        ((NotificationManager)GetSystemService(NotificationService)!).Notify(NotificationId, notification);
        StopSelf();
    }
    private Notification BuildNotification(string text, bool ongoing = true)
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        var pending = PendingIntent.GetActivity(this, 0, intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(this, Channel);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_walklogger);
        builder.SetContentTitle(ongoing ? "WalkLogger 記録中" : "WalkLogger 記録エラー");
        builder.SetContentText(text);
        builder.SetContentIntent(pending);
        builder.SetOngoing(ongoing);
        builder.SetOnlyAlertOnce(true);
        if (ongoing)
        {
            var pauseIntent = new Intent(this, typeof(GpsRecordingService)).SetAction(PauseAction);
            var pause = PendingIntent.GetService(this, 1, pauseIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            builder.AddAction(0, "一時停止", pause);
        }
        return builder.Build() ?? throw new InvalidOperationException("GPS記録通知を作成できません。");
    }
    public override void OnDestroy()
    {
        destroyed = true;
        IsRunning = false;
        if (listener is not null)
        {
            manager?.RemoveUpdates(listener);
            listener.Dispose();
        }
        if (wakeLock?.IsHeld == true) wakeLock.Release();
        wakeLock?.Dispose();
        if (!failed) StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }
    private sealed class LocationListener(GpsRecordingService service) : Java.Lang.Object, ILocationListener
    {
        public async void OnLocationChanged(Location location) => await service.LocationAsync(location);
        public void OnProviderDisabled(string? provider) =>
            service.control.Status("GPSが無効になりました。位置情報をオンにしてください。");
        public void OnProviderEnabled(string? provider) => service.control.Status("GPSを待っています。");
        public void OnStatusChanged(string? provider, Availability status, Bundle? extras) =>
            service.control.Status(status == Availability.Available ? "GPSを待っています。" : "GPSを測位できません。屋外で確認してください。");
    }
}
