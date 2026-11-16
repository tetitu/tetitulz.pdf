// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using A = Tetitulz.Pdf.Plugins.Conformance.Tests.PdfAJpxImageRuleTests;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// X20–X23: the four conditions a published PDF/X preflight check names for a <c>JPXDecode</c> image —
/// colour channels, colour specifications, CIEJab, bit depth — checked at PDF/X-4 with PDF/A-2's
/// limits, through the reading PDF/A's A24, A25, A27 and A28 use. Each rule has its refusal and its
/// acceptance control; X-1a and X-3 (no <c>JPXDecode</c> in their PDF versions) judge none.
/// </summary>
public sealed class PdfXJpxImageRuleTests
{
    private static JpxLosslessFixtures.Fixture Rgb => JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels;
    private static JpxLosslessFixtures.Fixture GrayAlpha => JpxLosslessFixtures.GrayAlpha40x24_OneLevel;

    private static readonly string[] JpxCodes = { "X20", "X21", "X22", "X23" };

    /// <summary>A raw two-component codestream has two colour channels: X20 at X-4, not at X-1a or X-3; three channels, and two whose cdef types one as opacity, pass.</summary>
    [Fact]
    public void TwoColourChannels_ReportX20_AtX4Only()
    {
        byte[] two = A.Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, "");
        var x20 = Assert.Single(Validate(two, PdfXConformanceLevel.X4).Violations, v => v.Code == "X20");
        Assert.Contains("has 2", x20.Message, StringComparison.Ordinal);
        Assert.StartsWith("PDF/X-4 ", x20.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Validate(two, PdfXConformanceLevel.X1a).Violations, v => JpxCodes.Contains(v.Code));
        Assert.DoesNotContain(Validate(two, PdfXConformanceLevel.X3).Violations, v => JpxCodes.Contains(v.Code));

