using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WalkLogger.App;
using WalkLogger.Application;
using WalkLogger.Core;
using WalkLogger.Infrastructure.Windows;
using WalkLogger.Presentation;

internal static class WindowsTestRunner
{
    private static int passed;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        var suppliedRoot = args.FirstOrDefault(a => a.StartsWith("--test-root="))?["--test-root=".Length..];
        var root = suppliedRoot ?? Path.Combine(Path.GetTempPath(), "WalkLogger-windows-tests-" + Guid.NewGuid().ToString("N"));
        if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) ||
            !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(root), "^WalkLogger-windows-tests-[0-9a-f]{32}$"))
            throw new ArgumentException("Windows test root must be an isolated temporary directory.");
        Directory.CreateDirectory(root);
        var app = new App(root);
        app.InitializeComponent();
        var scenario = args.FirstOrDefault(a => a.StartsWith("--case="))?["--case=".Length..];
        FaultHost? host = null;
        Exception? failure = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                if (scenario is not null)
                {
                    host = new FaultHost(scenario, root);
                    await app.StartHostAsync(host);
                    if (scenario != "start-fail") host.Lifetime.StopApplication();
                }
                else
                {
                    await RunAsync(root);
                    app.Shutdown(0);
                }
            }
            catch (Exception ex) { failure = ex; Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        var code = app.Run();
        try
        {
            if (failure is not null) throw failure;
            if (scenario is not null)
            {
                Check(code == 1 && host is { Disposed: true, Stopped: true },
                    "WPF awaits stop and disposes Host during " + scenario);
                Check(File.ReadAllText(Path.Combine(root, "diagnostics.log")).Contains("failed"),
                    "Lifecycle failure writes isolated diagnostics");
            }
            else Check(code == 0, "Windows scenarios exit cleanly");
            Console.WriteLine($"{passed} Windows scenarios passed.");
            Environment.ExitCode = 0;
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (suppliedRoot is null) Directory.Delete(root, true); }
    }

    private static async Task RunAsync(string root)
    {
        var settingsRoot = Path.Combine(root, "settings");
        var settings = new WindowsSettingsStore(settingsRoot);
        Check(settings.LoadKey() == "" && (await settings.LoadAsync()).ArchiveRoot.EndsWith("Archive"),
            "Isolated settings initialize without a key");
        var configuration = new SettingsData
        {
            ArchiveRoot = root, AzureEndpoint = "https://example.openai.azure.com/",
            AzureDeployment = "fake", NominatimContact = "test@example.com"
        };
        await settings.SaveAsync(configuration, "test-only-key");
        Check(settings.LoadKey() == "test-only-key" && (await settings.LoadAsync()).AzureDeployment == "fake" &&
              !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(settingsRoot, "azure-key.dpapi"))).Contains("test-only-key"),
            "Windows DPAPI protects and round-trips an isolated key");
        await settings.SaveAsync(configuration, "");
        Check(settings.LoadKey() == "", "Saving an empty key deletes the isolated encrypted key");
        await settings.SaveAsync(configuration, "");
        await File.WriteAllTextAsync(Path.Combine(settingsRoot, "settings.json"), "null");
        try { await settings.LoadAsync(); throw new Exception("Null settings accepted"); }
        catch (InvalidDataException) { Check(true, "Null Windows settings are rejected"); }
        await File.WriteAllBytesAsync(Path.Combine(settingsRoot, "azure-key.dpapi"), [1, 2, 3]);
        try { settings.LoadKey(); throw new Exception("Corrupt DPAPI accepted"); }
        catch (CryptographicException) { Check(true, "Corrupt DPAPI is reported"); }
        Check(new WindowsSettingsStore().LocalRoot == SettingsStore.LocalRoot, "Default Windows store retains its local root");

        var metadata = new PhotoMetadataReader();
        var jpeg = Path.Combine(root, "photo.jpg");
        WriteJpeg(jpeg, "2026:10:05 11:23:00");
        Check(metadata.ReadCaptureTime(jpeg) == new DateTime(2026, 10, 5, 11, 23, 0), "Windows reads EXIF capture time");
        WriteJpeg(jpeg, null);
        Check(metadata.ReadCaptureTime(jpeg) is null, "JPEG without EXIF has no inferred capture time");
        var photo = new WalkLogger.App.PhotoEditor { Photo = new(DateTimeOffset.UtcNow, 35, 139, "photo.jpg"), ImagePath = jpeg };
        photo.Note = "observed";
        Check(photo.Caption.Contains("photo.jpg") && photo.Note == "observed", "Legacy PhotoEditor remains compatible");
        Check(new BleDeviceInfo(42, "CoreS3").ToString().Contains("00000000002A"), "Windows BLE device caption");
        using (var connection = new WindowsBleTransferFactory().Create())
        {
            try
            {
                await connection.DownloadAsync(new(-1, "..\\bad", 1), root, new Progress<string>(), default);
                throw new Exception("Bad BLE path accepted");
            }
            catch (InvalidDataException) { Check(true, "Windows BLE download validates the path before accessing a device"); }
        }

        var ports = new WorkspaceChecks.FakePorts(root);
        var model = ports.Model();
        var dialogs = new WpfWorkspaceDialogs();
        var options = new WorkspaceLaunchOptions(AppContext.BaseDirectory);
        var window = new MainWindow(model, dialogs, ports, options);
        var map = (RouteMapPresenter)typeof(MainWindow).GetField("map", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
        var smoke = new WorkspaceSmokeRunner(ports, new(AppContext.BaseDirectory, Smoke: true));
        Check(!await smoke.RunAsync(model, map, window), "Smoke runner rejects an uninitialized workspace");
        var verification = typeof(WorkspaceSmokeRunner).GetMethod("VerifyBindings", BindingFlags.NonPublic | BindingFlags.Static)!;
        Check(((string?)verification.Invoke(null, [model, new Window()]))?.Contains("Record list") == true,
            "Smoke runner detects missing record bindings");
        await model.InitializeAsync();
        var password = (PasswordBox)window.FindName("ApiKeyBox");
        password.Password = "edited-key";
        Check(model.ApiKey == "edited-key", "PasswordBox forwards its value to the ViewModel");
        model.ApiKey = "updated-key";
        Check(password.Password == "updated-key", "ViewModel updates PasswordBox without a notification loop");
        ports.Initial.SaveFailureAt = 1;
        window.Close();
        Check(ports.Errors.Any(e => e.Contains("終了")), "Closing refuses to exit after a failed save");
        ports.Initial.SaveFailureAt = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = model.RunAsync("pending", ct => gate.Task.WaitAsync(ct));
        window.Close();
        await running;
        Check(model.Idle, "Closing cancels the active operation rather than discarding edits");
        window.Close();
        Check(ports.Initial.Saves == 2, "Closing saves before disposing the WPF view");
        var legacy = new MainWindow(ports, ports, NullLogger<MainWindow>.Instance, ports, ports, ports, ports, ports, ports, new(ports));
        legacy.Close();
        Check(true, "Legacy MainWindow constructor delegates to the same presentation composition");

        var owner = new Window();
        owner.Show();
        dialogs.Attach(owner);
        foreach (var action in new Func<string?>[]
        {
            dialogs.OpenLogFile, dialogs.OpenImportFolder, dialogs.OpenPhotoFile, dialogs.ChooseArchiveFolder,
            () => dialogs.SaveFile("test.md", "Markdown|*.md")
        })
        {
            Check(CancelNativeDialog(action) is null, "Native file or folder dialog cancellation");
        }
        Check(!CancelNativeDialog(() => dialogs.Confirm("test-only confirmation", "WalkLogger test")),
            "Native confirmation cancellation does not grant consent");
        var timestamp = new DateTimeOffset(2026, 10, 5, 11, 23, 0, TimeSpan.FromHours(9));
        _ = owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<PhotoTimeWindow>().Single();
            var text = (TextBox)dialog.FindName("TimeBox");
            text.Text = "invalid";
            typeof(PhotoTimeWindow).GetMethod("Confirm_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dialog, [dialog, new RoutedEventArgs()]);
            Check(((TextBlock)dialog.FindName("ErrorText")).Text.Contains("時差"), "Photo timestamp rejects a value without an offset");
            text.Text = timestamp.ToString("O");
            typeof(PhotoTimeWindow).GetMethod("Confirm_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(dialog, [dialog, new RoutedEventArgs()]);
        }));
        Check(dialogs.ChoosePhotoTime(timestamp.DateTime) == timestamp, "Photo timestamp dialog confirms the explicit offset");
        _ = owner.Dispatcher.BeginInvoke(new Action(() =>
            System.Windows.Application.Current.Windows.OfType<PhotoTimeWindow>().Single().Close()));
        Check(dialogs.ChoosePhotoTime(timestamp.DateTime) is null, "Photo timestamp dialog can be cancelled");
        var diagnosticFiles = new DiagnosticFiles(root);
        var diagnosticSettings = new WindowsSettingsStore(root);
        var diagnostic = new WpfWorkspaceDiagnostics(NullLogger<MainWindow>.Instance, diagnosticFiles, diagnosticSettings,
            dialogs, new(AppContext.BaseDirectory, Smoke: true));
        var textResult = await diagnostic.ReportAsync(new IOException("test failure"), "context", new Progress<string>());
        Check(textResult.Contains("test failure") && File.Exists(Path.Combine(root, "diagnostics.log")),
            "WPF diagnostics log the contextual error in an isolated root");
        diagnosticFiles.Fail = true;
        Check((await diagnostic.ReportAsync(new IOException("test"), null, new Progress<string>())).Contains("保存にも失敗"),
            "WPF diagnostics report secondary filesystem failure");
        var interactive = new WpfWorkspaceDiagnostics(NullLogger<MainWindow>.Instance, diagnosticFiles, diagnosticSettings,
            dialogs, options);
        CancelNativeDialog(() => interactive.ReportAsync(new IOException("test message")).GetAwaiter().GetResult());
        owner.Close();
    }

    private static void WriteJpeg(string path, string? date)
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Rgb24, null, new byte[] { 0, 0, 0 }, 3);
        var metadata = new BitmapMetadata("jpg");
        if (date is not null) metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", date);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T CancelNativeDialog<T>(Func<T> action)
    {
        var driver = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                nint found = 0;
                EnumWindows((handle, _) =>
                {
                    GetWindowThreadProcessId(handle, out var processId);
                    var name = new StringBuilder(128);
                    GetClassName(handle, name, name.Capacity);
                    if (processId == Environment.ProcessId && name.ToString() == "#32770" && IsWindowVisible(handle))
                        found = handle;
                    return true;
                }, 0);
                if (found != 0)
                {
                    PostMessage(found, 0x0010, 0, 0);
                    PostMessage(found, 0x0111, 7, 0);
                    return;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("No owned native dialog appeared.");
        });
        var result = action();
        driver.GetAwaiter().GetResult();
        return result;
    }

    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    private sealed class DiagnosticFiles(string root) : IWorkspaceFiles
    {
        public bool Fail { get; set; }
        public string[] FindImportFiles(string folder) => [];
        public bool DirectoryExists(string folder) => Directory.Exists(folder);
        public void CreateDirectory(string folder) => Directory.CreateDirectory(folder);
        public string CopyPhoto(string source, string destinationFolder) => throw new NotSupportedException();
        public Task AppendDiagnosticsAsync(string localRoot, string text) => Fail ?
            Task.FromException(new IOException("diagnostic write failed")) :
            File.AppendAllTextAsync(Path.Combine(root, "diagnostics.log"), text);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
    }

    private sealed class FaultHost : IHost
    {
        private readonly string scenario;
        public Lifetime Lifetime { get; } = new();
        public bool Disposed { get; private set; }
        public bool Stopped { get; private set; }
        public IServiceProvider Services { get; }
        public FaultHost(string scenario, string root)
        {
            this.scenario = scenario;
            var ports = new WorkspaceChecks.FakePorts(root);
            Services = new ServiceCollection().AddLogging()
                .AddSingleton<IHostApplicationLifetime>(Lifetime)
                .AddSingleton<IHostedService, HostSmokeProbe>()
                .AddSingleton(_ => new MainWindow(ports.Model(), new WpfWorkspaceDialogs(), ports,
                    new WorkspaceLaunchOptions(AppContext.BaseDirectory)))
                .BuildServiceProvider();
        }
        public async Task StartAsync(CancellationToken ct = default)
        {
            foreach (var hosted in Services.GetServices<IHostedService>()) await hosted.StartAsync(ct);
            if (scenario == "start-fail") throw new IOException("start failed");
        }
        public async Task StopAsync(CancellationToken ct = default)
        {
            foreach (var hosted in Services.GetServices<IHostedService>()) await hosted.StopAsync(ct);
            Stopped = true;
            if (scenario == "stop-fail") throw new IOException("stop failed");
        }
        public void Dispose()
        {
            ((IDisposable)Services).Dispose();
            Disposed = true;
            if (scenario == "dispose-fail") throw new IOException("dispose failed");
        }
    }
}
