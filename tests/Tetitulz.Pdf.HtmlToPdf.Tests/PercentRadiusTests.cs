// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// A percentage <c>border-radius</c> — Bootstrap's <c>.img-circle</c> is <c>50%</c> and
    /// nothing else — which read as 0, and an image's box that did not exist for a radius alone, so
    /// a circular avatar printed square.
    /// </summary>
    public class PercentRadiusTests
    {
        private static readonly string Png = Convert.ToBase64String(MakeRgbPng(8, 8));

        private static List<(double Dx, double Dy)> CurveExtents(string c)
        {
            var extents = new List<(double, double)>();
            double px = 0, py = 0;
            foreach (Match m in Regex.Matches(c, @"((?:[-\d.]+ )+)(m|l|c)\n"))
            {
                double[] v = m.Groups[1].Value.Trim().Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                double ex = v[v.Length - 2], ey = v[v.Length - 1];
                if (m.Groups[2].Value == "c") extents.Add((Math.Abs(ex - px), Math.Abs(ey - py)));
                px = ex; py = ey;
            }
            return extents;
        }

        /// <summary>The clipping path before the image's <c>Do</c>, or null when the image is not clipped.</summary>
        private static string? ClipBeforeImage(string content)
        {
            int image = Regex.Match(content, @"cm\s*/\w+\s+Do\b").Index;
            int clip = content.LastIndexOf("W", image, StringComparison.Ordinal);
            if (clip < 0 || !Regex.IsMatch(content.Substring(clip, image - clip), @"^W\s*n\b")) return null;
            int save = content.LastIndexOf("q\n", clip, StringComparison.Ordinal);
            return content.Substring(save, clip - save);
        }

        private static string Img(string style) => DecodeStreams(ToPdf(
            $"<html><body><img src='data:image/png;base64,{Png}' width='40' height='20' style='{style}'></body></html>"));

        /// <summary>
        /// <c>.img-circle</c>: an image at 50% is clipped to an ellipse its own size — 40 × 20px is
        /// 30 × 15pt, so four curves 15 across and 7.5 down; an image with no radius is not clipped
        /// (the control).
        /// </summary>
        [Fact]
        public void AnImageAtFiftyPercentIsClippedToAnEllipse()
        {
            string? clip = ClipBeforeImage(Img("border-radius:50%"));
            Assert.NotNull(clip);
            var e = CurveExtents(clip!);
            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.Equal(15, x.Dx, 2); Assert.Equal(7.5, x.Dy, 2); });

            Assert.Null(ClipBeforeImage(Img("")));
        }

        /// <summary><c>.img-rounded</c>: a length radius on an image, with nothing else, clips it too — 6px is 4.5pt.</summary>
        [Fact]
        public void AnImageWithALengthRadiusIsClipped()
        {
            var e = CurveExtents(ClipBeforeImage(Img("border-radius:6px"))!);
            Assert.Equal(4, e.Count);
            Assert.All(e, x => { Assert.Equal(4.5, x.Dx, 2); Assert.Equal(4.5, x.Dy, 2); });
        }

        /// <summary>A block at 50% is rounded by half its width across: four curves alike, wider than tall.</summary>
        [Fact]
        public void ABlockAtFiftyPercentIsRounded()
        {
            var e = CurveExtents(DecodeStreams(ToPdf("<html><body><div style='background-color:#0000ff;border-radius:50%'>A</div></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => Assert.Equal(e[0].Dx, x.Dx, 2));
            Assert.True(e[0].Dx > 4 * e[0].Dy, $"a full-width block's corner {e[0].Dx} × {e[0].Dy} is not an ellipse");
        }

        /// <summary>A box of blocks at 50% takes it too.</summary>
        [Fact]
        public void ABoxOfBlocksAtFiftyPercentIsRounded()
        {
            var e = CurveExtents(DecodeStreams(ToPdf("<html><body><div style='background-color:#0000ff;border-radius:50%'><p>A</p><p>B</p></div></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => Assert.Equal(e[0].Dx, x.Dx, 2));
        }

        /// <summary>A paragraph whose box moves round a lifted float keeps its percentage.</summary>
        [Fact]
        public void AMovedBoxKeepsItsPercent()
        {
            var e = CurveExtents(DecodeStreams(ToPdf("<html><body><div style='background-color:#0000ff;border-radius:50%'>A <span style='float:right'>N</span></div></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.True(e[0].Dx > 4 * e[0].Dy, $"the moved box's corner {e[0].Dx} × {e[0].Dy} is not the block's half-width ellipse");
        }

        /// <summary>A padded inline box at 50% — a round badge — is rounded by its sides.</summary>
        [Fact]
        public void AnInlineBoxAtFiftyPercentIsRounded()
        {
            var e = CurveExtents(DecodeStreams(ToPdf("<html><body><p>A <span style='background-color:#0000ff;padding:2px;border-radius:50%'>B</span></p></body></html>")));
            Assert.Equal(4, e.Count);
            Assert.All(e, x => Assert.Equal(e[0].Dy, x.Dy, 2));
        }

        /// <summary>
        /// A percentage among several values, or in a longhand, is applied at its corner
        /// (<c>PerCornerPercentTests</c>) and nothing is reported — it is not read as 0 — and the corners
        /// are drawn (the potency).
        /// </summary>
        [Fact]
        public void APerCornerPercent_IsNotReported()
        {
            static (List<string> Reports, string Content) Lay(string style)
            {
                var seen = new List<string>();
                var o = Managed();
                o.OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) seen.Add(d.Detail); };
                byte[] pdf = ToPdf($"<html><body><div style='background-color:#0000ff;padding:10px;{style}'>A</div></body></html>", o);
                return (seen, DecodeStreams(pdf));
            }
            var mixed = Lay("border-radius:50% 10px");
            var longhand = Lay("border-top-left-radius:50%");
            Assert.DoesNotContain(mixed.Reports, r => r.Contains("percentage", StringComparison.Ordinal));
            Assert.DoesNotContain(longhand.Reports, r => r.Contains("percentage", StringComparison.Ordinal));
            Assert.Equal(4, CurveExtents(mixed.Content).Count);
            Assert.Single(CurveExtents(longhand.Content));
        }
    }
}
