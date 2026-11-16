// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A24–A28: ISO 19005-2 §6.2.8.3's five rules for a <c>JPXDecode</c> image, as the published profiles quotes them, and A29, the JPX baseline's codestream
/// (6.2.8.3-1 to -5), checked for PDF/A-2 and PDF/A-3 from the file's header alone; PDF/A-1 (PDF
/// 1.4, no JPXDecode) reports none of them. Each rule has its refusal and its acceptance control.
/// </summary>
public sealed class PdfAJpxImageRuleTests
{
    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture GrayAlpha => JpxLosslessFixtures.GrayAlpha40x24_OneLevel;

    /// <summary>
    /// A29: an HTJ2K codestream (HT code-blocks, ITU-T T.814) is outside the JPX baseline §6.2.8.3
    /// admits through ISO 32000-1 §7.4.9 and T.801 M.9.2.3. The RGB fixture with code-block style bit
    /// 6 set in its COD is reported for PDF/A-2 and -3; the fixture as written (the control) is not,
    /// and PDF/A-1 judges neither. The rule reads headers only, so the patched bytes need not decode.
    /// </summary>
    [Fact]
    public void AnHtj2kCodestream_ReportsA29_ForPartsTwoAndThree()
    {
        byte[] ht = (byte[])Rgb.Codestream.Clone();
        int cod = IndexOfMarker(ht, 0xFF52);
        ht[cod + 2 + 2 + 1 + 4 + 3] |= 0x40;   // marker, Lcod, Scod, SGcod, then SPcod: levels, xcb, ycb, style
        byte[] pdf = Pdf(ht, Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(pdf, PdfAConformancePart.Part2).Violations, v => v.Code == "A29" && v.Message.Contains("HT code-blocks", StringComparison.Ordinal));
        Assert.Contains(Validate(pdf, PdfAConformancePart.Part3).Violations, v => v.Code == "A29");
        Assert.DoesNotContain(Validate(pdf, PdfAConformancePart.Part1).Violations, v => v.Code == "A29");
        Assert.DoesNotContain(Validate(Pdf(Rgb.Codestream, Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A29");
    }

    internal static int IndexOfMarker(byte[] codestream, int marker)
    {
        for (int i = 2; i + 1 < codestream.Length; i++)
            if (codestream[i] == (marker >> 8) && codestream[i + 1] == (marker & 0xFF)) return i;
        throw new InvalidOperationException($"no marker {marker:X4}");
    }

    /// <summary>6.2.8.3-1: a raw two-component codestream has two colour channels; three is accepted, and PDF/A-1 does not judge it.</summary>
    [Fact]
    public void TwoColourChannels_ReportA24_ForPartsTwoAndThree()
    {
        byte[] two = Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, "");
        Assert.Contains(Validate(two, PdfAConformancePart.Part2).Violations, v => v.Code == "A24" && v.Message.Contains("has 2", StringComparison.Ordinal));
        Assert.Contains(Validate(two, PdfAConformancePart.Part3).Violations, v => v.Code == "A24");
        Assert.DoesNotContain(Validate(two, PdfAConformancePart.Part1).Violations, v => v.Code.StartsWith("A2", StringComparison.Ordinal) && v.Code != "A22" && v.Code != "A23");

        byte[] three = Pdf(Rgb.Codestream, Rgb.Width, Rgb.Height, "");
        Assert.DoesNotContain(Validate(three, PdfAConformancePart.Part2).Violations, v => v.Code == "A24");

        // The header's count, not the codestream's: the same two components under a JP2 whose cdef
        // types one as opacity are one colour channel.
        byte[] greyAndOpacity = Pdf(JpxEmbedFixtures.GreyWithOpacityJp2(), GrayAlpha.Width, GrayAlpha.Height, "/SMaskInData 1");
        Assert.DoesNotContain(Validate(greyAndOpacity, PdfAConformancePart.Part2).Violations, v => v.Code == "A24");
    }

    /// <summary>6.2.8.3-2: two colr boxes and neither with APPROX 1 is refused; one with APPROX 1 passes, and so does a dictionary that names its own /ColorSpace.</summary>
    [Fact]
    public void SeveralColourSpecificationsWithoutOneApproxOne_ReportA25_UnlessTheDictionaryNamesAColourSpace()
    {
        byte[] profile = H.IccProfile("RGB ");
        byte[] neither = Pdf(Jp2(Rgb, H.ColrEnumerated(16, approximation: 0), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(neither, PdfAConformancePart.Part2).Violations, v => v.Code == "A25" && v.Message.Contains("2 specifications, 0 with APPROX 1", StringComparison.Ordinal));

        byte[] both = Pdf(Jp2(Rgb, H.ColrEnumerated(16, approximation: 1), H.ColrIcc(profile, approximation: 1)), Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(both, PdfAConformancePart.Part2).Violations, v => v.Code == "A25");

        byte[] one = Pdf(Jp2(Rgb, H.ColrEnumerated(16, approximation: 1), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "");
        Assert.DoesNotContain(Validate(one, PdfAConformancePart.Part2).Violations, v => v.Code == "A25");

        byte[] named = Pdf(Jp2(Rgb, H.ColrEnumerated(16, approximation: 0), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        Assert.DoesNotContain(Validate(named, PdfAConformancePart.Part2).Violations, v => v.Code == "A25");
    }

    /// <summary>6.2.8.3-3: a vendor colour specification (METH 4) is refused; the enumerated, restricted-ICC and any-ICC methods pass.</summary>
    [Fact]
    public void AVendorColourMethod_ReportsA26_TheThreeStandardMethodsDoNot()
    {
        byte[] vendor = Pdf(Jp2(Rgb, H.Box("colr", new byte[] { 4, 0, 0, 0, 0, 0, 0 })), Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(vendor, PdfAConformancePart.Part2).Violations, v => v.Code == "A26" && v.Message.Contains("METH 4", StringComparison.Ordinal));

        foreach (byte[] box in new[] { H.ColrEnumerated(16), H.ColrIcc(H.IccProfile("RGB "), method: 2), H.ColrIcc(H.IccProfile("RGB "), method: 3) })
            Assert.DoesNotContain(Validate(Pdf(Jp2(Rgb, box), Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A26");
    }

    /// <summary>6.2.8.3-4: EnumCS 19 (CIEJab) is refused; 16 (sRGB) passes.</summary>
    [Fact]
    public void CieJab_ReportsA27_SRgbDoesNot()
    {
        Assert.Contains(Validate(Pdf(Jp2(Rgb, H.ColrEnumerated(19)), Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A27");
        Assert.DoesNotContain(Validate(Pdf(Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A27");
    }

    /// <summary>6.2.8.3-5: a Bits Per Component box (varying depths) is refused; the same codestream under an ihdr depth passes.</summary>
    [Fact]
    public void ABitsPerComponentBox_ReportsA28_OneDepthDoesNot()
    {
        byte[] varying = H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(16));
        Assert.Contains(Validate(Pdf(varying, Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A28" && v.Message.Contains("bpcc", StringComparison.Ordinal));
        Assert.DoesNotContain(Validate(Pdf(Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A28");
    }

    /// <summary>One image with every defect reports each code once; a second such image adds nothing.</summary>
    [Fact]
    public void EveryCodeIsReportedOnce_AcrossImages()
    {
        byte[] jp2 = H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(19, approximation: 0), H.Box("colr", new byte[] { 4, 0, 0, 0, 0, 0, 0 }));
        byte[] pdf = Pdf(jp2, Rgb.Width, Rgb.Height, "", secondImage: jp2);
        var codes = Validate(pdf, PdfAConformancePart.Part2).Violations.Select(v => v.Code).Where(c => c.CompareTo("A24") >= 0 && c.CompareTo("A28") <= 0).ToList();
        Assert.Equal(new[] { "A25", "A26", "A27", "A28" }, codes.OrderBy(c => c).ToArray());
    }

    /// <summary>
    /// PDF/A-4: the published profile's profile states ISO 19005-4 §6.2.7.3 in the same words as §6.2.8.3, so the
    /// same image reports the same codes there, each citing PDF/A-4's own clause and rule.
    /// </summary>
    [Fact]
    public void PartFour_ReportsTheFiveRules_UnderItsOwnClause()
    {
        byte[] jp2 = H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(19, approximation: 0), H.Box("colr", new byte[] { 4, 0, 0, 0, 0, 0, 0 }));
        var four = Validate(Pdf(jp2, Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part4).Violations;
        foreach (var (code, rule) in new[] { ("A25", 2), ("A26", 3), ("A27", 4), ("A28", 5) })
            Assert.Contains(four, v => v.Code == code && v.Message.StartsWith("PDF/A-4 ", StringComparison.Ordinal)
                                       && v.Message.Contains($"(ISO 19005-4 §6.2.7.3, rule 6.2.7.3-{rule})", StringComparison.Ordinal));

        var a24 = Assert.Single(Validate(Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, ""), PdfAConformancePart.Part4).Violations, v => v.Code == "A24");
        Assert.Contains("(ISO 19005-4 §6.2.7.3, rule 6.2.7.3-1)", a24.Message);

        // The clean image is the control: nothing from A24-A29 at PDF/A-4.
        Assert.DoesNotContain(Validate(Pdf(Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part4).Violations,
                              v => string.CompareOrdinal(v.Code, "A24") >= 0 && string.CompareOrdinal(v.Code, "A29") <= 0);
    }

    /// <summary>A29 at PDF/A-4 rests on ISO 32000-2 §7.4.9, the PDF 2.0 text's own JPX-baseline limit.</summary>
    [Fact]
    public void PartFour_ReportsA29_FromIso32000Part2()
    {
        byte[] ht = (byte[])Rgb.Codestream.Clone();
        ht[IndexOfMarker(ht, 0xFF52) + 2 + 2 + 1 + 4 + 3] |= 0x40;
        var a29 = Assert.Single(Validate(Pdf(ht, Rgb.Width, Rgb.Height, ""), PdfAConformancePart.Part4).Violations, v => v.Code == "A29");
        Assert.StartsWith("PDF/A-4 ", a29.Message);
        Assert.Contains("ISO 32000-2 §7.4.9", a29.Message);
        Assert.DoesNotContain("ISO 32000-1", a29.Message);
    }

    /// <summary>A PDF/A-3 file is told about PDF/A-3's clause, not PDF/A-2's.</summary>
    [Fact]
    public void PartThree_CitesIso19005Part3()
    {
        var a24 = Assert.Single(Validate(Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, ""), PdfAConformancePart.Part3).Violations, v => v.Code == "A24");
        Assert.StartsWith("PDF/A-3 ", a24.Message);
        Assert.Contains("(ISO 19005-3 §6.2.8.3, rule 6.2.8.3-1)", a24.Message);
        var two = Assert.Single(Validate(Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, ""), PdfAConformancePart.Part2).Violations, v => v.Code == "A24");
        Assert.Contains("(ISO 19005-2 §6.2.8.3, rule 6.2.8.3-1)", two.Message);
    }

    private static PdfAValidationResult Validate(byte[] pdf, PdfAConformancePart part)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfAValidator.Validate(ms, part);
    }

    internal static byte[] Jp2(JpxLosslessFixtures.Fixture f, params byte[][] headerBoxes)
    {
        var boxes = new List<byte[]> { H.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7) };
        boxes.AddRange(headerBoxes);
        return H.Jp2File(f.Codestream, boxes.ToArray());
    }

    /// <summary>A page the image's size drawing one JPXDecode image XObject (object 5), and a second (object 6) when given — on the same page, or on a second page (objects 7 and 8) when <paramref name="secondImageOnPageTwo"/>; binary-safe.</summary>
    internal static byte[] Pdf(byte[] jpx, int width, int height, string extras, byte[] secondImage = null, bool secondImageOnPageTwo = false)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }
        void Image(int number, byte[] bytes)
        {
            offsets.Add(acc.Count);
            Emit($"{number} 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {extras} /Filter /JPXDecode /Length {bytes.Length} >>\nstream\n");
            acc.AddRange(bytes);
            Emit("\nendstream\nendobj\n");
        }
        bool samePage = secondImage != null && !secondImageOnPageTwo;
        string content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q" + (samePage ? " q 1 0 0 1 0 0 cm /Im1 Do Q" : "");
        string xobjects = "/Im0 5 0 R" + (samePage ? " /Im1 6 0 R" : "");
        string pageTwoContent = $"q {width} 0 0 {height} 0 0 cm /Im1 Do Q";
        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(secondImageOnPageTwo
            ? "2 0 obj\n<< /Type /Pages /Kids [3 0 R 7 0 R] /Count 2 >>\nendobj\n"
            : "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << {xobjects} >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Image(5, jpx);
        if (secondImage != null) Image(6, secondImage);
        if (secondImageOnPageTwo)
        {
            Obj($"7 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im1 6 0 R >> >> /Contents 8 0 R >>\nendobj\n");
            Obj($"8 0 obj\n<< /Length {pageTwoContent.Length} >>\nstream\n{pageTwoContent}\nendstream\nendobj\n");
        }
        int xref = acc.Count;
        Emit($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
