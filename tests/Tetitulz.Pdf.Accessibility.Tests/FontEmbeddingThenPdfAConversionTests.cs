// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Proves the two-step pipeline <c>PdfAConverter</c>'s own remarks describe: embed missing fonts
/// with <see cref="PdfFontEmbeddingRemediator"/> (built for PDF/UA-1 §7.21.4.1's identical question),
/// then hand the result to <see cref="PdfAConverter"/>. Neither component can reference the other in
/// production code without closing a cycle (<c>Accessibility → Plugins.Conformance</c> is the
/// sanctioned edge; the reverse would close it), so the composition is documented rather than built
/// as a new API — this test is what turns that documented claim into a checked one, in the one
/// project that can see both.
/// </summary>
public class FontEmbeddingThenPdfAConversionTests
{
    [Fact]
    public void EmbedMissingFonts_ThenConvertToPdfA1b_ProducesAFullyConformantDocument()
    {
        byte[] source = Standard14HelveticaPdf();

        byte[] embedded;
        using (var inMs = new MemoryStream(source, writable: false))
        using (var outMs = new MemoryStream())
        {
            var embedResult = PdfFontEmbeddingRemediator.EmbedMissingFonts(inMs, outMs, new FontEmbeddingOptions
            {
                FontProvider = _ => MinimalTrueType.Build("Helo"),
            });
            Assert.True(embedResult.Changed,
                "the font-embedding step should have embedded a program: " + string.Join(" | ", embedResult.Warnings));
            embedded = outMs.ToArray();
        }

        var conversionOutput = new MemoryStream();
        var conversionResult = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(embedded), conversionOutput,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.True(conversionResult.IsConformant, string.Join(" | ", conversionResult.Blockers));
        Assert.True(conversionResult.DeclaredPdfAIdentifier);

        // Independent re-check, over the FINAL bytes, the same way a downstream reader would.
        var check = PdfAValidator.Validate(new MemoryStream(conversionOutput.ToArray()));
        Assert.True(check.IsConformant,
            string.Join(" | ", check.Violations.Select(v => v.Code + ": " + v.Message)));
        Assert.DoesNotContain(check.Violations, v => v.Code == "A06");
    }

    /// <summary>
    /// The negative control this whole test exists for: skip the embedding step, and the identical
    /// source is not conformant — proving step 1 is what closed A06, not something else in the
    /// pipeline (the ICC profile, the sanitize pass) that happened to also be present.
    /// </summary>
    [Fact]
    public void WithoutTheEmbeddingStep_ConversionAloneStillReportsA06()
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA1b(
            new MemoryStream(Standard14HelveticaPdf()), output,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.False(result.IsConformant);
        Assert.Contains(result.Blockers, b => b.StartsWith("A06"));
    }

    /// <summary>The same composition reaches PDF/A-3b, proving it is not Part-1-specific.</summary>
    [Fact]
    public void EmbedMissingFonts_ThenConvertToPdfA3b_ProducesAFullyConformantDocument()
    {
        byte[] embedded;
        using (var inMs = new MemoryStream(Standard14HelveticaPdf(), writable: false))
        using (var outMs = new MemoryStream())
        {
            var embedResult = PdfFontEmbeddingRemediator.EmbedMissingFonts(inMs, outMs, new FontEmbeddingOptions
            {
                FontProvider = _ => MinimalTrueType.Build("Helo"),
            });
            Assert.True(embedResult.Changed);
            embedded = outMs.ToArray();
        }

        var conversionOutput = new MemoryStream();
        var conversionResult = PdfAConverter.ConvertToPdfA3b(
            new MemoryStream(embedded), conversionOutput,
            new PdfAConversionOptions { IccProfile = FakeIccProfile() });

        Assert.True(conversionResult.IsConformant, string.Join(" | ", conversionResult.Blockers));

        var check = PdfAValidator.Validate(new MemoryStream(conversionOutput.ToArray()), PdfAConformancePart.Part3);
        Assert.True(check.IsConformant,
            string.Join(" | ", check.Violations.Select(v => v.Code + ": " + v.Message)));
    }

