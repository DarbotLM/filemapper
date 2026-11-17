using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileMapper.Engine;

internal sealed class McpProtocolException(int code, string message, object? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public object? Details { get; } = data;
}

internal static class McpWire
{
    internal const int MaximumInputBytes = 1_048_576;
    internal const int MaximumOutputBytes = 2_097_152;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 64 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal sealed record Frame(byte[]? Bytes, bool Oversized = false);

    // The fixed buffers never grow with the peer's line. Report at limit+1 even without a newline,
    // then discard the rest of that frame so the next newline restores framing.
    internal static async IAsyncEnumerable<Frame> ReadFrames(Stream input,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var read = new byte[16_384]; var line = new byte[MaximumInputBytes];
        var length = 0; var draining = false;
        while (true)
        {
            var count = await input.ReadAsync(read.AsMemory(), cancellationToken).AsTask().WaitAsync(cancellationToken);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                if (read[i] == '\n')
                {
                    if (!draining) yield return new(line.AsSpan(0, length).ToArray());
                    length = 0; draining = false;
                }
                else if (!draining)
                {
                    if (length == line.Length) { draining = true; length = 0; yield return new(null, true); }
                    else line[length++] = read[i];
                }
            }
        }
        if (!draining && length != 0) yield return new(line.AsSpan(0, length).ToArray());
    }

    internal static JsonDocument Parse(byte[] bytes)
    {
        try { _ = StrictUtf8.GetCharCount(bytes); }
        catch (DecoderFallbackException) { throw new JsonException("Invalid UTF-8."); }
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
    }

    internal static void UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new McpProtocolException(-32600, "Duplicate JSON property.");
                UniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) UniqueProperties(item);
    }

    internal static void Fields(JsonElement value, int code, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new McpProtocolException(code, "Expected a JSON object.");
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) throw new McpProtocolException(code, "Unknown property: " + property.Name[..Math.Min(80, property.Name.Length)]);
    }

    internal static string Text(JsonElement value, int maximum, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw new McpProtocolException(-32602, "Expected a string.");
        var text = value.GetString()!;
        if ((!empty && text.Length == 0) || text.EnumerateRunes().Count() > maximum || text.Any(char.IsControl))
            throw new McpProtocolException(-32602, "String is empty, contains control characters, or exceeds its bound.");
        return text;
    }

    internal static bool ValidId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString() is { Length: <= 256 } text && !text.Any(char.IsControl),
        // Keep the exact JSON token, including integers beyond Int64/JavaScript's safe range.
        JsonValueKind.Number => id.GetRawText().Length <= 256 && Regex.IsMatch(id.GetRawText(), @"^-?(0|[1-9][0-9]*)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
        _ => false
    };
    internal static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + (id.GetRawText() == "-0" ? "0" : id.GetRawText());

    // JSON Schema integers are mathematical integers, including 1.0 and 1e0. Decimal/double parsing
    // can round a tiny fraction to zero/an integer, so inspect the exact bounded JSON token instead.
    internal static bool TryInteger(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText().AsSpan(); var negative = raw[0] == '-';
        if (negative) raw = raw[1..];
        var exponentAt = raw.IndexOfAny('e', 'E');
        var coefficient = exponentAt < 0 ? raw : raw[..exponentAt];
        var exponent = 0;
        if (exponentAt >= 0)
        {
            var digits = raw[(exponentAt + 1)..]; var minus = digits[0] == '-';
            if (digits[0] is '+' or '-') digits = digits[1..];
            // Saturation beyond the token length cannot affect a nonzero Int32 result.
            var bound = raw.Length + 32;
            foreach (var digit in digits) exponent = Math.Min(bound, exponent * 10 + digit - '0');
            if (minus) exponent = -exponent;
        }
        var point = coefficient.IndexOf('.'); var fraction = point < 0 ? 0 : coefficient.Length - point - 1;
        var first = -1; var last = -1; var total = 0;
        foreach (var digit in coefficient)
        {
            if (digit == '.') continue;
            if (digit != '0') { if (first < 0) first = total; last = total; }
            total++;
        }
        if (first < 0) return true;
        var significant = last - first + 1; var power = exponent - fraction + total - 1 - last;
        if (power < 0 || power > 10 || significant + power > 10) return false;
        long number = 0; var index = 0;
        foreach (var digit in coefficient)
        {
            if (digit == '.') continue;
            if (index >= first && index <= last) number = number * 10 + digit - '0';
            index++;
        }
        for (var i = 0; i < power; i++) number *= 10;
        if (negative) number = -number;
        if (number is < int.MinValue or > int.MaxValue) return false;
        result = (int)number; return true;
    }
    internal static int Integer(JsonElement value) => TryInteger(value, out var result) ? result
        : throw new ArgumentException("Expected an exact Int32 integer.");

    internal static byte[] Serialize(object value, int limit = MaximumOutputBytes)
    {
        using var stream = new LimitedStream(limit);
        JsonSerializer.Serialize(stream, value, Json);
        return stream.ToArray();
    }
    private sealed class LimitedStream(int limit) : MemoryStream
    {
        private void Check(int count) { if (Length + count > limit) throw new McpOutputLimitException(); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
    }
}

internal sealed class McpOutputLimitException : Exception;
