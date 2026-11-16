// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// The character sets an Extended Channel Interpretation names, as the readers decode them —
    /// Data Matrix's and QR's alike — by the AIM ECI assignments as two independent implementations
    /// read them: two independent implementations, which agree on 0–30;
    /// 31–35 are the second's.
    /// </summary>
    /// <remarks>
    /// <para><b>One answer for both symbologies.</b> An ECI means the same thing in every symbology that
    /// carries one (AIM ITS/04-001); only the way its number is written differs, and that stays with
    /// each reader.</para>
    /// <para><b>The sets.</b> CP437 (0, 2); ISO-8859-1 (1, 3); ISO-8859-2 to -11 (4–13), -13 to -16
    /// (15–18); Shift JIS (20); Windows-1250, -1251, -1252, -1256 (21–24); UTF-16BE (25), UTF-8 (26),
    /// US-ASCII (27); Big5 (28); GB 2312 (29) and GB18030 (32), both read through GB18030, its superset;
    /// EUC-KR (30); GBK (31); UTF-16LE (33); UTF-32BE (34) and -LE (35). The code pages come from the
    /// <c>System.Text.Encoding.CodePages</c> provider Core already carries, asked directly — nothing is
    /// registered process-wide. ISO-8859-10, -11, -14 and -16 (12, 13, 16, 18) have no code page on any
    /// target framework, so they are read through <see cref="Iso8859Tables"/>, generated
    /// from the Unicode Consortium's mapping files and checked against Python's codecs. Every unassigned
    /// ECI is refused by number; bytes a set cannot hold are refused with the reason, never replaced.</para>
    /// </remarks>
    internal static class EciCharacterSets
    {
        /// <summary>The ISO-8859 parts no target framework carries, by ECI: each part's upper half (0xA0–0xFF).</summary>
        private static readonly Dictionary<int, (int Part, string Upper)> Iso8859Parts = new Dictionary<int, (int, string)>
        {
            [12] = (10, Iso8859Tables.Part10Upper), [13] = (11, Iso8859Tables.Part11Upper),
            [16] = (14, Iso8859Tables.Part14Upper), [18] = (16, Iso8859Tables.Part16Upper),
        };

        /// <summary>The character no ISO-8859 table holds, where a part defines none for a byte.</summary>
        private const char Undefined = '￿';

        /// <summary>The code page each code-page ECI names (the Unicode, ASCII and Latin-1 ones are decoded natively).</summary>
        private static readonly Dictionary<int, int> CodePages = new Dictionary<int, int>
        {
            [0] = 437, [2] = 437,
            [4] = 28592, [5] = 28593, [6] = 28594, [7] = 28595, [8] = 28596, [9] = 28597, [10] = 28598, [11] = 28599,
            [15] = 28603, [17] = 28605,
            [20] = 932,
            [21] = 1250, [22] = 1251, [23] = 1252, [24] = 1256,
            [28] = 950, [29] = 54936, [30] = 51949, [31] = 936, [32] = 54936,
        };

        private static readonly HashSet<int> Native = new HashSet<int> { 1, 3, 25, 26, 27, 33, 34, 35 };

        /// <summary>Whether <paramref name="eci"/> names a set this decodes.</summary>
        internal static bool IsDecoded(int eci) => Native.Contains(eci) || CodePages.ContainsKey(eci) || Iso8859Parts.ContainsKey(eci);

        /// <summary>Why an ECI is refused, naming its number in the six digits the standards write it in.</summary>
        internal static string NotDecoded(string symbology, int eci)
            => $"{symbology} ECI {eci:D6} names a character set this reader does not decode (ECI 0–13, 15–18 and 20–35 are read)";

        /// <summary>
        /// <paramref name="bytes"/> in <paramref name="eci"/>'s set, or <c>null</c> with the reason
        /// when the set cannot hold them.
        /// </summary>
        internal static string Decode(string symbology, byte[] bytes, int eci, out string refusal)
        {
            refusal = null;
            try
            {
                switch (eci)
                {
                    case 26: return new UTF8Encoding(false, true).GetString(bytes);
                    case 25:
                    case 33:
                        if (bytes.Length % 2 != 0) throw new ArgumentException("an odd number of bytes");
                        return new UnicodeEncoding(eci == 25, false, true).GetString(bytes);
                    case 34:
                    case 35:
                        if (bytes.Length % 4 != 0) throw new ArgumentException("a byte count that is not a multiple of four");
                        return new UTF32Encoding(eci == 34, false, true).GetString(bytes);
                    case 27:
                        foreach (byte b in bytes) if (b > 0x7F) throw new ArgumentException($"byte {b} is not ASCII");
                        return Core.Text.Latin1Text.Decode(bytes);
                    case 1:
                    case 3:
                        return Core.Text.Latin1Text.Decode(bytes);
                }
                if (CodePages.TryGetValue(eci, out int codePage))
                    return Strict(codePage).GetString(bytes);
                if (Iso8859Parts.TryGetValue(eci, out var iso))
                {
                    // Below 0xA0 every ISO-8859 part is ASCII and the C1 controls; above, the part's own table.
                    var text = new StringBuilder(bytes.Length);
                    foreach (byte b in bytes)
                    {
                        char c = b < 0xA0 ? (char)b : iso.Upper[b - 0xA0];
                        if (c == Undefined) throw new ArgumentException($"byte {b} names no character in ISO-8859-{iso.Part}");
                        text.Append(c);
                    }
                    return text.ToString();
                }
                refusal = NotDecoded(symbology, eci);
                return null;
            }
            catch (ArgumentException ex)
            {
                refusal = $"{symbology} data after ECI {eci:D6} is not valid in that character set ({ex.Message})";
                return null;
            }
        }

        /// <summary>
        /// <paramref name="codePage"/> from the code-page provider with an exception fallback, so a byte
        /// sequence the set cannot hold is refused, not replaced (a <see cref="DecoderFallbackException"/>
        /// is an <see cref="ArgumentException"/>).
        /// </summary>
        internal static Encoding Strict(int codePage)
        {
            var encoding = (Encoding)CodePagesEncodingProvider.Instance.GetEncoding(codePage).Clone();
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            return encoding;
        }
    }
}