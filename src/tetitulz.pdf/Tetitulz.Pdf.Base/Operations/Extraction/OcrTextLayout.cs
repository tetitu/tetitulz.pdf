// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Recognised words as a <see cref="PdfPageTextLayout"/>, so a scanned page goes through the
    /// same field and table extraction as a page with a text layer.
    /// </summary>
    /// <remarks>
    /// <para><b>Laid out as the page is displayed.</b> The engine reads the page rotated by its
    /// <c>/Rotate</c>, and its words come back in unrotated user space — on a page turned a quarter,
    /// a displayed line is a column there. The boxes are turned back into the displayed frame
    /// before anything is grouped.</para>
    /// <para><b>Straightened.</b> A scan is rarely level, and a degree of skew lifts one end of a
    /// table row by several points — more than a line's tolerance — so a row fell apart and its
    /// description was attached to the item above. The skew is measured from neighbouring words
    /// on one line (the median slope, up to five degrees) and sheared away, and each line's words
    /// are given one baseline.</para>
    /// <para><b>Each word gets glyphs, laid out the way the page draws them.</b> The reading-order
    /// walks are built from glyph positions, and a word without glyphs contributes nothing to
    /// them. An OCR word is a box and a logical-order string, so its characters are spread evenly
    /// across the box in their visual order — the bidi algorithm's, under the line's direction —
    /// so <c>ל-12</c> in a Hebrew line is drawn <c>12-ל</c>, as a page would draw it, and the
    /// extractor's reordering gives it back as written.</para>
    /// <para><b>Lines are words whose vertical centres lie within half a word's height of each
    /// other</b>, read right to left when most of their letters are, as the structured extractor
    /// reads a line.</para>
    /// </remarks>
    internal static class OcrTextLayout
    {
        /// <summary>The largest skew straightened: past it, the page is turned, not tilted.</summary>
        internal const float MaxSkewSlope = 0.0875f;   // tan 5 degrees

        // The most words the skew is measured over: each word is paired with the next on its line,
        // which can take a scan of every word after it.
        private const int MaxSkewWords = 4000;

        private struct Box
        {
            internal string Text;
            internal float X, Y, W, H;
            internal float CentreX => X + W / 2;
            internal float CentreY => Y + H / 2;
        }

        /// <param name="pageNumber">The page's index.</param>
        /// <param name="words">The engine's words, in unrotated user space.</param>
        /// <param name="rotation">The page's <c>/Rotate</c>, in degrees.</param>
        internal static PdfPageTextLayout Build(int pageNumber, IReadOnlyList<PdfOcrWord> words, int rotation = 0)
        {
            var boxes = Displayed((words ?? Array.Empty<PdfOcrWord>())
                .Where(w => w != null && !string.IsNullOrWhiteSpace(w.Text))
                .Select(w => new Box { Text = w.Text.Trim(), X = w.X, Y = w.Y, W = w.Width, H = w.Height })
                .ToList(), rotation);
            Straighten(boxes);

            var groups = new List<List<Box>>();
            foreach (var b in boxes.OrderByDescending(b => b.CentreY))
            {
                var home = groups.FirstOrDefault(g =>
                    Math.Abs(g.Average(x => x.CentreY) - b.CentreY) <= Math.Max(b.H, g.Max(x => x.H)) / 2);
                if (home == null) groups.Add(home = new List<Box>());
                home.Add(b);
            }

            var lines = new List<PdfTextLine>();
            foreach (var g in groups)
            {
                string text = string.Concat(g.Select(b => b.Text));
                bool rtl = text.Count(TextDirection.IsRtlChar) * 2 > text.Count(c => !char.IsWhiteSpace(c));
                float baseline = Median(g.Select(b => b.Y));
                var built = g.Select(b => ToWord(b, baseline, rtl)).ToList();
                var ordered = rtl ? built.OrderByDescending(w => w.X).ToList() : built.OrderBy(w => w.X).ToList();

                float x = ordered.Min(w => w.X), y = ordered.Min(w => w.Y);
                float right = ordered.Max(w => w.X + w.Width), top = ordered.Max(w => w.Y + w.Height);
                lines.Add(new PdfTextLine(TextDirection.ToReadingOrder(ordered, rightToLeft: rtl),
                                          x, y, right - x, top - y, ordered, rtl));
            }

            return new PdfPageTextLayout(pageNumber, lines.OrderByDescending(l => l.Y + l.Height).ToList());
        }

        /// <summary>
        /// The boxes turned from unrotated user space into the displayed page, for a page turned
        /// <paramref name="rotation"/> degrees clockwise; the words' own extent stands for the
        /// page's, which only moves every box alike.
        /// </summary>
        private static List<Box> Displayed(List<Box> boxes, int rotation)
        {
            int r = ((rotation % 360) + 360) % 360;
            if (r == 0 || boxes.Count == 0) return boxes;
            float width = boxes.Max(b => b.X + b.W), height = boxes.Max(b => b.Y + b.H);
            return boxes.Select(b =>
            {
                switch (r)
                {
                    case 90:  return new Box { Text = b.Text, X = b.Y, Y = width - b.X - b.W, W = b.H, H = b.W };
                    case 180: return new Box { Text = b.Text, X = width - b.X - b.W, Y = height - b.Y - b.H, W = b.W, H = b.H };
                    case 270: return new Box { Text = b.Text, X = height - b.Y - b.H, Y = b.X, W = b.H, H = b.W };
                    default:  return b;
                }
            }).ToList();
        }

        /// <summary>
        /// Shears away the page's skew: the median slope between each word and the next word from
        /// the left on its line — its centre within three quarters of a height of the first's —
        /// when at least three pairs give one under <see cref="MaxSkewSlope"/>.
        /// </summary>
        /// <remarks>
        /// A table row's words are a column apart, tens of points: a neighbour sought only a few
        /// heights away was never found on one, and a scanned item table's skew was never measured.
        /// A word of the row above or below is told apart by its centre, a row pitch away; the
        /// tilt itself limits how far a pair can reach, which is why steep skew pairs only near
        /// neighbours. A box lying over the first — an engine's duplicate — gives no slope (its
        /// run is nothing), and the median outvotes a box that overlaps it only in part. The page's
        /// words are at most <see cref="MaxSkewWords"/>, which bounds the search.
        /// </remarks>
        private static void Straighten(List<Box> boxes)
        {
            if (boxes.Count < 4 || boxes.Count > MaxSkewWords) return;
            var byX = boxes.OrderBy(b => b.X).ToList();
            var slopes = new List<float>();
            for (int i = 0; i < byX.Count; i++)
            {
                var a = byX[i];
                for (int j = i + 1; j < byX.Count; j++)
                {
                    var b = byX[j];
                    float rise = b.CentreY - a.CentreY;
                    if (Math.Abs(rise) > 0.75f * Math.Max(a.H, b.H)) continue;   // another line's word
                    float run = b.CentreX - a.CentreX;
                    if (run > 0) slopes.Add(rise / run);
                    break;
                }
            }
            if (slopes.Count < 3) return;
            float slope = Median(slopes);
            if (Math.Abs(slope) > MaxSkewSlope || slope == 0f) return;

            float pivot = (boxes.Min(b => b.X) + boxes.Max(b => b.X + b.W)) / 2;
            for (int i = 0; i < boxes.Count; i++)
            {
                var b = boxes[i];
                b.Y -= (b.CentreX - pivot) * slope;
                boxes[i] = b;
            }
        }

        private static float Median(IEnumerable<float> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2;
        }

        // The word's characters spread across its box in their visual order under the line's
        // direction: the reordered string's characters, mirrored where the reorder mirrors, since
        // the extractor's own reorder mirrors them back.
        private static PdfTextWord ToWord(Box b, float baseline, bool lineRtl)
        {
            string text = b.Text;
            string visual = UnicodeBidi.Reorder(text, lineRtl ? BaseDirection.RightToLeft : BaseDirection.LeftToRight) ?? text;
            int n = Math.Max(1, visual.Length);
            float per = b.W / n;
            bool rtl = text.Count(TextDirection.IsRtlChar) * 2 > text.Count(c => !char.IsWhiteSpace(c));

            var chars = new List<PdfTextChar>(visual.Length);
            for (int i = 0; i < visual.Length; i++)
                chars.Add(new PdfTextChar(visual[i].ToString(), b.X + i * per, baseline, per, b.H, TextDirection.IsRtlChar(visual[i])));
            return new PdfTextWord(text, b.X, baseline, b.W, b.H, chars, rtl);
        }
    }
}
