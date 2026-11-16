// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Every producer in Base writes a composed run's mark attachment (GAPS.md §19c): the form field
/// appearance, the template engine's first line and its reflowed lines, the tagged document builder
/// and the SVG converter. With <see cref="MarkAttachmentFont"/> a bet with qamats is the qamats raised
/// by −50/1000 em under a text rise, moved 150 right by a <c>TJ</c> adjustment and back by 150 plus
/// its 300 advance, then the bet — <c>[-150 &lt;0002&gt; 450] TJ</c>. Each site used to write the
/// glyphs as one <c>&lt;00020001&gt; Tj</c>, which draws the point at the letter's left edge and moves
/// the letter 300 units right.
/// </summary>
public class MarkAttachmentProducerTests
{
    private static readonly string BetQamats = $"{F.Bet}{F.Qamats}";
    private const string PlacedQamats = "[-150 <0002> 450] TJ";

    [Fact]
    public void TheFormFieldAppearance_PlacesTheMark()
    {
        int next = 1;
        var objects = new List<IndirectObject>();
        var da = new DefaultAppearance { FontResourceName = "Helv", FontSize = 10, ColorOperators = "0 g" };
        var ap = FormAppearance.Build(BetQamats, 200, 20, 0, da, new FieldLayout(), null, null,
            new FormFillOptions { FontProvider = _ => F.Build() }, ref next, objects, out string error, out _);

        Assert.True(ap != null, "no appearance was built: " + error);
        Assert.Contains("-0.5 Ts\n" + PlacedQamats + "\n0 Ts\n<0001> Tj", Encoding.Latin1.GetString(ap!.RawData));
    }

    /// <summary>
    /// A reflowed paragraph whose replacement runs over two lines: thirty pointed bets, every one
    /// placed — the first line written by the engine's own site, the rest by the reflow's.
    /// </summary>
    [Fact]
    public void TheTemplateEngine_PlacesTheMarkOnEveryReflowedLine()
    {
        string words = string.Join(" ", Enumerable.Repeat("a", 20));
        string replacement = string.Join(" ", Enumerable.Repeat(BetQamats, 30));
        string content = "BT /F1 12 Tf\n" +
                         $"1 0 0 1 72 700 Tm (k {words}) Tj\n" +
                         $"1 0 0 1 72 686 Tm ({words}) Tj\n" +
                         "1 0 0 1 72 672 Tm (a) Tj\nET\n";

        using var output = new MemoryStream();
        TemplateReplaceResult result = PdfTextReflow.Replace(new MemoryStream(Page(content)), output,
            new Dictionary<string, string> { ["k"] = replacement },
            new TemplateReplaceOptions { FontProvider = _ => F.Build(), AllowReflowPastTheParagraph = true });

        Assert.Contains("k", result.ReplacedKeys);
        string produced = ContentOf(output.ToArray());
        Assert.True(Regex.Matches(produced, @"\d+(\.\d+)? \d+(\.\d+)? \d+(\.\d+)? \d+(\.\d+)? [\d.]+ [\d.]+ Tm").Count >= 2,
                    "the replacement did not run over a second line");
        Assert.Equal(30, Count(produced, "-0.6 Ts\n" + PlacedQamats + "\n0 Ts"));
    }

