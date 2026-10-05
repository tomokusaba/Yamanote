using System.Globalization;
using System.Windows;
using WalkLogger.Application;

namespace WalkLogger.App;

public partial class PhotoTimeWindow : Window
{
    public DateTimeOffset Timestamp { get; private set; }

    public PhotoTimeWindow(DateTime time)
    {
        InitializeComponent();
        TimeBox.Text = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Unspecified),
            TimeZoneInfo.Local.GetUtcOffset(time)).ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!TimestampText.HasTimezone(TimeBox.Text) ||
            !DateTimeOffset.TryParse(TimeBox.Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            ErrorText.Text = "例: 2026-10-05T11:23:00+09:00 のように時差を含めて入力してください。";
            return;
        }
        Timestamp = time;
        DialogResult = true;
    }
}
