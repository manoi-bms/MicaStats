using System;

namespace Kil0bitSystemMonitor.Services.Conference;

/// <summary>Validates user-configured meeting service base URLs without contacting them.</summary>
public static class MeetingServiceEndpoints
{
    internal const int MaxBaseUrlLength = 2048;

    public static bool TryNormalizeBaseUrl(string? input, out string normalized)
    {
        normalized = "";
        string value = input?.Trim() ?? "";
        if (value.Length == 0 || value.Length > MaxBaseUrlLength ||
            !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string absolute = uri.AbsoluteUri;
        normalized = absolute.EndsWith("/", StringComparison.Ordinal) ? absolute : absolute + "/";
        if (normalized.Length <= MaxBaseUrlLength) return true;
        normalized = "";
        return false;
    }

    public static Uri RequireBaseUrl(string? input, string serviceName)
    {
        if (TryNormalizeBaseUrl(input, out string normalized))
            return new Uri(normalized, UriKind.Absolute);

        string name = string.IsNullOrWhiteSpace(serviceName) ? "Meeting service" : serviceName.Trim();
        throw new MeetingException($"{name} endpoint is not configured or is invalid.");
    }
}
