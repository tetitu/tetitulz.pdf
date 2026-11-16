// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>box-shadow</c> lists and <c>inset</c>: every Bootstrap <c>.form-control</c>, <c>.well</c>
    /// and <c>.progress</c> has an inset shadow, which dropped the whole declaration with a report;
    /// only the first of a list was drawn; and a box of blocks read no shadow at all.
    /// </summary>
    public class InsetShadowTests
    {
        private static (string Content, List<string> Reports) Render(string body)
        {
            var reports = new List<string>();
            var o = Managed();
            o.OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) reports.Add(d.Detail); };
            return (DecodeStreams(ToPdf("<html><body>" + body + "</body></html>", o)), reports);
        }

        /// <summary>A hard inset shadow is drawn — its colour over the background — and not reported.</summary>
        [Fact]
        public void AnInsetShadowIsDrawn()
        {
            var (c, reports) = Render("<div style='background-color:#0000ff;box-shadow:inset 0 0 0 4px #ff0000'>A</div>");
            int blue = c.IndexOf("0 0 1 rg", StringComparison.Ordinal);
            Assert.True(blue >= 0 && c.IndexOf("1 0 0 rg", StringComparison.Ordinal) > blue, "no inset shadow over the background");
            Assert.DoesNotContain(reports, r => r.Contains("inset", StringComparison.Ordinal));
        }

        /// <summary>Bootstrap's <c>.well</c> shadow, blurred and translucent, is drawn as an image over the background.</summary>
        [Fact]
        public void AWellsBlurredInsetShadowIsDrawn()
        {
            var (c, _) = Render("<div style='background-color:#f5f5f5;box-shadow:inset 0 1px 1px rgba(0,0,0,.05)'>A</div>");
            Assert.Single(Regex.Matches(c, @"/\w+\s+Do\b"));
        }

        /// <summary>Every shadow of a list is drawn, and nothing reports the list.</summary>
        [Fact]
        public void EveryShadowOfAListIsDrawn()
        {
            var (c, reports) = Render("<div style='background-color:#0000ff;box-shadow:2px 2px #ff0000, 6px 6px #00ff00'>A</div>");
            Assert.Contains("1 0 0 rg", c);
            Assert.Contains("0 1 0 rg", c);
            Assert.DoesNotContain(reports, r => r.Contains("several", StringComparison.Ordinal));
        }

        /// <summary>A box of blocks casts its shadow — a panel's, a well's — where it cast none; a box with no shadow draws no red (the control).</summary>
        [Fact]
        public void ABoxOfBlocksCastsItsShadow()
        {
            var (c, _) = Render("<div style='box-shadow:0 0 0 2px #ff0000'><p>A</p><p>B</p></div>");
            Assert.Contains("1 0 0 rg", c);
            var (plain, _) = Render("<div style='border:1px solid #000'><p>A</p><p>B</p></div>");
            Assert.DoesNotContain("1 0 0 rg", plain);
        }
    }
}