    /// <summary>
    /// ISO 19005-1 6.3.5: a Type 1 subset's descriptor lists its character names. The remediator writes
    /// them — the names the drawn codes select through the encoding it leaves on the font
    /// (StandardEncoding here: H, e, l, o for "Hello").
    /// </summary>
    [Fact]
    public void EmbedMissingFonts_AType1Subset_GetsTheCharSetOfTheDrawnGlyphs()
    {
        var (descriptor, warnings) = EmbedAndReadDescriptor(Standard14HelveticaPdf());
        Assert.Equal("/H/e/l/o", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfString>(descriptor[Tetitulz.Pdf.Core.Objects.PdfName.CharSet]).GetText());
        Assert.DoesNotContain(warnings, w => w.Contains("no /CharSet was written", StringComparison.Ordinal));
    }

    /// <summary>
    /// ISO 32000-1 §9.6.6.1: an encoding dictionary with no <c>/BaseEncoding</c> is based on
    /// StandardEncoding while a non-symbolic font is not embedded, and on the program's own encoding once
    /// it is. The remediator writes the base the font implied — on the encoding object itself when it is
    /// indirect, in the font dictionary when it is direct — so no code changes glyph, and the CharSet then
    /// names every drawn code.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbedMissingFonts_AnEncodingDictionaryWithNoBase_GetsTheBaseItImplied(bool indirect)
    {
        string encoding = "<< /Differences [72 /H] >>";
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding " + (indirect ? "6 0 R" : encoding) + " >>",
        };
        if (indirect) bodies.Add(encoding);
        var (descriptor, warnings, font, doc) = EmbedAndRead(BuildPdf(bodies.ToArray()));
        using (doc)
        {
            var written = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(doc.ResolveObject(font[Tetitulz.Pdf.Core.Objects.PdfName.Encoding]));
            Assert.Equal("StandardEncoding", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfName>(written[Tetitulz.Pdf.Core.Objects.PdfName.BaseEncoding]).Value);
            Assert.NotNull(written[Tetitulz.Pdf.Core.Objects.PdfName.Differences]);
            Assert.Equal(indirect, font[Tetitulz.Pdf.Core.Objects.PdfName.Encoding] is Tetitulz.Pdf.Core.Objects.PdfReference);
            Assert.Equal("/H/e/l/o", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfString>(descriptor[Tetitulz.Pdf.Core.Objects.PdfName.CharSet]).GetText());
            Assert.Contains(warnings, w => w.Contains("/BaseEncoding /StandardEncoding was written", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// A symbolic font's implicit base is its program's whether embedded or not, so no base is written,
    /// and a drawn code its <c>/Differences</c> does not name has no name the dictionary states: no
    /// CharSet is guessed, and the warning says which codes.
    /// </summary>
    [Fact]
    public void EmbedMissingFonts_ASymbolicFontsUnnamedDrawnCodes_WriteNoCharSetAndSaySo()
    {
        byte[] source = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /MySymbols /FirstChar 72 /LastChar 111 /Widths ["
                + string.Join(" ", Enumerable.Repeat("500", 40)) + "] /Encoding << /Differences [72 /H] >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /MySymbols /Flags 4 /FontBBox [0 0 1000 1000] /ItalicAngle 0 "
                + "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>",
        });
        var (descriptor, warnings, font, doc) = EmbedAndRead(source);
        using (doc)
        {
            Assert.Null(descriptor[Tetitulz.Pdf.Core.Objects.PdfName.CharSet]);
            Assert.Null(Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(doc.ResolveObject(font[Tetitulz.Pdf.Core.Objects.PdfName.Encoding]))[Tetitulz.Pdf.Core.Objects.PdfName.BaseEncoding]);
            Assert.Contains(warnings, w => w.Contains("names no glyph for code(s) 101, 108, 111", StringComparison.Ordinal)
                                           && w.Contains("no /CharSet was written", StringComparison.Ordinal));
        }
    }

    /// <summary>An encoding dictionary that states its base keeps it: nothing is written, nothing warned.</summary>
    [Fact]
    public void EmbedMissingFonts_AnEncodingDictionaryWithABase_KeepsIt()
    {
        byte[] source = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /BaseEncoding /WinAnsiEncoding /Differences [72 /H] >> >>",
        });
        var (descriptor, warnings, font, doc) = EmbedAndRead(source);
        using (doc)
        {
            var written = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(doc.ResolveObject(font[Tetitulz.Pdf.Core.Objects.PdfName.Encoding]));
            Assert.Equal("WinAnsiEncoding", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfName>(written[Tetitulz.Pdf.Core.Objects.PdfName.BaseEncoding]).Value);
            Assert.DoesNotContain(warnings, w => w.Contains("/BaseEncoding /StandardEncoding was written", StringComparison.Ordinal));
            Assert.Equal("/H/e/l/o", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfString>(descriptor[Tetitulz.Pdf.Core.Objects.PdfName.CharSet]).GetText());
        }
    }

