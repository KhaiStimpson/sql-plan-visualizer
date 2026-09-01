using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Capture;
using SqlPlanViz.Common;
using SqlPlanViz.Controls;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Diagnostics.Rules;
using SqlPlanViz.Editing;
using SqlPlanViz.Model;
using SqlPlanViz.Parsing;

namespace SqlPlanViz.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlanCaptureService _capture = new();
    private readonly PlanAnnotationStore _annotationStore = new();
    private readonly FindingTriageStore _triageStore = new();
    private Dictionary<int, string> _annotations = [];
    private Dictionary<string, FixTriageState> _triage = new(StringComparer.OrdinalIgnoreCase);
    private string? _planSourcePath;
    private readonly DatabaseContextService _databaseContext = new();
    private readonly CatalogMetadataService _catalog = new();
    private CancellationTokenSource? _objectContextCancellation;
    private string? _lastCapturedQuery;
    private CaptureMode _lastCaptureMode;
    private CancellationTokenSource? _replanCancellation;

    /// <summary>
    /// False only while activating a plan the editor itself produced. Selecting a statement
    /// normally loads its text into the editor; a re-plan must not, or it would clobber the
    /// edit that caused it.
    /// </summary>
    private bool _syncEditorText = true;

    [ObservableProperty]
    private ExecutionPlan? _plan;

    [ObservableProperty]
    private PlanStatement? _selectedStatement;

    [ObservableProperty]
    private PlanNode? _selectedNode;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private SizeMetric _metric = SizeMetric.SubtreeCost;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private ExplanationVerbosity _explanationVerbosity = ExplanationVerbosity.Expansive;

    [ObservableProperty]
    private bool _rankOperatorsByDivergence;

    [ObservableProperty]
    private string? _busyMessage;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _selectedAnnotation = string.Empty;

    [ObservableProperty]
    private PlanDiffResult? _currentDiff;

    [ObservableProperty]
    private DatabaseObjectContext? _selectedObjectContext;

    [ObservableProperty]
    private bool _isLoadingObjectContext;

    [ObservableProperty]
    private string? _objectContextMessage;

    public ObservableCollection<PlanStatement> Statements { get; } = [];

    public ObservableCollection<IndexSuggestionItem> MissingIndexes { get; } = [];

    public ObservableCollection<WarningItem> Warnings { get; } = [];

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public ObservableCollection<OperatorRankItem> RankedOperators { get; } = [];

    public string RankedOperatorsTitle => RankOperatorsByDivergence
        ? "Operators ranked by cost-model divergence"
        : "Operators ranked by self time";

    public ObservableCollection<SessionPlanItem> SessionPlans { get; } = [];

    public ObservableCollection<PlanDeltaItem> DiffDeltas { get; } = [];

    public ObservableCollection<QueryStorePlanEntry> QueryStorePlans { get; } = [];

    /// <summary>The editor pane's state (live-plan-editor-plan.md Phase 4).</summary>
    public SqlEditorViewModel Editor { get; } = new();

    /// <summary>Pinned baseline and the diff every Phase 5 surface reads from.</summary>
    public TuningSession TuningSession { get; } = new();

    [ObservableProperty]
    private CatalogSnapshot _catalogSnapshot = CatalogSnapshot.Empty;

    [ObservableProperty]
    private string? _catalogMessage;

    [ObservableProperty]
    private bool _isLoadingCatalog;

    public bool CanRefreshCatalog => !string.IsNullOrWhiteSpace(Connection.Server) && !IsLoadingCatalog;

    /// <summary>
    /// Reads the connected database's schema for the completion providers
    /// (live-plan-editor-plan.md Phase 6). One round trip, cached per connection; the manual
    /// refresh exists because schemas change under a long-lived session.
    /// </summary>
    public async Task LoadCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Connection.Server))
        {
            CatalogSnapshot = CatalogSnapshot.Empty;
            CatalogMessage = "Connect to a server to complete real tables and columns.";
            return;
        }

        IsLoadingCatalog = true;
        OnPropertyChanged(nameof(CanRefreshCatalog));
        try
        {
            CatalogSnapshot = await _catalog.GetAsync(Connection, forceRefresh, cancellationToken).ConfigureAwait(true);
            CatalogMessage = CatalogSnapshot.IsEmpty
                ? "The connected database reported no user tables."
                : $"{CatalogSnapshot.Tables.Count} tables and views, {CatalogSnapshot.TableTypes.Count} table types.";
        }
        catch (PlanCaptureException ex)
        {
            // The catalog is an enhancement: without it the keyword and plan providers still
            // work, so a failure is a message and not an error bar.
            CatalogMessage = ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsLoadingCatalog = false;
            OnPropertyChanged(nameof(CanRefreshCatalog));
        }
    }

    /// <summary>The columns of a user table type, for a table-valued parameter's row grid (Phase 6).</summary>
    public IReadOnlyList<TvpColumn> TableTypeColumns(string tableTypeName) =>
        CatalogSnapshot.FindTableType(tableTypeName) is { } type
            ? [.. type.Columns.Select(c => new TvpColumn { Name = c.Name, DataType = c.DataType })]
            : [];

    public ConnectionSettings Connection { get; } = new();

    public bool HasPlan => Plan is not null;

    /// <summary>Command-strip readout of the live connection; raised by <see cref="NotifyConnectionChanged"/>.</summary>
    public string ConnectionDescription => Connection.Describe();

    public bool IsConnected => !string.IsNullOrWhiteSpace(Connection.Server);

    public bool HasSessionPlans => SessionPlans.Count > 0;

    public bool CanCompare => SessionPlans.Count >= 2;

    public bool CanRerun => !string.IsNullOrWhiteSpace(_lastCapturedQuery)
                            && !string.IsNullOrWhiteSpace(Connection.Server)
                            && !IsBusy;

    public bool HasDiff => CurrentDiff is not null;

    public bool CanBrowseQueryStore => !string.IsNullOrWhiteSpace(Connection.Server);

    public bool HasQueryStorePlans => QueryStorePlans.Count > 0;

    [ObservableProperty]
    private bool _isLoadingQueryStore;

    [ObservableProperty]
    private string? _queryStoreMessage;

    public bool HasNoPlan => Plan is null;

    public bool HasMultipleStatements => Statements.Count > 1;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public NodeDetail? SelectedDetail =>
        SelectedNode is { } n && SelectedStatement is { } s ? new NodeDetail(n, s) : null;

    public bool HasSelection => SelectedNode is not null;

    public bool HasNoSelection => SelectedNode is null;

    public bool CanAnnotate => SelectedNode is not null && !string.IsNullOrWhiteSpace(_planSourcePath);

    public bool CanPersistTriage => !string.IsNullOrWhiteSpace(_planSourcePath);

    public bool CanUseRegressionBaseline => SelectedStatement is not null && !string.IsNullOrWhiteSpace(_planSourcePath);

    public bool HasObjectContext => SelectedObjectContext is not null;

    public bool HasNoObjectContext => SelectedObjectContext is null && !IsLoadingObjectContext;

    public string AnnotationHint => string.IsNullOrWhiteSpace(_planSourcePath)
        ? "Save the plan to a file before adding persistent annotations."
        : $"Saved beside the plan as {Path.GetFileName(PlanAnnotationStore.SidecarPath(_planSourcePath))}";

    public string SourceLabel => Plan?.SourceName ?? "No plan loaded";

    public int WarningCount => Warnings.Count;

    public int MissingIndexCount => MissingIndexes.Count;

    public bool HasWarnings => Warnings.Count > 0;

    public bool HasMissingIndexes => MissingIndexes.Count > 0;

    public int FindingCount => Findings.Count;

    public bool HasFindings => Findings.Count > 0;

    public string Narrative => PlanNarrative.Generate(SelectedStatement, ExplanationVerbosity);

    /// <summary>Status-bar summary for the current statement.</summary>
    public string StatementSummary
    {
        get
        {
            if (SelectedStatement is not { } s)
            {
                return string.Empty;
            }

            var parts = new List<string>
            {
                $"{s.AllNodes.Count} operators",
                $"cost {Format.Cost(s.Summary.TotalSubtreeCost)}",
            };

            if (s.Summary.DegreeOfParallelism > 1)
            {
                parts.Add($"DOP {s.Summary.DegreeOfParallelism}");
            }

            if (s.Summary.QueryElapsedMs is double ms)
            {
                parts.Add($"elapsed {Format.Milliseconds(ms)}");
            }

            if (s.Summary.QueryCpuMs is double cpu)
            {
                parts.Add($"CPU {Format.Milliseconds(cpu)}");
            }

            parts.Add(s.HasRuntimeStats ? "actual plan" : "estimated plan");
            return string.Join("  ·  ", parts);
        }
    }

    public string PlanKindLabel => SelectedStatement?.HasRuntimeStats == true ? "Actual" : "Estimated";

    /// <summary>Metrics that need runtime data are meaningless on an estimated plan.</summary>
    public bool RuntimeMetricsAvailable => SelectedStatement?.HasRuntimeStats == true;

    public void LoadFromXml(string xml, string sourceName, string? sourcePath = null)
    {
        ErrorMessage = null;
        try
        {
            var plan = ShowplanParser.Parse(xml, sourceName);
            SetPlan(plan, sourcePath);
        }
        catch (ShowplanParseException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Call after the connection settings change without a capture (the standalone Connect
    /// flow), so the connection-derived surfaces re-evaluate against the new server.
    /// </summary>
    public void NotifyConnectionChanged()
    {
        OnPropertyChanged(nameof(CanRerun));
        OnPropertyChanged(nameof(CanBrowseQueryStore));
        OnPropertyChanged(nameof(ConnectionDescription));
        OnPropertyChanged(nameof(IsConnected));
    }

    /// <summary>Disconnect: reset the connection and drop every surface derived from it.</summary>
    public void Disconnect()
    {
        Connection.Reset();
        QueryStorePlans.Clear();
        SelectedObjectContext = null;
        QueryStoreMessage = null;
        OnPropertyChanged(nameof(HasQueryStorePlans));
        NotifyConnectionChanged();
    }

    public async Task CaptureAsync(string query, CaptureMode mode, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        IsBusy = true;
        BusyMessage = mode == CaptureMode.Actual
            ? "Running the query and capturing its actual plan…"
            : "Compiling the query for an estimated plan…";

        try
        {
            var plan = await _capture.CaptureAsync(Connection, query, mode, cancellationToken)
                .ConfigureAwait(true);
            _lastCapturedQuery = query;
            _lastCaptureMode = mode;
            SetPlan(plan, sourcePath: null);
            OnPropertyChanged(nameof(CanRerun));
            OnPropertyChanged(nameof(ConnectionDescription));
            OnPropertyChanged(nameof(IsConnected));
        }
        catch (PlanCaptureException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (ShowplanParseException ex)
        {
            ErrorMessage = $"SQL Server returned a plan that could not be parsed: {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Capture cancelled.";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
            OnPropertyChanged(nameof(CanRerun));
        }
    }

    public async Task RerunAndCompareAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRerun || _lastCapturedQuery is null)
        {
            return;
        }

        var previousCount = SessionPlans.Count;
        await CaptureAsync(_lastCapturedQuery, _lastCaptureMode, cancellationToken).ConfigureAwait(true);
        if (SessionPlans.Count == previousCount + 1 && SessionPlans.Count >= 2)
        {
            CompareLatestPlans();
        }
    }

    public async Task LoadQueryStoreAsync(CancellationToken cancellationToken = default)
    {
        QueryStorePlans.Clear();
        QueryStoreMessage = null;
        if (!CanBrowseQueryStore)
        {
            QueryStoreMessage = "Capture a live plan first to choose a database connection.";
            return;
        }

        IsLoadingQueryStore = true;
        try
        {
            foreach (var entry in await _databaseContext.GetQueryStoreHistoryAsync(Connection, cancellationToken).ConfigureAwait(true))
            {
                QueryStorePlans.Add(entry);
            }

            QueryStoreMessage = QueryStorePlans.Count == 0 ? "Query Store contains no captured plans." : null;
        }
        catch (PlanCaptureException ex)
        {
            QueryStoreMessage = ex.Message;
        }
        finally
        {
            IsLoadingQueryStore = false;
            OnPropertyChanged(nameof(HasQueryStorePlans));
        }
    }

    public void OpenQueryStorePlan(QueryStorePlanEntry entry)
    {
        LoadFromXml(entry.PlanXml, $"Query Store · query {entry.QueryId} · plan {entry.PlanId}");
    }

    public Task<string> TestConnectionAsync(CancellationToken cancellationToken = default) =>
        _capture.TestConnectionAsync(Connection, cancellationToken);

    private void SetPlan(ExecutionPlan plan, string? sourcePath)
    {
        var sessionItem = new SessionPlanItem { Plan = plan, SourcePath = sourcePath };
        SessionPlans.Add(sessionItem);
        ActivateSessionPlan(sessionItem);
        OnPropertyChanged(nameof(HasSessionPlans));
        OnPropertyChanged(nameof(CanCompare));
    }

    public void ActivateSessionPlan(SessionPlanItem item) => ActivateSessionPlan(item, null, null);

    /// <summary>
    /// Activates a plan, optionally keeping the caret on the statement the user was looking
    /// at. A re-plan of the same batch produces the same statements in the same order, so the
    /// index is usually right; the fingerprint is checked first because an edit that adds a
    /// statement would shift every index below it (Phase 4).
    /// </summary>
    public void ActivateSessionPlan(SessionPlanItem item, string? preferredFingerprint, int? preferredIndex)
    {
        CurrentDiff = null;
        var plan = item.Plan;
        Plan = plan;
        _planSourcePath = item.SourcePath;
        _annotations = new Dictionary<int, string>(_annotationStore.Load(item.SourcePath));
        _triage = _triageStore.Load(item.SourcePath);

        Statements.Clear();
        foreach (var s in plan.Statements)
        {
            Statements.Add(s);
        }

        SelectedStatement = ChooseStatement(plan, preferredFingerprint, preferredIndex);

        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasNoPlan));
        OnPropertyChanged(nameof(HasMultipleStatements));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanPersistTriage));
        OnPropertyChanged(nameof(CanUseRegressionBaseline));
        OnPropertyChanged(nameof(AnnotationHint));
    }

    /// <summary>
    /// Picks which statement to open on. Falling back to the costliest is the original rule:
    /// in a batch of ten, nine are usually trivial and the tenth is why you opened the plan.
    /// </summary>
    private static PlanStatement ChooseStatement(ExecutionPlan plan, string? preferredFingerprint, int? preferredIndex)
    {
        if (preferredFingerprint is { Length: > 0 })
        {
            var match = plan.Statements.FirstOrDefault(s =>
                string.Equals(s.Fingerprint, preferredFingerprint, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        if (preferredIndex is int index && index >= 0 && index < plan.Statements.Count)
        {
            return plan.Statements[index];
        }

        return plan.Statements.OrderByDescending(s => s.Summary.TotalSubtreeCost).First();
    }

    /// <summary>
    /// Compiles the edited batch and swaps in the plan it produces
    /// (live-plan-editor-plan.md Phase 4).
    ///
    /// Estimated only: SET SHOWPLAN_XML compiles without executing, so an edited DELETE is
    /// safe to press Ctrl+Enter on. The actual run is Phase 7 and is gated behind its own
    /// confirmation.
    /// </summary>
    public Task ReplanAsync(CancellationToken cancellationToken = default) =>
        CaptureFromEditorAsync(CaptureMode.EstimatedOnly, cancellationToken);

    /// <summary>
    /// Runs the edited batch and captures its actual plan
    /// (live-plan-editor-plan.md Phase 7). Reuses the Phase 4 pipeline unchanged — the
    /// difference between this and Ctrl+Enter is the capture mode and the confirmation the
    /// caller is required to have obtained first.
    /// </summary>
    public Task RunActualAsync(CancellationToken cancellationToken = default) =>
        CaptureFromEditorAsync(CaptureMode.Actual, cancellationToken);

    /// <summary>
    /// Classifies what the batch would do if executed. The caller shows this to the user and
    /// gets a deliberate confirmation before <see cref="RunActualAsync"/>.
    /// </summary>
    public BatchSafetyReport AnalyseBatchSafety()
    {
        var batch = Editor.Compose();

        // Classify the composed batch, prelude included: the DECLARE statements are part of
        // what will run, and a table-valued parameter's generated INSERTs are real INSERTs.
        return BatchSafetyAnalyzer.Analyse(batch.Text, Editor.ParserVersion);
    }

    private async Task CaptureFromEditorAsync(CaptureMode mode, CancellationToken cancellationToken)
    {
        if (!Editor.CanReplan)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Connection.Server))
        {
            Editor.ApplyError("Connect to a server before re-planning. Highlighting, completions and parameters work offline; compiling does not.");
            return;
        }

        var batch = Editor.Compose();
        if (!batch.IsValid)
        {
            Editor.ApplyError(string.Join(Environment.NewLine, batch.Errors));
            return;
        }

        // The statement the user is looking at, so the same one can be reselected afterwards.
        var preferredFingerprint = SelectedStatement?.Fingerprint;
        var preferredIndex = SelectedStatement is null ? null : (int?)Statements.IndexOf(SelectedStatement);

        _replanCancellation?.Cancel();
        _replanCancellation?.Dispose();
        _replanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _replanCancellation.Token;

        Editor.ClearError();
        Editor.IsBusy = true;
        Editor.StatusMessage = mode == CaptureMode.Actual
            ? "Running the edited batch and capturing its actual plan…"
            : "Compiling the edited batch…";
        ErrorMessage = null;
        OnPropertyChanged(nameof(CanCancelReplan));

        try
        {
            var plan = await _capture
                .CaptureAsync(Connection, batch.Text, mode, token)
                .ConfigureAwait(true);

            _lastCapturedQuery = batch.Text;
            _lastCaptureMode = mode;

            // Straight through the session-plan machinery, so a re-plan is a first-class
            // entry in history and stays inspectable and comparable like any other.
            var sessionItem = new SessionPlanItem { Plan = plan, SourcePath = null };
            SessionPlans.Add(sessionItem);

            // The editor's text is the source of this plan, not a thing to be overwritten by
            // it — activating normally would replace what the user just typed with the
            // statement text SQL Server echoed back.
            _syncEditorText = false;
            try
            {
                ActivateSessionPlan(sessionItem, preferredFingerprint, preferredIndex);
            }
            finally
            {
                _syncEditorText = true;
            }
            OnPropertyChanged(nameof(HasSessionPlans));
            OnPropertyChanged(nameof(CanCompare));

            Editor.MarkCaptured(Editor.Text);
            TuningSession.IsStale = false;
            TuningSession.SetCurrent(SelectedStatement);

            // The canvas already knows how to recolour by diff; this is the wire-up the plan
            // says is "mostly free", and it is.
            CurrentDiff = TuningSession.Diff;
        }
        catch (PlanCaptureException ex)
        {
            Editor.ApplyCaptureError(ex, batch);
        }
        catch (ShowplanParseException ex)
        {
            Editor.ApplyError($"SQL Server returned a plan that could not be parsed: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Editor.StatusMessage = mode == CaptureMode.Actual ? "Run cancelled." : "Re-plan cancelled.";
        }
        finally
        {
            Editor.IsBusy = false;
            OnPropertyChanged(nameof(CanRerun));
            OnPropertyChanged(nameof(CanCancelReplan));
        }
    }

    public bool CanCancelReplan => Editor.IsBusy;

    /// <summary>Re-anchors the comparison to the plan on screen, banking an improvement.</summary>
    public void PinBaseline()
    {
        TuningSession.PinCurrent();
        CurrentDiff = TuningSession.Diff;
    }

    /// <summary>
    /// Per-line impacts for the editor's gutter and annotations. Returns nothing when the
    /// text has moved on from the plan: marks pointing at lines that have since been edited
    /// are worse than no marks.
    /// </summary>
    public IReadOnlyList<LineImpact> EditorLineImpacts() =>
        Editor.IsStale
            ? []
            : SqlDeltaMapper.Map(Editor.Text, TuningSession.Diff, Editor.ParserVersion);

    /// <summary>Cancels an in-flight re-plan. The plan explicitly requires captures to be cancellable.</summary>
    public void CancelReplan() => _replanCancellation?.Cancel();

    public void CompareLatestPlans()
    {
        if (SessionPlans.Count < 2)
        {
            return;
        }

        var beforeItem = SessionPlans[^2];
        var afterItem = SessionPlans[^1];
        ActivateSessionPlan(afterItem);
        var before = beforeItem.Plan.Statements.OrderByDescending(s => s.Summary.TotalSubtreeCost).First();
        var after = afterItem.Plan.Statements.OrderByDescending(s => s.Summary.TotalSubtreeCost).First();
        CurrentDiff = Diagnostics.PlanDiff.Compare(before, after);
    }

    public void SortDiffDeltas(int mode)
    {
        IEnumerable<PlanDeltaItem> sorted = mode switch
        {
            1 => DiffDeltas.OrderByDescending(item => Math.Abs(item.Delta.RowsDelta ?? 0)),
            2 => DiffDeltas.OrderBy(item => item.Delta.Kind),
            3 => DiffDeltas.OrderBy(item => item.Operator, StringComparer.CurrentCultureIgnoreCase),
            _ => DiffDeltas.OrderByDescending(item => item.Delta.CostDelta),
        };

        var snapshot = sorted.ToList();
        DiffDeltas.Clear();
        foreach (var item in snapshot)
        {
            DiffDeltas.Add(item);
        }
    }

    partial void OnSelectedStatementChanged(PlanStatement? value)
    {
        SelectedNode = null;

        // Parameter types and the plan-object completions always follow the selected
        // statement; the text does so only when the plan did not come from the editor.
        Editor.SetSourceStatement(value, replaceText: _syncEditorText);

        // Opening a plan starts a tuning session anchored on it, so the very first re-plan
        // already has something to be better or worse than.
        if (_syncEditorText)
        {
            TuningSession.Reset();
        }

        TuningSession.SetCurrent(value);

        MissingIndexes.Clear();
        Warnings.Clear();
        Findings.Clear();
        RebuildRankedOperators(value);

        if (value is not null)
        {
            foreach (var mi in value.MissingIndexes)
            {
                MissingIndexes.Add(new IndexSuggestionItem(mi));
            }

            foreach (var (node, warning) in value.AllWarnings.OrderByDescending(w => w.Warning.Severity))
            {
                Warnings.Add(new WarningItem(node, warning));
            }

            foreach (var finding in value.Findings)
            {
                Findings.Add(CreateFindingItem(finding, value, ExplanationVerbosity));
            }

            _ = VerifyIndexSuggestionsAsync();
        }

        // An estimated plan has no runtime numbers, so a runtime metric would colour
        // every node identically — fall back rather than showing a blank canvas.
        if (!RuntimeMetricsAvailable
            && Metric is SizeMetric.ActualRows or SizeMetric.ElapsedTime
                       or SizeMetric.Efficiency or SizeMetric.SelfTime or SizeMetric.EstimateSkew)
        {
            Metric = SizeMetric.SubtreeCost;
        }

        OnPropertyChanged(nameof(StatementSummary));
        OnPropertyChanged(nameof(PlanKindLabel));
        OnPropertyChanged(nameof(RuntimeMetricsAvailable));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(MissingIndexCount));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(HasMissingIndexes));
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(Narrative));
    }

    /// <summary>
    /// Ranks by self time (or estimated operator cost on an estimated plan) by default; by
    /// absolute cost-model divergence when <see cref="RankOperatorsByDivergence"/> is set
    /// (hot-path-plan.md Phase 2) — the "sorted by absolute delta" view onto the same list.
    /// </summary>
    private void RebuildRankedOperators(PlanStatement? statement)
    {
        RankedOperators.Clear();
        if (statement is null)
        {
            return;
        }

        var orderedNodes = RankOperatorsByDivergence
            ? statement.AllNodes.OrderByDescending(n =>
                CostModelDivergenceRule.ComputeShares(n, statement)?.Delta ?? -1)
            : statement.AllNodes.OrderByDescending(n => n.SelfTimeMs ?? n.EstimatedOperatorCost);

        var rank = 1;
        foreach (var node in orderedNodes)
        {
            RankedOperators.Add(new OperatorRankItem { Rank = rank++, Node = node, Statement = statement });
        }
    }

    partial void OnRankOperatorsByDivergenceChanged(bool value)
    {
        RebuildRankedOperators(SelectedStatement);
        OnPropertyChanged(nameof(RankedOperatorsTitle));
    }

    partial void OnSelectedNodeChanged(PlanNode? value)
    {
        SelectedAnnotation = value is not null && _annotations.TryGetValue(value.NodeId, out var annotation)
            ? annotation
            : string.Empty;
        OnPropertyChanged(nameof(SelectedDetail));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(CanAnnotate));
        _ = LoadObjectContextAsync(value);
    }

    private async Task LoadObjectContextAsync(PlanNode? node)
    {
        _objectContextCancellation?.Cancel();
        _objectContextCancellation?.Dispose();
        _objectContextCancellation = new CancellationTokenSource();
        var token = _objectContextCancellation.Token;

        SelectedObjectContext = null;
        ObjectContextMessage = node?.ObjectName is null
            ? "This operator is not tied to a database object."
            : string.IsNullOrWhiteSpace(Connection.Server)
                ? "Capture a plan from a live connection to load object context."
                : null;
        if (node?.ObjectName is null || string.IsNullOrWhiteSpace(Connection.Server))
        {
            IsLoadingObjectContext = false;
            return;
        }

        IsLoadingObjectContext = true;
        try
        {
            SelectedObjectContext = await _databaseContext
                .GetObjectContextAsync(Connection, node.ObjectName, token)
                .ConfigureAwait(true);
            ObjectContextMessage = SelectedObjectContext is null
                ? $"{node.ObjectName} was not found in the connected database."
                : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (PlanCaptureException ex)
        {
            ObjectContextMessage = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsLoadingObjectContext = false;
            }
        }
    }

    partial void OnSelectedObjectContextChanged(DatabaseObjectContext? value)
    {
        OnPropertyChanged(nameof(HasObjectContext));
        OnPropertyChanged(nameof(HasNoObjectContext));
    }

    partial void OnIsLoadingObjectContextChanged(bool value) => OnPropertyChanged(nameof(HasNoObjectContext));

    private async Task VerifyIndexSuggestionsAsync()
    {
        if (MissingIndexes.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Connection.Server))
        {
            foreach (var item in MissingIndexes)
            {
                item.MarkUnavailable("No live connection is available; DDL is hidden until the suggestion can be checked against sys.indexes.");
            }
            return;
        }

        var contexts = new Dictionary<string, DatabaseObjectContext?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in MissingIndexes)
        {
            item.IsChecking = true;
            item.VerificationText = "Checking table size and existing indexes…";
            try
            {
                if (!contexts.TryGetValue(item.DisplayTarget, out var context))
                {
                    context = await _databaseContext
                        .GetObjectContextAsync(Connection, item.DisplayTarget)
                        .ConfigureAwait(true);
                    contexts[item.DisplayTarget] = context;
                }

                item.ApplyVerification(context);
            }
            catch (PlanCaptureException ex)
            {
                item.MarkUnavailable($"Verification failed: {ex.Message} DDL is hidden.");
            }
        }
    }

    public void SaveSelectedAnnotation()
    {
        if (SelectedNode is not { } node || string.IsNullOrWhiteSpace(_planSourcePath))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedAnnotation))
        {
            _annotations.Remove(node.NodeId);
        }
        else
        {
            _annotations[node.NodeId] = SelectedAnnotation.Trim();
        }

        _annotationStore.Save(_planSourcePath, _annotations);
    }

    public string SaveRegressionBaseline()
    {
        if (SelectedStatement is null || string.IsNullOrWhiteSpace(_planSourcePath))
        {
            return "Open a plan file before saving a regression baseline.";
        }

        var path = PlanRegressionBaselineStore.PathFor(_planSourcePath);
        PlanRegressionBaselineStore.Save(SelectedStatement, path);
        return $"Saved baseline beside the plan as {Path.GetFileName(path)}.";
    }

    public RegressionCheckResult CheckRegressionBaseline()
    {
        if (SelectedStatement is null || string.IsNullOrWhiteSpace(_planSourcePath))
        {
            return new RegressionCheckResult(false, "Open a plan file before checking a regression baseline.");
        }

        return PlanRegressionBaselineStore.Check(SelectedStatement, PlanRegressionBaselineStore.PathFor(_planSourcePath));
    }

    partial void OnExplanationVerbosityChanged(ExplanationVerbosity value)
    {
        Findings.Clear();
        if (SelectedStatement is { } statement)
        {
            foreach (var finding in statement.Findings)
            {
                Findings.Add(CreateFindingItem(finding, statement, value));
            }
        }

        OnPropertyChanged(nameof(Narrative));
        OnPropertyChanged(nameof(FindingCount));
        OnPropertyChanged(nameof(HasFindings));
    }

    public void SetFindingTriage(FindingItem item, FixTriageState state)
    {
        item.TriageState = state;
        if (state == FixTriageState.Untried)
        {
            _triage.Remove(item.PersistenceKey);
        }
        else
        {
            _triage[item.PersistenceKey] = state;
        }

        if (!string.IsNullOrWhiteSpace(_planSourcePath))
        {
            _triageStore.Save(_planSourcePath, _triage);
        }
    }

    private FindingItem CreateFindingItem(PlanFinding finding, PlanStatement statement, ExplanationVerbosity verbosity)
    {
        var key = FindingItem.CreatePersistenceKey(finding);
        return new FindingItem(finding, statement, verbosity, _triage.GetValueOrDefault(key), CanPersistTriage);
    }

    partial void OnCurrentDiffChanged(PlanDiffResult? value)
    {
        DiffDeltas.Clear();
        if (value is not null)
        {
            foreach (var delta in value.Nodes
                         .Where(delta => delta.Kind != PlanDiffKind.Unchanged)
                         .OrderByDescending(delta => delta.CostDelta))
            {
                DiffDeltas.Add(new PlanDeltaItem { Delta = delta });
            }
        }

        OnPropertyChanged(nameof(HasDiff));
    }

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
}
