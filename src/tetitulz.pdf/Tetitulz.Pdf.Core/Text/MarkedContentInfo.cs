// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// What a <c>BDC</c> property list says: its <c>/MCID</c> and its §14.9.4 <c>/ActualText</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>One reading of the rule, because there are two text extractors.</b>
    /// <c>Core.Text.PdfTextExtractor</c> produces a string and
    /// <c>Base.Operations.Extraction.PdfStructuredTextExtractor</c> produces positioned glyphs, and
    /// they must not disagree about what a marked-content sequence declares — a document whose
    /// <c>/ActualText</c> is honoured by one and ignored by the other extracts two different
    /// documents depending on which entry point a caller reached for.</para>
    ///
    /// <para><b>What is shared is the reading, not the applying.</b> Splicing a replacement into a
    /// string builder and splicing it into a list of positioned characters are genuinely different
    /// operations, and each extractor keeps its own. What is here is the part where a difference
    /// would be a bug rather than a design: where the property list lives, which keys it carries,
    /// and that an empty <c>/ActualText</c> is not an absent one.</para>
    ///
    /// <para><b>Public because it crosses the assembly boundary</b>, and a leaf by construction —
    /// no static fields, so no type initializer.</para>
    /// </remarks>
    public readonly struct MarkedContentInfo
    {
        private MarkedContentInfo(int mcid, string actualText)
        {
            Mcid = mcid;
            ActualText = actualText;
        }

        /// <summary>The <c>/MCID</c> binding this sequence to a structure element, or -1.</summary>
        public int Mcid { get; }

        /// <summary>
        /// §14.9.4's exact replacement for the sequence's content, or <see langword="null"/> when
        /// the sequence declares none.
        /// </summary>
        /// <remarks>
        /// <b>An empty string is not <see langword="null"/>.</b> <c>/ActualText ()</c> says the
        /// sequence's content is nothing — how a producer marks a decorative glyph run, a repeated
        /// drop cap or a rule drawn with characters as contributing no text — and reading it as
        /// absent leaves exactly the glyphs the producer went out of their way to suppress.
        /// </remarks>
        public string ActualText { get; }

        /// <summary>
        /// Reads a <c>BDC</c> property operand, following a name through the page's
        /// <c>/Properties</c> resource.
        /// </summary>
        /// <param name="propsOperand">The operand after the tag: an inline dictionary or a name.</param>
        /// <param name="resources">The resource dictionary in scope; may be null.</param>
        /// <param name="document">Used to follow indirect references; may be null.</param>
        /// <remarks>
        /// <b>Both spellings are legal and a producer picks one per file</b>, so a reader that
        /// handled only the inline form would apply marked-content rules on some documents and not
        /// others, with nothing in either to say which it had done.
        /// </remarks>
        public static MarkedContentInfo Read(
            PdfObject propsOperand, PdfDictionary resources, PdfDocument document)
        {
            PdfDictionary props = ResolvePropertyDictionary(propsOperand, resources, document);
            if (props == null) return new MarkedContentInfo(-1, null);

            // Resolved: an inline property list cannot hold a reference, but one named through
            // /Properties is an ordinary dictionary, where /ActualText 6 0 R is legal — and read
            // raw it was not applied, and the glyphs it replaces were extracted with no /MCID.
            // IntValue saturates: a cast of the double wrapped an MCID past int on .NET Framework.
            int mcid = Entry(props, PdfName.MCID, document) is PdfNumber n ? n.IntValue : -1;
            string actual = Entry(props, PdfName.ActualText, document) is PdfString s ? s.GetText() : null;

            return new MarkedContentInfo(mcid, actual);
        }

        /// <summary>
        /// Whether a <c>BMC</c> or <c>BDC</c> tag is §14.8.2.3.3's <c>/ReversedChars</c>: the
        /// sequence's show strings hold its characters in the reverse of their logical order.
        /// </summary>
        /// <remarks>
        /// Chromium (Skia's PDF backend) writes every right-to-left run so: drawn left to right,
        /// each character decoding to its logical self — so a bracket the run draws turned round
        /// decodes to the bracket it is, not the one it looks like
        /// (<see cref="UnicodeBidi.AsDrawnInReversedRun"/>).
        /// </remarks>
        internal static bool IsReversedChars(PdfObject tag)
            => tag is PdfName name && PdfName.ReversedChars.Equals(name);

        private static PdfObject Entry(PdfDictionary props, PdfName key, PdfDocument document)
        {
            PdfObject raw = props[key];
            return raw == null || document == null ? raw : document.ResolveObject(raw) ?? raw;
        }

        /// <summary>
        /// The dictionary a <c>BDC</c> property operand names, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Public — not just a private step of <see cref="Read"/> — because a <c>/OC</c>-tagged
        /// <c>BDC</c> block's property operand resolves to the optional-content group (or OCMD)
        /// dictionary itself, per §8.11.4.2, rather than to an <c>/MCID</c>/<c>/ActualText</c>
        /// carrier. A caller checking <b>that</b> (a PDF/X conformance validator, say) needs the
        /// same two-spelling resolution — inline dictionary vs. a name through
        /// <c>/Properties</c> — this method already has, and duplicating it is exactly the
        /// "two implementations that must agree" case CLAUDE.md's DRY section calls out.
        /// </remarks>
        public static PdfDictionary ResolvePropertyDictionary(
            PdfObject propsOperand, PdfDictionary resources, PdfDocument document)
        {
            if (propsOperand is PdfDictionary inline) return inline;
            if (!(propsOperand is PdfName name) || resources == null) return null;

            PdfObject catalogue = document != null
                ? document.ResolveObject(resources[PdfName.Properties])
                : resources[PdfName.Properties];

            if (!(catalogue is PdfDictionary map)) return null;

            PdfObject entry = document != null ? document.ResolveObject(map[name]) : map[name];
            return entry as PdfDictionary;
        }
    }
}
