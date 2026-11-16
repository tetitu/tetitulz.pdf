// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>overflow</c> on a box of blocks clips its content to its padding box — Bootstrap's
    /// <c>.progress</c> track, rounded 4px with <c>overflow: hidden</c>, clips the square bar inside
    /// it, which printed with square ends over the track's rounded ones (found against Chrome).
    /// </summary>
    public class OverflowClipTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private static string Track(string overflow) =>
            "<div style='background-color:#eeeeee;border-radius:4px;" + overflow + "'><div style='background-color:#0000ff'>60%</div></div>";

        /// <summary>A rounded track with <c>overflow: hidden</c> clips its bar — a rounded clip set before the bar's fill.</summary>
        [Fact]
        public void AProgressTrackClipsItsBar()
        {
            string c = Content(Track("overflow:hidden"));
            var clip = Regex.Match(c, @"W\s+n");
            int bar = c.IndexOf("0 0 1 rg", StringComparison.Ordinal);
            Assert.True(clip.Success && clip.Index < bar, "the bar is not clipped");
            Assert.True(Regex.Matches(c.Substring(0, clip.Index), @"\bc\n").Count >= 4, "the clip is not rounded");
        }

        /// <summary>Either axis clips — <c>overflow-x: auto</c>, as a table's responsive wrapper sets it; <c>visible</c> clips nothing (the control).</summary>
        [Fact]
        public void EitherAxisClipsAndVisibleDoesNot()
        {
            Assert.Matches(@"W\s+n", Content(Track("overflow-x:auto")));
            Assert.DoesNotMatch(@"W\s+n", Content(Track("overflow:visible")));
        }
    }
}
