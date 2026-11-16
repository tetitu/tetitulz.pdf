// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAValidator"/>'s A21 — ISO 19005-1:2005 rule 6.2.3.2-1 in the published profile's wording: an
/// ICCBased colour space's embedded profile must be one PDF 1.4 accepts — device class
/// <c>prtr</c>, <c>mntr</c>, <c>scnr</c> or <c>spac</c>; data colour space <c>RGB</c>, <c>CMYK</c>,
/// <c>GRAY</c> or <c>Lab</c>; version below 3.0.
/// </summary>
/// <remarks>
/// <para>The fixtures are the 132-byte header-only profiles A20's tests use, with the device class
/// (offset 12) and the version's major byte (offset 8) written as well, so each clause can be failed
/// on its own while the other two hold. The register once described this rule as judging the
/// profile connection space; it judges the data colour space, and the fixture writes nothing at
/// offset 20 so a check reading the PCS by mistake would see four NUL bytes and fail the
/// acceptance rows.</para>
/// </remarks>
public class PdfAIccProfileHeaderTests
{
    [Theory]
    [InlineData("mntr", "RGB ", 2)]
    [InlineData("prtr", "CMYK", 2)]
    [InlineData("scnr", "GRAY", 2)]
    [InlineData("spac", "Lab ", 2)]
    public void AProfilePdfA1Accepts_IsNotReported(string deviceClass, string dataSpace, int majorVersion)
    {
        var result = Validate(PdfWithIccProfile(deviceClass, dataSpace, majorVersion));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A21");
    }

    [Fact]
    public void ADeviceLinkProfile_ReportsA21_NamingTheDeviceClass()
    {
        var result = Validate(PdfWithIccProfile("link", "RGB ", 2));
        var a21 = Assert.Single(result.Violations, v => v.Code == "A21");
        Assert.Contains("device class 'link'", a21.Message);
        Assert.DoesNotContain("data colour space", a21.Message);
        Assert.DoesNotContain("ICC version", a21.Message);
    }

    [Fact]
    public void AProfileWhoseDataSpaceIsNotRgbCmykGrayOrLab_ReportsA21_NamingTheSpace()
    {
        var result = Validate(PdfWithIccProfile("mntr", "XYZ ", 2));
        var a21 = Assert.Single(result.Violations, v => v.Code == "A21");
        Assert.Contains("data colour space 'XYZ '", a21.Message);
        Assert.DoesNotContain("device class", a21.Message);
    }

    /// <summary>ICC v4 profiles postdate PDF 1.4; the published profile's test is <c>version &lt; 3.0</c>.</summary>
    [Fact]
    public void AVersion4Profile_ReportsA21_NamingTheVersion()
    {
        var result = Validate(PdfWithIccProfile("mntr", "RGB ", 4));
        var a21 = Assert.Single(result.Violations, v => v.Code == "A21");
        Assert.Contains("ICC version 4", a21.Message);
    }

    /// <summary>Every failed clause is named in the one violation.</summary>
    [Fact]
    public void AProfileFailingEveryClause_ReportsOneA21_NamingAllThree()
    {
        var result = Validate(PdfWithIccProfile("abst", "XYZ ", 4));
        var a21 = Assert.Single(result.Violations, v => v.Code == "A21");
        Assert.Contains("device class 'abst'", a21.Message);
        Assert.Contains("data colour space 'XYZ '", a21.Message);
        Assert.Contains("ICC version 4", a21.Message);
    }

    /// <summary>
    /// The fail-safe direction, as A20: a 132-byte body without the <c>'acsp'</c> signature is not a
    /// profile, so nothing about it can be verified wrong. Its other fields would fail every clause
    /// if they were judged — which is what makes a check that skips the gate visible here; a body
    /// that satisfied the clauses would pass whether or not the gate ran.
    /// </summary>
    [Fact]
    public void ABodyWithoutTheAcspSignature_IsNotAProfile_AndIsNotReported()
    {
        var result = Validate(PdfWithRawIccProfile(ProfileBody("link", "XYZ ", 4, signature: "xxxx")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "A21");
    }

    /// <summary>
    /// One verdict per profile, A20 first: a profile whose <c>/N</c> disagrees with its data space is
    /// reported for that and its header is not judged.
    /// </summary>
    [Fact]
    public void AProfileFailingBothRules_ReportsA20AndNotA21()
    {
        var result = Validate(PdfWithIccProfile("link", "GRAY", 2, declaredN: 3));
        Assert.Single(result.Violations, v => v.Code == "A20");
        Assert.DoesNotContain(result.Violations, v => v.Code == "A21");
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    private static PdfAValidationResult Validate(byte[] pdf)
        => PdfAValidator.Validate(new MemoryStream(pdf));

    private static byte[] PdfWithIccProfile(string deviceClass, string dataSpace, int majorVersion, int? declaredN = null)
    {
        int n = declaredN ?? ComponentCount(dataSpace);
        return PdfWithRawIccProfile(ProfileBody(deviceClass, dataSpace, majorVersion), n);
    }

    private static int ComponentCount(string dataSpace) => dataSpace switch
    {
        "GRAY" => 1,
        "CMYK" => 4,
        _ => 3,
    };

    private static byte[] PdfWithRawIccProfile(string profileBody, int declaredN = 3)
    {
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /ColorSpace << /CS1 [/ICCBased 5 0 R] >> >> >>",
            StreamObject("", "/CS1 cs 1 0 0 1 sc 10 10 100 100 re f"),
            StreamObject($" /N {declaredN}", profileBody),
        });
    }

    /// <summary>
    /// A 132-byte header-only profile: the major version at byte 8, the device class at 12, the
    /// data colour space at 16, <c>'acsp'</c> at 36, a zero tag count.
    /// </summary>
    private static string ProfileBody(string deviceClass, string dataSpace, int majorVersion, string signature = "acsp")
    {
        var bytes = new char[132];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = '\0';
        bytes[8] = (char)majorVersion;
        deviceClass.CopyTo(0, bytes, 12, 4);
        dataSpace.CopyTo(0, bytes, 16, 4);
        signature.CopyTo(0, bytes, 36, 4);
        return new string(bytes);
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
