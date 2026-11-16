// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.LigatureFont;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Every producer in Base draws a font's required ligatures (<c>GSUB</c> <c>rlig</c>) and writes
/// each back in <c>/ToUnicode</c> as its letters: the form field appearance, the template engine's
/// first line and its reflowed lines, the tagged document builder and the SVG converter. With
/// <see cref="F"/>'s <c>rlig</c> font, f then i is the fi ligature, <c>&lt;0004&gt; Tj</c>, and its
/// <c>/ToUnicode</c> entry <c>&lt;0004&gt; &lt;00660069&gt;</c>; a registry that dropped the text
/// would write the f alone, <c>&lt;0004&gt; &lt;0066&gt;</c>.
/// </summary>
public class LigatureProducerTests
{
    private const string Ligature = "<0004> Tj";
    private const string LigatureThenA = "<00040007> Tj";
    private const string Letters = "<0004> <00660069>";
    private const string FirstLetterOnly = "<0004> <0066>";

    /// <summary>
    /// The form appearance draws in a composite font only for a value a simple font cannot carry, so
    /// the value holds an ā beside the f and i: <c>&lt;00040007&gt; Tj</c>, the ligature then the ā.
    /// </summary>
    [Fact]
    public void TheFormFieldAppearance_FormsTheLigature_AndWritesItsLetters()
    {
        int next = 1;
        var objects = new List<IndirectObject>();
        var da = new DefaultAppearance { FontResourceName = "Helv", FontSize = 10, ColorOperators = "0 g" };
        var ap = FormAppearance.Build("fiā", 200, 20, 0, da, new FieldLayout(), null, null,
            new FormFillOptions { FontProvider = _ => F.Build(feature: "rlig") }, ref next, objects, out string error, out _);

        Assert.True(ap != null, "no appearance was built: " + error);
        Assert.Contains(LigatureThenA, Encoding.Latin1.GetString(ap!.RawData));
        AssertLetters(string.Concat(objects.Select(o => o.Object).OfType<PdfStream>().Select(s => Encoding.Latin1.GetString(s.GetDecodedData()))));
    }

    /// <summary>
    /// A replacement that reflows over two lines, every line holding the ligature: the first written
    /// by the engine's own site, the rest by the reflow's — one registry between them.
    /// </summary>
    [Fact]
    public void TheTemplateEngine_FormsTheLigature_OnEveryReflowedLine()
    {
        string replacement = string.Join(" ", Enumerable.Repeat("fi", 30));
        (string produced, string streams) = Template(replacement);

        Assert.True(System.Text.RegularExpressions.Regex.Matches(produced, @"[\d.]+ [\d.]+ Tm").Count >= 2,
                    "the replacement did not run over a second line");
        Assert.Equal(30, Count(produced, "0004"));
        AssertLetters(streams);
    }

    /// <summary>
    /// The two sites write one registry, so each has to be caught alone: a replacement that fits its
    /// line is the engine's own site's; one whose ligature falls only on a reflowed line is the
    /// reflow's. Either dropping the text would write the f alone.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTemplateEngine_WritesTheLetters_FromEitherSiteAlone(bool reflowed)
    {
        string replacement = reflowed ? string.Join(" ", Enumerable.Repeat("a", 40)) + " fi" : "fi";
        (string produced, string streams) = Template(replacement);

        Assert.Equal(1, Count(produced, "0004"));
        AssertLetters(streams);
    }

    /// <summary>The page content and every decoded stream of the fixture page with its key replaced.</summary>
    private static (string Content, string Streams) Template(string replacement)
    {
        string words = string.Join(" ", Enumerable.Repeat("a", 20));
        string content = "BT /F1 12 Tf\n" +
                         $"1 0 0 1 72 700 Tm (k {words}) Tj\n" +
                         $"1 0 0 1 72 686 Tm ({words}) Tj\n" +
                         "1 0 0 1 72 672 Tm (a) Tj\nET\n";

        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTextReflow.Replace(new MemoryStream(Page(content)), output,
            new Dictionary<string, string> { ["k"] = replacement },
            new TemplateReplaceOptions { FontProvider = _ => F.Build(feature: "rlig"), AllowReflowPastTheParagraph = true });
        Assert.Contains("k", result.ReplacedKeys);
        return (ContentOf(output.ToArray()), PdfContentProbe.DecodeStreams(output.ToArray()));
    }

    [Fact]
    public void TheTaggedDocumentBuilder_FormsTheLigature_AndWritesItsLetters()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build(feature: "rlig")).AddParagraph("fi").Build(ms);

        Assert.Contains(Ligature, ContentOf(ms.ToArray()));
        AssertLetters(PdfContentProbe.DecodeStreams(ms.ToArray()));

        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        Assert.Equal("fi", Core.Text.PdfTextExtractor.ExtractText(doc).Trim());
    }

    /// <summary>
    /// The signature appearance draws in a composite font only for text a simple font cannot carry,
    /// so the name holds an ā beside the f and i: the ligature forms and its letters are written.
    /// </summary>
    [Fact]
    public void TheSignatureAppearance_FormsTheLigature_AndWritesItsLetters()
    {
        int next = 10;
        var (ap, side) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = "fiā", FontProvider = _ => F.Build(feature: "rlig") },
            ref next);

        Assert.True(ap != null, "no appearance was built");
        Assert.Contains(LigatureThenA, Encoding.Latin1.GetString(ap!.RawData));
        AssertLetters(string.Concat(side.Select(o => o.Item2).OfType<PdfStream>().Select(s => Encoding.Latin1.GetString(s.GetDecodedData()))));
    }

    /// <summary>The SVG converter, likewise, embeds a font for text a standard face cannot carry.</summary>
    [Fact]
    public void TheSvgConverter_FormsTheLigature_AndWritesItsLetters()
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""><text x=""10"" y=""20"" font-size=""10"">fi&#x101;</text></svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FontProvider = _ => F.Build(feature: "rlig"), AllowSystemFontSubstitution = false });

        Assert.Contains(LigatureThenA, ContentOf(pdf));
        AssertLetters(PdfContentProbe.DecodeStreams(pdf));
    }

    private static void AssertLetters(string streams)
    {
        Assert.Contains(Letters, streams);
        Assert.DoesNotContain(FirstLetterOnly, streams);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0;
        for (int at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string ContentOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static byte[] Page(string content)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty, content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        return FormPdfFixtures.Build(bodies);
    }
}
