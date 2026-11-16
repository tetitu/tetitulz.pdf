// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="PageTreeShape.IsLeafPage"/> must agree with <c>Core.Document.PdfPageTree</c> about
/// which nodes are pages.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>PageTreeShape</c>'s own summary says it decides "by the
/// same rule <c>PdfPageTree</c> uses", and Core's copy is <c>internal</c> so the plugin cannot call
/// it — which leaves the claim resting entirely on prose. It stopped being true for two commits:
/// Core gained an exclusion for a node that positively declares <c>/Type /Pages</c>, and this copy
/// did not follow. That is the exact defect class the round making that fix exists to remove, and a
/// doc comment asserting agreement is what made it invisible.</para>
///
/// <para><b>The assertion is differential against Core</b> — the same bytes through
/// <c>PdfDocument.Pages</c> and through <c>IsLeafPage</c> — so it cannot go stale the way the
/// sentence did. Nothing here re-states the rule; the two implementations are compared.</para>
/// </remarks>
public class PageTreeShapeParityTests
{
    [Theory]
    // An emptied branch that kept a hoisted /MediaBox: not a page, in both.
    [InlineData("Pages", false, true,  false)]
    // An intermediate node with no /Kids and nothing page-shaped: not a page, in both.
    [InlineData("Pages", false, false, false)]
    // An explicit page: a page, in both, whatever else it carries.
    [InlineData("Page",  true,  true,  true)]
    // A leaf that forgot /Type — the case the structural clause exists for.
    [InlineData(null,    true,  false, true)]
    [InlineData(null,    false, true,  true)]
    // Untyped and nothing page-shaped: not a page. (An untyped INTERMEDIATE node has /Kids and is
    // covered by the walk itself, not by this predicate.)
    [InlineData(null,    false, false, false)]
    public void IsLeafPage_AgreesWithCore(string? type, bool hasContents, bool hasMediaBox, bool expected)
    {
        // The predicate's own answer.
        bool shapeSaysPage = PageTreeShape.IsLeafPage(
            type == null ? null : new PdfName(type), kids: null, hasContents, hasMediaBox);

        Assert.Equal(expected, shapeSaysPage);

        // Core's answer for the identical node, read back through the public page list. The node
        // sits beside one unambiguous page, so Core reports 2 when it agrees the node is a page.
        using var doc = PdfDocument.Open(new MemoryStream(NodeBesideARealPage(type, hasContents, hasMediaBox)));
        bool coreSaysPage = doc.Pages.Count == 2;

        Assert.Equal(coreSaysPage, shapeSaysPage);
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>
    /// One unambiguous page, plus a second <c>/Kids</c> entry built from the parameters. The node
    /// never has <c>/Kids</c> of its own, so it is exactly the shape <c>IsLeafPage</c> judges.
    /// </summary>
    private static byte[] NodeBesideARealPage(string type, bool hasContents, bool hasMediaBox)
    {
        var node = new StringBuilder("<< /Parent 2 0 R");
        if (type != null) node.Append(" /Type /").Append(type);
        if (hasContents) node.Append(" /Contents 6 0 R");
        if (hasMediaBox) node.Append(" /MediaBox [0 0 612 792]");
        node.Append(" >>");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            node.ToString(),
            StreamObj("q Q\n"),
            StreamObj("q Q\n"));
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

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
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
