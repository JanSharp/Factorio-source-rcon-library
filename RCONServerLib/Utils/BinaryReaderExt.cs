using System;
using System.IO;
using System.Text;

namespace RCONServerLib.Utils
{
    internal class BinaryReaderExt : BinaryReader
    {
        public BinaryReaderExt(Stream stream)
            : base(stream, Encoding.Unicode)
        {
        }

        /// <summary>
        ///     Converts bytes to Little-Endian if we're in a big-endian environment
        ///     The given <paramref name="bytes"/> may be reversed after the call
        /// </summary>
        /// <returns></returns>
        public static int ToInt32LittleEndian(byte[] bytes)
        {
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(bytes);

            return BitConverter.ToInt32(bytes, 0);
        }

        /// <summary>
        ///     Reads the next 4-Bytes as Little-Endian if we're in a big-endian environment
        /// </summary>
        /// <returns></returns>
        public int ReadInt32LittleEndian()
        {
            return ToInt32LittleEndian(ReadBytes(4));
        }

        /// <summary>
        ///     Reads a ASCII string (Null-terminated string) without the null-terminator
        /// </summary>
        /// <returns></returns>
        public string ReadAscii()
        {
            var sb = new StringBuilder();
            byte val;
            do
            {
                val = ReadByte();
                if (val > 0)
                    sb.Append((char) val);
            } while (val > 0);

            return sb.ToString();
        }
    }
}