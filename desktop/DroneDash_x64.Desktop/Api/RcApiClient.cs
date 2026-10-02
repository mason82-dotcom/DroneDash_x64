using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using DroneDash_x64.Desktop.Models;

namespace DroneDash_x64.Desktop.Api;

public sealed class RcApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _configurationLock = new();

    private Uri _baseUri = new("http://127.0.0.1:49152/");
    private string _token = "";

    public void Configure(string endpoint, string token)
    {
        endpoint = endpoint.Trim();
        if (!endpoint.EndsWith('/'))
        {
            endpoint += "/";
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(baseUri.Host))
        {
            throw new ArgumentException("Ungültiger Bridge-Endpunkt. Erwartet wird http:// oder https://.", nameof(endpoint));
        }

        lock (_configurationLock)
        {
            _baseUri = baseUri;
            _token = token.Trim();
        }
    }

    public async Task<HealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/health", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HealthDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Health-Antwort.");
    }

    public async Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/status", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<StatusDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Status-Antwort.");
    }

    public async Task<ConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/config", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ConfigurationDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Konfigurations-Antwort.");
    }

    public async Task<ConfigurationDto> UpdateConfigurationAsync(
        ConfigurationUpdateDto update,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(update, options: JsonOptions);
        using var response = await SendAsync(HttpMethod.Put, "api/v1/config", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ConfigurationDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Konfigurations-Antwort.");
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetMediaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/media", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<List<MediaItemDto>>(JsonOptions, cancellationToken))
            ?? [];
    }

    public async Task DownloadMediaAsync(
        int index,
        string destination,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"api/v1/media/{index}/download");
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);

        var total = response.Content.Headers.ContentLength;
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partial = destination + ".part";
        TryDelete(partial);

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(
                partial,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;

                    if (total is > 0)
                    {
                        progress?.Report(Math.Clamp(written / (double)total.Value, 0d, 1d));
                    }
                }

                await target.FlushAsync(cancellationToken);

                if (total is > 0 && written != total.Value)
                {
                    throw new IOException(
                        $"Unvollständiger Download: {written} von {total.Value} Bytes empfangen.");
                }
            }

            File.Move(partial, destination, true);
            progress?.Report(1d);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativePath,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, relativePath);
        request.Content = content;
        return await _http.SendAsync(request, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        Uri baseUri;
        string token;

        lock (_configurationLock)
        {
            baseUri = _baseUri;
            token = _token;
        }

        var request = new HttpRequestMessage(method, new Uri(baseUri, relativePath));
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.TryAddWithoutValidation("X-Bridge-Token", token);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException(
            $"Bridge HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup failure must not hide the original transfer result.
        }
    }

    public void Dispose() => _http.Dispose();
}
