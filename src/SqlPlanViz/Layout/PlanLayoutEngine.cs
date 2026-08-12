using SqlPlanViz.Model;

namespace SqlPlanViz.Layout;

public sealed class NodeLayout
{
    public required PlanNode Node { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    public required int Depth { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    /// <summary>True when this node has children that the layout deliberately omitted.</summary>
    public bool IsCollapsed { get; init; }

    public int HiddenDescendantCount { get; init; }

    public bool HasChildren => Node.Children.Count > 0;

    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterX => X + (Width / 2);

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public bool IntersectsViewport(double left, double top, double right, double bottom) =>
        X <= right && Right >= left && Y <= bottom && Bottom >= top;
}

public sealed class EdgeLayout
{
    public required NodeLayout Parent { get; init; }

    public required NodeLayout Child { get; init; }

    /// <summary>Rows flowing along this edge — actual where known, else estimated.</summary>
    public required double Rows { get; init; }

    public bool IsActual { get; init; }
}

public sealed class PlanLayout
{
    public required IReadOnlyList<NodeLayout> Nodes { get; init; }

    public required IReadOnlyList<EdgeLayout> Edges { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public double MaxEdgeRows { get; init; }

    private Dictionary<PlanNode, NodeLayout>? _byNode;

    /// <summary>
    /// Keyed by node identity rather than NodeId — a collapsed subtree's nodes are absent
    /// from the layout, and reference identity sidesteps any NodeId collisions between
    /// parallel branches.
    /// </summary>
    public NodeLayout? Find(PlanNode node)
    {
        if (_byNode is null)
        {
            _byNode = new Dictionary<PlanNode, NodeLayout>(Nodes.Count);
            foreach (var n in Nodes)
            {
                _byNode[n.Node] = n;
            }
        }

        return _byNode.GetValueOrDefault(node);
    }

    /// <summary>Topmost node containing the point, or null. Used for click hit-testing.</summary>
    public NodeLayout? HitTest(double x, double y)
    {
        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            if (Nodes[i].Contains(x, y))
            {
                return Nodes[i];
            }
        }

        return null;
    }
}

/// <summary>
/// Reingold–Tilford tree layout via Buchheim, Jünger &amp; Leipert's O(n) formulation
/// ("Improving Walker's Algorithm to Run in Linear Time", GD 2002).
///
/// Runs once per loaded plan and the positions are cached (TDD §8 performance point 5) —
/// pan, zoom, selection and filtering never re-run it. Only collapse/expand does, because
/// that genuinely changes the tree.
/// </summary>
public sealed class PlanLayoutEngine
{
    public double NodeWidth { get; init; } = 212;

    public double NodeHeight { get; init; } = 84;

    /// <summary>Gap between adjacent siblings (and between adjacent subtrees).</summary>
    public double SiblingGap { get; init; } = 26;

    /// <summary>Vertical gap between one level and the next.</summary>
    public double LevelGap { get; init; } = 62;

    public double Margin { get; init; } = 48;

    public PlanLayout Layout(PlanNode root, IReadOnlySet<int>? collapsedNodeIds = null)
    {
        collapsedNodeIds ??= new HashSet<int>();

        var tree = Build(root, collapsedNodeIds, depth: 0, parent: null, number: 1);

        FirstWalk(tree);
        SecondWalk(tree, -tree.Prelim, 0);

        var all = Flatten(tree).ToList();

        // Buchheim centres the root on zero, so shift everything into positive space.
        var minX = all.Min(t => t.X);
        var minY = all.Min(t => t.Y);
        var offsetX = Margin - minX;
        var offsetY = Margin - minY;

        var layouts = new Dictionary<TreeNode, NodeLayout>(all.Count);
        foreach (var t in all)
        {
            layouts[t] = new NodeLayout
            {
                Node = t.Plan,
                X = t.X + offsetX,
                Y = t.Y + offsetY,
                Depth = t.Depth,
                Width = NodeWidth,
                Height = NodeHeight,
                IsCollapsed = t.IsCollapsed,
                HiddenDescendantCount = t.HiddenDescendantCount,
            };
        }

        var edges = new List<EdgeLayout>();
        foreach (var t in all)
        {
            foreach (var c in t.Children)
            {
                // Rows on the edge are the rows the child produced, which is what flows up.
                var actual = c.Plan.ActualRows;
                edges.Add(new EdgeLayout
                {
                    Parent = layouts[t],
                    Child = layouts[c],
                    Rows = actual ?? c.Plan.EstimatedRowsTotal,
                    IsActual = actual.HasValue,
                });
            }
        }

        var nodes = layouts.Values.ToList();
        return new PlanLayout
        {
            Nodes = nodes,
            Edges = edges,
            Width = nodes.Max(n => n.Right) + Margin,
            Height = nodes.Max(n => n.Bottom) + Margin,
            MaxEdgeRows = edges.Count == 0 ? 0 : edges.Max(e => e.Rows),
        };
    }

    private TreeNode Build(PlanNode plan, IReadOnlySet<int> collapsed, int depth, TreeNode? parent, int number)
    {
        var isCollapsed = collapsed.Contains(plan.NodeId) && plan.Children.Count > 0;
        var node = new TreeNode
        {
            Plan = plan,
            Depth = depth,
            Parent = parent,
            Number = number,
            IsCollapsed = isCollapsed,
            HiddenDescendantCount = isCollapsed
                ? plan.DescendantsAndSelf().Count() - 1
                : 0,
        };

        node.Ancestor = node;

        if (!isCollapsed)
        {
            var i = 1;
            foreach (var childPlan in plan.Children)
            {
                node.Children.Add(Build(childPlan, collapsed, depth + 1, node, i++));
            }
        }

        return node;
    }

    private double Distance => NodeWidth + SiblingGap;

    private void FirstWalk(TreeNode v)
    {
        if (v.Children.Count == 0)
        {
            v.Prelim = v.LeftSibling is { } ls ? ls.Prelim + Distance : 0;
            return;
        }

        var defaultAncestor = v.Children[0];
        foreach (var w in v.Children)
        {
            FirstWalk(w);
            Apportion(w, ref defaultAncestor);
        }

        ExecuteShifts(v);

        var midpoint = (v.Children[0].Prelim + v.Children[^1].Prelim) / 2;
        if (v.LeftSibling is { } left)
        {
            v.Prelim = left.Prelim + Distance;
            v.Mod = v.Prelim - midpoint;
        }
        else
        {
            v.Prelim = midpoint;
        }
    }

    private void Apportion(TreeNode v, ref TreeNode defaultAncestor)
    {
        if (v.LeftSibling is not { } w)
        {
            return;
        }

        // "ip"/"im" = inner right/left contour, "op"/"om" = outer right/left contour.
        var vip = v;
        var vop = v;
        var vim = w;
        var vom = vip.LeftmostSibling!;

        var sip = vip.Mod;
        var sop = vop.Mod;
        var sim = vim.Mod;
        var som = vom.Mod;

        while (NextRight(vim) is { } nr && NextLeft(vip) is { } nl)
        {
            vim = nr;
            vip = nl;
            vom = NextLeft(vom)!;
            vop = NextRight(vop)!;
            vop.Ancestor = v;

            var shift = (vim.Prelim + sim) - (vip.Prelim + sip) + Distance;
            if (shift > 0)
            {
                MoveSubtree(Ancestor(vim, v, defaultAncestor), v, shift);
                sip += shift;
                sop += shift;
            }

            sim += vim.Mod;
            sip += vip.Mod;
            som += vom.Mod;
            sop += vop.Mod;
        }

        if (NextRight(vim) is { } threadRight && NextRight(vop) is null)
        {
            vop.Thread = threadRight;
            vop.Mod += sim - sop;
        }

        if (NextLeft(vip) is { } threadLeft && NextLeft(vom) is null)
        {
            vom.Thread = threadLeft;
            vom.Mod += sip - som;
            defaultAncestor = v;
        }
    }

    private static TreeNode? NextLeft(TreeNode v) => v.Children.Count > 0 ? v.Children[0] : v.Thread;

    private static TreeNode? NextRight(TreeNode v) => v.Children.Count > 0 ? v.Children[^1] : v.Thread;

    private static void MoveSubtree(TreeNode wm, TreeNode wp, double shift)
    {
        var subtrees = wp.Number - wm.Number;
        if (subtrees == 0)
        {
            return;
        }

        wp.Change -= shift / subtrees;
        wp.Shift += shift;
        wm.Change += shift / subtrees;
        wp.Prelim += shift;
        wp.Mod += shift;
    }

    private static void ExecuteShifts(TreeNode v)
    {
        double shift = 0;
        double change = 0;
        for (var i = v.Children.Count - 1; i >= 0; i--)
        {
            var w = v.Children[i];
            w.Prelim += shift;
            w.Mod += shift;
            change += w.Change;
            shift += w.Shift + change;
        }
    }

    private static TreeNode Ancestor(TreeNode vim, TreeNode v, TreeNode defaultAncestor) =>
        vim.Ancestor is { } a && v.Parent is { } p && p.Children.Contains(a)
            ? a
            : defaultAncestor;

    private void SecondWalk(TreeNode v, double m, int depth)
    {
        v.X = v.Prelim + m;
        v.Y = depth * (NodeHeight + LevelGap);
        foreach (var c in v.Children)
        {
            SecondWalk(c, m + v.Mod, depth + 1);
        }
    }

    private static IEnumerable<TreeNode> Flatten(TreeNode root)
    {
        var stack = new Stack<TreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children)
            {
                stack.Push(c);
            }
        }
    }

    /// <summary>Mutable scratch node — Buchheim's algorithm needs a lot of per-node state.</summary>
    private sealed class TreeNode
    {
        public required PlanNode Plan { get; init; }

        public required int Depth { get; init; }

        public TreeNode? Parent { get; init; }

        public required int Number { get; init; }

        public bool IsCollapsed { get; init; }

        public int HiddenDescendantCount { get; init; }

        public List<TreeNode> Children { get; } = [];

        public double Prelim;
        public double Mod;
        public double Shift;
        public double Change;
        public double X;
        public double Y;

        public TreeNode? Thread;
        public TreeNode? Ancestor;

        public TreeNode? LeftSibling =>
            Parent is null || Number <= 1 ? null : Parent.Children[Number - 2];

        public TreeNode? LeftmostSibling =>
            Parent is null ? null : Parent.Children[0];
    }
}
