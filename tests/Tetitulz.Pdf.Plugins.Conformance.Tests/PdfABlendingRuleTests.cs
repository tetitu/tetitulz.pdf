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
/// PDF/A-2 to -4's two conditions on transparency: A40 (a blend mode ISO 32000 defines) and A41 (a
/// transparent page in a file with no PDF/A output intent names its blending colour space) — ISO
/// 19005-2/3 6.2.10, ISO 19005-4 6.2.9, the published profile's rules of the same numbers.
/// </summary>
public class PdfABlendingRuleTests
{
    /// <summary>Object 5: an RGB output profile, for a catalog or page output intent to name.</summary>
    private const string Intent = "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) /DestOutputProfile 5 0 R >>]";

    private static byte[] Document(string pageExtras, string catalogExtras = "", params string[] extras)
    {
        byte[] icc = IccProfileFixtures.Rgb();
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R " + catalogExtras + " >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + pageExtras + " >>"),
            Encoding.ASCII.GetBytes("<< /Length 22 >>\nstream\n0 0 1 rg 0 0 9 9 re f\n\nendstream"),
            Encoding.ASCII.GetBytes("<< /N 3 /Length " + icc.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
                .Concat(icc).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray(),
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

    private static List<PdfAViolation> Coded(int part, string code, string pageExtras, string catalogExtras = "", params string[] extras)
    {
        using var ms = new MemoryStream(Document(pageExtras, catalogExtras, extras));
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    private static string States(string gs) => "/Resources << /ExtGState << /G0 << " + gs + " >> >> >>";

    // -------------------------------------------------------------- A40

    [Theory]
    [InlineData("/BM /Custom", true)]
    [InlineData("/BM [/Custom /Multiply]", true)]
    [InlineData("/BM /Multiply", false)]
    [InlineData("/BM /Luminosity", false)]
    [InlineData("/BM [/Normal /Screen]", false)]
    public void A40_AGraphicsStateBlendMode_MustBeStandard(string gs, bool reported)
    {
        var a40 = Coded(2, "A40", States(gs), Intent);
        if (reported)
            Assert.Contains("page 1 /G0 /BM /Custom", Assert.Single(a40).Message, StringComparison.Ordinal);
        else
            Assert.Empty(a40);
    }

    [Fact]
    public void A40_ABlendModeInAFormsResources_IsFound()
    {
        const string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] /Resources << /ExtGState << /F0 << /BM /Custom >> >> >> /Length 0 >>\nstream\n\nendstream";
        Assert.Single(Coded(3, "A40", "/Resources << /XObject << /X0 6 0 R >> >>", Intent, form));
    }

    [Fact]
    public void A40_AnAnnotationsBlendMode_IsPdfA4s_NotPdfA2s()
    {
        const string annot = "<< /Type /Annot /Subtype /Square /Rect [1 1 9 9] /F 4 /BM /Custom /AP << /N 4 0 R >> >>";
        Assert.Contains("annotation 0 /BM /Custom", Assert.Single(Coded(4, "A40", "/Annots [6 0 R]", Intent, annot)).Message,
            StringComparison.Ordinal);
        Assert.Empty(Coded(2, "A40", "/Annots [6 0 R]", Intent, annot));
    }

    [Fact]
    public void A40_IsNotAPdfA1Rule()
    {
        Assert.Empty(Coded(1, "A40", States("/BM /Custom"), Intent));
        Assert.Single(Coded(2, "A40", States("/BM /Custom"), Intent));
    }

    // -------------------------------------------------------------- A41

    [Theory]
    [InlineData(2, "", false)]                                                           // no intent, no group
    [InlineData(2, "/Group << /S /Transparency /CS /DeviceRGB >>", true)]
    [InlineData(2, "/Group << /S /Transparency >>", false)]                               // a group without /CS
    [InlineData(4, "", false)]
    public void A41_ATransparentPage_WithNoOutputIntent_NamesItsBlendingSpace(int part, string group, bool accepted)
    {
        var a41 = Coded(part, "A41", States("/ca 0.5") + " " + group);
        if (accepted)
            Assert.Empty(a41);
        else
            Assert.Contains("page 1", Assert.Single(a41).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A41_ADocumentOutputIntent_SettlesIt()
    {
        Assert.Empty(Coded(2, "A41", States("/ca 0.5"), Intent));
        // An intent with no profile does not: the published profile's gOutputCS is the profile's colour space.
        Assert.Single(Coded(2, "A41", States("/ca 0.5"),
            "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) >>]"));
        // Nor does another standard's intent.
        Assert.Single(Coded(2, "A41", States("/ca 0.5"), Intent.Replace("GTS_PDFA1", "GTS_PDFX", StringComparison.Ordinal)));
    }

    [Fact]
    public void A41_APageOutputIntent_SettlesItForPdfA4Only()
    {
        Assert.Empty(Coded(4, "A41", States("/ca 0.5") + " " + Intent));
        Assert.Single(Coded(2, "A41", States("/ca 0.5") + " " + Intent));
    }

    [Fact]
    public void A41_AnOpaquePage_NeedsNoBlendingSpace()
    {
        Assert.Empty(Coded(2, "A41", States("/ca 1.0")));
        // A page group alone is not transparent content.
        Assert.Empty(Coded(2, "A41", "/Group << /S /Transparency >>"));
    }

    [Fact]
    public void A41_AnAnnotationsBlendMode_MakesThePageTransparent()
    {
        const string annot = "<< /Type /Annot /Subtype /Square /Rect [1 1 9 9] /F 4 /BM /Multiply /AP << /N 4 0 R >> >>";
        Assert.Single(Coded(4, "A41", "/Annots [6 0 R]", "", annot));
        Assert.Empty(Coded(4, "A41", "/Annots [6 0 R] /Group << /S /Transparency /CS /DeviceRGB >>", "", annot));
    }
}
