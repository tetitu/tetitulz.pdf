// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Builder.Model
{
    /// <summary>
    /// The one walk over a <see cref="Document"/>'s measurements before any exporter reads them:
    /// every <c>double</c> the model carries is finite, and a size is not negative — the refusal
    /// naming the element and the property.
    /// </summary>
    /// <remarks>
    /// <para><b>Why at export, and why one walk.</b> The model is a public mutable graph set by three
    /// producers (the fluent <c>DocumentBuilder</c>, DocxToPdf's and HtmlToPdf's mappers) and by
    /// a caller's own code, so a check in a setter would miss direct assignment; and one
    /// <see cref="Document"/> goes out as PDF, HTML, DOCX and ODT, so a check in one exporter would let
    /// the same document be refused by one and written by the others. A NaN or an infinity reaching
    /// <c>PdfRealFormat</c>, which writes <c>0</c>, would make <c>FontSize(double.NaN)</c> export text
    /// drawn at size 0 and the export report success.</para>
    /// <para><b>What is a size.</b> A font size, a width or height, a radius, a padding, a blur, the
    /// spacing between cells or rows, a line height, a border width, a float width, a minimum height,
    /// a list's text indent and a page border's inset cannot be negative and are refused below 0. A
    /// margin, an indent, a spacing before or after a block, an offset, a baseline shift, a character
    /// spacing, a shadow's spread, a background's position, a transform's coefficient and the points
    /// term beside a width fraction (<c>calc(50% - 10px)</c>) may be negative and are required finite.
    /// An opacity is clamped where it is used and is required finite. Where <c>0</c> or a non-positive
    /// value means "automatic" or "ask the decoder" (<see cref="Paragraph.LineSpacingPt"/>,
    /// <see cref="BackgroundImage.WidthPt"/>, a <see cref="Table.ColumnWidths"/> entry), the value is
    /// required finite and its meaning is left to the exporter.</para>
    /// <para><b>The page size is here too.</b> <c>Core.Writing.PdfDocumentBuilder.AddPage</c> refuses a
    /// non-positive or non-finite side for every producer of a PDF, but only the PDF exporter reaches
    /// it: DOCX would write a NaN side as 0 twips and HTML and ODT never read it, so one document would
    /// be refused by one exporter and written by three. The walk refuses it for all four.</para>
    /// <para><b>The walk is bounded, and so are the exporters'.</b> Blocks nest through table cells,
    /// list items' sublists, placements and footnotes, and the model is a public graph: a caller can
    /// make an element its own descendant, or reach one from several places. The walk checks each
    /// element once (by identity) to <see cref="MaxWalkDepth"/>, deeper than any exporter draws, so
    /// every value an exporter reads has been checked — the cell content of a table nested past
    /// <see cref="ModelRules.MaxListDepth"/> was not, while the PDF renderer draws tables to
    /// <c>PdfLimits.MaxTableNestingDepth</c>. The exporters' own walks have depth caps and no memory
    /// of what they walked, so an element reachable from k places is walked k times a level: a list
    /// whose three items each hold it is 3²¹ items to them. The document is refused when its walks
    /// would repeat more than <see cref="MaxRepeatedVisits"/> visits (<see cref="Unfolded"/>).</para>
    /// </remarks>
    internal static class ModelMeasurements
    {
        /// <summary>The parameter every refusal names; the element and property are in the message.</summary>
        internal const string ParamName = "document";

        /// <summary>Refuses the first non-finite measurement, or negative size, in <paramref name="document"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A measurement is NaN or an infinity, or a size is negative, or a page side is not positive;
        /// <c>ParamName</c> is <see cref="ParamName"/> and the message names the element's path and the
        /// property.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// The exporters' walks would visit the document's elements more than
        /// <see cref="MaxRepeatedVisits"/> times beyond once each — an element reachable from several
        /// places, or its own descendant, with branching.
        /// </exception>
        internal static void Validate(Document? document)
        {
            if (document == null) return;
            var seen = new HashSet<object>(Tetitulz.Pdf.Core.Objects.PdfReferenceEquality.Instance);
            for (int i = 0; i < document.Sections.Count; i++)
            {
                Section? s = document.Sections[i];
                if (s == null) continue;
                string path = "Sections[" + i + "]";
                NumericArguments.RequirePositive(s.PageWidthPt, ParamName, path + "." + nameof(s.PageWidthPt));
                NumericArguments.RequirePositive(s.PageHeightPt, ParamName, path + "." + nameof(s.PageHeightPt));
                Finite(s.MarginTop, path, nameof(s.MarginTop));
                Finite(s.MarginBottom, path, nameof(s.MarginBottom));
                Finite(s.MarginLeft, path, nameof(s.MarginLeft));
                Finite(s.MarginRight, path, nameof(s.MarginRight));
                for (int a = 0; a < s.ContentAreas.Count; a++)
                {
                    ContentArea? area = s.ContentAreas[a];
                    if (area == null) continue;
                    string areaPath = path + ".ContentAreas[" + a + "]";
                    Finite(area.LeftPt, areaPath, nameof(area.LeftPt));
                    Finite(area.TopPt, areaPath, nameof(area.TopPt));
                    Size(area.WidthPt, areaPath, nameof(area.WidthPt));
                    Size(area.HeightPt, areaPath, nameof(area.HeightPt));
                }
                PageBorders(s.PageBorders, path + ".PageBorders");
                Band(s.Header?.Blocks, s.Header?.Paragraphs, path + ".Header", seen);
                Band(s.Footer?.Blocks, s.Footer?.Paragraphs, path + ".Footer", seen);
                Band(s.FirstPageHeader?.Blocks, s.FirstPageHeader?.Paragraphs, path + ".FirstPageHeader", seen);
                Band(s.FirstPageFooter?.Blocks, s.FirstPageFooter?.Paragraphs, path + ".FirstPageFooter", seen);
                Band(s.EvenPageHeader?.Blocks, s.EvenPageHeader?.Paragraphs, path + ".EvenPageHeader", seen);
                Band(s.EvenPageFooter?.Blocks, s.EvenPageFooter?.Paragraphs, path + ".EvenPageFooter", seen);
                Blocks(s.Children, path + ".Children", 0, seen);
            }
            Unfolded(document, seen.Count);
        }

        // ------------------------------------------------------------------ the two rules

        private static void Finite(double value, string path, string property)
            => NumericArguments.RequireFinite(value, ParamName, path + "." + property);

        private static void Finite(double? value, string path, string property)
        {
            if (value.HasValue) Finite(value.Value, path, property);
        }

        private static void Finite(double[]? values, string path, string property)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) Finite(values[i], path, property + "[" + i + "]");
        }

        private static void Size(double value, string path, string property)
            => NumericArguments.RequireNonNegative(value, ParamName, path + "." + property);

        private static void Size(double? value, string path, string property)
        {
            if (value.HasValue) Size(value.Value, path, property);
        }

        private static void Sizes(double[]? values, string path, string property)
        {
            if (values == null) return;
            for (int i = 0; i < values.Length; i++) Size(values[i], path, property + "[" + i + "]");
        }

        // ------------------------------------------------------------------ the elements

        /// <summary>
        /// How deep the walk follows nesting: past every exporter's drawing (tables to
        /// <c>PdfLimits.MaxTableNestingDepth</c>, placements to <see cref="ModelRules.MaxPlacementNesting"/>,
        /// a list to <see cref="ModelRules.MaxListDepth"/>, footnotes inside them), and short of the
        /// stack, which a chain of a hundred thousand distinct cells nested in one another would end.
        /// </summary>
        private const int MaxWalkDepth = 256;

        private static void Blocks(IEnumerable<IDocElement>? blocks, string path, int depth, HashSet<object> seen)
        {
            if (blocks == null) return;
            int i = 0;
            foreach (IDocElement? block in blocks)
            {
                Block(block, path + "[" + i + "]", depth, seen);
                i++;
            }
        }

        private static void Block(IDocElement? block, string path, int depth, HashSet<object> seen)
        {
            // The model is a public mutable graph: a cell may hold its own table, an item its own
            // list. Each element is checked once, where it is first reached; the depth bounds the stack.
            if (depth > MaxWalkDepth) return;
            switch (block)
            {
                case Paragraph p: Paragraph(p, path, depth, seen); break;
                case Table t: Table(t, path, depth, seen); break;
                case ListItem li: ListItem(li, path, depth, seen); break;
                case ListBlock lb: ListBlock(lb, path, depth, seen); break;
                case BoxStart b: BoxStart(b, path); break;
                case TocBlock toc: Size(toc.LevelIndentPt, path, nameof(toc.LevelIndentPt)); break;
            }
        }

        /// <summary>A header's or footer's content, as the exporters read it: its blocks when it has any, else its paragraphs.</summary>
        private static void Band(List<IDocElement>? blocks, List<Paragraph>? paragraphs, string path, HashSet<object> seen)
        {
            if (blocks != null && blocks.Count > 0) Blocks(blocks, path + ".Blocks", 0, seen);
            else Paragraphs(paragraphs, path + ".Paragraphs", seen);
        }

        private static void Paragraphs(List<Paragraph>? paragraphs, string path, HashSet<object> seen)
        {
            if (paragraphs == null) return;
            for (int i = 0; i < paragraphs.Count; i++)
                if (paragraphs[i] != null) Paragraph(paragraphs[i], path + "[" + i + "]", 0, seen);
        }

        private static void Paragraph(Paragraph p, string path, int depth, HashSet<object> seen)
        {
            if (!seen.Add(p)) return;
            Size(p.FloatWidth, path, nameof(p.FloatWidth));
            Size(p.CornerRadiusPt, path, nameof(p.CornerRadiusPt));
            Sizes(p.CornerRadiiPt, path, nameof(p.CornerRadiiPt));
            Sizes(p.CornerRadiiYPt, path, nameof(p.CornerRadiiYPt));
            Size(p.CornerRadiusPercent, path, nameof(p.CornerRadiusPercent));
            Sizes(p.CornerRadiiPercent, path, nameof(p.CornerRadiiPercent));
            Sizes(p.CornerRadiiYPercent, path, nameof(p.CornerRadiiYPercent));
            Finite(p.Transform, path, nameof(p.Transform));
            Finite(p.TransformOrigin, path, nameof(p.TransformOrigin));
            Finite(p.SpacingBefore, path, nameof(p.SpacingBefore));
            Finite(p.SpacingAfter, path, nameof(p.SpacingAfter));
            Finite(p.IndentLeft, path, nameof(p.IndentLeft));
            Finite(p.IndentRight, path, nameof(p.IndentRight));
            Finite(p.FirstLineIndent, path, nameof(p.FirstLineIndent));
            Size(p.LineSpacingPt, path, nameof(p.LineSpacingPt));
            Size(p.LineHeightFactor, path, nameof(p.LineHeightFactor));
            Size(p.BoxInsetTop, path, nameof(p.BoxInsetTop));
            Size(p.BoxInsetRight, path, nameof(p.BoxInsetRight));
            Size(p.BoxInsetBottom, path, nameof(p.BoxInsetBottom));
            Size(p.BoxInsetLeft, path, nameof(p.BoxInsetLeft));
            Finite(p.WidthFraction, path, nameof(p.WidthFraction));
            Finite(p.WidthPt, path, nameof(p.WidthPt));
            Finite(p.Opacity, path, nameof(p.Opacity));
            for (int i = 0; i < p.Tabs.Count; i++)
                if (p.Tabs[i] != null) Finite(p.Tabs[i].PosPt, path + ".Tabs[" + i + "]", nameof(TabStop.PosPt));
            Shadows(p.Shadow, p.MoreShadows, path);
            Placement(p.Placement, path + ".Placement", depth, seen);
            Background(p.BackgroundImage, path + ".BackgroundImage");
            Border(p.BorderTop, path, nameof(p.BorderTop));
            Border(p.BorderBottom, path, nameof(p.BorderBottom));
            Border(p.BorderLeft, path, nameof(p.BorderLeft));
            Border(p.BorderRight, path, nameof(p.BorderRight));
            Inlines(p.Runs, path + ".Runs", depth, seen);
        }

        private static void Inlines(IList<IInlineElement>? runs, string path, int depth, HashSet<object> seen)
        {
            if (runs == null || depth > MaxWalkDepth) return;
            for (int i = 0; i < runs.Count; i++)
            {
                string at = path + "[" + i + "]";
                switch (runs[i])
                {
                    case TextRun r: Run(r, at); break;
                    case ImageRun img:
                        Size(img.WidthPt, at, nameof(img.WidthPt));
                        Size(img.HeightPt, at, nameof(img.HeightPt));
                        InlineBox(img.Box, at + ".Box");
                        Drawing(img.Drawing, at + ".Drawing");
                        break;
                    case Footnote f:
                        if (!seen.Add(f)) break;
                        Run(f.MarkFormat, at + ".MarkFormat");
                        Inlines(f.Runs, at + ".Runs", depth + 1, seen);
                        break;
                }
            }
        }

        private static void Run(TextRun? r, string path)
        {
            if (r == null) return;
            Size(r.FontSize, path, nameof(r.FontSize));
            Finite(r.BaselineShift, path, nameof(r.BaselineShift));
            Finite(r.CharSpacing, path, nameof(r.CharSpacing));
            Finite(r.Opacity, path, nameof(r.Opacity));
            InlineBox(r.Box, path + ".Box");
        }

        private static void Drawing(VectorDrawing? drawing, string path)
        {
            if (drawing == null) return;
            Size(drawing.StrokeWidthPt, path, nameof(drawing.StrokeWidthPt));
            for (int i = 0; i < drawing.Path.Count; i++)
            {
                PathCommand c = drawing.Path[i];
                string at = path + ".Path[" + i + "]";
                Finite(c.X, at, nameof(c.X)); Finite(c.Y, at, nameof(c.Y));
                Finite(c.C1X, at, nameof(c.C1X)); Finite(c.C1Y, at, nameof(c.C1Y));
                Finite(c.C2X, at, nameof(c.C2X)); Finite(c.C2Y, at, nameof(c.C2Y));
            }
        }

        private static void InlineBox(InlineBox? box, string path)
        {
            if (box == null) return;
            Border(box.Border, path, nameof(box.Border));
            Background(box.BackgroundImage, path + ".BackgroundImage");
            Size(box.PaddingLeft, path, nameof(box.PaddingLeft));
            Size(box.PaddingRight, path, nameof(box.PaddingRight));
            Size(box.PaddingTop, path, nameof(box.PaddingTop));
            Size(box.PaddingBottom, path, nameof(box.PaddingBottom));
            Finite(box.MarginLeft, path, nameof(box.MarginLeft));
            Finite(box.MarginRight, path, nameof(box.MarginRight));
            Finite(box.MarginTop, path, nameof(box.MarginTop));
            Finite(box.MarginBottom, path, nameof(box.MarginBottom));
            Size(box.CornerRadiusPt, path, nameof(box.CornerRadiusPt));
            Sizes(box.CornerRadiiPt, path, nameof(box.CornerRadiiPt));
            Sizes(box.CornerRadiiYPt, path, nameof(box.CornerRadiiYPt));
            Size(box.CornerRadiusPercent, path, nameof(box.CornerRadiusPercent));
            Sizes(box.CornerRadiiPercent, path, nameof(box.CornerRadiiPercent));
            Sizes(box.CornerRadiiYPercent, path, nameof(box.CornerRadiiYPercent));
        }

        private static void Table(Table t, string path, int depth, HashSet<object> seen)
        {
            if (!seen.Add(t)) return;
            Size(t.BorderWidth, path, nameof(t.BorderWidth));
            Size(t.WidthPt, path, nameof(t.WidthPt));
            for (int i = 0; i < t.ColumnWidths.Count; i++)
                Finite(t.ColumnWidths[i], path, nameof(t.ColumnWidths) + "[" + i + "]");
            Finite(t.SpacingBefore, path, nameof(t.SpacingBefore));
            Finite(t.SpacingAfter, path, nameof(t.SpacingAfter));
            Size(t.MinRowHeightPt, path, nameof(t.MinRowHeightPt));
            Size(t.CellSpacingPt, path, nameof(t.CellSpacingPt));
            Size(t.RowSpacingPt, path, nameof(t.RowSpacingPt));
            for (int r = 0; r < t.Rows.Count; r++)
            {
                TableRow? row = t.Rows[r];
                if (row == null) continue;
                string rowPath = path + ".Rows[" + r + "]";
                Size(row.HeightPt, rowPath, nameof(row.HeightPt));
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    TableCell? cell = row.Cells[c];
                    if (cell == null) continue;
                    string cellPath = rowPath + ".Cells[" + c + "]";
                    Size(cell.WidthPt, cellPath, nameof(cell.WidthPt));
                    Border(cell.BorderTop, cellPath, nameof(cell.BorderTop));
                    Border(cell.BorderBottom, cellPath, nameof(cell.BorderBottom));
                    Border(cell.BorderLeft, cellPath, nameof(cell.BorderLeft));
                    Border(cell.BorderRight, cellPath, nameof(cell.BorderRight));
                    Size(cell.InnerPaddingPt, cellPath, nameof(cell.InnerPaddingPt));
                    Size(cell.MarginLeft, cellPath, nameof(cell.MarginLeft));
                    Size(cell.MarginRight, cellPath, nameof(cell.MarginRight));
                    Size(cell.MarginTop, cellPath, nameof(cell.MarginTop));
                    Size(cell.MarginBottom, cellPath, nameof(cell.MarginBottom));
                    // The renderer reads Blocks when it has any, else Paragraphs: the path names the
                    // list it will read.
                    if (cell.Blocks.Count > 0) Blocks(cell.Blocks, cellPath + ".Blocks", depth + 1, seen);
                    else Blocks(cell.Paragraphs, cellPath + ".Paragraphs", depth + 1, seen);
                }
            }
        }

        private static void ListItem(ListItem li, string path, int depth, HashSet<object> seen)
        {
            if (!seen.Add(li)) return;
            Run(li.TextFormat, path + ".TextFormat");
            Run(li.MarkerFormat, path + ".MarkerFormat");
            Inlines(li.Runs, path + ".Runs", depth, seen);
            Size(li.LineSpacingPt, path, nameof(li.LineSpacingPt));
            Size(li.LineHeightFactor, path, nameof(li.LineHeightFactor));
            Finite(li.SpacingAfter, path, nameof(li.SpacingAfter));
            if (li.Sublist != null) Block(li.Sublist, path + ".Sublist", depth + 1, seen);
        }

        private static void ListBlock(ListBlock lb, string path, int depth, HashSet<object> seen)
        {
            if (!seen.Add(lb)) return;
            Size(lb.TextIndentPt, path, nameof(lb.TextIndentPt));
            Finite(lb.SpacingBefore, path, nameof(lb.SpacingBefore));
            Finite(lb.SpacingAfter, path, nameof(lb.SpacingAfter));
            for (int i = 0; i < lb.Items.Count; i++)
                if (lb.Items[i] != null) ListItem(lb.Items[i], path + ".Items[" + i + "]", depth, seen);
        }

        private static void BoxStart(BoxStart b, string path)
        {
            Finite(b.WidthFraction, path, nameof(b.WidthFraction));
            Finite(b.WidthPt, path, nameof(b.WidthPt));
            Size(b.FloatWidth, path, nameof(b.FloatWidth));
            Finite(b.MarginTop, path, nameof(b.MarginTop));
            Finite(b.MarginBottom, path, nameof(b.MarginBottom));
            Finite(b.MarginLeft, path, nameof(b.MarginLeft));
            Finite(b.MarginRight, path, nameof(b.MarginRight));
            Border(b.BorderTop, path, nameof(b.BorderTop));
            Border(b.BorderRight, path, nameof(b.BorderRight));
            Border(b.BorderBottom, path, nameof(b.BorderBottom));
            Border(b.BorderLeft, path, nameof(b.BorderLeft));
            Size(b.PaddingTop, path, nameof(b.PaddingTop));
            Size(b.PaddingRight, path, nameof(b.PaddingRight));
            Size(b.PaddingBottom, path, nameof(b.PaddingBottom));
            Size(b.PaddingLeft, path, nameof(b.PaddingLeft));
            Background(b.BackgroundImage, path + ".BackgroundImage");
            Size(b.CornerRadiusPt, path, nameof(b.CornerRadiusPt));
            Sizes(b.CornerRadiiPt, path, nameof(b.CornerRadiiPt));
            Sizes(b.CornerRadiiYPt, path, nameof(b.CornerRadiiYPt));
            Size(b.CornerRadiusPercent, path, nameof(b.CornerRadiusPercent));
            Sizes(b.CornerRadiiPercent, path, nameof(b.CornerRadiiPercent));
            Sizes(b.CornerRadiiYPercent, path, nameof(b.CornerRadiiYPercent));
            Shadows(b.Shadow, b.MoreShadows, path);
            Finite(b.Transform, path, nameof(b.Transform));
            Finite(b.TransformOrigin, path, nameof(b.TransformOrigin));
            Size(b.MinHeightPt, path, nameof(b.MinHeightPt));
            Finite(b.Opacity, path, nameof(b.Opacity));
        }

        private static void PageBorders(PageBorders? pb, string path)
        {
            if (pb == null) return;
            Border(pb.Top, path, nameof(pb.Top));
            Border(pb.Bottom, path, nameof(pb.Bottom));
            Border(pb.Left, path, nameof(pb.Left));
            Border(pb.Right, path, nameof(pb.Right));
            Size(pb.SpaceTop, path, nameof(pb.SpaceTop));
            Size(pb.SpaceBottom, path, nameof(pb.SpaceBottom));
            Size(pb.SpaceLeft, path, nameof(pb.SpaceLeft));
            Size(pb.SpaceRight, path, nameof(pb.SpaceRight));
        }

        private static void Border(Border? border, string path, string property)
        {
            if (border == null) return;
            Size(border.Width, path + "." + property, nameof(border.Width));
        }

        private static void Shadows(BoxShadow? first, List<BoxShadow>? more, string path)
        {
            Shadow(first, path + ".Shadow");
            if (more == null) return;
            for (int i = 0; i < more.Count; i++) Shadow(more[i], path + ".MoreShadows[" + i + "]");
        }

        private static void Shadow(BoxShadow? sh, string path)
        {
            if (sh == null) return;
            Finite(sh.OffsetXPt, path, nameof(sh.OffsetXPt));
            Finite(sh.OffsetYPt, path, nameof(sh.OffsetYPt));
            Size(sh.BlurPt, path, nameof(sh.BlurPt));
            Finite(sh.SpreadPt, path, nameof(sh.SpreadPt));
        }

        private static void Placement(AbsolutePlacement? p, string path, int depth, HashSet<object> seen)
        {
            if (p == null) return;
            // A placement's Following paragraphs recurse through Paragraph, not Block, so the cap Block
            // carries is not on this path: a long chain of distinct placed paragraphs overflowed the
            // stack here — uncatchably — before any exporter ran.
            if (depth > MaxWalkDepth) return;
            for (int i = 0; i < p.Following.Count; i++)
                if (p.Following[i] != null) Paragraph(p.Following[i], path + ".Following[" + i + "]", depth + 1, seen);
            for (int i = 0; i < p.FollowingBlocks.Count; i++)
                Block(p.FollowingBlocks[i], path + ".FollowingBlocks[" + i + "]", depth + 1, seen);
            Finite(p.LeftPt, path, nameof(p.LeftPt));
            Finite(p.TopPt, path, nameof(p.TopPt));
            Size(p.WidthPt, path, nameof(p.WidthPt));
            Finite(p.RightPt, path, nameof(p.RightPt));
            Finite(p.BottomPt, path, nameof(p.BottomPt));
            Finite(p.LeftFraction, path, nameof(p.LeftFraction));
            Finite(p.RightFraction, path, nameof(p.RightFraction));
            Finite(p.TopFraction, path, nameof(p.TopFraction));
            Finite(p.BottomFraction, path, nameof(p.BottomFraction));
            Finite(p.WidthFraction, path, nameof(p.WidthFraction));
            Size(p.BandHeightPt, path, nameof(p.BandHeightPt));
        }

        private static void Background(BackgroundImage? bg, string path)
        {
            if (bg == null) return;
            Finite(bg.WidthPt, path, nameof(bg.WidthPt));
            Finite(bg.HeightPt, path, nameof(bg.HeightPt));
            Finite(bg.PositionX, path, nameof(bg.PositionX));
            Finite(bg.PositionY, path, nameof(bg.PositionY));
        }

        // ------------------------------------------------------------------ what the exporters walk

        /// <summary>
        /// How many visits the exporters' walks may make beyond one an element before the document is
        /// refused. Their walks cap depth (<see cref="ModelRules.MaxListDepth"/>) and remember nothing,
        /// so sharing costs a visit a path: an honest document has none of it, and a million is seconds
        /// of export where the list whose three items each hold it was 3²¹.
        /// </summary>
        internal const long MaxRepeatedVisits = 1_000_000;

        /// <summary>
        /// Walks the document as the exporters do — no memory, each nesting to
        /// <see cref="ModelRules.MaxListDepth"/> — counting its elements, and refuses it past
        /// <paramref name="distinct"/> + <see cref="MaxRepeatedVisits"/>: the work is bounded by that
        /// figure whatever the graph.
        /// </summary>
        private static void Unfolded(Document document, int distinct)
        {
            long left = distinct + MaxRepeatedVisits;
            foreach (Section? s in document.Sections)
            {
                if (s == null) continue;
                UnfoldBand(s.Header?.Blocks, s.Header?.Paragraphs, ref left);
                UnfoldBand(s.Footer?.Blocks, s.Footer?.Paragraphs, ref left);
                UnfoldBand(s.FirstPageHeader?.Blocks, s.FirstPageHeader?.Paragraphs, ref left);
                UnfoldBand(s.FirstPageFooter?.Blocks, s.FirstPageFooter?.Paragraphs, ref left);
                UnfoldBand(s.EvenPageHeader?.Blocks, s.EvenPageHeader?.Paragraphs, ref left);
                UnfoldBand(s.EvenPageFooter?.Blocks, s.EvenPageFooter?.Paragraphs, ref left);
                UnfoldBlocks(s.Children, 0, ref left);
            }
        }

        private static void UnfoldBand(List<IDocElement>? blocks, List<Paragraph>? paragraphs, ref long left)
        {
            if (blocks != null && blocks.Count > 0) UnfoldBlocks(blocks, 0, ref left);
            else UnfoldBlocks(paragraphs, 0, ref left);
        }

        private static void UnfoldBlocks(IEnumerable<IDocElement>? blocks, int depth, ref long left)
        {
            if (blocks == null) return;
            foreach (IDocElement? block in blocks) UnfoldBlock(block, depth, ref left);
        }

        private static void UnfoldBlock(IDocElement? block, int depth, ref long left)
        {
            if (block == null || depth > ModelRules.MaxListDepth) return;
            if (--left < 0) throw Repeated();
            switch (block)
            {
                case Paragraph p:
                    UnfoldInlines(p.Runs, depth, ref left);
                    if (p.Placement is AbsolutePlacement place)
                    {
                        foreach (Paragraph? f in place.Following) UnfoldBlock(f, depth + 1, ref left);
                        UnfoldBlocks(place.FollowingBlocks, depth + 1, ref left);
                    }
                    break;
                case Table t:
                    foreach (TableRow? row in t.Rows)
                        if (row != null)
                            foreach (TableCell? cell in row.Cells)
                                if (cell != null)
                                    UnfoldBlocks(cell.Blocks.Count > 0 ? cell.Blocks : (IEnumerable<IDocElement>)cell.Paragraphs, depth + 1, ref left);
                    break;
                case ListBlock lb:
                    foreach (ListItem? li in lb.Items) UnfoldBlock(li, depth, ref left);
                    break;
                case ListItem li:
                    UnfoldInlines(li.Runs, depth, ref left);
                    UnfoldBlock(li.Sublist, depth + 1, ref left);
                    break;
            }
        }

        private static void UnfoldInlines(IList<IInlineElement>? runs, int depth, ref long left)
        {
            if (runs == null || depth > ModelRules.MaxListDepth) return;
            foreach (IInlineElement? run in runs)
                if (run is Footnote f)
                {
                    if (--left < 0) throw Repeated();
                    UnfoldInlines(f.Runs, depth + 1, ref left);
                }
        }

        private static ArgumentException Repeated() => new ArgumentException(
            "The document's elements are reached more than " + MaxRepeatedVisits.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) +
            " times beyond once each: a list, table, paragraph or footnote reachable from several places, or its own " +
            "descendant, is walked by every exporter once per path, and a list whose items each hold it doubles at every level.",
            ParamName);
    }
}