    [Fact]
    public void TheTaggedDocumentBuilder_PlacesTheMark()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build()).AddParagraph(BetQamats).Build(ms);

        Assert.Contains("-0.6 Ts\n" + PlacedQamats + "\n0 Ts\n<0001> Tj", ContentOf(ms.ToArray()));
    }

    /// <summary>
    /// The signature appearance places the mark at each row's own size: the signer's name at 9
    /// points (a rise of −0.45), the location at 8 (−0.4).
    /// </summary>
    [Fact]
    public void TheSignatureAppearance_PlacesTheMarkAtEachRowsSize()
    {
        int next = 10;
        var (ap, _) = SignatureAppearanceGenerator.Build(
            new SignatureAppearanceOptions { SignerName = BetQamats, Location = BetQamats, FontProvider = _ => F.Build(alsoCovers: "Location:") },
            ref next);

        string content = Encoding.Latin1.GetString(ap.RawData);
        Assert.Contains("-0.45 Ts\n" + PlacedQamats + "\n0 Ts\n<0001> Tj", content);
        Assert.Contains("-0.4 Ts\n" + PlacedQamats + "\n0 Ts\n<0001> Tj", content);
    }

    /// <summary>
    /// Moving a mark does not split its word for a reader: two pointed words extract as the two
    /// words, each letter with its point, and no space appears between a point and its letter.
    /// </summary>
    [Fact]
    public void APlacedMark_ExtractsWithItsLetter()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build()).AddParagraph($"{BetQamats}{BetQamats} {BetQamats}").Build(ms);

        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        Assert.Equal($"{BetQamats}{BetQamats} {BetQamats}", Core.Text.PdfTextExtractor.ExtractText(doc).Trim());
    }

    /// <summary>
    /// What a reader extracts is what was typed, through the two things a placed run does that a
    /// plain one does not: a holam moved 520 thousandths onto its bet — a <c>TJ</c> move wider than
    /// the 0.2 em the extractor reads as a word break, which split the word before the mark — and a
    /// bet with dagesh drawn as the presentation form U+FB31, whose two-character <c>/ToUnicode</c>
    /// text came out reversed (dagesh, then bet) when the right-to-left line was reordered. The
    /// comparison is canonical: the form gives the dagesh back before any other point.
    /// </summary>
    [Fact]
    public void APlacedOrComposedMark_ExtractsAsTyped()
    {
        string typed = $"{F.Bet}{F.Holam} {F.Bet}{F.Dagesh}";
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build(alsoCovers: "בּ")).AddParagraph(typed).Build(ms);

        string content = ContentOf(ms.ToArray());
        Assert.Contains("[-520 <0006> 520] TJ", content);   // the holam's move, wider than a word gap
        var glyphs = Regex.Matches(content, "<([0-9A-F]+)>").Cast<Match>()
            .SelectMany(m => Enumerable.Range(0, m.Groups[1].Value.Length / 4).Select(i => m.Groups[1].Value.Substring(4 * i, 4))).ToList();
        Assert.Contains("0004", glyphs);                     // the form's glyph, drawn for bet and dagesh
        Assert.DoesNotContain("0003", glyphs);               // and no dagesh glyph of its own

        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));
        string extracted = Core.Text.PdfTextExtractor.ExtractText(doc).Trim();
        Assert.Equal(typed.Normalize(NormalizationForm.FormD), extracted.Normalize(NormalizationForm.FormD));
    }

    /// <summary>
    /// Structured extraction groups by geometry, and a mark placed by its font's anchors has none of
    /// its own to group by: the holam, raised 0.71 em over its bet and 520 thousandths in, was a line
    /// of its own and its bet a word without it. Each mark now joins the letter it lies over, after
    /// it: two words, each a letter with its point.
    /// </summary>
    [Fact]
    public void APlacedMark_JoinsItsLettersWord_InStructuredExtraction()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build(alsoCovers: "בּ"))
            .AddParagraph($"{F.Bet}{F.Holam} {F.Bet}{F.Dagesh}").Build(ms);

        var words = PdfOperations.ExtractStructuredText(new MemoryStream(ms.ToArray()))
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).Select(w => w.Text.Normalize(NormalizationForm.FormD)).ToList();
        Assert.Equal(new[] { $"{F.Bet}{F.Holam}", $"{F.Bet}{F.Dagesh}" }, words);
    }

    /// <summary>
    /// A mark the font gives no width — <c>/W</c> says 0 for the dagesh — takes none in structured
    /// extraction: the two pointed bets are one word and the bare bet another. A private 0.6 em stood
    /// in for every zero width, which pushed each glyph after a dagesh 0.6 em along and broke the
    /// words where they were not.
    /// </summary>
    [Fact]
    public void AZeroWidthMark_TakesNoRoom_InStructuredExtraction()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => F.Build())
            .AddParagraph($"{F.Bet}{F.Dagesh}{F.Bet}{F.Dagesh} {F.Bet}").Build(ms);

        var words = PdfOperations.ExtractStructuredText(new MemoryStream(ms.ToArray()))
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).ToList();
        Assert.Equal(new[] { $"{F.Bet}{F.Dagesh}{F.Bet}{F.Dagesh}", $"{F.Bet}" }, words.Select(w => w.Text.Normalize(NormalizationForm.FormD)));
        Assert.All(words.SelectMany(w => w.Chars).Where(c => c.Char == $"{F.Dagesh}"), c => Assert.Equal(0f, c.Width));
    }

    /// <summary>
    /// The SVG converter places the mark beside its word spacing, the adjustment after each space
    /// riding in the same writer: two pointed bets, the space between them spaced.
    /// </summary>
    [Fact]
    public void TheSvgConverter_PlacesTheMark_BesideItsWordSpacing()
    {
        var (content, _, _) = SvgEmbeddedFontTests.Convert(
            $@"<text x=""10"" y=""20"" font-size=""10"" word-spacing=""10"">{BetQamats} {BetQamats}</text>", _ => F.Build());

        Assert.Equal(2, Count(content, "-0.5 Ts\n" + PlacedQamats + "\n0 Ts"));
        Assert.Contains("[<00010005> -1000] TJ", content);
    }

    /// <summary>
    /// SVG letter-spacing (§10.11) follows a typographic character — the letter with its point: the
    /// qamats takes the <c>Tc</c> back (200 thousandths at 10 points), and an end-anchored pointed
    /// bet starts where the bare bet does. Measuring a gap per glyph moved it 2 left.
    /// </summary>
    [Fact]
    public void TheSvgConverter_SpacesTheLetterWithItsMark()
    {
        string Text(string t) => $@"<text x=""100"" y=""20"" font-size=""10"" text-anchor=""end"" letter-spacing=""2"">{t}</text>";
        var (pointed, _, _) = SvgEmbeddedFontTests.Convert(Text(BetQamats), _ => F.Build());
        var (bare, _, _) = SvgEmbeddedFontTests.Convert(Text($"{F.Bet}"), _ => F.Build());

        Assert.Contains("-0.5 Ts\n[-150 <0002> 650] TJ\n0 Ts\n<0001> Tj", pointed);
        Assert.Equal(TdX(bare), TdX(pointed));
    }

    private static string TdX(string content)
    {
        var m = Regex.Match(content, @"(\S+) \S+ Td");
        Assert.True(m.Success, "no text position was written");
        return m.Groups[1].Value;
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
