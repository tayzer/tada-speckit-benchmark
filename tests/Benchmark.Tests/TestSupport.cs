using Benchmark.Config;

namespace Benchmark.Tests;

public static class TestSupport
{
    public static string HarnessRoot => ExperimentConfig.FindHarnessRoot(AppContext.BaseDirectory);
    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static ExperimentConfig RealConfig() => ExperimentConfig.Load(Path.Combine(HarnessRoot, "experiments", "checkout-retry"));

    public static string TempDir(string prefix = "bench-test")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void ClearReadOnly(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
    }
}
