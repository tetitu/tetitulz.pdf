// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The <c>/FontDescriptor /Flags</c> bits of ISO 32000-1 Table 123, and §9.6.6.4's one
    /// question: <b>is this font symbolic?</b> — i.e. is its built-in encoding private to the font
    /// program, so neither a standard base encoding nor a reader can recover Unicode from it.
    /// </summary>
    /// <remarks>
    /// <para><b>One answer across an assembly boundary.</b> <c>PdfSimpleFont</c> (Core),
    /// <c>PdfUaChecker</c> (the Conformance plugin) and <c>PdfDeepAccessibilityChecker</c> (the
    /// Accessibility feature) all ask it — bit 3 alone, bit 3 with bit 6 clear, with or without a
    /// standard-14 fallback would be three answers. The plugin and the feature cannot reference each
    /// other, so Core is the only floor they share, and copies on either side would never see each
    /// other drift.</para>
    ///
    /// <para><b>Bit 3 alone is the rule, and Core had already settled it.</b> Table 123 says
    /// Symbolic and Nonsymbolic shall not both be set nor both be clear, so the extra clause is dead
    /// code in every conforming file — the two forms differ only for a descriptor that contradicts
    /// itself, which makes the two-clause version a policy about malformed input rather than a spec
    /// rule. <c>SymbolicFlagAgreementTests</c> pins that reasoning for the encoding side; this type
    /// carries it across to the two conformance checkers, where the same flag decides a real
    /// <b>UA26</b> (plugin) and <b>UA20</b> (deep checker) verdict.</para>
    ///
    /// <para><b>The descriptor-less fallback is part of the rule.</b> The standard 14 have no
    /// <c>/FontDescriptor</c> at all, so bit 3 is simply unavailable for them, and <c>Symbol</c> and
    /// <c>ZapfDingbats</c> are symbolic by construction. The match is on the <b>prefix</b> of the
    /// subset-stripped <c>/BaseFont</c>, so it also covers the real-world spellings — <c>SymbolMT</c>,
    /// <c>Symbol+Bold</c> — that an exact-name set misses. <c>PdfDeepAccessibilityChecker</c> tests
    /// the two exact names earlier and separately, so for it this widens the rule to those spellings
    /// rather than introducing it; <c>PdfUaChecker</c> already had exactly this test.</para>
    ///
    /// <para><b>A leaf: no static fields, hence no type initializer</b> — the same discipline
    /// <see cref="SubsetTag"/> is held to, and the reason it is safe to call from Core, a plugin and
    /// a feature alike.</para>
    /// </remarks>
    public static class FontFlags
    {
        /// <summary>Table 123 bit 3 (value 4) — <b>Symbolic</b>.</summary>
        public const int Symbolic = 1 << 2;

        /// <summary>
        /// Table 123 bit 6 (value 32) — <b>Nonsymbolic</b>. Exposed for completeness and for callers
        /// reporting on a self-contradictory descriptor; <see cref="IsSymbolic"/> deliberately does
        /// <b>not</b> consult it (see the remarks on this type).
        /// </summary>
        public const int Nonsymbolic = 1 << 5;

        /// <summary>
        /// True when <paramref name="font"/> is symbolic under §9.6.6.4 — Table 123's bit 3 when the
        /// font has a <c>/FontDescriptor</c>, otherwise the standard-14 rule that only
        /// <c>Symbol</c> and <c>ZapfDingbats</c> are symbolic.
        /// </summary>
        /// <param name="font">The font dictionary. A null dictionary is not symbolic.</param>
        /// <param name="doc">
        /// Used to follow indirect references — <c>/FontDescriptor</c>, <c>/Flags</c> and
        /// <c>/BaseFont</c> may each be one. May be null, in which case only direct values are read.
        /// </param>
        public static bool IsSymbolic(PdfDictionary font, PdfDocument doc)
        {
            if (font == null) return false;

            if (Resolve(doc, font[PdfName.FontDescriptor]) is PdfDictionary descriptor)
            {
                long flags = Resolve(doc, descriptor[PdfName.Flags]) is PdfNumber n ? n.LongValue : 0;
                return (flags & Symbolic) != 0;
            }

            // No descriptor — the standard-14 case. The subset tag is stripped first because
            // §9.6.4's "ABCDEF+" prefix is legal on any /BaseFont, and a raw StartsWith would
            // answer about the tag rather than the family.
            string baseFont = (Resolve(doc, font[PdfName.BaseFont]) as PdfName)?.Value;
            if (string.IsNullOrEmpty(baseFont)) return false;

            baseFont = SubsetTag.Strip(baseFont);
            return baseFont.StartsWith(StandardFonts.Symbol, StringComparison.Ordinal)
                || baseFont.StartsWith(StandardFonts.ZapfDingbats, StringComparison.Ordinal);
        }

        /// <summary>
        /// Follows an indirect reference, tolerating a damaged file: a resolve that throws yields the
        /// unresolved object rather than propagating out of a checker mid-audit.
        /// </summary>
        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
        {
            if (obj == null || doc == null) return obj;
            try { return doc.ResolveObject(obj) ?? obj; }
            catch { return obj; }
        }
    }
}
