using System;
using System.IO;
using System.Text;

namespace KoboldKare.Basis.Networking
{
    public enum KoboldKareSessionMessageKind : byte
    {
        StateSnapshot = 1,
        StateRequest = 2,
        Chat = 3,
        Kick = 4,
    }

    [Serializable]
    public struct KoboldKareSessionState
    {
        public string MapName;
        public string ModListJson;
        public bool CheatsEnabled;
        public uint Revision;
    }

    public readonly struct KoboldKareSessionMessage
    {
        public readonly KoboldKareSessionMessageKind Kind;
        public readonly KoboldKareSessionState State;
        public readonly string Text;

        public KoboldKareSessionMessage(
            KoboldKareSessionMessageKind kind,
            KoboldKareSessionState state,
            string text)
        {
            Kind = kind;
            State = state;
            Text = text;
        }
    }

    public static class KoboldKareSessionProtocol
    {
        public const int MaxMapNameBytes = 256;
        public const int MaxModListBytes = 32 * 1024;
        public const int MaxChatBytes = 2048;
        public const int MaxKickReasonBytes = 512;
        private const byte Version = 1;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeState(in KoboldKareSessionState state)
        {
            using MemoryStream stream = new MemoryStream(256 + (state.ModListJson?.Length ?? 0));
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write((byte)KoboldKareSessionMessageKind.StateSnapshot);
            writer.Write(state.Revision);
            WriteString(writer, state.MapName, MaxMapNameBytes, nameof(state.MapName));
            WriteString(writer, state.ModListJson, MaxModListBytes, nameof(state.ModListJson));
            writer.Write(state.CheatsEnabled);
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeStateRequest() =>
            new[] { Version, (byte)KoboldKareSessionMessageKind.StateRequest };

        public static byte[] EncodeChat(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("Chat message cannot be empty.", nameof(text));
            }

            using MemoryStream stream = new MemoryStream(64 + text.Length);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write((byte)KoboldKareSessionMessageKind.Chat);
            WriteString(writer, text, MaxChatBytes, nameof(text));
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeKick(string reason)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "Removed by the server host." : reason.Trim();
            using MemoryStream stream = new MemoryStream(64 + reason.Length);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write((byte)KoboldKareSessionMessageKind.Kick);
            WriteString(writer, reason, MaxKickReasonBytes, nameof(reason));
            writer.Flush();
            return stream.ToArray();
        }

        public static bool TryDecode(byte[] buffer, out KoboldKareSessionMessage message)
        {
            message = default;
            if (buffer == null || buffer.Length < 2)
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

                KoboldKareSessionMessageKind kind = (KoboldKareSessionMessageKind)reader.ReadByte();
                switch (kind)
                {
                    case KoboldKareSessionMessageKind.StateSnapshot:
                    {
                        KoboldKareSessionState state = new KoboldKareSessionState
                        {
                            Revision = reader.ReadUInt32(),
                            MapName = ReadString(reader, MaxMapNameBytes),
                            ModListJson = ReadString(reader, MaxModListBytes),
                            CheatsEnabled = reader.ReadBoolean(),
                        };
                        if (string.IsNullOrWhiteSpace(state.MapName) || stream.Position != stream.Length)
                        {
                            return false;
                        }
                        message = new KoboldKareSessionMessage(kind, state, null);
                        return true;
                    }
                    case KoboldKareSessionMessageKind.StateRequest:
                        if (stream.Position != stream.Length)
                        {
                            return false;
                        }
                        message = new KoboldKareSessionMessage(kind, default, null);
                        return true;
                    case KoboldKareSessionMessageKind.Chat:
                    {
                        string text = ReadString(reader, MaxChatBytes);
                        if (string.IsNullOrWhiteSpace(text) || stream.Position != stream.Length)
                        {
                            return false;
                        }
                        message = new KoboldKareSessionMessage(kind, default, text);
                        return true;
                    }
                    case KoboldKareSessionMessageKind.Kick:
                    {
                        string reason = ReadString(reader, MaxKickReasonBytes);
                        if (string.IsNullOrWhiteSpace(reason) || stream.Position != stream.Length)
                        {
                            return false;
                        }
                        message = new KoboldKareSessionMessage(kind, default, reason);
                        return true;
                    }
                    default:
                        return false;
                }
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is InvalidDataException ||
                exception is IOException ||
                exception is DecoderFallbackException ||
                exception is ArgumentException)
            {
                return false;
            }
        }

        private static void WriteString(BinaryWriter writer, string value, int maxBytes, string parameterName)
        {
            value ??= string.Empty;
            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > maxBytes || bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(parameterName, $"UTF-8 value exceeds {maxBytes} bytes.");
            }
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maxBytes)
        {
            ushort length = reader.ReadUInt16();
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (length > maxBytes || length > remaining)
            {
                throw new InvalidDataException("String exceeds session protocol bounds.");
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }
            return Utf8.GetString(bytes);
        }
    }
}
