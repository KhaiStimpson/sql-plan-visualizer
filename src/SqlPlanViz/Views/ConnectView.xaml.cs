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
    private readonly IReadOnlyList<RecentConnection> _recentConnections;

    public ConnectView(ConnectionSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _recentConnections = _recent.Load();
        ServerBox.ItemsSource = DistinctServers(string.Empty);

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

    // --- Recent-connection suggestions -------------------------------------------------

    private void OnServerTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = DistinctServers(sender.Text);
        }
    }

    private void OnDatabaseTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = DistinctDatabases(ServerBox.Text, sender.Text);
        }
    }

    private void OnServerSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not string server)
        {
            return;
        }

        var match = _recentConnections.FirstOrDefault(
            c => string.Equals(c.Server, server, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return;
        }

        DatabaseBox.Text = match.Database;
        UserBox.Text = match.UserId;
        AuthBox.SelectedIndex = AuthToIndex(match.Auth);
    }

    private List<string> DistinctServers(string? term)
    {
        var needle = term?.Trim() ?? string.Empty;
        return _recentConnections
            .Select(c => c.Server)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => needle.Length == 0 || s.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<string> DistinctDatabases(string? server, string? term)
    {
        var host = server?.Trim() ?? string.Empty;
        var needle = term?.Trim() ?? string.Empty;
        return _recentConnections
            .Where(c => host.Length == 0 || string.Equals(c.Server, host, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Database)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => needle.Length == 0 || d.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

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
