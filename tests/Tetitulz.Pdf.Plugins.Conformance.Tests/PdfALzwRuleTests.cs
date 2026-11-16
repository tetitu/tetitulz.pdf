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
/// A30: PDF/A forbids LZW compression at every part, in a stream and in an inline image, and
/// cites each part's own clause. The validator had no filter check at all, so an LZW stream
/// validated clean. The reading is shared with PDF/X's X15 (<c>LzwUse</c>).
/// </summary>
public class PdfALzwRuleTests
{
    [Theory]
    [InlineData(PdfAConformancePart.Part1, "ISO 19005-1 §6.1.10, rule 6.1.10-1")]
    [InlineData(PdfAConformancePart.Part2, "ISO 19005-2 §6.1.7.2, rule 6.1.7.2-1")]
    [InlineData(PdfAConformancePart.Part3, "ISO 19005-3 §6.1.7.2, rule 6.1.7.2-1")]
    [InlineData(PdfAConformancePart.Part4, "ISO 19005-4 §6.1.6.2, rule 6.1.6.2-1")]
    public void AnLzwStream_ReportsA30WithItsPartsClause(PdfAConformancePart part, string clause)
    {
        var a30 = Assert.Single(Validate(Pdf(extraStream: "/Filter /LZWDecode"), part).Violations, x => x.Code == "A30");
        Assert.Contains("stream object 5", a30.Message);
        Assert.Contains(clause, a30.Message);
    }

    [Theory]
    [InlineData(PdfAConformancePart.Part1, "ISO 19005-1 §6.1.10, rule 6.1.10-2")]
    [InlineData(PdfAConformancePart.Part2, "ISO 19005-2 §6.1.10, rule 6.1.10-1")]
    [InlineData(PdfAConformancePart.Part3, "ISO 19005-3 §6.1.10, rule 6.1.10-1")]
    [InlineData(PdfAConformancePart.Part4, "ISO 19005-4 §6.1.9, rule 6.1.9-1")]
    public void AnLzwInlineImage_ReportsA30WithItsPartsClause(PdfAConformancePart part, string clause)
    {
        var a30 = Assert.Single(Validate(Pdf(content: "BI /W 1 /H 1 /CS /G /BPC 8 /F /LZW ID 00> EI"), part).Violations,
                                x => x.Code == "A30");
        Assert.Contains("an inline image on page 1", a30.Message);
        Assert.Contains(clause, a30.Message);
    }

    /// <summary>The filter chain's later entries, and the full key in an inline image.</summary>
    [Theory]
    [InlineData("/Filter [/ASCIIHexDecode /LZWDecode]", "q Q")]
    [InlineData("", "BI /W 1 /H 1 /CS /G /BPC 8 /Filter /LZWDecode ID 00> EI")]
    public void OtherSpellings_ReportA30(string extraStream, string content)
        => Assert.Contains(Validate(Pdf(content, extraStream), PdfAConformancePart.Part2).Violations, x => x.Code == "A30");

    /// <summary>The control: Flate in both places reports nothing.</summary>
    [Fact]
    public void Flate_DoesNotReportA30()
        => Assert.DoesNotContain(
            Validate(Pdf("BI /W 1 /H 1 /CS /G /BPC 8 /F /Fl ID x EI", "/Filter /FlateDecode"), PdfAConformancePart.Part2).Violations,
            x => x.Code == "A30");

    /// <summary>A <c>/Filter</c> written as a reference to a name object (§7.3.10) is still LZW.</summary>
    [Fact]
    public void AnIndirectFilter_ReportsA30()
        => Assert.Contains(Validate(Pdf(extraStream: "/Filter 6 0 R", sixth: "/LZWDecode"), PdfAConformancePart.Part1).Violations,
                           x => x.Code == "A30");

