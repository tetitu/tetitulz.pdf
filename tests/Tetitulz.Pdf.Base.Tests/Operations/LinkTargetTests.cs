// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Where a link points beyond its page: the destination's top (§12.3.2.2), and the actions a viewer
/// reports rather than ignores — <c>/Named</c>, <c>/GoToR</c>, <c>/Launch</c> (§12.6.4).
/// </summary>
public class LinkTargetTests
{
    // ── The destination's top ─────────────────────────────────────────────────

    [Theory]
    [InlineData("[3 0 R /XYZ 72 640 0]", 640f)]
    [InlineData("[3 0 R /FitH 500]", 500f)]
    [InlineData("[3 0 R /FitBH 420.5]", 420.5f)]
    [InlineData("[3 0 R /FitR 10 100 300 700]", 700f)]      // left bottom right TOP
    public void AnExplicitDestination_GivesItsTop(string dest, float top)
    {
        PdfLink link = Assert.Single(Links(Page($"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest {dest} >>")));
        Assert.Equal(PdfLinkKind.GoTo, link.Kind);
        Assert.Equal(0, link.TargetPageIndex);
        Assert.Equal(top, link.TargetTop);
    }

    /// <summary>The forms that name no top, and a null top ("unchanged"), give none — the page's top is meant.</summary>
    [Theory]
    [InlineData("[3 0 R /Fit]")]
    [InlineData("[3 0 R /FitB]")]
    [InlineData("[3 0 R /FitV 100]")]
    [InlineData("[3 0 R /XYZ 72 null 0]")]
    [InlineData("[3 0 R /XYZ]")]
    public void ADestinationWithNoTop_GivesNone(string dest)
    {
        PdfLink link = Assert.Single(Links(Page($"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest {dest} >>")));
        Assert.Equal(0, link.TargetPageIndex);                  // potency: the page was still found
        Assert.Null(link.TargetTop);
    }

