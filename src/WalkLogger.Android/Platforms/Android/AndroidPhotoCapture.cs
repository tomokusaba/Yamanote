using System.Globalization;
using Android.Media;
using Stream = System.IO.Stream;

namespace WalkLogger.Mobile;

public sealed record CapturedPhoto(Stream Content, DateTimeOffset Time, bool EstimatedTime) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed class AndroidPhotoCapture
{
    public async Task<CapturedPhoto?> CaptureAsync()
    {
        if (!MediaPicker.Default.IsCaptureSupported)
            throw new InvalidOperationException("カメラを起動できません。カメラアプリのあるAndroid端末を使用してください。");
        var file = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions
        {
            Title = "街歩きの写真", PreserveMetaData = true
        });
        if (file is null) return null;
        var returnedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? time = null;
        await using (var metadata = await file.OpenReadAsync())
        {
            using var exif = new ExifInterface(metadata);
            if (DateTime.TryParseExact(exif.GetAttribute(ExifInterface.TagDatetimeOriginal),
                    "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                var offsetText = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? exif.GetAttribute(ExifInterface.TagOffsetTimeOriginal) : null;
                if (offsetText is not null && DateTimeOffset.TryParseExact(
                        local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + offsetText,
                        "yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetTime))
                    time = offsetTime;
                else if (!TimeZoneInfo.Local.IsInvalidTime(local) && !TimeZoneInfo.Local.IsAmbiguousTime(local))
                    time = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            }
        }
        return new(await file.OpenReadAsync(), time ?? returnedAt, time is null);
    }
}
