using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SqlPlanViz.Capture;

namespace SqlPlanViz.Views;

/// <summary>
/// Connection + query entry for live capture (TDD §6B). Nothing here is persisted —
/// §10 assumes credentials are re-entered per session.
/// </summary>
public sealed partial class ConnectView : UserControl
{
    private readonly ConnectionSettings _settings;
    private readonly PlanCaptureService _capture = new();
    private readonly RecentConnectionsStore _recent = new();

    public ConnectView(ConnectionSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        ServerBox.Text = settings.Server;
        DatabaseBox.Text = settings.Database;
        UserBox.Text = settings.UserId;
        EncryptBox.IsChecked = settings.Encrypt;
        TrustCertBox.IsChecked = settings.TrustServerCertificate;
        AuthBox.SelectedIndex = AuthToIndex(settings.Auth);
        ConnectionStringBox.Text = settings.RawConnectionString;
        EntryModeBox.SelectedIndex = settings.UseConnectionString ? 1 : 0;
        ApplyEntryMode();
    }

    /// <summary>
    /// When true the view is for opening a connection only — the query editor and the
    /// actual/estimated mode picker are hidden. Everything else is unchanged.
    /// </summary>
    public bool ConnectOnly
    {
        get => _connectOnly;
        set
        {
            _connectOnly = value;
            var visibility = value ? Visibility.Collapsed : Visibility.Visible;
            QueryBox.Visibility = visibility;
            ModeButtons.Visibility = visibility;
        }
    }

    private bool _connectOnly;

    public string Query => QueryBox.Text;

    public CaptureMode Mode =>
        ModeButtons.SelectedIndex == 1 ? CaptureMode.EstimatedOnly : CaptureMode.Actual;

    private void OnEntryModeChanged(object sender, SelectionChangedEventArgs e) => ApplyEntryMode();

    private void ApplyEntryMode()
    {
        if (DetailsPanel is null || ConnectionStringBox is null)
        {
            return;
        }

        var useRaw = EntryModeBox.SelectedIndex == 1;
        DetailsPanel.Visibility = useRaw ? Visibility.Collapsed : Visibility.Visible;
        ConnectionStringBox.Visibility = useRaw ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAuthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SqlAuthPanel is null)
        {
            return;
        }

        SqlAuthPanel.Visibility = AuthBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // AuthBox item order: 0 = Windows, 1 = SQL Server, 2 = Microsoft Entra MFA.
    private static int AuthToIndex(AuthMode auth) => auth switch
    {
        AuthMode.SqlLogin => 1,
        AuthMode.EntraMfa => 2,
        _ => 0,
    };

    private static AuthMode IndexToAuth(int index) => index switch
    {
        1 => AuthMode.SqlLogin,
        2 => AuthMode.EntraMfa,
        _ => AuthMode.Windows,
    };

    /// <summary>Pushes the form back into the shared settings object.</summary>
    public void Commit()
    {
        if (EntryModeBox.SelectedIndex == 1)
        {
            // Connection-string mode is authoritative: record only the pasted string and
            // leave the form fields cleared so nothing stale feeds the connection.
            _settings.Reset();
            _settings.UseConnectionString = true;
            _settings.RawConnectionString = ConnectionStringBox.Text.Trim();
            // Connection-string mode: skip the recent list — the pasted string may embed a
            // password, and it is not a form target worth suggesting back.
            return;
        }

        _settings.Server = ServerBox.Text.Trim();
        _settings.Database = DatabaseBox.Text.Trim();
        _settings.Auth = IndexToAuth(AuthBox.SelectedIndex);
        _settings.UserId = UserBox.Text.Trim();
        _settings.Password = PasswordBox.Password;
        _settings.Encrypt = EncryptBox.IsChecked == true;
        _settings.TrustServerCertificate = TrustCertBox.IsChecked == true;
        _settings.RawConnectionString = string.Empty;
        _settings.UseConnectionString = false;

        // Remember this target (server / database / login / auth — never the password).
        // Record() no-ops on a blank server and caps the list at 10.
        _recent.Record(new RecentConnection(
            _settings.Server, _settings.Database, _settings.UserId, _settings.Auth));
    }

    private async void OnTestConnection(object sender, RoutedEventArgs e)
    {
        Commit();
        TestButton.IsEnabled = false;
        TestResult.Text = "Connecting…";

        try
        {
            TestResult.Text = await _capture.TestConnectionAsync(_settings);
        }
        catch (PlanCaptureException ex)
        {
            TestResult.Text = ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }
}
