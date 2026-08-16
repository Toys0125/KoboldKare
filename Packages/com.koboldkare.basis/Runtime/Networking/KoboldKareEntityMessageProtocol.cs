using System;
using System.IO;
using System.Text;

namespace KoboldKare.Basis.Networking
{
    public readonly struct KoboldKareEntityMessage
    {
        public readonly ushort RouteId;
        public readonly byte[] Payload;

        public KoboldKareEntityMessage(ushort routeId, byte[] payload)
        {
            RouteId = routeId;
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Small bounded envelope for gameplay messages carried by an individual
    /// <see cref="KoboldKareNetworkEntity"/> Basis network channel.
    /// </summary>
    public static class KoboldKareEntityMessageProtocol
    {
        public const int MaxPayloadBytes = 64 * 1024;
        private const byte Version = 1;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] Encode(ushort routeId, byte[] payload)
        {
            int payloadLength = payload?.Length ?? 0;
            if (payloadLength > MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(payload), $"Payload exceeds {MaxPayloadBytes} bytes.");
            }

            using MemoryStream stream = new MemoryStream(7 + payloadLength);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write(routeId);
            writer.Write(payloadLength);
            if (payloadLength > 0)
            {
                writer.Write(payload);
            }
            writer.Flush();
            return stream.ToArray();
        }

        public static bool TryDecode(byte[] buffer, out KoboldKareEntityMessage message)
        {
            message = default;
            if (buffer == null || buffer.Length < 7)
            {
                return false;
            }

            try
            {
                using MemoryStream stream = new MemoryStream(buffer, false);
                using BinaryReader reader = new BinaryReader(stream, Utf8, true);
                if (reader.ReadByte() != Version)
                {
                    return false;
                }

                ushort routeId = reader.ReadUInt16();
                int payloadLength = reader.ReadInt32();
                long remaining = stream.Length - stream.Position;
                if (payloadLength < 0 || payloadLength > MaxPayloadBytes || payloadLength != remaining)
                {
                    return false;
                }

                byte[] payload = payloadLength == 0 ? Array.Empty<byte>() : reader.ReadBytes(payloadLength);
                if (payload.Length != payloadLength)
                {
                    return false;
                }

                message = new KoboldKareEntityMessage(routeId, payload);
                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is ArgumentException)
            {
                return false;
            }
        }
    }
}
