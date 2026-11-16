// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.IO.Brotli
{
    /// <summary>
    /// The fixed data of RFC 7932: the static dictionary (Appendix A), its word counts (§8), and the
    /// length and count code tables (§5, §6). The context lookup tables and the word transformations
    /// are generated from the RFC's text (<c>BrotliTables.Generated.cs</c>).
    /// </summary>
    internal static partial class BrotliTables
    {
        /// <summary>The dictionary's length, as Appendix A states it.</summary>
        internal const int DictionarySize = 122784;

        /// <summary>The dictionary's CRC-32, as Appendix A states it.</summary>
        internal const uint DictionaryCrc32 = 0x5136cb04;

        /// <summary>§8: the bit depth of each word length's index (lengths 4..24 have words).</summary>
        internal static readonly int[] NdBits = { 0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5 };

        /// <summary>§8: where each word length's words start in the dictionary.</summary>
        internal static readonly int[] DOffset = BuildOffsets();

        /// <summary>§5: insert length codes 0..23 — the base length and its extra bits.</summary>
        internal static readonly int[] InsertBase  = { 0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98, 130, 194, 322, 578, 1090, 2114, 6210, 22594 };
        internal static readonly int[] InsertExtra = { 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 12, 14, 24 };

        /// <summary>§5: copy length codes 0..23 — the base length and its extra bits.</summary>
        internal static readonly int[] CopyBase  = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54, 70, 102, 134, 198, 326, 582, 1094, 2118 };
        internal static readonly int[] CopyExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 7, 8, 9, 10, 24 };

        /// <summary>§5: for each 64-code cell of the insert-and-copy alphabet, the first insert and copy length codes.</summary>
        internal static readonly int[] CellInsert = { 0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16 };
        internal static readonly int[] CellCopy   = { 0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16 };

        /// <summary>§6: block count codes 0..25 — the base count and its extra bits.</summary>
        internal static readonly int[] BlockCountBase  = { 1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209, 241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625 };
        internal static readonly int[] BlockCountExtra = { 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 7, 8, 9, 10, 11, 12, 13, 24 };

        /// <summary>§3.5: the order in which the code length code lengths are given.</summary>
        internal static readonly int[] CodeLengthOrder = { 1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        /// <summary>§3.5: the lengths of the fixed code the code length code lengths are read with (symbols 0..5).</summary>
        internal static readonly int[] CodeLengthCodeLengths = { 2, 4, 3, 2, 2, 4 };

        private static readonly object DictionaryLock = new object();
        private static byte[] _dictionary;

        /// <summary>Appendix A's dictionary, read once from the assembly and checked against its length and CRC-32.</summary>
        internal static byte[] Dictionary
        {
            get
            {
                byte[] d = _dictionary;
                if (d != null) return d;
                lock (DictionaryLock)
                {
                    if (_dictionary != null) return _dictionary;
                    using (Stream s = typeof(BrotliTables).Assembly.GetManifestResourceStream("Tetitulz.Pdf.Core.IO.Brotli.dictionary.bin"))
                    {
                        if (s == null) throw new PdfFilterException("Brotli: the static dictionary resource is missing from the assembly.");
                        var bytes = new byte[DictionarySize];
                        if (StreamBytes.Fill(s, bytes, 0, bytes.Length) != bytes.Length || s.ReadByte() != -1 || Crc32(bytes) != DictionaryCrc32)
                            throw new PdfFilterException("Brotli: the static dictionary resource is not RFC 7932's.");
                        _dictionary = bytes;
                    }
                    return _dictionary;
                }
            }
        }

        private static int[] BuildOffsets()
        {
            var offsets = new int[25];
            for (int length = 0; length < 24; length++)
                offsets[length + 1] = offsets[length] + (length < 4 ? 0 : length << NdBits[length]);
            return offsets;
        }

        /// <summary>The CRC-32 of Appendix C (the ISO 3309 polynomial, reflected).</summary>
        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }
    }
}
