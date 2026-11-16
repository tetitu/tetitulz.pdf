// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// A16–A18 in every part: a device colour space painted needs an output intent of its class (or a
/// <c>Default*</c> space, or in PDF/A-4 a blending space of its class) — ISO 19005-1 6.2.3.3,
/// ISO 19005-2/3/4 6.2.4.3, the published profile's rules of the same numbers. From PDF/A-2 a transparency group's
/// blending space counts as used; PDF/A-4 reads a page's own output intent. In every part, a stream
/// that begins in the initial graphics state paints in DeviceGray until it sets a colour space.
/// </summary>
public class PdfADeviceColourRuleTests
{
    /// <summary>Object 5 is an RGB profile, object 6 a CMYK one.</summary>
    private static string Intent(int profile) =>
        $"/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (x) /DestOutputProfile {profile} 0 R >>]";

    private const string RgbIntent = "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (x) /DestOutputProfile 5 0 R >>]";
    private const string CmykIntent = "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (x) /DestOutputProfile 6 0 R >>]";
    private const string CalRgb = "/ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >>";

    private static byte[] Document(string content, string pageExtras = "", string resources = "", string catalogExtras = "",
        params string[] extras)
    {
        byte[] rgb = IccProfileFixtures.Rgb(), cmyk = IccProfileFixtures.Cmyk();
        byte[] Stream(string keys, byte[] data) => Encoding.ASCII.GetBytes("<< " + keys + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
            .Concat(data).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray();
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R " + catalogExtras + " >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> /Encoding << /Differences [] >> /FirstChar 0 /LastChar 0 /Widths [0] >> >> " + resources + " >> " + pageExtras + " >>"),
            Stream("", Encoding.ASCII.GetBytes(content)),
            Stream("/N 3", rgb),
            Stream("/N 4", cmyk),
        };
        objects.AddRange(extras.Select(e => Encoding.ASCII.GetBytes(e)));
        using var ms = new MemoryStream();
        void Write(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.7\n");
        var offsets = new List<long>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            Write((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            ms.Write(objects[i], 0, objects[i].Length);
            Write("\nendobj\n");
        }
        long xref = ms.Position;
        Write("xref\n0 " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (long o in offsets) Write(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size " + (objects.Count + 1).ToString(CultureInfo.InvariantCulture) + " /Root 1 0 R >>\nstartxref\n" +
              xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return ms.ToArray();
    }

    private static string[] Codes(int part, string content, string pageExtras = "", string resources = "", string catalogExtras = "",
        params string[] extras)
    {
        using var ms = new MemoryStream(Document(content, pageExtras, resources, catalogExtras, extras));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations
            .Select(v => v.Code).Where(c => c == "A16" || c == "A17" || c == "A18").Distinct().OrderBy(c => c).ToArray();
    }

    // -------------------------------------------------------------- the device families, parts 2 to 4

    [Theory]
    [InlineData(2, "1 0 0 rg 0 0 9 9 re f", "", "A16")]
    [InlineData(2, "1 0 0 rg 0 0 9 9 re f", RgbIntent, "")]
    [InlineData(3, "1 0 0 rg 0 0 9 9 re f", CmykIntent, "A16")]
    [InlineData(2, "0 0 0 1 k 0 0 9 9 re f", RgbIntent, "A17")]
    [InlineData(4, "0 0 0 1 k 0 0 9 9 re f", CmykIntent, "")]
    [InlineData(2, "0.5 g 0 0 9 9 re f", "", "A18")]
    [InlineData(2, "0.5 g 0 0 9 9 re f", CmykIntent, "")]
    public void EachPart_JudgesTheDeviceFamiliesItPaints(int part, string content, string catalog, string expected)
        => Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected }, Codes(part, content, catalogExtras: catalog));

    /// <summary>
    /// <c>/G cs</c> names a <c>/ColorSpace</c> resource called <c>G</c>: the abbreviations are reserved
    /// for inline images (§8.9.7). Read as DeviceGray, a page painting only a calibrated gray it named
    /// <c>G</c> was charged A18. <c>/DeviceGray cs</c> is still device gray (the control).
    /// </summary>
    [Fact]
    public void ACsOperandSpelledLikeAnAbbreviation_NamesAResource()
    {
        string calGray = "/ColorSpace << /G [/CalGray << /WhitePoint [0.9505 1 1.089] >>] >>";
        Assert.Empty(Codes(2, "/G cs 0.5 sc 0 0 9 9 re f", resources: calGray));
        Assert.Equal(new[] { "A18" }, Codes(2, "/DeviceGray cs 0.5 sc 0 0 9 9 re f", resources: calGray));
    }

    [Fact]
    public void ADefaultSpace_StandsForTheDeviceSpace()
    {
        string defaultRgb = "/ColorSpace << /DefaultRGB [/ICCBased 5 0 R] >>";
        Assert.Empty(Codes(2, "1 0 0 rg 0 0 9 9 re f", resources: defaultRgb));
        Assert.Equal(new[] { "A16" }, Codes(2, "1 0 0 rg 0 0 9 9 re f"));
    }

    [Theory]
    [InlineData("/Group << /S /Transparency /CS /DeviceCMYK >>", "A17")]
    [InlineData("/Group << /S /Transparency /CS [/ICCBased 6 0 R] >>", "")]
    public void FromPdfA2_APageGroupsBlendingSpace_IsUsed(string group, string expected)
    {
        string content = "/CS0 cs 0 0 0 sc 0 0 9 9 re f";
        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected },
            Codes(2, content, group, CalRgb, RgbIntent));
        // PDF/A-1 reads no blending space (A11 forbids the group outright).
        Assert.DoesNotContain("A17", Codes(1, content, group, CalRgb, RgbIntent));
    }

