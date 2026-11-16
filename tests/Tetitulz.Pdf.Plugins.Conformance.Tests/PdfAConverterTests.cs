// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAConverter"/> — which shipped with <b>no tests at all</b>, exactly as
/// <c>PdfXValidator</c> did before plan 04. Its own header concedes that font embedding,
/// transparency removal and colour conversion are not performed, so the interesting question is
/// not "does it run" but <b>what does the output claim about itself</b>.
/// </summary>
public class PdfAConverterTests
{
    // ------------------------------------------------------------------
    //  What the converter does do
    // ------------------------------------------------------------------

    [Fact]
    public void Convert_RemovesJavaScriptOpenAction()
    {
        byte[] input = PdfWithJavaScriptOpenAction();

        // The source really does carry the thing we expect to be stripped.
        Assert.Contains("JavaScript", Encoding.ASCII.GetString(input));

        byte[] output = Convert(input);

        var result = PdfAValidator.Validate(new MemoryStream(output));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A02" || v.Code == "A05");
    }

    [Fact]
    public void Convert_WritesTheOutputIntent()
    {
        byte[] output = Convert(SimplePdf());

        using var doc = PdfDocument.Open(new MemoryStream(output));
        PdfArray intents = OutputIntents(doc);

        Assert.NotNull(intents);
        var intent = doc.ResolveObject(intents[0]) as PdfDictionary;
        Assert.NotNull(intent);
        Assert.Equal("GTS_PDFA1", (intent[PdfName.S] as PdfName)?.Value);
    }

    [Fact]
    public void Convert_WithIccProfile_EmbedsDestOutputProfile()
    {
        byte[] icc = FakeIccProfile();
        byte[] output = Convert(SimplePdf(), icc);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var intent = doc.ResolveObject(OutputIntents(doc)[0]) as PdfDictionary;

        var profile = doc.ResolveObject(intent[new PdfName("DestOutputProfile")]) as PdfStream;
        Assert.NotNull(profile);
        Assert.Equal(icc, profile.RawData);
    }

    /// <summary>
    /// ISO 19005-1 §6.2.2 requires the output intent to embed an ICC profile. Without one the
    /// converter cannot write it — pinned so the gap is visible rather than assumed.
    /// </summary>
    [Fact]
    public void Convert_WithoutIccProfile_HasNoDestOutputProfile()
    {
        byte[] output = Convert(SimplePdf());

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var intent = doc.ResolveObject(OutputIntents(doc)[0]) as PdfDictionary;

        Assert.Null(intent[new PdfName("DestOutputProfile")]);
    }

    // ------------------------------------------------------------------
    //  What the output claims about itself
    // ------------------------------------------------------------------

    /// <summary>
    /// The converted file declares <c>pdfaid:part 1 / conformance B</c> — and the validator
    /// agrees the declaration is well formed (A07).
    /// </summary>
    [Fact]
    public void Convert_DeclaresPdfA1bInXmp()
    {
        byte[] output = Convert(SimplePdf());

        var result = PdfAValidator.Validate(new MemoryStream(output));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A07");
    }

    /// <summary>
    /// <b>The important one.</b> A document whose fonts are not embedded cannot be PDF/A-1b, and
    /// the converter does not embed fonts — its header says so. So the output declares
    /// conformance it does not have, and the validator run over the converter's own output
    /// reports A06.
    /// </summary>
    [Fact]
    public void Convert_NonEmbeddedFont_StillDeclaresPdfA_ButValidatorReportsA06()
    {
        byte[] output = Convert(PdfWithNonEmbeddedFont());

        var result = PdfAValidator.Validate(new MemoryStream(output));

        Assert.Contains(result.Violations, v => v.Code == "A06");
        Assert.False(result.IsConformant,
            "a document with a non-embedded font is not PDF/A-1b conformant");
    }

    // ------------------------------------------------------------------
    //  Honest reporting (the options overload)
    // ------------------------------------------------------------------

    /// <summary>
    /// A document the converter cannot make conformant does not come out claiming it is. This is
    /// the rule the accessibility side already applies — a document that could not be tagged is
    /// never made to claim it was — and PDF/A was breaking it.
    /// </summary>
    [Fact]
    public void ConvertWithOptions_NonEmbeddedFont_WithholdsThePdfAIdentifier()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(PdfWithNonEmbeddedFont()), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.False(result.IsConformant);
        Assert.False(result.DeclaredPdfAIdentifier);
        Assert.Contains(result.Blockers, b => b.StartsWith("A06"));

