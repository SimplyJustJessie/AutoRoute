using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoRoute.App.Services;
using AutoRoute.Tests.TestSupport;

namespace AutoRoute.Tests;

/// <summary>
/// What the Updates menu says. Package-manager installs check too, and only learn whether a newer
/// version exists — no "use your package manager" lecture.
/// </summary>
public sealed class UpdateCheckTests
{
    private sealed class OneReply(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private const string Release = """{ "tag_name": "v0.4.0", "assets": [] }""";

    private static UpdateService PackageInstall(string current) =>
        new(new HttpClient(new OneReply(Release)), new FakeProcessRunner(), new AppVersion(current));

    [Fact]
    public async Task Package_install_learns_a_newer_version_exists()
    {
        var check = await PackageInstall("0.3.0").CheckAsync();
        Assert.True(check.UpdateAvailable);
        Assert.Equal("Version 0.4.0 is available", check.Message);
    }

    [Fact]
    public async Task Current_version_just_says_up_to_date()
    {
        var check = await PackageInstall("0.4.0").CheckAsync();
        Assert.False(check.UpdateAvailable);
        Assert.Equal("Up to date", check.Message);
    }

    [Fact]
    public async Task Dev_build_says_so()
    {
        var check = await PackageInstall("dev").CheckAsync();
        Assert.False(check.UpdateAvailable);
        Assert.Equal("Development build", check.Message);
    }
}
