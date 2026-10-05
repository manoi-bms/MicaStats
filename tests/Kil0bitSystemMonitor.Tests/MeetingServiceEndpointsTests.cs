using Kil0bitSystemMonitor.Services.Conference;
using Xunit;

namespace Kil0bitSystemMonitor.Tests;

public sealed class MeetingServiceEndpointsTests
{
    [Theory]
    [InlineData("https://service.example/v1", "https://service.example/v1/")]
    [InlineData(" https://service.example:8443/custom/v1/ ", "https://service.example:8443/custom/v1/")]
    [InlineData("http://localhost:8000/v1", "http://localhost:8000/v1/")]
    public void Valid_http_endpoints_are_normalized_with_a_trailing_slash(string input, string expected)
    {
        Assert.True(MeetingServiceEndpoints.TryNormalizeBaseUrl(input, out string normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, MeetingServiceEndpoints.RequireBaseUrl(input, "Test").AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/v1")]
    [InlineData("ftp://service.example/v1")]
    [InlineData("https:///v1")]
    [InlineData("https://user:password@service.example/v1")]
    [InlineData("https://service.example/v1?key=secret")]
    [InlineData("https://service.example/v1#secret")]
    public void Unsafe_or_incomplete_endpoints_are_rejected(string? input)
    {
        Assert.False(MeetingServiceEndpoints.TryNormalizeBaseUrl(input, out string normalized));
        Assert.Equal("", normalized);

        MeetingException error = Assert.Throws<MeetingException>(
            () => MeetingServiceEndpoints.RequireBaseUrl(input, "Speech"));
        Assert.Equal("Speech endpoint is not configured or is invalid.", error.Message);
        if (!string.IsNullOrEmpty(input)) Assert.DoesNotContain(input, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Excessively_long_endpoints_are_rejected()
    {
        string endpoint = "https://service.example/" + new string('x', MeetingServiceEndpoints.MaxBaseUrlLength);

        Assert.False(MeetingServiceEndpoints.TryNormalizeBaseUrl(endpoint, out _));
    }
}