    /// <summary>A <c>/CharSet</c> is a Type 1 font's entry (Table 122): a TrueType subset gets none.</summary>
    [Fact]
    public void EmbedMissingFonts_ATrueTypeSubset_GetsNoCharSet()
    {
        byte[] source = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET"),
            "<< /Type /Font /Subtype /TrueType /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        });
        var (descriptor, _, font, doc) = EmbedAndRead(source);
        using (doc)
        {
            Assert.Null(descriptor[Tetitulz.Pdf.Core.Objects.PdfName.CharSet]);
            Assert.Equal("TrueType", Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfName>(font[Tetitulz.Pdf.Core.Objects.PdfName.Subtype]).Value);
        }
    }

    private static (Tetitulz.Pdf.Core.Objects.PdfDictionary Descriptor, System.Collections.Generic.IReadOnlyList<string> Warnings) EmbedAndReadDescriptor(byte[] source)
    {
        var (descriptor, warnings, _, doc) = EmbedAndRead(source);
        doc.Dispose();
        return (descriptor, warnings);
    }

    /// <summary>
    /// Embeds, then reads the output: the subset — the one font whose <c>/BaseFont</c> now carries a
    /// subset tag (six capitals and a plus) — its descriptor, the warnings, and the open document.
    /// </summary>
    private static (Tetitulz.Pdf.Core.Objects.PdfDictionary Descriptor, System.Collections.Generic.IReadOnlyList<string> Warnings,
        Tetitulz.Pdf.Core.Objects.PdfDictionary Font, Tetitulz.Pdf.Core.Document.PdfDocument Doc) EmbedAndRead(byte[] source)
    {
        using var inMs = new MemoryStream(source, writable: false);
        using var outMs = new MemoryStream();
        var result = PdfFontEmbeddingRemediator.EmbedMissingFonts(inMs, outMs, new FontEmbeddingOptions
        {
            FontProvider = _ => MinimalTrueType.Build("Helo"),
        });
        Assert.True(result.Changed, string.Join(" | ", result.Warnings));
        var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var font = Assert.Single(doc.Parser.EnumerateAllObjects().Select(kv => kv.Value)
            .OfType<Tetitulz.Pdf.Core.Objects.PdfDictionary>()
            .Where(d => d[Tetitulz.Pdf.Core.Objects.PdfName.FontDescriptor] != null
                        && (doc.ResolveObject(d[Tetitulz.Pdf.Core.Objects.PdfName.BaseFont]) as Tetitulz.Pdf.Core.Objects.PdfName)?.Value is string name
                        && name.Length > 7 && name[6] == '+' && name.Take(6).All(c => c >= 'A' && c <= 'Z')));
        var descriptor = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.ResolveObject(font[Tetitulz.Pdf.Core.Objects.PdfName.FontDescriptor]);
        return (descriptor, result.Warnings.ToList(), font, doc);
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>A header-only RGB display profile: what the output-intent writer's gate and
    /// PDF/A's header checks read. Arbitrary bytes were embedded unchecked before the gate.</summary>
    private static byte[] FakeIccProfile()
    {
        var profile = new byte[132];
        profile[3] = 132;
        profile[8] = 2;
        System.Text.Encoding.ASCII.GetBytes("mntr").CopyTo(profile, 12);
        System.Text.Encoding.ASCII.GetBytes("RGB ").CopyTo(profile, 16);
        System.Text.Encoding.ASCII.GetBytes("XYZ ").CopyTo(profile, 20);
        System.Text.Encoding.ASCII.GetBytes("acsp").CopyTo(profile, 36);
        return profile;
    }

    /// <summary>A Standard-14 Helvetica font with no /Widths and no /FontDescriptor — the exact
    /// shape PDF/UA-1's own font-embedding tests use for "the font A06/UA21/UA25 are all open
    /// against", so this composition test starts from the real worst case rather than an easy one.</summary>
    private static byte[] Standard14HelveticaPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObject("", "BT /F1 12 Tf 72 400 Td (Hello) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    });

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private static byte[] BuildPdf(string[] bodies)
    {
        var sb = new StringBuilder(64 * bodies.Length + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
