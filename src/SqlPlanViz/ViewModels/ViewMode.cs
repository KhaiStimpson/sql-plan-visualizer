namespace SqlPlanViz.ViewModels;

/// <summary>What the centre pane shows; the left rail's four items (docs/gauge-design-system-plan.md Phase 3).</summary>
public enum ViewMode
{
    /// <summary>The plan canvas (or the flame view, which is a canvas mode within it).</summary>
    Tree,

    /// <summary>Operators ranked by self time (or by cost-model divergence).</summary>
    Ranked,

    /// <summary>Query Store plan history for the connected database.</summary>
    History,

    /// <summary>The named anti-pattern library.</summary>
    Library,
}
