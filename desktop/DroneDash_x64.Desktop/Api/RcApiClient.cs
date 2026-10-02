using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DroneDash_x64.Desktop.Models;

namespace DroneDash_x64.Desktop.Api;

public sealed class RcApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public void Configure(string endpoint, string token)
    {
        if (!endpoint.EndsWith('/'))
        {
            endpoint += "/";
        }

        _http.BaseAddress = new Uri(endpoint, UriKind.Absolute);
        _http.DefaultRequestHeaders.Remove("X-Bridge-Token");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _http.DefaultRequestHeaders.Add("X-Bridge-Token", token.Trim());
        }
    }

    public async Task<HealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/health", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<HealthDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Health-Antwort.");
    }

    public async Task<StatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/status", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<StatusDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Status-Antwort.");
    }

    public async Task<ConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/config", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ConfigurationDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Konfigurations-Antwort.");
    }

    public async Task<ConfigurationDto> UpdateConfigurationAsync(
        ConfigurationUpdateDto update,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync("api/v1/config", update, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return (await response.Content.ReadFromJsonAsync<ConfigurationDto>(JsonOptions, cancellationToken))
            ?? throw new InvalidDataException("Leere Konfigurations-Antwort.");
    }

    public async Task<IReadOnlyList<MediaItemDto>> GetMediaAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("api/v1/media", cancellationToken);
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
        using var response = await _http.GetAsync(
            $"api/v1/media/{index}/download",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        await EnsureSuccessAsync(response, cancellationToken);
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

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

        progress?.Report(1d);
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

    public void Dispose() => _http.Dispose();
}
