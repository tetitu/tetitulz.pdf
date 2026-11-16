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
/// UA50 — the structure tree names a marked-content identifier the page's content stream never
/// defines.
/// <para>
/// This is the first content-level check in the plugin. It exists because a consumer of the
/// Conformance package <b>standalone</b> previously got "zero violations" for a file whose
/// structure elements pointed at MCIDs that were not in the page at all: UA28/UA29 are
/// dictionary-level and cannot see it. The documentation used to explain that away as an
/// architectural impossibility — "the plugin cannot reference a feature" — which does not follow,
/// since decoding needs only Core, and <c>PdfXValidator</c> in this same assembly already does it.
/// </para>
/// </summary>
public class PdfUaCheckerMcidExistenceTests
{
    /// <summary>The tree names MCID 5; the page defines only MCID 0. That element describes nothing.</summary>
    [Fact]
    public void StructElementNamingAMissingMcid_ReportsUA50()
    {
        byte[] pdf = TaggedPdf(contentMcid: 0, structMcid: 5);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.Contains(result.Violations, v => v.Code == "UA50");
    }

    /// <summary>
    /// The control, and the one that makes the test above mean something: when the tree names the
    /// MCID the page actually defines, UA50 stays silent. Without this the check could be
    /// "always report" and the first test would still pass.
    /// </summary>
    [Fact]
    public void StructElementNamingAPresentMcid_DoesNotReportUA50()
    {
        byte[] pdf = TaggedPdf(contentMcid: 0, structMcid: 0);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
    }

    /// <summary>
    /// A <c>BDC</c> whose property list is a name into <c>/Properties</c> carries its MCID
    /// indirectly (§14.6.2). Missing that resolution would make every such page look as though it
    /// defined no MCIDs at all — a false UA50 on correctly tagged output.
    /// </summary>
    [Fact]
    public void McidDeclaredViaPropertiesResource_IsFound()
    {
        byte[] pdf = TaggedPdf(contentMcid: 0, structMcid: 0, mcidViaProperties: true);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
    }

    /// <summary>
    /// An <c>/MCR</c> carrying <c>/Stm</c> names a stream other than the page's — a form
    /// XObject's, typically. This checker reads page content only, so it must skip those rather
    /// than measure them against the wrong stream and invent a violation.
    /// </summary>
    [Fact]
    public void McrWithStm_IsSkippedNotReported()
    {
        // MCID 5 is absent from the page, but /Stm says it lives in another stream.
        byte[] pdf = TaggedPdf(contentMcid: 0, structMcid: 5, mcrWithStm: true);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
    }

    // ------------------------------------------------------------------
    //  An MCID past the int range (2^32 + 1) is no MCID — never MCID 1
    // ------------------------------------------------------------------

    private const long PastTheIntRange = (1L << 32) + 1;

    /// <summary>
    /// The tree names 2^32 + 1; the page defines 1. <c>(int)LongValue</c> read the tree's as 1 and
    /// found it defined, so an element pointing nowhere passed. The control names 1 itself.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructElementNamingAnMcidPastTheIntRange_ReportsUA50(bool asMcr)
    {
        Assert.Contains(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: 1, structMcid: PastTheIntRange, mcr: asMcr))).Violations,
            v => v.Code == "UA50");
        Assert.DoesNotContain(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: 1, structMcid: 1, mcr: asMcr))).Violations,
            v => v.Code == "UA50");
    }

    /// <summary>The page marks 2^32 + 1, inline or through <c>/Properties</c>; it defines no MCID 1.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageMarkingAnMcidPastTheIntRange_DoesNotDefineMcidOne(bool viaProperties)
    {
        Assert.Contains(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: PastTheIntRange, structMcid: 1, mcidViaProperties: viaProperties))).Violations,
            v => v.Code == "UA50");
        Assert.DoesNotContain(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: 1, structMcid: 1, mcidViaProperties: viaProperties))).Violations,
            v => v.Code == "UA50");
    }

    /// <summary>
    /// UA02: the page's text takes its language from MCID 1's element. Marked 2^32 + 1, it has none
    /// — <c>(int)LongValue</c> borrowed MCID 1's.
    /// </summary>
    [Fact]
    public void TextMarkedWithAnMcidPastTheIntRange_DoesNotBorrowMcidOnesLanguage()
    {
        Assert.Contains(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: PastTheIntRange, structMcid: 1, structLang: "en-US"))).Violations,
            v => v.Code == "UA02");
        Assert.DoesNotContain(PdfUaChecker.Validate(new MemoryStream(TaggedPdf(contentMcid: 1, structMcid: 1, structLang: "en-US"))).Violations,
            v => v.Code == "UA02");
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page tagged document: the content marks text with <paramref name="contentMcid"/>,
    /// and a &lt;P&gt; structure element names <paramref name="structMcid"/>.
    /// </summary>
    private static byte[] TaggedPdf(long contentMcid, long structMcid,
        bool mcidViaProperties = false, bool mcrWithStm = false, bool mcr = false, string? structLang = null)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        string bdc = mcidViaProperties
            ? "/P /MC0 BDC"
            : $"/P << /MCID {contentMcid} >> BDC";
        string content = $"{bdc}\nBT /F1 12 Tf 20 100 Td (Hi) Tj ET\nEMC\n";

        string properties = mcidViaProperties
            ? $"/Properties << /MC0 << /MCID {contentMcid} >> >>"
            : "";

        // The <P> element's /K: a bare MCID, or an /MCR that may carry /Stm.
        string k = mcrWithStm
            ? $"<< /Type /MCR /Pg 3 0 R /MCID {structMcid} /Stm 4 0 R >>"
            : mcr
                ? $"<< /Type /MCR /Pg 3 0 R /MCID {structMcid} >>"
                : $"{structMcid}";
        // The document's language: the catalog's, or — with structLang — the <P> element's alone.
        string catalogLang = structLang == null ? " /Lang (en-US)" : "";
        string elementLang = structLang == null ? "" : $" /Lang ({structLang})";

        Emit("%PDF-1.7\n");
        BeginObj(1);
        Emit("<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R " +
             $"/MarkInfo << /Marked true >>{catalogLang} >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
             $"/StructParents 0 /Resources << /Font << /F1 5 0 R >> {properties} >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5);
        Emit("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        BeginObj(6);
        Emit("<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R >>\nendobj\n");
        BeginObj(7);
        Emit($"<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R] >>\nendobj\n");
        BeginObj(8);
        Emit($"<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K {k}{elementLang} >>\nendobj\n");
        BeginObj(9);
        Emit("<< /Nums [0 [8 0 R]] >>\nendobj\n");

        int xref = acc.Count;
        const int count = 10;
        Emit($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
