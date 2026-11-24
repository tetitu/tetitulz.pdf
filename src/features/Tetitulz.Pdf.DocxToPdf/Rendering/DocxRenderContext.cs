// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class DocxRenderContext
    {
        private readonly WordprocessingDocument _wordDoc;
        private readonly DocxToPdfOptions _options;
        private readonly StyleResolver _styleResolver;
        private readonly List<PdfPageBuilder> _pageBuilders = new List<PdfPageBuilder>();
        private PdfPageBuilder _currentPage;

        private double _marginTop;
        private double _marginBottom;
        private double _marginLeft;
        private double _marginRight;

        /// <summary>
        /// Sentinel assigned to <c>_marginBottom</c> while drawing a band that must not paginate
        /// (a header/footer, or the footnote band itself). Far below any real page bottom, so
        /// <see cref="NeedsNewPage"/> can never fire; the caller restores the saved value.
        /// </summary>
        private const double NoPageBottomSentinel = -1_000_000;

        // Resolved page dimensions (may differ from _options when UseDocumentMargins applies).
        // Stored here instead of mutating the user-provided options object.
        private double _pageWidth;
        private double _pageHeight;

        // Multi-column layout (w:sectPr/w:cols). A section defines 1+ columns; body flow
        // fills column 0 top→bottom, then column 1, … then a new page. Each entry is the
        // column's (left edge, width) in PDF user space. Default = one full-width column.
        private readonly List<(double left, double width)> _columns = new List<(double, double)>();
        private int _currentColumn;
        private bool _columnSeparator;
        private double ColumnTop => _pageHeight - _marginTop;

        // A floating image anchored on the current page: an exclusion rectangle that body
        // lines wrap around (see GetLineSegment). BehindText floats (wrapNone / behindDoc)
        // are painted but exert no exclusion. Cleared whenever a new page starts.
        internal readonly struct FloatRect
        {
            public readonly double Left, Right, Top, Bottom; // PDF user space; Top > Bottom
            public readonly bool BehindText;
            public FloatRect(double left, double right, double top, double bottom, bool behindText)
            { Left = left; Right = right; Top = top; Bottom = bottom; BehindText = behindText; }
        }
        private readonly List<FloatRect> _pageFloats = new List<FloatRect>();

        // Per-document list counters (replaces the former static field in ListRenderer).
        internal Dictionary<int, int> ListCounters { get; } = new Dictionary<int, int>();

        // Document-level embedded composite fonts (one font program is embedded once
        // and shared by every page that uses it).
        private readonly List<EmbeddedFontEntry> _embeddedFonts = new List<EmbeddedFontEntry>();

        internal sealed class EmbeddedFontEntry
        {
            public string ResName;
            public Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram Program;
            // glyph id → Unicode code point, for /W and /ToUnicode.
            public readonly Dictionary<ushort, int> UsedGidToUnicode = new Dictionary<ushort, int>();
            // a ligature glyph id → the letters it stands for, for /ToUnicode.
            public readonly Dictionary<ushort, string> UsedGidToText = new Dictionary<ushort, string>();
        }

        /// <summary>
        /// Registers a font program for embedding (deduped by program instance) and
        /// returns its entry. Callers record drawn glyphs into <c>UsedGidToUnicode</c>.
        /// </summary>
        internal EmbeddedFontEntry RegisterEmbeddedFont(Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram program)
        {
            foreach (var e in _embeddedFonts)
                if (ReferenceEquals(e.Program, program)) return e;
            var entry = new EmbeddedFontEntry { Program = program, ResName = "EF" + (_embeddedFonts.Count + 1) };
            _embeddedFonts.Add(entry);
            return entry;
        }

        // Fonts embedded in the DOCX itself (word/fontTable.xml + obfuscated font parts),
        // keyed by "family|variant" (variant = regular/bold/italic/bolditalic). Loaded once.
        private Dictionary<string, Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram> _embeddedFontPrograms;

        /// <summary>
        /// Returns the font program embedded in the DOCX for the given family + style that
        /// covers <paramref name="requiredCodePoints"/>, or null when the document embeds no
        /// such font. Lets a run use the author's embedded font instead of an OS substitute.
        /// </summary>
        /// <remarks>
        /// The requirement is narrowed by <see cref="FontResolver.RequiredCodePoints"/> — whitespace
        /// is not evidence of coverage. Here that matters most: the author has shipped the exact font
        /// for the text, and a <c>&lt;w:tab/&gt;</c> or <c>&lt;w:br/&gt;</c> in the same run would
        /// make <c>Covers(U+0009)</c> / <c>Covers(U+000A)</c> false, so their own embedded face would
        /// be rejected in favour of an OS substitute — or, on a host with no covering font, of blanks.
        /// </remarks>
        internal Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram ResolveEmbeddedFont(
            string family, bool bold, bool italic, IEnumerable<int> requiredCodePoints)
        {
            if (string.IsNullOrEmpty(family)) return null;
            EnsureEmbeddedFontsLoaded();
            if (_embeddedFontPrograms.Count == 0) return null;

            int[] required = FontResolver.RequiredCodePoints(requiredCodePoints);
            string lf = family.ToLowerInvariant();
            string exact = bold && italic ? "bolditalic" : bold ? "bold" : italic ? "italic" : "regular";
            foreach (var variant in new[] { exact, "regular", "bold", "italic", "bolditalic" })
                if (_embeddedFontPrograms.TryGetValue(lf + "|" + variant, out var prog)
                    && prog != null && CoversAll(prog, required))
                    return prog;
            return null;
        }

        private static bool CoversAll(Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram prog, int[] cps)
        {
            foreach (int cp in cps) if (!prog.CoversShaped(cp)) return false;
            return true;
        }

        private void EnsureEmbeddedFontsLoaded()
        {
            if (_embeddedFontPrograms != null) return;
            _embeddedFontPrograms = new Dictionary<string, Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram>(
                StringComparer.OrdinalIgnoreCase);

            var ftp = _wordDoc.MainDocumentPart?.FontTablePart;
            var fonts = ftp?.Fonts;
            if (fonts == null) return;

            foreach (var font in fonts.Elements<Font>())
            {
                string name = font.Name?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                foreach (var embed in font.ChildElements)
                {
                    string variant = embed.LocalName switch
                    {
                        "embedRegular" => "regular",
                        "embedBold" => "bold",
                        "embedItalic" => "italic",
                        "embedBoldItalic" => "bolditalic",
                        _ => null,
                    };
                    if (variant != null) TryLoadEmbeddedFont(ftp, name, embed, variant);
                }
            }
        }

        // Reads one embedded font part (by its r:id), de-obfuscates it with the w:fontKey GUID,
        // parses it, and registers it under "family|variant". Bad fonts are skipped silently
        // (the caller falls back to an OS font / standard-14).
        private void TryLoadEmbeddedFont(FontTablePart ftp, string family, OpenXmlElement embed, string variant)
        {
            string rId = OoxmlAttribute.Value(embed, "id");
            if (string.IsNullOrEmpty(rId)) return;
            string fontKey = OoxmlAttribute.Value(embed, "fontKey");
            try
            {
                var part = ftp.GetPartById(rId);
                if (part == null) return;
                // A DOCX is a ZIP; an embedded-font part can be a tiny deflated blob that
                // expands to gigabytes (zip bomb). Cap the decompressed size and skip the
                // font, mirroring the image paths (DocxToBuilderMapper / ImageRenderer).
                const long MaxFontBytes = 64L * 1024 * 1024;
                byte[] raw;
                using (var s = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var buf = new MemoryStream())
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxFontBytes) return;
                        buf.Write(chunk, 0, read);
                    }
                    raw = buf.ToArray();
                }
                byte[] data = ObfuscatedFontReader.Deobfuscate(raw, fontKey);
                var prog = Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram.Parse(data);
                if (prog != null)
                    _embeddedFontPrograms[family.ToLowerInvariant() + "|" + variant] = prog;
            }
            catch { /* malformed / unsupported embedded font — skip */ }
        }

        /// <summary>Marks that the current page references the given embedded font.</summary>
        internal void UseEmbeddedFontOnCurrentPage(string resName) => _currentPage.UseEmbeddedFont(resName);

        // ── Tagged PDF (opt-in logical structure) ───────────────────────────────────
        // Body paragraphs/headings become /P or /Hn structure elements; each drawn line is a
        // marked-content sequence (BDC/EMC) with a per-page MCID. Built into a StructTreeRoot +
        // parent tree at assembly time. Inert unless DocxToPdfOptions.TaggedPdf is set.
        private sealed class StructElem
        {
            public string Role;
            public int FirstPage = -1;
            public readonly List<(int page, int mcid)> Mcids = new List<(int, int)>();
        }
        private readonly List<StructElem> _structElems = new List<StructElem>();
        private StructElem _currentStruct;
        private readonly Dictionary<int, int> _mcidByPage = new Dictionary<int, int>();
        private bool _suppressStruct; // true while rendering header/footer/footnote bands

        /// <summary>Opens a structure element for the current paragraph (no-op unless tagging).</summary>
        internal void BeginStructElem(string role)
        {
            if (!_options.TaggedPdf || _suppressStruct) return;
            _currentStruct = new StructElem { Role = role };
        }

        /// <summary>Closes the current structure element, keeping it only if it tagged any lines.</summary>
        internal void EndStructElem()
        {
            if (_currentStruct == null) return;
            if (_currentStruct.Mcids.Count > 0) _structElems.Add(_currentStruct);
            _currentStruct = null;
        }

        /// <summary>
        /// Emits one drawn line, wrapped in marked content under the current structure element
        /// when tagging. The wrap stays within a single page (it is called per laid-out line).
        /// </summary>
        internal void EmitTaggedLine(Action emit)
        {
            if (!_options.TaggedPdf || _currentStruct == null || _currentPage == null) { emit(); return; }
            // In body flow the current page is the last one, so that is asked first: a search of the
            // list once a drawn line would make a long document's tagging quadratic in its
            // pages. A header or footer band draws on an earlier page, but carries no
            // structure and has returned above.
            int last = _pageBuilders.Count - 1;
            int pageIdx = last >= 0 && ReferenceEquals(_pageBuilders[last], _currentPage)
                ? last
                : _pageBuilders.IndexOf(_currentPage);
            if (pageIdx < 0) { emit(); return; }

            int mcid = _mcidByPage.TryGetValue(pageIdx, out int c) ? c : 0;
            _mcidByPage[pageIdx] = mcid + 1;
            AppendContent($"/{_currentStruct.Role} <</MCID {mcid}>> {PdfOperators.BeginMarkedContentProps}\n");
            emit();
            AppendContent(PdfOperators.EndMarkedContent + "\n");

            if (_currentStruct.FirstPage < 0) _currentStruct.FirstPage = pageIdx;
            _currentStruct.Mcids.Add((pageIdx, mcid));
        }

        // PAGE / NUMPAGES field support. PAGE is computed at render time (the page the
        // field lands on); NUMPAGES is a placeholder replaced once all pages exist. The token
        // itself is Builder's (PdfExporter.NumPagesPlaceholder) so both PDF paths agree on it.
        private int? _pageNumberOverride; // set while rendering a header/footer band onto page N
        internal int CurrentPageNumber => _pageNumberOverride ?? DisplayedPageNumber(_pageBuilders.Count - 1);

        // What a page's number reads minus its place in the document, from the last section whose
        // w:pgNumType sets @w:start (a section without one continues the count, as Word does).
        private int _pageNumberOffset;

        /// <summary>The number page <paramref name="pageIndex"/> (0-based in the document) shows.</summary>
        private int DisplayedPageNumber(int pageIndex) => pageIndex + 1 + _pageNumberOffset;

        // Page-number display format for the current section (w:pgNumType@fmt).
        private string _pageNumberFormat = "decimal";

        /// <summary>
        /// A section's <c>w:pgNumType/@w:fmt</c> as <c>Core.Util.OrdinalNumbering.FormatOoxmlNumber</c>'s
        /// vocabulary — this renderer's and the Builder path's (<c>DocxToBuilderMapper</c>) alike.
        /// </summary>
        internal static string MapPageNumberFormat(EnumValue<NumberFormatValues> fmt)
        {
            if (fmt == null) return "decimal";
            var v = fmt.Value;
            if (v == NumberFormatValues.UpperRoman) return "upperRoman";
            if (v == NumberFormatValues.LowerRoman) return "lowerRoman";
            if (v == NumberFormatValues.UpperLetter) return "upperLetter";
            if (v == NumberFormatValues.LowerLetter) return "lowerLetter";
            if (v == NumberFormatValues.Hebrew1) return "hebrew1";
            if (v == NumberFormatValues.Hebrew2) return "hebrew2";
            return "decimal";
        }

        /// <summary>Formats a page number per the current section's w:pgNumType format
        /// (decimal / upperRoman / lowerRoman / upperLetter / lowerLetter / hebrew1 / hebrew2).</summary>
        /// <remarks>
        /// The vocabulary and both alphabets are <c>Core.Util.OrdinalNumbering.FormatOoxmlNumber</c>,
        /// which the Builder's PDF exporter reaches for the same five strings. This renderer keyed
        /// them itself, through two private wrappers that upper- and lower-cased afterwards; the
        /// results were the same and the agreement was asserted by nothing.
        /// </remarks>
        internal string FormatPageNumber(int n)
            => Core.Util.OrdinalNumbering.FormatOoxmlNumber(n, _pageNumberFormat);

        /// <summary>
        /// A horizontal shift the content being drawn is under (a table row drawn in another column than the one it
        /// was laid out in, through <c>cm</c>), applied to the links it registers in page space too.
        /// </summary>
        internal double LinkShiftX { get; set; }

        /// <summary>
        /// Registers a clickable link rectangle (PDF user space) on the current page. A target whose scheme a
        /// document link does not use (<see cref="Core.Writing.LinkTargets"/>: a hyperlink relationship to
        /// <c>javascript:</c>, however spaced) gets no link — its text stays — and is reported once per conversion.
        /// </summary>
        internal void RegisterLink(double x0, double y0, double x1, double y1, string uri)
        {
            if (uri != null && (uri.StartsWith("#") || Core.Writing.LinkTargets.IsSafe(uri)))
            {
                _currentPage.AddLink(x0 + LinkShiftX, y0, x1 + LinkShiftX, y1, uri);
                return;
            }
            if (_unsafeLinkReported) return;
            _unsafeLinkReported = true;
            string shown = uri == null ? "" : uri.Length <= 40 ? uri : uri.Substring(0, 40) + "…";
            DocxDiagnostics.Report("DocxRenderContext.RegisterLink",
                $"a hyperlink to '{shown}' was not written: its scheme is not one a document link uses "
                + "(http, https, mailto, ftp, ftps, tel). Reported once per conversion.");
        }

        /// <summary>
        /// A link over a run of text <paramref name="width"/> wide drawn from <paramref name="x"/> on
        /// <paramref name="baseline"/>: from a quarter of the size below the baseline to nine tenths above
        /// it — a body line's and a table cell's alike.
        /// </summary>
        internal void RegisterTextLink(double x, double baseline, double width, double fontSize, string uri)
            => RegisterLink(x, baseline - fontSize * 0.25, x + width, baseline + fontSize * 0.9, uri);

        private bool _unsafeLinkReported;

        // Internal-navigation collection: bookmark name → (page index, y) for /Dests, and
        // heading paragraphs → the /Outlines (PDF bookmark pane) tree.
        private readonly List<(string name, int pageIndex, double y)> _bookmarks = new List<(string, int, double)>();
        private readonly List<(int level, string text, int pageIndex, double y)> _headings = new List<(int, string, int, double)>();

        /// <summary>Records a bookmark target at the current render position (for /Dests).</summary>
        internal void RegisterBookmark(string name)
        {
            if (!string.IsNullOrEmpty(name)) _bookmarks.Add((name, _pageBuilders.Count - 1, CursorY));
        }

        /// <summary>Records a heading paragraph at the current render position (for /Outlines).</summary>
        internal void RegisterHeading(int level, string text)
        {
            if (level > 0 && !string.IsNullOrEmpty(text))
                _headings.Add((level, text, _pageBuilders.Count - 1, CursorY));
        }

        // Footnotes are laid into a reserved band at the bottom of the page they are
        // referenced on; endnotes are flowed at the end of the document. While footnotes
        // are pending the effective page bottom rises by _footnoteReserve so body text does
        // not overlap them. All of this stays inert (reserve 0) for documents with no notes.
        private readonly List<(int number, string text)> _pendingFootnotes = new List<(int, string)>();
        private readonly List<(int number, string text)> _endnotes = new List<(int, string)>();
        private double _footnoteReserve;
        private int _footnoteCounter, _endnoteCounter;
        private readonly ParagraphRenderer _footnoteRenderer = new ParagraphRenderer();

        /// <summary>Length of the rule Word draws above the footnote band: 2&#160;inches, or the
        /// content width when the page is narrower than that.</summary>
        private static readonly double FootnoteSeparatorMaxWidthPt = PdfUnits.InchesToPoints(2.0);

        /// <summary>Floor on the width used to estimate a footnote's line count, so a degenerate
        /// (zero or negative) content width cannot make the estimate diverge.</summary>
        private const double MinFootnoteMeasureWidthPt = 50.0;

        /// <summary>Slack added to each footnote's reserved height so consecutive notes do not
        /// touch.</summary>
        private const double FootnoteBottomPaddingPt = 2.0;

        /// <summary>Gap between the top of the reserved band and the separator rule.</summary>
        private const double FootnoteSeparatorGapPt = 2.0;

        /// <summary>Gap between the separator rule and the first line of footnote text.</summary>
        private const double FootnoteTextGapPt = 4.0;

        private double EffectiveBottom => _marginBottom + _footnoteReserve;

        private readonly Dictionary<(long Id, bool IsEndnote), int> _noteNumbers = new Dictionary<(long, bool), int>();

        /// <summary>
        /// The display number of the note a reference names, queued the first time it is named.
        /// </summary>
        /// <remarks>
        /// A note id is one note — Word writes a note of its own for each reference — so a second
        /// reference to the same id shows that note's number and queues nothing, so a large note referenced
        /// many times is laid out, and reserves page space, once. <paramref name="text"/> is
        /// asked only the first time.
        /// </remarks>
        internal int NoteNumber(long id, bool isEndnote, Func<string> text)
        {
            if (_noteNumbers.TryGetValue((id, isEndnote), out int number)) return number;
            number = AddFootnote(text(), isEndnote);
            _noteNumbers[(id, isEndnote)] = number;
            return number;
        }

        /// <summary>Queues a footnote/endnote body and returns its display number. Footnotes
        /// reserve bottom-of-page space; endnotes are deferred to the document end.</summary>
        internal int AddFootnote(string text, bool isEndnote)
        {
            text ??= "";
            if (isEndnote)
            {
                _endnoteCounter++;
                _endnotes.Add((_endnoteCounter, text));
                return _endnoteCounter;
            }
            _footnoteCounter++;
            double fs = _options.DefaultFontSize * DocxLayoutMetrics.NoteFontScale;
            double tw = _styleResolver.MeasureTextWidth(text, StandardFonts.Helvetica, fs);
            double cw = Math.Max(MinFootnoteMeasureWidthPt, ContentWidth);
            int lines = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(tw / cw)));
            if (_pendingFootnotes.Count == 0)
                _footnoteReserve += fs * DocxLayoutMetrics.FootnoteSeparatorFactor; // separator gap
            _footnoteReserve += lines * fs * DocxLayoutMetrics.SingleLineFactor + FootnoteBottomPaddingPt;
            _pendingFootnotes.Add((_footnoteCounter, text));
            return _footnoteCounter;
        }

        // Draws the footnotes pending for the current page into the reserved bottom band.
        private void DrawPendingFootnotes()
        {
            if (_pendingFootnotes.Count == 0 || _currentPage == null) return;
            var notes = new List<(int number, string text)>(_pendingFootnotes);
            double bandTop = _marginBottom + _footnoteReserve;
            _pendingFootnotes.Clear();
            _footnoteReserve = 0;

            double sepY = bandTop - FootnoteSeparatorGapPt;
            _currentPage.AppendOperator(string.Format(CultureInfo.InvariantCulture,
                "q\n0.5 w\n0 0 0 RG\n{0:0.##} {1:0.##} m {2:0.##} {1:0.##} l S\nQ\n",
                _marginLeft, sepY, _marginLeft + Math.Min(ContentWidth, FootnoteSeparatorMaxWidthPt)));

            double savedCursor = CursorY, savedBottom = _marginBottom;
            var savedStruct = _currentStruct; bool savedSuppress = _suppressStruct;
            _currentStruct = null; _suppressStruct = true; // footnote text isn't body structure
            CursorY = sepY - FootnoteTextGapPt;
            _marginBottom = NoPageBottomSentinel;
            bool savedInBand = _inBand;
            _inBand = true;
            try
            {
                double fs = _options.DefaultFontSize * DocxLayoutMetrics.NoteFontScale;
                foreach (var (number, text) in notes)
                {
                    var p = new Paragraph(new Run(
                        new RunProperties(new FontSize { Val = (SaturatingCast.ToInt(fs * PdfUnits.HalfPointsPerPoint)).ToString() }),
                        new Text(FormatNoteNumber(number, isEndnote: false) + ". " + text) { Space = SpaceProcessingModeValues.Preserve }));
                    _footnoteRenderer.Render(p, this);
                }
            }
            catch (BandOverflow)
            {
                ReportBandOverflow("footnote band");
            }
            finally
            {
                _inBand = savedInBand;
                CursorY = savedCursor; _marginBottom = savedBottom;
                _currentStruct = savedStruct; _suppressStruct = savedSuppress;
            }
        }

        // Flushes the final page's footnotes and flows any endnotes at the document end.
        private void FinishNotes(ParagraphRenderer paragraphRenderer)
        {
            if (_endnotes.Count > 0)
            {
                var savedStruct = _currentStruct; bool savedSuppress = _suppressStruct;
                _currentStruct = null; _suppressStruct = true;
                AdvanceCursor(_options.DefaultFontSize);
                foreach (var (number, text) in _endnotes)
                {
                    var p = new Paragraph(new Run(
                        new Text(FormatNoteNumber(number, isEndnote: true) + ". " + text) { Space = SpaceProcessingModeValues.Preserve }));
                    paragraphRenderer.Render(p, this);
                }
                _currentStruct = savedStruct; _suppressStruct = savedSuppress;
            }
            DrawPendingFootnotes();
        }

        internal DocxRenderContext(WordprocessingDocument wordDoc, DocxToPdfOptions options)
        {
            _wordDoc = wordDoc;
            _options = options;
            _styleResolver = new StyleResolver(wordDoc);

            _marginTop    = options.MarginTop;
            _marginBottom = options.MarginBottom;
            _sectionMarginBottom = options.MarginBottom;
            _marginLeft   = options.MarginLeft;
            _marginRight  = options.MarginRight;
            _pageWidth    = options.PageWidth;
            _pageHeight   = options.PageHeight;

            // Default single full-width column until a section sets w:cols.
            _columns.Add((_marginLeft, _pageWidth - _marginLeft - _marginRight));

            // Page size / margins and the first page are established per section in
            // RenderToPdf (a document may have several sections with different sizes).
        }

        private void ApplySectionProperties(SectionProperties sectionProps)
        {
            if (sectionProps == null) return;

            // UseDocumentMargins gates the MARGINS and nothing else. Returning here would also skip
            // the w:pgSz block below: a caller who set it false to force uniform margins would
            // silently lose the section's page size and orientation too, so an A4
            // landscape document came out as portrait US Letter with the right-hand third of every
            // line off the MediaBox. The option's own documentation says "document margins".
            // Page size and margins in twips — written to this context's fields, NOT to the caller's options object;
            // together, so margins that would leave no room on the page give way (SectionPageSize.Resolve).
            SectionPageSize.Resolve(sectionProps.Elements<PageSize>().FirstOrDefault(),
                sectionProps.Elements<PageMargin>().FirstOrDefault(), _options.UseDocumentMargins,
                ref _pageWidth, ref _pageHeight, ref _marginTop, ref _marginBottom, ref _marginLeft, ref _marginRight);
            _sectionMarginBottom = _marginBottom;
        }

        /// <summary>
        /// The section's bottom margin, kept apart from <see cref="MarginBottom"/>, which a band or the
        /// footnote band sets to a sentinel far below the page while it is drawn: a drawing anchored to
        /// the bottom margin inside a footer is placed against this one.
        /// </summary>
        internal double SectionMarginBottom => _sectionMarginBottom;
        private double _sectionMarginBottom;

        // ------------------------------------------------------------------
        //  Footnote and endnote numbering (w:footnotePr / w:endnotePr, §17.11.11–12)
        // ------------------------------------------------------------------

        private string _footnoteFormat = "decimal", _endnoteFormat = "decimal";
        private int _footnoteStart = 1, _endnoteStart = 1;
        private RestartNumberValues _footnoteRestart = RestartNumberValues.Continuous;
        private RestartNumberValues _endnoteRestart = RestartNumberValues.Continuous;

        /// <summary>
        /// The notes' number format, first number and restart rule: the document's settings when
        /// <paramref name="sectPr"/> is null, else the section's own over them, each attribute on its
        /// own; a section restarts its counters when its rule says so (the page rule at each page).
        /// Numbers absent are decimal from 1, continuous.
        /// </summary>
        private void ReadNoteNumbering(SectionProperties sectPr)
        {
            if (sectPr == null)
            {
                var settings = _wordDoc.MainDocumentPart?.DocumentSettingsPart?.Settings;
                Read(settings?.GetFirstChild<FootnoteDocumentWideProperties>(), ref _footnoteFormat, ref _footnoteStart, ref _footnoteRestart);
                Read(settings?.GetFirstChild<EndnoteDocumentWideProperties>(), ref _endnoteFormat, ref _endnoteStart, ref _endnoteRestart);
                _footnoteCounter = _footnoteStart - 1;
                _endnoteCounter = _endnoteStart - 1;
                return;
            }
            Read(sectPr.GetFirstChild<FootnoteProperties>(), ref _footnoteFormat, ref _footnoteStart, ref _footnoteRestart);
            Read(sectPr.GetFirstChild<EndnoteProperties>(), ref _endnoteFormat, ref _endnoteStart, ref _endnoteRestart);
            if (_footnoteRestart != RestartNumberValues.Continuous) _footnoteCounter = _footnoteStart - 1;
            if (_endnoteRestart != RestartNumberValues.Continuous) _endnoteCounter = _endnoteStart - 1;

            static void Read(OpenXmlElement pr, ref string format, ref int start, ref RestartNumberValues restart)
            {
                if (pr == null) return;
                var fmt = pr.GetFirstChild<NumberingFormat>()?.Val;
                if (fmt != null) format = MapPageNumberFormat(fmt);
                if (pr.GetFirstChild<NumberingStart>()?.Val?.Value is ushort s) start = s;
                if (pr.GetFirstChild<NumberingRestart>()?.Val?.Value is RestartNumberValues r) restart = r;
            }
        }

        /// <summary>A note's number as its mark and its body show it, in the notes' format.</summary>
        internal string FormatNoteNumber(int number, bool isEndnote)
            => Core.Util.OrdinalNumbering.FormatOoxmlNumber(number, isEndnote ? _endnoteFormat : _footnoteFormat);

        // Resolves the section's column layout (w:cols) into per-column (left, width)
        // geometry. Equal-width columns split the content box minus the inter-column gaps;
        // unequal columns take explicit w:col widths. Defaults to a single full-width column.
        private void ApplyColumns(SectionProperties sectPr)
        {
            _columns.Clear();
            _columnSeparator = false;
            double left = _marginLeft;
            double full = _pageWidth - _marginLeft - _marginRight;

            var cols = sectPr?.GetFirstChild<Columns>();
            int num = cols?.ColumnCount?.Value ?? 1;
            var explicitCols = cols?.Elements<Column>().ToList();
            bool equal = cols?.EqualWidth?.Value ?? true;
            _columnSeparator = cols?.Separator?.Value ?? false;

            if (cols == null || (num <= 1 && (explicitCols == null || explicitCols.Count <= 1)))
            {
                _columns.Add((left, full));
            }
            else if (!equal && explicitCols != null && explicitCols.Count >= 1)
            {
                double x = left;
                foreach (var c in explicitCols)
                {
                    double w = TwipToPt(c.Width?.Value, full / explicitCols.Count);
                    _columns.Add((x, w));
                    x += w + TwipToPt(c.Space?.Value, 0);
                }
            }
            else
            {
                double gap = TwipToPt(cols?.Space?.Value, HalfInchPt); // w:space default 720 twips = 0.5"
                if (num < 1) num = 1;
                double w = (full - (num - 1) * gap) / num;
                if (w < 1) { _columns.Add((left, full)); _currentColumn = 0; return; }
                for (int i = 0; i < num; i++) _columns.Add((left + i * (w + gap), w));
            }
            if (_columns.Count == 0) _columns.Add((left, full));
            _currentColumn = 0;
        }

        // Word's default inter-column gap (w:cols@w:space) and header/footer distance from the
        // page edge are both 720 twips = half an inch.
        private static readonly double HalfInchPt = PdfUnits.InchesToPoints(0.5);

        private static double TwipToPt(string twip, double fallback)
        {
            if (!string.IsNullOrEmpty(twip)
                && double.TryParse(twip, NumberStyles.Integer, CultureInfo.InvariantCulture, out double t) && t > 0)
                return PdfUnits.TwipsToPoints(t);
            return fallback;
        }

        // Strokes a thin vertical rule down the gap between adjacent columns on every page
        // of a multi-column section that requested w:sep.
        private void RenderColumnSeparators(int firstPage, int lastPage)
        {
            if (!_columnSeparator || _columns.Count < 2) return;
            double top = _pageHeight - _marginTop;
            double bottom = _marginBottom;
            for (int p = firstPage; p <= lastPage && p < _pageBuilders.Count; p++)
            {
                var page = _pageBuilders[p];
                for (int i = 0; i < _columns.Count - 1; i++)
                {
                    double x = (_columns[i].left + _columns[i].width + _columns[i + 1].left) / 2.0;
                    page.AppendOperator(string.Format(CultureInfo.InvariantCulture,
                        "q\n0.5 w\n0 0 0 RG\n{0:0.##} {1:0.##} m {0:0.##} {2:0.##} l S\nQ\n", x, top, bottom));
                }
            }
        }

        internal void RenderToPdf(Stream output)
        {
            // Reset before anything can report: a diagnostic slot a caller reads *after* an
            // operation must be cleared at the *start* of that operation, on every path, or a
            // previous document's message is mistaken for this one's.
            DocxDiagnostics.Clear();

            // Every mc:AlternateContent replaced by its choice (MarkupCompatibility): Word writes
            // each wps shape and text box inside one, and the run held nothing this renderer could
            // draw. After the clear, so a fallback's report reaches the caller.
            MarkupCompatibility.ResolveAlternateContent(_wordDoc);

            var body = _wordDoc.MainDocumentPart?.Document?.Body;
            if (body == null)
                throw new DocxToPdfException("DOCX document has no body element.");

            var paragraphRenderer = new ParagraphRenderer();
            var tableRenderer = new TableRenderer();

            // A document is split into sections by paragraphs carrying a sectPr (each
            // ends a section); the body-level sectPr is the final section. Each section
            // may set its own page size / margins and starts on a fresh page.
            // Until the document ends or the page limit is reached (DocxToPdfOptions.MaxPages): the section
            // the limit interrupts still gets its headers, footers and borders on the pages it has.
            bool limited = false;
            var sections = SplitSections(body);
            _bands = new HeaderFooterReferences(_wordDoc.MainDocumentPart);
            ReadNoteNumbering(null);
            for (int s = 0; s < sections.Count; s++)
            {
                var (content, sectPr) = sections[s];
                ApplySectionProperties(sectPr);
                ApplyColumns(sectPr);
                _bands.EnterSection(sectPr);
                ReadNoteNumbering(sectPr);
                var pageNumbers = sectPr?.GetFirstChild<PageNumberType>();
                _pageNumberFormat = MapPageNumberFormat(pageNumbers?.Format);
                int firstPage = _pageBuilders.Count;
                if (pageNumbers?.Start?.Value is int start) _pageNumberOffset = start - (firstPage + 1);
                try
                {
                    NewPage();
                    foreach (var element in content)
                        RenderBlock(element, paragraphRenderer, tableRenderer);
                    // The endnotes flow at the document's end, inside the last section: the pages
                    // they add are its, and get its headers, footers, borders and column rules.
                    if (s == sections.Count - 1) FinishNotes(paragraphRenderer);
                }
                catch (PageLimitReached) { limited = true; }
                if (_pageBuilders.Count > firstPage)
                {
                    RenderColumnSeparators(firstPage, _pageBuilders.Count - 1);
                    RenderHeadersFooters(sectPr, firstPage, _pageBuilders.Count - 1,
                                         paragraphRenderer, tableRenderer);
                    RenderPageBorders(sectPr, firstPage, _pageBuilders.Count - 1);
                }
                if (limited) break;
            }

            if (limited)
                DocxDiagnostics.Report("DocxRenderContext.MaxPages",
                    $"the document reached the {_options.MaxPages}-page limit (DocxToPdfOptions.MaxPages); the rest of it was not laid out.");

            AssemblePdf(output);
        }

        // Renders a body-level block, unwrapping block content controls and custom XML (MarkupWrappers).
        private void RenderBlock(OpenXmlElement element, ParagraphRenderer paragraphRenderer, TableRenderer tableRenderer)
        {
            foreach (var block in MarkupWrappers.Blocks(new[] { element }))
            {
                if (block is Paragraph para) paragraphRenderer.Render(para, this);
                else if (block is Table table) tableRenderer.Render(table, this);
            }
        }

        private void RenderHeadersFooters(SectionProperties sectPr, int firstPage, int lastPage,
            ParagraphRenderer paragraphRenderer, TableRenderer tableRenderer)
        {
            if (sectPr == null) return;

            // Header/footer distance from the page edge (twips → points), default 0.5".
            var pm = sectPr.Elements<PageMargin>().FirstOrDefault();
            double headerDist = pm?.Header?.Value != null
                ? PdfUnits.TwipsToPoints(pm.Header.Value) : HalfInchPt;
            double footerDist = pm?.Footer?.Value != null
                ? PdfUnits.TwipsToPoints(pm.Footer.Value) : HalfInchPt;

            bool titlePg = sectPr.GetFirstChild<TitlePage>() != null;
            bool evenOdd = _wordDoc.MainDocumentPart?.DocumentSettingsPart?.Settings
                ?.GetFirstChild<EvenAndOddHeaders>() != null;

            for (int p = firstPage; p <= lastPage && p < _pageBuilders.Count; p++)
            {
                int pageNum = DisplayedPageNumber(p);
                // Pick the header/footer variant for this page: first-page (titlePg),
                // even (evenAndOddHeaders) by the number the page shows, as Word does, else default.
                HeaderFooterValues want =
                    titlePg && p == firstPage ? HeaderFooterValues.First :
                    evenOdd && pageNum % 2 == 0 ? HeaderFooterValues.Even :
                    HeaderFooterValues.Default;

                var header = _bands.Header(want);
                var footer = _bands.Footer(want);
                if (header != null)
                    RenderBand(_pageBuilders[p], pageNum, header, _pageHeight - headerDist, paragraphRenderer, tableRenderer);
                if (footer != null)
                    RenderFooterBand(_pageBuilders[p], pageNum, footer, footerDist, paragraphRenderer, tableRenderer);
            }
        }

        /// <summary>The section's header and footer references, inherited across sections, by type.</summary>
        private HeaderFooterReferences _bands;

        private bool _footerOverlapReported;

        /// <summary>
        /// A footer grows upward from the footer distance: its last line's baseline stands at the
        /// distance, where a one-line footer's does, and the lines before it rise above. The band is
        /// laid out downward from one default line above the distance, as every block is, and then
        /// moved up by the rest of what it took — its content under one translation, the links and
        /// bookmarks it registered with it — so a footer of several lines does not run below the page.
        /// </summary>
        private void RenderFooterBand(PdfPageBuilder page, int pageNumber, OpenXmlElement part, double footerDist,
            ParagraphRenderer paragraphRenderer, TableRenderer tableRenderer)
        {
            double defaultLine = _options.DefaultFontSize * DocxLayoutMetrics.SingleLineFactor;
            double startY = footerDist + defaultLine;
            int contentAt = page.ContentLength, linksAt = page.LinkCount;
            int bookmarksAt = _bookmarks.Count, headingsAt = _headings.Count;

            double consumed = RenderBand(page, pageNumber, part, startY, paragraphRenderer, tableRenderer);

            // The band's last baseline is at startY - consumed; it belongs at the distance.
            double dy = consumed - defaultLine;
            if (dy <= 0) return;
            page.TranslateContentFrom(contentAt, 0, dy);
            page.ShiftLinksFrom(linksAt, dy);
            for (int i = bookmarksAt; i < _bookmarks.Count; i++)
                _bookmarks[i] = (_bookmarks[i].name, _bookmarks[i].pageIndex, _bookmarks[i].y + dy);
            for (int i = headingsAt; i < _headings.Count; i++)
                _headings[i] = (_headings[i].level, _headings[i].text, _headings[i].pageIndex, _headings[i].y + dy);

            if (startY + dy > _sectionMarginBottom && !_footerOverlapReported)
            {
                _footerOverlapReported = true;
                DocxDiagnostics.Report("DocxRenderContext.Footer",
                    "a footer is taller than the room between the footer distance and the bottom margin, " +
                    "so it stands over the body's last lines; Word moves the margin up for it. Reported once per conversion.");
            }
        }

        // Draws the section's page border (w:pgBorders) around each of its pages. Each
        // side may carry its own width / colour / style, so the four edges are stroked
        // independently. offsetFrom="page" measures the inset from the page edge;
        // offsetFrom="text" measures it outward from the text margin box.
        private void RenderPageBorders(SectionProperties sectPr, int firstPage, int lastPage)
        {
            var pb = sectPr?.GetFirstChild<PageBorders>();
            if (pb == null) return;
            bool fromPage = pb.OffsetFrom?.Value == PageBorderOffsetValues.Page;
            double W = _pageWidth, H = _pageHeight;

            // space is in points for pgBorders; Word's page-offset default is 24pt.
            double Sp(BorderType b) => b?.Space?.Value ?? (fromPage ? DocxLayoutMetrics.PageBorderPageOffsetPt : 0.0);
            double leftX = fromPage ? Sp(pb.LeftBorder) : _marginLeft - Sp(pb.LeftBorder);
            double rightX = fromPage ? W - Sp(pb.RightBorder) : W - _marginRight + Sp(pb.RightBorder);
            double topY = fromPage ? H - Sp(pb.TopBorder) : H - _marginTop + Sp(pb.TopBorder);
            double bottomY = fromPage ? Sp(pb.BottomBorder) : _marginBottom - Sp(pb.BottomBorder);

            for (int p = firstPage; p <= lastPage && p < _pageBuilders.Count; p++)
            {
                var page = _pageBuilders[p];
                StrokeBorderSide(page, pb.TopBorder, leftX, topY, rightX, topY);
                StrokeBorderSide(page, pb.BottomBorder, leftX, bottomY, rightX, bottomY);
                StrokeBorderSide(page, pb.LeftBorder, leftX, bottomY, leftX, topY);
                StrokeBorderSide(page, pb.RightBorder, rightX, bottomY, rightX, topY);
            }
        }

        private static void StrokeBorderSide(PdfPageBuilder page, BorderType b,
            double x0, double y0, double x1, double y1)
        {
            if (b == null) return;
            var val = b.Val?.Value;
            if (val == BorderValues.None || val == BorderValues.Nil) return;

            // Size is in eighths of a point; default 4 (=0.5pt) when absent.
            double width = (b.Size?.Value ?? 4) / PdfUnits.BorderEighthsPerPoint;
            // "auto", a theme name, a short or non-hex value → black (the OOXML automatic colour).
            double r = 0, g = 0, bl = 0;
            if (RunRenderer.TryParseHexColor(b.Color?.Value, out int hr, out int hg, out int hb))
            {
                r = hr / 255.0; g = hg / 255.0; bl = hb / 255.0;
            }
            page.AppendOperator("q\n");
            page.AppendOperator($"{r:0.###} {g:0.###} {bl:0.###} RG\n");
            page.AppendOperator($"{width:0.###} w\n");
            page.AppendOperator($"{x0:0.###} {y0:0.###} m {x1:0.###} {y1:0.###} l S\n");
            page.AppendOperator("Q\n");
        }

        // Renders a header/footer part onto a specific page at a fixed Y, with page
        // breaks suppressed (the band must stay on its page). Returns the height the band took:
        // its start less the cursor where its last block left it.
        private double RenderBand(PdfPageBuilder page, int pageNumber, OpenXmlElement part, double startY,
            ParagraphRenderer paragraphRenderer, TableRenderer tableRenderer)
        {
            var savedPage = _currentPage;
            double savedCursor = CursorY, savedMarginBottom = _marginBottom;
            // Headers/footers span the full text box, not the body columns.
            var savedColumns = new List<(double, double)>(_columns);
            int savedColumn = _currentColumn;
            _columns.Clear();
            _columns.Add((_marginLeft, FullContentWidth));
            _currentColumn = 0;
            _currentPage = page;
            CursorY = startY;
            _marginBottom = NoPageBottomSentinel; // suppress NewPage during the band
            _pageNumberOverride = pageNumber;
            var savedStruct = _currentStruct; bool savedSuppress = _suppressStruct;
            _currentStruct = null; _suppressStruct = true; // header/footer text isn't body structure
            bool savedInBand = _inBand;
            _inBand = true;
            try
            {
                foreach (var el in part.ChildElements) RenderBlock(el, paragraphRenderer, tableRenderer);
                return startY - CursorY;
            }
            catch (BandOverflow)
            {
                ReportBandOverflow("header or footer");
                return startY - CursorY;
            }
            finally
            {
                _inBand = savedInBand;
                _currentPage = savedPage;
                CursorY = savedCursor;
                _marginBottom = savedMarginBottom;
                _pageNumberOverride = null;
                _columns.Clear();
                _columns.AddRange(savedColumns);
                _currentColumn = savedColumn;
                _currentStruct = savedStruct; _suppressStruct = savedSuppress;
            }
        }

        private static List<(List<OpenXmlElement> content, SectionProperties sectPr)> SplitSections(Body body)
        {
            var sections = new List<(List<OpenXmlElement>, SectionProperties)>();
            var current = new List<OpenXmlElement>();
            foreach (var el in body.ChildElements)
            {
                if (el is SectionProperties bodySect) // body-level = last section
                {
                    sections.Add((current, bodySect));
                    current = new List<OpenXmlElement>();
                    continue;
                }
                current.Add(el);
                if (el is Paragraph p &&
                    p.ParagraphProperties?.GetFirstChild<SectionProperties>() is SectionProperties ps)
                {
                    sections.Add((current, ps));
                    current = new List<OpenXmlElement>();
                }
            }
            if (current.Count > 0 || sections.Count == 0)
                sections.Add((current, null));
            return sections;
        }

        private void AssemblePdf(Stream output)
        {
            int nextObjectNumber = 1;

            // Reserve catalog and pages tree object numbers
            int catalogObjNum = nextObjectNumber++;
            int pagesTreeObjNum = nextObjectNumber++;

            // Resolve NUMPAGES now that the page count is final.
            foreach (var pb in _pageBuilders)
                pb.ReplaceInContent(Tetitulz.Pdf.Builder.Export.PdfExporter.NumPagesPlaceholder,
                                    _pageBuilders.Count.ToString(CultureInfo.InvariantCulture));

            // Build all page objects
            var allObjects = new List<(int objNum, PdfObject obj)>();
            var pageObjNums = new List<int>();

            // Embedded composite fonts are written ONCE (document-level) and every
            // page references them by object number — no per-page font duplication.
            var embeddedFontObjNums = new Dictionary<string, int>();
            foreach (var ef in _embeddedFonts)
            {
                var (type0ObjNum, fontObjects) = Tetitulz.Pdf.Core.Writing.Type0FontBuilder.BuildObjects(
                    ref nextObjectNumber, ef.Program, ef.UsedGidToUnicode, ef.UsedGidToText);
                allObjects.AddRange(fontObjects);
                embeddedFontObjNums[ef.ResName] = type0ObjNum;
            }

            // Tagged PDF: which page indices carry marked content (need a /StructParents key).
            var pagesWithTags = new HashSet<int>();
            if (_options.TaggedPdf)
                foreach (var se in _structElems)
                    foreach (var (p, _) in se.Mcids) pagesWithTags.Add(p);

            int pageIndex = 0;
            // A picture on several pages (a header's logo, one image part named n times) is written once.
            var images = new Tetitulz.Pdf.Core.Writing.ImageObjectShare();
            foreach (var pageBuilder in _pageBuilders)
            {
                int structParentsKey = pagesWithTags.Contains(pageIndex) ? pageIndex : -1;
                var pageObjects = pageBuilder.BuildObjects(ref nextObjectNumber, pagesTreeObjNum,
                    embeddedFontObjNums, structParentsKey, images);
                allObjects.AddRange(pageObjects);

                // The last object in each page's list is the page dictionary
                var lastObj = pageObjects[pageObjects.Count - 1];
                pageObjNums.Add(lastObj.objNum);
                pageIndex++;
            }

            // Pages tree — one implementation, in Core.Writing.PdfPageTreeWriter.
            var pagesDict = PdfPageTreeWriter.BuildRoot(pageObjNums, pagesTreeObjNum);
            allObjects.Add((pagesTreeObjNum, pagesDict));

            // Named destinations (/Dests) for internal links, and the document outline
            // (/Outlines) from heading paragraphs.
            int destsObjNum = BuildDests(allObjects, ref nextObjectNumber, pageObjNums);
            int outlinesObjNum = BuildOutlines(allObjects, ref nextObjectNumber, pageObjNums);
            int structRootObjNum = BuildStructTree(allObjects, ref nextObjectNumber, pageObjNums);

            // Catalog
            var catalogDict = new PdfDictionary();
            catalogDict.Set(PdfName.Type, PdfName.Catalog);
            catalogDict.Set(PdfName.Pages, new PdfReference(pagesTreeObjNum, 0));
            if (destsObjNum > 0) catalogDict.Set(PdfName.Dests, new PdfReference(destsObjNum, 0));
            if (outlinesObjNum > 0) catalogDict.Set(PdfName.Outlines, new PdfReference(outlinesObjNum, 0));
            if (structRootObjNum > 0)
            {
                // Tagged PDF: mark the document and point at the structure tree.
                catalogDict.Set(PdfName.StructTreeRoot, new PdfReference(structRootObjNum, 0));
                var markInfo = new PdfDictionary();
                markInfo.Set(PdfName.Marked, PdfBoolean.True);
                catalogDict.Set(PdfName.MarkInfo, markInfo);
            }
            catalogDict.ObjectNumber = catalogObjNum;
            catalogDict.GenerationNumber = 0;
            allObjects.Add((catalogObjNum, catalogDict));

            // Document information dictionary (/Info) from the DOCX core properties.
            int infoObjNum = nextObjectNumber++;
            var info = new PdfDictionary();
            var props = _wordDoc.PackageProperties;
            if (!string.IsNullOrEmpty(props?.Title)) info.Set(PdfName.Title, new PdfString(props.Title));
            if (!string.IsNullOrEmpty(props?.Creator)) info.Set(PdfName.Author, new PdfString(props.Creator));
            if (!string.IsNullOrEmpty(props?.Subject)) info.Set(PdfName.Subject, new PdfString(props.Subject));
            info.Set(PdfName.Producer, new PdfString(PdfProducer.Value));
            info.ObjectNumber = infoObjNum;
            info.GenerationNumber = 0;
            allObjects.Add((infoObjNum, info));

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Size, new PdfNumber(nextObjectNumber));
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
            trailer.Set(PdfName.Info, new PdfReference(infoObjNum, 0));

            // The header is written once every object exists, so it names what they use: the version
            // asked for (DocxToPdfOptions.Version, else 1.4) raised to what any object or content stream needs.
            var floor = new PdfFeatureVersions.Floor();
            foreach (var (_, obj) in allObjects) PdfFeatureVersions.Scan(obj, floor);
            PdfFeatureVersions.Scan(trailer, floor);
            foreach (var pageBuilder in _pageBuilders) pageBuilder.RequireContentFeatures(floor);
            PdfVersion asked = _options.Version ?? PdfVersion.Pdf14;

            var writer = new PdfWriter(output);
            writer.WriteHeader(floor.HeaderFor(asked));
            string raised = PdfFeatureVersions.RaiseReport(_options.Version, floor.HeaderFor(asked), floor.RaisedBecause(asked));
            if (raised != null) DocxDiagnostics.Report(DocxToPdfConverter.VersionOperation, raised);

            var xrefEntries = new List<(int objNum, long offset)> { (0, 0) };
            foreach (var (objNum, obj) in allObjects)
                xrefEntries.Add((objNum, writer.WriteIndirectObject(objNum, 0, obj)));

            long xrefOffset = writer.WriteXrefTable(xrefEntries);
            writer.WriteTrailer(trailer, xrefOffset);
        }

        // Builds the catalog /Dests dictionary (bookmark name → [page /XYZ x y null]).
        private int BuildDests(List<(int objNum, PdfObject obj)> objects, ref int nextObjNum, List<int> pageObjNums)
        {
            if (_bookmarks.Count == 0) return 0;
            var dict = new PdfDictionary();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool any = false;
            foreach (var (name, pageIndex, y) in _bookmarks)
            {
                if (pageIndex < 0 || pageIndex >= pageObjNums.Count || !seen.Add(name)) continue;
                dict.Set(new PdfName(name), Destination(pageObjNums[pageIndex], y));
                any = true;
            }
            if (!any) return 0;
            int n = nextObjNum++;
            dict.ObjectNumber = n; dict.GenerationNumber = 0;
            objects.Add((n, dict));
            return n;
        }

        private PdfArray Destination(int pageObjNum, double y)
            => XyzDestination.Build(pageObjNum, _marginLeft, y, zoom: null);

        // Builds the /Outlines (bookmark pane) tree from the collected heading paragraphs.
        private int BuildOutlines(List<(int objNum, PdfObject obj)> objects, ref int nextObjNum, List<int> pageObjNums)
        {
            if (_headings.Count == 0) return 0;
            int rootNum = nextObjNum++;
            var root = new PdfDictionary();
            root.Set(PdfName.Type, PdfName.Outlines);
            root.ObjectNumber = rootNum; root.GenerationNumber = 0;

            var items = new List<(int num, PdfDictionary dict, int level)>();
            foreach (var (level, text, pageIndex, y) in _headings)
            {
                if (pageIndex < 0 || pageIndex >= pageObjNums.Count) continue;
                int num = nextObjNum++;
                var d = new PdfDictionary();
                d.Set(PdfName.Title, new PdfString(text));
                d.Set(PdfName.Dest, Destination(pageObjNums[pageIndex], y));
                d.ObjectNumber = num; d.GenerationNumber = 0;
                items.Add((num, d, level));
            }
            if (items.Count == 0) return 0;

            // Link items into a tree by level (a stack of open ancestors).
            var stack = new List<(int level, int num, PdfDictionary dict)>();
            // The parent's dictionary is kept beside its children's: finding it again by a scan of
            // every item once a parent would be quadratic in a document of nested headings.
            var childInfo = new Dictionary<int, (int first, int last, PdfDictionary lastDict, PdfDictionary parent)>();
            // §12.3.3 Table 152: the root's /Count is every visible item, at all levels, and an open
            // item's is its visible descendants — every item here is open. It was each parent's
            // direct children. Each item adds one to every ancestor on the stack when it is linked.
            var descendants = new Dictionary<int, int> { [rootNum] = 0 };
            foreach (var (num, d, level) in items)
            {
                while (stack.Count > 0 && stack[stack.Count - 1].level >= level) stack.RemoveAt(stack.Count - 1);
                descendants[rootNum]++;
                foreach (var open in stack) descendants[open.num] = descendants.TryGetValue(open.num, out int n) ? n + 1 : 1;
                int parentNum = stack.Count == 0 ? rootNum : stack[stack.Count - 1].num;
                PdfDictionary parentDict = stack.Count == 0 ? root : stack[stack.Count - 1].dict;
                d.Set(PdfName.Parent, new PdfReference(parentNum, 0));

                if (!childInfo.TryGetValue(parentNum, out var ci))
                {
                    parentDict.Set(PdfName.First, new PdfReference(num, 0));
                    childInfo[parentNum] = (num, num, d, parentDict);
                }
                else
                {
                    ci.lastDict.Set(PdfName.Next, new PdfReference(num, 0));
                    d.Set(PdfName.Prev, new PdfReference(ci.last, 0));
                    childInfo[parentNum] = (ci.first, num, d, ci.parent);
                }
                stack.Add((level, num, d));
            }

            foreach (var kv in childInfo)
            {
                PdfDictionary pd = kv.Value.parent;
                pd.Set(PdfName.Last, new PdfReference(kv.Value.last, 0));
                pd.Set(PdfName.Count, new PdfNumber(descendants[kv.Key]));
            }

            foreach (var (num, d, _) in items) objects.Add((num, d));
            objects.Add((rootNum, root));
            return rootNum;
        }

        // Builds the Tagged-PDF logical structure: a /Document element holding one /P or /Hn
        // element per body paragraph, each referencing its line MCIDs, plus the parent number
        // tree mapping (page, MCID) back to the owning element. Returns the StructTreeRoot object
        // number, or 0 when tagging is off / nothing was tagged.
        private int BuildStructTree(List<(int objNum, PdfObject obj)> objects, ref int nextObjNum, List<int> pageObjNums)
        {
            if (!_options.TaggedPdf || _structElems.Count == 0) return 0;

            int structRootNum = nextObjNum++;
            int docElemNum = nextObjNum++;
            var elemNums = new List<int>();
            foreach (var _ in _structElems) elemNums.Add(nextObjNum++);
            int parentTreeNum = nextObjNum++;

            // /Document element — parent is the struct root, kids are every paragraph/heading.
            var docKids = new PdfArray();
            for (int i = 0; i < _structElems.Count; i++) docKids.Add(new PdfReference(elemNums[i], 0));
            var docElem = new PdfDictionary();
            docElem.Set(PdfName.Type, PdfName.StructElem);
            docElem.Set(PdfName.S, PdfName.Document);
            docElem.Set(PdfName.P, new PdfReference(structRootNum, 0));
            docElem.Set(PdfName.K, docKids);

            // Each paragraph/heading element + the (page, MCID) → element ownership map.
            var owner = new Dictionary<(int, int), int>();
            var pageMaxMcid = new Dictionary<int, int>();
            var elemObjs = new List<PdfDictionary>();
            for (int i = 0; i < _structElems.Count; i++)
            {
                var se = _structElems[i];
                var k = new PdfArray();
                foreach (var (p, m) in se.Mcids)
                {
                    owner[(p, m)] = i;
                    if (!pageMaxMcid.TryGetValue(p, out int mx) || m + 1 > mx) pageMaxMcid[p] = m + 1;
                    if (p == se.FirstPage)
                        k.Add(new PdfNumber(m)); // same page as /Pg → a bare MCID integer
                    else
                    {
                        var mcr = new PdfDictionary(); // a marked-content reference on another page
                        mcr.Set(PdfName.Type, PdfName.MCR);
                        mcr.Set(PdfName.Pg, new PdfReference(pageObjNums[p], 0));
                        mcr.Set(PdfName.MCID, new PdfNumber(m));
                        k.Add(mcr);
                    }
                }
                var elem = new PdfDictionary();
                elem.Set(PdfName.Type, PdfName.StructElem);
                elem.Set(PdfName.S, new PdfName(se.Role));
                elem.Set(PdfName.P, new PdfReference(docElemNum, 0));
                if (se.FirstPage >= 0 && se.FirstPage < pageObjNums.Count)
                    elem.Set(PdfName.Pg, new PdfReference(pageObjNums[se.FirstPage], 0));
                elem.Set(PdfName.K, k.Count == 1 ? k[0] : (PdfObject)k);
                elemObjs.Add(elem);
            }

            // Parent number tree: each page's StructParents key → [element ref per MCID].
            var nums = new PdfArray();
            foreach (var p in pageMaxMcid.Keys.OrderBy(x => x))
            {
                var arr = new PdfArray();
                for (int m = 0; m < pageMaxMcid[p]; m++)
                    arr.Add(owner.TryGetValue((p, m), out int ei)
                        ? (PdfObject)new PdfReference(elemNums[ei], 0) : PdfNull.Instance);
                nums.Add(new PdfNumber(p));
                nums.Add(arr);
            }
            var parentTree = new PdfDictionary();
            parentTree.Set(PdfName.Nums, nums);

            var structRoot = new PdfDictionary();
            structRoot.Set(PdfName.Type, PdfName.StructTreeRoot);
            var rootKids = new PdfArray();
            rootKids.Add(new PdfReference(docElemNum, 0));
            structRoot.Set(PdfName.K, rootKids);
            structRoot.Set(PdfName.ParentTree, new PdfReference(parentTreeNum, 0));
            structRoot.Set(PdfName.ParentTreeNextKey, new PdfNumber(pageObjNums.Count));

            void Write(int num, PdfObject o)
            {
                o.ObjectNumber = num; o.GenerationNumber = 0;
                objects.Add((num, o));
            }
            Write(structRootNum, structRoot);
            Write(docElemNum, docElem);
            for (int i = 0; i < elemObjs.Count; i++) Write(elemNums[i], elemObjs[i]);
            Write(parentTreeNum, parentTree);
            return structRootNum;
        }

        internal PdfPageBuilder NewPage()
        {
            DrawPendingFootnotes(); // lay this page's footnotes before leaving it
            if (_options.MaxPages > 0 && _pageBuilders.Count >= _options.MaxPages) throw new PageLimitReached();
            if (_footnoteRestart == RestartNumberValues.EachPage) _footnoteCounter = _footnoteStart - 1;
            _currentPage = new PdfPageBuilder(_options, _pageWidth, _pageHeight);
            _pageBuilders.Add(_currentPage);
            _currentColumn = 0;
            _pageFloats.Clear();
            CursorY = _pageHeight - _marginTop;
            return _currentPage;
        }

        /// <summary>
        /// Thrown by <see cref="NewPage"/> instead of beginning the page past <see cref="DocxToPdfOptions.MaxPages"/>, and
        /// caught where the sections are laid out: every page begun before it holds its content and footnotes.
        /// </summary>
        private sealed class PageLimitReached : Exception
        {
        }

        // Advances body flow past the bottom of the current column: to the next column on
        // the same page, or — if this was the last column — to a fresh page. This is the
        // multi-column generalisation of NewPage used at content overflow and for explicit
        // column breaks.
        internal void BreakFlow()
        {
            if (_currentColumn < _columns.Count - 1)
            {
                _currentColumn++;
                CursorY = ColumnTop;
            }
            else
            {
                NewPage();
            }
        }

        internal void AdvanceCursor(double points)
        {
            CursorY -= points;
            // A band (a header, a footer, the footnote band) is laid out with no page bottom, so a band
            // the file makes taller than a page would be laid out whole on every page it is drawn on —
            // its lines times the pages, both the file's to choose. One page's height below the page's
            // edge, nothing more of it can be seen, and its layout stops (RenderBand reports it).
            if (_inBand && CursorY < -_pageHeight) throw new BandOverflow();
            if (CursorY < EffectiveBottom)
            {
                BreakFlow();
            }
        }

        /// <summary>Whether a band is being laid out: the page bottom is the sentinel and the band's height is bounded.</summary>
        private bool _inBand;

        private bool _bandOverflowReported;

        /// <summary>Thrown by <see cref="AdvanceCursor"/> when a band's layout has gone a page's height below the page.</summary>
        private sealed class BandOverflow : Exception
        {
        }

        private void ReportBandOverflow(string band)
        {
            if (_bandOverflowReported) return;
            _bandOverflowReported = true;
            DocxDiagnostics.Report("DocxRenderContext.Band",
                $"a {band} is taller than the page; what lies more than a page's height below the page's edge was not " +
                "laid out. Reported once per conversion.");
        }

        internal bool NeedsNewPage(double requiredSpace)
        {
            return CursorY - requiredSpace < EffectiveBottom;
        }

        /// <summary>Registers a floating-image exclusion rectangle on the current page.</summary>
        internal void AddFloat(FloatRect r) => _pageFloats.Add(r);

        /// <summary>
        /// The usable horizontal extent (xStart, width) for a line whose top is at
        /// <paramref name="y"/> with height <paramref name="lineHeight"/>, within the active
        /// column and clear of any floats it vertically overlaps. With one column and no
        /// floats this is exactly (<see cref="ContentLeft"/>, <see cref="ContentWidth"/>).
        /// A float left-of-centre pushes the start right; right-of-centre shrinks the width.
        /// </summary>
        internal (double x, double width) GetLineSegment(double y, double lineHeight)
        {
            double x0 = ContentLeft;
            double x1 = ContentLeft + ContentWidth;
            if (_pageFloats.Count > 0)
            {
                double lineTop = y, lineBottom = y - lineHeight;
                double colMid = (x0 + x1) / 2.0;
                foreach (var f in _pageFloats)
                {
                    if (f.BehindText) continue;
                    if (f.Bottom >= lineTop || f.Top <= lineBottom) continue; // no vertical overlap
                    if (f.Right <= x0 || f.Left >= x1) continue;               // not in this column
                    double fMid = (f.Left + f.Right) / 2.0;
                    if (fMid <= colMid) x0 = Math.Max(x0, f.Right); // left-side float
                    else x1 = Math.Min(x1, f.Left);                 // right-side float
                }
            }
            return (x0, Math.Max(0, x1 - x0));
        }

        internal void AppendContent(string operators)
        {
            _currentPage.AppendOperator(operators);
        }

        /// <summary>
        /// How far the most recently emitted text line hangs below its baseline.
        ///
        /// <para>
        /// The flow cursor sits <b>on</b> a line's baseline, so anything placed at the cursor
        /// starts level with it and covers that line's descenders. Text gets away with it because
        /// the next line advances a full line height first; an inline drawing does not, and painted
        /// straight over the "p" and "g" of the caption above it. Consumed and cleared by
        /// <c>Drawings.DrawingPlacement.PlaceInline</c>.
        /// </para>
        /// </summary>
        internal double LastLineDescent { get; set; }

        internal string RegisterFont(string baseFontName)
        {
            return _currentPage.AddFont(baseFontName);
        }

        internal string RegisterImage(byte[] imageData, int width, int height, string filter,
            string colorSpace = PdfColorSpaceNames.DeviceRGB, int bitsPerComponent = 8,
            byte[] softMask = null, double[] decode = null)
        {
            // The mask is carried through to the builder's eight-argument overload, which owns the
            // compression and the "a fully opaque mask is not written" rule — neither this method
            // nor PdfPageBuilder re-decides either. The /Decode array is the frame's (an Adobe CMYK
            // JPEG's inversion), handed on as it is.
            return _currentPage.AddImage(imageData, width, height, filter, colorSpace,
                                         bitsPerComponent, softMask, decode);
        }

        /// <summary>
        /// Registers a constant-alpha graphics state, returning the resource name to emit as
        /// <c>/GSn gs</c>. DrawingML shapes commonly carry <c>a:alpha</c>, and constant alpha is
        /// the only transparency this renderer produces.
        /// </summary>
        internal string RegisterAlpha(double? fillAlpha, double? strokeAlpha)
        {
            return _currentPage.AddExtGState(
                fillAlpha.HasValue ? (float)fillAlpha.Value : (float?)null,
                strokeAlpha.HasValue ? (float)strokeAlpha.Value : (float?)null);
        }

        /// <summary>Registers a linear gradient; returns the name to emit as <c>/Shn sh</c>.</summary>
        internal string RegisterAxialShading(double x0, double y0, double x1, double y1,
            IReadOnlyList<Tetitulz.Pdf.Core.Writing.PdfContentStreamBuilder.ShadingStop> stops)
            => _currentPage.AddAxialShading(x0, y0, x1, y1, stops);

        /// <summary>Registers a radial gradient; returns the name to emit as <c>/Shn sh</c>.</summary>
        internal string RegisterRadialShading(double cx, double cy, double radius,
            IReadOnlyList<Tetitulz.Pdf.Core.Writing.PdfContentStreamBuilder.ShadingStop> stops)
            => _currentPage.AddRadialShading(cx, cy, radius, stops);

        // The active column's left edge / width (one full-width column by default).
        internal double ContentWidth => _columns[_currentColumn].width;
        internal double ContentLeft => _columns[_currentColumn].left;
        // Full text-box width (all columns + gaps) — for headers/footers/footnotes that span the page.
        internal double FullContentWidth => _pageWidth - _marginLeft - _marginRight;
        // Page / margin geometry (for absolute float positioning).
        internal double PageWidth => _pageWidth;
        internal double PageHeight => _pageHeight;
        internal double MarginLeft => _marginLeft;
        internal double MarginRight => _marginRight;
        internal double MarginTop => _marginTop;
        internal double MarginBottom => _marginBottom;
        internal bool HasActiveFloats => _pageFloats.Count > 0;
        internal double ContentHeight => _pageHeight - _marginTop - _marginBottom;
        // Printable height left on the current page below the cursor.
        internal double RemainingHeight => CursorY - EffectiveBottom;
        internal double CursorY { get; set; }
        internal DocxToPdfOptions Options => _options;
        internal StyleResolver StyleResolver => _styleResolver;

        /// <summary>
        /// The document's DrawingML colour resolver. One instance per document, because it caches
        /// the theme lookup behind <see cref="StyleResolver"/> and reports the no-theme fallback
        /// once rather than per shape.
        /// </summary>
        internal Drawings.DrawingColorResolver DrawingColors
            => _drawingColors ??= new Drawings.DrawingColorResolver(_styleResolver);

        private Drawings.DrawingColorResolver _drawingColors;

        /// <summary>
        /// The footnote or endnote <paramref name="id"/> names — the first with it, as a search finds — from an index
        /// of the notes part built once a conversion; null when none has it.
        /// </summary>
        /// <remarks>A search for each reference would cost references × notes: 2.1 s at 8 000 footnotes.</remarks>
        internal OpenXmlElement NoteById(long id, bool isEndnote)
        {
            var main = _wordDoc?.MainDocumentPart;
            Dictionary<long, OpenXmlElement> index = isEndnote
                ? _endnotesById ??= IndexNotes(main?.EndnotesPart?.Endnotes?.Elements<Endnote>())
                : _footnotesById ??= IndexNotes(main?.FootnotesPart?.Footnotes?.Elements<Footnote>());
            return index.TryGetValue(id, out var note) ? note : null;
        }

        private Dictionary<long, OpenXmlElement> _footnotesById, _endnotesById;

        /// <summary>
        /// Each image part as decoded once this conversion — the picture, or the reason nothing was painted
        /// (<c>ImageRenderer.TryDecode</c>); by part, so every reference hands the writer the same bytes.
        /// </summary>
        internal Dictionary<OpenXmlPart, object> DecodedImages { get; } = new Dictionary<OpenXmlPart, object>();

        private static Dictionary<long, OpenXmlElement> IndexNotes(IEnumerable<FootnoteEndnoteType> notes)
        {
            var index = new Dictionary<long, OpenXmlElement>();
            if (notes == null) return index;
            foreach (var note in notes)
                if (note.Id?.Value is long id && !index.ContainsKey(id)) index[id] = note;
            return index;
        }

        internal WordprocessingDocument WordDocument => _wordDoc;
        internal PdfPageBuilder CurrentPage => _currentPage;
    }
}
