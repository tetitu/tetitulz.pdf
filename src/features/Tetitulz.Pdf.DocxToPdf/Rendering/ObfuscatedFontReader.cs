// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// De-obfuscates fonts embedded in a DOCX via the Open Packaging Conventions
    /// "obfuscated font" mechanism (ECMA-376 §17.8.1). The first 32 bytes of the font
    /// stream are XOR-masked with the 16-byte <c>fontKey</c> GUID, applied as two
    /// 16-byte blocks with the GUID bytes taken in reverse order. XOR is its own
    /// inverse, so the same routine both obfuscates and de-obfuscates.
    /// </summary>
    internal static class ObfuscatedFontReader
    {
        /// <summary>
        /// Returns a NEW array with the OPC obfuscation removed (or applied — it is
        /// involutive). When <paramref name="fontKey"/> is null/empty/invalid the data is
        /// returned unchanged (an embedded font may be stored un-obfuscated).
        /// </summary>
        internal static byte[] Deobfuscate(byte[] data, string fontKey)
        {
            if (data == null) return null;
            var result = (byte[])data.Clone();
            byte[] mask = BuildMask(fontKey);
            if (mask == null) return result;

            int n = Math.Min(32, result.Length);
            for (int i = 0; i < n; i++)
                result[i] ^= mask[i % 16];
            return result;
        }

        // The 16 mask bytes: the GUID's 16 bytes (in string/hex order) reversed.
        private static byte[] BuildMask(string fontKey)
        {
            if (string.IsNullOrEmpty(fontKey)) return null;

            // Strip braces / dashes / whitespace → the 32 hex digits of the GUID.
            var hex = new StringBuilder(32);
            foreach (char c in fontKey)
                if (Uri.IsHexDigit(c)) hex.Append(c);
            if (hex.Length != 32) return null;

            var bytes = new byte[16];
            for (int i = 0; i < 16; i++)
                bytes[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
            Array.Reverse(bytes);
            return bytes;
        }

        private static int HexVal(char c)
            => c <= '9' ? c - '0' : (char.ToLowerInvariant(c) - 'a' + 10);
    }
}
