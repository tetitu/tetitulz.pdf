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
/// PDF/A-2 to -4's output intent rules: A47 (the destination profile's class, colour space and
/// version), A15 in every part (the intents share one profile), A48 (<c>/DestOutputProfileRef</c>),
/// A49 (PDF/A-4: one PDF/A output intent per array) — ISO 19005-2/3/4 6.2.3, the published profiles rules 6.2.3-1 to -4.
/// </summary>
public class PdfAOutputIntentRuleTests
{
    private static string Intent(string subtype, int profile, string extra = "")
        => $"<< /Type /OutputIntent /S /{subtype} /OutputConditionIdentifier (x) /DestOutputProfile {profile} 0 R {extra}>>";

    /// <summary>Objects 4 and 5 are profiles built from <paramref name="first"/> and <paramref name="second"/>.</summary>
    private static byte[] Document(string catalogIntents, string pageIntents = "", byte[]? first = null, byte[]? second = null)
    {
        first ??= IccProfileFixtures.Rgb();
        second ??= IccProfileFixtures.Rgb();
        byte[] Stream(byte[] data) => Encoding.ASCII.GetBytes("<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n")
            .Concat(data).Concat(Encoding.ASCII.GetBytes("\nendstream")).ToArray();
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R " + (catalogIntents.Length == 0 ? "" : "/OutputIntents [" + catalogIntents + "]") + " >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + (pageIntents.Length == 0 ? "" : "/OutputIntents [" + pageIntents + "]") + " >>"),
            Stream(first),
            Stream(second),
        };
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

    private static List<PdfAViolation> Coded(byte[] pdf, int part, string code)
    {
        using var ms = new MemoryStream(pdf);
        return PdfAValidator.Validate(ms, (PdfAConformancePart)part).Violations.Where(v => v.Code == code).ToList();
    }

    // -------------------------------------------------------------- A47

    [Theory]
    [InlineData("mntr", "RGB ", 2, null, null)]
    [InlineData("prtr", "CMYK", 4, null, "version 4")]
    [InlineData("mntr", "RGB ", 3, null, "version 3")]
    [InlineData("prtr", "GRAY", 2, null, null)]
    [InlineData("scnr", "RGB ", 2, "device class 'scnr'", "device class 'scnr'")]
    [InlineData("mntr", "Lab ", 2, "colour space 'Lab '", "colour space 'Lab '")]
    [InlineData("mntr", "RGB ", 5, "version 5", "version 5")]
    public void A47_TheDestinationProfile_IsAnOutputOrMonitorProfileBelowThePartsVersion(string deviceClass, string space, int version,
        string? expected, string? part1Expected)
    {
        byte[] pdf = Document(Intent("GTS_PDFA1", 4), first: IccProfileFixtures.Header(deviceClass, space, version));
        foreach (var (part, wanted) in new[] { (2, expected), (1, part1Expected) })
        {
            var a47 = Coded(pdf, part, "A47");
            if (wanted == null)
                Assert.Empty(a47);
            else
                Assert.Contains(wanted, Assert.Single(a47).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A47_PdfA1_CitesItsOwnClauseAndVersion()
    {
        byte[] pdf = Document(Intent("GTS_PDFA1", 4), first: IccProfileFixtures.Header("mntr", "RGB ", 4));
        string message = Assert.Single(Coded(pdf, 1, "A47")).Message;
        Assert.Contains("below version 3", message, StringComparison.Ordinal);
        Assert.Contains("ISO 19005-1 6.2.2; rule 6.2.2-1", message, StringComparison.Ordinal);
    }

    [Fact]
    public void A47_AHeaderThatIsNoIccHeader_IsReported()
    {
        byte[] garbage = Enumerable.Range(0, 200).Select(i => (byte)(i * 7)).ToArray();
        Assert.Contains("not an ICC header", Assert.Single(Coded(Document(Intent("GTS_PDFA1", 4), first: garbage), 3, "A47")).Message,
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A15, every part

    [Fact]
    public void A15_TwoIntentsNamingTwoProfiles_IsEveryPartsFinding()
    {
        string two = Intent("GTS_PDFA1", 4) + " " + Intent("GTS_PDFX", 5);
        Assert.Contains("ISO 19005-2", Assert.Single(Coded(Document(two), 2, "A15")).Message, StringComparison.Ordinal);
        Assert.Single(Coded(Document(two), 1, "A15"));
        Assert.Empty(Coded(Document(Intent("GTS_PDFA1", 4) + " " + Intent("GTS_PDFX", 4)), 2, "A15"));
    }

    // -------------------------------------------------------------- A48

    [Fact]
    public void A48_DestOutputProfileRef_InAPdfXIntentForPdfA2_InAnyForPdfA4()
    {
        string pdfaWithRef = Intent("GTS_PDFA1", 4, "/DestOutputProfileRef << /ProfileName (p) >> ");
        string pdfxWithRef = Intent("GTS_PDFX", 4, "/DestOutputProfileRef << /ProfileName (p) >> ");
        Assert.Empty(Coded(Document(pdfaWithRef), 2, "A48"));
        Assert.Contains("(/GTS_PDFX)", Assert.Single(Coded(Document(Intent("GTS_PDFA1", 4) + " " + pdfxWithRef), 2, "A48")).Message,
            StringComparison.Ordinal);
        Assert.Single(Coded(Document(pdfaWithRef), 4, "A48"));
        // PDF/A-1 has no such rule: the PDF/X intent that is PDF/A-2's finding above is not PDF/A-1's.
        Assert.Empty(Coded(Document(Intent("GTS_PDFA1", 4) + " " + pdfxWithRef), 1, "A48"));
    }

    // -------------------------------------------------------------- A49

    [Fact]
    public void A49_PdfA4_AdmitsOnePdfAIntentPerArray()
    {
        string two = Intent("GTS_PDFA1", 4) + " " + Intent("GTS_PDFA1", 4);
        Assert.Contains("the catalog holds 2", Assert.Single(Coded(Document(two), 4, "A49")).Message, StringComparison.Ordinal);
        Assert.Contains("page 1 holds 2", Assert.Single(Coded(Document(Intent("GTS_PDFA1", 4), two), 4, "A49")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Document(two), 2, "A49"));
        Assert.Empty(Coded(Document(Intent("GTS_PDFA1", 4) + " " + Intent("GTS_PDFX", 4)), 4, "A49"));
    }

    [Fact]
    public void PdfA4_APagesIntentProfile_IsJudgedToo()
    {
        byte[] pdf = Document(Intent("GTS_PDFA1", 4), Intent("GTS_PDFA1", 5), second: IccProfileFixtures.Header("scnr", "RGB "));
        Assert.Contains("page 1, intent 0: device class 'scnr'", Assert.Single(Coded(pdf, 4, "A47")).Message, StringComparison.Ordinal);
        Assert.Empty(Coded(pdf, 2, "A47"));
    }
}
