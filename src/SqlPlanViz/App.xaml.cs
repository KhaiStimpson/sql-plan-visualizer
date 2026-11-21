using Microsoft.UI.Xaml;

namespace SqlPlanViz;

public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    /// <summary>
    /// The HWND of the main window. Unpackaged apps have to parent pickers and dialogs to
    /// it explicitly, and only the Window knows it.
    /// </summary>
    public static IntPtr WindowHandle { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);

        // Anchor the Microsoft Entra MFA popup to the app window (see InteractiveAuthProvider).
        Capture.InteractiveAuthProvider.Register(() => WindowHandle);

        _window.Activate();
    }
}
