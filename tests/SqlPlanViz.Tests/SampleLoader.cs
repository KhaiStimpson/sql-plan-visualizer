using System.Reflection;

namespace SqlPlanViz.Tests;

/// <summary>
/// Loads the .sqlplan fixtures embedded from Samples/ as resources, keyed by file name.
/// </summary>
public static class SampleLoader
{
    public const string OrdersActual = "orders-actual.sqlplan";
    public const string NestedLoopLookupStorm = "nested-loop-lookup-storm.sqlplan";
    public const string OrdersEstimated = "orders-estimated.sqlplan";

    public static string Load(string sampleFileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("." + sampleFileName, StringComparison.Ordinal));

        if (resourceName is null)
        {
            throw new FileNotFoundException(
                $"No embedded resource found for sample '{sampleFileName}'. " +
                $"Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
