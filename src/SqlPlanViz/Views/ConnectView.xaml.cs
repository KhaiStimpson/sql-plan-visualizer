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
    private readonly PasswordVaultStore _passwords = new();
    private readonly ConnectionProfileStore _profiles = new();
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
        AuthIndex = AuthToIndex(settings.Auth);
        ConnectionStringBox.Text = settings.RawConnectionString;
        EntryModeIndex = settings.UseConnectionString ? 1 : 0;
        ApplyEntryMode();
        TryPrefillPassword(settings.Server, settings.UserId);
        RefreshProfiles();
    }

    private void RefreshProfiles() => ProfileBox.ItemsSource =
        _profiles.Load().Select(p => p.Name).ToList();

    /// <summary>
    /// If Windows Credential Manager holds a password for this server + login, fill
    /// <c>PasswordBox</c> and tick "Remember password" so a re-commit keeps it stored.
    /// </summary>
    private void TryPrefillPassword(string server, string userId)
    {
        var stored = _passwords.Retrieve(server, userId);
        if (stored is null)
        {
            return;
        }

        PasswordBox.Password = stored;
        RememberPasswordBox.IsChecked = true;
        UpdateForgetPasswordState();
    }

    /// <summary>Enables "Forget saved password" only when a vault entry exists for the current key.</summary>
    private void UpdateForgetPasswordState()
    {
        if (ForgetPasswordButton is null)
        {
            return;
        }

        ForgetPasswordButton.IsEnabled = _passwords.Has(ServerBox.Text, UserBox.Text);
    }

    private void OnUserTextChanged(object sender, TextChangedEventArgs e) => UpdateForgetPasswordState();

    private void OnForgetPassword(object sender, RoutedEventArgs e)
    {
        _passwords.Remove(ServerBox.Text, UserBox.Text);
        PasswordBox.Password = string.Empty;
        RememberPasswordBox.IsChecked = false;
        UpdateForgetPasswordState();
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
            QueryPanel.Visibility = visibility;
            ModePanel.Visibility = visibility;
        }
    }

    private bool _connectOnly;

    public string Query => QueryBox.Text;

    public CaptureMode Mode =>
        EstimatedModeRadio.IsChecked == true ? CaptureMode.EstimatedOnly : CaptureMode.Actual;

    /// <summary>0 = enter details, 1 = paste a connection string (the segmented control).</summary>
    private int EntryModeIndex
    {
        get => RawModeRadio?.IsChecked == true ? 1 : 0;
        set
        {
            DetailsModeRadio.IsChecked = value == 0;
            RawModeRadio.IsChecked = value == 1;
        }
    }

    /// <summary>0 = Windows, 1 = SQL Server, 2 = Microsoft Entra MFA (the segmented control).</summary>
    private int AuthIndex
    {
        get => SqlAuthRadio?.IsChecked == true ? 1 : EntraAuthRadio?.IsChecked == true ? 2 : 0;
        set
        {
            WindowsAuthRadio.IsChecked = value == 0;
            SqlAuthRadio.IsChecked = value == 1;
            EntraAuthRadio.IsChecked = value == 2;
        }
    }

    private void OnEntryModeChanged(object sender, RoutedEventArgs e) => ApplyEntryMode();

    // --- Recent-connection suggestions -------------------------------------------------

    private void OnServerTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = DistinctServers(sender.Text);
        }

        UpdateForgetPasswordState();
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
        AuthIndex = AuthToIndex(match.Auth);
        TryPrefillPassword(match.Server, match.UserId);
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
        if (DetailsPanel is null || ConnectionStringPanel is null)
        {
            return;
        }

        var useRaw = EntryModeIndex == 1;
        DetailsPanel.Visibility = useRaw ? Visibility.Collapsed : Visibility.Visible;
        ConnectionStringPanel.Visibility = useRaw ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAuthChanged(object sender, RoutedEventArgs e)
    {
        if (SqlAuthPanel is null)
        {
            return;
        }

        SqlAuthPanel.Visibility = AuthIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Authentication order: 0 = Windows, 1 = SQL Server, 2 = Microsoft Entra MFA.
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
        if (EntryModeIndex == 1)
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
        _settings.Auth = IndexToAuth(AuthIndex);
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

        // Opt-in password storage (Phase 5): only for SQL login, only when the box is checked.
        // Unchecked (or any other auth) removes any previously stored entry for this key.
        if (_settings.Auth == AuthMode.SqlLogin && RememberPasswordBox.IsChecked == true)
        {
            _passwords.Save(_settings.Server, _settings.UserId, _settings.Password);
        }
        else
        {
            _passwords.Remove(_settings.Server, _settings.UserId);
        }
    }

    // --- Named connection profiles (Phase 6) ------------------------------------------

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ProfileResult.Text = "Enter a profile name.";
            return;
        }

        Commit();
        _profiles.Save(BuildProfile(name));
        RefreshProfiles();
        ProfileResult.Text = $"Saved “{name}”.";
    }

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string current)
        {
            ProfileResult.Text = "Select a profile to rename.";
            return;
        }

        var newName = ProfileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            ProfileResult.Text = "Type the new name in the profile-name box first.";
            return;
        }

        _profiles.Rename(current, newName);
        RefreshProfiles();
        ProfileResult.Text = _profiles.Get(newName) is not null
            ? $"Renamed “{current}” to “{newName}”."
            : "Rename failed — that name is already in use.";
    }

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string current)
        {
            ProfileResult.Text = "Select a profile to delete.";
            return;
        }

        _profiles.Delete(current);
        RefreshProfiles();
        ProfileResult.Text = $"Deleted “{current}”.";
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not string name)
        {
            return;
        }

        var profile = _profiles.Get(name);
        if (profile is null)
        {
            return;
        }

        if (profile.IsRawConnectionString)
        {
            EntryModeIndex = 1;
            ConnectionStringBox.Text = profile.RawConnectionString;
            ApplyEntryMode();
            return;
        }

        EntryModeIndex = 0;
        ServerBox.Text = profile.Server;
        DatabaseBox.Text = profile.Database;
        UserBox.Text = profile.UserId;
        EncryptBox.IsChecked = profile.Encrypt;
        TrustCertBox.IsChecked = profile.TrustServerCertificate;
        AuthIndex = AuthToIndex(profile.Auth);
        ApplyEntryMode();

        if (profile.PasswordIsVaulted)
        {
            TryPrefillPassword(profile.Server, profile.UserId);
        }

        UpdateForgetPasswordState();
    }

    /// <summary>Snapshots the just-committed <see cref="_settings"/> as a named profile (no password).</summary>
    private ConnectionProfile BuildProfile(string name) => new(
        name,
        _settings.Server,
        _settings.Database,
        _settings.Auth,
        _settings.UserId,
        _settings.Encrypt,
        _settings.TrustServerCertificate,
        _settings.Auth == AuthMode.SqlLogin && RememberPasswordBox.IsChecked == true,
        _settings.UseConnectionString,
        _settings.RawConnectionString);

    /// <summary>
    /// Tests the form's connection; the dialog's footer button calls this. The result is a
    /// word first ("Connected in 0.4 s" in ok, or the error in crit), never colour alone.
    /// </summary>
    public async Task TestConnectionAsync()
    {
        Commit();
        var tokens = Themes.GaugeTokens.For(ActualTheme);
        TestResult.Visibility = Visibility.Visible;
        TestResult.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(tokens.Fg2);
        TestResult.Text = "Connecting…";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var banner = await _capture.TestConnectionAsync(_settings);
            TestResult.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(tokens.Ok);
            TestResult.Text = $"Connected in {stopwatch.Elapsed.TotalSeconds:0.0} s";
            ToolTipService.SetToolTip(TestResult, banner);
        }
        catch (PlanCaptureException ex)
        {
            TestResult.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(tokens.Crit);
            TestResult.Text = $"Could not connect: {ex.Message}";
        }
    }
}
