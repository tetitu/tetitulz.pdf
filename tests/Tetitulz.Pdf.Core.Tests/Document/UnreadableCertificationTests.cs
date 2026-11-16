// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A catalog that declares <c>/Perms /DocMDP</c> and names a certification that cannot be read is a
/// damaged document, not an uncertified one.
/// </summary>
/// <remarks>
/// <para><b>This is the shape GAPS.md §23 is about, at the place where it costs most.</b> A failed
/// resolve and an absent entry both produce <c>PdfNull</c>, so reading one as the other used to make
/// "the catalog names a certification I could not fetch" and "this document is not certified" the
/// same answer — and <i>not certified</i> is the answer that permits modification, permits the next
/// signature, and lets an in-place image replacement proceed.</para>
///
/// <para><b>The verdict deliberately does not change.</b> §12.8.2.2 puts the certification in the
/// signature's own <c>/Reference</c>; <c>/Perms</c> is the catalog's pointer to it, so an unreadable
/// pointer is not an unreadable certification and falling through to the signature fields stays
/// right. What changes is that the caller is now told, rather than handed a clean "no certification
/// found" about a file that says otherwise.</para>
/// </remarks>
public class UnreadableCertificationTests
{
    /// <summary>
    /// <b>A <c>/Perms</c> naming an object the cross-reference table cannot produce is reported.</b>
    /// </summary>
    [Fact]
    public void APermsEntryThatCannotBeResolvedIsReportedRatherThanReadAsUncertified()
    {
        // /Perms points at object 99, which no cross-reference entry names.
        using var doc = Open(CatalogWithPerms("99 0 R"));

        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, doc.BaseStream.Length);

        Assert.False(analysis.HasCertification);
        Assert.True(analysis.CertificationDeclarationUnreadable);
        Assert.Contains("declares /Perms /DocMDP", analysis.Summary, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A <c>/DocMDP</c> naming an unreachable signature is reported too</b> — the second link of
    /// the same chain, and the one a fixture that only breaks <c>/Perms</c> never reaches.
    /// </summary>
    [Fact]
    public void ADocMdpEntryThatCannotBeResolvedIsReportedRatherThanReadAsUncertified()
    {
        using var doc = Open(CatalogWithPerms("<< /DocMDP 99 0 R >>"));

        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, doc.BaseStream.Length);

        Assert.False(analysis.HasCertification);
        Assert.True(analysis.CertificationDeclarationUnreadable);
    }

    /// <summary>
    /// <b>The control, and the reason this flag is narrow: an ordinary uncertified document has no
    /// <c>/Perms</c> at all and must stay silent.</b> A flag that fires on every unsigned PDF is one
    /// a caller learns to ignore, which is worse than not having it.
    /// </summary>
    [Fact]
    public void AnOrdinaryDocumentWithNoPermsIsUncertifiedAndReportsNothing()
    {
        using var doc = Open(CatalogWithPerms(null));

        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, doc.BaseStream.Length);

        Assert.False(analysis.HasCertification);
        Assert.False(analysis.CertificationDeclarationUnreadable);
        Assert.Contains("No DocMDP certification", analysis.Summary, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>/Perms</c> that reads perfectly well but names no <c>/DocMDP</c> is also an ordinary
    /// uncertified document — <c>/Perms</c> carries other entries, so its mere presence is not a
    /// declaration of certification.
    /// </summary>
    [Fact]
    public void APermsWithoutADocMdpEntryIsUncertifiedAndReportsNothing()
    {
        using var doc = Open(CatalogWithPerms("<< /UR3 << /Type /Sig >> >>"));

        DocMdpAnalysis analysis = DocMdpAnalyzer.Analyze(doc, doc.BaseStream.Length);

        Assert.False(analysis.HasCertification);
        Assert.False(analysis.CertificationDeclarationUnreadable);
    }

    // ------------------------------------------------------------------
    //  Fixture assembly
    // ------------------------------------------------------------------

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    /// <summary>
    /// A one-page document whose catalog carries <paramref name="perms"/> as its <c>/Perms</c>
    /// value, or no <c>/Perms</c> at all when null.
    /// </summary>
    private static byte[] CatalogWithPerms(string perms)
    {
        string catalog = perms == null
            ? "<< /Type /Catalog /Pages 2 0 R >>"
            : $"<< /Type /Catalog /Pages 2 0 R /Perms {perms} >>";

        var bodies = new List<string>
        {
            Obj(1, catalog),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>"),
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string b in bodies) { offsets.Add(sb.Length); sb.Append(b); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
