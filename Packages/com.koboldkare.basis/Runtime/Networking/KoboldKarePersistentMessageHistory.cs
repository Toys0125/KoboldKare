using System;
using System.Collections.Generic;
using System.IO;

namespace KoboldKare.Basis.Networking
{
    public readonly struct KoboldKarePersistentMessage
    {
        public readonly ushort RouteId;
        public readonly byte[] Payload;

        public KoboldKarePersistentMessage(ushort routeId, byte[] payload)
        {
            RouteId = routeId;
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Bounded opaque application-message history carried inside late-join spawn snapshots.
    /// This replaces PUN's server-side buffered RPC replay without coupling the world lifecycle
    /// protocol to any particular gameplay RPC implementation.
    /// </summary>
    public static class KoboldKarePersistentMessageHistory
    {
        public const int MaxHistoryBytes = 64 * 1024;
        public const int MaxMessages = 64;

        public static bool TryAppend(
            byte[] existingHistory,
            ushort routeId,
            byte[] payload,
            out byte[] updatedHistory)
        {
            updatedHistory = existingHistory ?? Array.Empty<byte>();
            if (payload == null)
            {
                payload = Array.Empty<byte>();
            }
            if (payload.Length > KoboldKareEntityMessageProtocol.MaxPayloadBytes)
            {
                return false;
            }

            if (!TryDecode(existingHistory, out List<KoboldKarePersistentMessage> messages))
            {
                return false;
            }
            if (messages.Count >= MaxMessages)
            {
                return false;
            }

            int projectedLength = 1 + 1;
            for (int i = 0; i < messages.Count; i++)
            {
                projectedLength += 2 + 4 + messages[i].Payload.Length;
            }
            projectedLength += 2 + 4 + payload.Length;
            if (projectedLength > MaxHistoryBytes)
            {
                return false;
            }

            messages.Add(new KoboldKarePersistentMessage(routeId, payload));
            using MemoryStream stream = new MemoryStream(projectedLength);
            using BinaryWriter writer = new BinaryWriter(stream);
            writer.Write((byte)1);
            writer.Write((byte)messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                KoboldKarePersistentMessage message = messages[i];
                writer.Write(message.RouteId);
                writer.Write(message.Payload.Length);
                if (message.Payload.Length > 0)
                {
                    writer.Write(message.Payload);
                }
            }
            writer.Flush();
            updatedHistory = stream.ToArray();
            return true;
        }

        public static bool TryDecode(byte[] history, out List<KoboldKarePersistentMessage> messages)
        {
            messages = new List<KoboldKarePersistentMessage>();
            if (history == null || history.Length == 0)
            {
                return true;
            }
            if (history.Length > MaxHistoryBytes || history.Length < 2)
            {
                return false;
            }

            try
            {
                using MemoryStream stream = new MemoryStream(history, false);
                using BinaryReader reader = new BinaryReader(stream);
                if (reader.ReadByte() != 1)
                {
                    return false;
                }

                int count = reader.ReadByte();
                if (count > MaxMessages)
                {
                    return false;
                }

                for (int i = 0; i < count; i++)
                {
                    ushort routeId = reader.ReadUInt16();
                    int length = reader.ReadInt32();
                    long remaining = stream.Length - stream.Position;
                    if (length < 0 ||
                        length > KoboldKareEntityMessageProtocol.MaxPayloadBytes ||
                        length > remaining)
                    {
                        return false;
                    }

                    byte[] payload = reader.ReadBytes(length);
                    if (payload.Length != length)
                    {
                        return false;
                    }
                    messages.Add(new KoboldKarePersistentMessage(routeId, payload));
                }

                return stream.Position == stream.Length;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is ArgumentException)
            {
                messages.Clear();
                return false;
            }
        }
    }
}
