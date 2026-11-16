// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> draws the content of <c>&lt;a&gt;</c> (SVG 1.1 §17.1) and of
/// <c>&lt;switch&gt;</c> (§5.8).
/// </summary>
/// <remarks>
/// Both were reported as not drawn and dropped with everything inside them: a linked label vanished,
/// and a diagram exporter's text — a <c>foreignObject</c> label with an SVG <c>&lt;text&gt;</c>
/// fallback inside a switch — came out empty. An <c>&lt;a&gt;</c> is a group whose link this
/// converter does not keep, and says so; a switch draws its first child it can render.
/// </remarks>
public class SvgContainerTests
{
    /// <summary>
    /// An anchor's content is drawn as a group, transform included, and nothing is reported: its
    /// link is kept since batch 30 (<c>SvgLinkTests</c>), where batch 24 reported it as not kept.
    /// </summary>
    [Fact]
    public void AnAnchorsContentIsDrawnAsAGroup()
    {
        string c = Content(@"<a href=""https://example.org/"" transform=""translate(5, 0)""><rect width=""10"" height=""10""/></a>");

        Assert.Equal(new[] { 5f, 0, 10, 10 }, Assert.Single(Operands(c, "re")));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void AnAnchorsPaintIsInherited()
    {
        string c = Content(@"<a href=""#x"" fill=""#0000ff""><rect width=""10"" height=""10""/></a>");

        Assert.Contains("0.0000 0.0000 1.0000 rg", Lines(c));
    }

    /// <summary>The draw.io shape: a foreignObject label first, the SVG text fallback second.</summary>
    [Fact]
    public void ASwitchFallsBackPastAForeignObject()
    {
        string c = Content(
            @"<switch><foreignObject requiredFeatures=""http://www.w3.org/TR/SVG11/feature#Extensibility"" width=""10"" height=""10""><p xmlns=""http://www.w3.org/1999/xhtml"">rich</p></foreignObject>" +
            @"<text x=""1"" y=""2"">fallback</text></switch>");

        Assert.Contains("(fallback) Tj", Lines(c));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    [Fact]
    public void ASwitchSkipsAChildThatRequiresAnExtension()
    {
        string c = Content(
            @"<switch><rect requiredExtensions=""http://example.org/ext"" width=""1"" height=""1""/><rect x=""5"" width=""2"" height=""2""/></switch>");

        Assert.Equal(new[] { 5f, 0, 2, 2 }, Assert.Single(Operands(c, "re")));
    }

    /// <summary>A switch draws one child, the first it can: the second rect is not drawn.</summary>
    [Fact]
    public void ASwitchDrawsOnlyItsFirstDrawableChild()
    {
        string c = Content(
            @"<switch transform=""translate(0, 3)""><!-- c --><rect width=""1"" height=""1""/><rect x=""5"" width=""2"" height=""2""/></switch>");

        Assert.Equal(new[] { 0f, 3, 1, 1 }, Assert.Single(Operands(c, "re")));
    }

    /// <summary>
    /// <c>systemLanguage</c> (SVG 2 §5.8.5) chooses a switch's child by the reader's languages: a listed
    /// tag that is the reader's, or that the reader's is the prefix of up to a <c>-</c> (<c>en</c> reads
    /// <c>en-GB</c>), case folded; a child with no condition is the fallback.
    /// </summary>
    [Fact]
    public void ASwitchChoosesItsChildBySystemLanguage()
    {
        const string body = @"<switch><rect systemLanguage=""he"" width=""1"" height=""1""/>" +
                            @"<rect systemLanguage=""fr, EN-GB"" x=""5"" width=""2"" height=""2""/><rect x=""9"" width=""3"" height=""3""/></switch>";
        Assert.Equal(0f, Assert.Single(Operands(Content(body, new[] { "he" }), "re"))[0]);
        Assert.Equal(5f, Assert.Single(Operands(Content(body, new[] { "en" }), "re"))[0]);
        Assert.Equal(5f, Assert.Single(Operands(Content(body, new[] { "de", "FR" }), "re"))[0]);
        Assert.Equal(9f, Assert.Single(Operands(Content(body, new[] { "en-US" }), "re"))[0]);   // en-US is no prefix of en-GB
        Assert.Equal(9f, Assert.Single(Operands(Content(body, new string[0]), "re"))[0]);
    }

    /// <summary>
    /// Conditional processing applies outside a switch too: an element whose conditions are false is not
    /// drawn, nor its children — a <c>systemLanguage</c> the reader does not read, an empty one, any
    /// <c>requiredExtensions</c>.
    /// </summary>
    [Fact]
    public void AConditionThatIsFalse_HidesTheElementAnywhere()
    {
        const string group = @"<g systemLanguage=""fr""><rect width=""1"" height=""1""/></g><rect x=""5"" width=""2"" height=""2""/>";
        Assert.Equal(new[] { 5f }, Operands(Content(group, new[] { "en" }), "re").Select(o => o[0]));
        Assert.Equal(new[] { 0f, 5f }, Operands(Content(group, new[] { "fr" }), "re").Select(o => o[0]));

        Assert.Empty(Operands(Content(@"<rect systemLanguage="""" width=""1"" height=""1""/>", new[] { "en" }), "re"));
        Assert.Empty(Operands(Content(@"<rect requiredExtensions="""" width=""1"" height=""1""/>", new[] { "en" }), "re"));
        Assert.Single(Operands(Content(@"<rect width=""1"" height=""1""/>", new[] { "en" }), "re"));
    }

    /// <summary>
    /// A <c>systemLanguage</c> past its length cap is not read and does not hold, though it lists the
    /// reader's language; the same list under the cap does.
    /// </summary>
    [Fact]
    public void ASystemLanguagePastItsCap_DoesNotHold()
    {
        string Rect(string languages) => $@"<rect systemLanguage=""{languages}"" width=""1"" height=""1""/>";
        string under = string.Join(",", Enumerable.Repeat("xx", 300)) + ",en";      // 902 characters
        string over = string.Join(",", Enumerable.Repeat("xx", 400)) + ",en";       // 1 202
        Assert.Single(Operands(Content(Rect(under), new[] { "en" }), "re"));
        Assert.Empty(Operands(Content(Rect(over), new[] { "en" }), "re"));
    }

    /// <summary>With no languages given, the host's UI culture and its parents are the reader's: a <c>he-IL</c> host reads <c>he</c>.</summary>
    [Fact]
    public void WithNoLanguagesGiven_TheHostsUiCultureReads()
    {
        CultureInfo saved = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("he-IL");
            const string body = @"<switch><rect systemLanguage=""he"" width=""1"" height=""1""/><rect x=""9"" width=""3"" height=""3""/></switch>";
            Assert.Equal(0f, Assert.Single(Operands(Content(body), "re"))[0]);
            CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
            Assert.Equal(9f, Assert.Single(Operands(Content(body), "re"))[0]);
        }
        finally { CultureInfo.CurrentUICulture = saved; }
    }

    // ------------------------------------------------------------------ helpers

    private static string Content(string body, IList<string>? languages = null)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false, Languages = languages });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();

    private static float[][] Operands(string content, string op)
        => Lines(content)
            .Where(l => l.EndsWith(" " + op, StringComparison.Ordinal))
            .Select(l => l.Substring(0, l.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.All(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            .Select(parts => parts.Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
}
