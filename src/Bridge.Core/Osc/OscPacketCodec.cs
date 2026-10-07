using System.Buffers.Binary;
using System.Text;

namespace Bridge.Core.Osc;

public static class OscPacketCodec
{
    private static readonly byte[] BundlePrefix = Encoding.ASCII.GetBytes("#bundle\0");

    public static byte[] Encode(OscMessage message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Address);
        if (!message.Address.StartsWith('/'))
        {
            throw new ArgumentException("OSC address must start with '/'.", nameof(message));
        }

        using var stream = new MemoryStream();
        WritePaddedString(stream, message.Address);

        var typeTags = new StringBuilder(",");
        foreach (var argument in message.Arguments)
        {
            typeTags.Append(argument switch
            {
                int => 'i',
                float => 'f',
                string => 's',
                true => 'T',
                false => 'F',
                _ => throw new NotSupportedException($"Unsupported OSC argument type: {argument?.GetType().FullName ?? "null"}.")
            });
        }

        WritePaddedString(stream, typeTags.ToString());

        Span<byte> number = stackalloc byte[4];
        foreach (var argument in message.Arguments)
        {
            switch (argument)
            {
                case int integer:
                    BinaryPrimitives.WriteInt32BigEndian(number, integer);
                    stream.Write(number);
                    break;
                case float single:
                    BinaryPrimitives.WriteInt32BigEndian(number, BitConverter.SingleToInt32Bits(single));
                    stream.Write(number);
                    break;
                case string text:
                    WritePaddedString(stream, text);
                    break;
                case bool:
                    break;
            }
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        var messages = new List<OscMessage>();
        DecodePacket(packet, messages);
        return messages;
    }

    private static void DecodePacket(ReadOnlySpan<byte> packet, ICollection<OscMessage> messages)
    {
        if (packet.Length >= BundlePrefix.Length && packet[..BundlePrefix.Length].SequenceEqual(BundlePrefix))
        {
            DecodeBundle(packet, messages);
            return;
        }

        if (TryDecodeMessage(packet, out var message))
        {
            messages.Add(message);
        }
    }

    private static void DecodeBundle(ReadOnlySpan<byte> packet, ICollection<OscMessage> messages)
    {
        const int bundleHeaderLength = 16;
        if (packet.Length < bundleHeaderLength)
        {
            return;
        }

        var offset = bundleHeaderLength;
        while (offset + 4 <= packet.Length)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
            offset += 4;
            if (size <= 0 || offset + size > packet.Length)
            {
                return;
            }

            DecodePacket(packet.Slice(offset, size), messages);
            offset += size;
        }
    }

    private static bool TryDecodeMessage(ReadOnlySpan<byte> packet, out OscMessage message)
    {
        message = new OscMessage("/");
        var offset = 0;
        if (!TryReadPaddedString(packet, ref offset, out var address) ||
            string.IsNullOrWhiteSpace(address) ||
            !address.StartsWith('/') ||
            !TryReadPaddedString(packet, ref offset, out var tags) ||
            !tags.StartsWith(','))
        {
            return false;
        }

        var arguments = new List<object>();
        foreach (var tag in tags.AsSpan(1))
        {
            switch (tag)
            {
                case 'i':
                    if (!TryReadInt32(packet, ref offset, out var integer)) return false;
                    arguments.Add(integer);
                    break;
                case 'f':
                    if (!TryReadInt32(packet, ref offset, out var floatBits)) return false;
                    arguments.Add(BitConverter.Int32BitsToSingle(floatBits));
                    break;
                case 's':
                    if (!TryReadPaddedString(packet, ref offset, out var text)) return false;
                    arguments.Add(text);
                    break;
                case 'T':
                    arguments.Add(true);
                    break;
                case 'F':
                    arguments.Add(false);
                    break;
                default:
                    return false;
            }
        }

        message = new OscMessage(address, arguments);
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> packet, ref int offset, out int value)
    {
        if (offset + 4 > packet.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(offset, 4));
        offset += 4;
        return true;
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static bool TryReadPaddedString(ReadOnlySpan<byte> packet, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset >= packet.Length)
        {
            return false;
        }

        var remainder = packet[offset..];
        var terminator = remainder.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(remainder[..terminator]);
        offset += terminator + 1;
        offset = (offset + 3) & ~3;
        return offset <= packet.Length;
    }
}
