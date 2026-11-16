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
/// <see cref="PdfAValidator"/>'s A09-A14 — the six specific transparency constructs ISO 19005-1
/// §6.4 (the published profiles rules 6.4-1..6.4-6) forbids in PDF/A-1. Before this, a PDF/A-1b document using
/// real transparency validated as conformant, because the validator checked A01-A08 and nothing
/// about transparency at all — the same class of gap A08 itself once was (two parts of the same
/// assembly silently disagreeing about a clause).
///
/// <para>Every violation gets a positive control (the construct present, reported) and a negative
/// control (a conformant sibling value, not reported) — a validator that always finds something is
/// as useless as one that never does. <see cref="Validate_TransparencyConstructs_AreNotCheckedOutsidePart1"/>
/// is the other side of the scope: Parts 2/3/4 permit the transparent imaging model outright.</para>
/// </summary>
public class PdfATransparencyValidatorTests
{
    // ------------------------------------------------------------------
    //  A09 — /SMask in /ExtGState
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_ExtGStateWithSMask_ReportsA09()
    {
        var result = Validate(PdfWithExtGState("/SMask 6 0 R", extraObject: "<< /Type /Mask /S /Luminosity >>"));
        Assert.Contains(result.Violations, v => v.Code == "A09");
    }

    [Fact]
    public void Validate_ExtGStateWithSMaskNone_DoesNotReportA09()
    {
        var result = Validate(PdfWithExtGState("/SMask /None"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A09");
    }

    [Fact]
    public void Validate_ExtGStateWithNoSMaskEntry_DoesNotReportA09()
    {
        var result = Validate(PdfWithExtGState("/CA 1.0"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A09");
    }

    // ------------------------------------------------------------------
    //  A10 — /SMask directly on an XObject
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_ImageXObjectWithSMask_ReportsA10()
    {
        byte[] pdf = PdfWithImageXObject("/SMask 6 0 R",
            extraObject: "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                       + "/ColorSpace /DeviceGray /Length 1 >>\nstream\n\x00\nendstream");
        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "A10");
    }

    [Fact]
    public void Validate_ImageXObjectWithoutSMask_DoesNotReportA10()
    {
        var result = Validate(PdfWithImageXObject(""));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A10");
    }

    // ------------------------------------------------------------------
    //  A11 — a transparency group, on a page or a Form XObject
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_PageWithTransparencyGroup_ReportsA11()
    {
        var result = Validate(Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Group << /Type /Group /S /Transparency >> /Resources << >> >>",
            StreamObject("", "BT ET"),
        }));

        Assert.Contains(result.Violations, v => v.Code == "A11");
    }

    [Fact]
    public void Validate_FormXObjectWithTransparencyGroup_ReportsA11()
    {
        byte[] pdf = PdfWithFormXObject(
            "/Group << /Type /Group /S /Transparency >> ", "BT ET");
        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "A11");
    }

    [Fact]
    public void Validate_PageWithNoGroup_DoesNotReportA11()
    {
        var result = Validate(SimplePdf());
        Assert.DoesNotContain(result.Violations, v => v.Code == "A11");
    }

    // ------------------------------------------------------------------
    //  A12 — /BM must be /Normal or /Compatible
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_ExtGStateWithMultiplyBlendMode_ReportsA12()
    {
        var result = Validate(PdfWithExtGState("/BM /Multiply"));
        Assert.Contains(result.Violations, v => v.Code == "A12");
    }

    [Theory]
    [InlineData("/BM /Normal")]
    [InlineData("/BM /Compatible")]
    public void Validate_ExtGStateWithAllowedBlendMode_DoesNotReportA12(string bmEntry)
    {
        var result = Validate(PdfWithExtGState(bmEntry));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A12");
    }

    /// <summary>Table 136 permits an array of blend modes in preference order; every entry must be
    /// allowed for the whole value to be, since a reader picks the first one it supports.</summary>
    [Fact]
    public void Validate_ExtGStateWithBlendModeArrayContainingAForbiddenMode_ReportsA12()
    {
        var result = Validate(PdfWithExtGState("/BM [/Darken /Normal]"));
        Assert.Contains(result.Violations, v => v.Code == "A12");
    }

    [Fact]
    public void Validate_ExtGStateWithBlendModeArrayOfAllowedModes_DoesNotReportA12()
    {
        var result = Validate(PdfWithExtGState("/BM [/Compatible /Normal]"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A12");
    }

    // ------------------------------------------------------------------
    //  A13 / A14 — /CA and /ca must be 1.0
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_ExtGStateWithStrokeAlphaLessThanOne_ReportsA13()
    {
        var result = Validate(PdfWithExtGState("/CA 0.5"));
        Assert.Contains(result.Violations, v => v.Code == "A13");
    }

    [Fact]
    public void Validate_ExtGStateWithStrokeAlphaOfOne_DoesNotReportA13()
    {
        var result = Validate(PdfWithExtGState("/CA 1.0"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A13");
    }

    [Fact]
    public void Validate_ExtGStateWithFillAlphaLessThanOne_ReportsA14()
    {
        var result = Validate(PdfWithExtGState("/ca 0.5"));
        Assert.Contains(result.Violations, v => v.Code == "A14");
    }

    [Fact]
    public void Validate_ExtGStateWithFillAlphaOfOne_DoesNotReportA14()
    {
        var result = Validate(PdfWithExtGState("/ca 1.0"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A14");
    }

    /// <summary>
    /// The published profile's rules 6.4-5 and 6.4-6 read 1.0 within 10<sup>-6</sup> (<c>CA - 1.0 &lt; 0.000001
    /// &amp;&amp; CA - 1.0 &gt; -0.000001</c>): a writer's <c>1.0000001</c> or <c>0.9999999</c> is opaque
    /// — the conformance corpus passes both (6-4-t03-pass-b), and the validator failed it — while a
    /// difference of 2×10<sup>-6</sup> either way is not.
    /// </summary>
    [Theory]
    [InlineData("1.0000001", false)]
    [InlineData("0.9999999", false)]
    [InlineData("1.000002", true)]
    [InlineData("0.999998", true)]
    public void Validate_AlphaIsOneWithinTheProfilesTolerance(string alpha, bool reported)
    {
        var stroke = Validate(PdfWithExtGState("/CA " + alpha));
        var fill = Validate(PdfWithExtGState("/ca " + alpha));

        Assert.Equal(reported, stroke.Violations.Any(v => v.Code == "A13"));
        Assert.Equal(reported, fill.Violations.Any(v => v.Code == "A14"));
    }

    [Fact]
    public void Validate_ExtGStateWithNeitherAlphaEntry_DoesNotReportA13OrA14()
    {
        // Absence means the ISO 32000-1 §8.4.5 default of 1.0 (fully opaque) — conformant.
        var result = Validate(PdfWithExtGState("/Type /ExtGState"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A13");
        Assert.DoesNotContain(result.Violations, v => v.Code == "A14");
    }

    // ------------------------------------------------------------------
    //  Scope: Part 1 only
    // ------------------------------------------------------------------

    /// <summary>
    /// Parts 2/3/4 are based on PDF 1.7/2.0 and permit the transparent imaging model outright —
    /// the SAME document that reports A09-A14 against Part 1 must report none of them against
    /// Part 2, or the scope restriction is decorative.
    /// </summary>
    [Fact]
    public void Validate_TransparencyConstructs_AreNotCheckedOutsidePart1()
    {
        byte[] pdf = PdfWithExtGState("/SMask 6 0 R /BM /Multiply /CA 0.5 /ca 0.5",
            extraObject: "<< /Type /Mask /S /Luminosity >>");

        var part1 = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part1);
        Assert.Contains(part1.Violations, v => v.Code == "A09");
        Assert.Contains(part1.Violations, v => v.Code == "A12");
        Assert.Contains(part1.Violations, v => v.Code == "A13");
        Assert.Contains(part1.Violations, v => v.Code == "A14");

        var part2 = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A09");
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A10");
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A11");
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A12");
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A13");
        Assert.DoesNotContain(part2.Violations, v => v.Code == "A14");
    }

    // ------------------------------------------------------------------
    //  Nested Form XObjects — proves the recursive walk actually recurses
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_TransparencyGroupInsideANestedFormXObject_IsStillFound()
    {
        // Page -> Form A (no group) -> Form B (transparency group). The violation is two levels
        // down, so a walk that only looked at the page's own /Resources would miss it.
        byte[] pdf = Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /XObject << /FormA 5 0 R >> >> >>",
            StreamObject("", "/FormA Do"),
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Resources << /XObject << /FormB 6 0 R >> >> /Length 8 >>\nstream\n/FormB Do\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + "/Group << /Type /Group /S /Transparency >> /Length 2 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "A11");
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static PdfAValidationResult Validate(byte[] pdf)
        => PdfAValidator.Validate(new MemoryStream(pdf));

    private static byte[] SimplePdf() => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << >> >>",
        StreamObject("", "BT ET"),
    });

    /// <summary>A page whose single /ExtGState entry (/GS1, object 5) carries
    /// <paramref name="gsEntries"/>. <paramref name="extraObject"/>, when supplied, becomes object 6
    /// — e.g. the mask group an <c>/SMask 6 0 R</c> entry needs to resolve to something.</summary>
    private static byte[] PdfWithExtGState(string gsEntries, string extraObject = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /ExtGState << /GS1 5 0 R >> >> >>",
            StreamObject("", "/GS1 gs"),
            "<< " + gsEntries + " >>",
        };
        if (extraObject != null) objects.Add(extraObject);
        return Build(objects.ToArray());
    }

    /// <summary>A page whose single image XObject (/Im1, object 5) carries
    /// <paramref name="xobjEntries"/>. <paramref name="extraObject"/> becomes object 6, for an
    /// <c>/SMask 6 0 R</c> entry to resolve to.</summary>
    private static byte[] PdfWithImageXObject(string xobjEntries, string extraObject = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /XObject << /Im1 5 0 R >> >> >>",
            StreamObject("", "/Im1 Do"),
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray " + xobjEntries + " /Length 1 >>\nstream\n\x00\nendstream",
        };
        if (extraObject != null) objects.Add(extraObject);
        return Build(objects.ToArray());
    }

    /// <summary>A page invoking one Form XObject (/Fm1) whose own dictionary carries
    /// <paramref name="formEntries"/> and whose content stream is <paramref name="formContent"/>.</summary>
    private static byte[] PdfWithFormXObject(string formEntries, string formContent) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
            + "/Resources << /XObject << /Fm1 5 0 R >> >> >>",
        StreamObject("", "/Fm1 Do"),
        "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] " + formEntries
            + "/Length " + formContent.Length + " >>\nstream\n" + formContent + "\nendstream",
    });

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>Objects are numbered 1..N in <paramref name="objects"/> order.</summary>
    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new int[objects.Length];
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));

        Emit("%PDF-1.7\n");
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = acc.Count;
            Emit($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = acc.Count;
        Emit($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets)
            Emit($"{off:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
