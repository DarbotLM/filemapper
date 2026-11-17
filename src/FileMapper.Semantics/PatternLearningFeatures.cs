using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace FileMapper.Semantics;

internal static class PatternLearningRules
{
    internal static readonly ImmutableArray<string> Heads = ["storage-group", "code-path"];
    internal static void Text(string? value, int maximum, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
            throw new ArgumentException(field + " is absent, too long, or contains control characters.");
        CanonicalJson.ValidateUnicode(value);
    }
    internal static void Hash(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException(field + " must be a lowercase SHA256 digest.");
    }
    internal static void Config(PatternLearningConfig value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Schema != "filemapper.pattern-learning.config/1" || value.FeatureCount is < 256 or > 16384 ||
            (value.FeatureCount & (value.FeatureCount - 1)) != 0 || value.MaximumActiveFeatures is < 16 or > 256 ||
            value.LearningRateQ is < 1 or > 100000 || value.WeightBoundQ is < 1 or > 8000000 ||
            value.MaximumEvents is < 1 or > 1000000000 || value.MaximumTrainingUpdates is < 1 or > 1000000000)
            throw new ArgumentException("Unsupported learning schema or numeric configuration bound.");
    }
    internal static void Policy(PatternLearningPartitionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy); Text(policy.CohortId, 256, "cohortId"); Hash(policy.SeedHash, "seedHash");
        if (policy.Schema != "filemapper.pattern-learning.partition/1" || policy.TotalBuckets != 10000 ||
            policy.TrainBuckets is < 1 or > 9998 || policy.ValidationBuckets < 1 ||
            policy.TrainBuckets + policy.ValidationBuckets >= policy.TotalBuckets)
            throw new ArgumentException("Partition policy must reserve nonempty training, validation and test groups.");
    }
    internal static void Epoch(PatternLearningEpoch epoch, string policyHash)
    {
        ArgumentNullException.ThrowIfNull(epoch); Text(epoch.EpochId, 256, "epochId"); Hash(epoch.PartitionPolicyHash, "partitionPolicyHash");
        if (epoch.PartitionPolicyHash != policyHash || epoch.StartSequence < 1 || epoch.EndSequence < epoch.StartSequence)
            throw new ArgumentException("Epoch policy or sequence bounds are invalid.");
        if (epoch.ParentCheckpointHash is not null) Hash(epoch.ParentCheckpointHash, "parentCheckpointHash");
    }
    internal static void Context(PatternLearningContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var (name, value) in Dimensions(context))
        {
            if (value is null) throw new ArgumentException("Missing explicit context status: " + name);
            if (value.Status == "unknown")
            {
                if (value.Value is not null || value.Source is not null) throw new ArgumentException("Unknown context must not carry a guessed value/source.");
            }
            else if (value.Status is "observed" or "declared") { Text(value.Value, 512, name); Text(value.Source, 1024, name + " source"); }
            else throw new ArgumentException("Context status must be unknown, observed or declared.");
        }
    }
    internal static IEnumerable<(string Name, PatternLearningContextValue Value)> Dimensions(PatternLearningContext context)
    {
        yield return ("workspace", context.Workspace); yield return ("repository", context.Repository);
        yield return ("task", context.Task); yield return ("terminal", context.Terminal);
        yield return ("computer", context.Computer); yield return ("network", context.Network); yield return ("session", context.Session);
    }
    internal static void Input(PatternLearningInput input, bool requirePair = false)
    {
        ArgumentNullException.ThrowIfNull(input); Subject(input.Left);
        if (input.Right is not null) Subject(input.Right);
        else if (requirePair) throw new ArgumentException("Relevance training and prediction require two subjects.");
        Context(input.Context);
    }
    private static void Subject(PatternLearningSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject); Text(subject.SomId, 256, "somId"); Hash(subject.ObjectHash, "objectHash");
        Text(subject.SourceObservationId, 256, "sourceObservationId"); Text(subject.SourceNodeId, 256, "sourceNodeId");
        Text(subject.SnapshotId, 256, "snapshotId"); Text(subject.ShapeType, 64, "shapeType"); Hash(subject.GeometryHash, "geometryHash");
        Tokens(subject.Words); Tokens(subject.Path); Tokens(subject.Commands);
        if (subject.ObservedFrom.HasValue && subject.ObservedTo.HasValue && subject.ObservedFrom > subject.ObservedTo)
            throw new ArgumentException("Recorded observation window is reversed.");
    }
    private static void Tokens(PatternLearningTokens sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (sequence.Tokens.IsDefault || sequence.Sources.IsDefault || sequence.Tokens.Length > 64 || sequence.Sources.Length > 16)
            throw new ArgumentException("Token/source arrays are absent or exceed their bounds.");
        if (sequence.Status == "unknown")
        {
            if (sequence.Tokens.Length != 0 || sequence.Sources.Length != 0 || sequence.Order != "unknown" || sequence.Truncated)
                throw new ArgumentException("Unknown lexical context must remain empty and unordered.");
        }
        else if (sequence.Status == "observed")
        {
            if (sequence.Sources.Length == 0 || sequence.Order is not ("recorded" or "unordered"))
                throw new ArgumentException("Observed tokens need source provenance and an explicit order interpretation.");
            foreach (var token in sequence.Tokens) Text(token, 128, "token");
            foreach (var source in sequence.Sources) Text(source, 1024, "token source");
        }
        else throw new ArgumentException("Tokens must be observed or unknown; the learner does not invent them.");
    }
    internal static void Label(PatternLearningLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!Heads.Contains(label.Head, StringComparer.Ordinal) || label.Value is not (0 or 1) ||
            label.SourceKind is not ("human" or "controlled-fixture" or "reviewed-dataset"))
            throw new ArgumentException("Supervised labels need a known head, binary value and explicit label provenance.");
        Text(label.SourceId, 512, "label source"); Text(label.Rationale, 2048, "label rationale");
    }
}

