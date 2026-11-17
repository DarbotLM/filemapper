using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileMapper.Engine;

/// <summary>Exact typed request boundary shared by the learning CLI and local HTTP service.</summary>
public static class NativeRequestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 64
    };
    public static T Read<T>(JsonElement element)
    {
        Check(element);
        try { return element.Deserialize<T>(Options) ?? throw new ArgumentException("A non-null request is required."); }
        catch (JsonException ex) { throw new ArgumentException("Request has an unknown, missing, null, or incorrectly typed field.", ex); }
    }
    public static async Task<T> ReadAsync<T>(Stream input, int maximumBytes, CancellationToken cancellationToken = default)
    {
        if (maximumBytes is < 1 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var buffer = new MemoryStream(); var bytes = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(bytes, cancellationToken); if (read == 0) break;
            if (buffer.Length + read > maximumBytes) throw new ArgumentException($"Request exceeds {maximumBytes} bytes.");
            buffer.Write(bytes, 0, read);
        }
        try { using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 }); return Read<T>(document.RootElement); }
        catch (JsonException ex) { throw new ArgumentException("Request is not valid JSON.", ex); }
    }
    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!keys.Add(property.Name)) throw new ArgumentException("Duplicate JSON properties are not supported."); Check(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
    }
}
