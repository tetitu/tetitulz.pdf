// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The CIDs a CMap maps codes to — for ISO 19005-1 6.1.12 and 19005-2/3 6.1.13's implementation
    /// limit (rule -10: "the largest CID shall not exceed 65535"). The public door to
    /// <c>CMapParser.SectionTokens</c>, the one reader of a CMap's sections.
    /// </summary>
    public static class CMapCids
    {
        /// <summary>
        /// The largest CID the CMap's <c>cidchar</c> and <c>cidrange</c> sections reach — a <c>cidchar</c>'s
        /// CID, and a <c>cidrange</c>'s start CID plus its span — or -1 when they name none. Counted in
        /// <see cref="long"/>, so a range written to run past 32 bits is seen rather than wrapped.
        /// </summary>
        public static long MaximalCid(byte[] cmap)
        {
            if (cmap == null || cmap.Length == 0) return -1;
            string text = Latin1Text.Decode(cmap);
            long max = -1;

            foreach (var (kind, token) in CMapParser.SectionTokens(text, "begincidchar", "endcidchar"))
                if (kind == CMapParser.SectionTokenKind.Integer && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long cid) && cid > max)
                    max = cid;

            // The two hex strings nearest before each integer are the entry's bounds.
            string low = null, high = null;
            foreach (var (kind, token) in CMapParser.SectionTokens(text, "begincidrange", "endcidrange"))
            {
                if (kind == CMapParser.SectionTokenKind.Hex)
                {
                    if (low == null) low = token;
                    else if (high == null) high = token;
                    else { low = high; high = token; }
                    continue;
                }
                if (kind == CMapParser.SectionTokenKind.Integer && low != null && high != null
                    && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long start)
                    && TryParseHex(low, out long lo) && TryParseHex(high, out long hi) && hi >= lo)
                {
                    long last = start + (hi - lo);
                    if (last > max) max = last;
                }
                low = high = null;
            }
            return max;
        }

        /// <summary>A code of up to four bytes, written as hex digits, as a number.</summary>
        private static bool TryParseHex(string hex, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(hex) || hex.Length > 8) return false;
            return long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }
    }
}
