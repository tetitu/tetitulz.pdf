// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> applies a clipPath's own <c>clip-path</c> (SVG 1.1 §14.3.5): the
/// region is the clipPath's shapes intersected with the clip it names, and so on down the chain.
/// </summary>
/// <remarks>
/// It was left out without a word, so the element was clipped by the first clipPath alone. Each case
/// below clips a red 200 × 200 square; a point inside every clip of the chain is red, and one outside
/// any of them is the white page.
/// </remarks>
public class SvgClipChainTests
{
    private const string Square = @"<rect width=""200"" height=""200"" fill=""#ff0000"" clip-path=""url(#c1)""/>";

    [Fact]
    public void AClipPathsOwnClipPathIntersectsIt()
    {
        var at = Render(
            @"<defs><clipPath id=""c1"" clip-path=""url(#c2)""><rect width=""100"" height=""100""/></clipPath>" +
            @"<clipPath id=""c2""><rect x=""50"" width=""100"" height=""100""/></clipPath></defs>" + Square);

        AssertRed(at(75, 50));      // in both
        AssertWhite(at(25, 50));    // in c1 alone
        AssertWhite(at(125, 50));   // in c2 alone
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The chain is followed to its end: the third clip cuts the top half away.</summary>
    [Fact]
    public void TheChainIsFollowedToItsEnd()
    {
        var at = Render(
            @"<defs><clipPath id=""c1"" clip-path=""url(#c2)""><rect width=""100"" height=""100""/></clipPath>" +
            @"<clipPath id=""c2"" clip-path=""url(#c3)""><rect x=""50"" width=""100"" height=""100""/></clipPath>" +
            @"<clipPath id=""c3""><rect y=""50"" width=""200"" height=""150""/></clipPath></defs>" + Square);

        AssertRed(at(75, 75));
        AssertWhite(at(75, 25));
    }

    /// <summary>
    /// A clip down the chain in <c>objectBoundingBox</c> units is in the element's box, though the
    /// first is in user space: x 0.5–1 of a 100-wide rect is 50–100.
    /// </summary>
    [Fact]
    public void AClipDownTheChainIsMeasuredInTheElementsBox()
    {
        var at = Render(
            @"<defs><clipPath id=""c1"" clip-path=""url(#c2)""><rect width=""200"" height=""200""/></clipPath>" +
            @"<clipPath id=""c2"" clipPathUnits=""objectBoundingBox""><rect x=""0.5"" width=""0.5"" height=""1""/></clipPath></defs>" +
            @"<rect width=""100"" height=""100"" fill=""#ff0000"" clip-path=""url(#c1)""/>");

        AssertRed(at(75, 50));
        AssertWhite(at(25, 50));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>A chain that leads back to a clip already in it is set once each, and reported.</summary>
    [Theory]
    [InlineData(@"<clipPath id=""c1"" clip-path=""url(#c2)""><rect width=""100"" height=""100""/></clipPath><clipPath id=""c2"" clip-path=""url(#c1)""><rect x=""50"" width=""100"" height=""100""/></clipPath>", 2)]
    [InlineData(@"<clipPath id=""c1"" clip-path=""url(#c1)""><rect width=""100"" height=""100""/></clipPath>", 1)]
    public void ACyclicChainIsSetOnceEach(string clips, int count)
    {
        string body = "<defs>" + clips + "</defs>" + Square;
        string[] lines = Lines(Content(body));

        Assert.Equal(count, lines.Count(l => l is "W" or "W*"));
        Assert.Contains("leads back", SvgToPdfConverter.LastError, StringComparison.Ordinal);
        AssertRed(Render(body)(75, 50));
    }

    /// <summary>
    /// A hostile chain of many clips, closed into a cycle at its end: every clip is set once and the
    /// conversion ends. Unguarded, the cycle is followed forever.
    /// </summary>
    [Fact]
    public void AHostileLongCyclicChainEnds()
    {
        const int n = 2000;
        var defs = new StringBuilder("<defs>");
        for (int i = 1; i <= n; i++)
            defs.Append($@"<clipPath id=""c{i}"" clip-path=""url(#c{(i == n ? 1 : i + 1)})""><rect width=""100"" height=""100""/></clipPath>");
        defs.Append("</defs>");

        string[] lines = Lines(Content(defs + Square));

        Assert.Equal(n, lines.Count(l => l == "W"));
    }

    /// <summary>
    /// A hostile chain of clipPaths that each need a mask (two children under different rules), each
    /// naming the next: every link is a soft mask, and the conversion ends. The masks were built by a
    /// recursion one call a link, which a chain this long took past the stack — uncatchable.
    /// </summary>
    [Fact]
    public void AdversarialLongChainOfMaskClips_IsConvertedLinkByLink()
    {
        const int n = 25_000;
        var defs = new StringBuilder("<defs>");
        for (int i = 1; i <= n; i++)
            defs.Append($@"<clipPath id=""c{i}""{(i < n ? $@" clip-path=""url(#c{i + 1})""" : "")}>" +
                        @"<rect width=""100"" height=""100""/><rect x=""10"" width=""50"" height=""50"" clip-rule=""evenodd""/></clipPath>");
        defs.Append("</defs>");

        byte[] pdf = Convert(defs + Square);

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(1, doc.Pages.Count);
        var states = doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.ExtGState])
            as Tetitulz.Pdf.Core.Objects.PdfDictionary;
        Assert.NotNull(states);
        Assert.Equal(n, states!.Count(s => doc.ResolveObject(s.Value) is Tetitulz.Pdf.Core.Objects.PdfDictionary state
                                            && state[Tetitulz.Pdf.Core.Objects.PdfName.SMask] != null));
    }

    /// <summary>A reference down the chain that names no clipPath is reported; the clips before it apply.</summary>
    [Fact]
    public void ABrokenReferenceDownTheChainIsReported()
    {
        var at = Render(
            @"<defs><clipPath id=""c1"" clip-path=""url(#nothing)""><rect width=""100"" height=""100""/></clipPath></defs>" + Square);

        Assert.Contains("names no clipPath", SvgToPdfConverter.LastError, StringComparison.Ordinal);
        AssertRed(at(75, 50));
        AssertWhite(at(125, 50));
    }

    /// <summary>
    /// A <c>clip-path</c> on a shape inside a clipPath clips that shape's part of the region (batch
    /// 55, <c>SvgClipMaskTests</c>): the clipPath is a mask, and its shape's clip applies. It was
    /// reported and left out, and before that left out without a word.
    /// </summary>
    [Fact]
    public void AClipPathOnAShapeInsideAClipPathApplies()
    {
        var at = Render(
            @"<defs><clipPath id=""c2""><rect width=""10"" height=""10""/></clipPath>" +
            @"<clipPath id=""c1""><rect width=""100"" height=""100"" clip-path=""url(#c2)""/></clipPath></defs>" + Square);

        Assert.Null(SvgToPdfConverter.LastError);
        AssertRed(at(5, 5));
        AssertWhite(at(75, 50));    // the shape's own clip cuts this
    }

    /// <summary><c>none</c> names nothing, on a clipPath and on a shape inside one: nothing to report, and the clip applies.</summary>
    [Theory]
    [InlineData(@"<clipPath id=""c1"" clip-path=""none""><rect width=""100"" height=""100""/></clipPath>")]
    [InlineData(@"<clipPath id=""c1""><rect width=""100"" height=""100"" clip-path=""none""/></clipPath>")]
    public void NoneIsNotReported(string clip)
    {
        var at = Render("<defs>" + clip + "</defs>" + Square);

        Assert.Null(SvgToPdfConverter.LastError);
        AssertRed(at(75, 50));
        AssertWhite(at(125, 50));
    }

    /// <summary>
    /// A clip down the chain in <c>objectBoundingBox</c> units on an element with no box — a group
    /// that draws nothing — is reported, and no clip is set, though the first clip is in user space.
    /// </summary>
    [Fact]
    public void ABoxClipDownTheChainOnAnElementWithNoBoxIsReported()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c1"" clip-path=""url(#c2)""><rect width=""200"" height=""200""/></clipPath>" +
            @"<clipPath id=""c2"" clipPathUnits=""objectBoundingBox""><rect width=""0.5"" height=""1""/></clipPath></defs>" +
            @"<g clip-path=""url(#c1)""></g>"));

        Assert.Contains("box is not known", SvgToPdfConverter.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(lines, l => l is "W" or "W*");
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertRed((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 0, 5);
    }

    private static void AssertWhite((int R, int G, int B) p)
    {
        Assert.InRange(p.R, 250, 255);
        Assert.InRange(p.G, 250, 255);
    }

    private static byte[] Convert(string body)
        => SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200 });

    private static string Content(string body)
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(Convert(body)));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content)
        => content.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

    private static Func<int, int, (int R, int G, int B)> Render(string body)
    {
        var page = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(Convert(body)), 0, 72);
        return (x, y) => { int i = y * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }
}
