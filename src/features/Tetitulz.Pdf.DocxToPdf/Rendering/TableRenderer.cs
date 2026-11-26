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

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal sealed class TableRenderer : IDocxElementRenderer
    {
        private const double DefaultLeftPad = 4.0;
        private const double DefaultVertPad = 2.0;
        // Guard against pathological nesting. Shared with the Builder mapping path
        // (DocxToBuilderMapper), so both cap the same concept at one value.
        private static int MaxNestingDepth => PdfLimits.MaxTableNestingDepth;

        /// <summary>
        /// Set when table content was dropped (e.g. a nested table exceeding the
        /// nesting-depth guard). Follows the project's diagnostics pattern; null
        /// when the last render dropped nothing.
        /// </summary>
        internal static string LastError
        {
            get => _lastError;
            private set
            {
                _lastError = value;
                // Forwarded so one conversion has one place to read. This slot predates
                // DocxDiagnostics and is kept because callers use it; setting it here means no
                // call site had to move. A reset to null is not an anomaly, so it is not reported.
                if (value != null) DocxDiagnostics.Report("TableRenderer", value);
            }
        }

        [ThreadStatic]
        private static string _lastError;

        public void Render(OpenXmlElement element, DocxRenderContext context)
        {
            // Cleared at the start of every render, exactly as ListRenderer.Render does. LastError
            // documents itself as "null when the last render dropped nothing", and nothing was
            // clearing it - so one over-deep nested table anywhere in a document was reported
            // against every table rendered afterwards, and against the next conversion entirely.
            LastError = null;

            if (element is not Table table) return;

            double tableWidth = ResolveTableWidth(table, context.ContentWidth);
            _layoutLeft = context.ContentLeft;
            double tableX = _layoutLeft + TableStartOffset(table, tableWidth, context.ContentWidth, context.StyleResolver);
            var layout = LayoutTable(table, tableX, tableWidth, context, depth: 0);
            if (layout.Rows.Count == 0) return;

            // Page-break between top-level rows; a row taller than a whole page is
            // split across pages at block boundaries. Nested tables flow within
            // their cell: each nested row is a block, cut into page-sized slices
            // when it is taller than a page (NestedRowBlocks).
            foreach (var row in layout.Rows)
                DrawRowPaginated(row, context);
        }

        /// <summary>The column left the table being drawn was laid out against (<see cref="DrawTopLevelSegment"/>).</summary>
        private double _layoutLeft;

        /// <summary>
        /// How far from the column's left edge a table's box starts: <c>w:tblInd</c> from the start edge,
        /// and <c>w:jc</c> against the room its width leaves (ECMA-376 Part 1 §17.4.28, §17.4.29) — centred,
        /// or at the end edge. A <c>w:bidiVisual</c> table's start edge is the column's right, so its indent
        /// and its start alignment are measured from there.
        /// </summary>
        internal static double TableStartOffset(Table table, double tableWidth, double available, StyleResolver resolver)
        {
            var tblPr = resolver.TablePropertiesOf(table);
            double room = Math.Max(0, available - tableWidth);
            bool rtl = MirrorsColumns(tblPr);

            double indent = 0;
            var ind = tblPr?.TableIndentation;
            string w = ind == null ? null : OoxmlAttribute.Value(ind, "w");
            if (w != null && double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double twips)
                && !double.IsNaN(twips) && !double.IsInfinity(twips))
                indent = PdfUnits.TwipsToPoints(twips);

            string jc = tblPr?.TableJustification?.Val?.InnerText;
            switch (jc)
            {
                case "center": return room / 2;
                case "right": return rtl ? 0 : room;
                case "left": return rtl ? room : 0;
                case "end": return rtl ? 0 : room;
                default: return rtl ? room - indent : indent;   // start, or none
            }
        }

        /// <summary>
        /// A top-level row segment, drawn in the column the flow is in now. The table is laid out once, against the
        /// column it starts in, so a row that flows into the next column of a multi-column section would be drawn at
        /// the first column's x, over its content. The segment is drawn translated by how far
        /// the column has moved, its links registered under the same shift. A nested table's rows are drawn inside
        /// their cell's segment and are not shifted again.
        /// </summary>
        private void DrawTopLevelSegment(RowLayout row, double top, double bandHeight,
            bool drawTop, bool drawBottom, (int start, int count)[] ranges, DocxRenderContext ctx)
        {
            double dx = ctx.ContentLeft - _layoutLeft;
            if (dx == 0)
            {
                DrawRowSegment(row, top, bandHeight, drawTop, drawBottom, ranges, ctx);
                return;
            }
            ctx.AppendContent(string.Format(CultureInfo.InvariantCulture, "q\n1 0 0 1 {0:F2} 0 cm\n", dx));
            ctx.LinkShiftX = dx;
            try { DrawRowSegment(row, top, bandHeight, drawTop, drawBottom, ranges, ctx); }
            finally
            {
                ctx.LinkShiftX = 0;
                ctx.AppendContent("Q\n");
            }
        }

        // ==================================================================
        //  Layout model — a table is laid out into a fixed width and produces
        //  rows of known height, each able to draw itself at a given top-Y.
        //  This makes nested tables (a table inside a cell) just another block.
        // ==================================================================

        private sealed class TableLayout
        {
            public List<RowLayout> Rows = new List<RowLayout>();
            public double TotalHeight => Rows.Sum(r => r.Height);
        }

        private sealed class RowLayout
        {
            public double Height;
            public List<CellLayout> Cells = new List<CellLayout>();
        }

        private sealed class CellLayout
        {
            public double X, Width;
            public StyleResolver.CellBorders Borders;
            public bool SuppressTop;
            /// <summary>The cell below continues this one's vertical merge, so the edge between them is not drawn.</summary>
            public bool SuppressBottom;
            /// <summary>The grid column the cell starts at, and how many it spans — what a vertical merge matches on.</summary>
            public int GridColumn, GridSpan;
            public int VAlign; // 0 top, 1 center, 2 bottom
            public string Shading; // background fill hex (no #), or null
            public List<CellBlock> Blocks = new List<CellBlock>();
            public double ContentHeight => Blocks.Sum(b => b.Height);
        }

        private abstract class CellBlock
        {
            public double Height;
            public abstract void Draw(double topY, DocxRenderContext ctx, TableRenderer owner);
        }

        // A single wrapped text line drawn at X with its own per-run segments.
        private sealed class TextLineBlock : CellBlock
        {
            public double X;
            public List<CellSeg> Segs = new List<CellSeg>();

            public override void Draw(double topY, DocxRenderContext ctx, TableRenderer owner)
            {
                if (Segs.Count == 0) return;
                // Raised by the line's deepest descent — an equation's depth; text's is none, so its baseline is
                // the line's foot. A line holding an equation is its ascent plus its depth tall, and with the
                // baseline at the foot the whole depth would hang below the line, past the cell's padding and over
                // the next row's border.
                double depth = 0;
                foreach (var seg in Segs) depth = Math.Max(depth, seg.Descent);
                double baseline = topY - Height + depth;

                // Equations are paths and their own text objects, so they are drawn before the
                // line's text object opens — the same order the body renderer uses. A hyperlink's
                // segment gets its link here too (RegisterTextLink, the body line's box).
                double mathCursor = X;
                bool anyMath = false;
                var mathOps = new StringBuilder();
                foreach (var seg in Segs)
                {
                    if (seg.IsMath && seg.Math != null)
                    {
                        seg.Math.Draw(mathOps, mathCursor, baseline,
                            new MathTypesetting.MathDrawContext(ctx));
                        anyMath = true;
                    }
                    double width = seg.Width(ctx.StyleResolver);
                    if (seg.LinkUri != null && width > 0)
                        ctx.RegisterTextLink(mathCursor, baseline, width, seg.Size, seg.LinkUri);
                    mathCursor += width;
                }
                if (anyMath) ctx.AppendContent(mathOps.ToString());

                var sb = new StringBuilder();
                sb.AppendFormat(CultureInfo.InvariantCulture, "BT\n{0:F2} {1:F2} Td\n", X, baseline);
                // Tc / Tz track what has actually been emitted, so a run that does not change them
                // costs no operator — and so the state is reset before ET rather than leaking into
                // the next line's text object. The same shape the body renderer uses.
                double appliedTc = 0, appliedRise = 0;
                int appliedTz = 100;
                foreach (var seg in Segs)
                {
                    if (seg.IsControl) continue;   // a direction control has ordered the line and draws nothing
                    sb.Append(RunRenderer.FormatColorOperator(seg.Color) ?? "0 0 0 rg\n");
                    if (seg.IsMath)
                    {
                        // Already drawn; step the text cursor over its slot, as a tab does.
                        // Neutralise spacing and scale first so the advance is exactly the box.
                        if (appliedRise != 0) { sb.Append("0 Ts\n"); appliedRise = 0; }
                        if (appliedTc != 0) { sb.Append("0 Tc\n"); appliedTc = 0; }
                        if (appliedTz != 100) { sb.Append("100 Tz\n"); appliedTz = 100; }
                        var mathFont = ctx.RegisterFont(seg.Font ?? StandardFonts.Helvetica);
                        double size = seg.Size > 0 ? seg.Size : ctx.Options.DefaultFontSize;
                        sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", mathFont, size);
                        sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:F2}] TJ\n",
                            -seg.Width(ctx.StyleResolver) / size * PdfUnits.GlyphSpaceUnitsPerEm);
                    }
                    else
                    {
                        // Emitted for text only, and only on change: CellSeg.Width has already
                        // measured with both, so the wrap the layout chose and the width the page
                        // draws are the same number. A note mark's rise likewise.
                        if (seg.BaselineShift != appliedRise)
                        {
                            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} Ts\n", seg.BaselineShift);
                            appliedRise = seg.BaselineShift;
                        }
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

                        if (seg.IsGlyph)
                        {
                            ctx.UseEmbeddedFontOnCurrentPage(seg.EmbeddedResName);
                            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", seg.EmbeddedResName, seg.Size);
                            sb.Append(UnicodeText.ShowText(seg.Glyphs, seg.Size)).Append('\n');
                        }
                        else
                        {
                            var fontRes = ctx.RegisterFont(seg.Font);
                            sb.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", fontRes, seg.Size);
                            sb.AppendFormat("({0}) Tj\n", RunRenderer.EncodeWinAnsiLiteral(seg.Text));
                        }
                    }
                }
                // Reset before ET so nothing leaks into the next text object.
                if (appliedRise != 0) sb.Append("0 Ts\n");
                if (appliedTc != 0) sb.Append("0 Tc\n");
                if (appliedTz != 100) sb.Append("100 Tz\n");
                sb.Append("ET\n");
                ctx.AppendContent(sb.ToString());
            }
        }

        // A drawing (picture, shape, SmartArt) inside a cell. Cell content is laid out as a stack
        // of blocks rather than through the body's flow cursor, so the drawing is placed at an
        // absolute position — see DrawingRenderer.RenderAt.
        private sealed class DrawingBlock : CellBlock
        {
            public double X;
            public double MaxWidth;
            public OpenXmlElement Drawing;

            public override void Draw(double topY, DocxRenderContext ctx, TableRenderer owner)
                => owner._drawingRenderer.RenderAt(Drawing, X, topY, MaxWidth, ctx);
        }

        private readonly Drawings.DrawingRenderer _drawingRenderer = new Drawings.DrawingRenderer();

        // One row of a nested table, or one slice of it, drawn as a real sub-table row. Emitting nested rows as
        // individual blocks (rather than one atomic table block) lets a tall nested table split across pages at
        // its row boundaries, and a row taller than a page is cut into slices that each fit one (NestedRowBlocks)
        // rather than losing its lines past the bottom margin with nothing reported. A slice
        // can still outgrow a page only through one block that does (a picture taller than the page), and that is
        // said, not silent.
        private sealed class NestedRowBlock : CellBlock
        {
            public RowLayout Row;
            /// <summary>The blocks of each cell this slice draws; null for the whole row.</summary>
            public (int start, int count)[] Ranges;
            public bool First = true, Last = true;

            public override void Draw(double topY, DocxRenderContext ctx, TableRenderer owner)
            {
                if (Height > ctx.ContentHeight)
                    DocxDiagnostics.Report("TableRenderer.NestedRowTallerThanPage", string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "a nested table's row holds a block {0:F0} pt tall, more than a page's {1:F0} pt; what falls "
                            + "past the page's bottom margin is not shown",
                        Height, ctx.ContentHeight));
                owner.DrawRowSegment(Row, topY, Height, First, Last, Ranges ?? owner.FullRanges(Row), ctx);
            }
        }

        /// <summary>
        /// A nested row as cell blocks: whole when it fits <paramref name="budget"/>, else in slices that each do,
        /// cut where a top-level row is cut (<see cref="AssignSegment"/>), so the outer row's pagination can break
        /// between them.
        /// </summary>
        /// <param name="row">The nested row, laid out.</param>
        /// <param name="budget">The tallest slice's content, its own padding left out.</param>
        private static IEnumerable<NestedRowBlock> NestedRowBlocks(RowLayout row, double budget)
        {
            if (row.Height <= budget + 2 * DefaultVertPad || budget <= 0)
            {
                yield return new NestedRowBlock { Row = row, Height = row.Height };
                yield break;
            }

            var cursor = new int[row.Cells.Count];
            bool first = true;
            while (true)
            {
                var (ranges, consumed, allDone) = AssignSegment(row, cursor, budget);
                yield return new NestedRowBlock
                {
                    Row = row, Ranges = ranges, First = first, Last = allDone,
                    Height = consumed + 2 * DefaultVertPad,
                };
                if (allDone) yield break;
                first = false;
            }
        }

        private sealed class CellSeg
        {
            public string Text;
            public string Font;
            public double Size;
            public string Color;
            // A pre-shape Unicode (Hebrew/Arabic/…) run: word-wrapped, then shaped one line at a
            // time, exactly as a body paragraph does it. Never drawn in this form.
            public bool IsUnicode;
            public Tetitulz.Pdf.Core.Fonts.TrueTypeFontProgram Program;
            public DocxRenderContext.EmbeddedFontEntry FontEntry;

            // Embedded Unicode (Hebrew/Arabic/…) glyph run — one wrapped line of a Unicode run.
            public bool IsGlyph;
            public UnicodeText.GlyphRun Glyphs;
            public string EmbeddedResName;

            // A typeset equation, atomic like a shaped glyph run.
            public bool IsMath;
            public MathTypesetting.MathBox Math;

            // A w:dir or w:bdo's control (Text): no width, never drawn; LineBidi orders by it.
            public bool IsControl;

            // A w:br (a text-wrapping break, at the run's Size): the line ends here.
            public bool IsLineBreak;

            /// <summary>The target of the hyperlink the run lies under, or null: a link rectangle over what it draws.</summary>
            public string LinkUri;

            /// <summary><c>w:w</c> horizontal scale percent (emitted as <c>Tz</c>); 100 is unscaled.</summary>
            public int TextScale = 100;

            /// <summary><c>w:spacing</c> inter-character tracking in points (emitted as <c>Tc</c>).</summary>
            public double CharSpacing;

            /// <summary>The baseline rise, in points (emitted as <c>Ts</c>): a note mark's superscript.</summary>
            public double BaselineShift;

            /// <summary><c>w:kern</c> applies at this run's size: a shaped run is kerned.</summary>
            public bool Kerning;

            /// <summary><c>w14:cntxtAlts</c>: a shaped run takes its font's contextual alternates.</summary>
            public bool ContextualAlternates;

            /// <summary><c>w14:ligatures</c>: a shaped run forms its font's standard ligatures.</summary>
            public bool Ligatures;

            /// <summary>
            /// The segment's drawn width.
            /// </summary>
            /// <remarks>
            /// <b>Both attributes are applied here and emitted at draw time, through the same
            /// helper the body renderer uses.</b> This read neither, and the cell carried neither,
            /// so a <c>w:w</c>-scaled or <c>w:spacing</c>-tracked run rendered at one width inside
            /// a table and another outside it. An equation is excluded for the reason the body path
            /// gives: its box is already typeset, and the draw path neutralises the operators
            /// before stepping over it.
            /// </remarks>
            public double Width(StyleResolver resolver)
            {
                if (IsControl) return 0;
                if (IsMath) return Math?.Width ?? 0;
                double w = IsGlyph ? (Glyphs?.WidthPts ?? 0)
                         : resolver.MeasureTextWidth(Text, Font, Size);
                return RunRenderer.WithRunSpacing(w, Text, TextScale, CharSpacing);
            }

            /// <summary>
            /// The drawn width of <paramref name="text"/> under <b>this segment's</b> style.
            /// </summary>
            /// <remarks>
            /// <b>The wrap loop measures candidate substrings a segment does not carry yet</b>, and a
            /// bare <c>resolver.MeasureTextWidth</c> there would ignore the scale and tracking
            /// <see cref="Width"/> applies. The body renderer has the identical
            /// pair for the identical reason: one definition, two entry points. Without it a
            /// 150%-scaled cell run is wrapped to two-thirds of the width it draws at and runs past
            /// the cell edge.
            /// </remarks>
            public double TextWidth(string text, StyleResolver resolver)
                => RunRenderer.WithRunSpacing(
                    resolver.MeasureTextWidth(text, Font, Size), text, TextScale, CharSpacing);

            /// <summary>
            /// <see cref="TextWidth"/> for a run that will be shaped: measured through
            /// <see cref="Program"/>, then scaled and tracked the same way — the body paragraph's
            /// <c>ShapedTextWidth</c>, for the same wrap.
            /// </summary>
            public double ShapedTextWidth(string text)
                => RunRenderer.WithRunSpacing(
                    UnicodeText.MeasureWidth(text, Program, Size, Kerning, ContextualAlternates, Ligatures), text, TextScale, CharSpacing);

            /// <summary>
            /// This segment's style carrying <paramref name="text"/> instead.
            /// </summary>
            /// <remarks>
            /// A clone rather than a field-by-field copy at the call site, because the wrap loop
            /// rebuilds every segment it emits, and a hand-written copy silently drops each style field
            /// added after it — <see cref="TextScale"/> and <see cref="CharSpacing"/> among them — which
            /// is how a style field gets lost between measuring and drawing.
            /// </remarks>
            public CellSeg CloneStyle(string text) => new CellSeg
            {
                Text = text,
                Font = Font,
                Size = Size,
                Color = Color,
                TextScale = TextScale,
                CharSpacing = CharSpacing,
                BaselineShift = BaselineShift,
                LinkUri = LinkUri,
            };

            /// <summary>Height above the cell line's baseline; for text, the line's ascent.</summary>
            public double Ascent => IsMath && Math != null
                ? Math.Height
                : Size * DocxLayoutMetrics.CellLineFactor;

            /// <summary>Depth below it. Zero for text, so no cell changes height.</summary>
            public double Descent => IsMath && Math != null ? Math.Depth : 0;
        }

        // ------------------------------------------------------------------
        //  Layout
        // ------------------------------------------------------------------

        private TableLayout LayoutTable(Table table, double tableX, double tableWidth,
            DocxRenderContext context, int depth)
        {
            var layout = new TableLayout();
            var rows = MarkupWrappers.Rows(table).ToList();
            if (rows.Count == 0) return layout;

            var colWidths = ResolveColumnWidths(table, tableWidth);
            if (colWidths.Count == 0) return layout;
            int rowCount = rows.Count, colCount = colWidths.Count;
            // RTL table (w:bidiVisual): columns run right-to-left.
            bool rtlTable = MirrorsColumns(table.GetFirstChild<TableProperties>());
            double gridWidth = colWidths.Sum();

            bool cellsDropped = false;
            for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
            {
                // At most PdfLimits.MaxDocxTableColumns cells a row: a row taller than a page strokes every cell's
                // borders on every page it spans, so cells times pages, both the file's, would write gigabytes
                // of borders.
                var cells = MarkupWrappers.Cells(rows[rowIdx]).Take(PdfLimits.MaxDocxTableColumns + 1).ToList();
                if (cells.Count > PdfLimits.MaxDocxTableColumns)
                {
                    cells.RemoveAt(cells.Count - 1);
                    cellsDropped = true;
                }
                // w:gridBefore: the row's cells start that many grid columns in (§17.4.14).
                int gridBefore = rows[rowIdx].TableRowProperties?.GetFirstChild<GridBefore>()?.Val?.Value ?? 0;
                var placed = PlaceCells(cells, colWidths, tableX, gridBefore);
                if (rtlTable)
                    for (int k = 0; k < placed.Count; k++)
                    {
                        var (c, cx, cw, ci, sp) = placed[k];
                        placed[k] = (c, 2 * tableX + gridWidth - cx - cw, cw, ci, sp);
                    }

                var rowLayout = new RowLayout();
                double maxContent = 0;
                foreach (var (cell, x, width, colIdx, span) in placed)
                {
                    var styleFmt = context.StyleResolver.ResolveTableStyleFormat(
                        table, rowIdx, rowCount, colIdx, span, colCount);
                    var blocks = LayoutCellBlocks(cell, table, x, width, context, depth, styleFmt);
                    var vMerge = cell.TableCellProperties?.VerticalMerge;
                    bool suppressTop = vMerge != null
                        && (vMerge.Val == null || vMerge.Val.Value == MergedCellValues.Continue);
                    var va = cell.TableCellProperties?.TableCellVerticalAlignment?.Val?.Value;
                    int vAlign = va == TableVerticalAlignmentValues.Center ? 1
                               : va == TableVerticalAlignmentValues.Bottom ? 2 : 0;

                    var borders = context.StyleResolver.ResolveCellBorders(
                        cell, table, rowIdx, rowCount, colIdx, span, colCount);
                    // Resolved for the cell's logical place (its left the table's left when it is the first
                    // column); a mirrored table draws it at the opposite end, so its sides change places with
                    // it. Kept as resolved, the outer borders would be stroked on interior edges and the inside
                    // ones on the table's sides.
                    if (rtlTable) (borders.Left, borders.Right) = (borders.Right, borders.Left);

                    rowLayout.Cells.Add(new CellLayout
                    {
                        X = x,
                        Width = width,
                        Borders = borders,
                        SuppressTop = suppressTop,
                        GridColumn = colIdx,
                        GridSpan = span,
                        VAlign = vAlign,
                        // Direct cell <w:shd> wins; otherwise the table style's conditional
                        // formatting (header/total row, banded rows/cols, corners) supplies it.
                        Shading = context.StyleResolver.ResolveCellShading(cell) ?? styleFmt.ShadingHex,
                        Blocks = blocks,
                    });
                    maxContent = Math.Max(maxContent, blocks.Sum(b => b.Height));
                }

                rowLayout.Height = Math.Max(
                    context.Options.DefaultFontSize * DocxLayoutMetrics.MinRowHeightFactor,
                    maxContent + 2 * DefaultVertPad);
                layout.Rows.Add(rowLayout);
            }

            if (cellsDropped)
                DocxDiagnostics.Report("TableRenderer.TooManyColumns", string.Format(CultureInfo.InvariantCulture,
                    "a table row holds more than {0} cells (PdfLimits.MaxDocxTableColumns, the most Word allows); "
                        + "the cells past it were not drawn", PdfLimits.MaxDocxTableColumns));

            // A cell the next row's cell at the same grid column continues (w:vMerge) keeps its bottom edge off,
            // as the continuation keeps its top edge off, so the cell above draws no line across the merged
            // cell.
            for (int r = 0; r + 1 < layout.Rows.Count; r++)
            {
                var aboveByColumn = new Dictionary<(int, int), CellLayout>();
                foreach (var above in layout.Rows[r].Cells) aboveByColumn[(above.GridColumn, above.GridSpan)] = above;
                foreach (var below in layout.Rows[r + 1].Cells)
                    if (below.SuppressTop && aboveByColumn.TryGetValue((below.GridColumn, below.GridSpan), out var above))
                        above.SuppressBottom = true;
            }

            return layout;
        }

        // Cell left/right margin in points (w:tcMar on the cell, else w:tblCellMar on
        // the table), falling back to the default padding.
        private static double CellMargin(TableCell cell, Table table, StyleResolver resolver, bool left)
        {
            var tcMar = cell?.TableCellProperties?.TableCellMargin;
            double? v = WidthTwips(tcMar == null ? null : (left ? (OpenXmlElement)tcMar.LeftMargin : tcMar.RightMargin));
            if (v == null)
            {
                var tblMar = resolver.TablePropertiesOf(table)?.TableCellMarginDefault;
                v = WidthTwips(tblMar == null ? null
                    : (left ? (OpenXmlElement)tblMar.TableCellLeftMargin : tblMar.TableCellRightMargin));
            }
            return v.HasValue ? PdfUnits.TwipsToPoints(v.Value) : DefaultLeftPad;
        }

        private static double? WidthTwips(OpenXmlElement marginEl)
        {
            if (marginEl == null) return null;
            var w = OoxmlAttribute.Value(marginEl, "w");
            return w != null && double.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out double tw)
                ? tw : (double?)null;
        }

        private List<CellBlock> LayoutCellBlocks(TableCell cell, Table parentTable,
            double cellX, double cellWidth, DocxRenderContext context, int depth,
            StyleResolver.TableStyleCellFormat styleFmt = default)
        {
            var blocks = new List<CellBlock>();
            double leftPad = CellMargin(cell, parentTable, context.StyleResolver, left: true);
            double rightPad = CellMargin(cell, parentTable, context.StyleResolver, left: false);
            double inner = Math.Max(1, cellWidth - leftPad - rightPad);
            double textX = cellX + leftPad;

            foreach (var child in MarkupWrappers.Blocks(cell.ChildElements))
            {
                if (child is Paragraph para)
                {
                    // A cell paragraph can carry drawings as well as text; WrapParagraph reads only
                    // the text, so a drawing here would otherwise be dropped silently — for pictures
                    // too, not just for shapes.
                    foreach (var drawing in ShownDrawings(para.ChildElements))
                    {
                        double drawnHeight = Drawings.DrawingRenderer.MeasureInline(drawing, inner);
                        if (drawnHeight <= 0) continue;
                        blocks.Add(new DrawingBlock
                        {
                            X = textX,
                            MaxWidth = inner,
                            Drawing = drawing,
                            Height = drawnHeight,
                        });
                    }

                    // Each line in visual order and placed by the paragraph's alignment, as a body
                    // paragraph's is. A cell line was drawn from the cell's left edge in logical
                    // order: a centred heading sat on the left, and a Hebrew cell sat on the left
                    // with its runs reversed.
                    bool rtl = context.StyleResolver.ResolveParagraphRtl(para);
                    var alignment = context.StyleResolver.ResolveDisplayAlignment(para);
                    var wrapped = WrapParagraph(para, inner, context, out var bidiParagraph, rtl, styleFmt);
                    foreach (var line in wrapped)
                    {
                        var segs = InVisualOrder(line.segs, bidiParagraph, rtl);
                        double width = 0;
                        foreach (var seg in segs) width += seg.Width(context.StyleResolver);
                        double slack = Math.Max(0, inner - width);
                        double x = textX + (alignment == JustificationValues.Center ? slack / 2
                                          : alignment == JustificationValues.Right ? slack : 0);
                        blocks.Add(new TextLineBlock { X = x, Segs = segs, Height = line.height });
                    }
                }
                else if (child is Table nested)
                {
                    if (depth >= MaxNestingDepth)
                    {
                        LastError = $"Nested table beyond depth {MaxNestingDepth} dropped.";
                        continue;
                    }
                    double nestedWidth = ResolveTableWidth(nested, inner);
                    var nestedLayout = LayoutTable(nested, textX, nestedWidth, context, depth + 1);
                    // A slice must fit a fresh page inside every cell around it: each level of nesting, this
                    // one's included, takes its padding top and bottom.
                    double sliceBudget = context.ContentHeight - 2 * DefaultVertPad * (depth + 2);
                    foreach (var nestedRow in nestedLayout.Rows)
                        blocks.AddRange(NestedRowBlocks(nestedRow, sliceBudget));
                }
            }

            if (blocks.Count == 0)
                blocks.Add(new TextLineBlock
                {
                    X = textX,
                    Height = context.Options.DefaultFontSize * DocxLayoutMetrics.CellLineFactor,
                });
            return blocks;
        }

        /// <summary>
        /// Whether a table's columns run right to left (<c>w:bidiVisual</c>) — the legacy renderer and the Builder
        /// mapper alike. An on/off element: present with no <c>w:val</c>, or with it on, it mirrors the columns;
        /// <c>w:val="0"</c> leaves them left to right, never read as on from the element's presence alone.
        /// </summary>
        internal static bool MirrorsColumns(TableProperties tblPr)
        {
            var bidiVisual = tblPr?.BiDiVisual;
            return bidiVisual != null && (bidiVisual.Val == null || bidiVisual.Val.Value == OnOffOnlyValues.On);
        }

        /// <summary>
        /// The drawings a cell paragraph shows: a run's own, through the hyperlinks, simple fields and marking
        /// wrappers the text walk reads through — and none inside a tracked deletion or a move's source, which
        /// <see cref="MarkupWrappers.InlineContent"/> opens to nothing, as the body paragraph's walk leaves them.
        /// </summary>
        /// <remarks>
        /// <c>Descendants&lt;Drawing&gt;()</c> would find every drawing below the paragraph, so a cell would
        /// paint a picture deleted under tracked changes, or the one a move had left.
        /// </remarks>
        private static IEnumerable<Drawing> ShownDrawings(IEnumerable<OpenXmlElement> children)
        {
            foreach (var child in children)
            {
                if (child is Run run)
                {
                    foreach (var drawing in run.Elements<Drawing>()) yield return drawing;
                    continue;
                }
                IEnumerable<OpenXmlElement> inner = child is Hyperlink || child is SimpleField
                    ? child.ChildElements
                    : MarkupWrappers.InlineContent(child);
                if (inner == null) continue;
                foreach (var drawing in ShownDrawings(inner)) yield return drawing;
            }
        }

        // ------------------------------------------------------------------
        //  Drawing
        // ------------------------------------------------------------------

        // Draws a row, splitting it across pages when it is taller than a full page.
        private void DrawRowPaginated(RowLayout row, DocxRenderContext ctx)
        {
            // Whole row fits in the space left on this page.
            if (row.Height <= ctx.RemainingHeight)
            {
                DrawTopLevelSegment(row, ctx.CursorY, row.Height, true, true, FullRanges(row), ctx);
                ctx.AdvanceCursor(row.Height);
                return;
            }
            // Fits on a fresh page → move there and draw whole.
            if (row.Height <= ctx.ContentHeight)
            {
                ctx.BreakFlow();
                DrawTopLevelSegment(row, ctx.CursorY, row.Height, true, true, FullRanges(row), ctx);
                ctx.AdvanceCursor(row.Height);
                return;
            }

            // Taller than a full page → split at block boundaries across pages.
            if (ctx.RemainingHeight < ctx.Options.DefaultFontSize * 3)
                ctx.BreakFlow();

            var cursor = new int[row.Cells.Count];
            bool firstSegment = true;
            while (true)
            {
                // If the next (forced) block for some cell is taller than the space
                // left but would fit on a fresh page, move to a fresh page first so
                // it isn't drawn straddling/below the bottom margin.
                if (ctx.RemainingHeight < ctx.ContentHeight)
                {
                    double maxNext = 0;
                    for (int i = 0; i < row.Cells.Count; i++)
                        if (cursor[i] < row.Cells[i].Blocks.Count)
                            maxNext = Math.Max(maxNext, row.Cells[i].Blocks[cursor[i]].Height);
                    if (maxNext > ctx.RemainingHeight - 2 * DefaultVertPad
                        && maxNext <= ctx.ContentHeight - 2 * DefaultVertPad)
                        ctx.BreakFlow();
                }

                double budget = ctx.RemainingHeight - 2 * DefaultVertPad;
                var (ranges, consumed, allDone) = AssignSegment(row, cursor, budget);
                double band = allDone
                    ? Math.Max(ctx.Options.DefaultFontSize * DocxLayoutMetrics.MinRowHeightFactor,
                               consumed + 2 * DefaultVertPad)
                    : ctx.RemainingHeight;

                DrawTopLevelSegment(row, ctx.CursorY, band, firstSegment, allDone, ranges, ctx);

                if (allDone) { ctx.AdvanceCursor(band); return; }
                ctx.BreakFlow();
                firstSegment = false;
            }
        }

        // Picks the run of blocks (per cell, from each cell's cursor) that fits the
        // height budget, always taking at least one block so splitting makes progress.
        private static ((int start, int count)[] ranges, double consumed, bool allDone) AssignSegment(
            RowLayout row, int[] cursor, double budget)
        {
            var ranges = new (int, int)[row.Cells.Count];
            double consumed = 0;
            bool allDone = true;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                var blocks = row.Cells[i].Blocks;
                int start = cursor[i];
                int b = start;
                double used = 0;
                while (b < blocks.Count)
                {
                    double h = blocks[b].Height;
                    if (b > start && used + h > budget) break; // already have ≥1, stop before overflow
                    used += h; b++;                            // first block is always taken (forces progress)
                }
                ranges[i] = (start, b - start);
                cursor[i] = b;
                consumed = Math.Max(consumed, used);
                if (b < blocks.Count) allDone = false;
            }
            return (ranges, consumed, allDone);
        }

        private (int start, int count)[] FullRanges(RowLayout row)
        {
            var r = new (int, int)[row.Cells.Count];
            for (int i = 0; i < row.Cells.Count; i++)
                r[i] = (0, row.Cells[i].Blocks.Count);
            return r;
        }

        private void DrawRowSegment(RowLayout row, double top, double bandHeight,
            bool drawTop, bool drawBottom, (int start, int count)[] ranges, DocxRenderContext ctx)
        {
            for (int i = 0; i < row.Cells.Count; i++)
            {
                var cell = row.Cells[i];
                DrawCellBorders(cell, top, bandHeight, drawTop, drawBottom, ctx);

                // Vertical alignment: shift content down for center/bottom when the
                // whole cell is drawn (not split across pages).
                double vOffset = 0;
                bool whole = ranges[i].start == 0 && ranges[i].count >= cell.Blocks.Count;
                if (whole && cell.VAlign != 0)
                {
                    double slack = bandHeight - 2 * DefaultVertPad - cell.ContentHeight;
                    if (slack > 0) vOffset = cell.VAlign == 1 ? slack / 2 : slack;
                }

                double y = top - DefaultVertPad - vOffset;
                var (start, count) = ranges[i];
                for (int b = start; b < start + count && b < cell.Blocks.Count; b++)
                {
                    cell.Blocks[b].Draw(y, ctx, this);
                    y -= cell.Blocks[b].Height;
                }
            }
        }

        private void DrawCellBorders(CellLayout cell, double top, double height,
            bool drawTop, bool drawBottom, DocxRenderContext ctx)
        {
            double x = cell.X, width = cell.Width;
            double bottom = top - height, right = x + width;
            var borders = cell.Borders;

            // When no border information exists anywhere, fall back to a thin default
            // box (drawn as individual edges so split segments can omit top/bottom).
            var def = new StyleResolver.BorderSide
            {
                Visible = true, WidthPts = DocxLayoutMetrics.DefaultBorderWidthPt, HexColor = null
            };
            var topS = borders.AnyDefined ? borders.Top : def;
            var botS = borders.AnyDefined ? borders.Bottom : def;
            var leftS = borders.AnyDefined ? borders.Left : def;
            var rightS = borders.AnyDefined ? borders.Right : def;

            var ops = new StringBuilder();

            // Cell background shading (drawn first, behind borders + content).
            if (cell.Shading != null && RunRenderer.TryParseHexColor(cell.Shading, out int sr, out int sg, out int sb))
            {
                ops.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:F3} {1:F3} {2:F3} rg\n{3:F2} {4:F2} {5:F2} {6:F2} re\nf\n",
                    sr / 255f, sg / 255f, sb / 255f, x, bottom, width, height);
            }

            void Edge(StyleResolver.BorderSide side, double x1, double y1, double x2, double y2)
            {
                if (side.Visible) AppendStroke(ops, side, x1, y1, x2, y2);
            }

            if (drawTop && !cell.SuppressTop) Edge(topS, x, top, right, top);
            if (drawBottom && !cell.SuppressBottom) Edge(botS, x, bottom, right, bottom);
            Edge(leftS, x, bottom, x, top);
            Edge(rightS, right, bottom, right, top);
            if (ops.Length > 0) ctx.AppendContent(ops.ToString());
        }

        private static void AppendStroke(StringBuilder ops, StyleResolver.BorderSide side,
            double x1, double y1, double x2, double y2)
        {
            float r = 0, g = 0, b = 0;
            if (side.HexColor != null && RunRenderer.TryParseHexColor(side.HexColor, out int rr, out int gg, out int bb))
            {
                r = rr / 255f; g = gg / 255f; b = bb / 255f;
            }
            double w = Math.Max(0.1, side.WidthPts);

            if (side.Style == StyleResolver.BorderLineStyle.Double)
            {
                // Two thin parallel lines offset half the nominal width either side.
                double off = Math.Max(0.4, w / 2);
                double tw = Math.Max(0.1, w / 3);
                bool horizontal = Math.Abs(y2 - y1) < 0.01;
                double dx = horizontal ? 0 : off, dy = horizontal ? off : 0;
                AppendLine(ops, tw, r, g, b, null, x1 - dx, y1 - dy, x2 - dx, y2 - dy);
                AppendLine(ops, tw, r, g, b, null, x1 + dx, y1 + dy, x2 + dx, y2 + dy);
                return;
            }

            // Dash pattern for dotted/dashed (a PDF `d` array); null = solid.
            string dash = side.Style == StyleResolver.BorderLineStyle.Dotted
                ? $"[{(w * 1).ToString("F2", CultureInfo.InvariantCulture)} {(w * 2).ToString("F2", CultureInfo.InvariantCulture)}]"
                : side.Style == StyleResolver.BorderLineStyle.Dashed
                    ? $"[{(w * 3).ToString("F2", CultureInfo.InvariantCulture)} {(w * 2).ToString("F2", CultureInfo.InvariantCulture)}]"
                    : null;
            AppendLine(ops, w, r, g, b, dash, x1, y1, x2, y2);
        }

        private static void AppendLine(StringBuilder ops, double w, float r, float g, float b,
            string dash, double x1, double y1, double x2, double y2)
        {
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} w\n{1:F3} {2:F3} {3:F3} RG\n",
                Math.Max(0.1, w), r, g, b);
            if (dash != null) ops.Append(dash).Append(" 0 d\n");
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n{2:F2} {3:F2} l\nS\n", x1, y1, x2, y2);
            if (dash != null) ops.Append("[] 0 d\n"); // reset to solid for following strokes
        }

        // ------------------------------------------------------------------
        //  Geometry & text wrapping
        // ------------------------------------------------------------------

        /// <summary>
        /// The table's drawn width, from <c>w:tblW</c>.
        ///
        /// <para>
        /// <c>pct</c> is honoured as well as <c>dxa</c>. It is fiftieths of a percent (ECMA-376
        /// §17.4.88, <c>ST_MeasurementOrPercent</c>) — or a literal percentage when the value ends
        /// in '%' — and it is the normal form emitted by HTML-to-DOCX exporters and by Word's
        /// "AutoFit to window". Falling through to the full column width laid a 50% table across
        /// the whole column, so every cell came out twice as wide as authored, and nothing said so.
        /// </para>
        ///
        /// <para>
        /// <c>auto</c> keeps the full width — Word autofits to contents, which this renderer does
        /// not do — and says so, so an authored full-width table is distinguishable from an ignored
        /// declaration.
        /// </para>
        /// </summary>
        private static double ResolveTableWidth(Table table, double available)
        {
            var tw = table.GetFirstChild<TableProperties>()?.TableWidth;
            var type = tw?.Type?.Value;
            string raw = tw?.Width?.Value;

            if (type == TableWidthUnitValues.Dxa
                && raw != null
                && double.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out double twips)
                && twips > 0)
            {
                return Math.Min(available, PdfUnits.TwipsToPoints(twips));
            }

            if (type == TableWidthUnitValues.Pct && raw != null)
            {
                bool literal = raw.EndsWith("%", StringComparison.Ordinal);
                string number = literal ? raw.Substring(0, raw.Length - 1) : raw;
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)
                    && pct > 0)
                {
                    double fraction = literal ? pct / 100.0 : pct / PctPerWholeTable;
                    return Math.Min(available, available * fraction);
                }
            }

            if (type == TableWidthUnitValues.Auto && raw != null && raw != "0")
                DocxDiagnostics.Report("TableRenderer.ResolveTableWidth",
                    "the table states w:tblW w:type=\"auto\"; contents-based autofit is not "
                        + "implemented, so it was laid out at the full column width");

            return available;
        }

        /// <summary>
        /// <c>w:tblW w:type="pct"</c> is in fiftieths of a percent, so 5000 is the whole width.
        /// </summary>
        private const double PctPerWholeTable = 5000.0;

        private List<double> ResolveColumnWidths(Table table, double tableWidth)
        {
            var widths = new List<double>();
            var grid = table.Elements<TableGrid>().FirstOrDefault();
            if (grid != null)
            {
                var gridCols = grid.Elements<GridColumn>().ToList();
                double totalTwips = gridCols.Sum(col =>
                    col.Width?.Value != null && double.TryParse(col.Width.Value, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out double w) ? w : 0);

                if (totalTwips > 0)
                {
                    foreach (var col in gridCols)
                    {
                        double w = col.Width?.Value != null && double.TryParse(col.Width.Value,
                            NumberStyles.Integer, CultureInfo.InvariantCulture, out double cw) ? cw : 0;
                        widths.Add(w > 0 ? (w / totalTwips) * tableWidth : tableWidth / gridCols.Count);
                    }
                    return widths;
                }
            }

            var firstRow = MarkupWrappers.Rows(table).FirstOrDefault();
            int colCount = firstRow != null ? MarkupWrappers.Cells(firstRow).Count() : 1;
            for (int i = 0; i < colCount; i++)
                widths.Add(tableWidth / colCount);
            return widths;
        }

        /// <param name="cells">The row's cells.</param>
        /// <param name="colWidths">The grid's column widths.</param>
        /// <param name="tableX">The table's left edge.</param>
        /// <param name="gridBefore">The grid columns the row leaves empty before its first cell (<c>w:gridBefore</c>), clamped to the grid.</param>
        private static List<(TableCell cell, double x, double width, int colIdx, int span)> PlaceCells(
            List<TableCell> cells, List<double> colWidths, double tableX, int gridBefore = 0)
        {
            var placed = new List<(TableCell, double, double, int, int)>();
            int col = Math.Min(Math.Max(0, gridBefore), colWidths.Count);
            double x = tableX;
            for (int i = 0; i < col; i++) x += colWidths[i];
            double fallback = colWidths.Count > 0 ? colWidths[colWidths.Count - 1] : 0;
            foreach (var cell in cells)
            {
                int span = cell.TableCellProperties?.GridSpan?.Val?.Value ?? 1;
                if (span < 1) span = 1;

                // w:gridSpan is an unbounded xsd:integer and `col += span` is unchecked, so two
                // cells declaring int.MaxValue wrapped col NEGATIVE and the next cell's
                // `col + s < colWidths.Count` test then passed for a negative index —
                // colWidths[-2] threw out of a path ConvertBuiltIn re-wraps as a whole-document
                // failure. Clamping to the columns that actually remain keeps every index inside
                // [0, Count] while preserving the stated intent above: an overshoot still renders.
                int remaining = colWidths.Count - col;
                if (span > remaining)
                {
                    DocxDiagnostics.Report("TableRenderer.PlaceCells", string.Format(
                        CultureInfo.InvariantCulture,
                        "a table cell declares w:gridSpan {0} but only {1} of the grid's {2} "
                            + "columns remain; the span was clamped and the cell drawn narrower "
                            + "than the document asked for",
                        span, Math.Max(0, remaining), colWidths.Count));
                    span = Math.Max(1, remaining);
                }

                double width = 0;
                for (int s = 0; s < span && col + s < colWidths.Count; s++)
                    width += colWidths[col + s];
                // gridSpan overshoot / a grid that under-declares columns: give the
                // cell a fallback width so its content still renders (not dropped).
                if (width <= 0) width = fallback;

                placed.Add((cell, x, width, col, span));
                x += width;
                col += span;
            }
            return placed;
        }

        /// <summary>A cell line's segments in left-to-right visual order (<see cref="LineBidi"/>).</summary>
        private static List<CellSeg> InVisualOrder(List<CellSeg> line, LineBidi.Paragraph paragraph, bool rtlParagraph)
            => LineBidi.InVisualOrder(line, paragraph, rtlParagraph, LogicalText,
                seg => seg.IsMath || seg.IsControl,
                (seg, text, level, overridden, whole) =>
                {
                    if (seg.IsGlyph)
                    {
                        if (whole && !overridden && LineBidi.ShapedAsLevel(text, level)) return seg;
                        var glyphs = UnicodeText.Shape(text, seg.Program, seg.Size, seg.Kerning, seg.ContextualAlternates,
                            seg.Ligatures, LineBidi.DirectionOf(level), overridden);
                        glyphs.RecordInto(seg.FontEntry.UsedGidToUnicode, seg.FontEntry.UsedGidToText);
                        var piece = seg.CloneStyle(text);
                        piece.IsGlyph = true; piece.Glyphs = glyphs; piece.EmbeddedResName = seg.EmbeddedResName;
                        piece.Program = seg.Program; piece.FontEntry = seg.FontEntry; piece.Kerning = seg.Kerning;
                        piece.ContextualAlternates = seg.ContextualAlternates; piece.Ligatures = seg.Ligatures;
                        return piece;
                    }
                    return whole && !LineBidi.IsRightToLeft(level) ? seg : seg.CloneStyle(LineBidi.AsDrawn(text, level));
                });

        /// <summary>What a cell segment stands for in its paragraph's text, before wrapping and after (<see cref="LineBidi"/>).</summary>
        private static string LogicalText(CellSeg seg) => seg.IsMath ? LineBidi.ObjectReplacement : seg.Text ?? "";

        /// <summary>
        /// A cell paragraph's lines, and its bidi (<paramref name="bidiParagraph"/>, over its text
        /// before wrapping) for <see cref="InVisualOrder"/>.
        /// </summary>
        private List<(List<CellSeg> segs, double height)> WrapParagraph(
            Paragraph para, double maxWidth, DocxRenderContext context,
            out LineBidi.Paragraph bidiParagraph, bool rtlParagraph,
            StyleResolver.TableStyleCellFormat styleFmt = default)
        {
            bidiParagraph = null;
            var resolver = context.StyleResolver;
            var srcSegs = new List<CellSeg>();
            // Collect the cell paragraph's inline runs, plus any Office Math linearised to text,
            // so equations inside table cells are preserved too (matched by element name).
            // A run with the hyperlink target it lies under, or a w:dir / w:bdo control (Run null) — the
            // controls around their runs, in order.
            // An equation (Math) takes its place among them, in document order: collected apart and
            // appended after all the text, "Let a/b be" would draw "Let  be" and then the fraction.
            var inlineRuns = new List<(Run Run, char Control, string Link, CellSeg Math)>();
            // Through a hyperlink (its target carried to its runs, as a body paragraph's is), a simple
            // field (its cached result) and the wrappers that only mark their runs: reading only a
            // paragraph's own runs would make the text of all of those vanish from every table cell.
            void Collect(OpenXmlElement child, string link)
            {
                if (child is Run r) { inlineRuns.Add((r, '\0', link, null)); return; }
                if (child is Hyperlink h)
                {
                    string target = HyperlinkTargets.Of(h, context.WordDocument.MainDocumentPart) ?? link;
                    foreach (var c in child.ChildElements) Collect(c, target);
                    return;
                }
                if (child is SimpleField)
                {
                    foreach (var c in child.ChildElements) Collect(c, link);
                    return;
                }
                var wrapped = MarkupWrappers.InlineContent(child);
                if (wrapped != null)
                {
                    var (open, close) = MarkupWrappers.DirectionControls(child);
                    if (open != '\0') inlineRuns.Add((null, open, null, null));
                    foreach (var c in wrapped) Collect(c, link);
                    if (close != '\0') inlineRuns.Add((null, close, null, null));
                    return;
                }
                if (child.LocalName == DocxLayoutMetrics.OMathElement
                         || child.LocalName == DocxLayoutMetrics.OMathParaElement)
                {
                    // Typeset, exactly as in a body paragraph — an equation must not render one way
                    // in text and another inside a cell.
                    double mathSize = resolver.ResolveFontSizeInPoints(null, para, context.Options.DefaultFontSize);
                    if (mathSize <= 0) mathSize = context.Options.DefaultFontSize;
                    string mathFamily = resolver.ResolveFontName(null, para) ?? StandardFonts.Helvetica;

                    var style = MathTypesetting.MathStyle.Root(mathFamily, mathSize, display: false, "000000");
                    var box = new MathTypesetting.MathBoxBuilder(context).Build(child, style);
                    if (box != null && (box.Width > 0 || box.Height > 0))
                        inlineRuns.Add((null, '\0', null, new CellSeg
                        {
                            IsMath = true,
                            Math = box,
                            Font = resolver.MapToPdfFontName(mathFamily, false, false),
                            Size = mathSize,
                        }));
                }
            }
            // The fields resolved first, as the body paragraph's are (FieldWalk).
            foreach (var child in FieldWalk.ResolveFields(para.ChildElements, instruction => ParagraphRenderer.ComputeField(instruction, context)))
                Collect(child, null);

            // A numbered cell paragraph's marker, as the body paragraph's (ListRenderer), first on its
            // first line, from the counters the whole conversion shares.
            string marker = ListRenderer.GetListPrefix(para, context);
            if (!string.IsNullOrEmpty(marker))
            {
                double markerSize = resolver.ResolveFontSizeInPoints(null, para, context.Options.DefaultFontSize);
                var markerProgram = UnicodeText.NeedsUnicode(marker) ? ParagraphRenderer.MarkerProgram(marker, para, context) : null;
                srcSegs.Add(markerProgram != null
                    ? new CellSeg { IsUnicode = true, Program = markerProgram, FontEntry = context.RegisterEmbeddedFont(markerProgram), Text = marker, Size = markerSize }
                    : new CellSeg { Text = marker, Font = resolver.MapToPdfFontName(resolver.ResolveFontName(null, para), false, false), Size = markerSize });
            }

            // A footnote or endnote reference in a cell: a superscript mark, the note queued and numbered
            // in document order, as the body paragraph's AddNoteMark does.
            void EmitNoteMark(Run run, long id, bool isEnd, string link)
            {
                if (id < 1) return;
                int number = context.NoteNumber(id, isEnd, () => ParagraphRenderer.NoteText(context, id, isEnd));
                double size = resolver.ResolveFontSizeInPoints(run, para, context.Options.DefaultFontSize);
                srcSegs.Add(new CellSeg
                {
                    Text = context.FormatNoteNumber(number, isEnd), Font = StandardFonts.Helvetica,
                    Size = size * DocxLayoutMetrics.NoteMarkFontScale, BaselineShift = size * DocxLayoutMetrics.NoteMarkRiseFactor,
                    LinkUri = link,
                });
            }

            foreach (var (run, control, link, math) in inlineRuns)
            {
                if (math != null) { srcSegs.Add(math); continue; }
                if (run == null)
                {
                    srcSegs.Add(new CellSeg { IsControl = true, Text = control.ToString() });
                    continue;
                }
                // Hidden text (w:vanish) is left out of print and PDF.
                if (resolver.ResolveVanish(run, para)) continue;
                // A run holding a note reference beside its text is taken a part at a time, as the body's is;
                // a page or column break in a cell is passed over, as Word draws it, so a run without a
                // reference is read whole.
                bool hasNote = run.GetFirstChild<FootnoteReference>() != null || run.GetFirstChild<EndnoteReference>() != null;
                var parts = hasNote ? ParagraphRenderer.SplitAtBlockChildren(run) : null;
                if (parts != null)
                {
                    foreach (var (textPart, block) in parts)
                    {
                        if (textPart != null) EmitRun(textPart, link);
                        else if (block is FootnoteReference fn) EmitNoteMark(run, fn.Id?.Value ?? -1, isEnd: false, link);
                        else if (block is EndnoteReference en) EmitNoteMark(run, en.Id?.Value ?? -1, isEnd: true, link);
                    }
                    continue;
                }
                if (run.GetFirstChild<FootnoteReference>() is FootnoteReference footnote)
                {
                    EmitNoteMark(run, footnote.Id?.Value ?? -1, isEnd: false, link);
                    continue;
                }
                if (run.GetFirstChild<EndnoteReference>() is EndnoteReference endnote)
                {
                    EmitNoteMark(run, endnote.Id?.Value ?? -1, isEnd: true, link);
                    continue;
                }
                EmitRun(run, link);
            }

            // A run's text as cell segments: cut at its line breaks — a w:br of the text-wrapping kind, as
            // the body's RunRenderer reads it; a page or column break in a cell is passed over.
            //
            // Each piece is built in one buffer: appending a run's w:t elements to a string one at a time
            // would copy the piece once a w:t. A w:tab is drawn as a space and said so — a cell paragraph
            // lays out no tab stops — so the words on either side do not join.
            void EmitRun(Run run, string link)
            {
                var pieces = new List<string>();
                var piece = new StringBuilder();
                bool tabbed = false;
                foreach (var part in run.ChildElements)
                {
                    if (part is Text t) piece.Append(t.Text);
                    else if (part is TabChar) { piece.Append(' '); tabbed = true; }
                    else if (part is Break br && (br.Type == null || br.Type.Value == BreakValues.TextWrapping))
                    {
                        pieces.Add(piece.ToString());
                        piece.Clear();
                    }
                    else
                    {
                        switch (RunContent.Classify(part, out string stands))
                        {
                            case RunContent.Kind.Text: piece.Append(stands); break;
                            case RunContent.Kind.LineBreak: pieces.Add(piece.ToString()); piece.Clear(); break;
                            case RunContent.Kind.Tab: piece.Append(' '); tabbed = true; break;
                        }
                    }
                }
                pieces.Add(piece.ToString());
                if (tabbed)
                    DocxDiagnostics.Report("TableRenderer.TabInCell",
                        "a tab in a table cell was drawn as a space: a cell paragraph lays out no tab stops");
                if (pieces.Count == 1 && pieces[0].Length == 0) return;
                // w:caps draws the run in capitals; w:smallCaps draws its lowercase letters as capitals too,
                // at the run's size in a cell.
                bool capitals = resolver.ResolveCaps(run, para) || resolver.ResolveSmallCaps(run, para);
                double size = resolver.ResolveFontSizeInPoints(run, para, context.Options.DefaultFontSize);
                // Run / style formatting wins; the table style's conditional formatting (e.g. a
                // bold, coloured header row) only fills in what the run leaves unspecified.
                bool bold = resolver.ResolveBoldNullable(run, para) ?? (styleFmt.Bold ?? false);
                bool italic = resolver.ResolveIsItalic(run, para);
                var color = resolver.ResolveColor(run, para) ?? styleFmt.ColorHex;
                // w:w and w:spacing, read here for the same reason the body path reads them: they
                // change the run's drawn width, so wrapping without them puts the line break in
                // the wrong place and the cell's text in a different place from the same run
                // outside a table.
                int textScale = resolver.ResolveTextScale(run, para);
                double charSpacing = resolver.ResolveCharacterSpacing(run, para);
                bool kerning = resolver.ResolveKerning(run, para, size);
                bool contextualAlternates = resolver.ResolveContextualAlternates(run, para);
                bool ligatures = resolver.ResolveLigatures(run, para);
                // Resolved once a run, as everything above is: each asks the run for its w:rPr, which
                // the SDK finds by searching the run's children, and a run of many w:t and w:br asked
                // it again for every piece — quadratic in the run's children.
                string font = resolver.MapToPdfFontName(resolver.ResolveFontName(run, para), bold, italic);
                string csFamily = null;

                for (int pi = 0; pi < pieces.Count; pi++)
                {
                    if (pi > 0) srcSegs.Add(new CellSeg { IsLineBreak = true, Size = size });
                    string text = capitals ? pieces[pi].ToUpperInvariant() : pieces[pi];
                    if (text.Length == 0) continue;

                    // Hebrew/Arabic/… → a pre-shape Unicode segment, shaped per wrapped line below.
                    if (UnicodeText.NeedsUnicode(text))
                    {
                        csFamily ??= resolver.ResolveComplexScriptFontName(run, para);
                        var cps = UnicodeText.CodePointsToCover(text);
                        // Prefer a font embedded in the DOCX itself; fall back to an OS font.
                        var program = context.ResolveEmbeddedFont(csFamily, bold, italic, cps)
                                      ?? context.ResolveHostFont(csFamily, bold, italic, cps);
                        if (program != null)
                        {
                            srcSegs.Add(new CellSeg
                            {
                                IsUnicode = true, Program = program,
                                FontEntry = context.RegisterEmbeddedFont(program),
                                Text = text, Size = size, Color = color,
                                TextScale = textScale, CharSpacing = charSpacing, Kerning = kerning,
                                ContextualAlternates = contextualAlternates, Ligatures = ligatures,
                                LinkUri = link,
                            });
                            continue;
                        }

                        // The identical silent fall-through the body path had: with no covering font
                        // the cell's text drops to the standard-14 path below and every
                        // unrepresentable character becomes a space. One shared reporter, so a cell
                        // and a body run cannot describe the same loss differently.
                        RunRenderer.ReportNoCoveringFont("TableRenderer.NoCoveringFont", csFamily, text);
                    }

                    srcSegs.Add(new CellSeg
                    {
                        Text = text, Font = font, Size = size, Color = color,
                        TextScale = textScale, CharSpacing = charSpacing, LinkUri = link,
                    });
                }
            }

            var lines = new List<(List<CellSeg> segs, double height)>();
            bidiParagraph = LineBidi.Paragraph.Of(srcSegs, rtlParagraph, LogicalText);

            if (srcSegs.All(s => s.IsControl))
            {
                lines.Add((new List<CellSeg>(), context.Options.DefaultFontSize * DocxLayoutMetrics.CellLineFactor));
                return lines;
            }

            var current = new List<CellSeg>();
            double currentWidth = 0, currentHeight = 0;
            void Push()
            {
                if (current.Count > 0) lines.Add((current, currentHeight));
                current = new List<CellSeg>(); currentWidth = 0; currentHeight = 0;
            }
            void Add(string text, CellSeg seg)
            {
                current.Add(seg.CloneStyle(text));
                currentWidth += seg.TextWidth(text, resolver);
                currentHeight = Math.Max(currentHeight, seg.Size * DocxLayoutMetrics.CellLineFactor);
            }

            foreach (var seg in srcSegs)
            {
                if (seg.IsControl) { current.Add(seg); continue; }

                // A w:br ends the line, an empty one too, which still takes the run's line height.
                if (seg.IsLineBreak)
                {
                    lines.Add((current, Math.Max(currentHeight, seg.Size * DocxLayoutMetrics.CellLineFactor)));
                    current = new List<CellSeg>(); currentWidth = 0; currentHeight = 0;
                    continue;
                }

                // An equation is atomic and carries its own height and depth.
                if (seg.IsMath)
                {
                    double mw = seg.Width(resolver);
                    if (current.Count > 0 && currentWidth + mw > maxWidth) Push();
                    current.Add(seg);
                    currentWidth += mw;
                    currentHeight = Math.Max(currentHeight, seg.Ascent + seg.Descent);
                    continue;
                }

                // A Unicode run wraps like a body paragraph's: the words that fit on a line are
                // shaped TOGETHER, so bidi orders that line as a whole and the one segment it becomes
                // is already in visual order. A segment per word would be drawn left to right in list
                // order and reverse every right-to-left line, and a run shaped whole and kept atomic would
                // overflow a cell narrower than it.
                if (seg.IsUnicode)
                {
                    foreach (var (piece, breakBefore) in UnicodeText.WrapWords(
                                 seg.Text, seg.ShapedTextWidth, currentWidth, current.Count > 0, maxWidth))
                    {
                        if (breakBefore) Push();
                        var glyphs = UnicodeText.Shape(piece, seg.Program, seg.Size, seg.Kerning, seg.ContextualAlternates, seg.Ligatures);
                        glyphs.RecordInto(seg.FontEntry.UsedGidToUnicode, seg.FontEntry.UsedGidToText);
                        // Text is carried on a glyph segment too — not for drawing, which uses the
                        // shaped gids, but so the tracking applies per character exactly as it does
                        // for a shaped run in a body paragraph.
                        var line = new CellSeg
                        {
                            IsGlyph = true, Glyphs = glyphs, EmbeddedResName = seg.FontEntry.ResName,
                            Text = piece, Size = seg.Size, Color = seg.Color,
                            TextScale = seg.TextScale, CharSpacing = seg.CharSpacing,
                            // Kept so the line's bidi pass can shape a piece of it again.
                            Program = seg.Program, FontEntry = seg.FontEntry, Kerning = seg.Kerning,
                            ContextualAlternates = seg.ContextualAlternates, Ligatures = seg.Ligatures,
                            LinkUri = seg.LinkUri,
                        };
                        current.Add(line);
                        currentWidth += line.Width(resolver);
                        currentHeight = Math.Max(currentHeight, seg.Size * DocxLayoutMetrics.CellLineFactor);
                    }
                    continue;
                }

                var buf = new StringBuilder();
                double bufWidth = 0;
                void FlushBuf() { if (buf.Length > 0) { Add(buf.ToString(), seg); buf.Clear(); bufWidth = 0; } }

                // The body paragraph's tokens (UnicodeText.SpacedWords): a run's own spaces kept
                // but where a line breaks.
                foreach (var (spaces, word) in UnicodeText.SpacedWords(seg.Text))
                {
                    if (word.Length == 0) { buf.Append(spaces); bufWidth += seg.TextWidth(spaces, resolver); break; }
                    double wordWidth = seg.TextWidth(word, resolver);
                    if (wordWidth > maxWidth && word.Length > 1)
                    {
                        FlushBuf();
                        if (current.Count > 0) Push();
                        foreach (var chunk in ParagraphRenderer.BreakWord(word, maxWidth, w => seg.TextWidth(w, resolver)))
                        {
                            double cw = seg.TextWidth(chunk, resolver);
                            if (current.Count > 0 && currentWidth + cw > maxWidth) Push();
                            Add(chunk, seg);
                        }
                        continue;
                    }

                    string token = spaces + word;
                    double tokenWidth = seg.TextWidth(token, resolver);
                    if (currentWidth + bufWidth + tokenWidth <= maxWidth || (current.Count == 0 && buf.Length == 0))
                    {
                        buf.Append(token); bufWidth += tokenWidth;
                    }
                    else
                    {
                        FlushBuf(); Push(); buf.Append(word); bufWidth = wordWidth;
                    }
                }
                FlushBuf();
            }
            Push();
            if (lines.Count == 0)
                lines.Add((new List<CellSeg>(), context.Options.DefaultFontSize * DocxLayoutMetrics.CellLineFactor));
            return lines;
        }
    }
}