        Assert.DoesNotContain(Validate(A.Pdf(Rgb.Codestream, Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X20");
        byte[] greyAndOpacity = A.Pdf(JpxEmbedFixtures.GreyWithOpacityJp2(), GrayAlpha.Width, GrayAlpha.Height, "/SMaskInData 1");
        Assert.DoesNotContain(Validate(greyAndOpacity, PdfXConformanceLevel.X4).Violations, v => v.Code == "X20");
    }

    /// <summary>Two colr boxes with neither, or both, at APPROX 1 report X21; one at APPROX 1 passes, and so does a dictionary naming its own /ColorSpace.</summary>
    [Fact]
    public void SeveralColourSpecificationsWithoutOneApproxOne_ReportX21_UnlessTheDictionaryNamesAColourSpace()
    {
        byte[] profile = H.IccProfile("RGB ");
        byte[] neither = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16, approximation: 0), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(neither, PdfXConformanceLevel.X4).Violations, v => v.Code == "X21" && v.Message.Contains("2 specifications, 0 with APPROX 1", StringComparison.Ordinal));

        byte[] both = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16, approximation: 1), H.ColrIcc(profile, approximation: 1)), Rgb.Width, Rgb.Height, "");
        Assert.Contains(Validate(both, PdfXConformanceLevel.X4).Violations, v => v.Code == "X21");

        byte[] one = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16, approximation: 1), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "");
        Assert.DoesNotContain(Validate(one, PdfXConformanceLevel.X4).Violations, v => v.Code == "X21");

        byte[] named = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16, approximation: 0), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8");
        Assert.DoesNotContain(Validate(named, PdfXConformanceLevel.X4).Violations, v => v.Code == "X21");
    }

    /// <summary>EnumCS 19 (CIEJab) reports X22; 16 (sRGB) does not, nor does CIEJab under a dictionary naming its /ColorSpace.</summary>
    [Fact]
    public void CieJab_ReportsX22_SRgbDoesNot()
    {
        Assert.Contains(Validate(A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(19)), Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X22");
        Assert.DoesNotContain(Validate(A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X22");
        Assert.DoesNotContain(Validate(A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(19)), Rgb.Width, Rgb.Height, "/ColorSpace /DeviceRGB /BitsPerComponent 8"), PdfXConformanceLevel.X4).Violations, v => v.Code == "X22");
    }

    /// <summary>A Bits Per Component box, or a SIZ giving the channels unequal depths, reports X23; the same codestream at one depth does not.</summary>
    [Fact]
    public void VaryingDepths_ReportX23_OneDepthDoesNot()
    {
        byte[] varying = H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(16));
        Assert.Contains(Validate(A.Pdf(varying, Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X23" && v.Message.Contains("bpcc", StringComparison.Ordinal));
        Assert.Contains(Validate(A.Pdf(UnequalSizDepths(), Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X23" && v.Message.Contains("has depths 8, 12, 8", StringComparison.Ordinal));
        Assert.DoesNotContain(Validate(A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X23");
        Assert.DoesNotContain(Validate(A.Pdf(Rgb.Codestream, Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4).Violations, v => v.Code == "X23");
    }

    /// <summary>The RGB codestream with its second component's SIZ depth made 12 (Ssiz 11); the rules read headers only, so it need not decode.</summary>
    private static byte[] UnequalSizDepths()
    {
        byte[] cs = (byte[])Rgb.Codestream.Clone();
        int siz = A.IndexOfMarker(cs, 0xFF51);
        // marker, Lsiz, Rsiz, the eight four-byte sizes and offsets, Csiz, then Ssiz XRsiz YRsiz per component
        cs[siz + 2 + 2 + 2 + 32 + 2 + 3] = 11;
        return cs;
    }

    /// <summary>
    /// The two validators answer one question: each defective image reports PDF/A-2's code and
    /// PDF/X-4's together, and the clean image reports neither. The vendor <c>METH</c> is PDF/A's
    /// alone (A26) — not among the conditions that check names — and the same image proves PDF/A sees it.
    /// </summary>
    [Fact]
    public void PdfAAndPdfX_ReportTheSameDefects_ExceptTheMethodRule()
    {
        byte[] profile = H.IccProfile("RGB ");
        var pairs = new (byte[] Pdf, string PdfA, string PdfX)[]
        {
            (A.Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, ""), "A24", "X20"),
            (A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16, approximation: 0), H.ColrIcc(profile, approximation: 0)), Rgb.Width, Rgb.Height, ""), "A25", "X21"),
            (A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(19)), Rgb.Width, Rgb.Height, ""), "A27", "X22"),
            (A.Pdf(H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, ""), "A28", "X23"),
            (A.Pdf(UnequalSizDepths(), Rgb.Width, Rgb.Height, ""), "A28", "X23"),
        };
        foreach (var (pdf, pdfA, pdfX) in pairs)
        {
            Assert.Contains(ValidateA(pdf).Violations, v => v.Code == pdfA);
            Assert.Contains(Validate(pdf, PdfXConformanceLevel.X4).Violations, v => v.Code == pdfX);
        }

        byte[] clean = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(16)), Rgb.Width, Rgb.Height, "");
        Assert.DoesNotContain(ValidateA(clean).Violations, v => string.CompareOrdinal(v.Code, "A24") >= 0 && string.CompareOrdinal(v.Code, "A29") <= 0);
        Assert.DoesNotContain(Validate(clean, PdfXConformanceLevel.X4).Violations, v => JpxCodes.Contains(v.Code));

        byte[] vendor = A.Pdf(A.Jp2(Rgb, H.Box("colr", new byte[] { 4, 0, 0, 0, 0, 0, 0 })), Rgb.Width, Rgb.Height, "");
        Assert.Contains(ValidateA(vendor).Violations, v => v.Code == "A26");
        Assert.DoesNotContain(Validate(vendor, PdfXConformanceLevel.X4).Violations, v => JpxCodes.Contains(v.Code));
    }

    /// <summary>Two images with every defect report each code once, each naming the check's check as its source.</summary>
    [Fact]
    public void EveryCodeIsReportedOnce_NamingItsSource()
    {
        byte[] jp2 = H.Jp2File(Rgb.Codestream, H.Ihdr(24, 40, 3, 255), H.Bpcc(7, 7, 7), H.ColrEnumerated(19, approximation: 0), H.ColrEnumerated(16, approximation: 0));
        var found = Validate(A.Pdf(jp2, Rgb.Width, Rgb.Height, "", secondImage: jp2), PdfXConformanceLevel.X4).Violations.Where(v => JpxCodes.Contains(v.Code)).ToList();
        Assert.Equal(new[] { "X21", "X22", "X23" }, found.Select(v => v.Code).OrderBy(c => c, StringComparer.Ordinal).ToArray());
        Assert.All(found, v => Assert.EndsWith("(a published PDF/X JPEG 2000 preflight check, its limits as ISO 19005-2 §6.2.8.3 states them).", v.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// The walk stops early only once every code a validator holds is reported: a CIEJab image on
    /// page 1 and a two-channel one on page 2 report both defects, at PDF/X-4 and at PDF/A-2.
    /// </summary>
    [Fact]
    public void DefectsOnALaterPage_AreFound_ByBothValidators()
    {
        byte[] pdf = A.Pdf(A.Jp2(Rgb, H.ColrEnumerated(19)), Rgb.Width, Rgb.Height, "",
            secondImage: GrayAlpha.Codestream, secondImageOnPageTwo: true);
        var x = Validate(pdf, PdfXConformanceLevel.X4).Violations;
        Assert.Contains(x, v => v.Code == "X22" && v.Message.Contains("on page 1", StringComparison.Ordinal));
        Assert.Contains(x, v => v.Code == "X20" && v.Message.Contains("on page 2", StringComparison.Ordinal));
        var a = ValidateA(pdf).Violations;
        Assert.Contains(a, v => v.Code == "A27" && v.Message.Contains("on page 1", StringComparison.Ordinal));
        Assert.Contains(a, v => v.Code == "A24" && v.Message.Contains("on page 2", StringComparison.Ordinal));
    }

    /// <summary>
    /// The caller's content ceilings reach the walk: a one-byte page ceiling, and a one-byte
    /// document ceiling, each leave the page's scan partial, which X99 names for X20-X23; the
    /// defaults (the control) scan it whole. The image is a resource, so X20 is still found.
    /// </summary>
    [Theory]
    [InlineData(1, PdfXValidator.DefaultMaxDocumentContentBytes)]
    [InlineData(PdfXValidator.DefaultMaxPageContentBytes, 1L)]
    public void ACeilingTheCallerSets_ReachesTheJpxWalk_AsX99(int maxPage, long maxDocument)
    {
        byte[] two = A.Pdf(GrayAlpha.Codestream, GrayAlpha.Width, GrayAlpha.Height, "");
        using (var ms = new MemoryStream(two, writable: false))
        {
            var tight = PdfXValidator.Validate(ms, PdfXConformanceLevel.X4, maxPage, maxDocument);
            Assert.Contains(tight.Informational, v => v.Code == "X99" && v.Message.Contains("the X20-X23 JPEG 2000 check did not run in full", StringComparison.Ordinal));
            Assert.Contains(tight.Violations, v => v.Code == "X20");
        }
        Assert.DoesNotContain(Validate(two, PdfXConformanceLevel.X4).Informational, v => v.Message.Contains("X20-X23", StringComparison.Ordinal));
    }

    /// <summary>An image whose SIZ cannot be read is named in <see cref="PdfXValidator.LastValidationError"/> and judged by no code; a readable one leaves the slot clear.</summary>
    [Fact]
    public void AnUnreadableCodestream_IsNamedInLastValidationError()
    {
        byte[] garbage = A.Pdf(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 2 }, 8, 8, "");
        var result = Validate(garbage, PdfXConformanceLevel.X4);
        Assert.Contains("its JPEG 2000 SIZ could not be read; X20-X23 not checked for it", PdfXValidator.LastValidationError, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Violations, v => JpxCodes.Contains(v.Code));

        Validate(A.Pdf(Rgb.Codestream, Rgb.Width, Rgb.Height, ""), PdfXConformanceLevel.X4);
        Assert.DoesNotContain("X20-X23", PdfXValidator.LastValidationError ?? "", StringComparison.Ordinal);
    }

    private static PdfXValidationResult Validate(byte[] pdf, PdfXConformanceLevel level)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfXValidator.Validate(ms, level);
    }

    private static PdfAValidationResult ValidateA(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfAValidator.Validate(ms, PdfAConformancePart.Part2);
    }
}