    [Fact]
    public void FromPdfA2_ADrawnFormsBlendingSpace_IsUsed_AndAnUndrawnOnesIsNot()
    {
        const string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] /Group << /S /Transparency /CS /DeviceCMYK >> /Length 11 >>\nstream\n0 0 9 9 re n\nendstream";
        string resources = CalRgb + " /XObject << /X0 7 0 R >>";
        Assert.Equal(new[] { "A17" }, Codes(2, "/CS0 cs 0 0 0 sc /X0 Do", "", resources, RgbIntent, form));
        Assert.Empty(Codes(2, "/CS0 cs 0 0 0 sc 0 0 9 9 re f", "", resources, RgbIntent, form));
    }

    [Fact]
    public void PdfA4_ReadsAPagesOwnOutputIntent()
    {
        Assert.Equal(new[] { "A16" }, Codes(4, "1 0 0 rg 0 0 9 9 re f", CmykIntent, "", RgbIntent));
        Assert.Empty(Codes(4, "0 0 0 1 k 0 0 9 9 re f", CmykIntent, "", RgbIntent));
        // PDF/A-2 has no page-level intent: the document's decides.
        Assert.Equal(new[] { "A17" }, Codes(2, "0 0 0 1 k 0 0 9 9 re f", CmykIntent, "", RgbIntent));
    }

    [Theory]
    [InlineData("/Group << /S /Transparency /CS [/ICCBased 5 0 R] >>")]
    [InlineData("/Group << /S /Transparency /CS [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >>")]
    public void PdfA4_AnRgbBlendingSpace_AdmitsDeviceRgb(string group)
    {
        Assert.Empty(Codes(4, "1 0 0 rg 0 0 9 9 re f", group));
        Assert.Equal(new[] { "A16" }, Codes(2, "1 0 0 rg 0 0 9 9 re f", group));
        // It does not admit DeviceCMYK.
        Assert.Equal(new[] { "A17" }, Codes(4, "0 0 0 1 k 0 0 9 9 re f", group));
    }

    [Fact]
    public void PdfA4_ACmykBlendingSpace_AdmitsDeviceCmyk_AndNotDeviceRgb()
    {
        const string group = "/Group << /S /Transparency /CS [/ICCBased 6 0 R] >>";
        Assert.Empty(Codes(4, "0 0 0 1 k 0 0 9 9 re f", group));
        Assert.Equal(new[] { "A16" }, Codes(4, "1 0 0 rg 0 0 9 9 re f", group));
    }

    // -------------------------------------------------------------- the initial graphics state

    [Theory]
    [InlineData("0 0 9 9 re f", "A18")]                                  // filled before any colour is set
    [InlineData("0.5 sc 0 0 9 9 re f", "A18")]                           // sc sets a colour in the current space
    [InlineData("/CS0 cs 0 0 0 sc 0 0 m 9 9 l S", "A18")]                // the stroke space still DeviceGray
    [InlineData("q /CS0 cs Q 0 0 9 9 re f", "A18")]                      // Q restores it
    [InlineData("BT /F1 1 Tf (a) Tj ET", "A18")]                         // text fills
    [InlineData("BT 3 Tr /F1 1 Tf (a) Tj ET", "")]                       // invisible text paints nothing
    [InlineData("/CS0 cs 0 0 0 sc 0 0 9 9 re f", "")]
    [InlineData("/CS0 cs 0 0 0 sc /CS0 CS 0 0 0 SC 0 0 9 9 re B", "")]
    [InlineData("0 0 9 9 re n", "")]                                     // a path ended unpainted
    public void AStreamInTheInitialState_PaintsInDeviceGray_UntilItSetsASpace(string content, string expected)
    {
        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected }, Codes(2, content, resources: CalRgb));
        // The same in PDF/A-1, whose rule this is too.
        Assert.Equal(expected.Length == 0 ? Array.Empty<string>() : new[] { expected }, Codes(1, content, resources: CalRgb));
    }

    [Fact]
    public void AnAppearanceStream_BeginsInTheInitialStateToo()
    {
        const string annot = "<< /Type /Annot /Subtype /Square /Rect [1 1 9 9] /F 4 /AP << /N 8 0 R >> >>";
        const string appearance = "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] /Length 12 >>\nstream\n0 0 9 9 re f\nendstream";
        Assert.Equal(new[] { "A18" }, Codes(2, "/CS0 cs 0 0 0 sc 0 0 9 9 re f", "/Annots [7 0 R]", CalRgb, "", annot, appearance));
        Assert.Empty(Codes(2, "/CS0 cs 0 0 0 sc 0 0 9 9 re f", "", CalRgb));
    }
}
