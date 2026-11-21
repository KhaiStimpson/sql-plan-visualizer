using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Capture;
using SqlPlanViz.Controls;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Editing;
using SqlPlanViz.Editing.Completion;
using SqlPlanViz.Model;
using SqlPlanViz.Sql;
using SqlPlanViz.Themes;
using SqlPlanViz.ViewModels;
using SqlPlanViz.Views;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace SqlPlanViz;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        // x:Bind expressions are initialized while InitializeComponent builds the page.
        // The binding source must exist before that generated code dereferences it.
        ViewModel = new MainViewModel();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Canvas.PlaybackChanged += OnPlaybackChanged;
        Canvas.FocusChanged += OnFocusChanged;
        Canvas.SearchRequested += OnSearchRequested;
        SetUpEditor();
        SetViewMode(ViewMode.Tree);
        ThemePicker.SelectedIndex = (int)ThemeService.Preference;
        UpdateMetricAvailability();

        // Code-built text (the narrative) and data-bound row brushes take literal Gauge
        // colours, so they are re-evaluated when the theme changes.
        ActualThemeChanged += (_, _) =>
        {
            RenderNarrative();
            foreach (var finding in ViewModel.Findings)
            {
                finding.RefreshTheme();
            }

            foreach (var ranked in ViewModel.RankedOperators)
            {
                ranked.RefreshTheme();
            }
        };

        // Start on the canvas rather than letting the first title-bar control take focus.
        Loaded += (_, _) => Canvas.Focus(FocusState.Pointer);
        RefreshConnectionProfiles();
    }

    /// <summary>Completions come from here; Phase 6 registers the catalog and tuning providers on it.</summary>
    private readonly CompletionEngine _completionEngine = new();

    private readonly PlanObjectProvider _planObjectProvider = new();

    private readonly CatalogProvider _catalogProvider = new();

    private readonly TuningProvider _tuningProvider = new();

    /// <summary>Stops the editor and the view model from echoing each other's text updates.</summary>
    private bool _syncingEditorText;

    private void SetUpEditor()
    {
        _completionEngine.Register(new KeywordProvider());
        _completionEngine.Register(_planObjectProvider);
        _completionEngine.Register(_catalogProvider);
        _completionEngine.Register(_tuningProvider);
        SqlEditor.CompletionEngine = _completionEngine;

        Parameters.Bind(ViewModel.Editor);
        Parameters.BindingsChanged += (_, _) =>
        {
            ApplyTableTypeShapes();
            UpdateEditorStatus();
        };

        _parameterRefresh.Tick += (_, _) =>
        {
            _parameterRefresh.Stop();
            ViewModel.Editor.RefreshParameters();
            UpdateEditorStatus();
        };

        ViewModel.Editor.PropertyChanged += OnEditorPropertyChanged;
        ViewModel.Editor.StaleChanged += (_, _) => UpdateTuningSurfaces();
        ViewModel.TuningSession.Changed += (_, _) => UpdateTuningSurfaces();

        SqlEditor.TextChanged += OnEditorTextChanged;
        SqlEditor.CaretMoved += (_, _) => UpdateEditorStatus();
        SqlEditor.GutterMarkClicked += OnGutterMarkClicked;

        // The IME candidate window is positioned in screen coordinates; without this it can
        // only anchor to the window rather than to the caret.
        SqlEditor.ClientToScreen = ClientRectToScreen;

        EditorSplitter.TargetRow = EditorPaneRow;
        EditorSplitter.MinimumHeight = 0;
        EditorSplitter.Resized += (_, height) => EditorPane.Visibility =
            height < 8 ? Visibility.Collapsed : Visibility.Visible;

        // Ctrl+Enter re-plans from anywhere in the page, matching the toolbar button.
        var replan = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        replan.Invoked += (sender, args) =>
        {
            args.Handled = true;
            _ = ReplanAsync();
        };
        KeyboardAccelerators.Add(replan);

        // Shift+Alt+F formats from anywhere in the page, matching the toolbar button.
        var format = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Shift | VirtualKeyModifiers.Menu };
        format.Invoked += (_, args) =>
        {
            args.Handled = true;
            FormatSql();
        };
        KeyboardAccelerators.Add(format);

        UpdateEditorStatus();
        UpdateTuningSurfaces();
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_syncingEditorText)
        {
            return;
        }

        _syncingEditorText = true;
        try
        {
            ViewModel.Editor.Text = SqlEditor.Text;
        }
        finally
        {
            _syncingEditorText = false;
        }

        // Re-extraction is a parse, not a round trip, but it still does not need to run on
        // every keystroke — one pass once typing pauses keeps the strip current for free.
        _parameterRefresh.Stop();
        _parameterRefresh.Start();
        UpdateEditorStatus();
    }

    private readonly DispatcherTimer _parameterRefresh = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SqlEditorViewModel.Text))
        {
            UpdateSqlMapping();
        }

        if (e.PropertyName is nameof(SqlEditorViewModel.Squiggles) or nameof(SqlEditorViewModel.ErrorMessage))
        {
            ApplyEditorDecorations();
        }

        UpdateEditorStatus();
    }

    private void OnReplan(object sender, RoutedEventArgs e) => _ = ReplanAsync();

    private async Task ReplanAsync()
    {
        if (!ViewModel.Editor.CanReplan)
        {
            UpdateEditorStatus();
            return;
        }

        // Extraction is debounced, so force it current before composing: pressing Ctrl+Enter
        // within 400ms of typing a parameter must not send a batch missing its DECLARE.
        _parameterRefresh.Stop();
        ViewModel.Editor.RefreshParameters();

        await ViewModel.ReplanAsync();
        UpdateTuningSurfaces();
        UpdateEditorStatus();
    }

    private void OnCancelReplan(object sender, RoutedEventArgs e) => ViewModel.CancelReplan();

    /// <summary>
    /// The guarded actual run (docs/live-plan-editor-plan.md Phase 7). The confirmation is
    /// the point of the phase: the dialog names the server, names every modifying statement
    /// it found, and its run button stays disabled until the user ticks a box saying so. The
    /// close button is the default, so pressing Enter cancels.
    /// </summary>
    private async void OnRunActual(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Editor.CanReplan)
        {
            UpdateEditorStatus();
            return;
        }

        _parameterRefresh.Stop();
        ViewModel.Editor.RefreshParameters();

        var report = ViewModel.AnalyseBatchSafety();
        var view = new ConfirmRunDialog(ViewModel.Connection, report);

        var dialog = CreateDialog("Run for an actual plan?", view.Subtitle, view);
        dialog.PrimaryButtonText = view.RunButtonText;
        dialog.CloseButtonText = "Cancel";

        // Never make running the default action: Enter and Esc both back out.
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;

        view.ConfirmationChanged += (_, confirmed) => dialog.IsPrimaryButtonEnabled = confirmed;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !view.IsConfirmed)
        {
            return;
        }

        await ViewModel.RunActualAsync();
        UpdateTuningSurfaces();
        UpdateEditorStatus();
    }

    private void OnGutterMarkClicked(object? sender, GutterMark mark)
    {
        if (mark.NodeId is not int nodeId || ViewModel.SelectedStatement is not { } statement)
        {
            return;
        }

        ViewModel.SelectedNode = statement.AllNodes.FirstOrDefault(n => n.NodeId == nodeId);
    }

    private void ApplyEditorDecorations() =>
        SqlEditor.SetDecorations(squiggles: ViewModel.Editor.Squiggles);

    private void OnPinBaseline(object sender, RoutedEventArgs e)
    {
        ViewModel.PinBaseline();
        UpdateTuningSurfaces();
    }

    private void OnToggleInlineAnnotations(object sender, RoutedEventArgs e)
    {
        SqlEditor.ShowInlineAnnotations = AnnotationsToggle.IsChecked == true;
        SqlEditor.Redraw();
    }

    /// <summary>
    /// Refreshes the four Phase 5 surfaces at once: the cost bar, the canvas recolouring, the
    /// gutter marks and the inline annotations. They are computed together because they all
    /// describe the same diff, and letting them drift apart would be worse than any one of
    /// them being absent.
    /// </summary>
    private void UpdateTuningSurfaces()
    {
        var session = ViewModel.TuningSession;
        session.IsStale = ViewModel.Editor.IsStale;

        // The verdict is a word (Better / Worse) first, so it reads without the colour; the
        // colour (ok / crit) only reinforces it. Everything after the word stays fg-0.
        var parts = session.Headline.Split("  ·  ", 2);
        CostBarHeadline.Inlines.Clear();
        var verdict = new Microsoft.UI.Xaml.Documents.Run { Text = parts[0] };
        var verdictBrush = session.IsStale
            ? null
            : session.Direction switch
            {
                TuningDirection.Improved when session.HasComparison => "GaugeOkBrush",
                TuningDirection.Regressed when session.HasComparison => "GaugeCritBrush",
                _ => null,
            };
        if (verdictBrush is not null && ThemeBrush(verdictBrush) is { } themed)
        {
            verdict.Foreground = themed;
        }

        CostBarHeadline.Inlines.Add(verdict);
        if (parts.Length > 1)
        {
            CostBarHeadline.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = " · " + parts[1].Replace("  ·  ", " · ") });
        }

        CostBarDetail.Text = session.Detail;
        ToolTipService.SetToolTip(CostBarDetail, string.IsNullOrEmpty(session.Detail) ? null : session.Detail);
        PinBaselineButton.IsEnabled = session.Current is not null;

        // Gutter marks and annotations are inference and go silent when the text has moved on
        // — a mark pointing at a line that has since been edited is worse than no mark.
        var impacts = ViewModel.EditorLineImpacts();
        SqlEditor.SetDecorations(
            marks: SqlDeltaMapper.ToGutterMarks(impacts),
            annotations: SqlDeltaMapper.ToAnnotations(impacts),
            squiggles: ViewModel.Editor.Squiggles);
    }

    private void UpdateEditorStatus()
    {
        var editor = ViewModel.Editor;
        SqlEditor.IsReadOnly = editor.IsBusy;
        ReplanButton.IsEnabled = editor.CanReplan;
        RunActualButton.IsEnabled = editor.CanReplan && ViewModel.Connection.HasTarget;

        var (line, column) = SqlEditor.Document.PositionOf(SqlEditor.CaretOffset);
        var parts = new List<string> { $"Ln {line + 1}, Col {column + 1}" };

        if (editor.HasError)
        {
            parts.Add(editor.ErrorMessage!.Split(Environment.NewLine)[0]);
        }
        else if (!editor.AllParametersValid)
        {
            parts.Add("Some parameters need a valid value.");
        }
        else if (editor.IsStale)
        {
            parts.Add("Edited since the plan was captured — Ctrl+Enter to re-plan.");
        }
        else if (!string.IsNullOrEmpty(editor.StatusMessage))
        {
            parts.Add(editor.StatusMessage);
        }

        EditorStatusText.Text = string.Join(" · ", parts);
        EditorStatusText.Margin = new Thickness(SqlMappingLabel.Text.Length > 0 ? 4 : 0, 0, 0, 0);
        if (SqlMappingLabel.Text.Length > 0)
        {
            EditorStatusText.Text = "· " + EditorStatusText.Text;
        }
    }

    /// <summary>Best-effort client-to-screen mapping for the IME candidate window.</summary>
    private Windows.Foundation.Rect ClientRectToScreen(Windows.Foundation.Rect rect)
    {
        try
        {
            var transform = SqlEditor.TransformToVisual(null);
            var topLeft = transform.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
            var scale = XamlRoot?.RasterizationScale ?? 1.0;
            return new Windows.Foundation.Rect(topLeft.X * scale, topLeft.Y * scale, rect.Width * scale, rect.Height * scale);
        }
        catch (Exception)
        {
            return rect;
        }
    }

    public MainViewModel ViewModel { get; }

    private readonly ConnectionProfileStore _profileStore = new();
    private readonly PasswordVaultStore _profilePasswords = new();

    /// <summary>Saved connection profiles offered as one-click connect buttons in the empty state.</summary>
    public ObservableCollection<ConnectionProfile> ConnectionProfiles { get; } = new();

    private void RefreshConnectionProfiles()
    {
        ConnectionProfiles.Clear();
        foreach (var profile in _profileStore.Load())
        {
            ConnectionProfiles.Add(profile);
        }

        ProfilesPanel.Visibility = ConnectionProfiles.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnConnectProfile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ConnectionProfile profile)
        {
            return;
        }

        var password = profile.PasswordIsVaulted
            ? _profilePasswords.Retrieve(profile.Server, profile.UserId)
            : null;

        ViewModel.Connection.ApplyProfile(profile, password);
        ViewModel.NotifyConnectionChanged();
        _ = RefreshCatalogAsync(forceRefresh: false);
    }

    public IReadOnlyList<AntiPatternInfo> AntiPatterns => AntiPatternLibrary.All;

    /// <summary>x:Bind helper for the profiles table: "server · database".</summary>
    public static string ProfileTarget(string server, string database) =>
        string.IsNullOrWhiteSpace(database) ? server : $"{server} · {database}";

    /// <summary>x:Bind helper for the profiles table's authentication column.</summary>
    public static string ProfileAuth(AuthMode auth, bool passwordIsVaulted, bool isRawConnectionString) =>
        isRawConnectionString
            ? "Connection string"
            : auth switch
            {
                AuthMode.EntraMfa => "Microsoft Entra MFA",
                AuthMode.SqlLogin => passwordIsVaulted ? "SQL Server · saved password" : "SQL Server",
                _ => "Windows",
            };

    /// <summary>The element the host window hands to SetTitleBar for the custom caption.</summary>
    public UIElement TitleBar => AppTitleBar;

    /// <summary>Opens a plan by path — used for "Open with" and command-line invocation.</summary>
    public async Task OpenPathAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await LoadFileAsync(file);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Could not open {path}: {ex.Message}";
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SelectedStatement)
            or nameof(MainViewModel.RuntimeMetricsAvailable))
        {
            UpdateMetricAvailability();
            UpdateTabLabels();
            UpdateSqlMapping();

            // The canvas clears its selection when the statement changes, so the top finding's
            // operator is selected after it has taken the new statement.
            if (e.PropertyName is nameof(MainViewModel.SelectedStatement))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (ViewModel.SelectedNode is null && ViewModel.SelectedFinding?.PrimaryNode is { } node)
                    {
                        ViewModel.SelectedNode = node;
                    }
                });
            }
        }

        if (e.PropertyName is nameof(MainViewModel.Narrative))
        {
            RenderNarrative();
        }

        if (e.PropertyName is nameof(MainViewModel.Metric))
        {
            SyncMetricSelector();
        }
        if (e.PropertyName is nameof(MainViewModel.CurrentDiff))
        {
            Canvas.SetDiff(ViewModel.CurrentDiff);
            UpdateTuningSurfaces();
            if (ViewModel.CurrentDiff is not null)
            {
                DeltaTab.IsChecked = true;
            }
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedNode))
        {
            UpdateSqlMapping();
        }
    }

    private void UpdateSqlMapping()
    {
        if (!_syncingEditorText && SqlEditor.Text != ViewModel.Editor.Text)
        {
            _syncingEditorText = true;
            try
            {
                SqlEditor.Text = ViewModel.Editor.Text;
            }
            finally
            {
                _syncingEditorText = false;
            }
        }

        _planObjectProvider.Load(ViewModel.SelectedStatement);
        _tuningProvider.Load(ViewModel.SelectedStatement);

        // The mapper reads what is in the editor, so its offsets line up with what is on
        // screen even after the batch has been edited.
        var sql = SqlEditor.Text;
        if (ViewModel.SelectedNode is not { } node)
        {
            SqlEditor.ClearClauseHighlight();
            SetSqlMapping("Select an operator to highlight its likely clause", null, null, null);
            UpdateEditorStatus();
            return;
        }

        if (SqlNodeMapper.Map(sql, node) is not { } span)
        {
            // Exchanges and spools move rows around; no part of the statement is theirs.
            SqlEditor.ClearClauseHighlight();
            SqlEditor.SelectRange(0, 0);
            SetSqlMapping("No clause maps to ", null, null, node);
            UpdateEditorStatus();
            return;
        }

        // The clause gets the accent-dim row wash; the caret moves to its end without
        // selecting it, so the code keeps its syntax colours.
        SqlEditor.HighlightClause(span.Start, span.Length);
        SqlEditor.SelectRange(span.Start + span.Length, 0);
        if (span.Clause == SqlClauseSplitter.StatementKind)
        {
            SetSqlMapping("Likely source of ", null, null, node);
        }
        else
        {
            SetSqlMapping("Likely ", span.Clause, " clause for ", node);
        }

        UpdateEditorStatus();
    }

    /// <summary>"Likely <c>ORDER BY</c> clause for <u>Sort</u>": the clause in mono, the operator as an entity link.</summary>
    private void SetSqlMapping(string lead, string? clause, string? join, PlanNode? node)
    {
        // TextBlocks drop edge whitespace, so the gaps are margins on the clause and entity.
        SqlMappingLabel.Text = lead.Trim();
        SqlMappingClause.Text = clause ?? string.Empty;
        SqlMappingClause.Visibility = clause is null ? Visibility.Collapsed : Visibility.Visible;
        SqlMappingJoin.Text = join?.Trim() ?? string.Empty;
        SqlMappingEntity.Content = node?.PhysicalOp;
        SqlMappingEntity.Tag = node;
        SqlMappingEntity.Visibility = node is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSqlMappingEntity(object sender, RoutedEventArgs e)
    {
        if (SqlMappingEntity.Tag is PlanNode node)
        {
            SetViewMode(ViewMode.Tree);
            Canvas.BringIntoView(node);
        }
    }

    private void OnFormatSql(object sender, RoutedEventArgs e) => FormatSql();

    /// <summary>
    /// Re-lays out the batch on demand. This is a text edit like any other, so it travels the
    /// normal TextChanged path: the view model, the parameter strip and the staleness flag all
    /// follow on their own, and one Ctrl+Z takes the whole reflow back.
    /// </summary>
    private void FormatSql()
    {
        if (string.IsNullOrWhiteSpace(SqlEditor.Text)
            || !SqlEditor.ReplaceAll(SqlFormatter.Format(SqlEditor.Text)))
        {
            return;
        }

        // A reflow moves every offset in the batch at once, so marks placed against the old
        // layout are now pointing at the wrong lines. Recomputing drops them until the next
        // re-plan, which is the same thing editing the text does.
        UpdateTuningSurfaces();
    }

    private void OnCopySql(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(SqlEditor.Text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(SqlEditor.Text);
        Clipboard.SetContent(package);
    }

    /// <summary>An estimated plan has no actual rows or timings, so those metrics are off.</summary>
    private void UpdateMetricAvailability()
    {
        var available = ViewModel.RuntimeMetricsAvailable;
        RowsMetricItem.IsEnabled = available;
        TimeMetricItem.IsEnabled = available;
        SelfTimeMetricItem.IsEnabled = available;
        EfficiencyMetricItem.IsEnabled = available;
        EstimateSkewMetricItem.IsEnabled = available;
        SyncMetricSelector();
    }

    /// <summary>Counts in the tab labels, so a spill three levels deep isn't invisible (§8).</summary>
    private void UpdateTabLabels()
    {
        FindingsTab.Content = ViewModel.FindingCount > 0
            ? $"Findings {ViewModel.FindingCount}"
            : "Findings";

        IndexesTab.Content = ViewModel.MissingIndexCount > 0
            ? $"Indexes {ViewModel.MissingIndexCount}"
            : "Indexes";

        WarningsTab.Content = ViewModel.WarningCount > 0
            ? $"Warnings {ViewModel.WarningCount}"
            : "Warnings";
    }

    private void SyncMetricSelector()
    {
        var index = ViewModel.Metric switch
        {
            SizeMetric.SubtreeCost => 0,
            SizeMetric.OperatorCost => 1,
            SizeMetric.ActualRows => 2,
            SizeMetric.ElapsedTime => 3,
            SizeMetric.SelfTime => 4,
            SizeMetric.Efficiency => 5,
            SizeMetric.EstimateSkew => 6,
            _ => 0,
        };

        if (MetricSelector is not null && MetricSelector.SelectedIndex != index)
        {
            MetricSelector.SelectedIndex = index;
        }
    }

    #region Command strip

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(".sqlplan");
        picker.FileTypeFilter.Add(".xml");

        // Unpackaged apps have no implicit window for the picker to parent to.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.WindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadFileAsync(file);
    }

    private async Task LoadFileAsync(StorageFile file)
    {
        try
        {
            var xml = await FileIO.ReadTextAsync(file);
            ViewModel.LoadFromXml(xml, file.Name, file.Path);
        }
        catch (Exception ex)
        {
            ViewModel.ErrorMessage = $"Could not read {file.Name}: {ex.Message}";
        }
    }

    private async void OnPasteXml(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 380,
            Width = 520,
            FontFamily = Themes.GaugeFonts.Mono,
            FontSize = 12,
            PlaceholderText = "Paste Showplan XML here…",
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(input, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(input, ScrollBarVisibility.Auto);

        // Pre-fill from the clipboard when it already holds something plausible.
        var clipboard = Clipboard.GetContent();
        if (clipboard.Contains(StandardDataFormats.Text))
        {
            var text = await clipboard.GetTextAsync();
            if (text.TrimStart().StartsWith('<'))
            {
                input.Text = text;
            }
        }

        var dialog = CreateDialog("Paste Showplan XML", "Paste the XML from SSMS (Show Execution Plan XML) or a .sqlplan file.", input);
        dialog.PrimaryButtonText = "Visualize";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.LoadFromXml(input.Text, "Pasted XML");
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var view = new ConnectView(ViewModel.Connection) { ConnectOnly = true };

        var dialog = CreateDialog("Connect to SQL Server", "Connect once, then re-plan edits and capture plans against this server.", view);
        dialog.PrimaryButtonText = "Connect";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        // Test connection sits in the footer, left of Cancel; the dialog stays open for it.
        dialog.SecondaryButtonText = "Test connection";
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await view.TestConnectionAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        view.Commit();
        RefreshConnectionProfiles();
        ViewModel.NotifyConnectionChanged();

        // Connecting without capturing is still a connection: the editor's completion and the
        // parser dialect are derived from the server, not from the plan, so they refresh here
        // exactly as they do on the capture path.
        await RefreshCatalogAsync(forceRefresh: false);
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        ViewModel.Disconnect();

        // ViewModel.Disconnect() drops its own snapshot; the providers hold their own copy of
        // it, so they have to be pushed back to empty or completion keeps offering the old
        // server's tables. The dialect stays where it is — the newest parser is the fallback
        // an unconnected editor uses anyway.
        _catalogProvider.Snapshot = CatalogSnapshot.Empty;
        _tuningProvider.Snapshot = CatalogSnapshot.Empty;
        ApplyTableTypeShapes();
        UpdateEditorStatus();
    }

    private async void OnCapture(object sender, RoutedEventArgs e)
    {
        var view = new ConnectView(ViewModel.Connection);

        var dialog = CreateDialog("Capture a plan from SQL Server", "Compile a query for an estimated plan, or run it for an actual one.", view);
        dialog.PrimaryButtonText = "Capture";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        // Test connection sits in the footer, left of Cancel; the dialog stays open for it.
        dialog.SecondaryButtonText = "Test connection";
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                await view.TestConnectionAsync();
            }
            finally
            {
                deferral.Complete();
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        view.Commit();
        RefreshConnectionProfiles();
        await ViewModel.CaptureAsync(view.Query, view.Mode);

        // One schema read per connection, on connect rather than on a keystroke, and the
        // parser dialect follows the server it will actually be sent to.
        await RefreshCatalogAsync(forceRefresh: false);
    }

    private async Task RefreshCatalogAsync(bool forceRefresh)
    {
        await ViewModel.LoadCatalogAsync(forceRefresh);
        _catalogProvider.Snapshot = ViewModel.CatalogSnapshot;
        _tuningProvider.Snapshot = ViewModel.CatalogSnapshot;
        ApplyTableTypeShapes();
        UpdateEditorStatus();

        try
        {
            var banner = await ViewModel.TestConnectionAsync();
            var version = TSqlParserFactory.FromServerVersion(banner);
            TSqlParserFactory.Default = version;
            ViewModel.Editor.ParserVersion = version;
            SqlEditor.ParserVersion = version;
        }
        catch (Exception)
        {
            // Highlighting and completion degrade to the newest dialect rather than breaking.
        }
    }

    private void OnRefreshCatalog(object sender, RoutedEventArgs e) => _ = RefreshCatalogAsync(forceRefresh: true);

    /// <summary>
    /// Shapes each table-valued parameter's row grid from sys.table_types, closing the loop
    /// the Phase 3 strip left open.
    /// </summary>
    private void ApplyTableTypeShapes()
    {
        foreach (var parameter in ViewModel.Editor.Parameters.Where(p => p.IsTableValued))
        {
            var columns = ViewModel.TableTypeColumns(parameter.TableTypeName);
            if (columns.Count > 0)
            {
                Parameters.SetTableTypeColumns(parameter.Name, parameter.TableTypeName, columns);
            }
        }
    }

    private void OnMetricChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.Metric = ((ComboBox)sender).SelectedIndex switch
        {
            1 => SizeMetric.OperatorCost,
            2 => SizeMetric.ActualRows,
            3 => SizeMetric.ElapsedTime,
            4 => SizeMetric.SelfTime,
            5 => SizeMetric.Efficiency,
            6 => SizeMetric.EstimateSkew,
            _ => SizeMetric.SubtreeCost,
        };
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) =>
        ViewModel.FilterText = SearchBox.Text ?? string.Empty;

    private void OnToggleColorMode(object sender, RoutedEventArgs e)
    {
        Canvas.ColorMode = Canvas.ColorMode == ColorMode.Blame ? ColorMode.Metric : ColorMode.Blame;
        UpdateColorModeButton();
    }

    /// <summary>Fires after a new statement loads, since PlanCanvas defaults ColorMode to Blame when findings exist.</summary>
    private void OnCanvasLayoutChanged(object? sender, EventArgs e) => UpdateColorModeButton();

    private void UpdateColorModeButton() =>
        BlameToggleButton.IsChecked = Canvas.ColorMode == ColorMode.Blame;

    private void OnZoomIn(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1.25f);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Canvas.ZoomBy(1 / 1.25f);

    private void OnFit(object sender, RoutedEventArgs e) => Canvas.FitToView();

    private void OnTogglePlayback(object sender, RoutedEventArgs e) => Canvas.TogglePlayback();

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        PlaybackButton.Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Glyph = Canvas.IsPlaybackRunning ? "\uE769" : "\uE768",
        };
        ToolTipService.SetToolTip(
            PlaybackButton,
            Canvas.IsPlaybackRunning ? "Stop playback" : "Play operators in execution order");
    }

    private void OnFocusChanged(object? sender, EventArgs e)
    {
        FocusBreadcrumbButton.Content = $"←  {Canvas.FocusBreadcrumb}";
        FocusBreadcrumbButton.Visibility = Canvas.IsFocused ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnExitFocus(object sender, RoutedEventArgs e) => Canvas.ExitFocus();

    private void OnSearchRequested(object? sender, EventArgs e) => SearchBox.Focus(FocusState.Keyboard);

    /// <summary>Whether the Tree view shows the flame view instead of the plan canvas.</summary>
    private bool _flameMode;

    /// <summary>
    /// The single setter of which centre view is visible: the canvas (or flame view), the
    /// ranked operator list, Query Store history or the anti-pattern library. Every rail item,
    /// toggle and "jump to" path comes through here so the views can never disagree.
    /// </summary>
    private void SetViewMode(ViewMode mode)
    {
        ViewModel.ViewMode = mode;

        // The rail's current item follows the view however it was reached.
        _syncingRail = true;
        try
        {
            RailTree.IsChecked = mode == ViewMode.Tree;
            RailRanked.IsChecked = mode == ViewMode.Ranked;
            RailHistory.IsChecked = mode == ViewMode.History;
            RailLibrary.IsChecked = mode == ViewMode.Library;
            TreeModeRadio.IsChecked = !_flameMode;
            FlameModeRadio.IsChecked = _flameMode;
        }
        finally
        {
            _syncingRail = false;
        }

        var tree = mode == ViewMode.Tree;
        Canvas.Visibility = tree && !_flameMode ? Visibility.Visible : Visibility.Collapsed;
        Flame.Visibility = tree && _flameMode ? Visibility.Visible : Visibility.Collapsed;
        OperatorListPane.Visibility = mode == ViewMode.Ranked ? Visibility.Visible : Visibility.Collapsed;
        QueryStorePane.Visibility = mode == ViewMode.History ? Visibility.Visible : Visibility.Collapsed;
        AntiPatternPane.Visibility = mode == ViewMode.Library ? Visibility.Visible : Visibility.Collapsed;

        if (mode == ViewMode.History)
        {
            _ = ViewModel.LoadQueryStoreAsync();
        }
    }

    private bool _syncingRail;

    private void OnRailItemChecked(object sender, RoutedEventArgs e)
    {
        if (!_syncingRail && sender is RadioButton { Tag: string tag } && Enum.TryParse<ViewMode>(tag, out var mode))
        {
            SetViewMode(mode);
        }
    }

    private void SetFlameMode(bool flame)
    {
        _flameMode = flame;
        SetViewMode(ViewMode.Tree);
    }

    private void OnToggleOperatorList(object sender, RoutedEventArgs e) =>
        SetViewMode(ViewModel.ViewMode == ViewMode.Ranked ? ViewMode.Tree : ViewMode.Ranked);

    private void OnToggleFlameView(object sender, RoutedEventArgs e) => SetFlameMode(!_flameMode);

    private void OnCanvasModeChecked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML raises Checked while the page is still being built.
        if (!_syncingRail && Canvas is not null && FlameModeRadio is not null)
        {
            SetFlameMode(sender == FlameModeRadio);
        }
    }

    private void OnBackToTree(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Tree);

    private void OnJumpToRankedOperator(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanNode node })
        {
            SetViewMode(ViewMode.Tree);
            ViewModel.SelectedNode = node;
            Canvas.BringIntoView(node);
        }
    }

    private void OnSelectSessionPlan(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SessionPlanItem item })
        {
            ViewModel.ActivateSessionPlan(item);
        }
    }

    private void OnCompareLatest(object sender, RoutedEventArgs e) => ViewModel.CompareLatestPlans();

    private async void OnRerunAndCompare(object sender, RoutedEventArgs e) => await ViewModel.RerunAndCompareAsync();

    private void OnToggleQueryStore(object sender, RoutedEventArgs e) =>
        SetViewMode(ViewModel.ViewMode == ViewMode.History ? ViewMode.Tree : ViewMode.History);

    private void OnOpenQueryStorePlan(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryStorePlanEntry entry })
        {
            SetViewMode(ViewMode.Tree);
            ViewModel.OpenQueryStorePlan(entry);
        }
    }

    private void OnToggleAntiPatterns(object sender, RoutedEventArgs e) =>
        SetViewMode(ViewModel.ViewMode == ViewMode.Library ? ViewMode.Tree : ViewMode.Library);

    private async void OnSaveRegressionBaseline(object sender, RoutedEventArgs e) =>
        await ShowBaselineResultAsync("Regression baseline", ViewModel.SaveRegressionBaseline());

    private async void OnCheckRegressionBaseline(object sender, RoutedEventArgs e)
    {
        var result = ViewModel.CheckRegressionBaseline();
        await ShowBaselineResultAsync(result.Success ? "Baseline passed" : "Baseline failed", result.Message);
    }

    private async Task ShowBaselineResultAsync(string title, string message)
    {
        var dialog = CreateDialog(title, null, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.CloseButtonText = "Close";
        await dialog.ShowAsync();
    }

    private void OnDeltaSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            ViewModel.SortDiffDeltas(combo.SelectedIndex);
        }
    }

    private void OnJumpToDelta(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanNode node })
        {
            ViewModel.SelectedNode = node;
            Canvas.BringIntoView(node);
        }
    }

    private void OnTriageTried(object sender, RoutedEventArgs e) => SetTriage(sender, FixTriageState.Tried);
    private void OnTriageDidNotHelp(object sender, RoutedEventArgs e) => SetTriage(sender, FixTriageState.DidNotHelp);
    private void OnTriageFixed(object sender, RoutedEventArgs e) => SetTriage(sender, FixTriageState.Fixed);
    private void OnTriageReset(object sender, RoutedEventArgs e) => SetTriage(sender, FixTriageState.Untried);

    private void SetTriage(object sender, FixTriageState state)
    {
        if (sender is FrameworkElement { Tag: FindingItem item })
        {
            ViewModel.SetFindingTriage(item, state);
        }
    }

    /// <summary>A library entry is a plain collapsible section: the toggle shows its body below it.</summary>
    private void OnToggleLibraryEntry(object sender, RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Parent: StackPanel { Children: [_, UIElement body, ..] } } toggle)
        {
            var expanded = toggle.IsChecked == true;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (toggle.Content is StackPanel { Children: [FontIcon chevron, ..] })
            {
                chevron.Glyph = expanded ? "" : "";
            }
        }
    }

    /// <summary>x:Bind helper: Gauge's disabled treatment (45% opacity) for a non-control group.</summary>
    public static double EnabledOpacity(bool enabled) => enabled ? 1 : 0.45;

    /// <summary>Selecting a finding row selects its operator too, but focus stays in the list.</summary>
    private void OnSelectFinding(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FindingItem item })
        {
            ViewModel.SelectedFinding = item;
            if (item.PrimaryNode is { } node)
            {
                ViewModel.SelectedNode = node;
                Canvas.BringIntoView(node);
            }
        }
    }

    private void OnFindingEntity(object sender, RoutedEventArgs e) => OnSelectFinding(sender, e);

    private void OnFindingShowInPlan(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: FindingItem { PrimaryNode: { } node } })
        {
            ShowNodeInPlan(node);
        }
    }

    private void OnFindingOpenLibrary(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Library);

    private void OnVerbosityChecked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML raises Checked while the page is still being built.
        if (ViewModel is null || TerseRadio is null)
        {
            return;
        }

        ViewModel.ExplanationVerbosity = sender == TerseRadio
            ? ExplanationVerbosity.Terse
            : ExplanationVerbosity.Expansive;
    }

    private void OnToggleLabelDetail(object sender, RoutedEventArgs e)
    {
        Canvas.LabelDetail = Canvas.LabelDetail switch
        {
            LabelDetail.Full => LabelDetail.Standard,
            LabelDetail.Standard => LabelDetail.Minimal,
            _ => LabelDetail.Full,
        };
        LabelDetailButton.Content = Canvas.LabelDetail switch
        {
            LabelDetail.Full => "Detail: Full",
            LabelDetail.Standard => "Detail: Standard",
            _ => "Detail: Minimal",
        };
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        Canvas.CollapseAll();
        Canvas.FitToView();
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        Canvas.ExpandAll();
        Canvas.FitToView();
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => ViewModel.ErrorMessage = null;

    #endregion

    #region Detail pane

    private void OnInspectorTabChecked(object sender, RoutedEventArgs e)
    {
        // IsChecked="True" in XAML raises Checked while the page is still being built.
        if (sender is not RadioButton { Tag: string tag } || FindingsPane is null || DeltaPane is null)
        {
            return;
        }

        var index = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        FindingsPane.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        OperatorPane.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        IndexesPane.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        WarningsPane.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        DeltaPane.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopyIndexScript(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string script })
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(script);
        Clipboard.SetContent(package);
    }

    private void OnCollapseCheap(object sender, RoutedEventArgs e)
    {
        Canvas.CollapseCheapSubtrees();
        Canvas.FitToView();
    }

    private void OnFocusHotPath(object sender, RoutedEventArgs e)
    {
        Canvas.FocusHotPath();
        Canvas.FitToView();
    }

    private void OnCopyDiagnosis(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedStatement is not { } statement)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(PlanNarrative.GenerateMarkdown(statement, ViewModel.ExplanationVerbosity));
        Clipboard.SetContent(package);
    }

    private void OnJumpToWarning(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanNode node })
        {
            Canvas.BringIntoView(node);
        }
    }

    private void OnJumpToFinding(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanNode node })
        {
            ViewModel.SelectedNode = node;
            Canvas.BringIntoView(node);
        }
    }

    private void OnSaveAnnotation(object sender, RoutedEventArgs e) => ViewModel.SaveSelectedAnnotation();

    /// <summary>Expands or collapses a plain section in the Operator pane; the chevron follows.</summary>
    private void OnToggleOperatorSection(object sender, RoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Tag: string bodyName } toggle || FindName(bodyName) is not UIElement body)
        {
            return;
        }

        var expanded = toggle.IsChecked == true;
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (toggle.Content is StackPanel { Children: [FontIcon chevron, ..] })
        {
            chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        }
    }

    #endregion

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemePicker is { SelectedIndex: >= 0 } picker)
        {
            ThemeService.SetPreference((ThemePreference)picker.SelectedIndex);
        }
    }

    /// <summary>Renders the structured narrative: entity links, bold metrics, mono object names.</summary>
    private void RenderNarrative()
    {
        NarrativeText.Blocks.Clear();
        var tokens = GaugeTokens.For(ActualTheme);
        var paragraphs = NarrativeBuilder.Build(ViewModel.SelectedStatement, ViewModel.ExplanationVerbosity);
        for (var i = 0; i < paragraphs.Count; i++)
        {
            var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph
            {
                Margin = new Thickness(0, 0, 0, i == paragraphs.Count - 1 ? 0 : 6),
            };

            foreach (var run in paragraphs[i].Runs)
            {
                switch (run.Kind)
                {
                    case NarrativeRunKind.Entity when run.Node is { } node:
                        var link = new Microsoft.UI.Xaml.Documents.Hyperlink
                        {
                            Foreground = new SolidColorBrush(tokens.Fg0),
                            UnderlineStyle = Microsoft.UI.Xaml.Documents.UnderlineStyle.Single,
                        };
                        link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = run.Text });
                        link.Click += (_, _) => ShowNodeInPlan(node);
                        ToolTipService.SetToolTip(link, $"Show {node.PhysicalOp} (node {node.NodeId}) in the plan");
                        paragraph.Inlines.Add(link);
                        break;
                    case NarrativeRunKind.Bold:
                        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = run.Text,
                            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(tokens.Fg0),
                        });
                        break;
                    case NarrativeRunKind.Mono:
                        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                        {
                            Text = run.Text,
                            FontFamily = GaugeFonts.Mono,
                            FontSize = 12,
                            Foreground = new SolidColorBrush(tokens.Fg0),
                        });
                        break;
                    default:
                        paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = run.Text });
                        break;
                }
            }

            NarrativeText.Blocks.Add(paragraph);
        }
    }

    /// <summary>Selects an operator and brings it into view on the canvas.</summary>
    private void ShowNodeInPlan(PlanNode node)
    {
        SetViewMode(ViewMode.Tree);
        ViewModel.SelectedNode = node;
        Canvas.BringIntoView(node);
    }

    /// <summary>
    /// A dialog in the Gauge style: a 15px title with an fg-2 subtitle in the header. Dialogs
    /// open in the window's popup layer, outside this page, so the page's resolved theme is
    /// handed to them explicitly.
    /// </summary>
    private ContentDialog CreateDialog(string title, object? subtitle, object? content)
    {
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (subtitle is string text)
        {
            header.Children.Add(new TextBlock { Text = text, Style = (Style)Application.Current.Resources["GaugeDialogSubtitle"] });
        }
        else if (subtitle is UIElement element)
        {
            header.Children.Add(element);
        }

        return new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = header,
            Content = content,
            RequestedTheme = ActualTheme,
        };
    }

    /// <summary>A Gauge brush resolved against this page's current theme (code-built UI).</summary>
    private Brush? ThemeBrush(string key)
    {
        var tokens = GaugeTokens.For(ActualTheme);
        return key switch
        {
            "GaugeOkBrush" => new SolidColorBrush(tokens.Ok),
            "GaugeCritBrush" => new SolidColorBrush(tokens.Crit),
            "GaugeWarnBrush" => new SolidColorBrush(tokens.Warn),
            _ => null,
        };
    }

    /// <summary>Opens the command palette; wired up in Phase 9 of the Gauge plan.</summary>
    private void OnOpenPalette(object sender, RoutedEventArgs e)
    {
    }

    /// <summary>
    /// Controls inside the custom title bar are part of the drag region unless they are
    /// marked as passthrough, so the palette button gets its own non-client region.
    /// </summary>
    public void UpdateTitleBarPassthrough(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        if (XamlRoot is null || PaletteButton.ActualWidth <= 0)
        {
            return;
        }

        var scale = XamlRoot.RasterizationScale;
        var transform = PaletteButton.TransformToVisual(null);
        var bounds = transform.TransformBounds(new Windows.Foundation.Rect(0, 0, PaletteButton.ActualWidth, PaletteButton.ActualHeight));
        var rect = new Windows.Graphics.RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));

        Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(appWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, [rect]);
    }

    /// <summary>The inspector is minmax(340px, 29%) of the content width, as in the mock-up.</summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e) =>
        InspectorColumn.Width = new GridLength(Math.Max(340, Math.Round(e.NewSize.Width * 0.29)));

    #region Drag and drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open this plan";
        e.DragUIOverride.IsGlyphVisible = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.FirstOrDefault() is StorageFile file)
        {
            await LoadFileAsync(file);
        }
    }

    #endregion
}
