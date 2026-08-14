using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SqlPlanViz.Capture;
using SqlPlanViz.Common;
using SqlPlanViz.Controls;
using SqlPlanViz.Diagnostics;
using SqlPlanViz.Diagnostics.Rules;
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
    private CancellationTokenSource? _objectContextCancellation;
    private string? _lastCapturedQuery;
    private CaptureMode _lastCaptureMode;

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

    public ConnectionSettings Connection { get; } = new();

    public bool HasPlan => Plan is not null;

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

    public void ActivateSessionPlan(SessionPlanItem item)
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

        // Open on the statement that actually costs something — in a batch of ten, nine
        // are usually trivial and the tenth is why you opened the plan.
        SelectedStatement = plan.Statements
            .OrderByDescending(s => s.Summary.TotalSubtreeCost)
            .First();

        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(HasNoPlan));
        OnPropertyChanged(nameof(HasMultipleStatements));
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(CanPersistTriage));
        OnPropertyChanged(nameof(CanUseRegressionBaseline));
        OnPropertyChanged(nameof(AnnotationHint));
    }

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