public static class PatternLearningFeatures
{
    public const string Version = "filemapper.pattern-features/1";
    public static string SchemaHash(PatternLearningConfig config)
    {
        PatternLearningRules.Config(config);
        return CanonicalJson.Sha256(new { Version, config.FeatureCount, config.MaximumActiveFeatures,
            ValueScale = 1000, TokenPolicy = "supplied-exact-ordinal; no re-tokenization",
            HashPolicy = "sha256-utf8-first32-big-endian; bins-after-32; collisions-disclosed",
            Excluded = "SOM/node/snapshot IDs; absolute roots; label; group key; context values; timestamps" });
    }

    public static PatternLearningFeatureVector Extract(PatternLearningConfig config, PatternLearningInput input)
    {
        PatternLearningRules.Config(config); PatternLearningRules.Input(input);
        var candidates = new SortedDictionary<string, int>(StringComparer.Ordinal) { ["bias"] = 1000 };
        var missing = new List<string>();
        foreach (var (name, context) in PatternLearningRules.Dimensions(input.Context))
            if (context.Status == "unknown") missing.Add(name);
        Subject("left", input.Left);
        if (input.Right is { } right)
        {
            Subject("right", right);
            Add("pair:shape=" + CanonicalJson.Serialize(new[] { input.Left.ShapeType, right.ShapeType }));
            if (input.Left.ShapeType == right.ShapeType) Add("same-shape");
            if (input.Left.GeometryHash == right.GeometryHash) Add("same-geometry");
            Shared("word", input.Left.Words, right.Words);
            Shared("path", input.Left.Path, right.Path);
            Shared("command", input.Left.Commands, right.Commands);
            if (input.Left.Path.Status == "observed" && right.Path.Status == "observed" &&
                input.Left.Path.Order == "recorded" && right.Path.Order == "recorded")
            {
                var a = input.Left.Path.Tokens; var b = right.Path.Tokens; var prefix = 0;
                while (prefix < Math.Min(a.Length, b.Length) && a[prefix] == b[prefix]) prefix++;
                candidates["path-prefix-fraction"] = Math.Max(a.Length, b.Length) == 0 ? 0 : prefix * 1000 / Math.Max(a.Length, b.Length);
            }
        }
        else missing.Add("right-subject");
        // Structural features cannot be crowded out by lexical bins. Remaining names are retained ordinally.
        var reserved = new Dictionary<string, int>(StringComparer.Ordinal)
        { ["bias"] = 0, ["same-shape"] = 1, ["same-geometry"] = 2, ["word-jaccard"] = 3,
            ["path-jaccard"] = 4, ["command-jaccard"] = 5, ["path-prefix-fraction"] = 6 };
        var ordered = candidates.Where(pair => pair.Value != 0).OrderBy(pair => reserved.ContainsKey(pair.Key) ? 0 : 1)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var selected = ordered.Take(config.MaximumActiveFeatures).ToArray();
        var groups = selected.GroupBy(pair => reserved.TryGetValue(pair.Key, out var index) ? index : Bin(pair.Key, config.FeatureCount))
            .OrderBy(group => group.Key).ToArray();
        var items = groups.Select(group => new PatternLearningFeature(group.Key,
            Math.Min(1000, group.Sum(pair => pair.Value)), group.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToImmutableArray())).ToImmutableArray();
        var truncated = ordered.Length > selected.Length || input.Left.Words.Truncated || input.Left.Path.Truncated || input.Left.Commands.Truncated ||
            input.Right is { } other && (other.Words.Truncated || other.Path.Truncated || other.Commands.Truncated);
        return new(SchemaHash(config), CanonicalJson.Sha256(input), items, ordered.Length,
            selected.Length - groups.Length, truncated, missing.Order(StringComparer.Ordinal).ToImmutableArray());

        void Add(string name) => candidates[name] = 1000;
        void Subject(string side, PatternLearningSubject subject)
        {
            Add(side + ":shape=" + subject.ShapeType);
            Sequence(side + ":word", subject.Words); Sequence(side + ":path", subject.Path); Sequence(side + ":command", subject.Commands);
        }
        void Sequence(string category, PatternLearningTokens sequence)
        {
            if (sequence.Status == "unknown") { missing.Add(category); Add(category + ":unknown"); return; }
            foreach (var token in sequence.Tokens.Distinct(StringComparer.Ordinal)) Add(category + ":token=" + token);
            if (sequence.Order != "recorded") return;
            for (var i = 0; i + 1 < sequence.Tokens.Length; i++) Add(category + ":bigram=" + CanonicalJson.Serialize(sequence.Tokens.Skip(i).Take(2).ToArray()));
        }
        void Shared(string category, PatternLearningTokens left, PatternLearningTokens right)
        {
            if (left.Status != "observed" || right.Status != "observed") return;
            var a = left.Tokens.ToHashSet(StringComparer.Ordinal); var b = right.Tokens.ToHashSet(StringComparer.Ordinal);
            var shared = a.Intersect(b, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var union = a.Union(b, StringComparer.Ordinal).Count(); candidates[category + "-jaccard"] = union == 0 ? 0 : shared.Length * 1000 / union;
            foreach (var token in shared) Add("shared:" + category + "=" + token);
        }
    }
    private static int Bin(string feature, int count)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(feature));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);
        return 32 + (int)(value % (uint)(count - 32));
    }

    internal static (ImmutableArray<PatternLearningTerm> Terms, ImmutableArray<PatternLearningOccurrence> Occurrences) Observe(PatternLearningEvent value)
    {
        var terms = new Dictionary<string, PatternLearningTerm>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, PatternLearningOccurrence>(StringComparer.Ordinal);
        foreach (var subject in value.Input.Right is null ? new[] { value.Input.Left } : new[] { value.Input.Left, value.Input.Right })
        {
            Add("shape", subject.ShapeType, subject); Add("whole-shape-hash", subject.GeometryHash, subject);
            Tokens("word", subject.Words, subject); Tokens("path", subject.Path, subject); Tokens("command", subject.Commands, subject);
        }
        return (terms.Values.OrderBy(term => term.TermHash, StringComparer.Ordinal).ToImmutableArray(),
            occurrences.Values.OrderBy(item => item.PatternHash, StringComparer.Ordinal).ThenBy(item => item.SourceObservationId, StringComparer.Ordinal).ToImmutableArray());
        void Tokens(string category, PatternLearningTokens sequence, PatternLearningSubject subject)
        {
            foreach (var token in sequence.Tokens) Add(category, token, subject);
            if (sequence.Status != "observed" || sequence.Order != "recorded" || sequence.Tokens.Length == 0) return;
            // Full sequences are indexed by a collision-resistant hash; nine-bit signatures are not identities.
            Add(category + "-pattern-hash", CanonicalJson.Sha256(sequence.Tokens), subject);
            for (var i = 0; i + 1 < sequence.Tokens.Length; i++) Add(category + "-bigram", CanonicalJson.Serialize(sequence.Tokens.Skip(i).Take(2).ToArray()), subject);
        }
        void Add(string category, string token, PatternLearningSubject subject)
        {
            var hash = CanonicalJson.Sha256(new { Schema = "filemapper.pattern-term/1", Category = category, Token = token });
            terms[hash] = terms.TryGetValue(hash, out var previous) ? previous with { Count = checked(previous.Count + 1) } : new(hash, category, token, 1);
            if (value.Kind != "observation") return;
            var occurrence = new PatternLearningOccurrence(subject.SourceObservationId, hash, subject.SomId, subject.SourceNodeId,
                subject.SnapshotId, subject.ObservedFrom, subject.ObservedTo, value.CapturedAt, value.Input.Context);
            // Same source+pattern in one encounter is one source occurrence, irrespective of repeated token count.
            occurrences[subject.SourceObservationId + ":" + hash] = occurrence;
        }
    }
}
