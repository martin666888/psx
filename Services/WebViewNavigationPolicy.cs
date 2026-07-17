namespace PSX.Services;

internal enum WebViewNavigationTarget
{
    Blocked,
    Internal,
    External
}

internal static class WebViewNavigationPolicy
{
    private static readonly HashSet<string> InternalHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "psx.local",
        "psx-attachments.local"
    };

    public static WebViewNavigationTarget Classify(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return WebViewNavigationTarget.Blocked;
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(uri.OriginalString["mailto:".Length..])
                ? WebViewNavigationTarget.Blocked
                : WebViewNavigationTarget.External;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            return WebViewNavigationTarget.Blocked;

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && InternalHosts.Contains(uri.Host))
        {
            return WebViewNavigationTarget.Internal;
        }

        if (InternalHosts.Contains(uri.Host))
            return WebViewNavigationTarget.Blocked;

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.Host.StartsWith("psx.local.", StringComparison.OrdinalIgnoreCase)
                || uri.Host.StartsWith("psx-attachments.local.", StringComparison.OrdinalIgnoreCase))
            {
                return WebViewNavigationTarget.Blocked;
            }

            return string.IsNullOrWhiteSpace(uri.Host)
                ? WebViewNavigationTarget.Blocked
                : WebViewNavigationTarget.External;
        }

        return WebViewNavigationTarget.Blocked;
    }
}
