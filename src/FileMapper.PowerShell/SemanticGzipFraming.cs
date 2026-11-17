namespace FileMapper.PowerShell;

// GZipStream intentionally tolerates trailing non-gzip bytes. This separate RFC 1951/1952 framing
// pass establishes the exact member boundary without materializing inflated bytes or depending on
// the decompressor's private input buffering. The content pass still checks CRC32 and exact NDJSON.
// Primary format references: https://www.rfc-editor.org/rfc/rfc1951.html (3.1.1, 3.2.2–3.2.7)
// and https://www.rfc-editor.org/rfc/rfc1952.html (2.3.1). This export protocol deliberately accepts
// one member only; RFC 1952's general gzip file format also permits concatenated members.
internal static class SemanticGzipFraming
{
    internal sealed record Trailer(uint Crc32, uint Size, long ExpandedBytes);
    private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
    private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
    private static readonly int[] DistanceBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
    private static readonly int[] DistanceExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
    private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
    private static readonly Huffman FixedLiterals = new(Enumerable.Range(0, 288).Select(i => i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8).ToArray());
    private static readonly Huffman FixedDistances = new(Enumerable.Repeat(5, 32).ToArray());

    internal static Trailer Validate(Stream stream, long maximumExpandedBytes, CancellationToken token)
    {
        var reader = new Bits(stream, token);
        var headerChecksum = new SemanticGzipChecksum();
        var headerBytes = 0;
        int HeaderByte()
        {
            if (++headerBytes > 1_048_576) throw Invalid("Gzip header exceeds 1 MiB.");
            var value = reader.Read(8);
            headerChecksum.AppendByte((byte)value);
            return value;
        }
        if (HeaderByte() != 0x1f || HeaderByte() != 0x8b || HeaderByte() != 8) throw Invalid("Expected a gzip DEFLATE member.");
        var flags = HeaderByte();
        if ((flags & 0xe0) != 0) throw Invalid("Reserved gzip header flags are set.");
        for (var i = 0; i < 6; i++) _ = HeaderByte(); // MTIME, XFL, OS
        if ((flags & 4) != 0)
        {
            var extraLength = HeaderByte() | HeaderByte() << 8;
            for (var i = 0; i < extraLength; i++) _ = HeaderByte();
        }
        if ((flags & 8) != 0) while (HeaderByte() != 0) { }
        if ((flags & 16) != 0) while (HeaderByte() != 0) { }
        if ((flags & 2) != 0 && reader.Read(16) != (headerChecksum.Crc32 & 0xffff)) throw Invalid("Invalid gzip header CRC16.");

        long expanded = 0;
        void Add(int count)
        {
            if (count > maximumExpandedBytes - expanded) throw Invalid("Decoded export exceeds MaximumExpandedBytes.");
            expanded += count;
        }
        bool final;
        do
        {
            token.ThrowIfCancellationRequested();
            final = reader.Read(1) != 0;
            switch (reader.Read(2))
            {
                case 0:
                    reader.Align();
                    var length = reader.Read(16); var complement = reader.Read(16);
                    if ((length ^ 0xffff) != complement) throw Invalid("Invalid stored DEFLATE block length.");
                    Add(length); reader.SkipAligned(length);
                    break;
                case 1: Decode(FixedLiterals, FixedDistances); break;
                case 2:
                    var literalCount = reader.Read(5) + 257;
                    var distanceCount = reader.Read(5) + 1;
                    var codeCount = reader.Read(4) + 4;
                    if (literalCount > 286) throw Invalid("Invalid DEFLATE literal alphabet.");
                    var codeLengths = new int[19];
                    for (var i = 0; i < codeCount; i++) codeLengths[CodeLengthOrder[i]] = reader.Read(3);
                    var codeTree = new Huffman(codeLengths);
                    var lengths = new int[literalCount + distanceCount];
                    var position = 0;
                    while (position < lengths.Length)
                    {
                        var symbol = codeTree.Decode(reader);
                        if (symbol <= 15) { lengths[position++] = symbol; continue; }
                        int count, value;
                        if (symbol == 16)
                        {
                            if (position == 0) throw Invalid("DEFLATE repeat has no preceding length.");
                            count = reader.Read(2) + 3; value = lengths[position - 1];
                        }
                        else if (symbol == 17) { count = reader.Read(3) + 3; value = 0; }
                        else if (symbol == 18) { count = reader.Read(7) + 11; value = 0; }
                        else throw Invalid("Invalid DEFLATE code-length symbol.");
                        if (count > lengths.Length - position) throw Invalid("DEFLATE length repeat exceeds its alphabet.");
                        Array.Fill(lengths, value, position, count); position += count;
                    }
                    if (lengths[256] == 0) throw Invalid("DEFLATE alphabet has no end-of-block symbol.");
                    Decode(new Huffman(lengths[..literalCount]), new Huffman(lengths[literalCount..]));
                    break;
                default: throw Invalid("Reserved DEFLATE block type.");
            }
        } while (!final);
        reader.Align();
        var crc = reader.ReadUInt32(); var size = reader.ReadUInt32();
        if (size != unchecked((uint)expanded)) throw Invalid("Gzip trailer size differs from the DEFLATE member.");
        if (!reader.End()) throw Invalid("Data follows the single complete gzip member.");
        token.ThrowIfCancellationRequested();
        return new(crc, size, expanded);

        void Decode(Huffman literals, Huffman distances)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var symbol = literals.Decode(reader);
                if (symbol < 256) { Add(1); continue; }
                if (symbol == 256) return;
                if (symbol > 285) throw Invalid("Reserved DEFLATE length symbol.");
                var index = symbol - 257;
                var length = LengthBase[index] + reader.Read(LengthExtra[index]);
                var distanceSymbol = distances.Decode(reader);
                if (distanceSymbol >= 30) throw Invalid("Reserved DEFLATE distance symbol.");
                var distance = DistanceBase[distanceSymbol] + reader.Read(DistanceExtra[distanceSymbol]);
                if (distance > expanded) throw Invalid("DEFLATE distance precedes the start of the member.");
                Add(length);
            }
        }
    }

    private sealed class Huffman
    {
        private readonly int[] counts = new int[16];
        private readonly int[] firstCodes = new int[16];
        private readonly int[] firstSymbols = new int[16];
        private readonly int[] symbols;
        internal Huffman(int[] lengths)
        {
            foreach (var length in lengths)
            {
                if (length is < 0 or > 15) throw Invalid("Invalid DEFLATE code length.");
                if (length != 0) counts[length]++;
            }
            var available = 1; var code = 0; var offset = 0;
            for (var bits = 1; bits < counts.Length; bits++)
            {
                available = (available << 1) - counts[bits];
                if (available < 0) throw Invalid("Oversubscribed DEFLATE alphabet.");
                code = (code + counts[bits - 1]) << 1;
                firstCodes[bits] = code; firstSymbols[bits] = offset; offset += counts[bits];
            }
            symbols = new int[offset];
            var next = (int[])firstSymbols.Clone();
            for (var symbol = 0; symbol < lengths.Length; symbol++)
                if (lengths[symbol] != 0) symbols[next[lengths[symbol]]++] = symbol;
        }
        internal int Decode(Bits reader)
        {
            var code = 0;
            for (var bits = 1; bits < counts.Length; bits++)
            {
                code = (code << 1) | reader.Read(1);
                var index = code - firstCodes[bits];
                if (index >= 0 && index < counts[bits]) return symbols[firstSymbols[bits] + index];
            }
            throw Invalid("Invalid DEFLATE Huffman code.");
        }
    }

    private sealed class Bits(Stream stream, CancellationToken token)
    {
        private readonly byte[] buffer = new byte[65536];
        private int position, available, bitCount;
        private uint bits;
        private int Byte()
        {
            if (position == available)
            {
                token.ThrowIfCancellationRequested();
                available = stream.ReadAsync(buffer, token).AsTask().GetAwaiter().GetResult(); position = 0;
                if (available == 0) return -1;
            }
            return buffer[position++];
        }
        internal int Read(int count)
        {
            while (bitCount < count)
            {
                var value = Byte();
                if (value < 0) throw Invalid("Truncated gzip DEFLATE member or trailer.");
                bits |= (uint)value << bitCount; bitCount += 8;
            }
            var result = (int)(bits & ((1u << count) - 1)); bits >>= count; bitCount -= count; return result;
        }
        internal uint ReadUInt32() => (uint)Read(16) | (uint)Read(16) << 16;
        internal void Align() { bits = 0; bitCount = 0; }
        internal void SkipAligned(int count)
        {
            for (var i = 0; i < count; i++) if (Byte() < 0) throw Invalid("Truncated stored DEFLATE block.");
        }
        internal bool End() => Byte() < 0;
    }
    private static InvalidDataException Invalid(string message) => new(message);
}
