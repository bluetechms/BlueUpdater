using BlueUpdater;
using Xunit;

namespace BlueUpdater.Tests;

// Fixtures/printing-outlet-status.sample.json is a file written by the real BluePOSPrinting 1.1 (captured
// from a run against a dummy config). If the app's AppStatus format changes, this is what breaks.
public class StatusContractTests
{
    [Fact]
    public void Reads_the_status_file_the_real_app_writes()
    {
        using var d = new TempDir();
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "printing-outlet-status.sample.json"),
            Path.Combine(d.Path, "printing-outlet_99.json"));

        var all = AppStatusFile.ReadAll(d.Path, "printing-outlet");

        var s = Assert.Single(all);
        Assert.Equal("1.1.0.0", s.Version);
        Assert.True(s.Pid > 0);
        Assert.NotNull(s.StartedUtc);
        Assert.NotNull(s.UpdatedUtc);
        Assert.Null(s.LastApiOkUtc);
        Assert.Null(s.LastDeniedCode);
        Assert.False(s.AuthLatched);
        Assert.False(s.Busy);
    }

    [Fact]
    public void Other_products_and_junk_files_are_ignored()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "a.json"), "{ not json");
        File.WriteAllText(Path.Combine(d.Path, "b.json"), "{\"product\":\"bluepunch\",\"version\":\"1.0.0.0\",\"pid\":1}");
        Assert.Empty(AppStatusFile.ReadAll(d.Path, "printing-outlet"));
    }
}
