// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// A figure whose <c>/Alt</c> is an indirect reference must count as <b>having</b> alt text.
/// </summary>
/// <remarks>
/// <para><c>AnalyzeStructure</c> read <c>node.Dict?[PdfName.Alt]</c> raw, and <c>StructNode.Dict</c>
/// holds the element dictionary with raw entries — so <c>/Alt 12 0 R</c> yielded a
/// <c>PdfReference</c>, cast to null, and the figure was counted in
/// <c>FiguresMissingAlt</c>.</para>
///
/// <para><b>The wrong direction for an accessibility audit.</b> The count accuses a conforming
/// document of a PDF/UA failure it does not have, and a remediation run driven by it then goes
/// looking for figures to fix that are already correct. The sole call site has had the document in
/// hand on the same line all along (<c>RoleMapResolver.Build(doc)</c>).</para>
///
/// <para><b>Observed at <c>Inspect</c> rather than through <c>RemediateWithReport</c>.</b> The
/// pipeline reaches this counter through passes that rewrite the document and can re-take the
/// snapshot, so a test driven from there measures what those passes preserve as much as it measures
/// this read.</para>
/// </remarks>
public class IndirectFigureAltTests
{
    /// <summary>
    /// The figure counts as already described, whichever way <c>/Alt</c> is spelled. <c>false</c>
    /// is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Alt (A chart)
    [InlineData(true)]   // /Alt 9 0 R
    public void AFigureWithAltCountsAsDescribed_WhicheverWayAltIsSpelled(bool indirect)
    {
        Assert.Equal(0, FiguresMissingAlt(TaggedFigurePdf(indirect, withAlt: true)));
    }

    /// <summary>
    /// The floor: a figure that really has no <c>/Alt</c> must be counted as missing, so the
    /// assertion above is a claim about the read rather than about a counter stuck at zero.
    /// </summary>
    [Fact]
    public void AFigureWithoutAltIsCountedAsMissing()
    {
        Assert.Equal(1, FiguresMissingAlt(TaggedFigurePdf(indirect: false, withAlt: false)));
    }

    /// <summary>
    /// The counter <c>AnalyzeStructure</c> produces, read from the snapshot <c>Inspect</c> builds.
    /// Reflection because both are private; that is the cost of observing the narrowest thing the
    /// change alters.
    /// </summary>
    private static int FiguresMissingAlt(byte[] pdf)
    {
        MethodInfo inspect = typeof(PdfAccessibility).GetMethod("Inspect",
            BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(byte[]) }, null);
        Assert.NotNull(inspect);

        object snapshot = inspect!.Invoke(null, new object[] { pdf });
        Assert.NotNull(snapshot);

        FieldInfo figures = snapshot!.GetType().GetField("FigureCount",
            BindingFlags.NonPublic | BindingFlags.Instance);
        FieldInfo missing = snapshot.GetType().GetField("FiguresMissingAlt",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(figures);
        Assert.NotNull(missing);

        // A floor for the reflection itself: a snapshot that found no figures would report zero
        // missing for every fixture, which would make the assertions above vacuous.
        Assert.Equal(1, (int)figures!.GetValue(snapshot));

        return (int)missing!.GetValue(snapshot);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A tagged document with one <c>Figure</c> element. Object 9 holds the alt string when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] TaggedFigurePdf(bool indirect, bool withAlt)
    {
        string alt = !withAlt ? "" : indirect ? " /Alt 9 0 R" : " /Alt (A chart)";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /StructParents 0 >>",
            StreamObj("/Figure <</MCID 0>> BDC 0 0 0 rg 20 20 60 60 re f EMC\n"),
            "<< /Type /StructTreeRoot /K [6 0 R] >>",
            "<< /Type /StructElem /S /Figure /P 5 0 R /Pg 3 0 R /K 0" + alt + " >>",
            "null", "null",
            "(A chart)");
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
