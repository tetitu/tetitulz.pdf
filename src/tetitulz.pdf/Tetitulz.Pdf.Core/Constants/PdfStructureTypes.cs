// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Standard structure types (ISO 32000-1 Table 333–337) — the <c>/S</c> value of a structure
    /// element.
    ///
    /// <para><c>const string</c> rather than <see cref="Tetitulz.Pdf.Core.Objects.PdfName"/>
    /// because these are compared and switched on; the <c>PdfName</c> cache is for dictionary
    /// lookups. Same split as <c>PdfOperators</c> and <c>PdfAnnotationSubtypes</c>.</para>
    ///
    /// <para><b>Note for anyone adding a role:</b> <c>Artifact</c> is deliberately absent. An
    /// artifact is not a structure type — it is the <i>absence</i> of one, marked in the content
    /// stream rather than in the tree — and writing <c>/S /Artifact</c> is a PDF/UA violation this
    /// repository's own checker reports. Model it as a flag, never as a role.</para>
    /// </summary>
    public static class PdfStructureTypes
    {
        // ---- Grouping ----
        /// <summary>The whole document — the structure tree's usual root child.</summary>
        public const string Document = "Document";
        /// <summary>A section.</summary>
        public const string Sect = "Sect";
        /// <summary>A generic block-level grouping.</summary>
        public const string Div = "Div";
        /// <summary>A block quotation.</summary>
        public const string BlockQuote = "BlockQuote";
        /// <summary>A caption — of a table, figure or formula.</summary>
        public const string Caption = "Caption";
        /// <summary>A table of contents (§14.8.4.6.2).</summary>
        /// <remarks>
        /// <b>It contains <c>/TOCI</c> elements, not paragraphs.</b> A contents list written as a
        /// run of <c>/P</c> tells a reader nothing about what it is, and §14.8.4.6.2's whole
        /// purpose is letting one navigate it as a list of destinations.
        /// </remarks>
        public const string TOC = "TOC";
        /// <summary>One entry of a table of contents (§14.8.4.6.3).</summary>
        public const string TOCI = "TOCI";
        /// <summary>A footnote or endnote (§14.8.4.7.4).</summary>
        /// <remarks>
        /// <b>The note itself, not the mark that points at it.</b> §14.8.4.7.4 gives the mark
        /// its own role, <c>/Reference</c>, and a producer that tagged both as <c>/Note</c> would
        /// tell a reader the sentence contains the note rather than a pointer to it.
        /// </remarks>
        public const string Note = "Note";
        /// <summary>A reference to elsewhere in the document — a footnote mark, a citation.</summary>
        public const string Reference = "Reference";

        // ---- Block-level ----
        /// <summary>A paragraph.</summary>
        public const string P = "P";
        public const string H1 = "H1";
        public const string H2 = "H2";
        public const string H3 = "H3";
        public const string H4 = "H4";
        public const string H5 = "H5";
        public const string H6 = "H6";

        // ---- Lists ----
        /// <summary>A list.</summary>
        public const string L = "L";
        /// <summary>A list item — holds an optional <see cref="Lbl"/> then an <see cref="LBody"/>.</summary>
        public const string LI = "LI";
        /// <summary>A list item's label (bullet or number).</summary>
        public const string Lbl = "Lbl";
        /// <summary>A list item's body.</summary>
        public const string LBody = "LBody";

        // ---- Tables ----
        public const string Table = "Table";
        /// <summary>A table row.</summary>
        public const string TR = "TR";
        /// <summary>A table header cell.</summary>
        public const string TH = "TH";
        /// <summary>A table data cell.</summary>
        public const string TD = "TD";
        public const string THead = "THead";
        public const string TBody = "TBody";
        public const string TFoot = "TFoot";

        // ---- Inline ----
        /// <summary>A generic inline grouping — used to carry a language change.</summary>
        public const string Span = "Span";
        /// <summary>A hyperlink; pairs with an <c>/OBJR</c> to its annotation.</summary>
        public const string Link = "Link";
        /// <summary>Computer code.</summary>
        public const string Code = "Code";
        /// <summary>A quotation inside a paragraph.</summary>
        public const string Quote = "Quote";

        // ---- Illustration ----
        /// <summary>An image or drawing; needs <c>/Alt</c> to be conforming.</summary>
        public const string Figure = "Figure";
        public const string Formula = "Formula";
        /// <summary>A widget annotation's place in the structure (§14.8.4.5).</summary>
        public const string Form = "Form";

        // ---- The rest of Table 333 ----
        public const string Part = "Part";
        public const string Art = "Art";
        public const string Index = "Index";
        public const string NonStruct = "NonStruct";
        public const string Private = "Private";
        /// <summary>A heading of no stated level (§14.8.4.3.2).</summary>
        public const string H = "H";
        public const string BibEntry = "BibEntry";
        /// <summary>An annotation other than a link or a widget (PDF 1.5).</summary>
        public const string Annot = "Annot";
        public const string Ruby = "Ruby";
        public const string RB = "RB";
        public const string RT = "RT";
        public const string RP = "RP";
        public const string Warichu = "Warichu";
        public const string WT = "WT";
        public const string WP = "WP";

        /// <summary>
        /// Whether <paramref name="role"/> is one of ISO 32000-1 Table 333's standard structure types
        /// (§14.8.4): the vocabulary PDF/A-2 and -3 judge level A by, and the one Accessibility's
        /// checker and tagger widen with ISO 32000-2's additions. The one list.
        /// </summary>
        public static bool IsStandardInIso32000Part1(string role) => role switch
        {
            Document or Part or Art or Sect or Div or BlockQuote or Caption or TOC or TOCI or Index
                or NonStruct or Private => true,
            P or H or H1 or H2 or H3 or H4 or H5 or H6 => true,
            L or LI or Lbl or LBody => true,
            Table or TR or TH or TD or THead or TBody or TFoot => true,
            Span or Quote or Note or Reference or BibEntry or Code or Link or Annot
                or Ruby or RB or RT or RP or Warichu or WT or WP => true,
            Figure or Formula or Form => true,
            _ => false,
        };

        /// <summary>
        /// Whether <paramref name="role"/> is a standard structure type of the PDF Reference 1.4
        /// (§9.7.4), PDF/A-1's vocabulary: Table 333 without what PDF 1.5 added — <c>THead</c>,
        /// <c>TBody</c>, <c>TFoot</c>, <c>Annot</c> and the ruby and warichu types.
        /// </summary>
        public static bool IsStandardInPdf14(string role) => IsStandardInIso32000Part1(role) && role switch
        {
            THead or TBody or TFoot or Annot or Ruby or RB or RT or RP or Warichu or WT or WP => false,
            _ => true,
        };
    }

    /// <summary>
    /// <c>/Artifact</c> types (Table 330) — content that is page furniture rather than document
    /// content, marked in the content stream so a reader can skip it.
    /// </summary>
    public static class PdfArtifactTypes
    {
        /// <summary>Running heads, folios — anything repeated as pagination.</summary>
        public const string Pagination = "Pagination";
        /// <summary>Rules, boxes, shading — typographic decoration.</summary>
        public const string Layout = "Layout";
        /// <summary>Cut marks, colour bars.</summary>
        public const string Page = "Page";
        /// <summary>Background imagery.</summary>
        public const string Background = "Background";
    }

    /// <summary>Artifact subtypes for <see cref="PdfArtifactTypes.Pagination"/> (Table 330).</summary>
    public static class PdfArtifactSubtypes
    {
        public const string Header = "Header";
        public const string Footer = "Footer";
        public const string Watermark = "Watermark";
    }
}