    [Fact]
    public void AGoToAction_GivesItsDestinationsTop()
    {
        PdfLink link = Assert.Single(Links(Page(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /GoTo /D [3 0 R /XYZ null 333 null] >> >>")));
        Assert.Equal(333f, link.TargetTop);
    }

    /// <summary>A named destination's top comes from the array the name tree holds for it.</summary>
    [Fact]
    public void ANamedDestination_GivesTheTopOfTheArrayItNames()
    {
        PdfLink link = Assert.Single(Links(Page(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest (chapter) >>",
            catalogExtra: "/Names << /Dests << /Names [(chapter) [3 0 R /XYZ 0 512 null]] >> >>")));
        Assert.Equal(0, link.TargetPageIndex);
        Assert.Equal(512f, link.TargetTop);
    }

    // ── The other actions ─────────────────────────────────────────────────────

    [Fact]
    public void ANamedAction_IsANamedLink_WithItsName()
    {
        PdfLink link = Assert.Single(Links(Page("<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /Named /N /NextPage >> >>")));
        Assert.Equal(PdfLinkKind.Named, link.Kind);
        Assert.Equal("NextPage", link.NamedAction);
        Assert.Null(link.File);
    }

    /// <summary>The file is the file alone — <c>DescribeTarget</c>'s <c>file:destination</c> would break on <c>C:\</c>.</summary>
    [Fact]
    public void ARemoteGoTo_NamesItsFile_AndThePageTheOtherFileOpensAt()
    {
        PdfLink link = Assert.Single(Links(Page(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /GoToR /F << /Type /Filespec /F (old.pdf) /UF (C:\\\\docs\\\\other.pdf) >> /D [0 /Fit] >> >>")));
        Assert.Equal(PdfLinkKind.RemoteGoTo, link.Kind);
        Assert.Equal(@"C:\docs\other.pdf", link.File);         // /UF before /F (Table 43)
        Assert.Equal(0, link.TargetPageIndex);                  // the other file's page, as written
        Assert.Null(link.TargetTop);
    }

    /// <summary>
    /// A remote destination names its page by number (§12.6.4.3), answered as written — the other
    /// file's page count is not known — with the top its form gives; a named one, a non-integer, a
    /// negative and a number past an int are the other file's, or nothing's, to resolve.
    /// </summary>
    [Theory]
    [InlineData("[3 /XYZ 0 500 0]", 3, 500f)]
    [InlineData("[12 /FitH 240]", 12, 240f)]
    [InlineData("(chapter2)", -1, null)]
    [InlineData("[1.5 /Fit]", -1, null)]
    [InlineData("[-1 /Fit]", -1, null)]
    [InlineData("[4294967297 /Fit]", -1, null)]
    public void ARemoteDestination_GivesThePageAndTopItNames(string dest, int page, float? top)
    {
        PdfLink link = Assert.Single(Links(Page(
            $"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /GoToR /F (other.pdf) /D {dest} >> >>")));
        Assert.Equal(PdfLinkKind.RemoteGoTo, link.Kind);
        Assert.Equal("other.pdf", link.File);
        Assert.Equal(page, link.TargetPageIndex);
        Assert.Equal(top, link.TargetTop);
    }

    /// <summary>A top past a <c>float</c>'s range is none, for a local and a remote destination alike: a cast would be infinite.</summary>
    [Fact]
    public void ATopPastAFloatsRange_IsNone()
    {
        string past = "1" + new string('0', 40) + ".0";          // a real literal: 10^40, finite as a double only
        var links = Links(Page(
            $"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /Dest [3 0 R /XYZ 0 {past} 0] >>",
            secondAnnot: $"<< /Type /Annot /Subtype /Link /Rect [60 0 90 50] /A << /S /GoToR /F (o.pdf) /D [2 /XYZ 0 {past} 0] >> >>"));
        Assert.Equal(2, links.Count);
        Assert.Equal(0, links[0].TargetPageIndex);              // potency: the page was found
        Assert.Null(links[0].TargetTop);
        Assert.Equal(2, links[1].TargetPageIndex);
        Assert.Null(links[1].TargetTop);
    }

    [Theory]
    [InlineData("/F (setup.exe)", "setup.exe")]
    [InlineData("/Win << /F (cmd.exe) /P (/c dir) >>", "cmd.exe")]
    public void ALaunch_NamesTheFileItWouldOpen(string entries, string file)
    {
        PdfLink link = Assert.Single(Links(Page($"<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /Launch {entries} >> >>")));
        Assert.Equal(PdfLinkKind.Launch, link.Kind);
        Assert.Equal(file, link.File);
    }

    /// <summary>A script stays Unknown: a link does not run JavaScript. The URI control shows the action was read.</summary>
    [Fact]
    public void AJavaScriptLink_StaysUnknown()
    {
        var links = Links(Page(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 50 50] /A << /S /JavaScript /JS (app.alert(1)) >> >>",
            secondAnnot: "<< /Type /Annot /Subtype /Link /Rect [60 0 90 50] /A << /S /URI /URI (https://example.com/) >> >>"));
        Assert.Equal(2, links.Count);
        Assert.Equal(PdfLinkKind.Unknown, links[0].Kind);
        Assert.Equal(PdfLinkKind.Uri, links[1].Kind);
    }

    /// <summary>The appended members keep the values every caller compiled against.</summary>
    [Fact]
    public void PdfLinkKind_KeepsItsValues()
    {
        Assert.Equal(0, (int)PdfLinkKind.Unknown);
        Assert.Equal(1, (int)PdfLinkKind.Uri);
        Assert.Equal(2, (int)PdfLinkKind.GoTo);
        Assert.Equal(3, (int)PdfLinkKind.Named);
        Assert.Equal(4, (int)PdfLinkKind.RemoteGoTo);
        Assert.Equal(5, (int)PdfLinkKind.Launch);
    }

    // ----------------------------------------------------------------

    private static IReadOnlyList<PdfLink> Links(byte[] pdf)
        => PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf, writable: false));

    /// <summary>One page (object 3) carrying the annotation (object 4), and optionally a second (object 5).</summary>
    private static byte[] Page(string annot, string catalogExtra = "", string? secondAnnot = null)
    {
        string annots = secondAnnot == null ? "[4 0 R]" : "[4 0 R 5 0 R]";
        var bodies = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots {annots} >>",
            annot,
        };
        if (secondAnnot != null) bodies.Add(secondAnnot);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
