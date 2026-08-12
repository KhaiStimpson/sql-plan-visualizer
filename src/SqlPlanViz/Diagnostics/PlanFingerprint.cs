using System.Security.Cryptography;
using System.Text;
using SqlPlanViz.Model;

namespace SqlPlanViz.Diagnostics;

/// <summary>Stable SHA-256 hash of operator shape and referenced objects; metrics are excluded.</summary>
public static class PlanFingerprint
{
    public static string Compute(PlanStatement statement)
    {
        var shape = new StringBuilder();
        AppendNode(statement.Root, shape);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape.ToString()))).ToLowerInvariant();
    }

    private static void AppendNode(PlanNode node, StringBuilder shape)
    {
        shape.Append('(')
            .Append(node.PhysicalOp.Trim().ToUpperInvariant())
            .Append('|')
            .Append((node.ObjectName ?? string.Empty).Replace("[", string.Empty).Replace("]", string.Empty).ToUpperInvariant());
        foreach (var child in node.Children)
        {
            AppendNode(child, shape);
        }

        shape.Append(')');
    }
}
