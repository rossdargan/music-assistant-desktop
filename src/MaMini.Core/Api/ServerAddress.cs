namespace MaMini.Core.Api;

public static class ServerAddress
{
    public const int DefaultPort = 8095;

    /// <summary>
    /// Normalises user input ("192.168.1.10", "ma.local:8095", "https://ma.example.com/") into an
    /// http(s) base URL without a trailing slash. Returns false if the input can't be understood.
    /// </summary>
    public static bool TryNormalize(string? input, out Uri httpBase)
    {
        httpBase = null!;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        if (text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text[5..];
        }
        else if (text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text[6..];
        }
        else if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Query = "", Fragment = "" };

        // Bare host names default to MA's port; explicit https or an explicit port are respected.
        var explicitPort = HasExplicitPort(text);
        if (!explicitPort && uri.Scheme == Uri.UriSchemeHttp)
        {
            builder.Port = DefaultPort;
        }

        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^3];
        }

        builder.Path = path;
        httpBase = new Uri(builder.Uri.ToString().TrimEnd('/'));
        return true;
    }

    public static Uri ToWebSocketUri(Uri httpBase)
    {
        var builder = new UriBuilder(httpBase)
        {
            Scheme = httpBase.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
        };
        builder.Path = builder.Path.TrimEnd('/') + "/ws";
        return builder.Uri;
    }

    private static bool HasExplicitPort(string text)
    {
        var afterScheme = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var hostPort = afterScheme.Split('/', 2)[0];
        if (hostPort.StartsWith('['))
        {
            return hostPort.Contains("]:", StringComparison.Ordinal);
        }

        return hostPort.Contains(':', StringComparison.Ordinal);
    }
}
