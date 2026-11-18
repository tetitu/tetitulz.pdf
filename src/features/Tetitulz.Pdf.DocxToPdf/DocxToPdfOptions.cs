// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf
{
    public sealed class DocxToPdfOptions
    {
        /// <summary>
        /// Conversion engine to use. Default: BuiltIn.
        /// </summary>
        public ConversionEngine Engine { get; set; } = ConversionEngine.BuiltIn;

        /// <summary>
        /// Path to the LibreOffice executable (soffice).
        /// Only used when Engine is LibreOffice.
        /// Null means auto-detect from PATH and common install locations.
        /// </summary>
        public string LibreOfficePath { get; set; }

        /// <summary>
        /// Page width in points (72 points = 1 inch). Default: 612 (US Letter).
        /// Only applies to the BuiltIn engine.
        /// </summary>
        public double PageWidth { get; set; } = PdfPageSizes.LetterWidth;

        /// <summary>
        /// Page height in points. Default: 792 (US Letter).
        /// </summary>
        public double PageHeight { get; set; } = PdfPageSizes.LetterHeight;

        /// <summary>
        /// Top margin in points. Default: 72 (1 inch).
        /// </summary>
        public double MarginTop { get; set; } = PdfUnits.PointsPerInch;

        /// <summary>
        /// Bottom margin in points. Default: 72 (1 inch).
        /// </summary>
        public double MarginBottom { get; set; } = PdfUnits.PointsPerInch;

        /// <summary>
        /// Left margin in points. Default: 72 (1 inch).
        /// </summary>
        public double MarginLeft { get; set; } = PdfUnits.PointsPerInch;

        /// <summary>
        /// Right margin in points. Default: 72 (1 inch).
        /// </summary>
        public double MarginRight { get; set; } = PdfUnits.PointsPerInch;

        /// <summary>
        /// Default font size in points when the DOCX does not specify one. Default: 12.
        /// </summary>
        public double DefaultFontSize { get; set; } = 12;

        /// <summary>
        /// Whether to embed images found in the DOCX. Default: true.
        /// </summary>
        public bool EmbedImages { get; set; } = true;

        /// <summary>
        /// Whether to compress content streams with FlateDecode. Default: true.
        /// </summary>
        public bool CompressStreams { get; set; } = true;

        /// <summary>
        /// Whether to use DOCX document margins if available. Default: true.
        /// </summary>
        public bool UseDocumentMargins { get; set; } = true;

        /// <summary>
        /// Controls how the BuiltIn engine produces the PDF.
        /// <para><c>true</c> (default) uses the mature in-house OOXML renderer — full
        /// fidelity (images, live PAGE/NUMPAGES fields, /Link annotations, multi-section,
        /// header variants, etc.).</para>
        /// <para><c>false</c> maps the DOCX to the centralized <c>Tetitulz.Pdf.Builder</c>
        /// document model and renders it via Builder (a single shared engine, which also
        /// enables exporting the same document to DOCX/HTML/ODT). The Builder path is newer and
        /// lower-fidelity, and what it actually costs is:</para>
        /// <list type="bullet">
        ///   <item><description><b>2-D math typesetting.</b> An <c>m:oMath</c> equation is
        ///   linearised to text — <c>(a)/(b)</c>, <c>√(x)</c>, <c>x^(2)</c> — rather than laid out,
        ///   and the approximation is reported through
        ///   <see cref="DocxToPdfConverter.DiagnosticReported"/>.</description></item>
        ///   <item><description><b>Drawings, charts, SmartArt and shapes.</b> Only pictures (an
        ///   <c>a:blip</c>) are mapped; the rest are absent, and anything carrying text is
        ///   reported.</description></item>
        ///   <item><description><b>Multi-section page sizing.</b> One section's geometry applies to
        ///   the whole document.</description></item>
        /// </list>
        /// <para><b>Inline images, headers/footers and external hyperlinks DO work on this path</b> — the mapper
        /// reads a hyperlink's relationship, not its anchor alone. Measured, not assumed, and pinned by
        /// <c>BuilderPathContentLossTests</c>: nothing enforces prose. Live <c>PAGE</c> /
        /// <c>NUMPAGES</c> fields are <b>mapped</b> here (to the exporter's placeholders) but their
        /// per-page resolution is deliberately not claimed either way — the fixture written to check
        /// it could not produce a second page on <i>either</i> renderer, so the question is open
        /// rather than answered.</para>
        /// </summary>
        public bool UseLegacyRenderer { get; set; } = true;

        /// <summary>
        /// The most pages the conversion writes, on either path; <b>0, the default, for no limit</b>. A
        /// document that would run longer ends at the last whole page within the limit — its headers, footers
        /// and footnotes drawn — and <see cref="DocxToPdfConverter.DiagnosticReported"/> says so. A host
        /// converting documents it was handed sets the ceiling it can afford: the page count is the file's to
        /// choose. A negative value is refused.
        /// </summary>
        public int MaxPages
        {
            get => _maxPages;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "MaxPages is 0 for no limit, or a positive page count.");
                _maxPages = value;
            }
        }

        private int _maxPages;

        /// <summary>
        /// The PDF version the header names, on either path of the BuiltIn engine; <b><c>null</c>, the
        /// default, for the lowest version the content needs, never below 1.4</b>. A version below what the
        /// content uses (a tagged document's structure tree is 1.3, its <c>/MarkInfo</c> 1.4; an OpenType
        /// font program 1.6) is raised to it and <see cref="DocxToPdfConverter.DiagnosticReported"/> says so;
        /// a version no edition of the standard publishes (1.0 to 1.7, and 2.0) is refused. LibreOffice and
        /// Word write their own header and do not read it.
        /// </summary>
        public Tetitulz.Pdf.Core.Document.PdfVersion? Version
        {
            get => _version;
            set
            {
                if (value.HasValue && !value.Value.IsPublished)
                    throw new ArgumentOutOfRangeException(nameof(value), value.Value.ToString(),
                        "A PDF version is 1.0 to 1.7, or 2.0.");
                _version = value;
            }
        }

        private Tetitulz.Pdf.Core.Document.PdfVersion? _version;

        /// <summary>
        /// Allow a font picked up off the host machine to be embedded even when its <c>OS/2</c>
        /// <c>fsType</c> forbids it. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>Embedding a font redistributes it, and <c>fsType</c> is where the foundry says
        /// whether that is allowed.</b> A font the library picks up off the machine by itself is
        /// the case where nobody decided anything, so the only statement available is the one in
        /// the file — which is why the default refuses it and says so on
        /// <c>DocxToPdfConverter.LastDiagnostic</c> rather than shipping the outlines.</para>
        ///
        /// <para><b>A font embedded in the DOCX itself is not affected</b>, and that distinction is
        /// the point: the document's author put it there, so they may hold a licence the bits know
        /// nothing about, and refusing it would be this library overruling its own user. Only the
        /// OS-font search is gated.</para>
        ///
        /// <para>Set this true where the deployment owns the fonts it installed — the same
        /// situation <c>TETITULZ_PDF_FONTDIR</c> exists for. It is the counterpart of
        /// <c>Core.Fonts.FontResolutionRequest.AllowRestrictedSystemFonts</c> and of Builder's
        /// <c>PdfExportOptions.FontProvider</c>: each engine needs one way through, or a deployment
        /// that does hold the licence has no recourse.</para>
        /// </remarks>
        public bool AllowRestrictedSystemFonts { get; set; }

        /// <summary>
        /// When <c>true</c>, the BuiltIn legacy renderer emits a basic logical structure tree
        /// (Tagged PDF): the catalog is marked <c>/Marked true</c> with a <c>/StructTreeRoot</c>,
        /// and body paragraphs/headings are wrapped in marked content (BDC/EMC) and exposed as
        /// <c>/P</c> / <c>/H1</c>…<c>/H6</c> structure elements with a parent tree. Off by default
        /// (untagged output is unchanged). Tables, lists, images and header/footer content are not
        /// yet placed in the structure tree (a v1 limitation).
        /// </summary>
        public bool TaggedPdf { get; set; } = false;

        public static DocxToPdfOptions Default => new DocxToPdfOptions();
    }
}
