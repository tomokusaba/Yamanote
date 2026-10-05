namespace WalkLogger.Mobile;

public partial class SettingsPage : ContentPage
{
    private readonly RecorderViewModel model;
    public SettingsPage(RecorderViewModel model)
    {
        InitializeComponent();
        this.model = model;
        BindingContext = model;
        IntervalPicker.ItemsSource = new[] { "5秒", "10秒" };
        IntervalPicker.SelectedIndex = model.IntervalSeconds == 5 ? 0 : 1;
    }
    private void IntervalChanged(object? sender, EventArgs e)
    {
        if (IntervalPicker.SelectedIndex >= 0)
            model.IntervalSeconds = IntervalPicker.SelectedIndex == 0 ? 5 : 10;
    }
    private void OpenAppSettings(object? sender, EventArgs e) => AppInfo.Current.ShowSettingsUI();
}
