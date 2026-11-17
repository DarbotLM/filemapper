using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileMapper.Domain;

namespace FileMapper.Semantics;

/// <summary>Canonical JSON v1: ordinal object-key sorting; arrays and exact JSON number lexemes survive.</summary>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new ValidUnicodeStringConverter(), new ValidUnicodeCharConverter() }
    };

    public static string Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, element);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string Sha256<T>(T value) => HashUtf8(Serialize(value));

    /// <summary>SHA-256 over exact UTF-8 text, with no JSON wrapping or Unicode normalization.</summary>
    public static string HashUtf8(string value)
    { ValidateUnicode(value); return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))); }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previous = null;
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (previous == property.Name) throw new JsonException("Duplicate JSON property names have no canonical representation.");
                    previous = property.Name;
                    ValidateUnicode(property.Name);
                    writer.WritePropertyName(property.Name); Write(writer, property.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray(); break;
            case JsonValueKind.String:
                var value = element.GetString()!; ValidateUnicode(value); writer.WriteStringValue(value); break;
            case JsonValueKind.Number:
                // Never round through double/decimal: 9007199254740993 and 1e-400 must remain exact.
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new JsonException("Undefined JSON values have no canonical representation.");
        }
    }

    internal static void ValidateUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
                throw new JsonException("Unpaired UTF-16 surrogates cannot be canonically hashed.");
            remaining = remaining[consumed..];
        }
    }

    private sealed class ValidUnicodeStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { ValidateUnicode(value); writer.WriteStringValue(value); }
        public override void WriteAsPropertyName(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        { ValidateUnicode(value); writer.WritePropertyName(value); }
    }

    private sealed class ValidUnicodeCharConverter : JsonConverter<char>
    {
        private static string ValidText(char value)
        {
            // A char serializes as its own JSON string, so even two surrogate chars in an
            // array cannot form a scalar. Reject before Utf8JsonWriter substitutes U+FFFD.
            if (char.IsSurrogate(value)) throw new JsonException("A surrogate char cannot be canonically hashed.");
            return value.ToString();
        }

        public override char Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null || value.Length != 1 || char.IsSurrogate(value[0]))
                throw new JsonException("A canonical char must contain one non-surrogate UTF-16 character.");
            return value[0];
        }

        public override void Write(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
            => writer.WriteStringValue(ValidText(value));
        public override char ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => Read(ref reader, typeToConvert, options);
        public override void WriteAsPropertyName(Utf8JsonWriter writer, char value, JsonSerializerOptions options)
            => writer.WritePropertyName(ValidText(value));
    }
}

public sealed record SemanticPose(string Shape, int Axis = 0, int Rotation = 0);
public sealed record SemanticEvidenceRef(string Id, string SourceField);
public sealed record SemanticSourceReference(string SnapshotId, string NodeId, string? Path);
public sealed record SemanticSnapshotWindow(string SnapshotId, string Root, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string Status, string Completeness);
public sealed record SemanticObservedWindow(DateTimeOffset? From, DateTimeOffset? To, string Basis);
public sealed record SemanticDimension(string Status, JsonElement? Value, string? SourceField);
public sealed record SemanticBounds(int MaxTokens, int RetainedTokens, int TotalDistinctTokens, long TotalTokens, bool Truncated);
public sealed record SemanticHashes(string RawSourceHash, string ContentHash, string GeometryHash, string ObjectHash);
public sealed record LexicalFeatures(int RuneLength, int LetterCount, int DigitCount, bool HasNonAscii,
    bool HasCombiningMark, bool BeginsWithDigit, bool EndsWithDigit);
public sealed record SemanticWordShape(string WordId, string Token, long Count, IReadOnlyList<string> Sources,
    [property: JsonPropertyName("shape_type")] string ShapeType,
    [property: JsonPropertyName("shape_color")] string ShapeColor,
    [property: JsonPropertyName("somshape")] SemanticPose SomShape,
    int Signature9, string SignatureBits, LexicalFeatures Features)
{
    public string SignatureModel => "lexical-flags-9/v1";
    public bool SignatureCollisionsAllowed => true;
}
public sealed record SemanticObject(string ModelVersion, string SomId, string ObservationId, string Label,
    [property: JsonPropertyName("some_type")] string SomeType,
    [property: JsonPropertyName("shape_type")] string ShapeType,
    [property: JsonPropertyName("shape_color")] string ShapeColor,
    [property: JsonPropertyName("somshape")] SemanticPose SomShape,
    [property: JsonPropertyName("some_shapes")] IReadOnlyList<SemanticWordShape> SomeShapes,
    JsonElement RawSourceNode, IReadOnlyList<SemanticEvidenceRef> EvidenceRefs, SemanticSourceReference SourceReference,
    SemanticSnapshotWindow SnapshotWindow, SemanticObservedWindow ObservedWindow,
    IReadOnlyDictionary<string, SemanticDimension> Dimensions, SemanticBounds Bounds, SemanticHashes Hashes);

