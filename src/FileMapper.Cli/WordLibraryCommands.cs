using System.Text.Json;
using FileMapper.Harvest;
using FileMapper.Storage;

namespace FileMapper.Cli;

internal static class WordLibraryCommands
{
    private sealed record Request(string CaptureRoot, string IndexPath, bool CompactIndex, string Destination,
        string EvidenceJsonPath, PublicRepositoryEvidence Evidence, PublicWordLibraryOptions? Options = null, string? ExtractionId = null, PublicHeadEvidence? HeadEvidence = null, string? HeadReceiptPath = null, string? HeadStdoutPath = null);
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "help" or "--help")
        {
            Console.WriteLine("""
            word-library publish <request.json>
            word-library verify <request.json>
            word-library verify-index <request.json>

            publish creates a new immutable per-capture SQLite edition from a sealed compact index.
            verify checks an existing edition against the exact public-page, HEAD, capture and index inputs.
            verify-index checks a one-capture compact index against the request's capture and extraction IDs.
            The request includes captureRoot, indexPath, compactIndex=true, destination, extractionId,
            evidenceJsonPath, evidence, headReceiptPath, headStdoutPath, headEvidence, and optional options.
            evidence pins captureId, providerStableId, repositoryUrl, visibility=public, observedAt,
            evidenceUri and evidenceSha256. headEvidence pins receiptSha256, stdoutSha256 and observedAt.
            The retained HEAD receipt and raw output must match the repository ID, URL and exact revision.
            Provider records are checked for consistency and hashes, not cryptographically authenticated.
            Defaults bound an edition plus its journal to 8 GiB and require 1 GiB free; options may set
            maximumEditionBytes and minimumFreeBytes. Existing editions are never overwritten.
            """); return 0;
        }
        if (args.Length != 2 || args[0] is not ("publish" or "verify" or "verify-index")) throw new ArgumentException("Use word-library publish|verify <request.json>.");
        var path = Path.GetFullPath(args[1]); if (new FileInfo(path).Length > 65536) throw new ArgumentException("Request exceeds 64 KiB.");
        var request = JsonSerializer.Deserialize<Request>(await File.ReadAllBytesAsync(path, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("Missing request.");
        ISourceHarvestReader reader = request.CompactIndex ? new CompactSourceHarvestIndex(request.IndexPath) : new SourceHarvestIndex(request.IndexPath, readOnly: true);
        if (args[0] == "verify-index")
        {
            var compact = reader as CompactSourceHarvestIndex ?? throw new ArgumentException("A compact index is required.");
            var captures = compact.ListCaptures(0, 2, cancellationToken);
            if (captures.Total != 1 || captures.Items.Count != 1 || captures.Items[0].CaptureId != request.Evidence.CaptureId || captures.Items[0].ExtractionHash != request.ExtractionId || !compact.Counts(request.Evidence.CaptureId, cancellationToken).ExtractionSealed)
                throw new InvalidDataException("Compact index does not contain exactly the pinned capture and extraction.");
            await using var file = new FileStream(request.IndexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellationToken));
            Console.WriteLine(JsonSerializer.Serialize(new { sha256 = hash, captureId = request.Evidence.CaptureId, extractionId = request.ExtractionId }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return 0;
        }
        var receipt = args[0] == "verify"
            ? await PublicWordLibrary.VerifyAsync(reader as CompactSourceHarvestIndex ?? throw new ArgumentException("A compact index is required."), request.CaptureRoot, request.Destination, request.Evidence, request.EvidenceJsonPath, request.HeadEvidence ?? throw new ArgumentException("HEAD evidence required."), request.HeadReceiptPath ?? throw new ArgumentException("HEAD receipt required."), request.HeadStdoutPath ?? throw new ArgumentException("HEAD output required."), cancellationToken)
            : await PublicWordLibrary.PublishAsync(reader, request.CaptureRoot, request.Destination, request.Evidence, request.EvidenceJsonPath, request.HeadEvidence ?? throw new ArgumentException("HEAD evidence required."), request.HeadReceiptPath ?? throw new ArgumentException("HEAD receipt required."), request.HeadStdoutPath ?? throw new ArgumentException("HEAD output required."), request.Options, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web))); return 0;
    }
}