        // ...and the output really does not carry the claim.
        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.Contains(check.Violations, v => v.Code == "A07");
    }

    /// <summary>
    /// The converse: nothing blocking, so the identifier is written and the validator agrees.
    /// Without this the test above would pass with the identifier never written at all.
    /// </summary>
    [Fact]
    public void ConvertWithOptions_NothingBlocking_DeclaresAndValidates()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(SimplePdf()), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
        Assert.True(result.DeclaredPdfAIdentifier);
        Assert.True(result.EmbeddedOutputProfile);

        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.True(check.IsConformant,
            string.Join(" | ", System.Linq.Enumerable.Select(check.Violations, v => v.Code + ": " + v.Message)));
    }

    /// <summary>
    /// The converter writes level B, so it judges its input at level B: an input whose own XMP says
    /// level A, and which has no structure tree, is not blocked by A74 — a level-A rule the output
    /// never claims. The validator at the input's declared level does report it (the control), so the
    /// fixture reaches the rule.
    /// </summary>
    [Fact]
    public void ConvertWithOptions_AnInputDeclaringLevelA_IsJudgedAtLevelB()
    {
        const string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\" pdfaid:part=\"1\" pdfaid:conformance=\"A\"/>" +
            "</rdf:RDF></x:xmpmeta>";
        byte[] input = Build("/Metadata 6 0 R",
            $"6 0 obj\n<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream\nendobj\n");
        Assert.Contains(PdfAValidator.Validate(new MemoryStream(input)).Violations, v => v.Code == "A74");

        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(new MemoryStream(input), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A74"));
        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));
    }

    /// <summary>
    /// No ICC profile is a blocker in its own right: ISO 19005-1 §6.2.2 requires the output
    /// intent to embed one, so the intent the converter writes without it is incomplete.
    /// </summary>
    [Fact]
    public void ConvertWithOptions_NoIccProfile_IsABlocker()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(SimplePdf()), output, new PdfAConversionOptions());

        Assert.False(result.IsConformant);
        Assert.False(result.EmbeddedOutputProfile);
        Assert.Contains(result.Blockers, b => b.StartsWith("A08"));
    }

    /// <summary>
    /// The opt-out still works, and the historical five-argument overload keeps its behaviour —
    /// it always declared, and existing callers must not silently change.
    /// </summary>
    [Fact]
    public void ConvertWithOptions_DeclareRegardless_WritesTheIdentifierAnyway()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(PdfWithNonEmbeddedFont()), output,
            new PdfAConversionOptions { DeclareConformanceRegardless = true });

        Assert.False(result.IsConformant);
        Assert.True(result.DeclaredPdfAIdentifier);

        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()));
        Assert.DoesNotContain(check.Violations, v => v.Code == "A07");
    }

    // ------------------------------------------------------------------
    //  A08 — output intent must embed its profile
    // ------------------------------------------------------------------

    /// <summary>
    /// The validator used to have no A08 at all, so the converter could emit an intent with no
    /// profile and the validator would call the result conformant — two components in the same
    /// assembly disagreeing about ISO 19005-1 §6.2.2.
    /// </summary>
    [Fact]
    public void Validate_OutputIntentWithoutProfile_ReportsA08()
    {
        byte[] output = Convert(SimplePdf());   // legacy overload, no ICC profile

        var result = PdfAValidator.Validate(new MemoryStream(output));
        Assert.Contains(result.Violations, v => v.Code == "A08");
    }

    [Fact]
    public void Validate_OutputIntentWithProfile_DoesNotReportA08()
    {
        byte[] output = Convert(SimplePdf(), FakeIccProfile());

        var result = PdfAValidator.Validate(new MemoryStream(output));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A08");
    }

    /// <summary>
    /// A document with no <c>/OutputIntents</c> at all is not reported: whether one is required
    /// depends on the colour spaces actually used, which this validator does not analyse. Pinned
    /// so the narrow scope is deliberate rather than an oversight.
    /// </summary>
    [Fact]
    public void Validate_NoOutputIntentAtAll_DoesNotReportA08()
    {
        var result = PdfAValidator.Validate(new MemoryStream(SimplePdf()));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A08");
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static byte[] Convert(byte[] input, byte[] icc = null)
    {
        var output = new MemoryStream();
        PdfAConverter.ConvertToPdfA1b(new MemoryStream(input), output, "T", "C", icc);
        return output.ToArray();
    }

    private static PdfArray OutputIntents(PdfDocument doc)
        => doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("OutputIntents")]) as PdfArray;

    /// <summary>Not a real profile — the converter embeds the bytes verbatim, so any marker works.</summary>
    private static byte[] FakeIccProfile() => IccProfileFixtures.Rgb();

    private static byte[] SimplePdf() => Build(null, null);

    private static byte[] PdfWithJavaScriptOpenAction() =>
        Build("/OpenAction 6 0 R",
              "6 0 obj\n<< /S /JavaScript /JS (app.alert\\('hi'\\)) >>\nendobj\n");

    private static byte[] PdfWithNonEmbeddedFont() =>
        Build(null,
              "6 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /SomeUnembeddedFont " +
              "/FontDescriptor 7 0 R >>\nendobj\n" +
              "7 0 obj\n<< /Type /FontDescriptor /FontName /SomeUnembeddedFont /Flags 32 >>\nendobj\n",
              fontResource: "/Font << /F1 6 0 R >>");

    private static byte[] Build(string catalogExtra, string extraObjects, string fontResource = null)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        const string content = "BT /F1 12 Tf 20 100 Td (Hi) Tj ET";

        Emit("%PDF-1.4\n");
        BeginObj(1); Emit($"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
             $"/Resources << {fontResource} >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5); Emit("<< /Producer (test) >>\nendobj\n");

        // Extra objects are emitted verbatim; find their headers for the xref.
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
        Emit($"trailer\n<< /Size {highest + 1} /Root 1 0 R /Info 5 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
