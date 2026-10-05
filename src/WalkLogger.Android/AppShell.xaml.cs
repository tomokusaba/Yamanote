namespace WalkLogger.Mobile;

public partial class AppShell : Shell
{
    public AppShell(MainPage recorder, HistoryPage history, SettingsPage settings)
    {
        InitializeComponent();
        var bar = new TabBar();
        Add(bar, "記録", "record", recorder);
        Add(bar, "記録一覧", "history", history);
        Add(bar, "設定", "settings", settings);
        Items.Add(bar);
        SizeChanged += (_, _) =>
        {
            FlyoutBehavior = Width >= 600 ? FlyoutBehavior.Locked : FlyoutBehavior.Disabled;
            SetTabBarIsVisible(this, Width < 600);
        };
    }
    private static void Add(TabBar bar, string title, string route, ContentPage page)
    {
        var tab = new Tab { Title = title };
        tab.Items.Add(new ShellContent { Title = title, Route = route, Content = page });
        bar.Items.Add(tab);
    }
}
