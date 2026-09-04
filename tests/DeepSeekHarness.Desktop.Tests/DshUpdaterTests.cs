using DeepSeekHarness.Desktop.Services;
using Xunit;

namespace DeepSeekHarness.Desktop.Tests;

public class DshUpdaterTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0-rc.1", "1.0.0", -1)]        // rc < its stable
    [InlineData("1.0.0", "1.0.0-rc.9", 1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]  // numeric rc compare
    [InlineData("0.1.2", "0.1.3", -1)]
    [InlineData("1.1.0", "1.2.0", -1)]
    public void CompareVersions_handles_rc(string a, string b, int expected)
    {
        Assert.Equal(expected, Sign(DshUpdater.CompareVersions(a, b)));
    }

    [Fact]
    public void PickCandidate_prefers_higher_next_over_latest()
    {
        var c = DshUpdater.PickCandidate("0.1.1", "0.1.2", "0.1.3-rc.1");
        Assert.NotNull(c);
        Assert.Equal("0.1.3-rc.1", c.Version);
        Assert.Equal("next", c.Tag);
    }

    [Fact]
    public void PickCandidate_returns_null_when_current()
    {
        Assert.Null(DshUpdater.PickCandidate("0.1.2", "0.1.2", "0.1.2-rc.1"));
    }

    [Fact]
    public void PickCandidate_returns_latest_when_no_newer_next()
    {
        var c = DshUpdater.PickCandidate("0.1.1", "0.1.2", null);
        Assert.NotNull(c);
        Assert.Equal("0.1.2", c.Version);
        Assert.Equal("latest", c.Tag);
    }

    private static int Sign(int x) => x == 0 ? 0 : (x < 0 ? -1 : 1);
}
