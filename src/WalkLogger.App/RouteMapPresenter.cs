using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WalkLogger.Core;
using WalkLogger.Presentation;

namespace WalkLogger.App;

internal sealed class RouteMapPresenter(WebView2 view, string baseDirectory) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource rendered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action? Ready;
    public event Action<string>? Error;
    public bool IsReady { get; private set; }
    public bool Initialized => view.CoreWebView2 is not null;
    public Task Loaded => loaded.Task;
    public Task Rendered => rendered.Task;

    public async Task InitializeAsync(string dataFolder)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
        await view.EnsureCoreWebView2Async(environment);
        view.CoreWebView2.Settings.AreDevToolsEnabled = false;
        view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        view.CoreWebView2.Settings.UserAgent += " WalkLogger/1.0";
        view.CoreWebView2.SetVirtualHostNameToFolderMapping("walklogger.local",
            Path.Combine(baseDirectory, "Map"), CoreWebView2HostResourceAccessKind.DenyCors);
        view.CoreWebView2.NavigationStarting += (_, args) =>
        {
            if (args.Uri != "https://walklogger.local/index.html") args.Cancel = true;
        };
        view.CoreWebView2.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
                uri.Host is "www.openstreetmap.org" or "leafletjs.com")
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        view.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            if (args.Source != "https://walklogger.local/index.html") return;
            using var json = JsonDocument.Parse(args.WebMessageAsJson);
            var kind = json.RootElement.GetProperty("kind").GetString();
            if (kind == "ready")
            {
                IsReady = true;
                loaded.TrySetResult();
                Ready?.Invoke();
            }
            else if (kind == "rendered") rendered.TrySetResult();
            else if (kind == "error")
                Error?.Invoke("地図表示エラー: " + json.RootElement.GetProperty("message").GetString());
        };
        view.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) Error?.Invoke($"地図の読み込みに失敗しました: {args.WebErrorStatus}");
        };
        view.Source = new Uri("https://walklogger.local/index.html");
    }

    public void Render(RouteMapState state)
    {
        if (!IsReady) return;
        view.CoreWebView2.SetVirtualHostNameToFolderMapping("walkphotos.local", state.PhotoFolder,
            CoreWebView2HostResourceAccessKind.DenyCors);
        static object Lines(WalkSession walk) => WalkAnalysis.Segments(walk.Points)
            .Select(s => s.Select(p => new[] { p.Lat, p.Lon }).ToArray()).ToArray();
        view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            segments = Lines(state.Current),
            previous = state.Previous is null ? Array.Empty<object>() : Lines(state.Previous),
            photos = state.Current.Photos.Select(p => new
            {
                p.Lat, p.Lon, p.Image, p.Note, caption = p.Time.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss")
            }),
            places = state.Current.Places
        }, JsonOptions));
    }

    public Task<string> InspectAsync() => view.CoreWebView2.ExecuteScriptAsync(
        "JSON.stringify({title:document.title,points:routes.getLayers().length,photos:markers.getLayers().length})");

    public async Task<byte[]> CaptureAsync()
    {
        using var image = new MemoryStream();
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        return image.ToArray();
    }

    public void Dispose() => view.Dispose();
}
