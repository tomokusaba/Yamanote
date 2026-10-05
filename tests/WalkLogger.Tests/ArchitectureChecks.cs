using System.Reflection;
using System.Xml.Linq;
using WalkLogger.Application;
using WalkLogger.Core;
using WalkLogger.Presentation;
using System.Windows.Input;

static class ArchitectureChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var allowed = new Dictionary<string, string[]>
        {
            ["WalkLogger.Domain"] = [],
            ["WalkLogger.Application"] = ["WalkLogger.Domain"],
            ["WalkLogger.Infrastructure"] = ["WalkLogger.Application"],
            ["WalkLogger.Infrastructure.Windows"] = ["WalkLogger.Infrastructure"],
            ["WalkLogger.Presentation"] = ["WalkLogger.Application"],
            ["WalkLogger.App"] = ["WalkLogger.Application", "WalkLogger.Infrastructure.Windows", "WalkLogger.Presentation"]
        };
        foreach (var (project, expected) in allowed)
        {
            var path = Path.Combine(root, "src", project, project + ".csproj");
            var references = XDocument.Load(path).Descendants("ProjectReference")
                .Select(p => Path.GetFileNameWithoutExtension(p.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)))
                .Order().ToArray();
            check(references.SequenceEqual(expected.Order()), "Architecture dependencies: " + project);
        }
        var domainReferences = typeof(WalkSession).Assembly.GetReferencedAssemblies();
        check(domainReferences.All(a => !a.Name!.StartsWith("WalkLogger.") && a.Name != "System.Text.Json"),
            "Domain has no application, persistence, HTTP adapter or serialization reference");
        check(Assembly.Load("WalkLogger.Core").GetForwardedTypes().Length == 16,
            "Legacy Core assembly forwards all existing public types");
        var window = await File.ReadAllTextAsync(Path.Combine(root, "src", "WalkLogger.App", "MainWindow.xaml.cs"));
        check(!window.Contains("new ArchiveStore(") && !window.Contains("new HttpClient(") &&
              !window.Contains("new BleClient(") && !window.Contains("File.") && !window.Contains("Directory.") &&
              !window.Contains("WalkLogger.Infrastructure"),
            "MainWindow uses application ports, not concrete persistence or network adapters");
        var appProject = XDocument.Load(Path.Combine(root, "src", "WalkLogger.App", "WalkLogger.App.csproj"));
        var app = await File.ReadAllTextAsync(Path.Combine(root, "src", "WalkLogger.App", "App.xaml.cs"));
        var program = await File.ReadAllTextAsync(Path.Combine(root, "src", "WalkLogger.App", "Program.cs"));
        check(appProject.Descendants("StartupObject").Single().Value == "WalkLogger.App.Program" &&
              program.Contains("[STAThread]") && program.Contains("Host.CreateApplicationBuilder") &&
              !app.Contains("Host.CreateApplicationBuilder") && !app.Contains("builder.Services"),
            "Program is the STA entry point and sole Host composition root");
        check(!window.Contains("DownloadAsync") && !window.Contains("ImportAsync") &&
              !window.Contains("GenerateAsync") && !window.Contains("MatchPoint") &&
              !window.Contains("CoreWebView2") && !window.Contains("CapturePreviewAsync"),
            "MainWindow delegates use cases, map integration and smoke verification");
        check(typeof(MainWindowViewModel).Assembly.GetReferencedAssemblies().All(a =>
                a.Name != "PresentationFramework" && !a.Name!.StartsWith("WalkLogger.Infrastructure") &&
                !a.Name.StartsWith("Microsoft.Web.WebView2")),
            "Presentation is portable and independent of WPF, WebView2 and Infrastructure");
        var xaml = XDocument.Load(Path.Combine(root, "src", "WalkLogger.App", "MainWindow.xaml"));
        var commands = xaml.Descendants().Attributes("Command")
            .Select(a => a.Value["{Binding ".Length..^1]).ToArray();
        check(commands.Length >= 20 && commands.All(name =>
                typeof(ICommand).IsAssignableFrom(typeof(MainWindowViewModel).GetProperty(name)?.PropertyType)),
            "All XAML commands resolve to ViewModel ICommand properties");
        check(xaml.Descendants().Where(e => e.Name.LocalName == "TextBox" &&
                e.Attribute("IsReadOnly")?.Value != "True" && e.Attribute("Text") is not null)
            .All(e => e.Attribute("Text")!.Value.Contains("UpdateSourceTrigger=PropertyChanged")),
            "Text edits reach the ViewModel before closing even while the editor has focus");

        var archive = new FakeArchive();
        await new ArchiveImportUseCase(new FakeFiles()).ImportDirectoryAsync(archive, "ignored",
            new Progress<string>(), default);
        check(archive.Imported.SequenceEqual(new[] { "a.gpx", "z.gpx" }),
            "Import use case runs through fake ports in original deterministic order");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await new ArchiveImportUseCase(new FakeFiles()).ImportDirectoryAsync(new FakeArchive(),
                "ignored", new Progress<string>(), cancelled.Token);
            throw new Exception("Cancelled import unexpectedly succeeded");
        }
        catch (OperationCanceledException)
        {
            check(true, "Import use case honors cancellation without real filesystem");
        }
    }

    private sealed class FakeArchive : IArchiveStore
    {
        public List<string> Imported { get; } = [];
        public string Root => "unused";
        public string SessionFolder(WalkSession session) => "unused";
        public Task<List<WalkSession>> LoadAsync(CancellationToken ct = default) => Task.FromResult(new List<WalkSession>());
        public Task SaveAsync(WalkSession walk, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WalkSession> ImportAsync(string path, CancellationToken ct = default)
        {
            Imported.Add(path);
            return Task.FromResult(new WalkSession());
        }
    }

    private sealed class FakeFiles : IWorkspaceFiles
    {
        public string[] FindImportFiles(string folder) => ["z.gpx", "a.gpx"];
        public bool DirectoryExists(string folder) => throw new NotSupportedException();
        public void CreateDirectory(string folder) => throw new NotSupportedException();
        public string CopyPhoto(string source, string destinationFolder) => throw new NotSupportedException();
        public Task AppendDiagnosticsAsync(string localRoot, string text) => throw new NotSupportedException();
    }
}
