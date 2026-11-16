// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Util;
using WParagraph = DocumentFormat.OpenXml.Wordprocessing.Paragraph;
using WRun = DocumentFormat.OpenXml.Wordprocessing.Run;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;
using WTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WTableRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using WTableCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;
using WBreak = DocumentFormat.OpenXml.Wordprocessing.Break;
using WVerticalMerge = DocumentFormat.OpenXml.Wordprocessing.VerticalMerge;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>Exports a Document to DOCX using OpenXml SDK.</summary>
    public sealed class DocxExporter : IDocumentExporter
    {
        public void Export(Model.Document document, Stream output)
        {
            // Cleared per export, on every path. This exporter did not report anything until it
            // gained the background-image decline below, and without the clear a caller reading
            // BuilderDiagnostics.LastError after a DOCX export would be handed whatever the last
            // PDF export on this thread had left there — the stale-slot failure CLAUDE.md records
            // against LastPermsValidation, which was [ThreadStatic] and still wrong.
            BuilderDiagnostics.Clear();
            XmlText.BeginDocument();
            Model.ModelMeasurements.Validate(document);
            _reportedUnmaintainedList = false;

            using var wordDoc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document, true);
            var mainPart = wordDoc.AddMainDocumentPart();
            var body = new Body();

            // The footnotes part is created lazily by the first note, so a document with none
            // writes no part and its bytes are unchanged.
            _footnotes = new DocxFootnotes(mainPart);
            _endnotes = new DocxFootnotes(mainPart, endnotes: true);

            // A list Word will renumber needs a w:numPr per paragraph and a numbering.xml part to
            // point it at. Allocated lazily: a document with no list writes no part, so its bytes
            // are unchanged.
            var numbering = new DocxNumbering(mainPart);

            var sections = document.Sections.ToList();
            for (int s = 0; s < sections.Count; s++)
            {
                var section = sections[s];
                // Through the shared resolver, not `== RightToLeft`: that comparison answers
                // "left-to-right" for TextDirection.Auto, so an auto-detected Hebrew section came
                // out reordered and right-aligned in the PDF and left-to-right here.
                bool sectionRtl = Model.ModelRules.IsRightToLeft(section);
                foreach (var child in Model.ModelRules.InFlow(section.Children))
                {
                    if (child is Model.Paragraph para)
                    {
                        ReportFloatDropped(para);
                        body.Append(BuildParagraph(para, EffectiveRtl(para, section)));
                    }
                    else if (child is Model.Table table)
                        body.Append(BuildTable(table, sectionRtl, numbering, 0));
                    else if (child is Model.ListItem li)
                        body.Append(BuildListItem(li, sectionRtl));
                    else if (child is Model.ListBlock lb)
                        AppendList(body, lb, sectionRtl, numbering);
                    else if (child is Model.TocBlock toc)
                        AppendToc(body, toc, document, sectionRtl);
                }

                // Section properties (page size, margins). Dimensions are clamped to
                // valid twip ranges so a negative/NaN/huge caller value can't wrap
                // the unsigned cast into a garbage page size.
                var secProps = new SectionProperties(
                    new PageSize
                    {
                        Width = TwipsU(section.PageWidthPt),
                        Height = TwipsU(section.PageHeightPt)
                    },
                    new PageMargin
                    {
                        Top = TwipsI(section.MarginTop),
                        Bottom = TwipsI(section.MarginBottom),
                        Left = TwipsU(section.MarginLeft),
                        Right = TwipsU(section.MarginRight)
                    });
                // <w:bidi/> makes the section's layout right-to-left (column order, the gutter's side).
                if (sectionRtl || section.RightToLeftLayout) secProps.Append(new BiDi());

                // WordprocessingML: only the FINAL section's sectPr may be a direct
                // child of <body>; each earlier section's properties must live in the
                // ParagraphProperties of that section's last paragraph.
                if (s == sections.Count - 1)
                    body.Append(secProps);
                else
                    body.Append(new WParagraph(new ParagraphProperties(secProps)));
            }

            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(body);

            // Cleared so a later export on this thread cannot write into this document's part --
            // and so a footnote met outside an export is reported rather than silently placed.
            _footnotes = null;
            _endnotes = null;
        }

        private static uint TwipsU(double pt)
        {
            if (double.IsNaN(pt) || pt <= 0) return 0;
            double tw = PdfUnits.PointsToTwips(pt);
            return tw >= uint.MaxValue ? uint.MaxValue : SaturatingCast.ToUInt(tw);
        }

        private static int TwipsI(double pt)
        {
            if (double.IsNaN(pt) || pt <= 0) return 0;
            double tw = PdfUnits.PointsToTwips(pt);
            return SaturatingCast.ToInt(tw);
        }

        private static bool EffectiveRtl(Model.Paragraph para, Model.Section section)
            => Model.ModelRules.IsRightToLeft(para, section);

        /// <summary>Indent added per nesting level, matching the PDF exporter's 18 pt.</summary>
        private const double ListLevelIndentPt = 18;

        /// <summary>DOCX measures indents in twentieths of a point.</summary>
        private const int TwipsPerPoint = 20;

        /// <summary>
        /// Writes a list as paragraphs, natively numbered through <paramref name="numbering"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Flattened either way, and that is not a loss here.</b> OOXML has no list element:
        /// a list <i>is</i> a run of paragraphs sharing a numbering id, each carrying its own level,
        /// so the flat walk is the format's own shape rather than a compromise. What changes is
        /// whether the level is written as <c>w:ilvl</c> — which Word maintains — or as
        /// an indent with the marker typed in front of it, which it does not.</para>
        ///
        /// <para><b>An item with its own <c>Marker</c> gets an instance whose level text IS that
        /// marker</b>, sharing the list's definition so the items around it keep their ordinals.
        /// <see cref="DocxNumbering.LiteralMarkerInstance"/> answers null only for a marker no
        /// level text can carry, and that one item takes the typed-run path.</para>
        /// </remarks>
        private static void AppendList(OpenXmlCompositeElement into, Model.ListBlock list,
                                       bool containerRtl, DocxNumbering numbering)
        {
            int? numId = numbering.Allocate(list);

            foreach (var entry in Model.ModelRules.Flatten(list))
            {
                int? itemNumId = numId;
                if (numId.HasValue && entry.Item.Marker != null)
                    itemNumId = numbering.LiteralMarkerInstance(
                        numId.Value, DocxNumbering.LevelFor(entry.Item.Level), entry.Item.Marker);

                into.Append(BuildListItem(entry.Item, containerRtl, entry.Marker, itemNumId));
            }
        }

        private static WParagraph BuildListItem(Model.ListItem li, bool sectionRtl,
                                               string? marker = null, int? numId = null)
        {
            // Both overrides the model documents were ignored here: an explicit Marker ("1.2.",
            // "א.") was replaced by a number derived from IsNumbered, and Runs — which override
            // Text when non-empty — were dropped entirely, so an item whose content lived in Runs
            // exported as a bare bullet with its whole text missing.
            bool rtl = Model.ModelRules.IsRightToLeft(
                li.Direction ?? (sectionRtl ? Model.TextDirection.RightToLeft : Model.TextDirection.LeftToRight),
                li.Text);

            var wp = new WParagraph();
            var pProps = new ParagraphProperties();

            if (numId.HasValue)
            {
                // Native numbering: Word owns the marker and the indent, so writing either here
                // would fight the definition -- a literal marker would appear beside the generated
                // one, and an explicit indent would override the level's own. CT_PPrBase puts
                // numPr before bidi.
                pProps.Append(new NumberingProperties(
                    new NumberingLevelReference { Val = DocxNumbering.LevelFor(li.Level) },
                    new NumberingId { Val = numId.Value }));
                if (rtl) pProps.Append(new BiDi());

                wp.Append(pProps);
                AppendRuns(wp, Model.ModelRules.ListRuns(li), rtl);
                return wp;
            }

            // No definition: a loose ListItem with no container, or a Marker no Word level text can
            // carry (DocxNumbering.CanBeLevelText). The marker is typed in front of the text and
            // Word will not maintain it -- reported once per document, because a caller who chose
            // such a marker has chosen this and does not need it repeated per item.
            ReportUnmaintainedList();

            // CT_PPrBase puts bidi before ind.
            if (rtl) pProps.Append(new BiDi());

            // The level indent, so a nested item is indented in Word as the PDF and HTML exporters
            // indent it, not flush with its parent.
            int twips = SaturatingCast.ToInt(li.Level * ListLevelIndentPt * TwipsPerPoint);
            if (twips > 0)
            {
                pProps.Append(rtl
                    ? new Indentation { Right = twips.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : new Indentation { Left = twips.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            wp.Append(pProps);

            var markerRun = new WRun();
            if (rtl) markerRun.Append(new RunProperties(new RightToLeftText()));
            markerRun.Append(new WText(XmlText.ForOpenXml(marker ?? Model.ModelRules.ListMarker(li)))
                { Space = SpaceProcessingModeValues.Preserve });
            wp.Append(markerRun);

            AppendRuns(wp, Model.ModelRules.ListRuns(li), rtl);
            return wp;
        }

        /// <summary>
        /// Writes a table of contents as a Word <c>TOC</c> field with the entries this document
        /// has today as its cached result.
        /// </summary>
        /// <remarks>
        /// <para><b>A field plus a cached result, because Word shows the cache until it is
        /// told to update.</b> The field is what makes it a contents list Word owns — F9
        /// rebuilds it with real page numbers from its own layout — and the paragraphs
        /// between <c>begin</c> and <c>end</c> are what every other reader sees, and what Word
        /// itself shows before anyone presses anything.</para>
        ///
        /// <para><b>No page numbers in the cache.</b> This exporter has no layout to take them
        /// from, and a number here would be about the PDF export's pagination rather than about
        /// the document Word is going to lay out. The entries are text; Word supplies numbers when
        /// it updates the field.</para>
        /// </remarks>
        private static void AppendToc(
            OpenXmlCompositeElement into, Model.TocBlock toc, Model.Document document, bool rtl)
        {
            var entries = TocEntries.Collect(document, toc.MaxLevel);
            if (entries.Count == 0) return;

            if (!string.IsNullOrEmpty(toc.Title))
            {
                var title = new WParagraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "TOCHeading" }));
                title.Append(new WRun(new WText(XmlText.ForOpenXml(toc.Title!))
                    { Space = SpaceProcessingModeValues.Preserve }));
                into.Append(title);
            }

            // \o "1-N" : build from outline levels 1..N. \h : make the entries hyperlinks.
            string instruction = "TOC \\o \"1-"
                + toc.MaxLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\" \\h";

            var begin = new WParagraph();
            begin.Append(new WRun(new FieldChar { FieldCharType = FieldCharValues.Begin }));
            begin.Append(new WRun(new FieldCode(instruction)
                { Space = SpaceProcessingModeValues.Preserve }));
            begin.Append(new WRun(new FieldChar { FieldCharType = FieldCharValues.Separate }));
            into.Append(begin);

            foreach (var entry in entries)
            {
                var wp = new WParagraph(new ParagraphProperties(
                    new ParagraphStyleId { Val = "TOC" + entry.Level.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) }));

                if (rtl) wp.ParagraphProperties!.Append(new BiDi());

                wp.Append(new WRun(new WText(XmlText.ForOpenXml(entry.Text))
                    { Space = SpaceProcessingModeValues.Preserve }));
                into.Append(wp);
            }

            var end = new WParagraph();
            end.Append(new WRun(new FieldChar { FieldCharType = FieldCharValues.End }));
            into.Append(end);
        }

        /// <summary>
        /// Whether this export has already said that a list is written with literal markers.
        /// </summary>
        /// <remarks>
        /// <b>Per thread, and cleared at the start of every export</b> — CLAUDE.md's rule for
        /// a slot a caller reads afterwards. Once per document rather than once per item: a caller
        /// who set an explicit marker chose this, and a message repeated per item is one they learn
        /// to filter.
        /// </remarks>
        [System.ThreadStatic] private static bool _reportedUnmaintainedList;

        /// <summary>
        /// Reports, once, that a list was written with typed markers instead of a numbering
        /// definition.
        /// </summary>
        /// <remarks>
        /// The two ways to get here are a loose <c>ListItem</c> with no container — which has
        /// no list for a definition to describe — and an item whose <c>Marker</c> no Word level
        /// text can carry: one containing <c>%</c>, which Word reads as a counter placeholder and
        /// cannot escape, or a control character. Every other explicit marker becomes the level
        /// text of its own numbering instance (<see cref="DocxNumbering.LiteralMarkerInstance"/>)
        /// and is not reported, because nothing was lost.
        /// </remarks>
        private static void ReportUnmaintainedList()
        {
            if (_reportedUnmaintainedList) return;
            _reportedUnmaintainedList = true;

            BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                "a list item was written with its marker as literal text rather than through a "
                + "Word numbering definition, so Word will not renumber or maintain it. This "
                + "happens for a loose ListItem with no ListBlock around it, and for a Marker no "
                + "level text can carry: one containing '%' (Word's counter placeholder, which has "
                + "no escape) or a control character.");
        }

        /// <summary>Reports an <c>Opacity</c> DOCX cannot express (<see cref="BuilderDiagnostics.ReportDroppedOpacity"/>).</summary>
        private static void ReportDroppedOpacity(double opacity, string what)
            => BuilderDiagnostics.ReportDroppedOpacity(opacity, what, "DOCX has no constant-alpha property for it");

        /// <summary>
        /// Says so when a floated paragraph is written as an ordinary block.
        /// </summary>
        /// <remarks>
        /// <para><b>This format has no float this exporter writes.</b> A real one means a framed or
        /// anchored object in DOCX's own vocabulary, which is a separate piece of work; until then a
        /// floated paragraph becomes a block in the flow, so text that would have run alongside it
        /// runs underneath instead.</para>
        ///
        /// <para><b>Reported rather than silent, because the two look identical in the output.</b>
        /// A caller who set <c>Paragraph.Float</c> and got a stacked document has no way to tell
        /// the property was dropped from the property never having applied. The PDF and HTML
        /// exports honour it, so the same model produces different layouts across formats — which
        /// is exactly the case a diagnostic exists for.</para>
        ///
        /// <para>Fires only for a paragraph that really asked to float, so it cannot go off on a
        /// document that never used the feature.</para>
        /// </remarks>
        private static void ReportFloatDropped(Model.Paragraph para)
        {
            if (para.Float == Model.FloatSide.None) return;

            BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                "float: a floated paragraph was written as an ordinary block, because DOCX has no "
                + "float this exporter writes. Text that would have run alongside it runs below it. "
                + "The PDF and HTML exports honour Paragraph.Float.");
        }

        private static WParagraph BuildParagraph(Model.Paragraph para, bool rtl)
        {
            // WordprocessingML has no paragraph background image — <w:shd> carries a colour and a
            // pattern, not a picture — so the content is dropped here while the PDF and HTML
            // exports still carry it. Saying so is the whole point: "the same model exported to
            // four formats" is exactly when a caller needs to be told which one lost something.
            if (para.BackgroundImage?.Data is { Length: > 0 } bgBytes)
                BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                    $"a paragraph background image of {bgBytes.Length:N0} bytes was dropped: DOCX has no " +
                    "paragraph background-image property (w:shd carries a colour, not a picture).");

            // Same rule, same reason. WordprocessingML has no paragraph-level alpha at all: the
            // nearest thing is w14:textFill on a RUN, which fades the glyph fill only and would
            // leave shading and borders opaque — a partial rendering of the request is worse than
            // an honest refusal, because nothing would tell the caller the two do not match.
            ReportDroppedOpacity(para.Opacity, "a paragraph");

            var wp = new WParagraph();

            // Paragraph properties: alignment + (RTL) base direction.
            var pProps = new ParagraphProperties();
            // <w:bidi/> sets the paragraph base direction to RTL (so the default alignment
            // becomes right and runs lay out right-to-left).
            if (rtl) pProps.Append(new BiDi());
            // Start/End are logical and Start is the model's DEFAULT, so mapping them to Left would
            // give every paragraph an explicit <w:jc w:val="left"/> that overrides the right
            // alignment <w:bidi/> gives an RTL paragraph. The shared resolver returns null for the
            // direction's natural alignment, so an unset alignment stays unset in the file.
            var resolved = Model.ModelRules.ResolvePhysicalAlignment(para.Alignment, rtl);
            if (resolved.HasValue)
            {
                pProps.Append(new Justification
                {
                    Val = resolved.Value switch
                    {
                        Model.TextAlignment.Center => JustificationValues.Center,
                        Model.TextAlignment.Right => JustificationValues.Right,
                        Model.TextAlignment.Justify => JustificationValues.Both,
                        _ => JustificationValues.Left,
                    }
                });
            }
            wp.Append(pProps);

            AppendRuns(wp, para.Runs, rtl);
            return wp;
        }

        /// <summary>
        /// Writes a footnote as a Word footnote reference, placing the note in the footnotes part.
        /// </summary>
        /// <remarks>
        /// <para><b>A reference, not text.</b> The body carries a <c>w:footnoteReference</c> naming
        /// an id and the note itself lives in a separate part — writing the note's words
        /// inline would put them in the middle of the sentence, which is the one place a footnote
        /// must not be.</para>
        ///
        /// <para><b>The note's own runs go through this same writer.</b> That is what gives them
        /// the formatting, the right-to-left handling and the diagnostics body text gets; a second
        /// run writer for notes is the kind of copy that drifts.</para>
        ///
        /// <para><b>Word numbers the marks itself</b>, from the order of the references in the
        /// document, so <c>FootnoteNumbering</c>'s mark is not written for an ordinary note; the two
        /// agree. <b>An explicit <c>Mark</c> is written as Word's own custom mark</b>: §17.11.14's
        /// <c>w:customMarkFollows</c> on the reference says the run's following text is the mark,
        /// and <see cref="DocxFootnotes"/> writes the same text inside the note in place of the
        /// automatic number. A custom mark needs the attribute, not a style.</para>
        /// </remarks>
        private static void AppendFootnoteReference(WParagraph wp, Model.Footnote note, bool rtl)
        {
            // An endnote is the same reference into the other part (§17.11.3): w:endnoteReference,
            // the EndnoteReference style, the endnotes.xml part.
            bool endnote = note.Placement == Model.NotePlacement.DocumentEnd;
            DocxFootnotes? part = endnote ? _endnotes : _footnotes;
            if (part == null)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                    "a footnote was dropped: it appeared outside the document body, where this "
                    + "exporter writes no footnotes part.");
                return;
            }

            // A note's content is written through this writer too, and may reference a note: a chain
            // of distinct notes recursed once a level until the stack overflowed (IdFor stops only a
            // note re-entering itself). Bounded at the exporters' shared nesting ceiling, as the ODT
            // exporter bounds it.
            if (_noteDepth >= PdfLimits.MaxTableNestingDepth)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    "Footnotes nested more than " + PdfLimits.MaxTableNestingDepth + " deep were not written.");
                return;
            }

            int id = part.IdFor(note, paragraph =>
            {
                _noteDepth++;
                try { AppendRuns(paragraph, note.Runs, rtl); }
                finally { _noteDepth--; }
            });

            OpenXmlElement noteReference;
            if (endnote)
            {
                var endnoteReference = new EndnoteReference { Id = id };
                if (note.Mark != null) endnoteReference.CustomMarkFollows = true;
                noteReference = endnoteReference;
            }
            else
            {
                var footnoteReference = new FootnoteReference { Id = id };
                if (note.Mark != null) footnoteReference.CustomMarkFollows = true;
                noteReference = footnoteReference;
            }

            var reference = new WRun(noteReference);
            reference.PrependChild(new RunProperties(
                new RunStyle { Val = endnote ? "EndnoteReference" : "FootnoteReference" }));

            // The mark itself, in the same run and directly after the reference -- which is where
            // w:customMarkFollows says Word will find it.
            if (note.Mark != null)
                reference.Append(new WText(XmlText.ForOpenXml(note.Mark)) { Space = SpaceProcessingModeValues.Preserve });

            wp.Append(reference);
        }

        /// <summary>The footnotes part of the export in progress, or null outside one.</summary>
        /// <remarks>
        /// <b>[ThreadStatic] because the write chain is static and an export is a call, not an
        /// object.</b> Two threads exporting two documents would otherwise share one part and
        /// interleave their notes. Assigned at the top of every export, on every path, and cleared
        /// at the end — the rule CLAUDE.md states for every slot a caller reads afterwards.
        /// </remarks>
        [System.ThreadStatic] private static DocxFootnotes? _footnotes;

        /// <summary>The endnotes part of the export in progress, or null outside one; see <see cref="_footnotes"/>.</summary>
        [System.ThreadStatic] private static DocxFootnotes? _endnotes;

        /// <summary>How many notes' contents are being written, one inside another, on this thread.</summary>
        [System.ThreadStatic] private static int _noteDepth;

        /// <summary>
        /// Writes a run list into a paragraph, reporting anything WordprocessingML cannot carry
        /// through this exporter rather than dropping it in silence.
        /// </summary>
        /// <remarks>
        /// An <see cref="Model.ImageRun"/> matched no arm and vanished — while the paragraph
        /// <b>background</b> image, which matters far less, was already reported. A caller asking
        /// "did anything go wrong?" after exporting a document with a figure was told no.
        /// </remarks>
        private static void AppendRuns(WParagraph wp, System.Collections.Generic.IEnumerable<Model.IInlineElement> runs, bool rtl)
        {
            foreach (var run in runs)
            {
                if (run is Model.TextRun tr)
                {
                    ReportDroppedOpacity(tr.Opacity, "a text run");
                    var wr = new WRun();
                    wr.Append(BuildRunProps(tr, rtl));
                    wr.Append(new WText(XmlText.ForOpenXml(tr.Text)) { Space = SpaceProcessingModeValues.Preserve });
                    wp.Append(wr);
                }
                else if (run is Model.Footnote note) AppendFootnoteReference(wp, note, rtl);
                else if (run is Model.LineBreak) wp.Append(new WRun(new WBreak()));
                else if (run is Model.TabRun) wp.Append(new WRun(new TabChar()));
                else if (run is Model.PageBreak) wp.Append(new WRun(new WBreak { Type = BreakValues.Page }));
                else if (run is Model.ImageRun img)
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                        (img.Drawing != null ? "an inline vector drawing" : $"an inline image of {img.Data?.Length ?? 0:N0} bytes") + " was dropped: this DOCX " +
                        "exporter writes no image parts" +
                        (string.IsNullOrEmpty(img.Alt) ? "." : $" (alt: '{img.Alt}')."));
                else
                    BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat,
                        $"an inline element of type {run.GetType().Name} was dropped: the DOCX " +
                        "exporter has no mapping for it.");
            }
        }

        private static RunProperties BuildRunProps(Model.TextRun tr, bool rtl)
        {
            var rProps = new RunProperties();
            if (tr.IsBold) rProps.Append(new Bold());
            if (tr.IsItalic) rProps.Append(new Italic());
            if (tr.IsUnderline) rProps.Append(new Underline { Val = UnderlineValues.Single });
            if (tr.IsStrikethrough) rProps.Append(new Strike());
            if (tr.IsDoubleStrike) rProps.Append(new DoubleStrike());
            if (tr.IsAllCaps) rProps.Append(new Caps());
            if (tr.IsSmallCaps) rProps.Append(new SmallCaps());
            if (rtl) rProps.Append(new RightToLeftText());
            if (tr.FontSize != Model.DocDefaults.FontSizePt)
                rProps.Append(new FontSize { Val = (SaturatingCast.ToInt(tr.FontSize * PdfUnits.HalfPointsPerPoint)).ToString() });
            if (!string.IsNullOrEmpty(tr.Color) && tr.Color != Model.DocDefaults.TextColorHex)
                rProps.Append(new DocumentFormat.OpenXml.Wordprocessing.Color { Val = tr.Color.TrimStart('#') });
            // Arbitrary highlight colour via run shading (w:highlight only allows named colours).
            if (!string.IsNullOrEmpty(tr.Highlight))
                rProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = tr.Highlight.TrimStart('#') });
            if (tr.VerticalAlign == Model.VerticalAlign.Superscript)
                rProps.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Superscript });
            else if (tr.VerticalAlign == Model.VerticalAlign.Subscript)
                rProps.Append(new VerticalTextAlignment { Val = VerticalPositionValues.Subscript });
            if (tr.CharSpacing != 0)
                rProps.Append(new Spacing { Val = SaturatingCast.ToInt(PdfUnits.PointsToTwips(tr.CharSpacing)) });
            if (tr.HorizontalScale != 100)
                rProps.Append(new CharacterScale { Val = tr.HorizontalScale });
            return rProps;
        }

        /// <summary>
        /// The table's grid (CT_Tbl requires a <c>w:tblGrid</c> between <c>w:tblPr</c> and the rows): one
        /// column per grid column, the widest row's cells with their spans; each column's width from
        /// <see cref="Model.Table.ColumnWidths"/> when it states every column's, else an equal share of
        /// the table's width, or of a six-and-a-half-inch band when it states none.
        /// </summary>
        private static TableGrid BuildGrid(Model.Table table)
        {
            int columns = 1;
            foreach (var row in table.Rows)
            {
                int spanned = 0;
                foreach (var cell in row.Cells) spanned += Math.Max(1, cell.GridSpan);
                columns = Math.Max(columns, spanned);
            }

            const double DefaultBandPt = 468;
            double bandPt = table.WidthPt is double w && w > 0 ? w : DefaultBandPt;
            bool stated = table.ColumnWidths.Count >= columns && table.ColumnWidths.Take(columns).All(c => c > 0);

            var grid = new TableGrid();
            for (int i = 0; i < columns; i++)
            {
                double pt = stated ? table.ColumnWidths[i] : bandPt / columns;
                grid.Append(new GridColumn
                {
                    Width = SaturatingCast.ToInt(PdfUnits.PointsToTwips(pt)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }
            return grid;
        }

        /// <param name="table">The table to write.</param>
        /// <param name="rtl">Whether the containing section reads right-to-left.</param>
        /// <param name="numbering">
        /// Carried through so a list inside a cell is numbered natively too. A cell is an ordinary
        /// block container to this model, and a list that lost its numbering by being in one would
        /// be a difference nothing in the document explains.
        /// </param>
        /// <param name="depth">
        /// How many tables enclose this one. A cell may hold its own table, so the recursion is bounded
        /// at <see cref="PdfLimits.MaxTableNestingDepth"/> — the ceiling the PDF exporter already uses.
        /// </param>
        private static WTable BuildTable(Model.Table table, bool rtl, DocxNumbering numbering, int depth)
        {
            var wt = new WTable();
            // A cell may hold its own table (a self-reference, or a long distinct chain): every re-entry
            // carries depth + 1, so both are refused at the same ceiling the PDF path uses. A cycle guard
            // (a "being built" set) would pass the chain; only a depth bounds the uncatchable overflow.
            if (depth > PdfLimits.MaxTableNestingDepth)
            {
                BuilderDiagnostics.Report(BuilderDiagnosticKind.ContentApproximated,
                    "Tables nested more than " + PdfLimits.MaxTableNestingDepth + " deep were not written.");
                return wt;
            }
            var tProps = new TableProperties();
            // <w:bidiVisual/> mirrors the column order so cell 0 is the rightmost column; CT_TblPr puts
            // it before tblBorders.
            if (rtl) tProps.Append(new BiDiVisual());
            // CT_TblBorders: top, left, bottom, right, insideH, insideV.
            tProps.Append(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }));
            wt.Append(tProps);
            wt.Append(BuildGrid(table));

            foreach (var row in table.Rows)
            {
                var wr = new WTableRow();
                foreach (var cell in row.Cells)
                {
                    var wc = new WTableCell();
                    var tcProps = new TableCellProperties();
                    if (cell.GridSpan > 1) tcProps.Append(new GridSpan { Val = cell.GridSpan });
                    if (cell.VMerge == Model.VerticalMerge.Restart)
                        tcProps.Append(new WVerticalMerge { Val = MergedCellValues.Restart });
                    else if (cell.VMerge == Model.VerticalMerge.Continue)
                        tcProps.Append(new WVerticalMerge { Val = MergedCellValues.Continue });
                    if (!string.IsNullOrEmpty(cell.BackgroundColor))
                        tcProps.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = cell.BackgroundColor.TrimStart('#') });
                    if (cell.VAlign == Model.CellVerticalAlign.Center)
                        tcProps.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
                    else if (cell.VAlign == Model.CellVerticalAlign.Bottom)
                        tcProps.Append(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Bottom });
                    // Baseline is written as Word's top (w:vAlign has top, center, bottom and both).
                    if (tcProps.HasChildren) wc.Append(tcProps);

                    bool any = false;
                    foreach (var block in Model.ModelRules.InFlow(cell.Content()))
                    {
                        if (block is Model.Paragraph p) { wc.Append(BuildParagraph(p, rtl)); any = true; }
                        else if (block is Model.Table nt) { wc.Append(BuildTable(nt, rtl, numbering, depth + 1)); wc.Append(new WParagraph()); any = true; }
                        else if (block is Model.ListItem li) { wc.Append(BuildListItem(li, rtl)); any = true; }
                        else if (block is Model.ListBlock lb) { AppendList(wc, lb, rtl, numbering); any = true; }
                    }
                    if (!any) wc.Append(new WParagraph()); // a cell needs at least one paragraph
                    wr.Append(wc);
                }
                wt.Append(wr);
            }
            return wt;
        }
    }
}
