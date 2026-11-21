using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Capture;
using SqlPlanViz.Editing;
using SqlPlanViz.Themes;

namespace SqlPlanViz.Views;

/// <summary>
/// The guard on the actual run (live-plan-editor-plan.md Phase 7), in the Gauge dialog.
///
/// The plan is explicit that the guard is the point of the phase and that it should err
/// toward too much friction. So: the connected server, database and login are named in the
/// dialog's header, every modifying statement is listed with its line rather than summarised,
/// and the confirmation is a tick box the user has to set before the run button will even
/// enable — a deliberate second action, and never the default one.
/// </summary>
public sealed partial class ConfirmRunDialog : UserControl
{
    private readonly BatchSafetyReport _report;
    private readonly string _target;
    private readonly string _login;

    public ConfirmRunDialog(ConnectionSettings connection, BatchSafetyReport report)
    {
        InitializeComponent();
        _report = report;
        _target = connection.DescribeTarget();
        _login = connection.Auth switch
        {
            AuthMode.SqlLogin => connection.UserId,
            AuthMode.EntraMfa => string.IsNullOrWhiteSpace(connection.UserId) ? "your Entra account" : connection.UserId,
            _ => Environment.UserName,
        };

        var tokens = GaugeTokens.For(ThemeService.ActualTheme);

        // The headline is a severity word first: crit when anything will change, neutral when
        // the batch only reads.
        var risky = report.Risky;
        HeadlineText.FontSize = 12.5;
        HeadlineText.FontWeight = FontWeights.SemiBold;
        if (report.ParseFailed)
        {
            HeadlineText.Text = "The batch did not parse, so what it would do is unknown";
            HeadlineText.Foreground = new SolidColorBrush(tokens.Crit);
        }
        else if (risky.Count > 0)
        {
            HeadlineText.Text = report.WorstRisk == StatementRisk.Modifying
                ? $"{risky.Count} statement{(risky.Count == 1 ? string.Empty : "s")} will modify rows"
                : report.Headline.TrimEnd('.');
            HeadlineText.Foreground = new SolidColorBrush(tokens.Crit);
        }
        else
        {
            HeadlineText.Text = "This batch only reads";
            HeadlineText.Foreground = new SolidColorBrush(tokens.Fg2);
        }

        // Each risky statement as a row: "Ln 7" then the statement in mono.
        for (var i = 0; i < risky.Count; i++)
        {
            var statement = risky[i];
            var row = new Grid
            {
                Padding = new Thickness(10, 6, 10, 6),
                ColumnSpacing = 10,
                BorderBrush = new SolidColorBrush(tokens.Line),
                BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = $"Ln {statement.Line}",
                FontFamily = GaugeFonts.Mono,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(tokens.Fg2),
            });

            var text = new TextBlock
            {
                Text = string.IsNullOrEmpty(statement.Target) ? $"{statement.Kind} …" : $"{statement.Kind} {statement.Target} …",
                FontFamily = GaugeFonts.Mono,
                FontSize = 12,
                Foreground = new SolidColorBrush(tokens.Fg0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            StatementsHost.Items.Add(row);
        }

        StatementsCard.Visibility = risky.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ConfirmText.Text = report.IsReadOnly
            ? $"Yes, run this read-only batch against {_target}."
            : $"I understand this will change {_target}.";
    }

    /// <summary>Raised when the tick box changes, so the host can enable or disable its run button.</summary>
    public event EventHandler<bool>? ConfirmationChanged;

    public bool IsConfirmed => ConfirmCheck.IsChecked == true;

    /// <summary>Wording for the dialog's primary button.</summary>
    public string RunButtonText => "Run and capture";

    /// <summary>
    /// The dialog header's subtitle: "This will execute against <c>sql-dev01 · SalesDb</c> as
    /// <c>khai@corp</c>." with the target and login in mono fg-0.
    /// </summary>
    public UIElement Subtitle
    {
        get
        {
            var tokens = GaugeTokens.For(ThemeService.ActualTheme);
            var block = new TextBlock { Style = (Style)Application.Current.Resources["GaugeDialogSubtitle"] };
            block.Inlines.Add(new Run { Text = "This will execute against " });
            block.Inlines.Add(new Run { Text = _target, FontFamily = GaugeFonts.Mono, FontSize = 12, Foreground = new SolidColorBrush(tokens.Fg0) });
            block.Inlines.Add(new Run { Text = " as " });
            block.Inlines.Add(new Run { Text = _login, FontFamily = GaugeFonts.Mono, FontSize = 12, Foreground = new SolidColorBrush(tokens.Fg0) });
            block.Inlines.Add(new Run { Text = "." });
            return block;
        }
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e) =>
        ConfirmationChanged?.Invoke(this, IsConfirmed);
}
