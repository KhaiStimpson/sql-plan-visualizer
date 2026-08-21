using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SqlPlanViz.Capture;
using SqlPlanViz.Controls;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Editing;
using SqlPlanViz.Editing.Completion;
using SqlPlanViz.Model;
using SqlPlanViz.Sql;
using SqlPlanViz.ViewModels;
using SqlPlanViz.Views;
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
        UpdateMetricAvailability();
    }

    /// <summary>Completions come from here; Phase 6 registers the catalog and tuning providers on it.</summary>
    private readonly CompletionEngine _completionEngine = new();

    private readonly PlanObjectProvider _planObjectProvider = new();

    /// <summary>Stops the editor and the view model from echoing each other's text updates.</summary>
    private bool _syncingEditorText;

    private void SetUpEditor()
    {
        _completionEngine.Register(new KeywordProvider());
        _completionEngine.Register(_planObjectProvider);
        SqlEditor.CompletionEngine = _completionEngine;

        Parameters.Bind(ViewModel.Editor);
        Parameters.BindingsChanged += (_, _) => UpdateEditorStatus();

        _parameterRefresh.Tick += (_, _) =>
        {
            _parameterRefresh.Stop();
            ViewModel.Editor.RefreshParameters();
            UpdateEditorStatus();
        };

        ViewModel.Editor.PropertyChanged += OnEditorPropertyChanged;

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
        replan.Invoked += (_, args) =>
        {
            args.Handled = true;
            _ = ReplanAsync();
        };
        KeyboardAccelerators.Add(replan);

        UpdateEditorStatus();
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
        ApplyEditorDecorations();
        UpdateEditorStatus();
    }

    private void OnCancelReplan(object sender, RoutedEventArgs e) => ViewModel.CancelReplan();

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

    private void UpdateEditorStatus()
    {
        var editor = ViewModel.Editor;
        SqlEditor.IsReadOnly = editor.IsBusy;
        ReplanButton.IsEnabled = editor.CanReplan;

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

        EditorStatusText.Text = string.Join("  ·  ", parts);
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

    public IReadOnlyList<AntiPatternInfo> AntiPatterns => AntiPatternLibrary.All;

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
        }

        if (e.PropertyName is nameof(MainViewModel.Metric))
        {
            SyncMetricSelector();
        }
        if (e.PropertyName is nameof(MainViewModel.CurrentDiff))
        {
            Canvas.SetDiff(ViewModel.CurrentDiff);
            if (ViewModel.CurrentDiff is not null)
            {
                PaneSelector.SelectedItem = DeltaTab;
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

        // The mapper reads what is in the editor, so its offsets line up with what is on
        // screen even after the batch has been edited.
        var sql = SqlEditor.Text;
        if (ViewModel.SelectedNode is not { } node)
        {
            SqlMappingLabel.Text = "SQL · select an operator to highlight its likely clause";
            return;
        }

        if (SqlNodeMapper.Map(sql, node) is not { } span)
        {
            // Exchanges and spools move rows around; no part of the statement is theirs.
            SqlEditor.SelectRange(0, 0);
            SqlMappingLabel.Text = $"SQL · no clause maps to node {node.NodeId} ({node.PhysicalOp})";
            return;
        }

        SqlEditor.SelectRange(span.Start, span.Length);
        SqlMappingLabel.Text = span.Clause == SqlClauseSplitter.StatementKind
            ? $"SQL · likely source of node {node.NodeId}"
            : $"SQL · likely {span.Clause} clause for node {node.NodeId}";
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
        FindingsTab.Text = ViewModel.FindingCount > 0
            ? $"Findings ({ViewModel.FindingCount})"
            : "Findings";

        IndexesTab.Text = ViewModel.MissingIndexCount > 0
            ? $"Indexes ({ViewModel.MissingIndexCount})"
            : "Indexes";

        WarningsTab.Text = ViewModel.WarningCount > 0
            ? $"Warnings ({ViewModel.WarningCount})"
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

        if (MetricSelector.SelectedItem != MetricSelector.Items[index])
        {
            MetricSelector.SelectedItem = MetricSelector.Items[index];
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
            Width = 720,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
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

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Paste Showplan XML",
            Content = input,
            PrimaryButtonText = "Visualize",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.LoadFromXml(input.Text, "Pasted XML");
        }
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var view = new ConnectView(ViewModel.Connection);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Capture a plan from SQL Server",
            Content = view,
            PrimaryButtonText = "Capture",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        view.Commit();
        await ViewModel.CaptureAsync(view.Query, view.Mode);
    }

    private void OnMetricChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
        ViewModel.Metric = index switch
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

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ViewModel.FilterText = sender.Text ?? string.Empty;

    private void OnToggleColorMode(object sender, RoutedEventArgs e)
    {
        Canvas.ColorMode = Canvas.ColorMode == ColorMode.Blame ? ColorMode.Metric : ColorMode.Blame;
        UpdateColorModeButton();
    }

    /// <summary>Fires after a new statement loads, since PlanCanvas defaults ColorMode to Blame when findings exist.</summary>
    private void OnCanvasLayoutChanged(object? sender, EventArgs e) => UpdateColorModeButton();

    private void UpdateColorModeButton() =>
        BlameToggleButton.Content = Canvas.ColorMode == ColorMode.Blame ? "Blame ●" : "Blame";

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

    private void OnToggleOperatorList(object sender, RoutedEventArgs e)
    {
        var show = OperatorListPane.Visibility != Visibility.Visible;
        OperatorListPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnJumpToRankedOperator(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PlanNode node })
        {
            OperatorListPane.Visibility = Visibility.Collapsed;
            Canvas.Visibility = Visibility.Visible;
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

    private async void OnToggleQueryStore(object sender, RoutedEventArgs e)
    {
        var show = QueryStorePane.Visibility != Visibility.Visible;
        QueryStorePane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            await ViewModel.LoadQueryStoreAsync();
        }
    }

    private void OnOpenQueryStorePlan(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: QueryStorePlanEntry entry })
        {
            QueryStorePane.Visibility = Visibility.Collapsed;
            Canvas.Visibility = Visibility.Visible;
            ViewModel.OpenQueryStorePlan(entry);
        }
    }

    private void OnToggleAntiPatterns(object sender, RoutedEventArgs e)
    {
        var show = AntiPatternPane.Visibility != Visibility.Visible;
        AntiPatternPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Canvas.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnSaveRegressionBaseline(object sender, RoutedEventArgs e) =>
        await ShowBaselineResultAsync("Regression baseline", ViewModel.SaveRegressionBaseline());

    private async void OnCheckRegressionBaseline(object sender, RoutedEventArgs e)
    {
        var result = ViewModel.CheckRegressionBaseline();
        await ShowBaselineResultAsync(result.Success ? "Baseline passed" : "Baseline failed", result.Message);
    }

    private async Task ShowBaselineResultAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Close",
        };
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
        if (sender is Button { Tag: FindingItem item })
        {
            ViewModel.SetFindingTriage(item, state);
        }
    }

    private void OnToggleVerbosity(object sender, RoutedEventArgs e)
    {
        ViewModel.ExplanationVerbosity = ViewModel.ExplanationVerbosity == ExplanationVerbosity.Expansive
            ? ExplanationVerbosity.Terse
            : ExplanationVerbosity.Expansive;
        VerbosityButton.Content = ViewModel.ExplanationVerbosity == ExplanationVerbosity.Expansive
            ? "Detailed"
            : "Terse";
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

    private void OnDismissError(InfoBar sender, object args) => ViewModel.ErrorMessage = null;

    #endregion

    #region Detail pane

    private void OnPaneChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var index = sender.Items.IndexOf(sender.SelectedItem);
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

    #endregion

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
