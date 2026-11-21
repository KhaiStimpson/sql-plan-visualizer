using Microsoft.UI.Xaml;
using SqlPlanViz.Themes;

namespace SqlPlanViz;

/// <summary>
/// Thin host for <see cref="MainPage"/>. The content lives in a Page rather than directly
/// in the Window because compiled bindings that use a converter need a FrameworkElement
/// root, and Window is not one.
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "SQL Plan Visualizer";

        // No backdrop material: Gauge paints the root bg-0 and the title bar bg-2 itself
        // (docs/design/gauge/PLATFORMS.md, "Native window chrome").
        var args = Environment.GetCommandLineArgs();
        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 980));

        // A developer view of every Gauge style, for judging them in both themes.
        if (args.Length > 1 && args[1] == "--gallery")
        {
            var gallery = new Views.GaugeGalleryPage();
            Content = gallery;
            ThemeService.Attach(this, gallery);
            SetTitleBar(gallery.TitleBar);
            return;
        }

        var page = new MainPage();
        Content = page;

        // The stored theme goes on before the window is first shown, so it never paints in
        // the system theme and then flips.
        ThemeService.Attach(this, page);
        SetTitleBar(page.TitleBar);

        // Keep the palette button in the title bar clickable as the window and the bar resize.
        page.TitleBarLayoutChanged += (_, _) => page.UpdateTitleBarPassthrough(AppWindow);

        // "Open with" / command line: SqlPlanViz.exe some-plan.sqlplan
        if (args.Length > 1 && File.Exists(args[1]))
        {
            page.Loaded += async (_, _) => await page.OpenPathAsync(Path.GetFullPath(args[1]));
        }
    }
}
