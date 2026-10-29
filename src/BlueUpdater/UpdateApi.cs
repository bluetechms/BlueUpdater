using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlueUpdater;

public sealed class ManifestResponse
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("manifest")] public string Manifest { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

public sealed class ReportBody
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("targetVersion")] public string? TargetVersion { get; set; }
    [JsonPropertyName("updaterVersion")] public string? UpdaterVersion { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public interface IUpdateApi
{
    // null = nothing newer is available (HTTP 204).
    Task<ManifestResponse?> GetManifestAsync(string product, string currentVersion, string instance, CancellationToken ct);
    Task DownloadAsync(string product, string version, string instance, string destFile, long expectedSize, CancellationToken ct);
    Task ReportAsync(string product, string instance, ReportBody body, CancellationToken ct);
    // Any HTTP answer at all counts as reachable (used to tell "new build is broken" from "no internet").
    Task<bool> ReachableAsync(CancellationToken ct);
}

public sealed class UpdateApiException : Exception
{
    public int? StatusCode { get; }
    public UpdateApiException(string message, int? status = null) : base(message) { StatusCode = status; }
}

// HTTPS only, no redirects (a redirect to another host would carry the key along or swap the payload).
public sealed class UpdateApi : IUpdateApi
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _apiKey;
    private readonly string _host;
    private readonly string _updaterVersion;

    public UpdateApi(string apiBase, string apiKey, string hostName, string updaterVersion, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(apiBase, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("ApiBase must be an https URL", nameof(apiBase));
        _base = apiBase.TrimEnd('/');
        _apiKey = apiKey;
        _host = hostName;
        _updaterVersion = updaterVersion;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(45)
        };
    }

    private HttpRequestMessage Req(HttpMethod method, string path, string instance)
    {
        var r = new HttpRequestMessage(method, _base + path);
        r.Headers.Add("x-api-key", _apiKey);
        r.Headers.Add("X-Client-Host", _host);
        if (!string.IsNullOrEmpty(instance)) r.Headers.Add("X-Client-Instance", instance);
        r.Headers.UserAgent.ParseAdd("BlueUpdater/" + _updaterVersion);
        return r;
    }

    public async Task<ManifestResponse?> GetManifestAsync(string product, string currentVersion, string instance, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, $"/clientupdate/{Uri.EscapeDataString(product)}/manifest?current={Uri.EscapeDataString(currentVersion)}", instance);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        if (!res.IsSuccessStatusCode) throw new UpdateApiException($"manifest request failed: HTTP {(int)res.StatusCode}", (int)res.StatusCode);
        return await res.Content.ReadFromJsonAsync<ManifestResponse>(cancellationToken: ct)
               ?? throw new UpdateApiException("empty manifest response");
    }

    public async Task DownloadAsync(string product, string version, string instance, string destFile, long expectedSize, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Get, $"/clientupdate/{Uri.EscapeDataString(product)}/download/{Uri.EscapeDataString(version)}", instance);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new UpdateApiException($"download failed: HTTP {(int)res.StatusCode}", (int)res.StatusCode);
        if (res.Content.Headers.ContentLength is long len && len != expectedSize)
            throw new UpdateApiException("download size differs from the signed manifest");

        var part = destFile + ".part";
        try
        {
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > expectedSize) throw new UpdateApiException("download is larger than the signed manifest says");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                }
                await dst.FlushAsync(ct);
            }
            File.Move(part, destFile, true);
        }
        catch
        {
            try { File.Delete(part); } catch { /* best effort */ }
            throw;
        }
    }

    public async Task ReportAsync(string product, string instance, ReportBody body, CancellationToken ct)
    {
        using var req = Req(HttpMethod.Post, $"/clientupdate/{Uri.EscapeDataString(product)}/report", instance);
        req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new UpdateApiException($"report failed: HTTP {(int)res.StatusCode}", (int)res.StatusCode);
    }

    public async Task<bool> ReachableAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var res = await _http.GetAsync(_base, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
