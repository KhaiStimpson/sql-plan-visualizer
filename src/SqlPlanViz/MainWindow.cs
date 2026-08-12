using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

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

        // Mica Alt is the layered "base" material — the right backdrop for a tool window
        // with a command strip and a side pane sitting on top of it.
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };

        var page = new MainPage();
        Content = page;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(page.TitleBar);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1560, 980));

        // "Open with" / command line: SqlPlanViz.exe some-plan.sqlplan
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            page.Loaded += async (_, _) => await page.OpenPathAsync(Path.GetFullPath(args[1]));
        }
    }
}