    /// <summary>
    /// An object the parser cannot read might be an LZW stream: the check says it skipped it, as
    /// an A99, rather than pass over it. The parseable file is the control.
    /// </summary>
    [Theory]
    [InlineData(">> ]")]
    [InlineData("<< /A [1 2 >>")]
    public void AnUnreadableObject_IsReportedAsPartial(string broken)
    {
        var result = Validate(Pdf(sixth: broken), PdfAConformancePart.Part1);
        Assert.Contains(result.Informational, i => i.Code == "A99" && i.Message.Contains("A30 LZW check skipped 1 object"));

        Assert.DoesNotContain(Validate(Pdf(sixth: "/LZWDecode"), PdfAConformancePart.Part1).Informational,
                              i => i.Message.Contains("A30"));
    }

    // ---- A31: the rest of the same rules (the published profiles 6.1.7.2-1 / 6.1.10-1, PDF/A-4 6.1.6.2-1 / 6.1.9-1) ----

    [Theory]
    [InlineData(PdfAConformancePart.Part2, "ISO 19005-2 §6.1.7.2, rule 6.1.7.2-1")]
    [InlineData(PdfAConformancePart.Part3, "ISO 19005-3 §6.1.7.2, rule 6.1.7.2-1")]
    [InlineData(PdfAConformancePart.Part4, "ISO 19005-4 §6.1.6.2, rule 6.1.6.2-1")]
    public void AnUnknownStreamFilter_ReportsA31WithItsPartsClause(PdfAConformancePart part, string clause)
    {
        var a31 = Assert.Single(Validate(Pdf(extraStream: "/Filter /BogusDecode"), part).Violations, x => x.Code == "A31");
        Assert.Contains("stream object 5 uses /BogusDecode", a31.Message);
        Assert.Contains(clause, a31.Message);
    }

    /// <summary>
    /// A filter the library decodes is not thereby one PDF/A permits: <c>/BrotliDecode</c>, read since
    /// 2026-10-17, is outside ISO 32000's table and stays an A31 at every part that has the rule. Guards
    /// the permitted list against being taken from the decoder's registry.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part2)]
    [InlineData(PdfAConformancePart.Part4)]
    public void ABrotliStream_ReportsA31_ThoughTheLibraryDecodesIt(PdfAConformancePart part)
    {
        var a31 = Assert.Single(Validate(Pdf(extraStream: "/Filter /BrotliDecode"), part).Violations, x => x.Code == "A31");
        Assert.Contains("uses /BrotliDecode", a31.Message);
    }

    /// <summary>PDF/A-1's §6.1.10 bans LZW alone, so the same file is not an A31 there. The
    /// part-2 verdict on the same bytes is the potency control.</summary>
    [Fact]
    public void AnUnknownStreamFilter_IsNotA31AtPart1()
    {
        byte[] pdf = Pdf(extraStream: "/Filter /BogusDecode");
        Assert.DoesNotContain(Validate(pdf, PdfAConformancePart.Part1).Violations, x => x.Code == "A31");
        Assert.Contains(Validate(pdf, PdfAConformancePart.Part2).Violations, x => x.Code == "A31");
    }

    /// <summary>A stream's Crypt filter is permitted only when it names Identity — §7.4.10's
    /// default when <c>/DecodeParms</c> names none — and only first in the chain, where §7.4.10
    /// requires it. An abbreviation is an inline-image spelling, not a stream filter.</summary>
    [Theory]
    [InlineData("/Filter /Crypt /DecodeParms << /Name /StdCF >>", "/Crypt")]
    [InlineData("/Filter [/FlateDecode /Crypt]", "/Crypt")]
    [InlineData("/Filter /Fl", "/Fl")]
    public void AForbiddenStreamFilter_ReportsA31(string extraStream, string named)
        => Assert.Contains(Validate(Pdf(extraStream: extraStream), PdfAConformancePart.Part2).Violations,
                           x => x.Code == "A31" && x.Message.Contains("uses " + named));

