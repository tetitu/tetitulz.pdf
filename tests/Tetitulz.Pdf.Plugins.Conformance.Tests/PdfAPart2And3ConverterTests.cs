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
/// <see cref="PdfAConverter.ConvertToPdfA2b(Stream, Stream, PdfAConversionOptions)"/> and
/// <see cref="PdfAConverter.ConvertToPdfA3b(Stream, Stream, PdfAConversionOptions)"/> — added
/// alongside the pre-existing Part 1b converter, since <see cref="PdfAValidator"/> already checked
/// Parts 1–4 while the converter could only ever produce Part 1b.
///
/// <para><b>The one real behavioural difference between the parts is embedded files</b>
/// (ISO 19005-3 lifts the restriction A03 checks for; Parts 1 and 2 keep it) — everything else
/// (XMP `pdfaid:part`, the shared blocker/warning machinery, the output intent) is the identical
/// pipeline with a different part number threaded through.</para>
/// </summary>
public class PdfAPart2And3ConverterTests
{
    // ------------------------------------------------------------------
    //  pdfaid:part is correct per method, and the validator agrees
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToPdfA2b_DeclaresPart2InXmp_AndValidatesCleanForPart2()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA2b(
            new MemoryStream(SimplePdf()), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));

        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part2);
        Assert.True(check.IsConformant,
            string.Join(" | ", System.Linq.Enumerable.Select(check.Violations, v => v.Code + ": " + v.Message)));
        Assert.DoesNotContain(check.Violations, v => v.Code == "A07");

        // The negative control this whole test is for: validating the SAME bytes against Part 1
        // must disagree, or "declares part 2" was never actually checked.
        var checkAgainstPart1 = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part1);
        Assert.Contains(checkAgainstPart1.Violations, v => v.Code == "A07");
    }

    [Fact]
    public void ConvertToPdfA3b_DeclaresPart3InXmp_AndValidatesCleanForPart3()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA3b(
            new MemoryStream(SimplePdf()), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.True(result.IsConformant, string.Join(" | ", result.Blockers));

        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part3);
        Assert.True(check.IsConformant,
            string.Join(" | ", System.Linq.Enumerable.Select(check.Violations, v => v.Code + ": " + v.Message)));

        var checkAgainstPart2 = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part2);
        Assert.Contains(checkAgainstPart2.Violations, v => v.Code == "A07");
    }

    // ------------------------------------------------------------------
    //  Embedded files: the one real behavioural difference
    // ------------------------------------------------------------------

    /// <summary>
    /// ISO 19005-1/2 forbid embedded files (A03); Parts 1 and 2 must therefore strip the
    /// <c>/Names /EmbeddedFiles</c> entry the same way the historical Part 1b converter always did.
    /// </summary>
    [Fact]
    public void ConvertToPdfA2b_StripsEmbeddedFiles()
    {
        byte[] input = PdfWithEmbeddedFile();
        Assert.Contains("EmbeddedFiles", Encoding.ASCII.GetString(input)); // the source really has one

        var output = new MemoryStream();
        PdfAConverter.ConvertToPdfA2b(
            new MemoryStream(input), output, new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Null(EmbeddedFilesEntry(doc));

        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part2);
        Assert.DoesNotContain(check.Violations, v => v.Code == "A03");
    }

    /// <summary>
    /// ISO 19005-3 explicitly permits any embedded file — the standard's whole point, letting a
    /// "PDF/A package" carry its own machine-readable source data. The converter must therefore
    /// keep it, unlike Parts 1 and 2.
    /// </summary>
    [Fact]
    public void ConvertToPdfA3b_KeepsEmbeddedFiles()
    {
        byte[] input = PdfWithEmbeddedFile();

        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA3b(
            new MemoryStream(input), output, new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.NotNull(EmbeddedFilesEntry(doc));

        // And PdfAValidator, which already skips A03 for Part 3, must not now block on it either —
        // proving the converter and the validator agree about Part 3's own rule.
        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A03"));
        var check = PdfAValidator.Validate(new MemoryStream(output.ToArray()), PdfAConformancePart.Part3);
        Assert.DoesNotContain(check.Violations, v => v.Code == "A03");
        Assert.True(check.IsConformant,
            string.Join(" | ", System.Linq.Enumerable.Select(check.Violations, v => v.Code + ": " + v.Message)));
    }

    // ------------------------------------------------------------------
    //  A06 (font embedding) still applies to both new parts
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(PdfAConformancePart.Part2)]
    [InlineData(PdfAConformancePart.Part3)]
    public void NonEmbeddedFont_IsStillABlocker_ForPart2AndPart3(PdfAConformancePart part)
    {
        var output = new MemoryStream();
        var result = part == PdfAConformancePart.Part2
            ? PdfAConverter.ConvertToPdfA2b(
                new MemoryStream(PdfWithNonEmbeddedFont()), output,
                new PdfAConversionOptions { IccProfile = FakeIccProfile() })
            : PdfAConverter.ConvertToPdfA3b(
                new MemoryStream(PdfWithNonEmbeddedFont()), output,
                new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("A06"));
    }

    // ------------------------------------------------------------------
    //  Throwing overloads
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToPdfA2b_ThrowingOverload_WritesTheOutputIntent()
    {
        var output = new MemoryStream();
        PdfAConverter.ConvertToPdfA2b(new MemoryStream(SimplePdf()), output, "T", "C", FakeIccProfile());

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var intents = doc.ResolveObject(doc.Catalog.Dictionary[new PdfName("OutputIntents")]) as PdfArray;
        Assert.NotNull(intents);
        var intent = doc.ResolveObject(intents[0]) as PdfDictionary;
        Assert.Equal("GTS_PDFA1", (intent[PdfName.S] as PdfName)?.Value);
    }

    /// <summary>
    /// Mirrors the refusal contract <c>ConvertToPdfA1b</c>'s throwing overload already has —
    /// proven here for Part 3's own entry point (<c>ConvertThrowing</c> is shared by all three
    /// parts, but a shared private helper is exactly the kind of thing a per-part test should
    /// still exercise directly rather than assume carries over untested).
    /// </summary>
    [Fact]
    public void ConvertToPdfA3b_ThrowingOverload_ThrowsOnUnreadableInput()
    {
        var ex = Record.Exception(() =>
            PdfAConverter.ConvertToPdfA3b(new MemoryStream(new byte[] { 1, 2, 3 }), new MemoryStream()));

        var ioe = Assert.IsType<Tetitulz.Pdf.Core.PdfException>(ex);
        Assert.Contains("ConvertToPdfA3b", ioe.Message, System.StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Fixtures — shared shape with PdfAConverterTests, kept local per CLAUDE.md's
    //  "self-contained" testing rule (each test file builds its own fixtures).
    // ------------------------------------------------------------------

    private static byte[] FakeIccProfile() => IccProfileFixtures.Rgb();

    private static byte[] SimplePdf() => Build(null, null);

    private static byte[] PdfWithNonEmbeddedFont() =>
        Build(null,
              "6 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /SomeUnembeddedFont " +
              "/FontDescriptor 7 0 R >>\nendobj\n" +
              "7 0 obj\n<< /Type /FontDescriptor /FontName /SomeUnembeddedFont /Flags 32 >>\nendobj\n",
              fontResource: "/Font << /F1 6 0 R >>");

    /// <summary>
    /// A catalog carrying <c>/Names /EmbeddedFiles</c> naming one file specification, the exact
    /// shape <see cref="PdfAValidator"/>'s A03 check and <c>PdfSanitizer</c>'s
    /// <c>RemoveEmbeddedFiles</c> both read.
    /// </summary>
    private static byte[] PdfWithEmbeddedFile() =>
        Build("/Names 6 0 R",
              "6 0 obj\n<< /EmbeddedFiles 7 0 R >>\nendobj\n" +
              "7 0 obj\n<< /Names [(data.csv) 8 0 R] >>\nendobj\n" +
              "8 0 obj\n<< /Type /Filespec /F (data.csv) /EF << /F 9 0 R >> >>\nendobj\n" +
              "9 0 obj\n<< /Type /EmbeddedFile /Length 4 >>\nstream\ndata\nendstream\nendobj\n");

    private static PdfObject EmbeddedFilesEntry(PdfDocument doc)
    {
        var names = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names]) as PdfDictionary;
        return names?[PdfName.EmbeddedFiles];
    }

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