/// <summary>A deterministic lexical projection of supplied graph evidence, not a trained model or an identity resolver.</summary>
public static class SemanticObjects
{
    public const string ModelVersion = "semantic-object/v1";
    public const int MaxTokens = 256;
    private static readonly string[] WordShapes = ["I", "O", "T", "L", "J", "S", "Z", "pillar", "stair"];
    private static readonly HashSet<string> ObservationOrPresentation = new(StringComparer.Ordinal)
    {
        "evidenceId", "evidenceIds", "evidenceRefs", "observationId", "observationIds", "observedAt",
        "observedFrom", "observedTo", "snapshotId", "shape_type", "shape_color", "somshape", "rotation", "axis"
    };

    public static string SomIdFor(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        return "som:" + CanonicalJson.Sha256(new { Model = "som-identity/v1", NodeId = nodeId });
    }

    /// <summary>NFKC, invariant lowercase, then Unicode letters/digits and attached combining marks; duplicates survive.</summary>
    public static string[] Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text); CanonicalJson.ValidateUnicode(text);
        var builder = new StringBuilder(); var tokens = new List<string>();
        foreach (var rune in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune) || (builder.Length > 0 && category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                builder.Append(rune.ToString());
            else if (builder.Length > 0)
            { tokens.Add(builder.ToString()); builder.Clear(); }
        }
        if (builder.Length > 0) tokens.Add(builder.ToString());
        return tokens.ToArray();
    }

    public static SemanticObject Create(Snapshot snapshot, MapNode node, DateTimeOffset? observedFrom = null, DateTimeOffset? observedTo = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.Id); ArgumentException.ThrowIfNullOrWhiteSpace(node.Id);
        if (observedFrom.HasValue && observedTo.HasValue && observedTo < observedFrom)
            throw new ArgumentException("The supplied observation window is reversed.", nameof(observedTo));

        // Freeze a complete JSON copy; later mutation of the caller's property dictionary must not invalidate hashes.
        var raw = JsonDocument.Parse(CanonicalJson.Serialize(node));
        using (raw)
        {
            var rawNode = raw.RootElement.Clone();
            var properties = rawNode.GetProperty("properties");
            var contentProperties = properties.EnumerateObject().Where(p => !ObservationOrPresentation.Contains(p.Name))
                .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
            var content = new { node.Kind, node.Name, node.Path, Properties = contentProperties };
            var rawHash = CanonicalJson.Sha256(rawNode);
            var contentHash = CanonicalJson.Sha256(content);
            // Node identity is already a collector decision. Never replace it with path, name, a token, or a small signature.
            var somId = SomIdFor(node.Id);
            var evidence = EvidenceReferences(properties);
            var observed = new SemanticObservedWindow(observedFrom, observedTo,
                observedFrom.HasValue || observedTo.HasValue ? "supplied-observation-range" : "unknown");
            var observationId = "som-observation:" + CanonicalJson.Sha256(new
            { Model = "som-observation/v1", SnapshotId = snapshot.Id, NodeId = node.Id, RawSourceHash = rawHash, ObservedWindow = observed });
            var shape = ShapeCatalog.ForKind(node.Kind);
            var geometryHash = CanonicalJson.Sha256(new
            { Model = "oasis-canonical-geometry/v1", Cells = ShapeCatalog.Rotate(shape, 0).OrderBy(c => c.X).ThenBy(c => c.Y).ThenBy(c => c.Z) });
            var (words, bounds) = Words(node, contentProperties);
            var result = new SemanticObject(ModelVersion, somId, observationId, node.Name, node.Kind, shape,
                ShapeCatalog.ColorFor(shape), new(shape), words, rawNode, evidence, new(snapshot.Id, node.Id, node.Path),
                new(snapshot.Id, snapshot.Root, snapshot.StartedAt, snapshot.FinishedAt, snapshot.Status, snapshot.Completeness),
                observed, Dimensions(properties), bounds, new(rawHash, contentHash, geometryHash, ""));
            // The empty objectHash slot is the documented, reproducible preimage; other hashes remain included.
            return result with { Hashes = result.Hashes with { ObjectHash = CanonicalJson.Sha256(result) } };
        }
    }

    private static SemanticEvidenceRef[] EvidenceReferences(JsonElement properties)
    {
        var refs = new List<SemanticEvidenceRef>();
        foreach (var property in properties.EnumerateObject().Where(p => p.Name is "evidenceId" or "evidenceIds" or "evidenceRefs").OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            void Add(JsonElement value, string field)
            {
                if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) refs.Add(new(value.GetString()!, field));
                else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id)) Add(id, field + "/id");
                else if (value.ValueKind == JsonValueKind.Array)
                { var index = 0; foreach (var item in value.EnumerateArray()) Add(item, field + "/" + index++); }
            }
            Add(property.Value, "/properties/" + Pointer(property.Name));
        }
        return refs.Distinct().OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.SourceField, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyDictionary<string, SemanticDimension> Dimensions(JsonElement properties)
    {
        var result = new SortedDictionary<string, SemanticDimension>(StringComparer.Ordinal);
        foreach (var dimension in new[] { "workspace", "repository", "task", "terminal", "computer", "network", "chip" })
        {
            result[dimension] = new("unknown", null, null);
            foreach (var key in new[] { dimension + "Id", dimension })
                if (properties.TryGetProperty(key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                    && !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                {
                    result[dimension] = new("source-asserted", value.Clone(), "/properties/" + key); break;
                }
        }
        return result;
    }

    private sealed class WordAccumulator
    {
        public long Count;
        public SortedSet<string> Sources { get; } = new(StringComparer.Ordinal);
    }

    private static (SemanticWordShape[] Words, SemanticBounds Bounds) Words(MapNode node, Dictionary<string, JsonElement> properties)
    {
        var all = new SortedDictionary<string, WordAccumulator>(StringComparer.Ordinal);
        long total = 0;
        void AddText(string? text, string source)
        {
            if (text is null) return;
            foreach (var token in Tokenize(text))
            {
                if (!all.TryGetValue(token, out var entry)) all[token] = entry = new();
                entry.Count++; entry.Sources.Add(source); total++;
            }
        }
        void AddValue(JsonElement value, string source)
        {
            if (value.ValueKind == JsonValueKind.String) AddText(value.GetString(), source);
            else if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) AddValue(property.Value, source + "/" + Pointer(property.Name));
            else if (value.ValueKind == JsonValueKind.Array)
            { var index = 0; foreach (var item in value.EnumerateArray()) AddValue(item, source + "/" + index++); }
        }
        AddText(node.Kind, "/kind"); AddText(node.Name, "/name"); AddText(node.Path, "/path");
        foreach (var property in properties.OrderBy(p => p.Key, StringComparer.Ordinal)) AddValue(property.Value, "/properties/" + Pointer(property.Key));
        var words = all.Take(MaxTokens).Select(pair =>
        {
            var runes = pair.Key.EnumerateRunes().ToArray();
            var features = new LexicalFeatures(runes.Length, runes.Count(Rune.IsLetter), runes.Count(Rune.IsDigit),
                runes.Any(r => !r.IsAscii), runes.Any(r => Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark),
                Rune.IsDigit(runes[0]), Rune.IsDigit(runes[^1]));
            var signature = (features.RuneLength <= 3 ? 1 : features.RuneLength <= 7 ? 2 : 4)
                | (features.LetterCount > 0 ? 8 : 0) | (features.DigitCount > 0 ? 16 : 0)
                | (features.HasNonAscii ? 32 : 0) | (features.HasCombiningMark ? 64 : 0)
                | (features.BeginsWithDigit ? 128 : 0) | (features.EndsWithDigit ? 256 : 0);
            var shape = WordShapes[signature % WordShapes.Length];
            return new SemanticWordShape("word:" + CanonicalJson.Sha256(new { Model = "lexical-word/v1", Token = pair.Key }),
                pair.Key, pair.Value.Count, pair.Value.Sources.ToArray(), shape, ShapeCatalog.ColorFor(shape), new(shape),
                signature, Convert.ToString(signature, 2).PadLeft(9, '0'), features);
        }).ToArray();
        return (words, new(MaxTokens, words.Length, all.Count, total, all.Count > MaxTokens));
    }

    private static string Pointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
