// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A <c>/ToUnicode</c> CMap's values against the rule ISO 14289-1 7.21.7, ISO 19005-2/3 6.2.11.7.2 and
    /// ISO 19005-4 6.2.10.7 share: every value is greater than zero and neither U+FEFF nor U+FFFE. A
    /// narrow public door to the internal <c>CMapParser</c>, for the conformance plugin.
    /// </summary>
    public static class ToUnicodeValues
    {
        /// <summary>
        /// The first code whose value holds U+0000, U+FEFF or U+FFFE, with that character — or
        /// <see langword="false"/> when none does (or the CMap holds no mapping the parser reads).
        /// </summary>
        public static bool TryFindForbidden(byte[] cmapData, out int code, out char value)
        {
            code = 0;
            value = '\0';
            if (cmapData == null || cmapData.Length == 0) return false;
            foreach (var kvp in CMapParser.Parse(cmapData))
            {
                if (kvp.Value == null) continue;
                foreach (char c in kvp.Value)
                    if (c == '\0' || c == '\uFEFF' || c == '\uFFFE')
                    {
                        code = kvp.Key;
                        value = c;
                        return true;
                    }
            }
            return false;
        }
    }
}
