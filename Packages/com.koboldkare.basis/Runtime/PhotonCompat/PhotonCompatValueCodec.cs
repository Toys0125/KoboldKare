using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace KoboldKare.Basis.PhotonCompat
{
    internal static class PhotonCompatValueCodec
    {
        private enum ValueType : byte
        {
            Null = 0,
            Int32 = 1,
            Single = 2,
            Int16 = 3,
            Byte = 4,
            Bool = 5,
            Vector3 = 6,
            Quaternion = 7,
            String = 8,
            BitBuffer = 9,
            UInt32 = 10,
            UInt16 = 11,
            Int64 = 12,
            Double = 13,
            Vector2 = 14,
            Vector4 = 15,
            Color = 16,
            ByteArray = 17,
            SByte = 18,
        }

        public const int MaxValues = 128;
        public const int MaxStringBytes = 4096;
        public const int MaxBlobBytes = 64 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private const string BitBufferFullName = "NetStack.Serialization.BitBuffer";

        private static Type cachedBitBufferType;
        private static PropertyInfo cachedBitBufferLength;
        private static MethodInfo cachedBitBufferToArray;
        private static MethodInfo cachedBitBufferFromArray;
        private static ConstructorInfo cachedBitBufferConstructor;
        private static bool bitBufferLookupAttempted;

        public static void WriteValues(BinaryWriter writer, IReadOnlyList<object> values)
        {
            int count = values?.Count ?? 0;
            if (count > MaxValues)
            {
                throw new ArgumentOutOfRangeException(nameof(values), $"Value count exceeds {MaxValues}.");
            }

            writer.Write((byte)count);
            for (int i = 0; i < count; i++)
            {
                WriteValue(writer, values[i]);
            }
        }

        public static bool TryReadValues(BinaryReader reader, out object[] values)
        {
            values = Array.Empty<object>();
            try
            {
                int count = reader.ReadByte();
                if (count > MaxValues)
                {
                    return false;
                }

                object[] decoded = new object[count];
                for (int i = 0; i < count; i++)
                {
                    if (!TryReadValue(reader, out decoded[i]))
                    {
                        return false;
                    }
                }

                values = decoded;
                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is DecoderFallbackException ||
                exception is ArgumentException ||
                exception is TargetInvocationException)
            {
                return false;
            }
        }

        public static void WriteBoundedString(BinaryWriter writer, string value, int maxBytes = MaxStringBytes)
        {
            value ??= string.Empty;
            byte[] encoded = Utf8.GetBytes(value);
            if (encoded.Length > maxBytes || encoded.Length > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(value), $"UTF-8 string exceeds {maxBytes} bytes.");
            }
            writer.Write((ushort)encoded.Length);
            writer.Write(encoded);
        }

        public static bool TryReadBoundedString(BinaryReader reader, out string value, int maxBytes = MaxStringBytes)
        {
            value = null;
            try
            {
                ushort length = reader.ReadUInt16();
                long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
                if (length > maxBytes || length > remaining)
                {
                    return false;
                }
                byte[] bytes = reader.ReadBytes(length);
                if (bytes.Length != length)
                {
                    return false;
                }
                value = Utf8.GetString(bytes);
                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is DecoderFallbackException ||
                exception is ArgumentException)
            {
                return false;
            }
        }

        private static void WriteValue(BinaryWriter writer, object value)
        {
            switch (value)
            {
                case null:
                    writer.Write((byte)ValueType.Null);
                    return;
                case int int32:
                    writer.Write((byte)ValueType.Int32);
                    writer.Write(int32);
                    return;
                case float single:
                    writer.Write((byte)ValueType.Single);
                    writer.Write(single);
                    return;
                case short int16:
                    writer.Write((byte)ValueType.Int16);
                    writer.Write(int16);
                    return;
                case byte unsignedByte:
                    writer.Write((byte)ValueType.Byte);
                    writer.Write(unsignedByte);
                    return;
                case sbyte signedByte:
                    writer.Write((byte)ValueType.SByte);
                    writer.Write(signedByte);
                    return;
                case bool boolean:
                    writer.Write((byte)ValueType.Bool);
                    writer.Write(boolean);
                    return;
                case Vector3 vector3:
                    writer.Write((byte)ValueType.Vector3);
                    writer.Write(vector3.x);
                    writer.Write(vector3.y);
                    writer.Write(vector3.z);
                    return;
                case Quaternion rotation:
                    writer.Write((byte)ValueType.Quaternion);
                    writer.Write(rotation.x);
                    writer.Write(rotation.y);
                    writer.Write(rotation.z);
                    writer.Write(rotation.w);
                    return;
                case string text:
                    writer.Write((byte)ValueType.String);
                    WriteBoundedString(writer, text);
                    return;
                case uint uint32:
                    writer.Write((byte)ValueType.UInt32);
                    writer.Write(uint32);
                    return;
                case ushort uint16:
                    writer.Write((byte)ValueType.UInt16);
                    writer.Write(uint16);
                    return;
                case long int64:
                    writer.Write((byte)ValueType.Int64);
                    writer.Write(int64);
                    return;
                case double doubleValue:
                    writer.Write((byte)ValueType.Double);
                    writer.Write(doubleValue);
                    return;
                case Vector2 vector2:
                    writer.Write((byte)ValueType.Vector2);
                    writer.Write(vector2.x);
                    writer.Write(vector2.y);
                    return;
                case Vector4 vector4:
                    writer.Write((byte)ValueType.Vector4);
                    writer.Write(vector4.x);
                    writer.Write(vector4.y);
                    writer.Write(vector4.z);
                    writer.Write(vector4.w);
                    return;
                case Color color:
                    writer.Write((byte)ValueType.Color);
                    writer.Write(color.r);
                    writer.Write(color.g);
                    writer.Write(color.b);
                    writer.Write(color.a);
                    return;
                case byte[] bytes:
                    writer.Write((byte)ValueType.ByteArray);
                    WriteBlob(writer, bytes);
                    return;
                default:
                    if (IsBitBuffer(value.GetType()))
                    {
                        writer.Write((byte)ValueType.BitBuffer);
                        WriteBitBuffer(writer, value);
                        return;
                    }
                    throw new NotSupportedException($"Photon compatibility value type '{value.GetType().FullName}' is not supported.");
            }
        }

        private static bool TryReadValue(BinaryReader reader, out object value)
        {
            value = null;
            ValueType type = (ValueType)reader.ReadByte();
            switch (type)
            {
                case ValueType.Null:
                    return true;
                case ValueType.Int32:
                    value = reader.ReadInt32();
                    return true;
                case ValueType.Single:
                    value = reader.ReadSingle();
                    return IsFinite((float)value);
                case ValueType.Int16:
                    value = reader.ReadInt16();
                    return true;
                case ValueType.Byte:
                    value = reader.ReadByte();
                    return true;
                case ValueType.SByte:
                    value = reader.ReadSByte();
                    return true;
                case ValueType.Bool:
                    value = reader.ReadBoolean();
                    return true;
                case ValueType.Vector3:
                {
                    Vector3 vector = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(vector)) return false;
                    value = vector;
                    return true;
                }
                case ValueType.Quaternion:
                {
                    Quaternion rotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(rotation)) return false;
                    value = rotation;
                    return true;
                }
                case ValueType.String:
                {
                    if (!TryReadBoundedString(reader, out string decoded)) return false;
                    value = decoded;
                    return true;
                }
                case ValueType.UInt32:
                    value = reader.ReadUInt32();
                    return true;
                case ValueType.UInt16:
                    value = reader.ReadUInt16();
                    return true;
                case ValueType.Int64:
                    value = reader.ReadInt64();
                    return true;
                case ValueType.Double:
                {
                    double decoded = reader.ReadDouble();
                    if (double.IsNaN(decoded) || double.IsInfinity(decoded)) return false;
                    value = decoded;
                    return true;
                }
                case ValueType.Vector2:
                {
                    Vector2 vector = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(vector.x) || !IsFinite(vector.y)) return false;
                    value = vector;
                    return true;
                }
                case ValueType.Vector4:
                {
                    Vector4 vector = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(vector.x) || !IsFinite(vector.y) || !IsFinite(vector.z) || !IsFinite(vector.w)) return false;
                    value = vector;
                    return true;
                }
                case ValueType.Color:
                {
                    Color color = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b) || !IsFinite(color.a)) return false;
                    value = color;
                    return true;
                }
                case ValueType.ByteArray:
                    return TryReadBlob(reader, out value);
                case ValueType.BitBuffer:
                    return TryReadBitBuffer(reader, out value);
                default:
                    return false;
            }
        }

        private static void WriteBlob(BinaryWriter writer, byte[] bytes)
        {
            int length = bytes?.Length ?? 0;
            if (length > MaxBlobBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes), $"Blob exceeds {MaxBlobBytes} bytes.");
            }
            writer.Write(length);
            if (length > 0) writer.Write(bytes);
        }

        private static bool TryReadBlob(BinaryReader reader, out object value)
        {
            value = null;
            int length = reader.ReadInt32();
            long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (length < 0 || length > MaxBlobBytes || length > remaining)
            {
                return false;
            }
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                return false;
            }
            value = bytes;
            return true;
        }

        private static void WriteBitBuffer(BinaryWriter writer, object bitBuffer)
        {
            if (!EnsureBitBufferReflection(bitBuffer.GetType()))
            {
                throw new NotSupportedException("NetStack BitBuffer reflection metadata could not be resolved.");
            }

            int length = (int)cachedBitBufferLength.GetValue(bitBuffer);
            if (length < 0 || length > MaxBlobBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(bitBuffer), $"BitBuffer exceeds {MaxBlobBytes} bytes.");
            }
            byte[] bytes = new byte[length];
            cachedBitBufferToArray.Invoke(bitBuffer, new object[] { bytes });
            WriteBlob(writer, bytes);
        }

        private static bool TryReadBitBuffer(BinaryReader reader, out object value)
        {
            value = null;
            if (!TryReadBlob(reader, out object blob) || blob is not byte[] bytes)
            {
                return false;
            }
            if (!EnsureBitBufferReflection(null))
            {
                return false;
            }

            object instance = cachedBitBufferConstructor.Invoke(new object[] { bytes.Length / 4 + 1 });
            cachedBitBufferFromArray.Invoke(instance, new object[] { bytes, bytes.Length });
            value = instance;
            return true;
        }

        private static bool IsBitBuffer(Type type) => type != null && string.Equals(type.FullName, BitBufferFullName, StringComparison.Ordinal);

        private static bool EnsureBitBufferReflection(Type knownType)
        {
            if (cachedBitBufferType != null)
            {
                return true;
            }
            if (bitBufferLookupAttempted && knownType == null)
            {
                return false;
            }

            bitBufferLookupAttempted = true;
            Type type = IsBitBuffer(knownType) ? knownType : FindType(BitBufferFullName);
            if (type == null)
            {
                return false;
            }

            PropertyInfo length = type.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo toArray = type.GetMethod("ToArray", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]) }, null);
            MethodInfo fromArray = type.GetMethod("FromArray", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(byte[]), typeof(int) }, null);
            ConstructorInfo constructor = type.GetConstructor(new[] { typeof(int) });
            if (length == null || toArray == null || fromArray == null || constructor == null)
            {
                return false;
            }

            cachedBitBufferType = type;
            cachedBitBufferLength = length;
            cachedBitBufferToArray = toArray;
            cachedBitBufferFromArray = fromArray;
            cachedBitBufferConstructor = constructor;
            return true;
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        private static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