    [Theory]
    [InlineData("/Filter /Crypt")]
    [InlineData("/Filter /Crypt /DecodeParms << /Name /Identity >>")]
    [InlineData("/Filter [/Crypt /FlateDecode] /DecodeParms [<< /Name /Identity >> null]")]
    [InlineData("/Filter [/ASCII85Decode /FlateDecode]")]
    [InlineData("/Filter /JBIG2Decode")]
    [InlineData("/Filter /JPXDecode")]
    public void APermittedStreamFilter_DoesNotReportA31(string extraStream)
        => Assert.DoesNotContain(Validate(Pdf(extraStream: extraStream), PdfAConformancePart.Part2).Violations,
                                 x => x.Code == "A31");

    /// <summary>An inline image may not use Crypt at all, nor JBIG2 or JPX (not in Table 92).</summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part2, "/F /Crypt", "/Crypt", "ISO 19005-2 §6.1.10, rule 6.1.10-1")]
    [InlineData(PdfAConformancePart.Part4, "/F /JBIG2Decode", "/JBIG2Decode", "ISO 19005-4 §6.1.9, rule 6.1.9-1")]
    [InlineData(PdfAConformancePart.Part3, "/Filter [/AHx /JPXDecode]", "/JPXDecode", "ISO 19005-3 §6.1.10, rule 6.1.10-1")]
    public void AForbiddenInlineImageFilter_ReportsA31(PdfAConformancePart part, string filter, string named, string clause)
    {
        var a31 = Assert.Single(Validate(Pdf($"BI /W 1 /H 1 /CS /G /BPC 8 {filter} ID 00> EI"), part).Violations,
                                x => x.Code == "A31");
        Assert.Contains("an inline image on page 1 uses " + named, a31.Message);
        Assert.Contains(clause, a31.Message);
    }

    [Theory]
    [InlineData("/F /Fl")]
    [InlineData("/F [/AHx /DCT]")]
    [InlineData("/Filter /CCITTFaxDecode")]
    public void APermittedInlineImageFilter_DoesNotReportA31(string filter)
        => Assert.DoesNotContain(Validate(Pdf($"BI /W 1 /H 1 /CS /G /BPC 8 {filter} ID 00> EI"), PdfAConformancePart.Part2).Violations,
                                 x => x.Code == "A31");

    /// <summary>LZW is A30's: one finding, not two. A30's presence is the potency check.</summary>
    [Fact]
    public void Lzw_IsReportedAsA30Only()
    {
        var violations = Validate(Pdf("BI /W 1 /H 1 /CS /G /BPC 8 /F /LZW ID 00> EI", "/Filter /LZWDecode"),
                                  PdfAConformancePart.Part2).Violations;
        Assert.Contains(violations, x => x.Code == "A30");
        Assert.DoesNotContain(violations, x => x.Code == "A31");
    }

    private static PdfAValidationResult Validate(byte[] pdf, PdfAConformancePart part)
        => PdfAValidator.Validate(new MemoryStream(pdf), part);

    /// <summary>Objects 1-4 are catalog, page tree, page and content; 5 an unused stream with
    /// <paramref name="extraStream"/> in its dictionary; 6, when given, <paramref name="sixth"/>
    /// written as it is.</summary>
    private static byte[] Pdf(string content = "q Q", string extraStream = "", string? sixth = null)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(acc.Count); acc.AddRange(Encoding.Latin1.GetBytes(body)); }

        acc.AddRange(Encoding.Latin1.GetBytes("%PDF-1.7\n"));
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< {extraStream} /Length 1 >>\nstream\nx\nendstream\nendobj\n");
        if (sixth != null) Obj($"6 0 obj\n{sixth}\nendobj\n");
        int xref = acc.Count;
        var tail = new StringBuilder($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) tail.Append($"{o:D10} 00000 n \n");
        tail.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        acc.AddRange(Encoding.Latin1.GetBytes(tail.ToString()));
        return acc.ToArray();
    }
}
