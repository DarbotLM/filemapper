using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Engine;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
var bytes = Encoding.UTF8.GetBytes("hello");
var oid = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes("blob 5\0").Concat(bytes).ToArray())).ToLowerInvariant();
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 6, 10, new Reply(bytes)))
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes!.SequenceEqual(bytes), "exact object limit");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 5, 10, new Reply(bytes)))
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes is null, "exact body budget cannot prove EOF");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 4, 10, 10, new Reply(bytes)))
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes is null, "oversized body");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 6, 10, new Reply(Encoding.UTF8.GetBytes("HELLO"))))
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes is null, "mismatched oid");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 6, 1, new Stall()))
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes is null, "deadline");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 6, 10, new Stall()))
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await t.ReadAsync(oid, "sha1", cts.Token); throw new Exception("cancellation swallowed"); }
    catch (OperationCanceledException) { }
}
var large = new byte[8 * 1024 * 1024];
var largeOid = Convert.ToHexString(SHA1.HashData(Encoding.ASCII.GetBytes("blob 8388608\0").Concat(large).ToArray())).ToLowerInvariant();
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", large.Length, large.Length + 1, 10, new Reply(large)))
    Check((await t.ReadAsync(largeOid, "sha1", default)).Bytes?.Length == large.Length, "8 MiB object boundary");
using (var t = new BoundedGitHubBlobs("https://github.com/o/r", large.Length, large.Length, 10, new Reply(large)))
    Check((await t.ReadAsync(largeOid, "sha1", default)).Bytes is null, "8 MiB aggregate boundary");
foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests })
{
    var reply = new Limited(status);
    using var t = new BoundedGitHubBlobs("https://github.com/o/r", 5, 100, 10, reply);
    Check((await t.ReadAsync(oid, "sha1", default)).Bytes is null, "provider rejection");
    var next = await t.ReadAsync(oid, "sha1", default);
    Check(reply.Count == 1 && next.Detail!.Contains("remaining blobs were not requested"), "provider limit closure");
}
if (args.Length >= 3)
{
    var store = new GitSourceCapture(args[2]);
    var receipt = await store.CaptureAsync(new GitHarvestRequest("123", "https://github.com/o/r", "fixture", args[0], args[1],
        Policy: new GitHarvestPolicy(MaximumBlobBytes: 32, MaximumCapturedBytes: 64, MaterializeBlobs: true,
            BoundedMaterialization: new("github-response-body", 100))));
    Check(receipt.Manifest.State == "sealed-complete-for-policy" && receipt.Manifest.Coverage.CapturedEntries == 1, "fixture capture state");
    var entries = new List<CapturedSourceEntry>();
    await foreach (var entry in store.EntriesAsync(receipt.CaptureId)) entries.Add(entry);
    Check(entries.Count == 1 && entries[0].Entry.RepositoryPath == "hello.txt" && entries[0].Status == "captured", "fixture entry");
    var verification = await store.Blobs.VerifyAsync(entries[0].Blob!.Sha256);
    Check(verification.ActualSha256 == entries[0].Blob!.Sha256, "fixture retained bytes");
    var attempt = Path.Combine(args[2], "attempts", receipt.Manifest.CaptureRequestId);
    Check(File.Exists(Path.Combine(attempt, "request.json")) && File.Exists(Path.Combine(attempt, "materialization.json")) && File.Exists(Path.Combine(attempt, "sealed.json")), "attempt evidence");
    using var sealedJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "sealed.json")));
    using var materializationJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt, "materialization.json")));
    Check(sealedJson.RootElement.GetProperty("captureId").GetString() == receipt.CaptureId &&
        materializationJson.RootElement.GetProperty("responseBodyBytes").GetInt64() == 0, "attempt evidence binding and accounting");
    Check(receipt.Manifest.Commands.All(c => !c.Arguments.Contains("fetch")), "no fetch command");
    Check(receipt.Manifest.MaterializationEvidence is not null, "evidence descriptor");
    var verify = JsonSerializer.SerializeToElement(await new SourceHarvestWorkspace(args[2], true).VerifyAsync(receipt.CaptureId), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(verify.GetProperty("verified").GetBoolean(), "native verification");
}
if (args.Length >= 7)
{
    // The fixture tree points at deleted loose blobs; this handler is the only source of bodies.
    var provider = new FixtureProvider(oid);
    var store = new GitSourceCapture(args[5], () => provider);
    var policy = new GitHarvestPolicy(MaximumBlobBytes: 5, MaximumCapturedBytes: 64, MaterializeBlobs: true,
        BoundedMaterialization: new("github-response-body", 100));
    var receipt = await store.CaptureAsync(new GitHarvestRequest("123", "https://github.com/o/r", "remote fixture", args[3], args[4], Policy: policy));
    var entries = new List<CapturedSourceEntry>(); await foreach (var entry in store.EntriesAsync(receipt.CaptureId)) entries.Add(entry);
    Check(receipt.Manifest.State == "sealed-partial" && entries.Count == 5 && entries.Count(e => e.Status == "captured") == 2 && entries.Count(e => e.Status == "unavailable") == 3, "remote partial coverage");
    Check(entries[0].Blob!.Sha256 == entries[1].Blob!.Sha256 && entries[0].Entry.GitObjectId == oid && provider.Count == 3, "remote deduplication and closure");
    Check(entries[2].Detail!.Contains("ceiling") && entries[4].Detail!.Contains("not requested"), "per-object and rate-limit details");
    var proof = await store.Blobs.VerifyAsync(entries[0].Blob!.Sha256); Check(proof.Status == "verified", "remote CAS");
    var verify = JsonSerializer.SerializeToElement(await new SourceHarvestWorkspace(args[5], true).VerifyAsync(receipt.CaptureId), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(verify.GetProperty("verified").GetBoolean(), "remote native verification");
    var evidence = receipt.Manifest.MaterializationEvidence!;
    File.WriteAllText(Path.Combine(store.Blobs.RootDirectory, evidence.RelativePath), "corrupted");
    verify = JsonSerializer.SerializeToElement(await new SourceHarvestWorkspace(args[5], true).VerifyAsync(receipt.CaptureId), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(!verify.GetProperty("verified").GetBoolean(), "evidence corruption detected");
    var limited = new GitSourceCapture(args[6], () => new FixtureProvider(oid));
    var limitedReceipt = await limited.CaptureAsync(new GitHarvestRequest("123", "https://github.com/o/r", "budget fixture", args[3], args[4], Policy: policy with { BoundedMaterialization = new("github-response-body", 5) }));
    Check(limitedReceipt.Manifest.State == "sealed-partial" && limitedReceipt.Manifest.Coverage.CapturedEntries == 0, "remote aggregate boundary");
    var mismatch = new GitSourceCapture(args[6] + "-mismatch", () => new FixtureProvider(oid, true));
    var mismatchReceipt = await mismatch.CaptureAsync(new GitHarvestRequest("123", "https://github.com/o/r", "mismatch fixture", args[3], args[4], Policy: policy));
    var mismatchEntries = new List<CapturedSourceEntry>(); await foreach (var entry in mismatch.EntriesAsync(mismatchReceipt.CaptureId)) mismatchEntries.Add(entry);
    Check(mismatchEntries[0].Status == "unavailable" && mismatchEntries[0].Detail!.Contains("object ID") && mismatchEntries[0].Blob is null, "remote OID rejection");
}
if (args.Length >= 9)
{
    var oldStore = new GitSourceCapture(args[7], true);
    Check(oldStore.Read(args[8]).CaptureId == args[8], "legacy capture ID");
    var oldVerify = JsonSerializer.SerializeToElement(await new SourceHarvestWorkspace(args[7], true).VerifyAsync(args[8]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(oldVerify.GetProperty("verified").GetBoolean(), "legacy capture verification");
}
if (args.Length >= 11)
    Check(new GitSourceCapture(args[9], true).Read(args[10]).CaptureId == args[10], "historical capture ID");
if (args.Length == 14)
{
    var provider = new MixedProvider();
    var store = new GitSourceCapture(args[13], () => provider);
    var policy = new GitHarvestPolicy(MaximumBlobBytes: 5, MaximumCapturedBytes: 6, MaterializeBlobs: true,
        BoundedMaterialization: new("github-response-body", 100));
    var receipt = await store.CaptureAsync(new GitHarvestRequest("123", "https://github.com/o/r", "mixed fixture", args[11], args[12], Policy: policy));
    var entries = new List<CapturedSourceEntry>(); await foreach (var entry in store.EntriesAsync(receipt.CaptureId)) entries.Add(entry);
    Check(receipt.Manifest.State == "sealed-partial" && entries.Count == 9 && entries.Count(e => e.Status == "captured") == 3 &&
        entries.Count(e => e.Status == "unavailable") == 4 && entries.Count(e => e.Status == "excluded-capture-budget") == 2, "mixed coverage");
    Check(receipt.Manifest.Coverage.UniqueRetainedBytes == 6 && receipt.Manifest.Coverage.AssociatedSourceBytes == 11 && provider.Count == 4, "mixed deduplication and accounting");
    Check(entries[8].Status == "captured" && entries[8].Entry.RepositoryPath == "i.txt", "local continuation after 429");
    var evidence = receipt.Manifest.MaterializationEvidence!;
    using var evidenceJson = JsonDocument.Parse(store.Blobs.OpenRead(evidence.Sha256));
    Check(evidenceJson.RootElement.GetProperty("responseBodyBytes").GetInt64() == 15, "failed response charged to body budget");
}
Console.WriteLine("Bounded capture checks passed.");
sealed class Reply(byte[] bytes) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
}
sealed class Stall : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { await Task.Delay(Timeout.Infinite, token); throw new Exception("unexpected"); }
}
sealed class Limited(HttpStatusCode status) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Count++; return Task.FromResult(new HttpResponseMessage(status)); }
}
sealed class FixtureProvider(string firstOid, bool corrupt = false) : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Count++;
        if (request.RequestUri!.AbsolutePath.EndsWith(firstOid, StringComparison.Ordinal)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(corrupt ? "HELLO" : "hello")) });
        if (Count == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("123456")) });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
    }
}
sealed class MixedProvider : HttpMessageHandler
{
    public int Count { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Count++;
        return Task.FromResult(Count switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")) },
            2 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("123456")) },
            3 => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("four")) },
            _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        });
    }
}
