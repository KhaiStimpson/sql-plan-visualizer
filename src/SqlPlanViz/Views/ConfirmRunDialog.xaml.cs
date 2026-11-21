using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Capture;
using SqlPlanViz.Editing;

namespace SqlPlanViz.Views;

/// <summary>
/// The guard on the actual run (live-plan-editor-plan.md Phase 7).
///
/// The plan is explicit that the guard is the point of the phase and that it should err
/// toward too much friction. So: the connected server and database are the largest thing on
/// the dialog, every modifying statement is named with its line rather than summarised, and
/// the confirmation is a tick box the user has to set before the run button will even enable
/// — a deliberate second action, and never the default one.
/// </summary>
public sealed partial class ConfirmRunDialog : UserControl
{
    private readonly BatchSafetyReport _report;

    public ConfirmRunDialog(ConnectionSettings connection, BatchSafetyReport report)
    {
        InitializeComponent();
        _report = report;

        TargetText.Text = string.IsNullOrWhiteSpace(connection.Database)
            ? connection.Server
            : $"{connection.Server}  ·  {connection.Database}";

        AuthText.Text = connection.Auth == AuthMode.Windows
            ? "Windows authentication"
            : $"SQL login {connection.UserId}";

        HeadlineText.Text = report.Headline;

        var risky = report.Risky.Select(s => s.Describe()).ToList();
        if (report.ParseFailed)
        {
            risky.Add("The batch did not parse, so its statements could not be listed.");
        }

        StatementsHost.ItemsSource = risky;
        StatementsCard.Visibility = risky.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ConfirmText.Text = report.IsReadOnly
            ? $"Yes, run this read-only batch against {TargetText.Text}."
            : $"I understand this will change {TargetText.Text}.";

        var (glyph, brushKey) = report.WorstRisk switch
        {
            // Info, warning, warning, warning, caution — the glyph distinguishes the
            // read-only case from every other one without relying on colour alone.
            StatementRisk.ReadOnly => ("\uE946", "SystemFillColorNeutralBrush"),
            StatementRisk.Modifying => ("\uE7BA", "SystemFillColorCriticalBrush"),
            StatementRisk.Ddl => ("\uE7BA", "SystemFillColorCriticalBrush"),
            StatementRisk.Administrative => ("\uE7BA", "SystemFillColorCriticalBrush"),
            _ => ("\uE9CE", "SystemFillColorCautionBrush"),
        };

        RiskGlyph.Glyph = glyph;
        if (Application.Current.Resources.TryGetValue(brushKey, out var brush))
        {
            RiskGlyph.Foreground = (Brush)brush;
        }
    }

    /// <summary>Raised when the tick box changes, so the host can enable or disable its run button.</summary>
    public event EventHandler<bool>? ConfirmationChanged;

    public bool IsConfirmed => ConfirmCheck.IsChecked == true;

    /// <summary>Wording for the dialog's primary button — never just "OK".</summary>
    public string RunButtonText => _report.IsReadOnly
        ? "Run and capture the actual plan"
        : "Run anyway and change data";

    private void OnConfirmChanged(object sender, RoutedEventArgs e) =>
        ConfirmationChanged?.Invoke(this, IsConfirmed);
}
