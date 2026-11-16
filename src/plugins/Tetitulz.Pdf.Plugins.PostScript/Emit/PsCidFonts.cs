// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// Recognises CJK composite fonts and the Adobe predefined CMaps that encode them.
    ///
    /// <para><b>The bytes are never decoded, and that is the whole trick.</b> A PostScript composite
    /// font uses a CMap to turn a byte string into CIDs; a PDF Type0 font naming the <i>same</i>
    /// predefined CMap does the identical job inside the reader. So a <c>show</c> operand passes
    /// through into <c>Tj</c> unchanged and the reader's CMap decodes it — no CJK font, no glyph
    /// table and no character-set knowledge is needed on this side.</para>
    ///
    /// <para><b>Where the CMap names come from.</b>
    /// <c>Core.Fonts.PredefinedCMapRegistry</c> is populated by
    /// <c>Tetitulz.Pdf.Plugins.CMap</c>, which embeds Adobe's ~700 CMap resources. The registry
    /// lives in <b>Core</b>, so this plugin consults it without referencing that plugin — which
    /// would be a plugin-to-plugin dependency the architecture does not permit. If the host has not
    /// registered the CMap plugin the registry is empty, nothing is recognised as CJK, and the
    /// conversion says so rather than guessing.</para>
    /// </summary>
    internal static class PsCidFonts
    {
        /// <summary>True when <paramref name="cmapName"/> is a registered Adobe predefined CMap.</summary>
        /// <remarks>
        /// Identity-H and Identity-V are predefined (ISO 32000-1 §9.7.5.2) and need no file, so the
        /// registry, which holds Adobe's CMap files, does not list them: <c>Face-Identity-H</c> was
        /// split as the face <c>Face-Identity</c> under the CMap <c>H</c>, a JIS X 0208 encoding.
        /// </remarks>
        public static bool IsKnownCMap(string cmapName)
            => !string.IsNullOrEmpty(cmapName)
               && (cmapName == "Identity-H" || cmapName == "Identity-V" || PredefinedCMapRegistry.IsRegistered(cmapName));

        /// <summary>
        /// Splits a composite font name of the form <c>Face-CMapName</c>.
        ///
        /// <para>CJK PostScript names a font and its encoding together —
        /// <c>KozMinPro-Regular-83pv-RKSJ-H</c> is the face <c>KozMinPro-Regular</c> under the CMap
        /// <c>83pv-RKSJ-H</c>. The split point cannot be found by counting hyphens because both
        /// halves contain them; the only reliable test is asking the registry which trailing
        /// substring is a real CMap.</para>
        /// </summary>
        public static bool TrySplitCompositeName(string fontName, out string face, out string cmapName)
        {
            face = null;
            cmapName = null;
            if (string.IsNullOrEmpty(fontName)) return false;

            // Walk hyphens left to right; the first suffix that names a registered CMap wins.
            for (int i = 0; i < fontName.Length; i++)
            {
                if (fontName[i] != '-') continue;

                // Length-check BEFORE copying. A Substring at every hyphen would copy and hash O(n²)
                // characters of an n-character name with n/2 hyphens — and a
                // program can build a 500 KB font name without a single long token in its source,
                // uncharged. No registered Adobe CMap name is anywhere near this long, so a longer
                // suffix cannot be one and needs no copy to rule out.
                if (fontName.Length - i - 1 > PsLimits.MaxCMapNameChars) continue;

                string candidate = fontName.Substring(i + 1);
                if (!IsKnownCMap(candidate)) continue;

                face = fontName.Substring(0, i);
                cmapName = candidate;
                return true;
            }

            return false;
        }

        /// <summary>
        /// The character collection a CMap belongs to, as <c>(Registry, Ordering, Supplement)</c>.
        ///
        /// <para>Derived from the CMap name rather than parsed out of the file: every Adobe CMap
        /// name carries its collection in a fixed vocabulary, and reading the file would mean
        /// getting its bytes back out of a registry that only answers "is it there".</para>
        /// </summary>
        public static (string Registry, string Ordering, int Supplement) CollectionFor(string cmapName)
        {
            if (string.IsNullOrEmpty(cmapName)) return ("Adobe", "Identity", 0);

            // Identity-H / Identity-V address glyphs directly and have their own ordering.
            if (cmapName.StartsWith("Identity", StringComparison.Ordinal))
                return ("Adobe", "Identity", 0);

            // The Adobe-Japan1 CMaps whose names carry no collection marker at all — the JIS X 0208
            // originals (H, V, 78-H, 78-V) and the NEC, Fujitsu and vertical-kana sets — fell
            // through to Identity, and a reader then took the shown codes as glyph indices.
            if (Array.IndexOf(UnmarkedJapan1, cmapName) >= 0) return ("Adobe", "Japan1", 7);

            foreach (var (marker, ordering, supplement) in Collections)
                if (cmapName.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    return ("Adobe", ordering, supplement);

            // An unrecognised CMap still gets a plausible collection rather than nothing: a Type0
            // font without /CIDSystemInfo is malformed, and Identity is the one that assumes least.
            return ("Adobe", "Identity", 0);
        }

        // Marker substrings that identify a character collection, with the supplement this
        // converter declares.
        //
        // THE RULE IS: DECLARE THE HIGHEST SUPPLEMENT ADOBE HAS PUBLISHED FOR THE COLLECTION.
        // A supplement is additive — Adobe-Japan1-7 contains every CID Adobe-Japan1-6 does — so a
        // font declaring at least what the CMap needs is the one a reader accepts, and declaring
        // LESS is what makes it reject the font or drop the CIDs above the declared range. The
        // maximum published value is therefore the safe declaration, not the risky one.
        //
        // So a collection added to the table takes Adobe's highest published supplement (GB1-5,
        // CNS1-7, Japan1-7, Korea1-2); the LOWEST would declare less coverage than its CMap requires.
        //
        // CjkCompositeFontTests pins the declared /Supplement per collection, so the table cannot
        // drift from this paragraph without a test saying so.
        // Adobe-Japan1 CMaps named with no collection marker, matched whole (Adobe's cmap-resources
        // Adobe-Japan1-7 set). A substring match would take every -H CMap for Japanese.
        private static readonly string[] UnmarkedJapan1 =
            { "H", "V", "78-H", "78-V", "Hankaku", "Hiragana", "Katakana", "Roman", "WP-Symbol" };

        private static readonly (string Marker, string Ordering, int Supplement)[] Collections =
        {
            // Order matters — the first marker that appears in the name wins — so the
            // collection-specific prefixes come before the encoding-family ones, and no marker may
            // be a bare letter. ("H" was one, briefly, and matched the trailing -H of *every*
            // horizontal CMap, making UniCNS-UCS2-H Japanese.)

            // Simplified Chinese
            ("UniGB",   "GB1", 5),    ("GBK",   "GB1", 5),     ("GBpc",  "GB1", 5),
            ("GBT",     "GB1", 5),    ("GB-",   "GB1", 5),
            // Traditional Chinese
            ("UniCNS",  "CNS1", 7),   ("ETen",  "CNS1", 7),    ("HKscs", "CNS1", 7),
            ("B5",      "CNS1", 7),   ("CNS",   "CNS1", 7),
            // Korean
            ("UniKS",   "Korea1", 2), ("KSCms", "Korea1", 2),  ("KSCpc", "Korea1", 2),
            ("KSC",     "Korea1", 2),
            // Japanese — last, because RKSJ/EUC are encoding families rather than collection
            // names and would otherwise claim a Chinese or Korean CMap that shares them.
            ("UniJIS",  "Japan1", 7), ("RKSJ",  "Japan1", 7),  ("83pv",  "Japan1", 7),
            ("90pv",    "Japan1", 7), ("90ms",  "Japan1", 7),  ("Add",   "Japan1", 7),
            ("Ext",     "Japan1", 7), ("NWP",   "Japan1", 7),  ("EUC",   "Japan1", 7),
        };

        /// <summary>
        /// How many character codes a byte string holds under <paramref name="cmapName"/>.
        ///
        /// <para><b>Approximated from the CMap's naming convention, not from its codespace
        /// ranges.</b> The registry does not hand the file back, so the exact ranges are not
        /// available here. The rule below is right for the overwhelming majority of real text: the
        /// Unicode CMaps are fixed two-byte, and the legacy mixed encodings (Shift-JIS, EUC, Big5,
        /// GBK, KSC) put single-byte codes below 0x80 and two-byte codes above it.</para>
        ///
        /// <para>This only affects where the <i>pen</i> ends up after a <c>show</c>, and CJK
        /// typesetters overwhelmingly emit an explicit <c>moveto</c> per run rather than relying on
        /// the advance. It is reported so a caller knows the positioning is approximate.</para>
        /// </summary>
        public static int CountCodes(string cmapName, byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return 0;

            int count = 0;
            for (int i = 0; i < bytes.Length; )
            {
                i += CodeLength(cmapName, bytes, i);
                count++;
            }
            return count;
        }

        /// <summary>
        /// The byte string as its codes, split by the rule <see cref="CountCodes"/> counts them —
        /// for the positioned shows, which place one glyph per <b>code</b>, not per byte. A
        /// two-byte code cut short by the end of the string is kept as it stands.
        /// </summary>
        public static List<byte[]> SplitCodes(string cmapName, byte[] bytes)
        {
            var codes = new List<byte[]>();
            if (bytes == null) return codes;

            for (int i = 0; i < bytes.Length; )
            {
                int length = Math.Min(CodeLength(cmapName, bytes, i), bytes.Length - i);
                var code = new byte[length];
                Array.Copy(bytes, i, code, 0, length);
                codes.Add(code);
                i += length;
            }
            return codes;
        }

        /// <summary>How many bytes the code starting at <paramref name="at"/> occupies.</summary>
        private static int CodeLength(string cmapName, byte[] bytes, int at)
            => IsFixedTwoByte(cmapName) || bytes[at] >= 0x80 ? 2 : 1;

        /// <summary>
        /// True for the CMaps whose codes are always two bytes — the Unicode ones and Identity.
        /// </summary>
        private static bool IsFixedTwoByte(string cmapName)
        {
            if (string.IsNullOrEmpty(cmapName)) return true;

            return cmapName.StartsWith("Identity", StringComparison.Ordinal)
                || cmapName.IndexOf("UCS2", StringComparison.Ordinal) >= 0
                || cmapName.IndexOf("UTF16", StringComparison.Ordinal) >= 0;
        }
    }
}
