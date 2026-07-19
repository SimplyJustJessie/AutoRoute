using AutoRoute.App.Services;

namespace AutoRoute.Tests;

/// <summary>
/// The updater's pure decision logic: version comparison (what counts as "newer") and parsing the
/// checksum manifest. The network / file-swap / relaunch paths are integration-shaped and covered by
/// the end-to-end verification, not here.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("0.3.0", "v0.3.1", true)]   // patch bump
    [InlineData("0.3.0", "v0.4.0", true)]   // minor bump
    [InlineData("0.3.0", "v1.0.0", true)]   // major bump
    [InlineData("0.3.0", "v0.3.0", false)]  // same
    [InlineData("0.3.0", "v0.2.9", false)]  // older
    [InlineData("dev", "v0.3.0", false)]    // an unbaked dev build never self-updates
    [InlineData("0.3.0", "garbage", false)] // unparseable tag
    public void IsNewer_compares_semver(string current, string candidate, bool expected)
    {
        var version = new AppVersion(current);
        Assert.Equal(expected, version.IsNewer(candidate));
    }

    [Fact]
    public void Normalize_strips_git_metadata_and_leading_v()
    {
        Assert.Equal("0.3.0", new AppVersion("0.3.0+abc123").Current);
        Assert.Equal("0.3.0", new AppVersion("v0.3.0").Current);
        Assert.Equal(AppVersion.Dev, new AppVersion(null).Current);
        Assert.Equal(AppVersion.Dev, new AppVersion("   ").Current);
    }

    [Fact]
    public void FindChecksum_matches_asset_by_basename()
    {
        const string sums =
            "111aaa  AutoRoute-v0.3.0-x86_64.AppImage\n" +
            "222bbb  some-other-file\n";

        Assert.Equal("111aaa", UpdateService.FindChecksum(sums, "AutoRoute-v0.3.0-x86_64.AppImage"));
        Assert.Null(UpdateService.FindChecksum(sums, "AutoRoute-v9.9.9-x86_64.AppImage"));
    }

    [Fact]
    public void FindChecksum_tolerates_binary_marker_and_path_prefix()
    {
        const string sums = "deadbeef *./AutoRoute-v0.3.0-x86_64.AppImage\n";
        Assert.Equal("deadbeef", UpdateService.FindChecksum(sums, "AutoRoute-v0.3.0-x86_64.AppImage"));
    }
}
