// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// <c>float</c> on an inline element — Bootstrap's <c>.pull-right</c> button, a list-group
    /// item's <c>.badge</c>, an alert's <c>.close</c>: lifted out of its line into a floated
    /// paragraph (<c>Paragraph.Float</c>) placed just before it, so the line's text flows beside it.
    /// </summary>
    /// <remarks>
    /// <para><b>Recorded while collecting, lifted after mapping.</b> Whether a paragraph can carry a
    /// float is known only once it has a place: in a section's flow the exporter floats it, and so
    /// does a table cell since the cell's layout took floats. So the floated element's
    /// runs are collected in place, as they always were, and remembered; after the body is mapped,
    /// each one whose paragraph is in the flow or in a cell is moved into a float before it. The
    /// others stay inline — the layout they had — and are reported.</para>
    /// <para><b>Exact on the line it occurs on.</b> CSS puts a float at the top of the line it
    /// occurs on. A float on a paragraph's first line lifts before the paragraph, which starts
    /// where the line does; one after a <c>&lt;br&gt;</c> is on a later line, so the paragraph is
    /// split at the last break before it (<see cref="SplitAtBreak"/>) and the float lifts before the
    /// tail. A paragraph with a box would draw the box twice if split, so its box moves to a
    /// <c>BoxStart</c>/<c>BoxEnd</c> round every piece first (<see cref="BoxOf"/>) — its
    /// borders, padding, shading, background image, corner radius and shadow, its spacing
    /// the box's margins. A transform has no box to move to, so a float after a break in a transformed
    /// paragraph stays inline, reported.</para>
    /// <para><b>Any number per side, per line</b>: the exporter places a float inward
    /// of the floats already on its line (<c>PdfExporter.FloatBox</c>), so each lifts in document
    /// order — the first to the edge, the next beside it, never one drawn over another.</para>
    /// <para><b>A lifted float keeps with its line</b> (<c>KeepWithNext</c>): drawn at the foot of a
    /// page, it would otherwise stay there while the line it belongs to moved to the next.</para>
    /// </remarks>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>A floated inline element's runs, in the paragraph they were collected into.</summary>
        private sealed class InlineFloat
        {
            public InlineFloat(M.Paragraph host, List<M.IInlineElement> runs, FloatSide side, IElement element)
            {
                Host = host; Runs = runs; Side = side; Element = element;
            }

            public M.Paragraph Host { get; }
            public List<M.IInlineElement> Runs { get; }
            public FloatSide Side { get; }
            public IElement Element { get; }
        }

        private readonly List<InlineFloat> _inlineFloats = new List<InlineFloat>();

        /// <summary>How deep a table in a cell in a table is followed for hosts; a model this deep is not one HTML made.</summary>
        private const int MaxInlineFloatTableDepth = 32;

        /// <summary>Remembers a floated inline element's runs, collected at <paramref name="first"/> onwards.</summary>
        /// <remarks>
        /// Not a list item: a floated <c>&lt;li&gt;</c> reaches inline collection only through
        /// <c>CollectInlineList</c>, which lays a row of them — Bootstrap's tabs — out as one line on
        /// purpose. Lifting the first would take it out of its row.
        /// </remarks>
        private void RecordInlineFloat(IElement el, M.Paragraph para, int first)
        {
            if (para.Runs.Count <= first || NameIs(el, HtmlTags.Li)) return;
            FloatSide side = ResolveFloatSide(el);
            if (side == FloatSide.None) return;
            _inlineFloats.Add(new InlineFloat(para, para.Runs.GetRange(first, para.Runs.Count - first), side, el));
        }

        /// <summary>
        /// Moves each recorded float whose paragraph is in <paramref name="sec"/>'s flow, or in a
        /// cell of a table in it, into a floated paragraph just before it, and reports the ones left
        /// inline.
        /// </summary>
        private void LiftInlineFloats(M.Section sec)
        {
            if (_inlineFloats.Count == 0) return;

            var byHost = new Dictionary<M.Paragraph, List<InlineFloat>>();
            foreach (InlineFloat f in _inlineFloats)
            {
                if (!byHost.TryGetValue(f.Host, out var list)) byHost[f.Host] = list = new List<InlineFloat>();
                list.Add(f);
            }

            int lifted = 0, nested = 0;
            LiftIn(sec.Children, byHost, SectionContentWidth(sec), ref lifted, ref nested, depth: 0);

            int inline = _inlineFloats.Count - lifted - nested;
            if (inline > 0)
                ReportDiagnostic(BuilderDiagnosticKind.ContentApproximated,
                    $"float: {inline} floated inline element(s) were laid out in their line — each had " +
                    "no width to float, or its runs were no longer in its paragraph. Use HtmlRenderEngine.Browser where that matters.");
        }

        /// <summary>
        /// Lifts the recorded floats whose hosts are among <paramref name="blocks"/> — a section's
        /// children, or a cell's — and, for each table among them, in its cells.
        /// </summary>
        /// <remarks>
        /// A host is walked float by float in document order. A float after a line break splits the
        /// host there, and the tail becomes the host for it and those after it: its own line, with
        /// both sides free again.
        /// </remarks>
        private void LiftIn(List<M.IDocElement> blocks, Dictionary<M.Paragraph, List<InlineFloat>> byHost,
            double contentWidth, ref int lifted, ref int nested, int depth)
        {
            if (depth > MaxInlineFloatTableDepth) return;
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is M.Table table)
                {
                    foreach (M.TableRow row in table.Rows)
                        foreach (M.TableCell cell in row.Cells)
                            LiftIn(cell.Blocks, byHost, contentWidth, ref lifted, ref nested, depth + 1);
                    continue;
                }
                if (blocks[i] is not M.Paragraph host || !byHost.TryGetValue(host, out var floats)) continue;
                // A floated paragraph never hosts one: TryMapExactFloat sends such an element to the
                // band, whose cell is walked above. A placed paragraph has no line to lift before.
                if (host.Float != M.FloatSide.None || host.Placement != null) continue;

                M.Paragraph current = host;
                int at = i;
                bool sharedBox = false;
                // A boxed host's box goes round it and its floats as a BoxStart, whatever line a
                // float is on: CSS paints a block's background before its floats, and a float lifted
                // before a paragraph that kept its box was painted over by it (a .badge in a white
                // .list-group-item vanished) and sat outside its padding. BoxOf leaves the host
                // plain, so no piece has a box of its own. Its transform goes with it.
                if (HasBox(current) && floats.Exists(f => !IsInsideAnother(f, floats)))
                {
                    blocks.Insert(at, BoxOf(current));
                    at++;
                    sharedBox = true;
                }
                foreach (InlineFloat f in floats)
                {
                    // A float inside another is carried by it: its runs are among the outer one's.
                    if (IsInsideAnother(f, floats)) { nested++; continue; }
                    int run = IndexOfRun(current.Runs, f.Runs[0]);
                    if (run < 0) continue;
                    int lastBreak = LastBreakBefore(current.Runs, run);
                    if (lastBreak >= 0)
                    {
                        if (SplitAtBreak(current, lastBreak) is not { } tail) continue;
                        blocks.Insert(at + 1, tail);
                        current = tail;
                        at++;
                    }
                    if (LiftOne(f, current, contentWidth) is not { } p) continue;
                    // In document order before the host: the exporter places each inward of the last.
                    blocks.Insert(at, p);
                    at++;
                    lifted++;
                }
                if (sharedBox)
                {
                    blocks.Insert(at + 1, new M.BoxEnd());
                    at++;
                }
                i = at;
            }
        }

        private static bool IsInsideAnother(InlineFloat f, List<InlineFloat> floats)
        {
            foreach (InlineFloat other in floats)
                if (!ReferenceEquals(other, f) && other.Runs.Count > f.Runs.Count && other.Runs.Contains(f.Runs[0]))
                    return true;
            return false;
        }

        /// <summary>The index of the last line break before <paramref name="run"/>, or −1.</summary>
        private static int LastBreakBefore(List<M.IInlineElement> runs, int run)
        {
            for (int k = run - 1; k >= 0; k--)
                if (runs[k] is M.LineBreak) return k;
            return -1;
        }

        /// <summary>
        /// Splits <paramref name="host"/> at the line break at <paramref name="at"/>: the runs after
        /// it go to a new paragraph of the same style that follows the host, kept with it, its
        /// spacing after moved there; the break itself goes, since the host now ends there. A
        /// paragraph with a box — a border, shading, a background image, a shadow, a corner radius,
        /// an inset — or a transform is not split, since the box would become two: <c>null</c>.
        /// </summary>
        private static M.Paragraph? SplitAtBreak(M.Paragraph host, int at)
        {
            if (HasBox(host)) return null;
            var tail = new M.Paragraph
            {
                Alignment = host.Alignment,
                Direction = host.Direction,
                LineSpacingPt = host.LineSpacingPt,
                LineHeightFactor = host.LineHeightFactor,
                IndentLeft = host.IndentLeft,
                IndentRight = host.IndentRight,
                SpacingBefore = 0,
                SpacingAfter = host.SpacingAfter,
                Opacity = host.Opacity,
                Role = host.Role,
                IsArtifact = host.IsArtifact,
                Orphans = host.Orphans,
                Widows = host.Widows,
                KeepTogether = host.KeepTogether,
                KeepWithNext = host.KeepWithNext,
                Hyphenate = host.Hyphenate,
            };
            tail.Tabs.AddRange(host.Tabs);
            tail.Runs.AddRange(host.Runs.GetRange(at + 1, host.Runs.Count - at - 1));
            host.Runs.RemoveRange(at, host.Runs.Count - at);
            host.SpacingAfter = 0;
            host.KeepWithNext = true;
            return tail;
        }

        /// <summary>Whether a paragraph paints a box round itself, or transforms its painting, which a split would double.</summary>
        private static bool HasBox(M.Paragraph p)
            => p.BorderTop != null || p.BorderBottom != null || p.BorderLeft != null || p.BorderRight != null
               || p.Shading.Length > 0 || p.BackgroundImage != null || p.Shadow != null || p.CornerRadiusPt > 0
               || p.CornerRadiiPt != null || p.CornerRadiusPercent > 0
               || p.BoxInsetTop > 0 || p.BoxInsetRight > 0 || p.BoxInsetBottom > 0 || p.BoxInsetLeft > 0
               || p.Transform != null;

        /// <summary>
        /// The paragraph's box as a <see cref="M.BoxStart"/> to go round it and the pieces it is split
        /// into: its borders, its insets as padding, its shading, background image, corner radius,
        /// shadow and transform (the box's <c>cm</c> round the pieces), and what
        /// is left of its spacing as the box's margins — so the space stays outside the border, as CSS
        /// margins do. The paragraph is left plain, so no piece draws a box or turns its painting of its own.
        /// </summary>
        /// <remarks>
        /// <c>ApplyBlockStyle</c> writes a paragraph's padding twice: into its indents and spacing,
        /// where the text goes, and into its insets, where the box is drawn out from the text. The
        /// box takes the insets as padding, so the indents and spacing give them back, or the padding
        /// would be laid out twice.
        /// </remarks>
        private static M.BoxStart BoxOf(M.Paragraph p)
        {
            // The paragraph's frame (padding and border) per side; the box draws its border in
            // its own band, so its padding is the frame less the border.
            var frame = ParagraphFrame(p);
            var box = new M.BoxStart
            {
                // Negative too: a vertical margin may be (Bootstrap's list items overlap by -1px).
                MarginTop = p.SpacingBefore - frame.Top, MarginBottom = p.SpacingAfter - frame.Bottom,
                BorderTop = p.BorderTop, BorderRight = p.BorderRight, BorderBottom = p.BorderBottom, BorderLeft = p.BorderLeft,
                PaddingTop = frame.Top - M.Border.PaintedWidth(p.BorderTop), PaddingRight = frame.Right - M.Border.PaintedWidth(p.BorderRight),
                PaddingBottom = frame.Bottom - M.Border.PaintedWidth(p.BorderBottom), PaddingLeft = frame.Left - M.Border.PaintedWidth(p.BorderLeft),
                Shading = p.Shading, BackgroundImage = p.BackgroundImage, CornerRadiusPt = p.CornerRadiusPt,
                CornerRadiiPt = p.CornerRadiiPt, CornerRadiiYPt = p.CornerRadiiYPt, CornerRadiusPercent = p.CornerRadiusPercent,
                CornerRadiiPercent = p.CornerRadiiPercent, CornerRadiiYPercent = p.CornerRadiiYPercent,
                Shadow = p.Shadow,
                Transform = p.Transform,
                TransformOrigin = p.TransformOrigin,
                // The box's group carries the opacity; the pieces inside draw opaque.
                Opacity = p.Opacity,
            };
            p.Opacity = 1.0;
            box.MoreShadows.AddRange(p.MoreShadows);
            p.MoreShadows.Clear();
            p.IndentLeft = Math.Max(0, p.IndentLeft - frame.Left);
            p.IndentRight = Math.Max(0, p.IndentRight - frame.Right);
            p.SpacingBefore = 0; p.SpacingAfter = 0;
            p.BorderTop = p.BorderRight = p.BorderBottom = p.BorderLeft = null;
            p.BoxInsetTop = p.BoxInsetRight = p.BoxInsetBottom = p.BoxInsetLeft = 0;
            // Clearing CornerRadiiYPt is an equivalent mutation: with no horizontal radii every corner is square whatever the vertical ones say.
            // It is cleared so the paragraph holds no half of a box it no longer paints.
            p.Shading = ""; p.BackgroundImage = null; p.CornerRadiusPt = 0; p.CornerRadiiPt = null; p.CornerRadiiYPt = null; p.CornerRadiusPercent = 0; p.CornerRadiiPercent = null; p.CornerRadiiYPercent = null; p.Shadow = null; p.Transform = null; p.TransformOrigin = null;
            return box;
        }

        /// <summary>
        /// The floated paragraph for one recorded float, its runs moved out of
        /// <paramref name="host"/> — or <c>null</c>, leaving the host untouched, when the runs are
        /// gone or have no width.
        /// </summary>
        /// <remarks>
        /// A recorded float's runs are a contiguous slice of its host, and nothing but a lift takes
        /// runs out of a paragraph; floats in one host are disjoint or nested, and a nested one is
        /// skipped. So finding the first run finds the slice. It is not found only where two
        /// floats hold the same runs — a floated element whose whole content is another — and the
        /// inner one, recorded first, took them.
        /// </remarks>
        private M.Paragraph? LiftOne(InlineFloat f, M.Paragraph host, double contentWidth)
        {
            int at = IndexOfRun(host.Runs, f.Runs[0]);
            if (at < 0) return null;

            var p = new M.Paragraph
            {
                Float = f.Side == FloatSide.Left ? M.FloatSide.Left : M.FloatSide.Right,
                // Its top where the line's is, and nothing after: a float does not move the flow.
                SpacingBefore = host.SpacingBefore,
                SpacingAfter = 0,
                LineSpacingPt = host.LineSpacingPt,
                LineHeightFactor = host.LineHeightFactor,
                Direction = host.Direction,
                Alignment = host.Alignment,
                Opacity = host.Opacity,
                Hyphenate = host.Hyphenate,
                KeepWithNext = true,
            };
            p.Runs.AddRange(f.Runs);
            // Its first line starts inside it, so its own leading space is one CSS removes. A
            // trailing space — its own, or the host's before a float that ended the line — needs
            // nothing: the layout already leaves a line's last space out of its width.
            var first = p.Runs[0] as M.TextRun;
            string? leading = first?.Text;
            if (first != null) first.Text = first.Text.TrimStart(' ');

            double width = FloatTrack(f.Element, contentWidth);
            if (width <= 0) width = Measurer.MeasureShrinkToFitWidth(p, contentWidth);
            if (width <= 0)
            {
                // Nothing to float: the runs stay in the line as they were collected.
                if (first != null) first.Text = leading!;
                return null;
            }
            p.FloatWidth = width;

            host.Runs.RemoveRange(at, f.Runs.Count);
            // The space after it now starts the line, where CSS removes it.
            if (at < host.Runs.Count && host.Runs[at] is M.TextRun after && (at == 0 || host.Runs[at - 1] is M.LineBreak))
                after.Text = after.Text.TrimStart(' ');
            return p;
        }

        private static int IndexOfRun(List<M.IInlineElement> runs, M.IInlineElement run)
        {
            for (int i = 0; i < runs.Count; i++)
                if (ReferenceEquals(runs[i], run)) return i;
            return -1;
        }
    }
}
