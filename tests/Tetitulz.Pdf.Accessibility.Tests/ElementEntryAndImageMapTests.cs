// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The deep checker's UA33 (a Formula's /Alt, ISO 14289-1 7.7), UA34 (a Note's unique /ID, 7.9) and
/// UA35 (a link's URI action with /IsMap, 7.18.5); and the optional content name rule PDF/UA-1's UA58 and
/// PDF/UA-2's §8.7 check read from one place, since the two parts state it in the same words.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class ElementEntryAndImageMapTests
{
    private const string StandardEncryptDict =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
      + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
      + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    private static byte[] Pdf(IReadOnlyList<string> objects, string trailerExtra = "")
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string Stream(string text)
        => "<< /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "endstream";

    /// <summary>
    /// A tagged one-page document whose element 7, of <paramref name="role"/> with
    /// <paramref name="entries"/>, and element 8, a P with <paramref name="otherEntries"/>, sit under a
    /// Document element.
    /// </summary>
    private static byte[] Tagged(string role, string entries, string otherEntries = "", string roleMap = "", bool encrypted = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 >>",
            Stream("/Span <</MCID 0>> BDC 0 0 1 1 re f EMC\n/Span <</MCID 1>> BDC 2 2 1 1 re f EMC\n"),
            "<< /Type /StructTreeRoot /K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1"
                + (roleMap.Length > 0 ? " /RoleMap << " + roleMap + " >>" : "") + " >>",
            "<< /Type /StructElem /S /Document /P 5 0 R /K [7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /" + role + " /P 6 0 R /Pg 3 0 R /K 0 " + entries + " >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 1 " + otherEntries + " >>",
            "<< /Nums [0 [7 0 R 8 0 R]] >>",
        };
        if (encrypted) objects.Add(StandardEncryptDict);
        return Pdf(objects, encrypted ? " /Encrypt 10 0 R" : "");
    }

    private static List<AccessibilityIssue> Coded(byte[] pdf, string code, AuditOptions? options = null)
        => PdfAccessibility.Audit(new MemoryStream(pdf), options ?? new AuditOptions()).Issues
            .Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    // -------------------------------------------------------------- UA33

    [Theory]
    [InlineData("Formula", "", "", "Formula element has no /Alt")]
    [InlineData("Formula", "/Alt ()", "", "Formula element has an empty /Alt")]
    [InlineData("Formula", "/Alt (  )", "", "Formula element has an empty /Alt")]
    [InlineData("Formula", "/ActualText (x2)", "", "Formula element has no /Alt")]            // 7.7 names Alt alone
    [InlineData("MathExpr", "", "/MathExpr /Formula", "Formula element has no /Alt")]
    [InlineData("Formula", "/Alt (x squared)", "", null)]
    [InlineData("P", "", "", null)]
    public void UA33_AFormulasAlt(string role, string entries, string roleMap, string? expected)
    {
        var found = Coded(Tagged(role, entries, roleMap: roleMap), "UA33");
        if (expected == null)
        {
            Assert.Empty(found);
            return;
        }
        AccessibilityIssue issue = Assert.Single(found);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.StartsWith(expected, issue.Message, StringComparison.Ordinal);
        Assert.Equal(7, issue.ObjectNumber);
    }

    /// <summary>
    /// On an encrypted document an /Alt that is present is accepted, and the report says its emptiness
    /// was not judged; a missing /Alt is a dictionary fact and still reported.
    /// </summary>
    [Fact]
    public void UA33_OnAnEncryptedDocument_JudgesPresenceAndSaysSo()
    {
        byte[] present = Tagged("Formula", "/Alt ()", encrypted: true);
        Assert.Empty(Coded(present, "UA33"));
        Assert.Contains(Coded(present, "UA99"), i => i.Message.Contains("UA33's emptiness rule was weakened", StringComparison.Ordinal));
        Assert.Single(Coded(Tagged("Formula", "", encrypted: true), "UA33"));
    }

    // -------------------------------------------------------------- UA34

    [Theory]
    [InlineData("Note", "", "", "", "Note element has no /ID")]
    [InlineData("Note", "/ID /n1", "", "", "Note element has an /ID that is not a byte string")]
    [InlineData("Note", "/ID ()", "", "", "Note element has an empty /ID")]
    [InlineData("Note", "/ID (n1)", "/ID (n1)", "", "Note element has an /ID another structure element also carries")]
    [InlineData("Note", "/ID <6E31>", "/ID (n1)", "", "Note element has an /ID another structure element also carries")]   // the same bytes, as hex
    [InlineData("Footnote", "", "", "/Footnote /Note", "Note element has no /ID")]
    [InlineData("Note", "/ID (n1)", "/ID (n2)", "", null)]
    [InlineData("P", "", "/ID (n2)", "", null)]
    public void UA34_ANotesId(string role, string entries, string otherEntries, string roleMap, string? expected)
    {
        var found = Coded(Tagged(role, entries, otherEntries, roleMap), "UA34");
        if (expected == null)
        {
            Assert.Empty(found);
            return;
        }
        AccessibilityIssue issue = Assert.Single(found);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.StartsWith(expected, issue.Message, StringComparison.Ordinal);
        Assert.Equal(7, issue.ObjectNumber);
    }

    /// <summary>
    /// An identifier read as ciphertext cannot be compared, so on an encrypted document a present /ID is
    /// accepted and the report says uniqueness and emptiness were not judged; a missing /ID is still
    /// reported.
    /// </summary>
    [Fact]
    public void UA34_OnAnEncryptedDocument_JudgesPresenceAndSaysSo()
    {
        byte[] duplicated = Tagged("Note", "/ID (n1)", "/ID (n1)", encrypted: true);
        Assert.Empty(Coded(duplicated, "UA34"));
        Assert.Contains(Coded(duplicated, "UA99"), i => i.Message.Contains("UA34 was weakened", StringComparison.Ordinal));
        Assert.Single(Coded(Tagged("Note", "", encrypted: true), "UA34"));
    }

    // -------------------------------------------------------------- UA35

    /// <summary>A Link annotation (object 10) a Link element holds, with <paramref name="annotationExtra"/>.</summary>
    private static byte[] LinkPdf(string annotationExtra, params string[] more)
        => AnnotationPdf("Link", annotationExtra, more);

    /// <summary>An annotation of <paramref name="subtype"/> (object 10) a Link element holds.</summary>
    private static byte[] AnnotationPdf(string subtype, string annotationExtra, params string[] more)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [10 0 R] /Tabs /S >>",
            Stream("/Artifact BMC\n0.9 g 72 100 200 200 re f\nEMC\n"),
            "<< >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /Link /P 7 0 R /Pg 3 0 R /K [11 0 R] >>",
            "<< /Nums [0 8 0 R] >>",
            "<< /Type /Annot /Subtype /" + subtype + " /Rect [72 700 260 720] /StructParent 0 /Border [0 0 0] "
                + "/Contents (Map) " + annotationExtra + " >>",
            "<< /Type /OBJR /Pg 3 0 R /Obj 10 0 R >>",
        };
        objects.AddRange(more);
        return Pdf(objects);
    }

    private const string ImageMap = "<< /S /URI /URI (http://example.com/map) /IsMap true >>";

    [Theory]
    [InlineData("/A " + ImageMap, 1)]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] /Next " + ImageMap + " >>", 1)]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] /Next [<< /S /GoTo /D [3 0 R /Fit] >> " + ImageMap + "] >>", 1)]
    [InlineData("/A << /S /URI /URI (http://example.com/map) /IsMap false >>", 0)]
    [InlineData("/A << /S /URI /URI (http://example.com/map) >>", 0)]
    [InlineData("/A << /S /GoTo /D [3 0 R /Fit] /IsMap true >>", 0)]                    // IsMap means something on a URI action only
    [InlineData("/F 2 /A " + ImageMap, 0)]                                                // hidden: outside 7.18.1's scope
    public void UA35_ALinksUriActionWithIsMap_IsAWarning(string annotationExtra, int reported)
    {
        var found = Coded(LinkPdf(annotationExtra), "UA35");
        Assert.Equal(reported, found.Count);
        if (reported == 0) return;
        Assert.Equal(IssueSeverity.Warning, found[0].Severity);
        Assert.Equal(10, found[0].ObjectNumber);
        Assert.Contains("cannot confirm", found[0].Message, StringComparison.Ordinal);
    }

    /// <summary>7.18.5 is the Links clause: another annotation's URI action with /IsMap is not UA35's.</summary>
    [Fact]
    public void UA35_AnotherAnnotationsImageMap_IsNotUa35()
    {
        Assert.Empty(Coded(AnnotationPdf("Screen", "/A " + ImageMap), "UA35"));
        Assert.Single(Coded(AnnotationPdf("Link", "/A " + ImageMap), "UA35"));
    }

    /// <summary>
    /// A /Next chain that leads back to itself is read once: an /IsMap on it is found, and a loop
    /// without one ends without reaching the ceiling, so no notice says the chain was cut.
    /// </summary>
    [Fact]
    public void UA35_ANextChainThatLoops_IsReadOnce_HostileInput()
    {
        byte[] looping = LinkPdf("/A 12 0 R",
            "<< /S /GoTo /D [3 0 R /Fit] /Next [12 0 R 13 0 R] >>",
            "<< /S /URI /URI (http://example.com/map) /IsMap true /Next 12 0 R >>");
        Assert.Single(Coded(looping, "UA35"));

        byte[] loopWithoutMap = LinkPdf("/A 12 0 R", "<< /S /GoTo /D [3 0 R /Fit] /Next 12 0 R >>");
        Assert.Empty(Coded(loopWithoutMap, "UA35"));
        Assert.DoesNotContain(Coded(loopWithoutMap, "UA99"), i => i.Message.StartsWith("UA35 stopped", StringComparison.Ordinal));
    }

    /// <summary>
    /// A /Next array wider than the ceiling is not read past it — its entries are not even queued — and
    /// the notice says the chain was cut; within the ceiling the same array is read whole.
    /// </summary>
    [Fact]
    public void UA35_ANextArrayWiderThanTheCeiling_IsCutAndSaid_HostileInput()
    {
        byte[] pdf = LinkPdf("/A << /S /GoTo /D [3 0 R /Fit] /Next [1 2 3 4 5] >>");
        Assert.DoesNotContain(Coded(pdf, "UA99"), i => i.Message.StartsWith("UA35 stopped", StringComparison.Ordinal));
        try
        {
            PdfLimits.MaxActionChainDepth = 3;
            Assert.Contains(Coded(pdf, "UA99"), i => i.Message.StartsWith("UA35 stopped", StringComparison.Ordinal));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A chain longer than <see cref="PdfLimits.MaxActionChainDepth"/> is read to the ceiling, and a
    /// UA99 says the rest was not; under the default the same chain is read whole.
    /// </summary>
    [Fact]
    public void UA35_AChainPastTheCeiling_IsCutAndSaid()
    {
        byte[] pdf = LinkPdf("/A << /S /GoTo /D [3 0 R /Fit] /Next << /S /GoTo /D [3 0 R /Fit] /Next " + ImageMap + " >> >>");
        Assert.Single(Coded(pdf, "UA35"));
        Assert.DoesNotContain(Coded(pdf, "UA99"), i => i.Message.StartsWith("UA35 stopped", StringComparison.Ordinal));
        try
        {
            PdfLimits.MaxActionChainDepth = 2;
            Assert.Empty(Coded(pdf, "UA35"));
            Assert.Contains(Coded(pdf, "UA99"), i => i.Message.StartsWith("UA35 stopped reading a link's action chain at the 2-action ceiling", StringComparison.Ordinal));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    // -------------------------------------------------------------- UA58 and UA2-8.7

    /// <summary>
    /// ISO 14289-1 7.10 and ISO 14289-2 §8.7 ask a /Name of every configuration in one sentence, so a
    /// PDF/UA-1 audit and a PDF/UA-2 audit of one file agree: an unnamed default is reported once /Configs
    /// holds a configuration dictionary, and not when it holds none.
    /// </summary>
    [Theory]
    [InlineData("/Configs [<< /Name (Print) >>]", true)]
    [InlineData("/Configs [5]", false)]
    [InlineData("", false)]
    public void UA58AndUa2_8_7_AskTheSameNames(string configs, bool reported)
    {
        byte[] pdf = Pdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [] /D << >> " + configs + " >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        });
        var ua1 = Coded(pdf, "UA58");
        var ua2 = Coded(pdf, "UA2-8.7", new AuditOptions { Target = PdfUaVersion.Ua2 })
            .Where(i => i.Message.Contains("/Name", StringComparison.Ordinal)).ToList();
        Assert.Equal(reported ? 1 : 0, ua1.Count);
        Assert.Equal(reported ? 1 : 0, ua2.Count);
    }
}
