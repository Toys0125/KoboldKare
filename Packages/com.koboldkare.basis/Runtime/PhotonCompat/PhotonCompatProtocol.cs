using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KoboldKare.Basis.PhotonCompat
{
    internal enum PhotonCompatMessageKind : byte
    {
        Rpc = 1,
        Observable = 2,
        OwnershipRequest = 3,
    }

    internal readonly struct PhotonCompatRpcMessage
    {
        public readonly byte SubViewIndex;
        public readonly bool Buffered;
        public readonly ulong PersistentMessageId;
        public readonly string MethodName;
        public readonly object[] Arguments;

        public PhotonCompatRpcMessage(
            byte subViewIndex,
            bool buffered,
            ulong persistentMessageId,
            string methodName,
            object[] arguments)
        {
            SubViewIndex = subViewIndex;
            Buffered = buffered;
            PersistentMessageId = persistentMessageId;
            MethodName = methodName;
            Arguments = arguments ?? Array.Empty<object>();
        }
    }

    internal readonly struct PhotonCompatObservableMessage
    {
        public readonly byte SubViewIndex;
        public readonly uint Sequence;
        public readonly object[] Values;

        public PhotonCompatObservableMessage(byte subViewIndex, uint sequence, object[] values)
        {
            SubViewIndex = subViewIndex;
            Sequence = sequence;
            Values = values ?? Array.Empty<object>();
        }
    }

    internal static class PhotonCompatProtocol
    {
        public const ushort EntityRouteId = 0x4B50;
        private const byte Version = 1;
        private const int MaxRpcMethodNameBytes = 128;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeRpc(
            byte subViewIndex,
            bool buffered,
            ulong persistentMessageId,
            string methodName,
            IReadOnlyList<object> arguments)
        {
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("RPC method name is required.", nameof(methodName));
            }

            if (buffered && persistentMessageId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(persistentMessageId), "Buffered RPCs require a non-zero persistent message id.");
            }
            if (!buffered)
            {
                persistentMessageId = 0;
            }

            using MemoryStream stream = new MemoryStream(136);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write((byte)PhotonCompatMessageKind.Rpc);
            writer.Write(subViewIndex);
            writer.Write(buffered);
            writer.Write(persistentMessageId);
            PhotonCompatValueCodec.WriteBoundedString(writer, methodName, MaxRpcMethodNameBytes);
            PhotonCompatValueCodec.WriteValues(writer, arguments);
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeObservable(byte subViewIndex, uint sequence, IReadOnlyList<object> values)
        {
            using MemoryStream stream = new MemoryStream(128);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Version);
            writer.Write((byte)PhotonCompatMessageKind.Observable);
            writer.Write(subViewIndex);
            writer.Write(sequence);
            PhotonCompatValueCodec.WriteValues(writer, values);
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeOwnershipRequest(byte subViewIndex)
        {
            return new[]
            {
                Version,
                (byte)PhotonCompatMessageKind.OwnershipRequest,
                subViewIndex,
            };
        }

        public static bool TryGetKind(byte[] payload, out PhotonCompatMessageKind kind)
        {
            kind = default;
            if (payload == null || payload.Length < 2 || payload[0] != Version)
            {
                return false;
            }
            kind = (PhotonCompatMessageKind)payload[1];
            return kind == PhotonCompatMessageKind.Rpc ||
                   kind == PhotonCompatMessageKind.Observable ||
                   kind == PhotonCompatMessageKind.OwnershipRequest;
        }

        public static bool TryDecodeRpc(byte[] payload, out PhotonCompatRpcMessage message)
        {
            message = default;
            if (!TryOpen(payload, PhotonCompatMessageKind.Rpc, out MemoryStream stream, out BinaryReader reader))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                try
                {
                    byte subViewIndex = reader.ReadByte();
                    bool buffered = reader.ReadBoolean();
                    ulong persistentMessageId = reader.ReadUInt64();
                    if ((buffered && persistentMessageId == 0) ||
                        (!buffered && persistentMessageId != 0) ||
                        !PhotonCompatValueCodec.TryReadBoundedString(reader, out string methodName, MaxRpcMethodNameBytes) ||
                        string.IsNullOrWhiteSpace(methodName) ||
                        !PhotonCompatValueCodec.TryReadValues(reader, out object[] arguments) ||
                        stream.Position != stream.Length)
                    {
                        return false;
                    }
                    message = new PhotonCompatRpcMessage(
                        subViewIndex,
                        buffered,
                        persistentMessageId,
                        methodName,
                        arguments);
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

        public static bool TryDecodeOwnershipRequest(byte[] payload, out byte subViewIndex)
        {
            subViewIndex = default;
            if (payload == null || payload.Length != 3 ||
                payload[0] != Version ||
                payload[1] != (byte)PhotonCompatMessageKind.OwnershipRequest)
            {
                return false;
            }
            subViewIndex = payload[2];
            return true;
        }

        public static bool TryDecodeObservable(byte[] payload, out PhotonCompatObservableMessage message)
        {
            message = default;
            if (!TryOpen(payload, PhotonCompatMessageKind.Observable, out MemoryStream stream, out BinaryReader reader))
            {
                return false;
            }

            using (stream)
            using (reader)
            {
                try
                {
                    byte subViewIndex = reader.ReadByte();
                    uint sequence = reader.ReadUInt32();
                    if (!PhotonCompatValueCodec.TryReadValues(reader, out object[] values) ||
                        stream.Position != stream.Length)
                    {
                        return false;
                    }
                    message = new PhotonCompatObservableMessage(subViewIndex, sequence, values);
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

        private static bool TryOpen(
            byte[] payload,
            PhotonCompatMessageKind expectedKind,
            out MemoryStream stream,
            out BinaryReader reader)
        {
            stream = null;
            reader = null;
            if (payload == null || payload.Length < 3)
            {
                return false;
            }

            stream = new MemoryStream(payload, false);
            reader = new BinaryReader(stream, Utf8, true);
            try
            {
                if (reader.ReadByte() != Version || reader.ReadByte() != (byte)expectedKind)
                {
                    reader.Dispose();
                    stream.Dispose();
                    reader = null;
                    stream = null;
                    return false;
                }
                return true;
            }
            catch
            {
                reader.Dispose();
                stream.Dispose();
                reader = null;
                stream = null;
                return false;
            }
        }
    }
}
