using System.Text.Json;
using FileMapper.Domain;
using FileMapper.Extract.CSharp;
using FileMapper.Harvest;
using FileMapper.Semantics;

namespace FileMapper.Engine;

internal static class SourceSemanticLinker
{
    internal sealed record Result(long ExactSpans, long Links, bool LinksTruncated, long WordLimitedDocuments);
    private sealed record Word(string OccurrenceId, string TermId, long Start, long Length);

    internal static async Task<Result> WriteAsync(string corpus, SourceSemanticInput input, IReadOnlyList<MapNode> nodes,
        IReadOnlyList<SuppliedSourceDocumentStatus> parserDocuments, SourceSemanticOptions options,
        SourceSemanticArtifacts artifacts, CancellationToken token)
    {
        var store = new GitSourceCapture(corpus, readOnly: true).Blobs;
        var replayed = new Dictionary<string, SourceLexiconResult>(StringComparer.Ordinal);
        var parserStatuses = parserDocuments.ToDictionary(d => d.LogicalPath, StringComparer.Ordinal);
        if (parserStatuses.Count != input.Selected.Count || input.Selected.Any(d => !parserStatuses.ContainsKey(d.Uri)))
            throw new InvalidDataException("Parser admission denominator differs from the verified supplied documents.");
        var nodesByPath = nodes.Where(n => n.Path is not null).ToLookup(n => n.Path!, StringComparer.Ordinal);
        var handled = new HashSet<string>(StringComparer.Ordinal);
        long exact = 0, links = 0, wordLimited = 0; bool truncated = false;
        await using var spans = artifacts.Open("source-spans.ndjson");
        await using var output = artifacts.Open("word-links.ndjson");
        await using var replays = artifacts.Open("lexical-replays.ndjson");
        await using var joins = artifacts.Open("document-joins.ndjson");
        foreach (var source in input.Selected)
        {
            token.ThrowIfCancellationRequested();
            var parser = parserStatuses[source.Uri]; var sourceNodes = nodesByPath[source.Uri].ToArray();
            if (parser.Status != "admitted")
            {
                foreach (var node in sourceNodes)
                {
                    handled.Add(node.Id);
                    await spans.AddAsync(new SourceSemanticSpan(node.Id, source.Capture.DocumentId, source.Capture.Blob!.Sha256,
                        "parser-rejected:" + parser.Status, null, null, null, null), token);
                }
                await joins.AddAsync(new { source.Capture.DocumentId, source.Uri, status = "parser-rejected", parserStatus = parser.Status,
                    sourceNodes = sourceNodes.Length, emittedLinks = 0, wordCoverage = "not-evaluated" }, token);
                continue;
            }
            var hash = source.Capture.Blob!.Sha256;
            var decoded = SuppliedSourceDecoder.Decode(source.Bytes, hash, source.Bytes.LongLength, options.MaximumDocumentBytes, token).Decoded
                ?? throw new InvalidDataException("An admitted parser document did not pass exact span decoding.");
            if (!replayed.TryGetValue(hash, out var replay))
            {
                var filename = hash + ".lexical.ndjson.gz";
                replay = await SourceLexicon.ExtractAsync(store, source.Capture.Blob, artifacts.PathFor(filename), input.Extraction.Policy.Options, token);
                replayed.Add(hash, replay);
                if (replay.OutputPath is not null) await artifacts.RegisterGeneratedAsync(filename, replay.Occurrences, token);
                await replays.AddAsync(replay with { OutputPath = replay.OutputPath is null ? null : filename }, token);
            }
            var saved = source.Extraction;
            if (replay.Status != saved.LexicalStatus || replay.Complete != saved.Complete || replay.Occurrences != saved.Occurrences ||
                replay.WordOccurrences != saved.WordOccurrences || replay.OutputSha256 != saved.OutputSha256 || replay.CanonicalNdjsonSha256 != saved.CanonicalNdjsonSha256)
                throw new InvalidDataException("Fresh lexical replay differs from the pinned extraction association.");
            var words = new List<Word>(); long eligible = 0, fragmented = 0;
            if (replay.OutputPath is { } path)
                await foreach (var word in SourceLexicon.ReadAsync(path, token))
                {
                    if (word.Kind != "word") continue;
                    if (word.Fragmented || word.TermId is null) { fragmented++; continue; }
                    eligible++;
                    if (words.Count < options.MaximumJoinWordsPerDocument) words.Add(new(word.OccurrenceId, word.TermId, word.ByteStart, word.ByteLength));
                }
            var limited = eligible > words.Count; if (limited) { wordLimited++; truncated = true; }
            long emitted = 0, omitted = 0, spanCount = 0;
            foreach (var node in sourceNodes)
            {
                handled.Add(node.Id); token.ThrowIfCancellationRequested();
                var start = Integer(node.Properties.GetValueOrDefault("start"));
                var length = Integer(node.Properties.GetValueOrDefault("length"));
                var status = "exact-native-source-span";
                if (node.Kind == "SourceDocument") { start = 0; length = decoded.Text.Length; status = "derived-whole-document-span"; }
                if (start is null || length is null)
                {
                    await spans.AddAsync(new SourceSemanticSpan(node.Id, source.Capture.DocumentId, hash,
                        "native-span-unavailable", start, length, null, null), token); continue;
                }
                if (!decoded.TryMapUtf16Span(start.Value, length.Value, out var first, out var byteLength))
                {
                    await spans.AddAsync(new SourceSemanticSpan(node.Id, source.Capture.DocumentId, hash,
                        "native-span-invalid-or-scalar-interior", start, length, null, null), token); continue;
                }
                exact++; spanCount++;
                await spans.AddAsync(new SourceSemanticSpan(node.Id, source.Capture.DocumentId, hash, status,
                    start, length, first, byteLength), token);
                var end = first + byteLength;
                var firstWord = LowerBound(words, first); var afterWord = LowerBound(words, end);
                // Lexical units do not overlap. Only the last candidate can cross the span's end.
                if (afterWord > firstWord && words[afterWord - 1].Length > end - words[afterWord - 1].Start) afterWord--;
                var candidateCount = afterWord - firstWord;
                var emitCount = (int)Math.Min(candidateCount, options.MaximumLexicalLinks - links);
                var dropped = candidateCount - emitCount;
                if (dropped > 0) { omitted += dropped; truncated = true; }
                for (var index = firstWord; index < firstWord + emitCount; index++)
                {
                    token.ThrowIfCancellationRequested(); var word = words[index];
                    await output.AddAsync(new SourceSemanticWordLink(node.Id, SemanticObjects.SomIdFor(node.Id), source.Capture.DocumentId!, hash,
                        word.OccurrenceId, word.TermId, word.Start, word.Length), token);
                    links++; emitted++;
                }
            }
            await joins.AddAsync(new { source.Capture.DocumentId, source.Uri, status = "byte-containment-only", parserStatus = parser.Status,
                sourceNodes = sourceNodes.Length, exactSpans = spanCount, lexicalStatus = replay.Status, replay.Complete, replay.WordOccurrences,
                eligibleUnfragmentedWords = eligible, fragmentedWordUnits = fragmented, retainedJoinWords = words.Count,
                joinWordsTruncated = limited, emittedLinks = emitted, linksOmittedForGlobalBudgetAmongRetainedWords = omitted,
                exhaustiveUnfragmentedWordsWithinReturnedSpans = replay.Complete && !limited && omitted == 0,
                scope = "Only wholly contained unfragmented lexical words; missing native spans remain uncovered. No resolved symbol-binding claim." }, token);
        }
        foreach (var node in nodes.Where(n => !handled.Contains(n.Id)))
            await spans.AddAsync(new SourceSemanticSpan(node.Id, null, null,
                node.Path is null ? "external-or-source-path-unavailable" : "outside-admitted-source-set",
                Integer(node.Properties.GetValueOrDefault("start")), Integer(node.Properties.GetValueOrDefault("length")), null, null), token);
        await spans.CompleteAsync(token); await output.CompleteAsync(token); await replays.CompleteAsync(token); await joins.CompleteAsync(token);
        return new(exact, links, truncated, wordLimited);
    }
    private static int LowerBound(IReadOnlyList<Word> words, long value)
    {
        int low = 0, high = words.Count;
        while (low < high) { var middle = low + (high - low) / 2; if (words[middle].Start < value) low = middle + 1; else high = middle; }
        return low;
    }
    private static int? Integer(object? value) => value switch
    {
        int number => number,
        long number when number is >= int.MinValue and <= int.MaxValue => (int)number,
        JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) => number,
        _ => null
    };
}
