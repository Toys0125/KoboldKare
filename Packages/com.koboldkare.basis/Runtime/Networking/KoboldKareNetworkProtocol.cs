using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace KoboldKare.Basis.Networking
{
    public enum KoboldKareNetworkMessageKind : byte
    {
        SpawnCommit = 1,
        DespawnCommit = 2,
        SnapshotRequest = 3,
        SnapshotSpawn = 4,
        SnapshotComplete = 5,
        SnapshotPersistentApplicationState = 6,
    }

    [Serializable]
    public struct KoboldKareSpawnDescriptor
    {
        public string InstanceId;
        public string PrefabId;
        public ushort OwnerPlayerId;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        public byte[] InitialPayload;
        public byte[] PersistentApplicationPayload;
    }

    public readonly struct KoboldKareDecodedNetworkMessage
    {
        public readonly KoboldKareNetworkMessageKind Kind;
        public readonly KoboldKareSpawnDescriptor Spawn;
        public readonly string InstanceId;
        public readonly string PersistentApplicationSceneName;
        public readonly byte[] PersistentApplicationPayload;

        public KoboldKareDecodedNetworkMessage(
            KoboldKareNetworkMessageKind kind,
            KoboldKareSpawnDescriptor spawn,
            string instanceId,
            byte[] persistentApplicationPayload = null,
            string persistentApplicationSceneName = null)
        {
            Kind = kind;
            Spawn = spawn;
            InstanceId = instanceId;
            PersistentApplicationSceneName = persistentApplicationSceneName ?? string.Empty;
            PersistentApplicationPayload = persistentApplicationPayload ?? Array.Empty<byte>();
        }
    }

    public static class KoboldKareNetworkProtocol
    {
        public const int MaxInstanceIdBytes = 128;
        public const int MaxPrefabIdBytes = 256;
        public const int MaxInitialPayloadBytes = 64 * 1024;

        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        public static byte[] EncodeSpawn(KoboldKareNetworkMessageKind kind, in KoboldKareSpawnDescriptor descriptor)
        {
            if (kind != KoboldKareNetworkMessageKind.SpawnCommit &&
                kind != KoboldKareNetworkMessageKind.SnapshotSpawn)
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Message kind is not a spawn message.");
            }
            if (string.IsNullOrWhiteSpace(descriptor.InstanceId))
            {
                throw new ArgumentException("A non-empty instance id is required.", nameof(descriptor));
            }
            if (string.IsNullOrWhiteSpace(descriptor.PrefabId))
            {
                throw new ArgumentException("A non-empty prefab id is required.", nameof(descriptor));
            }
            if (!IsFinite(descriptor.Position) || !IsFinite(descriptor.Rotation) || !IsFinite(descriptor.Scale))
            {
                throw new ArgumentException("Spawn transform values must be finite.", nameof(descriptor));
            }

            int initialPayloadLength = descriptor.InitialPayload?.Length ?? 0;
            if (initialPayloadLength > MaxInitialPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor.InitialPayload),
                    $"Payload exceeds {MaxInitialPayloadBytes} bytes.");
            }
            int persistentPayloadLength = descriptor.PersistentApplicationPayload?.Length ?? 0;
            if (persistentPayloadLength > KoboldKarePersistentMessageHistory.MaxHistoryBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(descriptor.PersistentApplicationPayload),
                    $"Persistent application payload exceeds {KoboldKarePersistentMessageHistory.MaxHistoryBytes} bytes.");
            }
            if (persistentPayloadLength > 0 &&
                !KoboldKarePersistentMessageHistory.TryDecode(descriptor.PersistentApplicationPayload, out _))
            {
                throw new ArgumentException("Persistent application payload is malformed.", nameof(descriptor));
            }

            using MemoryStream stream = new MemoryStream(196 + initialPayloadLength + persistentPayloadLength);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)kind);
            WriteBoundedString(writer, descriptor.InstanceId, MaxInstanceIdBytes, nameof(descriptor.InstanceId));
            WriteBoundedString(writer, descriptor.PrefabId, MaxPrefabIdBytes, nameof(descriptor.PrefabId));
            writer.Write(descriptor.OwnerPlayerId);
            WriteVector3(writer, descriptor.Position);
            WriteQuaternion(writer, descriptor.Rotation);
            WriteVector3(writer, descriptor.Scale);
            WriteBoundedBytes(writer, descriptor.InitialPayload, MaxInitialPayloadBytes, nameof(descriptor.InitialPayload));
            WriteBoundedBytes(
                writer,
                descriptor.PersistentApplicationPayload,
                KoboldKarePersistentMessageHistory.MaxHistoryBytes,
                nameof(descriptor.PersistentApplicationPayload));
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeDespawn(string instanceId)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("A non-empty instance id is required.", nameof(instanceId));
            }

            using MemoryStream stream = new MemoryStream(64);
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)KoboldKareNetworkMessageKind.DespawnCommit);
            WriteBoundedString(writer, instanceId, MaxInstanceIdBytes, nameof(instanceId));
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodePersistentApplicationState(
            string instanceId,
            string sceneName,
            byte[] persistentApplicationPayload)
        {
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                throw new ArgumentException("A non-empty instance id is required.", nameof(instanceId));
            }
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                throw new ArgumentException("A non-empty scene name is required.", nameof(sceneName));
            }
            if (persistentApplicationPayload is { Length: > 0 } &&
                !KoboldKarePersistentMessageHistory.TryDecode(persistentApplicationPayload, out _))
            {
                throw new ArgumentException("Persistent application payload is malformed.", nameof(persistentApplicationPayload));
            }

            using MemoryStream stream = new MemoryStream(96 + (persistentApplicationPayload?.Length ?? 0));
            using BinaryWriter writer = new BinaryWriter(stream, Utf8, true);
            writer.Write((byte)KoboldKareNetworkMessageKind.SnapshotPersistentApplicationState);
            WriteBoundedString(writer, instanceId, MaxInstanceIdBytes, nameof(instanceId));
            WriteBoundedString(writer, sceneName, MaxPrefabIdBytes, nameof(sceneName));
            WriteBoundedBytes(
                writer,
                persistentApplicationPayload,
                KoboldKarePersistentMessageHistory.MaxHistoryBytes,
                nameof(persistentApplicationPayload));
            writer.Flush();
            return stream.ToArray();
        }

        public static byte[] EncodeControl(KoboldKareNetworkMessageKind kind)
        {
            if (kind != KoboldKareNetworkMessageKind.SnapshotRequest &&
                kind != KoboldKareNetworkMessageKind.SnapshotComplete)
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Message kind is not a control message.");
            }
            return new[] { (byte)kind };
        }

        public static bool TryDecode(byte[] buffer, out KoboldKareDecodedNetworkMessage message)
        {
            message = default;
            if (buffer == null || buffer.Length == 0)
            {
                return false;
            }

            try
            {
                using MemoryStream stream = new MemoryStream(buffer, false);
                using BinaryReader reader = new BinaryReader(stream, Utf8, true);
                KoboldKareNetworkMessageKind kind = (KoboldKareNetworkMessageKind)reader.ReadByte();

                switch (kind)
                {
                    case KoboldKareNetworkMessageKind.SpawnCommit:
                    case KoboldKareNetworkMessageKind.SnapshotSpawn:
                    {
                        KoboldKareSpawnDescriptor spawn = new KoboldKareSpawnDescriptor
                        {
                            InstanceId = ReadBoundedString(reader, MaxInstanceIdBytes),
                            PrefabId = ReadBoundedString(reader, MaxPrefabIdBytes),
                            OwnerPlayerId = reader.ReadUInt16(),
                            Position = ReadVector3(reader),
                            Rotation = ReadQuaternion(reader),
                            Scale = ReadVector3(reader),
                            InitialPayload = ReadBoundedBytes(reader, MaxInitialPayloadBytes),
                            PersistentApplicationPayload = ReadBoundedBytes(
                                reader,
                                KoboldKarePersistentMessageHistory.MaxHistoryBytes),
                        };

                        if (string.IsNullOrWhiteSpace(spawn.InstanceId) ||
                            string.IsNullOrWhiteSpace(spawn.PrefabId) ||
                            !IsFinite(spawn.Position) ||
                            !IsFinite(spawn.Rotation) ||
                            !IsFinite(spawn.Scale) ||
                            (spawn.PersistentApplicationPayload.Length > 0 &&
                             !KoboldKarePersistentMessageHistory.TryDecode(spawn.PersistentApplicationPayload, out _)))
                        {
                            return false;
                        }

                        message = new KoboldKareDecodedNetworkMessage(kind, spawn, spawn.InstanceId);
                        return stream.Position == stream.Length;
                    }
                    case KoboldKareNetworkMessageKind.DespawnCommit:
                    {
                        string instanceId = ReadBoundedString(reader, MaxInstanceIdBytes);
                        if (string.IsNullOrWhiteSpace(instanceId))
                        {
                            return false;
                        }
                        message = new KoboldKareDecodedNetworkMessage(kind, default, instanceId);
                        return stream.Position == stream.Length;
                    }
                    case KoboldKareNetworkMessageKind.SnapshotPersistentApplicationState:
                    {
                        string instanceId = ReadBoundedString(reader, MaxInstanceIdBytes);
                        string sceneName = ReadBoundedString(reader, MaxPrefabIdBytes);
                        byte[] persistentApplicationPayload = ReadBoundedBytes(
                            reader,
                            KoboldKarePersistentMessageHistory.MaxHistoryBytes);
                        if (string.IsNullOrWhiteSpace(instanceId) ||
                            string.IsNullOrWhiteSpace(sceneName) ||
                            (persistentApplicationPayload.Length > 0 &&
                             !KoboldKarePersistentMessageHistory.TryDecode(persistentApplicationPayload, out _)))
                        {
                            return false;
                        }
                        message = new KoboldKareDecodedNetworkMessage(
                            kind,
                            default,
                            instanceId,
                            persistentApplicationPayload,
                            sceneName);
                        return stream.Position == stream.Length;
                    }
                    case KoboldKareNetworkMessageKind.SnapshotRequest:
                    case KoboldKareNetworkMessageKind.SnapshotComplete:
                        message = new KoboldKareDecodedNetworkMessage(kind, default, null);
                        return stream.Position == stream.Length;
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

        private static void WriteBoundedString(BinaryWriter writer, string value, int maxBytes, string paramName)
        {
            value ??= string.Empty;
            byte[] encoded = Utf8.GetBytes(value);
            if (encoded.Length > maxBytes)
            {
                throw new ArgumentOutOfRangeException(paramName, $"UTF-8 value exceeds {maxBytes} bytes.");
            }
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }

        private static string ReadBoundedString(BinaryReader reader, int maxBytes)
        {
            ushort length = reader.ReadUInt16();
            if (length > maxBytes || length > Remaining(reader))
            {
                throw new InvalidDataException("String length exceeds protocol bounds.");
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }
            return Utf8.GetString(bytes);
        }

        private static void WriteBoundedBytes(BinaryWriter writer, byte[] value, int maxBytes, string paramName)
        {
            int length = value?.Length ?? 0;
            if (length > maxBytes)
            {
                throw new ArgumentOutOfRangeException(paramName, $"Payload exceeds {maxBytes} bytes.");
            }
            writer.Write(length);
            if (length > 0)
            {
                writer.Write(value);
            }
        }

        private static byte[] ReadBoundedBytes(BinaryReader reader, int maxBytes)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > maxBytes || length > Remaining(reader))
            {
                throw new InvalidDataException("Payload length exceeds protocol bounds.");
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException();
            }
            return bytes;
        }

        private static long Remaining(BinaryReader reader) => reader.BaseStream.Length - reader.BaseStream.Position;

        private static void WriteVector3(BinaryWriter writer, Vector3 value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
        }

        private static Vector3 ReadVector3(BinaryReader reader) =>
            new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static void WriteQuaternion(BinaryWriter writer, Quaternion value)
        {
            writer.Write(value.x);
            writer.Write(value.y);
            writer.Write(value.z);
            writer.Write(value.w);
        }

        private static Quaternion ReadQuaternion(BinaryReader reader) =>
            new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
