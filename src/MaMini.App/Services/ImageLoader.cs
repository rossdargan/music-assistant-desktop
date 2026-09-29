using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MaMini.Core.Diagnostics;

namespace MaMini.App.Services;

internal sealed record LoadedImage(ImageSource Image, byte[] Bytes);

/// <summary>
/// Downloads cover art at thumbnail size, decodes it small, and keeps a tiny LRU cache so skipping
/// back and forth doesn't re-download.
/// </summary>
internal sealed partial class ImageLoader : IDisposable
{
    public const int ThumbnailSize = 160;
    private const int DecodeSize = 144;
    private const int Capacity = 20;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Func<Uri?> _serverBase;
    private readonly Func<string?> _token;
    private readonly LinkedList<(string Url, LoadedImage Image)> _lru = new();

    public ImageLoader(Func<Uri?> serverBase, Func<string?> token)
    {
        _serverBase = serverBase;
        _token = token;
    }

    /// <summary>Resolves relative URLs against the server and asks the image proxy for a thumbnail.</summary>
    public string? Normalize(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        Uri? uri;
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https"))
        {
            var server = _serverBase();
            if (server is null || !Uri.TryCreate(server, imageUrl, out uri))
            {
                return null;
            }
        }

        var url = uri.ToString();
        if (url.Contains("/imageproxy", StringComparison.OrdinalIgnoreCase))
        {
            url = SizeParameter().IsMatch(url)
                ? SizeParameter().Replace(url, $"size={ThumbnailSize}")
                : url + (url.Contains('?') ? "&" : "?") + $"size={ThumbnailSize}";
        }

        return url;
    }

    public async Task<LoadedImage?> LoadAsync(string url, CancellationToken ct)
    {
        var cached = _lru.FirstOrDefault(e => e.Url == url);
        if (cached.Image is not null)
        {
            _lru.Remove(cached);
            _lru.AddFirst(cached);
            return cached.Image;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var server = _serverBase();
            if (server is not null && _token() is { } token && string.Equals(new Uri(url).Host, server.Host, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodeSize;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new LoadedImage(bitmap, bytes);
            _lru.AddFirst((url, image));
            while (_lru.Count > Capacity)
            {
                _lru.RemoveLast();
            }

            return image;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Info($"Cover art could not be loaded ({ex.GetType().Name}: {ex.Message}).");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"(?<=[?&])size=\d+", RegexOptions.IgnoreCase)]
    private static partial Regex SizeParameter();
}
