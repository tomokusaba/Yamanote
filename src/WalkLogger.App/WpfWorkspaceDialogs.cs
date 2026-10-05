using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using WalkLogger.Presentation;

namespace WalkLogger.App;

public sealed class WpfWorkspaceDialogs : IWorkspaceDialogs
{
    private Window? owner;
    private Window Owner => owner ?? throw new InvalidOperationException("ダイアログの所有ウィンドウが未設定です。");
    internal void Attach(Window window) => owner = window;

    public string? OpenLogFile()
    {
        var dialog = new OpenFileDialog { Filter = "GPSログ|*.gpx;*.ndjson|GPX|*.gpx|CoreS3ログ|*.ndjson" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? OpenImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "SDカードのwalksフォルダー、または記録フォルダーを選択" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? OpenPhotoFile()
    {
        var dialog = new OpenFileDialog { Filter = "JPEG写真|*.jpg;*.jpeg" };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? ChooseArchiveFolder()
    {
        var dialog = new OpenFolderDialog { Title = "アーカイブ保存先（OneDriveの同期フォルダー内も選べます）" };
        return dialog.ShowDialog(Owner) == true ? dialog.FolderName : null;
    }

    public string? SaveFile(string filename, string filter)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = filename };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public bool Confirm(string text, string title) => MessageBox.Show(Owner, text, title,
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public DateTimeOffset? ChoosePhotoTime(DateTime initialTime)
    {
        var dialog = new PhotoTimeWindow(initialTime) { Owner = Owner };
        return dialog.ShowDialog() == true ? dialog.Timestamp : null;
    }

    public void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

    internal void ShowError(string text) => MessageBox.Show(Owner, text,
        "WalkLogger — 処理できませんでした", MessageBoxButton.OK, MessageBoxImage.Error);
}
