using WalkLogger.Core;

namespace WalkLogger.App;

public sealed class PhotoEditor
{
    public required PhotoRecord Photo { get; init; }
    public required string ImagePath { get; init; }
    public string Caption => Photo.Time.ToLocalTime().ToString("MM/dd HH:mm:ss") + " / " + Photo.Image;
    public string Note { get; set; } = "";
}
