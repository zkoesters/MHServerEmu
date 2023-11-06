﻿using Google.ProtocolBuffers;

namespace MHServerEmu.Common.Extensions
{
    public static class ArrayExtensions
    {
        private static readonly byte[] BitReversalLookupTable = new byte[]
        {
            0x00, 0x80, 0x40, 0xc0, 0x20, 0xa0, 0x60, 0xe0, 0x10, 0x90, 0x50, 0xd0, 0x30, 0xb0, 0x70, 0xf0,
            0x08, 0x88, 0x48, 0xc8, 0x28, 0xa8, 0x68, 0xe8, 0x18, 0x98, 0x58, 0xd8, 0x38, 0xb8, 0x78, 0xf8,
            0x04, 0x84, 0x44, 0xc4, 0x24, 0xa4, 0x64, 0xe4, 0x14, 0x94, 0x54, 0xd4, 0x34, 0xb4, 0x74, 0xf4,
            0x0c, 0x8c, 0x4c, 0xcc, 0x2c, 0xac, 0x6c, 0xec, 0x1c, 0x9c, 0x5c, 0xdc, 0x3c, 0xbc, 0x7c, 0xfc,
            0x02, 0x82, 0x42, 0xc2, 0x22, 0xa2, 0x62, 0xe2, 0x12, 0x92, 0x52, 0xd2, 0x32, 0xb2, 0x72, 0xf2,
            0x0a, 0x8a, 0x4a, 0xca, 0x2a, 0xaa, 0x6a, 0xea, 0x1a, 0x9a, 0x5a, 0xda, 0x3a, 0xba, 0x7a, 0xfa,
            0x06, 0x86, 0x46, 0xc6, 0x26, 0xa6, 0x66, 0xe6, 0x16, 0x96, 0x56, 0xd6, 0x36, 0xb6, 0x76, 0xf6,
            0x0e, 0x8e, 0x4e, 0xce, 0x2e, 0xae, 0x6e, 0xee, 0x1e, 0x9e, 0x5e, 0xde, 0x3e, 0xbe, 0x7e, 0xfe,
            0x01, 0x81, 0x41, 0xc1, 0x21, 0xa1, 0x61, 0xe1, 0x11, 0x91, 0x51, 0xd1, 0x31, 0xb1, 0x71, 0xf1,
            0x09, 0x89, 0x49, 0xc9, 0x29, 0xa9, 0x69, 0xe9, 0x19, 0x99, 0x59, 0xd9, 0x39, 0xb9, 0x79, 0xf9,
            0x05, 0x85, 0x45, 0xc5, 0x25, 0xa5, 0x65, 0xe5, 0x15, 0x95, 0x55, 0xd5, 0x35, 0xb5, 0x75, 0xf5,
            0x0d, 0x8d, 0x4d, 0xcd, 0x2d, 0xad, 0x6d, 0xed, 0x1d, 0x9d, 0x5d, 0xdd, 0x3d, 0xbd, 0x7d, 0xfd,
            0x03, 0x83, 0x43, 0xc3, 0x23, 0xa3, 0x63, 0xe3, 0x13, 0x93, 0x53, 0xd3, 0x33, 0xb3, 0x73, 0xf3,
            0x0b, 0x8b, 0x4b, 0xcb, 0x2b, 0xab, 0x6b, 0xeb, 0x1b, 0x9b, 0x5b, 0xdb, 0x3b, 0xbb, 0x7b, 0xfb,
            0x07, 0x87, 0x47, 0xc7, 0x27, 0xa7, 0x67, 0xe7, 0x17, 0x97, 0x57, 0xd7, 0x37, 0xb7, 0x77, 0xf7,
            0x0f, 0x8f, 0x4f, 0xcf, 0x2f, 0xaf, 0x6f, 0xef, 0x1f, 0x9f, 0x5f, 0xdf, 0x3f, 0xbf, 0x7f, 0xff
        };

        /// <summary>
        /// Enumerates a generic array.
        /// </summary>
        public static IEnumerable<T> Enumerate<T>(this T[] array, int start, int count)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            for (int i = 0; i < count; i++)
                yield return array[start + i];
        }

        #region Hex/ByteString Helpers

        /// <summary>
        /// Converts a byte array to a hex string.
        /// </summary>
        public static string ToHexString(this byte[] byteArray)
        {
            return byteArray.Aggregate("", (current, b) => current + b.ToString("X2"));
        }

        /// <summary>
        /// Converts a byte array to a protobuf-compatible ByteString.
        /// </summary>
        public static ByteString ToByteString(this byte[] byteArray)
        {
            return ByteString.CopyFrom(byteArray);
        }
        
        /// <summary>
        /// Converts a hex string to a byte array.
        /// </summary>
        public static byte[] ToByteArray(this string hexString)
        {
            return Convert.FromHexString(hexString);
        }

        /// <summary>
        /// Converts a hex string to a protobuf-compatible ByteString.
        /// </summary>
        public static ByteString ToByteString(this string hexString)
        {
            return hexString.ToByteArray().ToByteString();
        }

        #endregion

        #region Bit/Byte Manipulation

        /// <summary>
        /// Reverses the order of bytes in a ulong value.
        /// </summary>
        public static ulong ReverseBytes(this ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);
            return BitConverter.ToUInt64(bytes);
        }

        /// <summary>
        /// Reverses the order of bits in a ulong value.
        /// </summary>
        public static ulong ReverseBits(this ulong value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Array.Reverse(bytes);

            // Use a lookup table to speed up bit reversal for each byte
            for (int i = 0; i < 8; i++)
                bytes[i] = BitReversalLookupTable[bytes[i]];

            return BitConverter.ToUInt64(bytes);
        }

        #endregion

        #region uint <-> bool[] Conversion

        /* uint mask cheat sheet for getting bools (1 << i)
         0 == 0x1,       1 == 0x2,       2 == 0x4,       3 == 0x8,       4 == 0x10,      5 == 0x20,     6 == 0x40,     7 == 0x80,
         8 == 0x100,     9 == 0x200,    10 == 0x400,    11 == 0x800,    12 == 0x1000,   13 == 0x2000,  14 == 0x4000,  15 == 0x8000
        16 == 0x10000,  17 == 0x20000,  18 == 0x40000,  19 == 0x80000,  20 == 0x100000
        */

        /// <summary>
        /// Converts a uint to a bool array of flags. A uint can hold up to 32 bools.
        /// </summary>
        public static bool[] ToBoolArray(this uint value, int arraySize = 32)
        {
            if (arraySize > 32) throw new("Cannot decode more than 32 bools from a uint.");

            bool[] output = new bool[arraySize];

            for (int i = 0; i < output.Length; i++)
                output[i] = (value & (1 << i)) > 0;

            return output;
        }

        /// <summary>
        /// Converts a bool array of flags to a uint. A uint can hold up to 32 bools.
        /// </summary>
        public static uint ToUInt32(this bool[] boolArray)
        {
            if (boolArray.Length > 32) throw new("Cannot encode more than 32 bools in a uint.");

            uint output = 0;

            for (int i = 0; i < boolArray.Length; i++)
                if (boolArray[i]) output |= (uint)(1 << i);

            return output;
        }
        #endregion
    }
}
