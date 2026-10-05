using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WalkLogger.Core;

namespace WalkLogger.App;

public partial class App : System.Windows.Application
{
    private readonly string localRoot;
    public App() : this(SettingsStore.LocalRoot) { }
    internal App(string localRoot) => this.localRoot = localRoot;
    private IHost? host;
    private ILogger<App>? logger;
    private CancellationTokenRegistration stoppingRegistration;
    private bool shuttingDown;
    private readonly bool smoke = Environment.GetCommandLineArgs().Contains("--smoke");
    private readonly string? smokeOutput = Environment.GetCommandLineArgs()
        .FirstOrDefault(a => a.StartsWith("--smoke-output=", StringComparison.Ordinal))?["--smoke-output=".Length..];

    internal async Task StartHostAsync(IHost applicationHost)
    {
        host = applicationHost;
        try
        {
            logger = host.Services.GetRequiredService<ILogger<App>>();
            await host.StartAsync();

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            stoppingRegistration = lifetime.ApplicationStopping.Register(() =>
            {
                if (!shuttingDown)
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!shuttingDown) MainWindow?.Close();
                    }));
            });
            MainWindow = host.Services.GetRequiredService<MainWindow>();
            MainWindow.Closed += MainWindow_Closed;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            await HandleStartupFailureAsync(ex);
        }
    }

    internal async Task HandleStartupFailureAsync(Exception ex)
    {
        await ReportLifetimeErrorAsync(ex, "アプリケーションの起動に失敗しました。");
        await ShutdownHostAsync(1);
    }

    private async void MainWindow_Closed(object? sender, EventArgs e) =>
        await ShutdownHostAsync(Environment.ExitCode);

    private async Task ShutdownHostAsync(int exitCode)
    {
        if (shuttingDown) return;
        shuttingDown = true;
        stoppingRegistration.Dispose();
        HostSmokeProbe? probe = null;
        try
        {
            if (host is not null)
            {
                if (smoke)
                    probe = host.Services.GetServices<IHostedService>().OfType<HostSmokeProbe>().SingleOrDefault();
                await host.StopAsync();
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            await ReportLifetimeErrorAsync(ex, "アプリケーションの終了処理に失敗しました。");
        }
        finally
        {
            try { host?.Dispose(); }
            catch (Exception ex)
            {
                exitCode = 1;
                await ReportLifetimeErrorAsync(ex, "アプリケーションのリソース解放に失敗しました。");
            }
            host = null;
        }
        if (smoke && smokeOutput is not null)
        {
            try
            {
                var result = File.Exists(smokeOutput)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(smokeOutput))?.AsObject() ??
                      throw new InvalidDataException("起動確認の出力が不正です。")
                    : new JsonObject { ["success"] = false };
                result["hostStarted"] = probe?.Started == true;
                result["hostStopped"] = probe?.Stopped == true;
                result["hostDisposed"] = probe?.Disposed == true;
                if (exitCode != 0 || probe is not { Started: true, Stopped: true, Disposed: true })
                {
                    result["success"] = false;
                    exitCode = 1;
                }
                await ArchiveStore.AtomicWriteAsync(smokeOutput, result.ToJsonString(Json.Options));
            }
            catch (Exception ex)
            {
                exitCode = 1;
                await ReportLifetimeErrorAsync(ex, "起動確認の結果を保存できませんでした。");
            }
        }
        Environment.ExitCode = exitCode;
        Shutdown(exitCode);
    }

    private async Task ReportLifetimeErrorAsync(Exception ex, string context)
    {
        logger?.LogError(ex, "{Context}", context);
        var text = context + "\n" + ex.Message;
        try
        {
            Directory.CreateDirectory(localRoot);
            await File.AppendAllTextAsync(Path.Combine(localRoot, "diagnostics.log"),
                $"{DateTimeOffset.Now:O} {context} {ex}\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            text += "\n診断ログも保存できませんでした: " + logError.Message;
        }
        if (!smoke) MessageBox.Show(text, "WalkLogger — ライフサイクルエラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
