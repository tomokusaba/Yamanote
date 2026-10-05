namespace WalkLogger.Mobile;

public partial class MainPage : ContentPage
{
    private readonly RecorderViewModel model;
    public MainPage(RecorderViewModel model)
    {
        InitializeComponent();
        this.model = model;
        BindingContext = model;
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RecorderViewModel.Current)) RouteMap.SetTrack(model.Current);
        };
        RouteMap.Failed += model.ShowMapError;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await model.InitializeAsync();
    }
}
