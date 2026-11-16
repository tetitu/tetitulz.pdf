// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAValidator"/>'s A20 — ISO 19005-1:2005 rule 6.2.3.2-2 (the published profiles wording): an
/// <c>[/ICCBased …]</c> colour space's declared <c>/N</c> must match the component count its own
/// profile bytes actually declare (GRAY=1, RGB/Lab=3, CMYK=4).
/// </summary>
public class PdfAIccComponentConsistencyTests
{
    [Fact]
    public void Validate_IccBasedDeclaresN3_ButProfileIsGray_ReportsA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 3, dataColorSpace: "GRAY"));
        Assert.Contains(result.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_IccBasedDeclaresN3_AndProfileIsRgb_DoesNotReportA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 3, dataColorSpace: "RGB "));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_IccBasedDeclaresN1_AndProfileIsGray_DoesNotReportA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 1, dataColorSpace: "GRAY"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_IccBasedDeclaresN4_AndProfileIsCmyk_DoesNotReportA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 4, dataColorSpace: "CMYK"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_IccBasedDeclaresN4_ButProfileIsRgb_ReportsA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 4, dataColorSpace: "RGB "));
        Assert.Contains(result.Violations, v => v.Code == "A20");
    }

    /// <summary>Lab is a 3-component space, same as RGB — /N 3 over a Lab profile must not
    /// false-report just because the signature differs from "RGB ".</summary>
    [Fact]
    public void Validate_IccBasedDeclaresN3_AndProfileIsLab_DoesNotReportA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: 3, dataColorSpace: "Lab "));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_IccBasedWithNoNEntry_ButProfileHasAReadableColorSpace_ReportsA20()
    {
        var result = Validate(PdfWithIccColorSpace(declaredN: null, dataColorSpace: "CMYK"));
        Assert.Contains(result.Violations, v => v.Code == "A20");
    }

    /// <summary>
    /// The fail-safe direction: a profile whose header cannot be read at all (too short, no
    /// 'acsp' signature) must be skipped, not reported — "cannot verify" is not "verified wrong".
    /// </summary>
    [Fact]
    public void Validate_IccBasedOverAnUnreadableProfile_DoesNotReportA20()
    {
        var result = Validate(PdfWithRawIccColorSpace(declaredN: 3, profileBody: "not an icc profile"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    /// <summary>
    /// The same mismatched profile, reachable two ways (a <c>/ColorSpace</c> resource entry and an
    /// image XObject's own <c>/ColorSpace</c>), is reported once — this pins that outcome, not its
    /// cause. It is <see cref="CheckIccBasedComponentCounts"/>'s stop-at-the-first-finding
    /// design (the same single-verdict shape A16/A17/A18 use) that produces it here, not the
    /// <c>checkedProfiles</c> dedup guard: a mutation removing that guard does not turn this red,
    /// because the first path checked already returns before a second path is ever tried.
    /// <c>checkedProfiles</c> exists for a different, not-independently-testable-at-this-level
    /// reason instead — avoiding re-decoding and re-parsing the same profile stream once per page
    /// on a large document where it is never found to mismatch, so no violation ever short-circuits
    /// the walk.
    /// </summary>
    [Fact]
    public void Validate_TheSameMismatchedIccProfile_ReachableTwoWays_IsReportedOnce()
    {
        var result = Validate(PdfWithIccColorSpaceNamedTwice(declaredN: 3, dataColorSpace: "GRAY"));
        Assert.Single(result.Violations, v => v.Code == "A20");
    }

    /// <summary>
    /// A profile nested as an <c>/Indexed</c> base, a <c>/Separation</c> alternate or a shading's
    /// colour space is a profile in the file (a classifier descends every base), judged as a
    /// resource's own <c>[/ICCBased …]</c> is.
    /// </summary>
    [Theory]
    [InlineData("/CS1 [/Indexed [/ICCBased 5 0 R] 1 <000000FFFFFF>]", "")]
    [InlineData("/CS1 [/Separation /Spot [/ICCBased 5 0 R] << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 0 0] /N 1 >>]", "")]
    [InlineData("", "/Shading << /Sh0 << /ShadingType 2 /ColorSpace [/ICCBased 5 0 R] /Coords [0 0 10 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >> >> >>")]
    public void Validate_AnIccProfileNestedInAnotherSpace_IsJudged(string colourSpaces, string otherResources)
    {
        string resources = "<< /ColorSpace << " + colourSpaces + " >> " + otherResources + " >>";
        var mismatched = Validate(PdfWithResources(resources, declaredN: 3, dataColorSpace: "GRAY"));
        var matched = Validate(PdfWithResources(resources, declaredN: 1, dataColorSpace: "GRAY"));

        Assert.Contains(mismatched.Violations, v => v.Code == "A20");
        Assert.DoesNotContain(matched.Violations, v => v.Code == "A20");
    }

    [Fact]
    public void Validate_NoIccBasedColorSpaceAtAll_DoesNotReportA20()
    {
        var result = Validate(PdfWithContent("1 0 0 rg 10 10 100 100 re f"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A20");
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static PdfAValidationResult Validate(byte[] pdf)
        => PdfAValidator.Validate(new MemoryStream(pdf));

    private static byte[] PdfWithContent(string content)
    {
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << >> >>",
            StreamObject("", content),
        });
    }

    private static byte[] PdfWithIccColorSpace(int? declaredN, string dataColorSpace)
        => PdfWithRawIccColorSpace(declaredN, MinimalIccProfileBody(dataColorSpace));

    /// <param name="profileBody">The literal bytes of the profile stream — a well-formed ICC
    /// header from <see cref="MinimalIccProfileBody"/>, or arbitrary junk for the unreadable-
    /// profile fail-safe test.</param>
    private static byte[] PdfWithRawIccColorSpace(int? declaredN, string profileBody)
    {
        string n = declaredN.HasValue ? $" /N {declaredN.Value}" : "";
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /ColorSpace << /CS1 [/ICCBased 5 0 R] >> >> >>",
            StreamObject("", "/CS1 cs 1 0 0 1 sc 10 10 100 100 re f"),
            StreamObject(n, profileBody),
        });
    }

    /// <summary>A page with <paramref name="resources"/>, whose object 5 is the profile stream.</summary>
    private static byte[] PdfWithResources(string resources, int declaredN, string dataColorSpace)
    {
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources " + resources + " >>",
            StreamObject("", "q Q"),
            StreamObject($" /N {declaredN}", MinimalIccProfileBody(dataColorSpace)),
        });
    }

    private static byte[] PdfWithIccColorSpaceNamedTwice(int declaredN, string dataColorSpace)
    {
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /ColorSpace << /CS1 [/ICCBased 6 0 R] >> "
                + "/XObject << /Im1 5 0 R >> >> >>",
            StreamObject("", "/CS1 cs 1 0 0 1 sc 10 10 100 100 re f /Im1 Do"),
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace [/ICCBased 6 0 R] /Length 1 >>\nstream\n\0\nendstream",
            StreamObject($" /N {declaredN}", MinimalIccProfileBody(dataColorSpace)),
        });
    }

    /// <summary>
    /// The smallest byte array <c>IccProfile.Parse</c> reads far enough into to report a data
    /// colour space: 132 bytes (its own <c>MinProfileBytes</c> floor), <c>'acsp'</c> at byte 36
    /// (the file-signature check), the data colour space at byte 16, and a zero tag count — which
    /// makes <c>Parse</c> stop at "profile declares no tags" having already read and kept the
    /// signature, exactly like every other early-exit branch after that point. No tag table, no
    /// curves, no LUT: none of that is reachable from <c>IccProfileComponents</c>, which reads
    /// only <c>DataColorSpace</c>.
    /// </summary>
    private static string MinimalIccProfileBody(string dataColorSpaceSignature)
    {
        var bytes = new char[132];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = '\0';
        for (int i = 0; i < 4; i++) bytes[16 + i] = dataColorSpaceSignature[i];
        "acsp".CopyTo(0, bytes, 36, 4);
        return new string(bytes);
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>Objects are numbered 1..N in <paramref name="objects"/> order.</summary>
    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new int[objects.Length];
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = acc.Count;
            Emit($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = acc.Count;
        Emit($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int i = 0; i < objects.Length; i++)
            Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        Emit($"startxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
