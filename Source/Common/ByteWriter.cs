using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Multiplayer.Common
{
    /// <summary>
    /// High-performance, zero-allocation binary serialization writer operating over a contiguous byte buffer.
    /// Replaces standard MemoryStream to eliminate heap overhead on hot network and sync paths.
    /// </summary>
    public class ByteWriter
    {
        private byte[] buffer;
        public object? context;

        /// <summary>
        /// Current write position within the backing buffer, indicating written byte count.
        /// </summary>
        public int Position { get; private set; }

        /// <summary>
        /// Initializes a new writer instance with pre-allocated backing storage.
        /// </summary>
        /// <param name="capacity">Initial capacity in bytes; clamped to a minimum of 64 bytes.</param>
        public ByteWriter(int capacity = 64)
        {
            buffer = new byte[capacity > 0 ? capacity : 64];
            Position = 0;
        }

        /// <summary>
        /// Ensures the backing array has enough space for an incoming chunk, doubling size when exceeded.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int size)
        {
            if (Position + size > buffer.Length)
            {
                int newCap = buffer.Length == 0 ? 64 : buffer.Length * 2;
                while (newCap < Position + size)
                    newCap *= 2;

                Array.Resize(ref buffer, newCap);
            }
        }

        public virtual void WriteByte(byte val)
        {
            EnsureCapacity(1);
            buffer[Position++] = val;
        }

        public virtual void WriteSByte(sbyte val)
        {
            EnsureCapacity(1);
            buffer[Position++] = (byte)val;
        }

        public virtual void WriteShort(short val)
        {
            EnsureCapacity(2);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
        }

        public virtual void WriteUShort(ushort val)
        {
            EnsureCapacity(2);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
        }

        public virtual void WriteInt32(int val)
        {
            EnsureCapacity(4);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
            buffer[Position++] = (byte)(val >> 16);
            buffer[Position++] = (byte)(val >> 24);
        }

        public virtual void WriteUInt32(uint val)
        {
            EnsureCapacity(4);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
            buffer[Position++] = (byte)(val >> 16);
            buffer[Position++] = (byte)(val >> 24);
        }

        public virtual void WriteLong(long val)
        {
            EnsureCapacity(8);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
            buffer[Position++] = (byte)(val >> 16);
            buffer[Position++] = (byte)(val >> 24);
            buffer[Position++] = (byte)(val >> 32);
            buffer[Position++] = (byte)(val >> 40);
            buffer[Position++] = (byte)(val >> 48);
            buffer[Position++] = (byte)(val >> 56);
        }

        public virtual void WriteULong(ulong val)
        {
            EnsureCapacity(8);
            buffer[Position++] = (byte)val;
            buffer[Position++] = (byte)(val >> 8);
            buffer[Position++] = (byte)(val >> 16);
            buffer[Position++] = (byte)(val >> 24);
            buffer[Position++] = (byte)(val >> 32);
            buffer[Position++] = (byte)(val >> 40);
            buffer[Position++] = (byte)(val >> 48);
            buffer[Position++] = (byte)(val >> 56);
        }

        /// <summary>
        /// Serializes a 32-bit floating point value using direct pointer reinterpretation to avoid BitConverter allocations.
        /// </summary>
        public virtual unsafe void WriteFloat(float val)
        {
            EnsureCapacity(4);
            uint intVal = *(uint*)&val;
            buffer[Position++] = (byte)intVal;
            buffer[Position++] = (byte)(intVal >> 8);
            buffer[Position++] = (byte)(intVal >> 16);
            buffer[Position++] = (byte)(intVal >> 24);
        }

        /// <summary>
        /// Serializes a 64-bit double precision float value using direct pointer reinterpretation to avoid BitConverter allocations.
        /// </summary>
        public virtual unsafe void WriteDouble(double val)
        {
            EnsureCapacity(8);
            ulong longVal = *(ulong*)&val;
            buffer[Position++] = (byte)longVal;
            buffer[Position++] = (byte)(longVal >> 8);
            buffer[Position++] = (byte)(longVal >> 16);
            buffer[Position++] = (byte)(longVal >> 24);
            buffer[Position++] = (byte)(longVal >> 32);
            buffer[Position++] = (byte)(longVal >> 40);
            buffer[Position++] = (byte)(longVal >> 48);
            buffer[Position++] = (byte)(longVal >> 56);
        }

        public virtual void WriteBool(bool val)
        {
            EnsureCapacity(1);
            buffer[Position++] = val ? (byte)1 : (byte)0;
        }

        public virtual void WritePrefixedBytes(byte[]? bytes)
        {
            if (bytes == null)
            {
                WriteInt32(0);
                return;
            }

            WriteInt32(bytes.Length);
            WriteRaw(bytes);
        }

        public virtual void WritePrefixedInts(IList<int> ints)
        {
            WriteInt32(ints.Count);
            for (int i = 0; i < ints.Count; i++)
                WriteInt32(ints[i]);
        }

        public virtual void WritePrefixedUInts(IList<uint> ints)
        {
            WriteInt32(ints.Count);
            for (int i = 0; i < ints.Count; i++)
                WriteUInt32(ints[i]);
        }

        public virtual void WriteRaw(byte[] bytes)
        {
            EnsureCapacity(bytes.Length);
            Buffer.BlockCopy(bytes, 0, buffer, Position, bytes.Length);
            Position += bytes.Length;
        }

        public virtual void WriteFrom(byte[] srcBuffer, int offset, int length)
        {
            EnsureCapacity(length);
            Buffer.BlockCopy(srcBuffer, offset, buffer, Position, length);
            Position += length;
        }

        public virtual ByteWriter WriteString(string? s)
        {
            WritePrefixedBytes(s == null ? null : Encoding.UTF8.GetBytes(s));
            return this;
        }

        public virtual void WriteEnum<T>(T value) where T : Enum
        {
            Type type = Enum.GetUnderlyingType(typeof(T) == typeof(Enum) ? value.GetType() : typeof(T));

            if (type == typeof(byte)) WriteByte(Convert.ToByte(value));
            else if (type == typeof(sbyte)) WriteSByte(Convert.ToSByte(value));
            else if (type == typeof(short)) WriteShort(Convert.ToInt16(value));
            else if (type == typeof(ushort)) WriteUShort(Convert.ToUInt16(value));
            else if (type == typeof(int)) WriteInt32(Convert.ToInt32(value));
            else if (type == typeof(uint)) WriteUInt32(Convert.ToUInt32(value));
            else if (type == typeof(long) || type == typeof(IntPtr)) WriteLong(Convert.ToInt64(value));
            else if (type == typeof(ulong) || type == typeof(UIntPtr)) WriteULong(Convert.ToUInt64(value));
            else ServerLog.Error($"MP ByteWriter.WriteEnum: Unknown type {type}");
        }

        private void Write(object obj)
        {
            if (obj is int @int) WriteInt32(@int);
            else if (obj is ushort @ushort) WriteUShort(@ushort);
            else if (obj is short @short) WriteShort(@short);
            else if (obj is bool @bool) WriteBool(@bool);
            else if (obj is long @long) WriteLong(@long);
            else if (obj is ulong @ulong) WriteULong(@ulong);
            else if (obj is byte @byte) WriteByte(@byte);
            else if (obj is float @float) WriteFloat(@float);
            else if (obj is double @double) WriteDouble(@double);
            else if (obj is byte[] bytes) WritePrefixedBytes(bytes);
            else if (obj is Enum enumObj) WriteEnum(enumObj);
            else if (obj is string @string) WriteString(@string);
            else if (obj is Array arr)
            {
                WriteInt32(arr.Length);
                foreach (object o in arr) Write(o);
            }
            else if (obj is IList list)
            {
                WriteInt32(list.Count);
                for (int i = 0; i < list.Count; i++) Write(list[i]);
            }
            else if (obj is ITuple tuple)
            {
                for (int i = 0; i < tuple.Length; i++) Write(tuple[i]);
            }
            else ServerLog.Error($"MP ByteWriter.Write: Unknown type {obj.GetType()}");
        }

        /// <summary>
        /// Materializes only the populated portion of the internal buffer into a newly allocated byte array.
        /// </summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[Position];
            Buffer.BlockCopy(buffer, 0, result, 0, Position);
            return result;
        }

        /// <summary>
        /// Returns the direct internal buffer reference to allow zero-copy packet transmission when paired with Position.
        /// </summary>
        public byte[] GetInternalBuffer() => buffer;

        public static byte[] GetBytes(params object[] data)
        {
            var writer = new ByteWriter();
            for (int i = 0; i < data.Length; i++)
                writer.Write(data[i]);
            return writer.ToArray();
        }

        /// <summary>
        /// Rewinds or advances the logical write position without clearing allocated memory.
        /// </summary>
        public void SetLength(int value)
        {
            Position = value;
        }
    }

    public class WriterException(string msg) : Exception(msg)
    {
    }
}