using WalkLogger.Core;

namespace WalkLogger.Presentation;

public interface IWorkspaceDialogs
{
    string? OpenLogFile();
    string? OpenImportFolder();
    string? OpenPhotoFile();
    string? ChooseArchiveFolder();
    string? SaveFile(string filename, string filter);
    bool Confirm(string text, string title);
    DateTimeOffset? ChoosePhotoTime(DateTime initialTime);
    void OpenFolder(string folder);
}

public interface IWorkspaceDiagnostics
{
    Task<string> ReportAsync(Exception exception, string? context = null, IProgress<string>? status = null);
}

public sealed record WorkspaceLaunchOptions(string BaseDirectory, bool Smoke = false,
    bool SmallWindow = false, string? SmokeOutput = null);

public sealed class PhotoEditorState
{
    public required PhotoRecord Photo { get; init; }
    public required string ImagePath { get; init; }
    public string Caption => Photo.Time.ToLocalTime().ToString("MM/dd HH:mm:ss") + " / " + Photo.Image;
    public string Note { get; set; } = "";
}

public sealed record RouteMapState(WalkSession Current, WalkSession? Previous, string PhotoFolder);
