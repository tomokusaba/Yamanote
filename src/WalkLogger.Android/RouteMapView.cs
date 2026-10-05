using System.Text.Json;
using WalkLogger.Core;
using WalkLogger.Recording;

namespace WalkLogger.Mobile;

public sealed class RouteMapView : WebView
{
    private bool ready;
    private RecordedWalk? record;
    private bool rendering;
    public event Action<string>? Failed;
    public RouteMapView()
    {
        Source = "file:///android_asset/map/index.html";
        Navigating += async (_, e) =>
        {
            if (e.Url == "file:///android_asset/map/index.html") return;
            e.Cancel = true;
            if (e.Url == "https://www.openstreetmap.org/copyright")
            {
                try { await Launcher.Default.OpenAsync(e.Url); }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceError(ex.ToString());
                    Failed?.Invoke("OpenStreetMapの帰属ページを開けません。");
                }
            }
        };
        Navigated += async (_, e) =>
        {
            ready = e.Result == WebNavigationResult.Success;
            if (ready) await RenderAsync();
            else Failed?.Invoke("地図を読み込めません。GPS記録は利用できます。");
        };
        HandlerChanged += (_, _) =>
        {
            if (Handler?.PlatformView is Android.Webkit.WebView native)
            {
                native.Settings.UserAgentString += " WalkLoggerAndroid/1.0";
                native.Settings.AllowFileAccess = true;
            }
        };
        SemanticProperties.SetDescription(this, "OpenStreetMapの実測歩行ルート。GPS未取得時は初期表示です。");
    }
    public void SetTrack(RecordedWalk? value)
    {
        if (ReferenceEquals(record, value)) return;
        record = value;
        _ = RenderAsync();
    }
    private async Task RenderAsync()
    {
        if (!ready || rendering) return;
        rendering = true;
        try
        {
            RecordedWalk? snapshot;
            do
            {
                snapshot = record;
                var segments = WalkAnalysis.Segments(snapshot?.Points ?? []).Select(segment =>
                    segment.Select(p => new[] { p.Lat, p.Lon }));
                var data = JsonSerializer.Serialize(new { segments, photos = snapshot?.Photos ?? [] });
                await EvaluateJavaScriptAsync($"window.drawTrack({data})");
            } while (!ReferenceEquals(snapshot, record));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(ex.ToString());
            SemanticProperties.SetDescription(this, "地図描画に失敗しました。GPS記録は継続します。");
            Failed?.Invoke("地図描画に失敗しました。GPS記録は継続します。");
        }
        finally { rendering = false; }
    }
}
