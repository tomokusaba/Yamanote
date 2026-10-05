using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WalkLogger.Application;
using WalkLogger.Presentation;

namespace WalkLogger.App;

internal sealed class WorkspaceSmokeRunner(IFileExporter exporter, WorkspaceLaunchOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<bool> RunAsync(MainWindowViewModel model, RouteMapPresenter map, Window window)
    {
        if (!model.Initialized) return await FinishAsync(model, map, false, "Initialization failed");
        try { await map.Loaded.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { return await FinishAsync(model, map, false, "Map load timed out"); }
        await model.LoadDemoAsync();
        if (!map.IsReady || model.Current is null)
            return await FinishAsync(model, map, false, "Map or sample unavailable");
        try { await map.Rendered.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { return await FinishAsync(model, map, false, "Map render timed out"); }
        await Task.Delay(1500);
        var bindingError = VerifyBindings(model, window);
        if (bindingError is not null) return await FinishAsync(model, map, false, bindingError);
        var result = await map.InspectAsync();
        using var document = JsonDocument.Parse(JsonSerializer.Deserialize<string>(result)!);
        if (document.RootElement.GetProperty("points").GetInt32() != model.Current.Stats.SegmentCount)
            return await FinishAsync(model, map, false, "Rendered route count does not match measured segments");
        if (options.SmokeOutput is not null)
        {
            await exporter.WriteBytesAsync(Path.ChangeExtension(options.SmokeOutput, ".map.png"), await map.CaptureAsync());
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var screen = new MemoryStream();
            encoder.Save(screen);
            await exporter.WriteBytesAsync(Path.ChangeExtension(options.SmokeOutput, ".window.png"), screen.ToArray());
        }
        return await FinishAsync(model, map, true, result);
    }

    private async Task<bool> FinishAsync(MainWindowViewModel model, RouteMapPresenter map, bool success, string result)
    {
        if (options.SmokeOutput is not null)
            await exporter.WriteTextAsync(options.SmokeOutput, JsonSerializer.Serialize(new
            {
                success, mapReady = map.IsReady, records = model.Walks.Count, result, root = model.SmokeRoot
            }, JsonOptions));
        return success;
    }

    private static string? VerifyBindings(MainWindowViewModel model, Window window)
    {
        if (window.FindName("WalkList") is not ListBox records || records.SelectedItem != model.Current ||
            !ReferenceEquals(records.ItemsSource, model.Walks))
            return "Record list binding does not match the ViewModel";
        if (window.FindName("PhotoList") is not ItemsControl photos || !ReferenceEquals(photos.ItemsSource, model.PhotoEditors))
            return "Photo list binding does not match the ViewModel";
        foreach (var item in LogicalDescendants(window))
        {
            if (item is Button button && button.GetBindingExpression(Button.CommandProperty) is { } command &&
                (command.HasError || button.Command is null))
                return "Command binding failed: " + button.Content;
            if (item is TextBox text && text.GetBindingExpression(TextBox.TextProperty) is { HasError: true })
                return "Editor binding failed: " + text.Name;
        }
        if (window.FindName("TitleBox") is not TextBox title || title.Text != model.Title)
            return "Title binding does not match the ViewModel";
        var original = model.Title;
        title.Focus();
        title.SetCurrentValue(TextBox.TextProperty, original + " [binding check]");
        var updated = model.Title == title.Text;
        title.SetCurrentValue(TextBox.TextProperty, original);
        return updated ? null : "Focused editor did not update the ViewModel immediately";
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalDescendants(child)) yield return descendant;
        }
    }
}
