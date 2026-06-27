using System.Net;

namespace Tomur.Models;

public sealed class ProxySettings
{
    private ProxySettings(bool useProxy, IWebProxy? proxy, string displayText)
    {
        UseProxy = useProxy;
        Proxy = proxy;
        DisplayText = displayText;
    }

    public bool UseProxy { get; }

    public IWebProxy? Proxy { get; }

    public string DisplayText { get; }

    public static ProxySettings Resolve(string? explicitProxy, bool noProxy)
    {
        if (noProxy)
        {
            return new ProxySettings(false, null, "disabled");
        }

        var proxyValue = FirstNonEmpty(
            explicitProxy,
            Environment.GetEnvironmentVariable("HTTPS_PROXY"),
            Environment.GetEnvironmentVariable("https_proxy"),
            Environment.GetEnvironmentVariable("HTTP_PROXY"),
            Environment.GetEnvironmentVariable("http_proxy"),
            Environment.GetEnvironmentVariable("ALL_PROXY"),
            Environment.GetEnvironmentVariable("all_proxy"));

        if (string.IsNullOrWhiteSpace(proxyValue))
        {
            return new ProxySettings(false, null, "none");
        }

        var normalized = NormalizeProxyUrl(proxyValue);
        var proxy = new WebProxy(normalized);
        return new ProxySettings(true, proxy, normalized);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string NormalizeProxyUrl(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return value;
        }

        return $"http://{value}";
    }
}
