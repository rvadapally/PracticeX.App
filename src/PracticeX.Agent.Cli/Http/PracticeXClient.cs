using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PracticeX.Agent.Cli.Http;

/// <summary>
/// Thin wrapper around the cloud manifest + bundle endpoints. Keeps multipart
/// streaming-from-disk so we never load full bundles into memory.
/// </summary>
public sealed class PracticeXClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Guid _connectionId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public PracticeXClient(Uri apiBaseUrl, Guid connectionId, string? bearerToken, bool insecure)
    {
        var handler = new HttpClientHandler();
        if (insecure)
        {
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        _http = new HttpClient(handler)
        {
            BaseAddress = apiBaseUrl,
            Timeout = TimeSpan.FromMinutes(10)
        };
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
        _connectionId = connectionId;
    }

    public async Task<ManifestScanResponse> PostManifestAsync(
        IReadOnlyList<ManifestItemDto> items,
        string? notes,
        CancellationToken cancellationToken)
    {
        var request = new ManifestScanRequest(items, notes);
        var response = await _http.PostAsJsonAsync(
            $"/api/sources/connections/{_connectionId}/folder/manifest",
            request,
            Json,
            cancellationToken);

        await EnsureSuccessAsync(response, "manifest", cancellationToken);

        var body = await response.Content.ReadFromJsonAsync<ManifestScanResponse>(Json, cancellationToken)
            ?? throw new HttpRequestException("Empty response from /folder/manifest.");
        return body;
    }

    public async Task<IngestionBatchSummaryDto> PostBundleAsync(
        Guid batchId,
        IReadOnlyList<BundleFile> selected,
        string? notes,
        CancellationToken cancellationToken)
    {
        using var multipart = new MultipartFormDataContent();

        var index = 0;
        var openedStreams = new List<Stream>(selected.Count);
        try
        {
            foreach (var file in selected)
            {
                var stream = File.OpenRead(file.AbsolutePath);
                openedStreams.Add(stream);

                var part = new StreamContent(stream);
                part.Headers.ContentType = new MediaTypeHeaderValue(file.MimeType);
                multipart.Add(part, $"files[{index}]", file.Name);
                multipart.Add(new StringContent(file.RelativePath), $"paths[{index}]");
                if (!string.IsNullOrEmpty(file.ManifestItemId))
                {
                    multipart.Add(new StringContent(file.ManifestItemId), $"manifestItemIds[{index}]");
                }
                index++;
            }

            if (!string.IsNullOrEmpty(notes))
            {
                multipart.Add(new StringContent(notes), "notes");
            }

            var response = await _http.PostAsync(
                $"/api/sources/connections/{_connectionId}/folder/bundles?batchId={batchId}",
                multipart,
                cancellationToken);

            await EnsureSuccessAsync(response, "bundle", cancellationToken);

            var body = await response.Content.ReadFromJsonAsync<IngestionBatchSummaryDto>(Json, cancellationToken)
                ?? throw new HttpRequestException("Empty response from /folder/bundles.");
            return body;
        }
        finally
        {
            foreach (var s in openedStreams)
            {
                await s.DisposeAsync();
            }
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string stage, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // ASP.NET Core returns ProblemDetails on failure; surface the title/detail
        // so the operator sees the real reason instead of just an HTTP code.
        string? title = null;
        string? detail = null;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsDto>(cancellationToken);
            title = problem?.Title;
            detail = problem?.Detail;
        }
        catch
        {
            detail = await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new HttpRequestException(
            $"{stage} request failed ({(int)response.StatusCode} {response.StatusCode}): {title ?? "unknown"} — {detail ?? "(no detail)"}",
            inner: null,
            statusCode: response.StatusCode);
    }

    public void Dispose() => _http.Dispose();
}

public sealed record BundleFile(
    string AbsolutePath,
    string RelativePath,
    string Name,
    string MimeType,
    string? ManifestItemId
);
