using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WalkLogger.Core;
using WalkLogger.Application;

namespace WalkLogger.App;

public sealed class AppSettings : SettingsData
{
    public AppSettings() => ArchiveRoot = Path.Combine(SettingsStore.LocalRoot, "Archive");
}

public static class SettingsStore
{
    public static string LocalRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WalkLogger");
    public static Task<AppSettings> LoadAsync() => LoadAsync(LocalRoot);
    internal static async Task<AppSettings> LoadAsync(string root)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        return File.Exists(settingsPath)
            ? JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsPath), Json.Options) ??
              throw new InvalidDataException("設定ファイルが不正です。")
            : new();
    }

    public static Task SaveAsync(AppSettings settings, string key) => SaveAsync(LocalRoot, settings, key);
    internal static async Task SaveAsync(string root, AppSettings settings, string key)
    {
        var settingsPath = Path.Combine(root, "settings.json");
        var keyPath = Path.Combine(root, "azure-key.dpapi");
        Directory.CreateDirectory(root);
        await ArchiveStore.AtomicWriteAsync(settingsPath, JsonSerializer.Serialize(settings, Json.Options));
        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(keyPath)) File.Delete(keyPath);
        }
        else
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
            var temp = keyPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, keyPath, true);
        }
    }

    public static string LoadKey() => LoadKey(LocalRoot);
    internal static string LoadKey(string root)
    {
        var keyPath = Path.Combine(root, "azure-key.dpapi");
        return File.Exists(keyPath)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser))
            : "";
    }
}
