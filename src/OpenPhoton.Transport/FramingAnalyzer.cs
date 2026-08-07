using System.Buffers.Binary;
using System.Text;

namespace OpenPhoton.Transport;

/// <summary>
/// Infers a TCP stream's message framing by brute-force search.
/// </summary>
/// <remarks>
/// <para>
/// We know what a Photon <em>message</em> starts with (0xF3, 0xF0, 0xF5, 'P', 'G') but not
/// what delimits messages on a TCP stream. So rather than guess, enumerate plausible
/// framings and test each one by walking the whole buffer with it.
/// </para>
/// <para>
/// The walk is what makes this convincing. A length field can match one message by
/// coincidence; it will not chain through several messages and land exactly on the end of
/// the buffer by coincidence. Any framing that consumes the buffer exactly, with a valid
/// magic byte at every computed boundary, is almost certainly the real one.
/// </para>
/// <para>
/// <b>Expect more than one answer.</b> A 32-bit length below 65536 carries two zero high
/// bytes, so a 16-bit read of its low half — two bytes further along, in a header two bytes
/// shorter — produces the same number and chains just as well. Nothing in the stream
/// separates them. This returns every framing that fits, ranked by how much evidence
/// supports it, rather than picking one and implying a certainty the bytes do not justify.
/// Deeper chains are stronger evidence, so message count dominates the ranking.
/// </para>
/// </remarks>
public static class FramingAnalyzer
{
    static readonly byte[] MagicBytes = [0xF3, 0xF0, 0xF5, (byte)'P', (byte)'G'];

    public sealed record Candidate(
        int HeaderLength,
        int LengthOffset,
        int LengthWidth,
        bool BigEndian,
        bool LengthIncludesHeader,
        int MessageCount)
    {
        public override string ToString()
        {
            var endian = BigEndian ? "BE" : "LE";
            var counts = LengthIncludesHeader ? "whole record" : "payload after header";
            return $"header {HeaderLength}B, uint{LengthWidth * 8} {endian} at offset {LengthOffset}, "
                 + $"length counts {counts} ({MessageCount} messages chained)";
        }
    }

    static bool IsMagic(byte b) => Array.IndexOf(MagicBytes, b) >= 0;

    /// <summary>Ranks framings that fully explain <paramref name="data"/>, best first.</summary>
    public static IReadOnlyList<Candidate> Search(ReadOnlySpan<byte> data)
    {
        var hits = new List<Candidate>();
        if (data.Length == 0) return hits;

        if (IsMagic(data[0]))
        {
            var count = CountBareMessages(data);
            if (count > 0) hits.Add(new Candidate(0, 0, 0, true, false, count));
        }

        for (var headerLen = 1; headerLen <= 12; headerLen++)
        {
            foreach (var width in (int[])[2, 4])
            {
                for (var lenOffset = 0; lenOffset + width <= headerLen; lenOffset++)
                {
                    foreach (var bigEndian in (bool[])[true, false])
                    {
                        foreach (var includesHeader in (bool[])[true, false])
                        {
                            var count = Walk(data, headerLen, lenOffset, width, bigEndian, includesHeader);
                            if (count > 0)
                                hits.Add(new Candidate(headerLen, lenOffset, width, bigEndian, includesHeader, count));
                        }
                    }
                }
            }
        }

        return hits
            .OrderByDescending(h => h.MessageCount)
            .ThenBy(h => h.HeaderLength)
            .ToList();
    }

    /// <summary>Walks the buffer under one framing. Returns messages consumed, or 0 if it does not fit exactly.</summary>
    static int Walk(ReadOnlySpan<byte> data, int headerLen, int lenOffset, int width, bool bigEndian, bool includesHeader)
    {
        var pos = 0;
        var messages = 0;

        while (pos < data.Length)
        {
            if (pos + headerLen > data.Length) return 0;

            var field = data.Slice(pos + lenOffset, width);
            long declared = width == 2
                ? (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(field) : BinaryPrimitives.ReadUInt16LittleEndian(field))
                : (bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(field) : BinaryPrimitives.ReadUInt32LittleEndian(field));

            var recordLen = includesHeader ? declared : declared + headerLen;
            if (recordLen <= headerLen) return 0;
            if (pos + recordLen > data.Length) return 0;

            if (!IsMagic(data[pos + headerLen])) return 0;

            pos += (int)recordLen;
            messages++;
        }

        return pos == data.Length ? messages : 0;
    }

    /// <summary>Counts messages when there is no framing, using fixed sizes we already know.</summary>
    static int CountBareMessages(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        var count = 0;
        while (pos < data.Length)
        {
            if (!IsMagic(data[pos])) return 0;
            if (data[pos] != 0xF0) return count > 0 || pos == 0 ? -1 : 0;
            pos += 9;
            count++;
            if (pos > data.Length) return 0;
        }
        return pos == data.Length ? count : 0;
    }

    /// <summary>Annotated hex dump.</summary>
    public static string Dump(ReadOnlySpan<byte> data, int maxBytes = 256)
    {
        var sb = new StringBuilder();
        var n = Math.Min(data.Length, maxBytes);

        for (var row = 0; row < n; row += 16)
        {
            sb.Append($"  {row:X4}  ");
            for (var i = 0; i < 16; i++)
            {
                if (row + i < n) sb.Append($"{data[row + i]:X2} ");
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (var i = 0; i < 16 && row + i < n; i++)
            {
                var c = data[row + i];
                sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
            }
            sb.AppendLine();
        }

        if (data.Length > n) sb.AppendLine($"  ... {data.Length - n} more bytes");
        return sb.ToString();
    }

    /// <summary>Reports where recognisable message-start bytes appear.</summary>
    public static string MagicPositions(ReadOnlySpan<byte> data, int maxBytes = 64)
    {
        var found = new List<string>();
        var n = Math.Min(data.Length, maxBytes);
        for (var i = 0; i < n; i++)
            if (IsMagic(data[i]))
                found.Add($"0x{data[i]:X2}@{i}");

        return found.Count > 0 ? string.Join(", ", found) : "(none in range)";
    }
}
