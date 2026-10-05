namespace WalkLogger.Mobile;

public partial class HistoryPage : ContentPage
{
    private readonly RecorderViewModel model;
    public HistoryPage(RecorderViewModel model)
    {
        InitializeComponent();
        this.model = model;
        BindingContext = model;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await model.LoadHistoryAsync();
    }
}
