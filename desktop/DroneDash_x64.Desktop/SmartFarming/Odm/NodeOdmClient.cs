using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DroneDash_x64.Desktop.SmartFarming.Odm;

public sealed class NodeOdmClient : IDisposable
{
    public const string DefaultEndpoint =
        "http://127.0.0.1:3000/";

    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly string? _token;
    private bool _disposed;

    public NodeOdmClient(
        string endpoint,
        string? token = null,
        HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(
                endpoint,
                UriKind.Absolute,
                out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "Ungültiger NodeODM-Endpunkt.",
                nameof(endpoint));
        }

        _baseUri = new Uri(
            uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? uri.AbsoluteUri
                : uri.AbsoluteUri + "/");

        _token = string.IsNullOrWhiteSpace(token)
            ? null
            : token;

        _httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler);

        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    public static string EndpointFromEnvironment() =>
        Environment.GetEnvironmentVariable(
            "DRONEDASH_NODEODM_URL") ??
        DefaultEndpoint;

    public static string? TokenFromEnvironment() =>
        Environment.GetEnvironmentVariable(
            "DRONEDASH_NODEODM_TOKEN");

    public async Task<NodeOdmServerInfo> ProbeAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                BuildUri("info"),
                cancellationToken);

            var content =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new(
                    false,
                    _baseUri.ToString(),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"HTTP {(int)response.StatusCode}: {Trim(content)}");
            }

            using var json = JsonDocument.Parse(content);
            var root = json.RootElement;

            return new(
                true,
                _baseUri.ToString(),
                String(root, "version"),
                String(root, "engine"),
                String(root, "engineVersion"),
                Int(root, "taskQueueCount"),
                Int(root, "maxImages"),
                Int(root, "cpuCores"),
                Long(root, "availableMemory"),
                "NodeODM erreichbar.");
        }
        catch (Exception ex) when (
            ex is HttpRequestException or
                  TaskCanceledException or
                  JsonException)
        {
            return new(
                false,
                _baseUri.ToString(),
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                ex.Message);
        }
    }

    public static IReadOnlyList<NodeOdmOption> BuildM3mOptions(
        string radiometricCalibration)
    {
        if (radiometricCalibration is not "camera" and not "camera+sun")
        {
            throw new ArgumentOutOfRangeException(
                nameof(radiometricCalibration),
                "Erlaubt sind camera oder camera+sun.");
        }

        return
        [
            new(
                "radiometric-calibration",
                radiometricCalibration),
            new(
                "primary-band",
                "NIR")
        ];
    }

    public static IReadOnlyList<string> GetM3mInputFiles(
        M3mDatasetResult dataset)
    {
        var files = new List<string>();

        foreach (var capture in dataset.Captures)
        {
            if (!capture.IsComplete)
                continue;

            files.Add(capture.Green!.FilePath);
            files.Add(capture.Red!.FilePath);
            files.Add(capture.RedEdge!.FilePath);
            files.Add(capture.Nir!.FilePath);
        }

        return files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<string> CreateM3mTaskAsync(
        M3mDatasetResult dataset,
        string taskName,
        string radiometricCalibration,
        IProgress<NodeOdmUploadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = GetM3mInputFiles(dataset);
        if (files.Count == 0)
        {
            throw new InvalidOperationException(
                "Keine vollständigen M3M-Multispektralaufnahmen vorhanden.");
        }

        foreach (var file in files)
        {
            if (!File.Exists(file))
                throw new FileNotFoundException(
                    "M3M-Eingabedatei fehlt.",
                    file);
        }

        var optionsJson = JsonSerializer.Serialize(
            BuildM3mOptions(radiometricCalibration)
                .Select(option => new
                {
                    name = option.Name,
                    value = option.Value
                }));

        using var initForm = new MultipartFormDataContent
        {
            { new StringContent(taskName), "name" },
            { new StringContent(optionsJson), "options" }
        };

        using var initResponse =
            await _httpClient.PostAsync(
                BuildUri("task/new/init"),
                initForm,
                cancellationToken);

        var initContent =
            await initResponse.Content.ReadAsStringAsync(
                cancellationToken);

        EnsureSuccess(
            initResponse,
            initContent,
            "NodeODM-Task konnte nicht initialisiert werden.");

        using var initJson = JsonDocument.Parse(initContent);
        var uuid =
            initJson.RootElement
                .GetProperty("uuid")
                .GetString();

        if (string.IsNullOrWhiteSpace(uuid))
            throw new InvalidDataException(
                "NodeODM hat keine Task-UUID geliefert.");

        var uploaded = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new(
                uploaded,
                files.Count,
                Path.GetFileName(file)));

            await using var fileStream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                useAsync: true);

            using var fileContent =
                new StreamContent(fileStream);

            fileContent.Headers.ContentType =
                new MediaTypeHeaderValue(
                    "application/octet-stream");

            using var uploadForm =
                new MultipartFormDataContent();

            uploadForm.Add(
                fileContent,
                "images",
                Path.GetFileName(file));

            using var uploadResponse =
                await _httpClient.PostAsync(
                    BuildUri(
                        $"task/new/upload/{Uri.EscapeDataString(uuid)}"),
                    uploadForm,
                    cancellationToken);

            var uploadContent =
                await uploadResponse.Content.ReadAsStringAsync(
                    cancellationToken);

            EnsureSuccess(
                uploadResponse,
                uploadContent,
                $"Upload fehlgeschlagen: {Path.GetFileName(file)}");

            uploaded++;

            progress?.Report(new(
                uploaded,
                files.Count,
                Path.GetFileName(file)));
        }

        using var commitResponse =
            await _httpClient.PostAsync(
                BuildUri(
                    $"task/new/commit/{Uri.EscapeDataString(uuid)}"),
                new ByteArrayContent([]),
                cancellationToken);

        var commitContent =
            await commitResponse.Content.ReadAsStringAsync(
                cancellationToken);

        EnsureSuccess(
            commitResponse,
            commitContent,
            "NodeODM-Task konnte nicht gestartet werden.");

        return uuid;
    }

    public async Task<NodeOdmTaskInfo> GetTaskInfoAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            BuildUri(
                $"task/{Uri.EscapeDataString(uuid)}/info"),
            cancellationToken);

        var content =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        EnsureSuccess(
            response,
            content,
            "NodeODM-Taskstatus konnte nicht gelesen werden.");

        using var json = JsonDocument.Parse(content);
        var root = json.RootElement;

        var statusCode =
            root.GetProperty("status")
                .GetProperty("code")
                .GetInt32();

        return new(
            root.GetProperty("uuid").GetString() ?? uuid,
            root.GetProperty("name").GetString() ?? "",
            statusCode,
            root.TryGetProperty("progress", out var progress)
                ? progress.GetDouble()
                : 0,
            root.TryGetProperty("imagesCount", out var images)
                ? images.GetInt32()
                : 0,
            root.TryGetProperty("processingTime", out var processing)
                ? processing.GetInt64()
                : 0);
    }

    public async Task<string> GetTaskOutputAsync(
        string uuid,
        int fromLine = 0,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            BuildUri(
                $"task/{Uri.EscapeDataString(uuid)}/output",
                ("line", fromLine.ToString(
                    CultureInfo.InvariantCulture))),
            cancellationToken);

        var content =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        EnsureSuccess(
            response,
            content,
            "NodeODM-Taskausgabe konnte nicht gelesen werden.");

        return content;
    }

    public async Task CancelTaskAsync(
        string uuid,
        CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(
                new { uuid }),
            Encoding.UTF8,
            "application/json");

        using var response =
            await _httpClient.PostAsync(
                BuildUri("task/cancel"),
                content,
                cancellationToken);

        var body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        EnsureSuccess(
            response,
            body,
            "NodeODM-Task konnte nicht abgebrochen werden.");
    }

    public async Task<string> DownloadAllAsync(
        string uuid,
        string destinationFolder,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationFolder);

        using var response =
            await _httpClient.GetAsync(
                BuildUri(
                    $"task/{Uri.EscapeDataString(uuid)}/download/all.zip"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            EnsureSuccess(
                response,
                body,
                "NodeODM-Ergebnis konnte nicht heruntergeladen werden.");
        }

        var total =
            response.Content.Headers.ContentLength;

        var path = Path.Combine(
            destinationFolder,
            $"nodeodm_{uuid}_all.zip");
        var partialPath =
            path + ".part";

        TryDelete(partialPath);

        try
        {
            await using var source =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);

            await using (var target = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;

                while (true)
                {
                    var read = await source.ReadAsync(
                        buffer,
                        cancellationToken);

                    if (read == 0)
                        break;

                    await target.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken);

                    written += read;

                    if (total is > 0)
                        progress?.Report(
                            written * 100d / total.Value);
                }

                await target.FlushAsync(
                    cancellationToken);

                if (total is > 0 &&
                    written != total.Value)
                {
                    throw new IOException(
                        $"Unvollständiger NodeODM-Download: {written} von {total.Value} Bytes empfangen.");
                }
            }

            File.Move(
                partialPath,
                path,
                overwrite: true);

            progress?.Report(100d);

            return path;
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private Uri BuildUri(
        string relativePath,
        params (string Key, string Value)[] extraQuery)
    {
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(_token))
        {
            query.Add(
                "token=" +
                Uri.EscapeDataString(_token));
        }

        foreach (var (key, value) in extraQuery)
        {
            query.Add(
                Uri.EscapeDataString(key) +
                "=" +
                Uri.EscapeDataString(value));
        }

        var relative =
            relativePath.TrimStart('/');

        if (query.Count > 0)
            relative += "?" + string.Join("&", query);

        return new Uri(
            _baseUri,
            relative);
    }

    private static void EnsureSuccess(
        HttpResponseMessage response,
        string body,
        string message)
    {
        if (response.IsSuccessStatusCode)
            return;

        throw new HttpRequestException(
            $"{message} HTTP {(int)response.StatusCode}: {Trim(body)}");
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 1200
            ? value
            : value[..1200];
    }

    private static string? String(
        JsonElement root,
        string property) =>
        root.TryGetProperty(
            property,
            out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static long? Long(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(
                property,
                out var value))
            return null;

        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup failure must not hide the original transfer result.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }
}
