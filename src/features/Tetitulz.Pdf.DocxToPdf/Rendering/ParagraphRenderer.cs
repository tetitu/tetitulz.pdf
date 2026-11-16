// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class ParagraphRenderer : IDocxElementRenderer
    {
        private readonly RunRenderer _runRenderer = new RunRenderer();
        private readonly Drawings.DrawingRenderer _drawingRenderer = new Drawings.DrawingRenderer();

        /// <summary>
        /// How many consecutive lines the float-aware wrap loop may skip because a floating image
        /// leaves too little room, before it gives up and lays the line out in what room there is.
        /// A page break clears the page's floats, so the loop ends at the next page at the latest;
        /// the cap is the backstop behind that.
        /// </summary>
        private const int MaxConsecutiveBlockedLines = 10_000;

        public void Render(OpenXmlElement element, DocxRenderContext context)
        {
            var paragraph = element as Paragraph;
            if (paragraph == null) return;

            var resolver = context.StyleResolver;
            var (spaceBefore, spaceAfter) = resolver.ResolveParagraphSpacing(paragraph);
            var (indentLeft, indentRight, firstLineIndent) = resolver.ResolveIndentation(paragraph);
            var alignment = resolver.ResolveDisplayAlignment(paragraph);

            // Check for numbering (list items)
            string listPrefix = ListRenderer.GetListPrefix(paragraph, context);

            // Apply spacing before
            if (spaceBefore > 0)
                context.AdvanceCursor(spaceBefore);

            // Internal navigation: record bookmark targets and heading outline entries at
            // this paragraph's start position.
            foreach (var bm in paragraph.Elements<BookmarkStart>())
                if (bm.Name?.Value != null) context.RegisterBookmark(bm.Name.Value);
            int headingLevel = HeadingLevel(paragraph);
            if (headingLevel > 0)
                context.RegisterHeading(headingLevel, ParagraphText(paragraph));

            // Tagged PDF: open a structure element for this paragraph (H1–H6 for a heading,
            // else P). No-op unless TaggedPdf is enabled. Closed at the end of the paragraph.
            context.BeginStructElem(headingLevel > 0 ? "H" + System.Math.Min(headingLevel, 6) : "P");

            // Collect all run results
            var runs = new List<RunRenderResult>();
            bool renderedImage = false;

            void ProcessRun(Run run)
            {
                // A run holding a page or column break, a drawing or a note reference beside other
                // content is taken a part at a time, in order: each of those takes its own path below and
                // returns, so the run's text before or after it would otherwise be lost.
                var parts = SplitAtBlockChildren(run);
                if (parts != null)
                {
                    foreach (var (textPart, block) in parts)
                    {
                        if (textPart != null) ProcessRun(textPart);
                        else ProcessBlockChild(run, block);
                    }
                    return;
                }

                // A run may carry a <w:drawing> (inline/anchored image). Flush the
                // text accumulated so far, then render the image(s) as a block.
                var drawings = run.Elements<Drawing>().ToList();
                if (drawings.Count > 0)
                {
                    FlushRuns(runs, paragraph, context, alignment, indentLeft, indentRight, firstLineIndent, listPrefix);
                    runs.Clear();
                    listPrefix = null;
                    foreach (var drawing in drawings)
                        _drawingRenderer.Render(drawing, context);
                    renderedImage = true;
                    return;
                }

                // Footnote / endnote reference: draw a superscript number and queue the
                // note body (footnotes → bottom-of-page band; endnotes → document end).
                var fnRef = run.GetFirstChild<FootnoteReference>();
                var enRef = run.GetFirstChild<EndnoteReference>();
                if (fnRef != null || enRef != null)
                {
                    bool isEnd = enRef != null;
                    AddNoteMark(run, isEnd ? (enRef.Id?.Value ?? -1) : (fnRef.Id?.Value ?? -1), isEnd);
                    return;
                }

                var result = _runRenderer.Render(run, paragraph, context);
                if (result.PageBreak || result.ColumnBreak)
                {
                    FlushRuns(runs, paragraph, context, alignment, indentLeft, indentRight, firstLineIndent, listPrefix);
                    runs.Clear();
                    listPrefix = null;
                    // A page break always starts a fresh page; a column break advances to the
                    // next column (or a new page if this was the last column).
                    if (result.PageBreak) context.NewPage();
                    else context.BreakFlow();
                    return;
                }
                runs.Add(result);
            }

            // A footnote or endnote reference: a superscript number, and the note queued (footnotes to the
            // page's band, endnotes to the document's end).
            void AddNoteMark(Run run, long id, bool isEnd)
            {
                if (id < 1) return;
                int number = context.NoteNumber(id, isEnd, () => NoteText(context, id, isEnd));
                double size = resolver.ResolveFontSizeInPoints(run, paragraph, context.Options.DefaultFontSize);
                runs.Add(new RunRenderResult
                {
                    Text = context.FormatNoteNumber(number, isEnd),
                    PdfFontName = StandardFonts.Helvetica,
                    FontSize = size * DocxLayoutMetrics.NoteMarkFontScale,
                    BaselineShift = size * DocxLayoutMetrics.NoteMarkRiseFactor,
                });
            }

            // One block child of a run that holds other content too (SplitAtBlockChildren), on the path the
            // run would have taken for it alone.
            void ProcessBlockChild(Run run, OpenXmlElement block)
            {
                switch (block)
                {
                    case Drawing drawing:
                        FlushRuns(runs, paragraph, context, alignment, indentLeft, indentRight, firstLineIndent, listPrefix);
                        runs.Clear();
                        listPrefix = null;
                        _drawingRenderer.Render(drawing, context);
                        renderedImage = true;
                        return;
                    case FootnoteReference fn:
                        AddNoteMark(run, fn.Id?.Value ?? -1, isEnd: false);
                        return;
                    case EndnoteReference en:
                        AddNoteMark(run, en.Id?.Value ?? -1, isEnd: true);
                        return;
                    case Break br:
                        FlushRuns(runs, paragraph, context, alignment, indentLeft, indentRight, firstLineIndent, listPrefix);
                        runs.Clear();
                        listPrefix = null;
                        if (br.Type?.Value == BreakValues.Page) context.NewPage();
                        else context.BreakFlow();
                        return;
                }
            }

            // Recursively render a paragraph's inline children, unwrapping the
            // containers that also hold runs: hyperlinks and simple fields here, the
            // wrappers that only mark their runs through MarkupWrappers.
            void ProcessInline(OpenXmlElement child, string linkUri)
            {
                switch (child)
                {
                    case Run run:
                        int b1 = runs.Count;
                        ProcessRun(run);
                        if (linkUri != null) for (int k = b1; k < runs.Count; k++) runs[k].LinkUri = linkUri;
                        break;
                    case Hyperlink h:
                        string uri = ResolveHyperlinkUri(h, context) ?? linkUri;
                        foreach (var c in h.ChildElements) ProcessInline(c, uri);
                        break;
                    case SimpleField f:
                        string computed = ComputeField(f.Instruction?.Value, context);
                        if (computed != null)
                        {
                            var rPr = f.Descendants<Run>().FirstOrDefault()?.RunProperties;
                            var fieldRun = new Run(new Text(computed) { Space = SpaceProcessingModeValues.Preserve });
                            if (rPr != null) fieldRun.RunProperties = (RunProperties)rPr.CloneNode(true);
                            int b2 = runs.Count;
                            ProcessRun(fieldRun);
                            if (linkUri != null) for (int k = b2; k < runs.Count; k++) runs[k].LinkUri = linkUri;
                        }
                        else foreach (var c in f.ChildElements) ProcessInline(c, linkUri);
                        break;
                    default:
                        // Content controls, insertions, smart tags, custom XML, moves, bidi wrappers: their runs;
                        // a deletion or a move's source: nothing (MarkupWrappers).
                        var wrapped = MarkupWrappers.InlineContent(child);
                        if (wrapped != null)
                        {
                            // A w:dir or w:bdo: its controls around its runs, for the line's bidi pass.
                            var (open, close) = MarkupWrappers.DirectionControls(child);
                            if (open != '\0') runs.Add(new RunRenderResult { IsDirectionControl = true, Text = open.ToString() });
                            foreach (var c in wrapped) ProcessInline(c, linkUri);
                            if (close != '\0') runs.Add(new RunRenderResult { IsDirectionControl = true, Text = close.ToString() });
                            break;
                        }

                        // Office Math (m:oMath / m:oMathPara): typeset the equation as a box tree
                        // and hand it to the line as one atomic inline. Constructs with no 2-D form
                        // fall back to MathLinearizer per node, so nothing is ever dropped.
                        if (child.LocalName == DocxLayoutMetrics.OMathParaElement)
                        {
                            // A display equation is a block of its own: it gets a whole line,
                            // justified by m:jc rather than flowing with the surrounding text.
                            FlushRuns(runs, paragraph, context, alignment,
                                indentLeft, indentRight, firstLineIndent, listPrefix);
                            runs.Clear();
                            listPrefix = null;
                            RenderDisplayMath(child, paragraph, context);
                            break;
                        }

                        if (child.LocalName == DocxLayoutMetrics.OMathElement)
                        {
                            var mathSeg = BuildMathSegment(child, paragraph, context);
                            if (mathSeg != null)
                            {
                                mathSeg.LinkUri = linkUri;
                                runs.Add(new RunRenderResult
                                {
                                    Text = null,
                                    MathBox = mathSeg.Math,
                                    PdfFontName = mathSeg.PdfFontName,
                                    FontSize = mathSeg.FontSize,
                                    Color = mathSeg.Color,
                                    LinkUri = linkUri,
                                });
                            }
                        }
                        break;
                }
            }

            // The fields resolved first (FieldWalk): a complex PAGE or NUMPAGES field draws its live
            // value, not the number the producer cached.
            foreach (var child in FieldWalk.ResolveFields(paragraph.ChildElements, instruction => ComputeField(instruction, context)))
                ProcessInline(child, null);

            // Render remaining runs
            if (runs.Count > 0 || listPrefix != null)
            {
                FlushRuns(runs, paragraph, context, alignment, indentLeft, indentRight, firstLineIndent, listPrefix);
            }
            else if (!renderedImage)
            {
                // Empty paragraph -- advance by its own resolved line height so blank
                // spacer paragraphs keep the correct height (not always the default).
                double fontSize = resolver.ResolveFontSizeInPoints(null, paragraph, context.Options.DefaultFontSize);
                context.AdvanceCursor(fontSize * DocxLayoutMetrics.SingleLineFactor);
            }

            // Apply spacing after
            if (spaceAfter > 0)
                context.AdvanceCursor(spaceAfter);

            context.EndStructElem();
        }

        private static string ResolveHyperlinkUri(Hyperlink hyperlink, DocxRenderContext context)
            => HyperlinkTargets.Of(hyperlink, context.WordDocument.MainDocumentPart);

        // Heading outline level (1-9) from the paragraph's style id ("Heading1"…) or an
        // explicit w:outlineLvl; 0 = not a heading.
        private static int HeadingLevel(Paragraph paragraph)
        {
            var ol = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
            if (ol != null && ol.Value >= 0 && ol.Value <= 8) return ol.Value + 1;
            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (styleId != null && styleId.StartsWith("Heading", System.StringComparison.OrdinalIgnoreCase)
                && int.TryParse(styleId.Substring(7), out int lvl) && lvl >= 1 && lvl <= 9)
                return lvl;
            return 0;
        }

        private static string ParagraphText(Paragraph paragraph)
        {
            var sb = new StringBuilder();
            foreach (var t in paragraph.Descendants<Text>()) sb.Append(t.Text);
            return sb.ToString();
        }

        // Concatenated text of the footnote/endnote with the given id.
        /// <summary>
        /// A child the paragraph walk takes on its own and returns from: a page or column break, a drawing,
        /// a footnote or endnote reference.
        /// </summary>
        private static bool IsBlockChild(OpenXmlElement child)
            => child is Drawing || child is FootnoteReference || child is EndnoteReference
               || child is Break br && (br.Type?.Value == BreakValues.Page || br.Type?.Value == BreakValues.Column);

        /// <summary>
        /// <paramref name="run"/>'s parts in order — each block child (<see cref="IsBlockChild"/>) itself, the
        /// original element, and the content between them as a run of its own carrying the run's properties —
        /// or <c>null</c> when it has nothing beside a single block child to lose.
        /// </summary>
        /// <remarks>
        /// A block child is handed over as it is, never cloned: a drawing's image is found through the part it
        /// belongs to. Only the content between is copied, and it is text, tabs and line breaks.
        /// </remarks>
        internal static List<(Run Text, OpenXmlElement Block)> SplitAtBlockChildren(Run run)
        {
            int blockChildren = 0, others = 0;
            foreach (var child in run.ChildElements)
            {
                if (child is RunProperties) continue;
                if (IsBlockChild(child)) blockChildren++;
                else others++;
            }
            if (blockChildren == 0 || blockChildren + others <= 1) return null;

            var parts = new List<(Run, OpenXmlElement)>();
            Run current = null;
            foreach (var child in run.ChildElements)
            {
                if (child is RunProperties) continue;
                if (IsBlockChild(child))
                {
                    if (current != null) { parts.Add((current, null)); current = null; }
                    parts.Add((null, child));
                    continue;
                }
                if (current == null)
                {
                    current = new Run();
                    if (run.RunProperties != null) current.RunProperties = (RunProperties)run.RunProperties.CloneNode(true);
                }
                current.AppendChild(child.CloneNode(true));
            }
            if (current != null) parts.Add((current, null));
            return parts;
        }

        /// <summary>The text of the footnote or endnote <paramref name="id"/> names, every run joined; "" for none.</summary>
        internal static string NoteText(DocxRenderContext context, long id, bool isEndnote)
        {
            OpenXmlElement note = context.NoteById(id, isEndnote);
            if (note == null) return "";
            var sb = new StringBuilder();
            foreach (var t in note.Descendants<Text>()) sb.Append(t.Text);
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Computed text for the field instructions this renderer evaluates, or <c>null</c> to fall
        /// back to the field's own cached result runs.
        ///
        /// <para>
        /// Matched on the field's <b>name</b> — the first whitespace-delimited token of the
        /// instruction (ECMA-376 §17.16.5) — never on a substring of the whole instruction. A
        /// substring test would hijack every field whose instruction merely contains one of the four
        /// tokens, and because a non-null return skips the cached-result fallback the correct value
        /// would be discarded at the same time: <c>PAGEREF _Toc90210 \h</c> would match "PAGE" and
        /// every entry of a table of contents render the TOC's own page number,
        /// <c>CREATEDATE</c> / <c>SAVEDATE</c> / <c>PRINTDATE</c> all matched "DATE" and rendered
        /// today, and <c>EDITTIME</c> matched "TIME" and rendered the wall clock.
        /// </para>
        /// </summary>
        internal static string ComputeField(string instruction, DocxRenderContext context)
        {
            string name = FieldName(instruction);
            if (name == null) return null;

            switch (name)
            {
                case "NUMPAGES": return Tetitulz.Pdf.Builder.Export.PdfExporter.NumPagesPlaceholder;
                case "PAGE": return context.FormatPageNumber(context.CurrentPageNumber);
                case "TIME": return System.DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
                case "DATE": return System.DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                default: return null;   // unrecognised → the field's cached result runs are used
            }
        }

        /// <summary>
        /// The upper-cased field name — the first whitespace-delimited token of a field
        /// instruction — or <c>null</c> when the instruction is empty.
        ///
        /// <para>
        /// The single definition, reached from both output paths, <c>DocxToBuilderMapper</c>'s too: a
        /// <c>Contains("PAGE")</c> copy of the decision would let a PAGEREF hijack handled in one
        /// renderer survive in the other.
        /// </para>
        /// </summary>
        internal static string FieldName(string instruction)
        {
            if (string.IsNullOrEmpty(instruction)) return null;
            string trimmed = instruction.Trim();
            if (trimmed.Length == 0) return null;

            int end = 0;
            while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end])) end++;
            return trimmed.Substring(0, end).ToUpperInvariant();
        }

        /// <summary>
        /// The embedded font a marker beyond WinAnsi is drawn in: the paragraph's complex-script
        /// family, resolved as a run's is (the DOCX's embedded fonts, then the machine's).
        /// <c>null</c> — reported — when nothing covers it. The cell renderer's markers take it too.
        /// </summary>
        internal static Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram MarkerProgram(string marker, Paragraph paragraph,
            DocxRenderContext context)
        {
            var cps = UnicodeText.CodePointsToCover(marker);
            string family = context.StyleResolver.ResolveComplexScriptFontName(null, paragraph);
            var program = context.ResolveEmbeddedFont(family, false, false, cps)
                          ?? FontResolver.Resolve(family, false, false, cps, context.Options.AllowRestrictedSystemFonts);
            if (program == null) RunRenderer.ReportNoCoveringFont("ParagraphRenderer.NoCoveringMarkerFont", family, marker);
            return program;
        }

        private void FlushRuns(List<RunRenderResult> runs, Paragraph paragraph, DocxRenderContext context,
            JustificationValues alignment, double indentLeft, double indentRight,
            double firstLineIndent, string listPrefix)
        {
            var resolver = context.StyleResolver;
            double contentWidth = context.ContentWidth - indentLeft - indentRight;
            var (tabStops, defaultTab) = resolver.ResolveTabs(paragraph);
            bool rtlParagraph = resolver.ResolveParagraphRtl(paragraph);
            LineBidi.Paragraph bidiParagraph = null;   // resolved once the segments are built, before any line

            // Build text segments for line wrapping
            var segments = new List<TextSegment>();

            // Add list prefix as first segment if present
            if (!string.IsNullOrEmpty(listPrefix))
            {
                var defaultFontName = StandardFonts.Helvetica;
                var defaultFontSize = context.Options.DefaultFontSize;
                // Font the marker from the first run that actually has text (an empty
                // but styled leading run shouldn't dictate the bullet/number size).
                var styleRun = runs.FirstOrDefault(r => !r.IsDirectionControl && !string.IsNullOrEmpty(r.Text))
                               ?? runs.FirstOrDefault(r => !r.IsDirectionControl);
                if (styleRun != null)
                {
                    defaultFontName = styleRun.PdfFontName ?? StandardFonts.Helvetica;
                    defaultFontSize = styleRun.FontSize;
                }

                // A marker beyond WinAnsi — a Hebrew number (hebrew1, hebrew2) — is drawn as a run
                // of glyphs in an embedded font, as such a run's own text is: through the standard-14
                // font every letter of it was encoded as a space, and "א." came out " .".
                var markerProgram = UnicodeText.NeedsUnicode(listPrefix) ? MarkerProgram(listPrefix, paragraph, context) : null;
                if (markerProgram != null)
                {
                    segments.Add(new TextSegment
                    {
                        IsUnicode = true,
                        Text = listPrefix,
                        Program = markerProgram,
                        FontEntry = context.RegisterEmbeddedFont(markerProgram),
                        FontSize = defaultFontSize,
                        Color = null,
                    });
                }
                else
                {
                    segments.Add(new TextSegment
                    {
                        Text = listPrefix,
                        PdfFontName = defaultFontName,
                        FontSize = defaultFontSize,
                        Color = null
                    });
                }
            }

            foreach (var run in runs)
            {
                if (run.IsDirectionControl)
                {
                    segments.Add(new TextSegment { IsControl = true, Text = run.Text });
                    continue;
                }

                // A typeset equation is one atomic box, not a run of text.
                if (run.MathBox != null)
                {
                    segments.Add(new TextSegment
                    {
                        IsMath = true,
                        Math = run.MathBox,
                        PdfFontName = run.PdfFontName,
                        FontSize = run.FontSize,
                        Color = run.Color,
                        LinkUri = run.LinkUri,
                    });
                    continue;
                }

                // Embedded-font (Hebrew/Arabic/…) runs are drawn as a glyph segment.
                if (run.IsUnicode && run.Program != null && !string.IsNullOrEmpty(run.Text))
                {
                    segments.Add(new TextSegment
                    {
                        IsUnicode = true,
                        Text = run.Text,
                        Program = run.Program,
                        FontEntry = run.FontEntry,
                        FontSize = run.FontSize,
                        Color = run.Color,
                        Underline = run.Underline,
                        Strike = run.Strike,
                        Highlight = run.Highlight,
                        BaselineShift = run.BaselineShift,
                        CharSpacing = run.CharSpacing,
                        TextScale = run.TextScale,
                        Kerning = run.Kerning,
                        ContextualAlternates = run.ContextualAlternates,
                        Ligatures = run.Ligatures,
                        LinkUri = run.LinkUri,
                    });
                    continue;
                }

                if (string.IsNullOrEmpty(run.Text)) continue;

                // Split the run on line breaks ('\n') and tabs ('\t'); a tab becomes a
                // positional tab segment, a newline a line-break segment. A small-caps run is cut
                // where its letters change class too: a lowercase letter is drawn as its capital at
                // SmallCapsScale of the size, the rest at the size (§17.3.2.33).
                var buf = new StringBuilder();
                bool? bufLower = null;
                void FlushText()
                {
                    if (buf.Length == 0) return;
                    segments.Add(new TextSegment
                    {
                        Text = buf.ToString(),
                        PdfFontName = run.PdfFontName,
                        FontSize = run.FontSize * (run.SmallCaps && bufLower == true ? DocxLayoutMetrics.SmallCapsScale : 1),
                        Color = run.Color,
                        Underline = run.Underline,
                        Strike = run.Strike,
                        Highlight = run.Highlight,
                        BaselineShift = run.BaselineShift,
                        CharSpacing = run.CharSpacing,
                        TextScale = run.TextScale,
                        LinkUri = run.LinkUri,
                    });
                    buf.Clear();
                }
                foreach (char ch in run.Text)
                {
                    if (ch == '\n') { FlushText(); segments.Add(new TextSegment { IsLineBreak = true }); }
                    else if (ch == '\t')
                    {
                        FlushText();
                        segments.Add(new TextSegment
                        {
                            IsTab = true, TabStops = tabStops, DefaultTab = defaultTab,
                            FontSize = run.FontSize, PdfFontName = run.PdfFontName, Color = run.Color,
                        });
                    }
                    else
                    {
                        bool lower = run.SmallCaps && char.IsLower(ch);
                        if (run.SmallCaps && buf.Length > 0 && lower != bufLower) FlushText();
                        bufLower = lower;
                        buf.Append(lower ? char.ToUpperInvariant(ch) : ch);
                    }
                }
                FlushText();
            }

            if (segments.Count == 0) return;

            // Lays out one already-wrapped line at the given left edge, honouring alignment /
            // justification against `avail`. Shared by the batch (no-float) and incremental
            // (float-aware) paths so their geometry stays identical.
            void EmitLine(List<TextSegment> line, bool isFirstLine, bool isLastLine, double xLeft, double avail)
            {
                double lineWidth = 0;
                foreach (var seg in line) lineWidth += seg.Width(resolver);

                double xOffset = xLeft + indentLeft;
                if (isFirstLine) xOffset += firstLineIndent;

                // Full justification (w:jc=both): stretch inter-word spacing on all but the last line.
                double wordSpacing = 0;
                if (alignment == JustificationValues.Both && !isLastLine)
                {
                    int spaces = line.Where(s => !s.IsGlyph && s.Text != null).Sum(s => s.Text.Count(c => c == ' '));
                    if (spaces > 0 && lineWidth < avail)
                        wordSpacing = (avail - lineWidth) / spaces;
                }
                else if (alignment == JustificationValues.Center)
                    xOffset += (avail - lineWidth) / 2.0;
                else if (alignment == JustificationValues.Right)
                    xOffset += avail - lineWidth;

                // Each drawn line is its own marked-content sequence (always within one page),
                // recorded under the current paragraph's structure element when tagging is on.
                // xLeft, not xOffset, is the tab origin: w:tab/@w:pos is measured from the
                // paragraph's text-start edge, which carries neither the indent nor the alignment
                // shift that xOffset does.
                // Right to left, a tab's stops are measured from the right margin, as Word mirrors them.
                var drawn = InVisualOrder(line, bidiParagraph, rtlParagraph);
                context.EmitTaggedLine(() =>
                    EmitTextLine(drawn, xOffset, context.CursorY, context, wordSpacing,
                                 rtlParagraph ? xLeft + indentLeft + avail + indentRight : xLeft, rtlParagraph));
            }

            // The paragraph's bidi, over its text before wrapping: each line then takes its part (LineBidi).
            bidiParagraph = LineBidi.Paragraph.Of(segments, rtlParagraph, LogicalText);

            if (!context.HasActiveFloats)
            {
                // Batch path: constant width → wrap the whole paragraph once, then emit.
                var wrappedLines = WrapSegments(segments, contentWidth, resolver);
                for (int li = 0; li < wrappedLines.Count; li++)
                {
                    var line = wrappedLines[li];
                    if (line.Count == 0) continue;

                    double maxFont = 0, ascent = 0, descent = 0;
                    bool hasMath = false;
                    foreach (var seg in line)
                    {
                        maxFont = Math.Max(maxFont, seg.FontSize);
                        ascent = Math.Max(ascent, seg.Ascent);
                        descent = Math.Max(descent, seg.Descent);
                        hasMath |= seg.IsMath;
                    }

                    // For math-free text `ascent` equals maxFont × SingleLineFactor exactly, so
                    // this is the same number it always was.
                    double lineHeight = ascent;
                    double explicitLine = resolver.ResolveLineHeight(paragraph, maxFont);
                    if (explicitLine > 0)
                        // An explicit w:spacing/w:line still wins for ordinary text — but it must
                        // not clip a tall equation, which Word would do and which would overprint
                        // the line above. Growing only when math is present keeps every existing
                        // document byte-identical.
                        lineHeight = hasMath ? Math.Max(explicitLine, ascent) : explicitLine;

                    if (context.NeedsNewPage(lineHeight + descent)) context.BreakFlow();
                    context.AdvanceCursor(lineHeight);
                    EmitLine(line, li == 0, li == wrappedLines.Count - 1, context.ContentLeft, contentWidth);
                    if (descent > 0) context.AdvanceCursor(descent);

                    // Record how far this line hangs below its baseline, so a drawing placed at the
                    // cursor next clears it instead of painting over the descenders.
                    context.LastLineDescent = maxFont * (DocxLayoutMetrics.SingleLineFactor - 1.0);
                }
            }
            else
            {
                // Float-aware path: the usable width varies per line (text wraps around a
                // floating image). Advance one line at a time, query the available segment at
                // that Y, re-wrap the remaining text to it, and emit the first line. When the
                // float ends below, the width re-widens automatically.
                double repFont = 0;
                foreach (var s in segments) repFont = Math.Max(repFont, s.FontSize);
                if (repFont <= 0) repFont = context.Options.DefaultFontSize;
                double explicitLine = resolver.ResolveLineHeight(paragraph, repFont);
                double lineHeight = explicitLine > 0 ? explicitLine : repFont * DocxLayoutMetrics.SingleLineFactor;
                double minWidth = repFont * 1.5;

                // The wrap is kept and the next line taken from it while the width stays the same; the rest
                // is wrapped again only when it changes. Wrapping all the remaining text again for every line
                // would be quadratic in the paragraph's lines, and the hard breaks after the first line are
                // kept, not consumed by the wrap, so beside a float they do not run their lines together.
                var remaining = segments;
                List<List<TextSegment>> lines = null;
                var hardEnds = new List<bool>();
                int next = 0;
                double wrappedAt = double.NaN;
                bool firstLine = true;
                int blocked = 0;
                while (lines != null ? next < lines.Count : remaining.Count > 0)
                {
                    if (context.NeedsNewPage(lineHeight)) context.BreakFlow();
                    context.AdvanceCursor(lineHeight);

                    var (segX, segW) = context.GetLineSegment(context.CursorY, lineHeight);
                    double avail = segW - indentLeft - indentRight;
                    if (avail < minWidth)
                    {
                        // A float (wrapTopAndBottom, or a wide image) that narrows this line blocks
                        // it: descend until the float clears. A line the float does not narrow is
                        // short at every height — an indent wider than the column — so descending
                        // would only break pages until the count ran out; it is laid out at once in
                        // the room the indent leaves, as the batch path lays such a paragraph out.
                        bool narrowedByAFloat = segW < context.ContentWidth;
                        if (narrowedByAFloat && ++blocked <= MaxConsecutiveBlockedLines) continue;
                        avail = Math.Max(minWidth, contentWidth);
                    }
                    blocked = 0;

                    if (lines == null || avail != wrappedAt)
                    {
                        if (lines != null) remaining = Unwrapped(lines, hardEnds, next);
                        hardEnds = new List<bool>();
                        lines = WrapSegments(remaining, avail, resolver, hardEnds);
                        next = 0;
                        wrappedAt = avail;
                        if (lines.Count == 0) break;
                    }

                    var line = lines[next++];
                    if (line.Count > 0)
                        EmitLine(line, firstLine, next == lines.Count, segX, avail);
                    firstLine = false;
                }
            }
        }

        /// <summary>
        /// Draws an <c>m:oMathPara</c> as its own block: a whole line, justified by
        /// <c>m:oMathParaPr/m:jc</c> (centred by default, as Word does).
        ///
        /// <para>
        /// An equation wider than the column is <b>scaled down uniformly</b>, floored at
        /// <see cref="MathTypesetting.MathLayoutMetrics.MinDisplayMathScale"/> and reported past it.
        /// Breaking a formula would need binary-operator break points and penalties — a separate
        /// project — and letting it overflow into the margin is silently wrong, which is the failure
        /// mode this renderer works hardest to avoid. Scaling is one operator and stays legible.
        /// </para>
        /// </summary>
        private static void RenderDisplayMath(OpenXmlElement mathPara, Paragraph paragraph,
            DocxRenderContext context)
        {
            var seg = BuildMathSegment(mathPara, paragraph, context);
            if (seg?.Math == null) return;

            var box = seg.Math;
            double avail = context.ContentWidth;
            double scale = 1.0;

            if (box.Width > avail && box.Width > 0)
            {
                scale = avail / box.Width;
                if (scale < MathTypesetting.MathLayoutMetrics.MinDisplayMathScale)
                {
                    DocxDiagnostics.Report("ParagraphRenderer.RenderDisplayMath", string.Format(
                        CultureInfo.InvariantCulture,
                        "a display equation is {0:F1}× the column width; it was drawn at the {1:F2} "
                            + "minimum scale and still overflows",
                        box.Width / avail, MathTypesetting.MathLayoutMetrics.MinDisplayMathScale));
                    scale = MathTypesetting.MathLayoutMetrics.MinDisplayMathScale;
                }
            }

            double width = box.Width * scale;
            double height = box.Height * scale;
            double depth = box.Depth * scale;

            string jc = Justification(mathPara);
            double x = jc switch
            {
                "left" => context.ContentLeft,
                "right" => context.ContentLeft + avail - width,
                _ => context.ContentLeft + (avail - width) / 2,   // center / centerGroup / absent
            };

            if (context.NeedsNewPage(height + depth)) context.BreakFlow();
            context.AdvanceCursor(height);

            double baseline = context.CursorY;
            context.EmitTaggedLine(() =>
            {
                var ops = new StringBuilder();
                if (scale < 1.0)
                {
                    // Scale about the equation's own origin, so the anchor stays where it was
                    // placed and the whole formula shrinks towards it.
                    ops.Append("q\n");
                    ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F4} 0 0 {0:F4} {1:F2} {2:F2} cm\n",
                        scale, x, baseline);
                    box.Draw(ops, 0, 0, new MathTypesetting.MathDrawContext(context));
                    ops.Append("Q\n");
                }
                else
                {
                    box.Draw(ops, x, baseline, new MathTypesetting.MathDrawContext(context));
                }
                if (ops.Length > 0) context.AppendContent(ops.ToString());
            });

            if (depth > 0) context.AdvanceCursor(depth);
        }

        private static string Justification(OpenXmlElement mathPara)
        {
            var pr = mathPara.ChildElements.FirstOrDefault(e => e.LocalName == "oMathParaPr");
            var jc = pr?.ChildElements.FirstOrDefault(e => e.LocalName == "jc");
            return OoxmlAttribute.Value(jc, "val");
        }

        /// <summary>
        /// Typesets an <c>m:oMath</c> / <c>m:oMathPara</c> into an atomic segment, at the
        /// surrounding paragraph's own font and size.
        /// </summary>
        private static TextSegment BuildMathSegment(OpenXmlElement math, Paragraph paragraph,
            DocxRenderContext context)
        {
            var resolver = context.StyleResolver;
            // The equation is set at the paragraph's own size and family, so it matches the text it
            // sits in rather than a size of its own.
            double size = resolver.ResolveFontSizeInPoints(null, paragraph, context.Options.DefaultFontSize);
            if (size <= 0) size = context.Options.DefaultFontSize;

            string family = resolver.ResolveFontName(null, paragraph) ?? StandardFonts.Helvetica;

            // m:oMathPara is display style: fractions set at full size and n-ary limits go
            // above and below rather than beside.
            bool display = math.LocalName == DocxLayoutMetrics.OMathParaElement;

            var style = MathTypesetting.MathStyle.Root(family, size, display, "000000");
            var box = new MathTypesetting.MathBoxBuilder(context).Build(math, style);
            if (box == null || (box.Width <= 0 && box.Height <= 0)) return null;

            return new TextSegment
            {
                IsMath = true,
                Math = box,
                PdfFontName = resolver.MapToPdfFontName(family, false, false),
                FontSize = size,
            };
        }

        /// <param name="segments">The paragraph's segments (or what is left of them), in logical order.</param>
        /// <param name="maxWidth">The width a line may fill.</param>
        /// <param name="resolver">Measures each piece, as the draw will.</param>
        /// <param name="hardEnds">
        /// When given, one entry a line: whether it ended at a line break (<c>w:br</c>) rather than at the width.
        /// A line break is consumed here, so a caller that wraps a line's successors again needs it to put the
        /// breaks back (<see cref="Unwrapped"/>).
        /// </param>
        private List<List<TextSegment>> WrapSegments(List<TextSegment> segments,
            double maxWidth, StyleResolver resolver, List<bool> hardEnds = null)
        {
            var lines = new List<List<TextSegment>>();
            var current = new List<TextSegment>();
            double currentWidth = 0;

            void PushLine(bool hard = false)
            {
                lines.Add(current); hardEnds?.Add(hard);
                current = new List<TextSegment>(); currentWidth = 0;
            }
            // Every measurement below goes through seg.TextWidth, which is the SAME arithmetic
            // TextSegment.Width uses at emit time (w:w scale and w:spacing tracking included), so a
            // scaled or tracked run is wrapped against the width it is drawn at.
            void AddPiece(string text, TextSegment seg)
            {
                var piece = seg.CloneStyle();
                piece.Text = text;
                current.Add(piece);
                currentWidth += seg.TextWidth(text, resolver);
            }

            foreach (var segment in segments)
            {
                if (segment.IsLineBreak) { PushLine(hard: true); continue; }

                // A positional tab — keep it on the current line; its width is resolved
                // at emit time against the line's accumulated x position.
                if (segment.IsTab) { current.Add(segment); currentWidth += segment.Width(resolver); continue; }
                if (segment.IsControl) { current.Add(segment); continue; }

                // A Unicode run: word-wrap its text, shaping each wrapped line as a unit
                // (so bidi orders the whole line right-to-left). This lets long Hebrew/
                // Arabic paragraphs wrap instead of overrunning the margin.
                //
                // Measured through Width, scale and tracking included: a line's bare shaped width would
                // wrap a w:w-scaled Hebrew run at its unscaled width and draw past the margin — and the
                // piece carries its Text, so its Width includes the tracking the draw emits as Tc and a
                // right-aligned line sits on its edge.
                if (segment.IsUnicode)
                {
                    foreach (var (piece, breakBefore) in UnicodeText.WrapWords(
                                 segment.Text, segment.ShapedTextWidth, currentWidth, current.Count > 0, maxWidth))
                    {
                        if (breakBefore) PushLine();
                        var shaped = UnicodeText.Shape(piece, segment.Program, segment.FontSize, segment.Kerning,
                                                       segment.ContextualAlternates, segment.Ligatures);
                        shaped.RecordInto(segment.FontEntry.UsedGidToUnicode, segment.FontEntry.UsedGidToText);
                        var gs = segment.CloneStyle();
                        gs.Text = piece;
                        gs.IsGlyph = true; gs.Glyphs = shaped; gs.EmbeddedResName = segment.FontEntry.ResName;
                        // Kept so the line's bidi pass can shape a piece of it again (InVisualOrder).
                        gs.Program = segment.Program; gs.FontEntry = segment.FontEntry;
                        current.Add(gs);
                        currentWidth += gs.Width(resolver);
                    }
                    continue;
                }

                // An equation is atomic for the same reason a shaped glyph run is: it has already
                // been laid out as a unit and cannot be split between lines.
                if (segment.IsMath)
                {
                    double mw = segment.Width(resolver);
                    if (current.Count > 0 && currentWidth + mw > maxWidth) PushLine();
                    current.Add(segment);
                    currentWidth += mw;
                    continue;
                }

                // A glyph (embedded-font) segment is atomic — it's already shaped and
                // bidi-ordered; wrap it as one indivisible piece.
                if (segment.IsGlyph)
                {
                    double gw = segment.Width(resolver);
                    if (current.Count > 0 && currentWidth + gw > maxWidth) PushLine();
                    current.Add(segment);
                    currentWidth += gw;
                    continue;
                }

                if (string.IsNullOrEmpty(segment.Text)) continue;

                // A segment's spaces are its own, kept wherever they stand but at a line break
                // (UnicodeText.SpacedWords); none are synthesised between adjacent segments (so two
                // runs "Hello"+"World" stay "HelloWorld").
                var buf = new StringBuilder();
                double bufWidth = 0;

                void FlushBuf()
                {
                    if (buf.Length == 0) return;
                    AddPiece(buf.ToString(), segment);
                    buf.Clear();
                    bufWidth = 0;
                }

                foreach (var (spaces, word) in UnicodeText.SpacedWords(segment.Text))
                {
                    if (word.Length == 0)
                    {
                        // Spaces closing the segment stay on its line.
                        buf.Append(spaces);
                        bufWidth += segment.TextWidth(spaces, resolver);
                        break;
                    }

                    double wordWidth = segment.TextWidth(word, resolver);

                    // A single token wider than the whole content box: hard-break it at
                    // the character level so it can't overrun the right margin.
                    if (wordWidth > maxWidth && word.Length > 1)
                    {
                        FlushBuf();
                        if (current.Count > 0) PushLine();
                        foreach (var chunk in BreakWord(word, maxWidth, w => segment.TextWidth(w, resolver)))
                        {
                            double cw = segment.TextWidth(chunk, resolver);
                            if (current.Count > 0 && currentWidth + cw > maxWidth) PushLine();
                            AddPiece(chunk, segment);
                        }
                        continue;   // the spaces before the word went with the break
                    }

                    string token = spaces + word;
                    double tokenWidth = segment.TextWidth(token, resolver);

                    if (currentWidth + bufWidth + tokenWidth <= maxWidth || (current.Count == 0 && buf.Length == 0))
                    {
                        buf.Append(token);
                        bufWidth += tokenWidth;
                    }
                    else
                    {
                        FlushBuf();
                        PushLine();
                        buf.Append(word);
                        bufWidth = wordWidth;
                    }
                }

                FlushBuf();
            }

            if (current.Count > 0) { lines.Add(current); hardEnds?.Add(false); }
            return lines;
        }

        /// <summary>
        /// The pieces of <paramref name="lines"/> from <paramref name="from"/> on, in order, with a line break
        /// after each line that ended at one — what to wrap again at another width.
        /// </summary>
        private static List<TextSegment> Unwrapped(List<List<TextSegment>> lines, List<bool> hardEnds, int from)
        {
            var rest = new List<TextSegment>();
            for (int k = from; k < lines.Count; k++)
            {
                rest.AddRange(lines[k]);
                if (hardEnds[k]) rest.Add(new TextSegment { IsLineBreak = true });
            }
            return rest;
        }

        /// <summary><paramref name="line"/> in left-to-right visual order (<see cref="LineBidi"/>).</summary>
        private static List<TextSegment> InVisualOrder(List<TextSegment> line, LineBidi.Paragraph paragraph, bool rtlParagraph)
            => LineBidi.InVisualOrder(line, paragraph, rtlParagraph, LogicalText,
                seg => seg.IsTab || seg.IsMath || seg.IsControl,
                OneLevelPiece);

        /// <summary>What a segment stands for in its paragraph's text, before wrapping and after (<see cref="LineBidi"/>).</summary>
        private static string LogicalText(TextSegment seg)
            => seg.IsLineBreak ? "" : seg.IsTab ? "\t" : seg.IsMath ? LineBidi.ObjectReplacement : seg.Text ?? "";

        /// <summary>
        /// <paramref name="text"/>, a one-level part of <paramref name="seg"/>, as drawn at
        /// <paramref name="level"/>: a glyph piece shaped in the level's direction (the segment
        /// itself when it is whole, outside an override, and its own shaping already read that way),
        /// a standard-14 piece through <see cref="LineBidi.AsDrawn"/>.
        /// </summary>
        private static TextSegment OneLevelPiece(TextSegment seg, string text, int level, bool overridden, bool wholeSegment)
        {
            if (seg.IsGlyph)
            {
                if (wholeSegment && !overridden && LineBidi.ShapedAsLevel(text, level)) return seg;
                var shaped = UnicodeText.Shape(text, seg.Program, seg.FontSize, seg.Kerning, seg.ContextualAlternates,
                    seg.Ligatures, LineBidi.DirectionOf(level), overridden);
                shaped.RecordInto(seg.FontEntry.UsedGidToUnicode, seg.FontEntry.UsedGidToText);
                var piece = seg.CloneStyle();
                piece.Text = text;
                piece.IsGlyph = true; piece.Glyphs = shaped; piece.EmbeddedResName = seg.EmbeddedResName;
                piece.Program = seg.Program; piece.FontEntry = seg.FontEntry;
                return piece;
            }
            if (wholeSegment && !LineBidi.IsRightToLeft(level)) return seg;
            var plain = seg.CloneStyle();
            plain.Text = LineBidi.AsDrawn(text, level);
            return plain;
        }

        // Splits a single word into the largest character chunks that each fit
        // within maxWidth (a chunk may still exceed it only for a single glyph) — a body
        // paragraph's and a table cell's alike, each measuring with its own segment.
        internal static List<string> BreakWord(string word, double maxWidth, Func<string, double> width)
        {
            var chunks = new List<string>();
            var cur = new StringBuilder();
            double curWidth = 0;
            foreach (char ch in word)
            {
                // Measure each character once and accumulate, rather than re-measuring the whole growing
                // chunk — `width(cur.ToString())` on every character was O(chunk²) glyph lookups and
                // allocated a fresh prefix string each time (≈40 GB for a 20 M-character word, minutes of
                // CPU). The widths are additive here (StandardFontMetrics sums per-glyph advances and run
                // spacing is per character), so the sum equals the whole-chunk width exactly.
                double chWidth = width(ch.ToString());
                if (cur.Length > 0 && curWidth + chWidth > maxWidth)
                {
                    chunks.Add(cur.ToString());
                    cur.Clear();
                    curWidth = 0;
                }
                cur.Append(ch);
                curWidth += chWidth;
            }
            if (cur.Length > 0) chunks.Add(cur.ToString());
            return chunks;
        }

        /// <param name="x">The line's drawn origin — indent, first-line indent and alignment shift
        /// already applied.</param>
        /// <param name="tabOrigin">
        /// The paragraph's <b>text-start edge</b> (the un-indented, un-aligned column origin), which
        /// is what <c>w:tab/@w:pos</c> is measured from. Defaults to <paramref name="x"/> for the
        /// degenerate case of a caller that has no separate origin. Resolving stops against
        /// <paramref name="x"/> displaced every stop by whatever offset the line carried: a
        /// paragraph indented 1" with a right stop at 6.5" (a normal ToC line) put the stop 1" past
        /// the right margin, and a centred paragraph put the same stop somewhere different on every
        /// line.
        /// </param>
        /// <param name="segments">The line's runs, already measured and ordered for display.</param>
        /// <param name="y">Baseline position of the line.</param>
        /// <param name="context">Render state: the style resolver, the content stream and the page.</param>
        /// <param name="wordSpacing">
        /// Extra space added per space character, from justification. 0 for every other alignment.
        /// </param>
        /// <param name="rightToLeft">
        /// A right-to-left paragraph's line: placed from its right end, <paramref name="tabOrigin"/>
        /// the right margin.
        /// </param>
        private void EmitTextLine(List<TextSegment> segments, double x, double y,
            DocxRenderContext context, double wordSpacing = 0, double? tabOrigin = null, bool rightToLeft = false)
        {
            var resolver = context.StyleResolver;
            double tabX = tabOrigin ?? x;

            // Per-segment x positions (segments flow left→right by their DRAWN width — see
            // DrawnWidth for why word spacing has to be part of that).
            // A tab advances to its next stop; a right/decimal tab right-aligns the text
            // that follows it (up to the next tab) so it ends at the stop.
            var xs = new double[segments.Count];
            double cursor = x;
            if (rightToLeft)
            {
                // The mirror image, for a right-to-left paragraph: the visual order placed from the
                // line's right end leftwards, each tab's stop measured from the right margin
                // (tabOrigin) and a right or decimal tab ending the text that follows it — to its
                // left — at the stop. With no tab the positions are the left-to-right loop's.
                foreach (var seg in segments) cursor += DrawnWidth(seg, resolver, wordSpacing);
                for (int i = segments.Count - 1; i >= 0; i--)
                {
                    var seg = segments[i];
                    if (seg.IsTab)
                    {
                        ResolveTabStop(seg, tabX - cursor, out double stopPos, out int align, out char leader);
                        seg.ResolvedLeader = leader;
                        double target = tabX - stopPos;
                        seg.TabWidth = Math.Max(0, cursor - target - AlignedOffset(segments, i, rightToLeft: true, align, resolver, wordSpacing));
                        cursor -= seg.TabWidth;
                    }
                    else cursor -= DrawnWidth(seg, resolver, wordSpacing);
                    xs[i] = cursor;
                }
            }
            else for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg.IsTab)
                {
                    ResolveTabStop(seg, cursor - tabX, out double stopPos, out int align, out char leader);
                    seg.ResolvedLeader = leader;
                    double target = tabX + stopPos;
                    double tw = Math.Max(0, target - cursor - AlignedOffset(segments, i, rightToLeft: false, align, resolver, wordSpacing));
                    seg.TabWidth = tw;
                    xs[i] = cursor;
                    cursor += tw;
                }
                else { xs[i] = cursor; cursor += DrawnWidth(seg, resolver, wordSpacing); }
            }

            // Link annotations over hyperlink segments.
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg.LinkUri == null) continue;
                double w = DrawnWidth(seg, resolver, wordSpacing);
                if (w > 0)
                    context.RegisterTextLink(xs[i], y, w, seg.FontSize, seg.LinkUri);
            }

            // 1) Highlight rectangles (path ops — must be outside any BT/ET).
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (seg.Highlight == null) continue;
                double w = DrawnWidth(seg, resolver, wordSpacing);
                if (w <= 0) continue;
                RunRenderer.TryParseHexColor(seg.Highlight, out int hr, out int hg, out int hb);
                var hl = new StringBuilder();
                hl.AppendFormat(CultureInfo.InvariantCulture, "q\n{0:F3} {1:F3} {2:F3} rg\n", hr / 255f, hg / 255f, hb / 255f);
                hl.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2} re f\nQ\n",
                    xs[i], y - seg.FontSize * 0.22, w, seg.FontSize * 1.05);
                context.AppendContent(hl.ToString());
            }

            // 1.5) Equations. Drawn outside the text object, at their absolute slot, because a
            // formula is paths and several text objects rather than a run of glyphs. Highlights are
            // already down, so an equation sits above them and below the surrounding text — the
            // same order an ordinary run would take.
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!seg.IsMath || seg.Math == null) continue;

                var mathOps = new StringBuilder();
                seg.Math.Draw(mathOps, xs[i], y, new MathTypesetting.MathDrawContext(context));
                if (mathOps.Length > 0) context.AppendContent(mathOps.ToString());
            }

            // 2) Text.
            // From the leftmost segment's place: x itself left to right, and right to left wherever
            // the tabs, resolved from the right margin, put the line's left end.
            var sb = new StringBuilder();
            sb.AppendFormat(CultureInfo.InvariantCulture, "BT\n{0:F2} {1:F2} Td\n", segments.Count > 0 ? xs[0] : x, y);
            if (wordSpacing != 0)
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} Tw\n", wordSpacing);
            double appliedRise = 0, appliedTc = 0;
            int appliedTz = 100;
            int segIndex = -1;
            foreach (var seg in segments)
            {
                segIndex++;
                if (seg.IsControl) continue;   // a direction control has ordered the line and draws nothing
                sb.Append(RunRenderer.FormatColorOperator(seg.Color) ?? "0 0 0 rg\n");

                if (seg.IsTab)
                {
                    // Neutralise rise / spacing / scale so the tab's advance is exact.
                    if (appliedRise != 0) { sb.Append("0 Ts\n"); appliedRise = 0; }
                    if (appliedTc != 0) { sb.Append("0 Tc\n"); appliedTc = 0; }
                    if (appliedTz != 100) { sb.Append("100 Tz\n"); appliedTz = 100; }
                    EmitTab(sb, seg, xs[segIndex], context);
                    continue;
                }

                if (seg.BaselineShift != appliedRise)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} Ts\n", seg.BaselineShift);
                    appliedRise = seg.BaselineShift;
                }
                // The spacing under the scale: Tz scales Tc, Word's pitch is absolute (RunRenderer.EmittedCharSpacing).
                double emittedTc = RunRenderer.EmittedCharSpacing(seg.CharSpacing, seg.TextScale);
                if (emittedTc != appliedTc)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} Tc\n", emittedTc);
                    appliedTc = emittedTc;
                }
                if (seg.TextScale != appliedTz)
                {
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0} Tz\n", seg.TextScale);
                    appliedTz = seg.TextScale;
                }

                if (seg.IsMath)
                {
                    // The equation was drawn above; here the text object only has to step over the
                    // space it occupies, exactly as a tab does. Both the drawn position and this
                    // skip read seg.Width(resolver), so they cannot disagree.
                    if (appliedRise != 0) { sb.Append("0 Ts\n"); appliedRise = 0; }
                    if (appliedTc != 0) { sb.Append("0 Tc\n"); appliedTc = 0; }
                    if (appliedTz != 100) { sb.Append("100 Tz\n"); appliedTz = 100; }

                    var mathFont = context.RegisterFont(seg.PdfFontName ?? StandardFonts.Helvetica);
                    double mathSize = seg.FontSize > 0 ? seg.FontSize : context.Options.DefaultFontSize;
                    sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", mathFont, mathSize);
                    sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F2}] TJ\n",
                        -seg.Width(resolver) / mathSize * PdfUnits.GlyphSpaceUnitsPerEm);
                }
                else if (seg.IsGlyph)
                {
                    context.UseEmbeddedFontOnCurrentPage(seg.EmbeddedResName);
                    sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", seg.EmbeddedResName, seg.FontSize);
                    sb.Append(UnicodeText.ShowText(seg.Glyphs, seg.FontSize)).Append('\n');
                }
                else
                {
                    var fontRes = context.RegisterFont(seg.PdfFontName);
                    sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", fontRes, seg.FontSize);
                    sb.AppendFormat("({0}) Tj\n", RunRenderer.EncodeWinAnsiLiteral(seg.Text));
                }
            }
            if (appliedRise != 0) sb.Append("0 Ts\n");
            if (appliedTc != 0) sb.Append("0 Tc\n");
            if (appliedTz != 100) sb.Append("100 Tz\n");
            if (wordSpacing != 0) sb.Append("0 Tw\n");
            sb.Append("ET\n");

            // 3) Underline / strikethrough rules.
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!seg.Underline && !seg.Strike) continue;
                double w = DrawnWidth(seg, resolver, wordSpacing);
                if (w <= 0) continue;
                float r = 0, g = 0, b = 0;
                if (seg.Color != null && RunRenderer.TryParseHexColor(seg.Color, out int rr, out int gg, out int bb))
                { r = rr / 255f; g = gg / 255f; b = bb / 255f; }
                double rb = y + seg.BaselineShift;
                if (seg.Underline) AppendRule(sb, xs[i], xs[i] + w, rb - seg.FontSize * 0.12, seg.FontSize * 0.05, r, g, b);
                if (seg.Strike) AppendRule(sb, xs[i], xs[i] + w, rb + seg.FontSize * 0.28, seg.FontSize * 0.05, r, g, b);
            }

            context.AppendContent(sb.ToString());
        }

        /// <summary>
        /// A segment's width <b>as drawn</b> — including the stretch a justified line's
        /// <c>Tw</c> adds to every space it contains.
        ///
        /// <para>
        /// <see cref="TextSegment.Width"/> knows nothing about word spacing, yet the <c>xs[]</c>
        /// array it fed drives the /Link rectangles, the highlight rectangles, the equation
        /// placement, the right/decimal tab's look-ahead sum and the underline/strike rules. On a
        /// justified line with ten spaces stretched by 3 pt, the last segment's glyphs are drawn
        /// 30 pt to the right of where all five were placed: the underline sat under the preceding
        /// words and clicking the visible link did nothing.
        /// </para>
        ///
        /// <para>
        /// Glyph (Identity-H) segments are excluded on exactly the terms the <c>wordSpacing</c>
        /// calculation excludes them — ISO 32000-1 §9.3.3 applies <c>Tw</c> to the single-byte
        /// character code 32, never to a byte 32 inside a multiple-byte code.
        /// </para>
        /// </summary>
        private static double DrawnWidth(TextSegment seg, StyleResolver resolver, double wordSpacing)
        {
            double w = seg.Width(resolver);
            if (wordSpacing == 0 || seg.IsGlyph || seg.Text == null) return w;
            return w + wordSpacing * seg.Text.Count(c => c == ' ');
        }

        // Finds the next tab stop strictly past `rel` (points from the paragraph's text-start edge).
        /// <summary>
        /// How far into the text a tab stop aligns — that text being the segments after the tab on its
        /// own side, up to the next tab: to the right of it left to right, to the left of it right to
        /// left — measured from the tab's side: nothing for a left stop, which the text starts at; half
        /// its width for a centre stop; all of it for a right stop, which it ends at; for a decimal stop
        /// the width before its first period, which is placed at the stop (as a right stop without one).
        /// </summary>
        /// <remarks>
        /// <para>Read from Word 16's output for a <c>w:bidi</c> paragraph with one stop of each kind at
        /// 2 in: every alignment is the mirror image of a left-to-right paragraph's, and a
        /// decimal stop puts the period at the stop whichever side of it the number lies — so the part
        /// before the period is the part visually left of it in both directions.</para>
        /// <para>A centre stop is not read as a left one, nor a decimal stop as a right one, in either
        /// direction.</para>
        /// </remarks>
        private static double AlignedOffset(List<TextSegment> segments, int tab, bool rightToLeft, int align,
            StyleResolver resolver, double wordSpacing)
        {
            if (align == 0) return 0;
            int first, last;   // the aligned text, in visual (left-to-right) order
            if (rightToLeft)
            {
                last = tab - 1;
                first = last;
                while (first >= 0 && !segments[first].IsTab) first--;
                first++;
            }
            else
            {
                first = tab + 1;
                last = first;
                while (last < segments.Count && !segments[last].IsTab) last++;
                last--;
            }

            double width = 0;
            for (int k = first; k <= last; k++) width += DrawnWidth(segments[k], resolver, wordSpacing);
            if (align == 1) return width / 2;
            if (align == 3)
            {
                double before = 0;
                for (int k = first; k <= last; k++)
                {
                    var s = segments[k];
                    int period = s.IsMath || s.IsControl || s.Text == null ? -1 : s.Text.IndexOf('.');
                    if (period >= 0)
                    {
                        string head = s.Text.Substring(0, period);
                        before += s.IsGlyph ? s.ShapedTextWidth(head) : s.TextWidth(head, resolver);
                        return rightToLeft ? width - before : before;
                    }
                    before += DrawnWidth(s, resolver, wordSpacing);
                }
            }
            return width;
        }

        private static void ResolveTabStop(TextSegment seg, double rel, out double pos, out int align, out char leader)
        {
            if (seg.TabStops != null)
            {
                foreach (var t in seg.TabStops)
                    if (t.PosPt > rel + 0.1) { pos = t.PosPt; align = t.Align; leader = t.Leader; return; }
            }
            double d = seg.DefaultTab > 0 ? seg.DefaultTab : StyleResolver.DefaultTabIntervalPt;
            pos = (Math.Floor(rel / d) + 1) * d;
            align = 0; leader = '\0';
        }

        // Advances the text position by the tab's width, drawing the leader (dots/etc.)
        // when the matched stop has one. Uses a TJ position adjustment for an exact advance.
        /// <remarks>
        /// The leader is drawn only over the part of the tab that lies on the page, left of its right
        /// edge (<paramref name="x"/> is where the tab starts); the advance is the whole tab, so what
        /// follows lands where it does. The stop's position is the file's — up to two billion twips — so
        /// one leader string of the tab's width over the leader's would be tens of millions of characters
        /// a tab, for a stop no page reaches.
        /// </remarks>
        private static void EmitTab(StringBuilder sb, TextSegment seg, double x, DocxRenderContext context)
        {
            double tw = seg.TabWidth;
            if (tw <= 0.01) return;
            double size = seg.FontSize > 0 ? seg.FontSize : context.Options.DefaultFontSize;
            string fontName = seg.PdfFontName ?? StandardFonts.Helvetica;
            var fontRes = context.RegisterFont(fontName);
            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", fontRes, size);

            if (seg.ResolvedLeader != '\0')
            {
                string lc = seg.ResolvedLeader.ToString();
                double lcw = context.StyleResolver.MeasureTextWidth(lc, fontName, size);
                if (lcw > 0)
                {
                    double onPage = Math.Max(0, Math.Min(tw, context.PageWidth - x));
                    int count = SaturatingCast.ToInt(onPage / lcw);
                    if (count > 0)
                        sb.AppendFormat("({0}) Tj\n", RunRenderer.EncodeWinAnsiLiteral(new string(seg.ResolvedLeader, count)));
                    double rem = tw - count * lcw;
                    if (rem > 0.01)
                        sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F2}] TJ\n",
                                        -rem / size * PdfUnits.GlyphSpaceUnitsPerEm);
                    return;
                }
            }
            sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F2}] TJ\n",
                            -tw / size * PdfUnits.GlyphSpaceUnitsPerEm);
        }

        private static void AppendRule(StringBuilder sb, double x1, double x2, double y,
            double thickness, float r, float g, float b)
        {
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F3} {1:F3} {2:F3} RG\n{3:F2} w\n{4:F2} {5:F2} m\n{6:F2} {5:F2} l\nS\n",
                r, g, b, Math.Max(0.3, thickness), x1, y, x2);
        }

        private class TextSegment
        {
            public string Text { get; set; }
            public string PdfFontName { get; set; }
            public double FontSize { get; set; }
            public string Color { get; set; }
            public bool IsLineBreak { get; set; }

            // A pre-shape Unicode run (word-wrapped + shaped per line during wrapping).
            public bool IsUnicode { get; set; }
            public Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram Program { get; set; }
            public DocxRenderContext.EmbeddedFontEntry FontEntry { get; set; }

            // A shaped (Identity-H) glyph run — the emitted form.
            public bool IsGlyph { get; set; }
            public UnicodeText.GlyphRun Glyphs { get; set; }
            public string EmbeddedResName { get; set; }

            // Decorations / positioning.
            public bool Underline { get; set; }
            public bool Strike { get; set; }
            public string Highlight { get; set; }
            public double BaselineShift { get; set; }
            public double CharSpacing { get; set; }
            public int TextScale { get; set; } = 100;
            public bool Kerning { get; set; }
            public bool ContextualAlternates { get; set; }
            public bool Ligatures { get; set; }
            public string LinkUri { get; set; }

            // A positional tab: advances to the next tab stop at emit time (and draws a
            // leader if the matched stop has one). TabWidth is filled in by EmitTextLine.
            public bool IsTab { get; set; }
            public System.Collections.Generic.List<StyleResolver.TabInfo> TabStops { get; set; }
            public double DefaultTab { get; set; }
            public double TabWidth { get; set; }
            public char ResolvedLeader { get; set; }

            // A typeset equation, laid out as an atomic box that participates in the line like a
            // very tall glyph. Null for ordinary text.
            public bool IsMath { get; set; }
            public MathTypesetting.MathBox Math { get; set; }

            // A w:dir or w:bdo's control (Text): no width, never drawn; LineBidi orders by it.
            public bool IsControl { get; set; }

            /// <summary>
            /// How far this segment reaches above the baseline.
            ///
            /// <para>
            /// For ordinary text this is <b>arithmetically identical</b> to the
            /// <c>FontSize × SingleLineFactor</c> the line height was computed from before, so a
            /// paragraph containing no math emits byte-for-byte what it always did. That invariance
            /// is the whole point of the seam and is pinned by a test.
            /// </para>
            /// </summary>
            public double Ascent => IsMath && Math != null
                ? Math.Height
                : FontSize * DocxLayoutMetrics.SingleLineFactor;

            /// <summary>How far it hangs below. Zero for ordinary text, so nothing moves.</summary>
            public double Descent => IsMath && Math != null ? Math.Depth : 0;

            public double Width(StyleResolver resolver)
            {
                if (IsControl) return 0;
                if (IsMath) return Math?.Width ?? 0;
                if (IsTab) return TabWidth > 0 ? TabWidth : DefaultTab;
                double w = IsGlyph ? (Glyphs?.WidthPts ?? 0)
                         : IsUnicode ? UnicodeText.MeasureWidth(Text, Program, FontSize, Kerning, ContextualAlternates, Ligatures)
                         : resolver.MeasureTextWidth(Text, PdfFontName, FontSize);
                return WithRunSpacing(w, Text);
            }

            /// <summary>
            /// The drawn width of <paramref name="text"/> under <b>this segment's</b> style.
            ///
            /// <para>
            /// Exists because the wrap loop measures candidate substrings of a segment that does
            /// not carry them yet, and a bare <c>resolver.MeasureTextWidth</c> would ignore
            /// <c>w:w</c> (emitted as <c>Tz</c>) and <c>w:spacing</c> (emitted as <c>Tc</c>), both of
            /// which <see cref="Width"/> applies at emit time. A run at 200% scale would be wrapped to
            /// half the width it draws at and run a full column past the right margin, while the
            /// alignment offset — which does read <see cref="Width"/> — would mis-centre it by the
            /// same amount. One definition,
            /// two entry points.
            /// </para>
            /// </summary>
            public double TextWidth(string text, StyleResolver resolver)
                => WithRunSpacing(resolver.MeasureTextWidth(text, PdfFontName, FontSize), text);

            /// <summary>
            /// <see cref="TextWidth"/> for a run that will be shaped: <paramref name="text"/>
            /// measured through <see cref="Program"/> rather than a standard-14 metric, then scaled
            /// and tracked the same way.
            /// </summary>
            public double ShapedTextWidth(string text)
                => WithRunSpacing(UnicodeText.MeasureWidth(text, Program, FontSize, Kerning, ContextualAlternates, Ligatures), text);

            // The arithmetic itself is RunRenderer.WithRunSpacing — shared with the table path, so
            // both draw the same run at the same width.
            private double WithRunSpacing(double w, string text)
                => RunRenderer.WithRunSpacing(w, text, TextScale, CharSpacing);

            public TextSegment CloneStyle() => new TextSegment
            {
                PdfFontName = PdfFontName, FontSize = FontSize, Color = Color,
                Underline = Underline, Strike = Strike, Highlight = Highlight,
                BaselineShift = BaselineShift, CharSpacing = CharSpacing, TextScale = TextScale, Kerning = Kerning,
                ContextualAlternates = ContextualAlternates, Ligatures = Ligatures, LinkUri = LinkUri,
            };
        }
    }
}
