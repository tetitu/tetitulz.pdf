// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>@media</c> width features evaluated against the page's content width — what a browser's print
/// evaluates them against — or <see cref="HtmlToPdfOptions.ViewportWidth"/>.
/// </summary>
/// <remarks>
/// <para>Every breakpoint applying at once would let a responsive stylesheet's last tier win and
/// every <c>hidden-*</c> rule hide. The fixtures are Bootstrap 3's own shapes — a column that floats at
/// <c>min-width: 768px</c>, an element hidden at <c>max-width: 767px</c> — and each is asserted
/// both ways round, so a result that never changes cannot pass.</para>
/// <para>US Letter portrait with the default margins is 739 CSS px of content: below Bootstrap's
/// 768px <c>sm</c> breakpoint, as it is when the page is printed from a browser.</para>
/// </remarks>
public class MediaViewportTests
{
    private const string SmGrid =
        "<style>.col{width:100%} @media (min-width:768px){.col{float:left;width:50%}}</style>" +
        "<div><div class='col'>LEFTCOL</div><div class='col'>RIGHTCOL</div></div>";

    private const string HiddenXs =
        "<style>@media (max-width:767px){.hidden-xs{display:none}}</style>" +
        "<p>VISIBLE</p><p class='hidden-xs'>WIDEONLY</p>";

    private static List<PdfTextWord> Words(string html, HtmlToPdfOptions options)
    {
        using var input = new MemoryStream(ToPdf(html, options), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }

    private static PdfTextWord Find(List<PdfTextWord> words, string text)
    {
        var w = words.FirstOrDefault(x => x.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(w != null, $"\"{text}\" was not laid out at all");
        return w!;
    }

    private static bool SideBySide(HtmlToPdfOptions options)
    {
        var words = Words(SmGrid, options);
        return Math.Abs(Find(words, "LEFTCOL").Y - Find(words, "RIGHTCOL").Y) < 2.0;
    }

    private static HtmlToPdfOptions With(Action<HtmlToPdfOptions> set)
    {
        var o = Managed();
        set(o);
        return o;
    }

    [Fact]
    public void AtThePagesWidth_AnSmColumnStacks_AndAWideViewportPutsItSideBySide()
    {
        Assert.False(SideBySide(Managed()), "a 739px page matched min-width:768px");
        Assert.True(SideBySide(With(o => o.ViewportWidth = 1200)), "a 1200px viewport did not match min-width:768px");
    }

    [Fact]
    public void AtThePagesWidth_HiddenXsHides_AndAWideViewportShowsIt()
    {
        var narrow = Words(HiddenXs, Managed());
        var wide = Words(HiddenXs, With(o => o.ViewportWidth = 1200));

        Assert.Contains(narrow, w => w.Text.Contains("VISIBLE", StringComparison.Ordinal));
        Assert.DoesNotContain(narrow, w => w.Text.Contains("WIDEONLY", StringComparison.Ordinal));
        Assert.Contains(wide, w => w.Text.Contains("WIDEONLY", StringComparison.Ordinal));
    }

    /// <summary>A landscape Letter page is 979 CSS px of content, so it reaches the sm tier.</summary>
    [Fact]
    public void ALandscapePageIsWiderAndReachesTheSmTier()
    {
        Assert.True(SideBySide(With(o => o.Landscape = true)));
    }

    /// <summary>
    /// <see cref="HtmlToPdfOptions.Scale"/> below 1 fits more content on the page, so the viewport
    /// grows by the same factor — as a browser's print scale does.
    /// </summary>
    [Fact]
    public void AScaleBelowOneWidensTheViewport()
    {
        Assert.True(SideBySide(With(o => o.Scale = 0.5)));        // 739 / 0.5 = 1478px
        Assert.False(SideBySide(With(o => o.Scale = 1.0)));
    }

    /// <summary>A narrower page than Letter is narrower still: A5 portrait does not reach sm either.</summary>
    [Fact]
    public void ThePageSizeDecidesTheViewport()
    {
        Assert.False(SideBySide(With(o => { o.PageWidth = "148mm"; o.PageHeight = "210mm"; })));
        Assert.True(SideBySide(With(o => { o.PageWidth = "297mm"; o.PageHeight = "420mm"; })));   // A3: 1046px
    }

    /// <summary><c>&lt;source media&gt;</c> is evaluated against the same viewport.</summary>
    [Fact]
    public void APictureSourceMediaIsEvaluatedAgainstTheViewport()
    {
        using var dir = NewScratchDir();
        dir.Write("wide.png", MakeRgbPng(9, 9));
        dir.Write("narrow.png", MakeRgbPng(3, 3));
        const string Html =
            "<picture><source media='(min-width: 1000px)' srcset='wide.png'>" +
            "<img src='narrow.png' width='20'></picture>";

        string narrow = Raw(ToPdf(Html, With(o => o.BasePath = dir.Path)));
        string wide = Raw(ToPdf(Html, With(o => { o.BasePath = dir.Path; o.ViewportWidth = 1200; })));

        Assert.Contains("/Width 3", narrow);
        Assert.DoesNotContain("/Width 9", narrow);
        Assert.Contains("/Width 9", wide);
    }
}
