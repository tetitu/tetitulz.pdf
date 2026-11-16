// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Accessibility
{
    /// <summary>
    /// The tagged-PDF standard structure types (<c>/S</c> values) this assembly recognises.
    ///
    /// <para>These are <c>const string</c>, not <see cref="Core.Objects.PdfName"/> statics, because
    /// every use is a comparison against a <i>role string</i> already read out of a dictionary —
    /// and several are <c>switch</c> case labels, which require a compile-time constant. The
    /// dictionary-key half of the problem is covered by <see cref="Core.Objects.PdfName"/>.</para>
    ///
    /// <para>They live here rather than in <c>Tetitulz.Pdf.Core</c> because tagged-PDF roles are
    /// the accessibility feature's vocabulary: the checker, the auto-tagger, the structure editor
    /// and the reading-order analyzer all speak it, and nothing in Core does.</para>
    ///
    /// <para>The PDF 1.7 entries are ISO 32000-1:2008 Table 333; the additions are ISO
    /// 32000-2:2020 Table 364. <c>H7</c> and higher are legal in PDF 2.0 and are recognised
    /// dynamically by <c>StructTreeWalker.HeadingLevel</c> rather than enumerated here.</para>
    /// </summary>
    internal static class StructureTypes
    {
        // ---- Grouping elements ----
        /// <summary><c>/Document</c> — the whole document.</summary>
        public const string Document = "Document";
        /// <summary><c>/Part</c></summary>
        public const string Part = "Part";
        /// <summary><c>/Art</c> — an article.</summary>
        public const string Art = "Art";
        /// <summary><c>/Sect</c> — a section. Its nesting depth gives a strong <c>&lt;H&gt;</c> its level.</summary>
        public const string Sect = "Sect";
        /// <summary><c>/Div</c></summary>
        public const string Div = "Div";
        /// <summary><c>/BlockQuote</c></summary>
        public const string BlockQuote = "BlockQuote";
        /// <summary><c>/Caption</c></summary>
        public const string Caption = "Caption";
        /// <summary><c>/TOC</c> — a table of contents.</summary>
        public const string Toc = "TOC";
        /// <summary><c>/TOCI</c> — one entry of a table of contents.</summary>
        public const string Toci = "TOCI";
        /// <summary><c>/Index</c></summary>
        public const string Index = "Index";
        /// <summary><c>/NonStruct</c> — a grouping element with no structural meaning of its own.</summary>
        public const string NonStruct = "NonStruct";
        /// <summary><c>/Private</c> — producer-private content, which by definition references none of the document's logical structure.</summary>
        public const string Private = "Private";

        // ---- Block-level: paragraph-like ----
        /// <summary><c>/P</c> — a paragraph.</summary>
        public const string Paragraph = "P";
        /// <summary><c>/H</c> — an <i>unnumbered</i> heading whose level comes from its <see cref="Sect"/> nesting ("strong" structure).</summary>
        public const string Heading = "H";
        /// <summary><c>/H1</c></summary>
        public const string Heading1 = "H1";
        /// <summary><c>/H2</c></summary>
        public const string Heading2 = "H2";
        /// <summary><c>/H3</c></summary>
        public const string Heading3 = "H3";
        /// <summary><c>/H4</c></summary>
        public const string Heading4 = "H4";
        /// <summary><c>/H5</c></summary>
        public const string Heading5 = "H5";
        /// <summary><c>/H6</c></summary>
        public const string Heading6 = "H6";

        // ---- Block-level: lists ----
        /// <summary><c>/L</c> — a list.</summary>
        public const string List = "L";
        /// <summary><c>/LI</c> — a list item.</summary>
        public const string ListItem = "LI";
        /// <summary><c>/Lbl</c> — a list item's label (its bullet or number).</summary>
        public const string Label = "Lbl";
        /// <summary><c>/LBody</c> — a list item's body.</summary>
        public const string ListBody = "LBody";

        // ---- Block-level: tables ----
        /// <summary><c>/Table</c></summary>
        public const string Table = "Table";
        /// <summary><c>/TR</c> — a table row.</summary>
        public const string TableRow = "TR";
        /// <summary><c>/TH</c> — a table header cell.</summary>
        public const string TableHeaderCell = "TH";
        /// <summary><c>/TD</c> — a table data cell.</summary>
        public const string TableDataCell = "TD";
        /// <summary><c>/THead</c></summary>
        public const string TableHead = "THead";
        /// <summary><c>/TBody</c></summary>
        public const string TableBody = "TBody";
        /// <summary><c>/TFoot</c></summary>
        public const string TableFoot = "TFoot";

        // ---- Inline-level ----
        /// <summary><c>/Span</c></summary>
        public const string Span = "Span";
        /// <summary><c>/Quote</c></summary>
        public const string Quote = "Quote";
        /// <summary><c>/Note</c></summary>
        public const string Note = "Note";
        /// <summary><c>/Reference</c></summary>
        public const string Reference = "Reference";
        /// <summary><c>/BibEntry</c></summary>
        public const string BibEntry = "BibEntry";
        /// <summary><c>/Code</c></summary>
        public const string Code = "Code";
        /// <summary><c>/Link</c> — the structure element wrapping a link annotation. Distinct from the annotation <c>/Subtype /Link</c>.</summary>
        public const string Link = "Link";
        /// <summary><c>/Annot</c> — the structure element wrapping any other annotation.</summary>
        public const string Annot = "Annot";
        /// <summary><c>/Ruby</c></summary>
        public const string Ruby = "Ruby";
        /// <summary><c>/RB</c></summary>
        public const string RubyBase = "RB";
        /// <summary><c>/RT</c></summary>
        public const string RubyText = "RT";
        /// <summary><c>/RP</c></summary>
        public const string RubyPunctuation = "RP";
        /// <summary><c>/Warichu</c></summary>
        public const string Warichu = "Warichu";
        /// <summary><c>/WT</c></summary>
        public const string WarichuText = "WT";
        /// <summary><c>/WP</c></summary>
        public const string WarichuPunctuation = "WP";

        // ---- Illustration ----
        /// <summary><c>/Figure</c></summary>
        public const string Figure = "Figure";
        /// <summary><c>/Formula</c></summary>
        public const string Formula = "Formula";
        /// <summary><c>/Form</c> — the structure element wrapping a form field's widget annotation.</summary>
        public const string Form = "Form";

        // ---- ISO 32000-2:2020 Table 364 additions ----
        /// <summary><c>/DocumentFragment</c> — a fragment of a larger document.</summary>
        public const string DocumentFragment = "DocumentFragment";
        /// <summary><c>/Aside</c> — content outside the main narrative flow.</summary>
        public const string Aside = "Aside";
        /// <summary><c>/Title</c> — the title of a document or section.</summary>
        public const string Title = "Title";
        /// <summary><c>/Sub</c> — a subdivision of a title or heading.</summary>
        public const string Sub = "Sub";
        /// <summary><c>/Em</c> — emphasis.</summary>
        public const string Emphasis = "Em";
        /// <summary><c>/Strong</c> — strong emphasis.</summary>
        public const string Strong = "Strong";
        /// <summary><c>/FENote</c> — a footnote or endnote.</summary>
        public const string FENote = "FENote";

        /// <summary>
        /// <c>/Artifact</c> — a <b>PDF 2.0</b> standard structure type only. Under PDF 1.7, and
        /// therefore under PDF/UA-1, an artifact is a content-stream concept
        /// (<see cref="MarkedContentTags.Artifact"/>) and never a structure element, which is why
        /// the PDF/UA-1 checker deliberately excludes this from its standard-role set.
        /// </summary>
        public const string Artifact = "Artifact";

        /// <summary>
        /// Whether <paramref name="role"/> is a standard structure type — one an element may use
        /// with no <c>/RoleMap</c> entry behind it.
        /// </summary>
        /// <remarks>
        /// <para><b>One set, because two callers decide opposite things with it.</b> The deep
        /// checker asks in order to <i>report</i> a role that resolves to nothing standard (UA11);
        /// the auto-tagger asks in order to decide whether it may <i>adopt</i> a producer's existing
        /// marked-content tag as a structure element's <c>/S</c>. A tag the checker would flag is
        /// exactly the tag the tagger must not copy, so the two questions have to be the same
        /// question — and a second copy of a forty-eight-name list is a place for them to drift.</para>
        ///
        /// <para><c>H7</c> and above are accepted dynamically rather than enumerated: PDF 2.0
        /// permits arbitrary heading levels, so a list of them would be both endless and wrong.</para>
        ///
        /// <para><b><c>Artifact</c> is deliberately absent.</b> It is not a structure type in
        /// PDF 32000-1:2008 Table 333 — under PDF 1.7, and therefore under PDF/UA-1, artifacts are
        /// a <i>content-level</i> concept marked with <c>/Artifact BMC</c>…<c>EMC</c> in the
        /// content stream, never an element in the structure tree. Listing it here let a
        /// <c>/RoleMap</c> that pointed at it pass UA13, which is exactly the mistake that produced
        /// <c>/S /Artifact</c> structure elements.</para>
        ///
        /// <para><b>Known limitation.</b> ISO 32000-2:2020 <i>does</i> admit <c>Artifact</c> into
        /// its standard structure namespace, so a PDF 2.0 file that legitimately uses
        /// <c>/S /Artifact</c> is reported by UA13. Deciding correctly would mean reading the
        /// element's <c>/NS</c> and the document's <c>/Namespaces</c> and judging each element
        /// against the namespace it declares — which is PDF/UA-2 territory. This checker validates
        /// PDF/UA-1, where excluding <c>Artifact</c> is right; the PDF 2.0 case is recorded in
        /// GAPS.md rather than papered over by widening this set, because widening it would let
        /// the <c>/S /Artifact</c> mistake back in for every PDF 1.7 file.</para>
        /// </remarks>
        public static bool IsStandard(string role)
            => role != null
               && (Core.Constants.PdfStructureTypes.IsStandardInIso32000Part1(role) || Pdf20Additions.Contains(role)
                   || Internal.StructTreeWalker.HeadingLevel(role) >= 1);

        /// <summary>
        /// The ISO 32000-2:2020 Table 364 additions to ISO 32000-1:2008 Table 333 (which is Core's,
        /// <see cref="Core.Constants.PdfStructureTypes.IsStandardInIso32000Part1"/>, shared with
        /// PDF/A's level A rules), so a valid PDF 2.0 producer is not false-flagged.
        /// </summary>
        private static readonly System.Collections.Generic.HashSet<string> Pdf20Additions =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        {
            DocumentFragment,   // grouping: a fragment of a larger document
            Aside,              // grouping: content outside the main narrative flow
            Title,              // block-level: the title of a document or section
            Sub,                // block-level: a subdivision of a title / heading
            Emphasis,           // inline: emphasis
            Strong,             // inline: strong emphasis
            FENote,             // inline: footnote or endnote
        };
    }

    /// <summary>
    /// The <c>/Type</c> values of the two <c>/K</c> children that are <b>not</b> structure
    /// elements (ISO 32000-1:2008, Tables 324 and 325). Both are leaves: they name content, so a
    /// tree walk exposes them but never descends into them.
    /// </summary>
    internal static class StructureKidTypes
    {
        /// <summary><c>/MCR</c> — a marked-content reference, naming an MCID on a page.</summary>
        public const string MarkedContentReference = "MCR";

        /// <summary><c>/OBJR</c> — an object reference, naming an annotation or other object.</summary>
        public const string ObjectReference = "OBJR";
    }

    /// <summary>Marked-content tags written into a page content stream.</summary>
    internal static class MarkedContentTags
    {
        /// <summary>
        /// <c>/Artifact</c> — the tag that marks a run of content as decorative or as pagination
        /// furniture (ISO 32000-1:2008 §14.8.2). Content inside one is deliberately unreachable
        /// from the structure tree, which is the opposite of the PDF 2.0 structure type that
        /// shares its spelling (<see cref="StructureTypes.Artifact"/>).
        /// </summary>
        public const string Artifact = "Artifact";
    }
}
