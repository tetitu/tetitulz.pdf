// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The document-level rules: A60 (<c>/NeedAppearances</c>), A61 (permissions), A62 (PDF/A-4's
/// catalog <c>/Version</c>), A63 (alternate presentations and presentation steps) and A64 (the XMP
/// packet header's attributes), against the published profile's rules of their clauses.
/// </summary>
public class PdfADocumentLevelRuleTests
{
    private static List<PdfAViolation> Coded(string catalogExtra, int part, string code, string pageExtra = "", params string[] extra)
        => PdfAXObjectRuleTests.Coded(PdfAXObjectRuleTests.Document(catalogExtra, pageExtra, "", extra), part, code);

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- A60

    [Theory]
    [InlineData("true", "/NeedAppearances true")]
    [InlineData("false", null)]
    public void A60_NeedAppearances_IsEveryPartsFinding(string value, string? expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
            AssertFinding(Coded(" /AcroForm << /Fields [] /NeedAppearances " + value + " >>", part, "A60"), expected);
    }

    // -------------------------------------------------------------- A61

    [Theory]
    [InlineData("/UR3 5 0 R /DocMDP 5 0 R", 2, null)]
    [InlineData("/UR3 5 0 R /DocMDP 5 0 R", 4, "/Perms /UR3")]
    [InlineData("/DocMDP 5 0 R", 4, null)]
    [InlineData("/Other 5 0 R", 3, "/Perms /Other")]
    [InlineData("/Other 5 0 R", 1, null)]
    public void A61_APermissionsKey_IsOneThePartAdmits(string keys, int part, string? expected)
        => AssertFinding(Coded(" /Perms << " + keys + " >>", part, "A61", "", "<< /Type /Sig >>"), expected);

    [Theory]
    [InlineData("/DigestMethod /MD5", 2, "the DocMDP signature's /Reference 0 /DigestMethod")]
    [InlineData("/DigestValue (x)", 3, "the DocMDP signature's /Reference 0 /DigestValue")]
    [InlineData("/DigestLocation [0 1]", 2, "the DocMDP signature's /Reference 0 /DigestLocation")]
    [InlineData("/DigestMethod /MD5", 4, null)]
    [InlineData("/Data 1 0 R", 2, null)]
    public void A61_ADocMdpReference_CarriesNoDigest(string keys, int part, string? expected)
        => AssertFinding(Coded(" /Perms << /DocMDP 5 0 R >>", part, "A61", "",
            "<< /Type /Sig /Reference [<< /Type /SigRef /TransformMethod /DocMDP " + keys + " >>] >>"), expected);

    // -------------------------------------------------------------- A62

    [Theory]
    [InlineData("/2.0", null)]
    [InlineData("/2.9", null)]
    [InlineData("/1.7", "/1.7")]
    [InlineData("/2,0", "/2,0")]
    [InlineData("/2.A", "/2.A")]
    [InlineData("/2.00", "/2.00")]
    [InlineData("(2.0)", "not a name")]
    public void A62_PdfA4_TheCatalogVersionReads2Dot(string value, string? expected)
    {
        AssertFinding(Coded(" /Version " + value, 4, "A62"), expected);
        Assert.Empty(Coded(" /Version " + value, 2, "A62"));
    }

    // -------------------------------------------------------------- A63

    [Fact]
    public void A63_AlternatePresentationsAndPresSteps_FromPdfA2()
    {
        foreach (int part in new[] { 2, 4 })
        {
            AssertFinding(Coded(" /Names << /AlternatePresentations << /Names [] >> >>", part, "A63"), "/Names /AlternatePresentations");
            AssertFinding(Coded("", part, "A63", " /PresSteps << /Type /NavNode >>"), "page 1 /PresSteps");
            Assert.Empty(Coded(" /Names << /Dests << /Names [] >> >>", part, "A63"));
        }
        Assert.Empty(Coded(" /Names << /AlternatePresentations << /Names [] >> >>", 1, "A63", " /PresSteps << >>"));
    }

    // -------------------------------------------------------------- A69, A73

    [Fact]
    public void A69_PdfA1_HasNoOptionalContent()
    {
        const string oc = " /OCProperties << /OCGs [] /D << >> >>";
        AssertFinding(Coded(oc, 1, "A69"), "/OCProperties");
        Assert.Empty(Coded(oc, 2, "A69"));
        Assert.Empty(Coded("", 1, "A69"));
    }

    [Theory]
    [InlineData("", 1, "A", "the catalog has no /MarkInfo")]
    [InlineData(" /MarkInfo << >>", 2, "A", "/MarkInfo has no /Marked")]
    [InlineData(" /MarkInfo << /Marked false >>", 3, "A", "/MarkInfo /Marked is not true")]
    [InlineData(" /MarkInfo << /Marked (true) >>", 1, "A", "/MarkInfo /Marked is not true")]
    [InlineData(" /MarkInfo << /Marked true >>", 2, "A", null)]
    [InlineData("", 2, "B", null)]
    [InlineData("", 2, "U", null)]
    [InlineData("", 4, null, null)]
    public void A73_ALevelAFile_IsMarked(string catalogExtra, int part, string? level, string? expected)
        => AssertFinding(PdfAXObjectRuleTests.Coded(PdfAEmbeddedFileRuleTests.PdfA(part, catalogExtra, level ?? "B"), part, "A73"), expected);

    // -------------------------------------------------------------- A64

    private static string Metadata(string header)
    {
        string xmp = "<?xpacket " + header + "?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"></x:xmpmeta><?xpacket end='w'?>";
        return "<< /Type /Metadata /Subtype /XML /Length " + Encoding.ASCII.GetByteCount(xmp) + " >>\nstream\n" + xmp + "\nendstream";
    }

    [Theory]
    [InlineData("begin='' bytes='870' id='W5M0MpCehiHzreSzNTczkc9d'", "the bytes attribute")]
    [InlineData("encoding=\"UTF-8\" begin='' id='W5M0MpCehiHzreSzNTczkc9d'", "the encoding attribute")]
    [InlineData("begin='' bytes = '1' encoding='UTF-8' id='W5M0MpCehiHzreSzNTczkc9d'", "the bytes and encoding attribute")]
    [InlineData("begin='' id='W5M0MpCehiHzreSzNTczkc9d'", null)]
    [InlineData("begin='' id='an encoding bytes'", null)]
    [InlineData("begin='' xbytes='1' id='W5M0MpCehiHzreSzNTczkc9d'", null)]            // a longer name ending in one
    public void A64_ThePacketHeader_CarriesNoBytesOrEncoding(string header, string? expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
            AssertFinding(Coded(" /Metadata 5 0 R", part, "A64", "", Metadata(header)), expected);
    }
}
