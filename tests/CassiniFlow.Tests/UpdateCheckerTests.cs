using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CassiniFlow.Domain.Ports.Inbound;
using CassiniFlow.Infrastructure.Adapters;

namespace CassiniFlow.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("1.2.0", "1.1.0", true)]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("v1.1.1", "1.1.0", true)]
    [InlineData("2.0.0", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0", false)]
    [InlineData("v1.1.0", "1.1.0", false)]
    [InlineData("1.0.9", "1.1.0", false)]
    [InlineData("v1.0.5", "1.1.0", false)]
    public void IsNewerVersion_CorrectlyComparesVersions(string latest, string current, bool expected)
    {
        bool result = GitHubReleaseUpdateChecker.IsNewerVersion(latest, current);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenNewerReleaseExists_ReturnsUpdateAvailable()
    {
        var jsonResponse = """
        {
            "tag_name": "v1.2.0",
            "name": "CassiniFlow v1.2.0 — Lucky Imaging Update",
            "body": "- Super fast multi-zone stacking\n- Auto detection",
            "html_url": "https://github.com/cloud0259/CassiniFlow/releases/tag/v1.2.0",
            "published_at": "2026-11-03T20:00:00Z"
        }
        """;

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var client = new HttpClient(handler);
        using var checker = new GitHubReleaseUpdateChecker(client, currentVersionOverride: "1.1.0");

        var info = await checker.CheckForUpdateAsync(CancellationToken.None);

        Assert.True(info.IsUpdateAvailable);
        Assert.Equal("1.1.0", info.CurrentVersion);
        Assert.Equal("1.2.0", info.LatestVersion);
        Assert.Equal("CassiniFlow v1.2.0 — Lucky Imaging Update", info.ReleaseTitle);
        Assert.Contains("Super fast", info.ReleaseNotes);
        Assert.Equal("https://github.com/cloud0259/CassiniFlow/releases/tag/v1.2.0", info.HtmlUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenSameOrOlderRelease_ReturnsNoUpdate()
    {
        var jsonResponse = """
        {
            "tag_name": "v1.1.0",
            "name": "CassiniFlow v1.1.0",
            "body": "Current release",
            "html_url": "https://github.com/cloud0259/CassiniFlow/releases/tag/v1.1.0"
        }
        """;

        var handler = new MockHttpMessageHandler(HttpStatusCode.OK, jsonResponse);
        using var client = new HttpClient(handler);
        using var checker = new GitHubReleaseUpdateChecker(client, currentVersionOverride: "1.1.0");

        var info = await checker.CheckForUpdateAsync(CancellationToken.None);

        Assert.False(info.IsUpdateAvailable);
        Assert.Equal("1.1.0", info.CurrentVersion);
        Assert.Equal("1.1.0", info.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WhenNetworkFails_ReturnsGracefullyWithoutThrowing()
    {
        var handler = new MockHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "Error");
        using var client = new HttpClient(handler);
        using var checker = new GitHubReleaseUpdateChecker(client, currentVersionOverride: "1.1.0");

        var info = await checker.CheckForUpdateAsync(CancellationToken.None);

        Assert.False(info.IsUpdateAvailable);
        Assert.Equal("1.1.0", info.CurrentVersion);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public MockHttpMessageHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
