// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfXConverter"/> — PDF/X remediation, which did not exist: a caller could learn from
/// <see cref="PdfXValidator"/> that a file was non-conforming and had no supported way to act on
/// it (GAPS.md §12d).
/// <para>
/// The converter deliberately does only what can be done by rewriting metadata and the output
/// intent. Font embedding, page-box repair, colour conversion and transparency flattening are
/// <b>reported, never half-done</b> — a file that claims PDF/X and is not reaches a RIP.
/// </para>
/// </summary>
public class PdfXConverterTests
{
    // ------------------------------------------------------------------
    //  The identifier, and whether the claim is warranted
    // ------------------------------------------------------------------

    /// <summary>
    /// Everything the converter can supply is supplied, so it declares — and the shipped
    /// validator agrees the result conforms. This is the control that gives the withholding
    /// tests meaning, and it is also what verifies the <c>GTS_PDFXVersion</c> string: it is
    /// checked against <see cref="PdfXValidator"/> rather than asserted from memory.
    /// </summary>
    [Theory]
    [InlineData(PdfXConformanceLevel.X1a)]
    [InlineData(PdfXConformanceLevel.X3)]
    public void FullySuppliedInput_DeclaresAndValidatesClean(PdfXConformanceLevel level)
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf()), output,
            new PdfXConversionOptions
            {
                Level = level,
                IccProfile = FakeCmykProfile(),
                OutputConditionIdentifier = "FOGRA39",
                Trapped = false,
            });

        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
        Assert.True(result.DeclaredPdfXIdentifier);
        Assert.True(result.EmbeddedOutputProfile);
        Assert.True(result.SetTrapped);

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()), level);
        Assert.True(check.IsConforming,
            string.Join(" | ", check.Violations.Select(x => x.Code + ": " + x.Message)));
    }

    /// <summary>
    /// Converting a converted file leaves one <c>GTS_PDFX</c> intent. The writer appended a second
    /// beside the first, so the second conversion's own validation found X11 — two intents, an
    /// ambiguous printing condition — and withheld the identifier from a file that was already
    /// PDF/X. The first conversion is the control: it is conformant, so any blocker on the second
    /// is the second's doing.
    /// </summary>
    [Fact]
    public void ConvertingTwiceLeavesOneOutputIntent()
    {
        var options = new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false };

        var once = new MemoryStream();
        var first = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf()), once, options);
        Assert.True(first.IsConformant, string.Join(" | ", first.Blockers));

        var twice = new MemoryStream();
        var second = PdfXConverter.Convert(new MemoryStream(once.ToArray()), twice, options);

        Assert.DoesNotContain(second.Blockers, b => b.StartsWith("X11"));
        Assert.True(second.IsConformant, string.Join(" | ", second.Blockers));
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(twice.ToArray()));
        var intents = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfArray>(
            doc.ResolveObject(doc.Catalog.Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.OutputIntents]));
        Assert.Single(intents);
    }

    /// <summary>
    /// A non-embedded font is X04, which this converter cannot fix. It must say so and must not
    /// stamp the file as PDF/X.
    /// </summary>
    [Fact]
    public void NonEmbeddedFont_WithholdsTheIdentifierAndReportsX04()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        Assert.False(result.IsConformant);
        Assert.False(result.DeclaredPdfXIdentifier);
        Assert.Contains(result.Blockers, b => b.StartsWith("X04"));

        // ...and the output really carries no PDF/X claim.
        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.Contains(check.Violations, x => x.Code == "X03");
    }

    /// <summary>
    /// <c>/Trapped</c> is a question only the producer can answer — the PDF default is
    /// <c>/Unknown</c>, exactly the non-answer ISO 15930-1 §6.2.2 refuses. Not supplying it is a
    /// blocker rather than something the converter guesses.
    /// </summary>
    [Fact]
    public void TrappedNotSupplied_IsABlocker()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf()), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile() });   // no Trapped

        Assert.False(result.SetTrapped);
        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("X10"));
    }

    /// <summary>
    /// X-1a admits no substitute for the embedded profile (X11), so omitting it blocks.
    /// </summary>
    [Fact]
    public void NoIccProfile_IsABlockerForX1a()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf()), output,
            new PdfXConversionOptions { Trapped = false });   // no profile

        Assert.False(result.EmbeddedOutputProfile);
        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("X11"));
    }

    /// <summary>The documented opt-out still stamps the file, for callers finishing the job themselves.</summary>
    [Fact]
    public void DeclareRegardless_WritesTheIdentifierAnyway()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                DeclareConformanceRegardless = true,
            });

        Assert.False(result.IsConformant);
        Assert.True(result.DeclaredPdfXIdentifier);

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, x => x.Code == "X03");
    }

    // ------------------------------------------------------------------
    //  What it fixes
    // ------------------------------------------------------------------

    /// <summary>
    /// A JavaScript <c>/OpenAction</c> is X07; the sanitize step removes it. Asserted through the
    /// validator so the test states the requirement rather than the mechanism.
    /// </summary>
    [Fact]
    public void ForbiddenAction_IsRemoved()
    {
        byte[] input = OpenActionJavaScriptPdf();
        Assert.Contains("JavaScript", Encoding.ASCII.GetString(input));

        var output = new MemoryStream();
        PdfXConverter.Convert(new MemoryStream(input), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, x => x.Code == "X07");
    }

    /// <summary>
    /// X16 forbids embedded files, so the converter removes them as it removes script. The input
    /// is the control: it reports X16 before conversion, so a clean output is the converter's doing.
    /// </summary>
    [Fact]
    public void EmbeddedFile_IsRemoved()
    {
        byte[] input = EmbeddedFilePdf();
        Assert.Contains(PdfXValidator.Validate(new MemoryStream(input)).Violations, x => x.Code == "X16");

        var output = new MemoryStream();
        PdfXConverter.Convert(new MemoryStream(input), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, x => x.Code == "X16");
        Assert.DoesNotContain("/EmbeddedFiles", Encoding.ASCII.GetString(output.ToArray()));
    }

    /// <summary>
    /// The X-4 version string has no year suffix where X-1a and X-3 do, so it exercises a
    /// different branch of the validator's prefix match. Pinned separately for that reason.
    /// </summary>
    [Fact]
    public void X4VersionString_IsAcceptedByTheValidator()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf(pdfVersion: "1.6")), output,
            new PdfXConversionOptions
            {
                Level = PdfXConformanceLevel.X4,
                IccProfile = FakeCmykProfile(),
                OutputConditionIdentifier = "FOGRA39",
                Trapped = false,
            });

        Assert.True(result.DeclaredPdfXIdentifier, string.Join(" | ", result.Blockers));

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()), PdfXConformanceLevel.X4);
        Assert.DoesNotContain(check.Violations, x => x.Code == "X03");
    }

    // ------------------------------------------------------------------
    //  Font embedding (X04), through the caller-supplied seam
    // ------------------------------------------------------------------

    /// <summary>
    /// The embedder's output is what the rest of the conversion builds on — not merely invoked and
    /// discarded. Asserted by having the delegate emit a document that is recognisably different
    /// and then finding that difference in the final bytes, which is the only way to tell "the
    /// seam works" from "the seam is called".
    /// </summary>
    [Fact]
    public void SuppliedEmbedder_OutputIsCarriedIntoTheResult()
    {
        byte[] marked = TemplateMarker(NonEmbeddedFontPdf());

        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                EmbedFonts = (input, o) => { o.Write(marked, 0, marked.Length); return true; },
            });

        Assert.True(result.EmbeddedFonts);
        Assert.Contains("TetitulzEmbedMarker", Encoding.ASCII.GetString(output.ToArray()));
    }

    /// <summary>
    /// An embedder that declines leaves the document untouched and X04 standing — the seam must not
    /// silently adopt an empty or unchanged result.
    /// </summary>
    [Fact]
    public void EmbedderThatDeclines_LeavesTheDocumentAndTheBlocker()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                EmbedFonts = (input, o) => false,
            });

        Assert.False(result.EmbeddedFonts);
        Assert.Contains(result.Blockers, b => b.StartsWith("X04"));
        Assert.True(output.Length > 0, "the conversion must still produce the document");
    }

    /// <summary>
    /// A throwing embedder is a reason X04 stays open, not a reason to lose the conversion. The
    /// exception is reported and the output is still written.
    /// </summary>
    [Fact]
    public void EmbedderThatThrows_IsReportedAndTheConversionSurvives()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                EmbedFonts = (input, o) => throw new InvalidOperationException("no font licence"),
            });

        Assert.False(result.EmbeddedFonts);
        Assert.Contains(result.Blockers, b => b.Contains("font embedding failed") && b.Contains("no font licence"));
        Assert.True(output.Length > 0);
    }

    /// <summary>
    /// The control: with no embedder the behaviour is exactly what it was before the seam existed.
    /// </summary>
    [Fact]
    public void NoEmbedder_BehavesAsBefore()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NonEmbeddedFontPdf()), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        Assert.False(result.EmbeddedFonts);
        Assert.Contains(result.Blockers, b => b.StartsWith("X04"));
    }

    /// <summary>A valid PDF carrying a marker string, so its bytes are identifiable downstream.</summary>
    private static byte[] TemplateMarker(byte[] pdf)
    {
        string s = Encoding.ASCII.GetString(pdf);
        s = s.Replace("/Producer (test)", "/Producer (TetitulzEmbedMarker)");
        return Encoding.ASCII.GetBytes(s);
    }

    // ------------------------------------------------------------------
    //  Page geometry (X05)
    // ------------------------------------------------------------------

    /// <summary>
    /// A page declaring neither box is X05. The converter supplies a <c>/TrimBox</c>, and the
    /// shipped validator agrees the result conforms — asserted through the validator so the test
    /// states the requirement rather than the mechanism.
    /// </summary>
    [Fact]
    public void PageWithNoBoxes_GetsATrimBoxAndValidatesClean()
    {
        byte[] input = NoPageBoxesPdf();
        var before = PdfXValidator.Validate(new MemoryStream(input));
        Assert.Contains(before.Violations, x => x.Code == "X05");   // the fixture reaches the check

        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(input), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                OutputConditionIdentifier = "FOGRA39",
                Trapped = false,
            });

        Assert.Equal(1, result.PagesGivenTrimBox);
        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, x => x.Code == "X05");
    }

    /// <summary>
    /// The synthesised box comes from the <b>/CropBox</b>, not the <c>/MediaBox</c>: the visible
    /// page is what "finished size" means. A fixture whose two boxes are equal cannot tell those
    /// apart, so this one makes them differ.
    /// </summary>
    [Fact]
    public void SynthesisedTrimBox_ComesFromTheCropBox_NotTheMediaBox()
    {
        var output = new MemoryStream();
        PdfXConverter.Convert(new MemoryStream(NoPageBoxesPdf(cropBox: "/CropBox [10 20 150 160]")),
            output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        Assert.Equal(new[] { 10d, 20d, 150d, 160d }, WrittenTrimBox(output.ToArray()));
    }

    /// <summary>
    /// With no <c>/CropBox</c> the visible page <i>is</i> the <c>/MediaBox</c>, so that is the
    /// fallback. Paired with the test above, the two pin which box is preferred.
    /// </summary>
    [Fact]
    public void SynthesisedTrimBox_FallsBackToTheMediaBox()
    {
        var output = new MemoryStream();
        PdfXConverter.Convert(new MemoryStream(NoPageBoxesPdf()), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        Assert.Equal(new[] { 0d, 0d, 200d, 200d }, WrittenTrimBox(output.ToArray()));
    }

    /// <summary>
    /// Both boxes present is also X05 (§6.2.4 says one <i>or</i> the other). Removing one discards
    /// a statement the producer made, so by default the converter declines and the violation
    /// stands — the caller is told rather than quietly having a box deleted.
    /// </summary>
    [Fact]
    public void BothBoxes_AreLeftAloneByDefault_AndStillBlock()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(BothPageBoxesPdf()), output,
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false });

        Assert.Equal(0, result.PagesArtBoxRemoved);
        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("X05"));

        // ...and the caller is told *why* it declined, which the X05 message cannot say.
        Assert.Contains(result.PageBoxesLeftAlone,
            s => s.Contains("page 1") && s.Contains(nameof(PdfXConversionOptions.RemoveArtBoxWhenBothPresent)));

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.Contains(check.Violations, x => x.Code == "X05");
    }

    /// <summary>Opted in, the <c>/ArtBox</c> goes and the <c>/TrimBox</c> — the finished size — stays.</summary>
    [Fact]
    public void BothBoxes_ArtBoxRemovedWhenOptedIn()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(BothPageBoxesPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                OutputConditionIdentifier = "FOGRA39",
                Trapped = false,
                RemoveArtBoxWhenBothPresent = true,
            });

        Assert.Equal(1, result.PagesArtBoxRemoved);
        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));

        // The surviving box is the /TrimBox, read back out of the produced document.
        Assert.Equal(new[] { 5d, 5d, 195d, 195d }, WrittenTrimBox(output.ToArray()));

        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, x => x.Code == "X05");
    }

    /// <summary>
    /// Turning the repair off leaves the violation standing for a human, which is the point of
    /// having the switch: the assumption a synthesised <c>/TrimBox</c> makes is not always right.
    /// </summary>
    [Fact]
    public void RepairDisabled_LeavesX05Standing()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NoPageBoxesPdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                RepairPageBoxes = false,
            });

        Assert.Equal(0, result.PagesGivenTrimBox);
        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("X05"));
    }

    /// <summary>
    /// A page that already conforms is not rewritten — the counts stay zero rather than reporting
    /// work on a document that needed none.
    /// </summary>
    [Fact]
    public void AlreadyConformingPage_IsNotTouched()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(ConvertiblePdf()), output,
            new PdfXConversionOptions
            {
                IccProfile = FakeCmykProfile(),
                Trapped = false,
                RemoveArtBoxWhenBothPresent = true,
            });

        Assert.Equal(0, result.PagesGivenTrimBox);
        Assert.Equal(0, result.PagesArtBoxRemoved);
        Assert.Equal(new[] { 0d, 0d, 200d, 200d }, WrittenTrimBox(output.ToArray()));
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// The page's <c>/TrimBox</c> as the produced document actually carries it — read back through
    /// the parser rather than matched in the bytes, so an incremental update that superseded the
    /// page is resolved the way a reader would resolve it.
    /// </summary>
    private static double[] WrittenTrimBox(byte[] pdf)
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var box = doc.Pages[0].Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.TrimBox]
                  as Tetitulz.Pdf.Core.Objects.PdfArray;
        Assert.NotNull(box);
        return new[] { box.GetDouble(0), box.GetDouble(1), box.GetDouble(2), box.GetDouble(3) };
    }

    /// <summary>Not a real profile — the writer embeds the bytes verbatim.</summary>
    /// <summary>
    /// Setting <c>/Trapped</c> on a document that has NO <c>/Info</c> dictionary must mint one,
    /// not throw.
    /// </summary>
    /// <remarks>
    /// <b>A comment asserting the behaviour, and the behaviour not happening.</b>
    /// <c>OutputIntentWriter</c> says in so many words that /Info is "absent from many documents,
    /// in which case one is created and linked from the trailer below" -- and the branch that does
    /// exactly that could never run, because <c>CloneDictionary(null)</c> threw
    /// <see cref="System.ArgumentNullException"/> eight lines earlier. X10 REQUIRES /Trapped, so
    /// this is the normal PDF/X path, not a corner: any input without an /Info -- which a
    /// generator is under no obligation to write -- failed the conversion outright.
    /// <para>Found by a fixture built for something else entirely. It is asserted here rather than
    /// left incidental, because the fixture that exposed it exists to test XMP escaping and could
    /// legitimately grow an /Info tomorrow.</para>
    /// </remarks>
    [Fact]
    public void TrappedOnADocumentWithNoInfoDictionaryMintsOneInsteadOfThrowing()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(NoInfoDictionaryPdf()), output,
            new PdfXConversionOptions
            {
                Level = PdfXConformanceLevel.X1a,
                IccProfile = FakeCmykProfile(),
                OutputConditionIdentifier = "FOGRA39",
                Trapped = false,
            });

        Assert.True(result.SetTrapped);

        // The minted /Info must be REACHABLE, not merely written: an object nothing references is
        // the same as no object at all, and the trailer link is the half the throw skipped.
        string text = Encoding.Latin1.GetString(output.ToArray());
        Assert.Contains("/Trapped /False", text);
        Assert.Matches(@"/Info\s+\d+\s+0\s+R", text);
    }

    /// <summary>X-1a-convertible in every respect, except the trailer names no <c>/Info</c>.</summary>
    private static byte[] NoInfoDictionaryPdf()
    {
        const string content = "0 0 0 1 k 10 10 100 100 re f";
        var acc = new List<byte>();
        var off = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /TrimBox [0 0 200 200] " +
                "/Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        Emit("%PDF-1.4\n");
        for (int i = 0; i < objs.Length; i++) { off.Add(acc.Count); Emit($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = acc.Count;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in off) sb.Append($"{o:D10} 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        Emit(sb.ToString());
        return acc.ToArray();
    }

    // ------------------------------------------------------------------
    //  Colour (X08), converted when asked
    // ------------------------------------------------------------------

    /// <summary>
    /// RGB and Lab colour X-1a forbids, converted into the output intent's CMYK press: the converted file is
    /// PDF/X-1a by the shipped validator. Without <c>ConvertColour</c> the same input reports X08 — the control.
    /// </summary>
    [Fact]
    public void RgbAndLab_ConvertedWhenAsked_ReachX1a()
    {
        byte[] press = Tetitulz.Pdf.TestHelpers.SyntheticCmykProfile.BlackFromLightness();
        var control = PdfXConverter.Convert(new MemoryStream(RgbAndLabPdf()), new MemoryStream(),
            new PdfXConversionOptions { IccProfile = press, Trapped = false });
        Assert.Contains(control.Blockers, b => b.StartsWith("X08"));
        Assert.Null(control.ColourConversion);

        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(RgbAndLabPdf()), output,
            new PdfXConversionOptions { IccProfile = press, Trapped = false, ConvertColour = true });

        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
        Assert.Equal(2, result.ColourConversion.ColoursConverted);
        Assert.Equal(1, result.ColourConversion.ColourSpacesConverted);
        var check = PdfXValidator.Validate(new MemoryStream(output.ToArray()), PdfXConformanceLevel.X1a);
        Assert.True(check.IsConforming, string.Join(" | ", check.Violations.Select(x => x.Code + ": " + x.Message)));
    }

    /// <summary>At X-3 under a CMYK intent DeviceRGB is converted and Lab, which X-3 admits, is left as it is.</summary>
    [Fact]
    public void AtX3_OnlyDeviceRgbIsConverted()
    {
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(RgbAndLabPdf()), output,
            new PdfXConversionOptions { Level = PdfXConformanceLevel.X3, IccProfile = Tetitulz.Pdf.TestHelpers.SyntheticCmykProfile.BlackFromLightness(),
                                        Trapped = false, ConvertColour = true });
        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
        Assert.Equal(1, result.ColourConversion.ColoursConverted);   // the rg; the Lab sc stays
        Assert.Equal(0, result.ColourConversion.ColourSpacesConverted);
    }

    /// <summary>A profile with no <c>B2A</c> table cannot be converted into: said, and X08 stands.</summary>
    [Fact]
    public void AProfileThatCannotBeConvertedInto_LeavesX08()
    {
        var result = PdfXConverter.Convert(new MemoryStream(RgbAndLabPdf()), new MemoryStream(),
            new PdfXConversionOptions { IccProfile = FakeCmykProfile(), Trapped = false, ConvertColour = true });
        Assert.Contains(result.Warnings, w => w.StartsWith("The colour was not converted", StringComparison.Ordinal));
        Assert.Contains(result.Blockers, b => b.StartsWith("X08"));
        Assert.False(result.DeclaredPdfXIdentifier);

        var none = PdfXConverter.Convert(new MemoryStream(RgbAndLabPdf()), new MemoryStream(),
            new PdfXConversionOptions { Trapped = false, ConvertColour = true });
        Assert.Contains(none.Blockers, b => b.StartsWith("ConvertColour needs IccProfile", StringComparison.Ordinal));
    }

    /// <summary>A DeviceRGB fill and a Lab one, otherwise X-1a-ready.</summary>
    private static byte[] RgbAndLabPdf()
        => Build("1 0 0 rg 10 10 50 50 re f /L0 cs 50 20 -10 sc 70 10 50 50 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects: null, fontResource: "/ColorSpace << /L0 [/Lab << /WhitePoint [0.9642 1 0.8249] >>] >>");

    private static byte[] FakeCmykProfile() => IccProfileFixtures.Cmyk();

    /// <summary>
    /// A page that can reach X-1a: a <c>/TrimBox</c>, no fonts, no transparency, and CMYK-only
    /// painting. Everything still missing is something the converter supplies.
    /// </summary>
    private static byte[] ConvertiblePdf(string pdfVersion = "1.4")
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects: null, fontResource: "", pdfVersion: pdfVersion);

    /// <summary>X-1a in every respect except that the page declares no <c>/TrimBox</c> or <c>/ArtBox</c>.</summary>
    private static byte[] NoPageBoxesPdf(string cropBox = "")
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: cropBox,
                 extraObjects: null, fontResource: "");

    /// <summary>Both boxes — the other half of X05, whose remediation is deletion, not synthesis.</summary>
    private static byte[] BothPageBoxesPdf()
        => Build("0 0 0 1 k 10 10 100 100 re f",
                 pageExtra: "/TrimBox [5 5 195 195] /ArtBox [20 20 180 180]",
                 extraObjects: null, fontResource: "");

    private static byte[] NonEmbeddedFontPdf()
        => Build("BT /F1 12 Tf 20 100 Td (Hi) Tj ET", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects:
                     "6 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /Unembedded " +
                     "/FontDescriptor 7 0 R >>\nendobj\n" +
                     "7 0 obj\n<< /Type /FontDescriptor /FontName /Unembedded /Flags 32 >>\nendobj\n",
                 fontResource: "/Font << /F1 6 0 R >>");

    private static byte[] OpenActionJavaScriptPdf()
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects: "6 0 obj\n<< /S /JavaScript /JS (app.alert\\('x'\\)) >>\nendobj\n",
                 fontResource: "", catalogExtra: "/OpenAction 6 0 R");

    private static byte[] EmbeddedFilePdf()
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects:
                     "6 0 obj\n<< /Type /Filespec /F (a.txt) /EF << /F 7 0 R >> >>\nendobj\n" +
                     "7 0 obj\n<< /Type /EmbeddedFile /Length 5 >>\nstream\nhello\nendstream\nendobj\n",
                 fontResource: "", catalogExtra: "/Names << /EmbeddedFiles << /Names [(a.txt) 6 0 R] >> >>");

    // ================================================================
    //  Input the converter must refuse rather than mangle
    // ================================================================
    //
    // Both cases used to fall through to PdfSanitizer, one step down and in a different assembly:
    // encrypted input escaped as a PdfEncryptedDocumentException thrown out of Convert, and a
    // SIGNED document was renumbered like any other, its signature silently destroyed with nothing
    // on the result saying so.

    [Fact]
    public void Convert_EncryptedInput_ReportsABlockerRatherThanThrowing()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var result = PdfXConverter.Convert(input, output, new PdfXConversionOptions());

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.Contains("encrypted"));
        Assert.False(result.DeclaredPdfXIdentifier);
    }

    [Fact]
    public void Convert_SignedInput_IsRefusedUnlessTheCallerOptsIn()
    {
        using var input = new MemoryStream(SignedPdf());
        using var output = new MemoryStream();

        var result = PdfXConverter.Convert(input, output, new PdfXConversionOptions());

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.Contains("signature"));
        Assert.Equal(0, output.Length);   // the caller's stream is untouched by a refusal
    }

    [Fact]
    public void Convert_SignedInput_WithOptIn_ConvertsAndWarnsThatTheSignatureIsGone()
    {
        using var input = new MemoryStream(SignedPdf());
        using var output = new MemoryStream();

        var result = PdfXConverter.Convert(input, output,
            new PdfXConversionOptions { AllowSignatureInvalidation = true });

        Assert.DoesNotContain(result.Blockers, b => b.Contains("signature"));
        Assert.Contains(result.Warnings, w => w.Contains("invalidated"));
        Assert.True(output.Length > 0);
    }

    /// <summary>
    /// The control for all three: an ordinary unsigned, unencrypted document must still convert.
    /// Without it, "refuses signed input" would be indistinguishable from "refuses everything".
    /// </summary>
    [Fact]
    public void Convert_OrdinaryInput_IsNotRefused()
    {
        using var input = new MemoryStream(ConvertiblePdf());
        using var output = new MemoryStream();

        var result = PdfXConverter.Convert(input, output, new PdfXConversionOptions());

        Assert.DoesNotContain(result.Blockers, b => b.Contains("encrypted") || b.Contains("signature"));
        Assert.True(output.Length > 0);
    }

    /// <summary>
    /// A trailer <c>/Encrypt</c> is all <c>PdfDocument.Load</c> needs to set <c>IsEncrypted</c> and
    /// stop before the catalog, which is the state under test. No real cryptography is involved —
    /// producing a genuinely encrypted file would need <c>Tetitulz.Pdf.Encryption</c>, which this
    /// plugin does not and must not reference.
    /// </summary>
    private static byte[] EncryptedPdf()
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects:
                     "6 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
                     "/O <00> /U <00> >>\nendobj\n",
                 fontResource: "", trailerExtra: "/Encrypt 6 0 R /ID [<01> <01>]");

    /// <summary>
    /// A document carrying a signature <i>value</i>. Detection is structural — an
    /// <c>/AcroForm</c> field of <c>/FT /Sig</c> with a <c>/V</c> — because the question is
    /// "is there something conversion would destroy?", for which a present value is the answer
    /// whether or not it verifies.
    /// </summary>
    private static byte[] SignedPdf()
        => Build("0 0 0 1 k 10 10 100 100 re f", pageExtra: "/TrimBox [0 0 200 200]",
                 extraObjects:
                     "6 0 obj\n<< /Fields [7 0 R] >>\nendobj\n" +
                     "7 0 obj\n<< /FT /Sig /T (Signature1) /V 8 0 R >>\nendobj\n" +
                     "8 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
                     "/ByteRange [0 0 0 0] /Contents <00> >>\nendobj\n",
                 fontResource: "", catalogExtra: "/AcroForm 6 0 R");

    // ================================================================
    //  What PDF/X may forbid where the rule was not available: left out
    // ================================================================

    private const string Screen = "/HalftoneType 1 /Frequency 60 /Angle 45 /SpotFunction /Round";

    private static (PdfXConversionResult Result, byte[] Output) ConvertX4(byte[] input, PdfXConversionOptions? options = null)
    {
        options ??= new PdfXConversionOptions();
        options.Level = PdfXConformanceLevel.X4;
        options.IccProfile ??= FakeCmykProfile();
        options.Trapped ??= false;
        var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(input), output, options);
        return (result, output.ToArray());
    }

    /// <summary>A graphics state read back from the converted file: the page's /ExtGState entry <paramref name="name"/>.</summary>
    private static Tetitulz.Pdf.Core.Objects.PdfDictionary PageState(byte[] pdf, string name)
    {
        var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var states = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.ExtGState]);
        return (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(states[new Tetitulz.Pdf.Core.Objects.PdfName(name)]);
    }

    public static IEnumerable<object[]> HalftonePlaces() => new[]
    {
        // A state written in the map, in the page's resources.
        new object[] { "/ExtGState << /G0 << /LW 2 /HT << " + Screen + " /TransferFunction /Identity >> /HTP [1 1] >> >>", null! },
        // A state of its own number.
        new object[] { "/ExtGState << /G0 6 0 R >>", "6 0 obj\n<< /LW 2 /HT << " + Screen + " >> /HTP [1 1] >>\nendobj\n" },
        // A map of its own number holding a state written in it.
        new object[] { "/ExtGState 6 0 R", "6 0 obj\n<< /G0 << /LW 2 /HT << " + Screen + " >> /HTP [1 1] >> >>\nendobj\n" },
        // A map of its own number naming a state of its own number.
        new object[] { "/ExtGState 6 0 R", "6 0 obj\n<< /G0 7 0 R >>\nendobj\n7 0 obj\n<< /LW 2 /HT << " + Screen + " >> /HTP [1 1] >>\nendobj\n" },
    };

    [Theory]
    [MemberData(nameof(HalftonePlaces))]
    public void X4_AGraphicsStatesHalftoneAndPhase_AreLeftOut_WhereverTheStateIsWritten(string resources, string? extra)
    {
        byte[] input = Build("/G0 gs 0 0 0 1 k 10 10 100 100 re f", "/TrimBox [0 0 200 200]", extra!, resources, pdfVersion: "1.6");
        Assert.NotNull(PageState(input, "G0")[Tetitulz.Pdf.Core.Objects.PdfName.HT]);   // potency: the input holds a halftone

        var (result, output) = ConvertX4(input);
        var state = PageState(output, "G0");
        Assert.Null(state[Tetitulz.Pdf.Core.Objects.PdfName.HT]);
        Assert.Null(state[Tetitulz.Pdf.Core.Objects.PdfName.HTP]);
        Assert.NotNull(state[Tetitulz.Pdf.Core.Objects.PdfName.LW]);   // the rest of the state is kept
        Assert.Equal(1, result.HalftonesRemoved);
        Assert.Contains(result.Warnings, w => w.Contains("(RemoveHalftones)", System.StringComparison.Ordinal));
        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
        Assert.DoesNotContain(PdfXValidator.Validate(new MemoryStream(output), PdfXConformanceLevel.X4).Informational, i => i.Code == "X26");
    }

    /// <summary>A shading pattern's /ExtGState is one state, not a map of them (ISO 32000-1 Table 76) —
    /// written in the pattern, or an object of its own.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void X4_AShadingPatternsState_LosesItsHalftone(bool stateOfItsOwnNumber)
    {
        const string patternState = "<< /LW 2 /HTO [1 1] /HT << " + Screen + " >> >>";
        string resources = "/Pattern << /P0 6 0 R >>";
        string extra = "6 0 obj\n<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 1 0] " +
                       "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 1] /N 1 >> >> " +
                       "/ExtGState " + (stateOfItsOwnNumber ? "7 0 R" : patternState) + " >>\nendobj\n" +
                       (stateOfItsOwnNumber ? "7 0 obj\n" + patternState + "\nendobj\n" : "");
        var (result, output) = ConvertX4(Build("0 0 0 1 k 10 10 100 100 re f", "/TrimBox [0 0 200 200]", extra, resources, pdfVersion: "1.6"));

        var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(output));
        var patterns = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.Pattern]);
        var pattern = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(patterns[new Tetitulz.Pdf.Core.Objects.PdfName("P0")]);
        var state = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(pattern[Tetitulz.Pdf.Core.Objects.PdfName.ExtGState]);
        Assert.Null(state[Tetitulz.Pdf.Core.Objects.PdfName.HT]);
        Assert.Null(state[Tetitulz.Pdf.Core.Objects.PdfName.HTO]);
        Assert.NotNull(state[Tetitulz.Pdf.Core.Objects.PdfName.LW]);
        Assert.NotNull(pattern[Tetitulz.Pdf.Core.Objects.PdfName.Shading]);
        Assert.Equal(1, result.HalftonesRemoved);
    }

    [Fact]
    public void X4_RemoveHalftonesOff_KeepsThem()
    {
        byte[] input = Build("/G0 gs", "/TrimBox [0 0 200 200]", null!, "/ExtGState << /G0 << /HT << " + Screen + " >> >> >>", pdfVersion: "1.6");
        var (result, output) = ConvertX4(input, new PdfXConversionOptions { RemoveHalftones = false });
        Assert.NotNull(PageState(output, "G0")[Tetitulz.Pdf.Core.Objects.PdfName.HT]);
        Assert.Equal(0, result.HalftonesRemoved);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("RemoveHalftones", System.StringComparison.Ordinal));
    }

    /// <summary>A document with nothing to leave out is not told it had something.</summary>
    [Fact]
    public void X4_NothingToLeaveOut_ReportsNothing()
    {
        var (result, _) = ConvertX4(ConvertiblePdf("1.6"));
        Assert.Equal(0, result.HalftonesRemoved);
        Assert.Equal(0, result.OptionalContentConfigurationsRepaired);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("RemoveHalftones", System.StringComparison.Ordinal)
                                                    || w.Contains("RepairOptionalContentConfigurations", System.StringComparison.Ordinal));
    }

    /// <summary>Nothing to leave out is a copy of the input, not an empty revision appended to it.</summary>
    [Fact]
    public void PrintStateRepair_NothingToChange_IsByteIdentical()
    {
        byte[] input = ConvertiblePdf("1.6");
        var output = new MemoryStream();
        var repaired = PrintStateRepair.Repair(new MemoryStream(input), output, true, true);
        Assert.Equal(input, output.ToArray());
        Assert.Equal(0, repaired.StatesChanged);

        // Potency: the same file with a halftone is changed.
        byte[] withHalftone = Build("/G0 gs", "/TrimBox [0 0 200 200]", null!, "/ExtGState << /G0 << /HT << " + Screen + " >> >> >>", pdfVersion: "1.6");
        var changed = new MemoryStream();
        Assert.Equal(1, PrintStateRepair.Repair(new MemoryStream(withHalftone), changed, true, true).StatesChanged);
        Assert.NotEqual(withHalftone, changed.ToArray());
    }

    /// <summary>An object the parser cannot read is counted, never read as one with nothing to change.</summary>
    [Fact]
    public void PrintStateRepair_AnUnreadableObject_IsCounted()
    {
        byte[] input = Build("0 0 0 1 k", "/TrimBox [0 0 200 200]", "6 0 obj\n<< /ExtGState ] >>\nendobj\n", "", pdfVersion: "1.6");
        var repaired = PrintStateRepair.Repair(new MemoryStream(input), new MemoryStream(), true, true);
        Assert.True(repaired.Unreadable > 0);
        Assert.Equal(0, PrintStateRepair.Repair(new MemoryStream(ConvertiblePdf("1.6")), new MemoryStream(), true, true).Unreadable);
    }

    /// <summary>
    /// <b>Adversarial: twenty thousand unnamed configurations.</b> Each is named past the ones before it,
    /// and the search for a free name used to restart at the bare stem for every one — two hundred
    /// million tries here, six minutes at a hundred thousand. It resumes: the names come out as they did,
    /// "Configuration" then "Configuration 2" on, one past a name the file already holds, and the tries
    /// are about one a configuration — the evidence, since the names alone are the same either way.
    /// </summary>
    [Fact]
    public void PrintStateRepair_AdversarialManyUnnamedConfigurations_AreNamedInOnePass()
    {
        const int Count = 20_000;
        var configs = new System.Text.StringBuilder("[<< /Name (Configuration 3) >>");
        for (int i = 0; i < Count; i++) configs.Append(" <<>>");
        configs.Append(']');
        byte[] input = Build("0 0 0 1 k 10 10 100 100 re f", "/TrimBox [0 0 200 200]", "6 0 obj\n<< /Type /OCG /Name (L) >>\nendobj\n", "",
            catalogExtra: "/OCProperties << /OCGs [6 0 R] /D << >> /Configs " + configs + " >>", pdfVersion: "1.6");

        var output = new MemoryStream();
        var repaired = PrintStateRepair.Repair(new MemoryStream(input), output, false, true);

        Assert.Equal(Count + 1, repaired.ConfigurationsNamed);   // /D and every unnamed one
        Assert.InRange(repaired.NameCandidatesTried, Count + 1, Count + 3);   // one each, and the one name the file held
        var names = Configurations(output.ToArray()).Select(c => c.Name).ToList();
        Assert.Equal("Default", names[0]);
        Assert.Equal("Configuration 3", names[1]);
        Assert.Equal("Configuration", names[2]);
        Assert.Equal("Configuration 2", names[3]);
        Assert.Equal("Configuration 4", names[4]);
        Assert.Equal("Configuration " + (Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), names[^1]);
        Assert.Equal(names.Count, names.Distinct(System.StringComparer.Ordinal).Count());
    }

    private static List<(string? Name, bool HasAs)> Configurations(byte[] pdf)
    {
        var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var ocp = OptionalContentConfigurations.Properties(doc);
        return OptionalContentConfigurations.Read(doc, ocp)
            .Select(c => (c.Name, c.Dictionary[Tetitulz.Pdf.Core.Objects.PdfName.AS] != null)).ToList();
    }

    private const string Groups = "6 0 obj\n<< /Type /OCG /Name (One) >>\nendobj\n";

    public static IEnumerable<object[]> ConfigurationLayouts() => new[]
    {
        // All written in the catalog.
        new object[] { "/OCProperties << /OCGs [6 0 R] /D << /AS [<< /Event /Print /OCGs [6 0 R] /Category [/Print] >>] >> " +
                       "/Configs [<< /Name (Default) >> << /Name (Default) /AS [] >>] >>", Groups },
        // /OCProperties in the catalog, /D an object of its own, /Configs in it naming one of its own —
        // the one that needs the change.
        new object[] { "/OCProperties << /OCGs [6 0 R] /D 7 0 R /Configs [<< /Name (Default) >> 8 0 R] >>",
                       Groups + "7 0 obj\n<< /AS [<< /Event /Print /OCGs [6 0 R] /Category [/Print] >>] >>\nendobj\n" +
                       "8 0 obj\n<< /Name (Default) /AS [] >>\nendobj\n" },
        // Each of its own number.
        new object[] { "/OCProperties 7 0 R",
                       Groups + "7 0 obj\n<< /OCGs [6 0 R] /D 8 0 R /Configs 9 0 R >>\nendobj\n" +
                       "8 0 obj\n<< /Name () /AS [<< /Event /Print /OCGs [6 0 R] /Category [/Print] >>] >>\nendobj\n" +
                       "9 0 obj\n[<< /Name (Default) >> << /Name (Default) /AS [] >>]\nendobj\n" },
    };

    [Theory]
    [MemberData(nameof(ConfigurationLayouts))]
    public void X4_EveryConfiguration_IsNamedUniquely_AndLosesItsAs(string catalogExtra, string extra)
    {
        byte[] input = Build("0 0 0 1 k 10 10 100 100 re f", "/TrimBox [0 0 200 200]", extra, "", catalogExtra: catalogExtra, pdfVersion: "1.6");
        Assert.Contains(PdfXValidator.Validate(new MemoryStream(input), PdfXConformanceLevel.X4).Informational, i => i.Code == "X27");

        var (result, output) = ConvertX4(input);
        var configs = Configurations(output);
        Assert.Equal(new[] { "Default 2", "Default", "Configuration" }, configs.Select(c => c.Name).ToArray());
        Assert.All(configs, c => Assert.False(c.HasAs));
        Assert.Equal(2, result.OptionalContentConfigurationsRepaired);   // /D and /Configs [1]; /Configs [0] kept its name
        Assert.Contains(result.Warnings, w => w.Contains("(RepairOptionalContentConfigurations)", System.StringComparison.Ordinal));
        Assert.DoesNotContain(PdfXValidator.Validate(new MemoryStream(output), PdfXConformanceLevel.X4).Informational, i => i.Code == "X27");
    }

    [Fact]
    public void X4_RepairOptionalContentConfigurationsOff_KeepsThem()
    {
        byte[] input = Build("0 0 0 1 k 10 10 100 100 re f", "/TrimBox [0 0 200 200]", Groups, "",
            catalogExtra: "/OCProperties << /OCGs [6 0 R] /D << /AS [] >> >>", pdfVersion: "1.6");
        var (result, output) = ConvertX4(input, new PdfXConversionOptions { RepairOptionalContentConfigurations = false });
        var d = Assert.Single(Configurations(output));
        Assert.Null(d.Name);
        Assert.True(d.HasAs);
        Assert.Equal(0, result.OptionalContentConfigurationsRepaired);
    }

    private static byte[] Build(string content, string pageExtra, string extraObjects,
        string fontResource, string catalogExtra = "", string pdfVersion = "1.4",
        string trailerExtra = "")
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit($"%PDF-{pdfVersion}\n");
        BeginObj(1); Emit($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
             $"{pageExtra} /Contents 4 0 R /Resources << {fontResource} >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5); Emit("<< /Producer (test) >>\nendobj\n");

        int highest = 5;
        if (!string.IsNullOrEmpty(extraObjects))
        {
            int start = acc.Count;
            Emit(extraObjects);
            for (int n = 6; n <= 9; n++)
            {
                int at = extraObjects.IndexOf($"{n} 0 obj", System.StringComparison.Ordinal);
                if (at < 0) continue;
                offsets[n] = start + at;
                highest = n;
            }
        }

        int xref = acc.Count;
        Emit($"xref\n0 {highest + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= highest; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {highest + 1} /Root 1 0 R /Info 5 0 R {trailerExtra} >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
