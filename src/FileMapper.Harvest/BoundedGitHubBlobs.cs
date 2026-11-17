using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Harvest;

// Counts response body bytes delivered by HttpClient, not TLS, headers, or wire bytes.
// No downloaded body is written to scratch; one blob plus one probe byte is buffered.
internal sealed class BoundedGitHubBlobs : IDisposable
{
    private readonly HttpClient client;
    private readonly string repository;
    private readonly long objectLimit, bodyLimit;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow, deadline;
    private long bodyBytes, requests;
    private string lastOutcome = "not-started";
    private string? closed;

    internal BoundedGitHubBlobs(string canonicalUri, long objectLimit, long bodyLimit, int seconds, HttpMessageHandler? handler = null)
    {
        repository = new Uri(canonicalUri).AbsolutePath.Trim('/');
        this.objectLimit = objectLimit; this.bodyLimit = bodyLimit;
        deadline = started.AddSeconds(seconds);
        client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FileMapper-source-capture/3");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.raw+json");
    }

    internal object Evidence() => new { schema = "filemapper.materialization-attempt/1", startedAt = started, deadline,
        finishedAt = DateTimeOffset.UtcNow, maximumResponseBodyBytes = bodyLimit, responseBodyBytes = bodyBytes,
        maximumBlobBytes = objectLimit, requests, lastOutcome,
        accounting = "Response body bytes read by the application; excludes headers, TLS, protocol framing and unread buffered bytes. Downloaded bodies are not staged on disk." };

    internal async Task<(byte[]? Bytes, string? Detail)> ReadAsync(string id, string format, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (closed is not null) return Fail(closed);
        if (DateTimeOffset.UtcNow >= deadline) return Fail("Materialization deadline reached.");
        if (bodyBytes >= bodyLimit) return Fail("Response-body byte budget reached.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return Fail("Materialization deadline reached.");
        timeout.CancelAfter(remaining);
        try
        {
            requests++;
            using var response = await client.GetAsync("https://api.github.com/repos/" + repository + "/git/blobs/" + id,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var detail = "Blob request returned HTTP " + (int)response.StatusCode + "; response body was not read.";
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    closed = "Materialization stopped after HTTP " + (int)response.StatusCode + "; remaining blobs were not requested.";
                return Fail(detail);
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = new MemoryStream(); var buffer = new byte[65536];
            while (true)
            {
                var available = Math.Min(bodyLimit - bodyBytes, objectLimit + 1 - data.Length);
                if (available <= 0) return Fail(data.Length > objectLimit ? "Blob exceeds the per-document byte ceiling." : "Response-body byte budget reached before end of body could be verified.");
                var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, available)), timeout.Token);
                if (count == 0) break;
                bodyBytes += count;
                data.Write(buffer, 0, count);
                if (data.Length > objectLimit) return Fail("Blob exceeds the per-document byte ceiling.");
            }
            var bytes = data.ToArray();
            using var hash = IncrementalHash.CreateHash(format == "sha1" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
            hash.AppendData(Encoding.ASCII.GetBytes("blob " + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + "\0"));
            hash.AppendData(bytes);
            if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != id) return Fail("Response bytes did not match the pinned Git blob object ID.");
            lastOutcome = "verified";
            return (bytes, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Fail("Materialization deadline reached."); }
        catch (HttpRequestException) { return Fail("Blob request failed; response unavailable."); }
        catch (IOException) { return Fail("Blob response was interrupted or invalid."); }
    }

    private (byte[]? Bytes, string? Detail) Fail(string detail) { lastOutcome = detail; return (null, detail); }
    public void Dispose() => client.Dispose();
}
