// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A structure element must still be located when the marked-content identifier that places it is
/// written as an <b>indirect reference</b> — as <c>/K 9 0 R</c>, or as <c>/MCID 9 0 R</c> inside an
/// <c>/MCR</c> dictionary.
/// </summary>
/// <remarks>
/// <para><b>What the failure costs is coverage, not correctness.</b> An unplaceable element hits
/// <c>if (!resolver.TryResolve(…)) continue;</c> at the caller, so it is counted in
/// <c>ElementsTotal</c> and not in <c>ElementsPlaced</c> — which is precisely what
/// <c>IsPartial</c> reports. The caller is therefore not misled, but the repair pass sees a
/// smaller document than the one it was given, and <c>Fix</c> only permutes what it can prove.
/// A file that spells every <c>/MCID</c> indirectly was analysed down to nothing.</para>
///
/// <para><b>The two spellings were broken by two different lines.</b> <c>/MCID</c> was read
/// straight off the dictionary. <c>/K</c> <i>was</i> resolved — but only into
/// <c>as PdfDictionary</c>, and after the <c>is PdfNumber</c> test, so <c>/K 9 0 R → &lt;&lt;/MCR&gt;&gt;</c>
/// worked while <c>/K 9 0 R → 0</c> fell through to <c>return false</c>. §14.7.2 Table 323 permits
/// both forms of <c>/K</c>; §7.3.10 permits either to be indirect. <b>Resolving in the wrong order
/// looks like resolving</b>, which is why the raw-read grep did not find this one.</para>
///
/// <para>Asserted on <c>ElementsPlaced</c>. "Analyze returned a result" and even
/// <c>IsOrdered == true</c> are both satisfied by a run that placed nothing at all.</para>
/// </remarks>
public class IndirectReadingOrderReadTests
{
    /// <summary>
    /// Both paragraphs are placed whichever way the file spells their identifier.
    /// <c>Direct</c> is the control: it always worked.
    /// </summary>
    [Theory]
    [InlineData(Spelling.Direct)]
    [InlineData(Spelling.IndirectMcid)]
    [InlineData(Spelling.IndirectK)]
    public void BothElementsArePlaced_WhicheverWayTheIdentifierIsSpelled(Spelling spelling)
    {
        using var ms = new MemoryStream(TwoParagraphPdf(spelling));
        ReadingOrderAnalysis result = PdfReadingOrderAnalyzer.Analyze(ms);

        Assert.True(result.Analyzed, result.NotAnalyzedReason);
        Assert.Equal(2, result.ElementsPlaced);
        Assert.False(result.IsPartial);
    }

    /// <summary>
    /// The floor. An element whose <c>/MCID</c> names marked content the page never defines cannot
    /// be placed by any amount of resolving, so <c>ElementsPlaced</c> must fall — otherwise the
    /// assertion above would be met by a counter that increments unconditionally.
    /// </summary>
    [Fact]
    public void AnElementNamingAnAbsentMcid_IsNotPlaced()
    {
        using var ms = new MemoryStream(TwoParagraphPdf(Spelling.Direct, secondMcid: 77));
        ReadingOrderAnalysis result = PdfReadingOrderAnalyzer.Analyze(ms);

        Assert.Equal(1, result.ElementsPlaced);
        Assert.True(result.IsPartial);
    }

    /// <summary>How the fixture writes the marked-content identifier of each paragraph.</summary>
    public enum Spelling
    {
        /// <summary><c>/K &lt;&lt; /Type /MCR /MCID 0 &gt;&gt;</c>.</summary>
        Direct,

        /// <summary><c>/K &lt;&lt; /Type /MCR /MCID 9 0 R &gt;&gt;</c>.</summary>
        IndirectMcid,

        /// <summary><c>/K 9 0 R</c>, where object 9 is the integer.</summary>
        IndirectK,
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Two <c>/P</c> elements over two marked-content sequences on one page. The spelling of their
    /// identifiers is the only difference between the three fixtures; objects 9 and 10 hold the
    /// MCIDs, unused by <see cref="Spelling.Direct"/>.
    /// </summary>
    private static byte[] TwoParagraphPdf(Spelling spelling, int secondMcid = 1)
    {
        string K(int mcid, int obj) => spelling switch
        {
            Spelling.IndirectMcid => "<< /Type /MCR /MCID " + obj + " 0 R >>",
            Spelling.IndirectK    => obj + " 0 R",
            _                     => "<< /Type /MCR /MCID " + mcid + " >>",
        };

        string content = "BT /F1 12 Tf 20 160 Td /P <</MCID 0>> BDC (First) Tj EMC "
                       + "0 -60 Td /P <</MCID 1>> BDC (Second) Tj EMC ET\n";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /Font << /F1 8 0 R >> >> /StructParents 0 >>",
            StreamObj(content),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K " + K(0, 9) + " >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K " + K(secondMcid, 10) + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "0",
            secondMcid.ToString(CultureInfo.InvariantCulture));
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);

        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
