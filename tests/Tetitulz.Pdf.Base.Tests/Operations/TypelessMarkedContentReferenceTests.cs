// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A marked-content reference with no <c>/Type</c> is still a marked-content reference.
/// </summary>
/// <remarks>
/// <para>§14.7.2 Table 324 makes <c>/Type</c> <b>Optional</b> on an MCR and <c>/MCID</c>
/// <b>Required</b>, so <c>/K &lt;&lt; /MCID 0 &gt;&gt;</c> is perfectly legal — and both Base
/// readers classified a <c>/K</c> dictionary by <c>/Type</c> alone.</para>
///
/// <para><b>What that cost.</b> <c>PdfTaggedTextExtractor</c> treated the typeless dictionary as a
/// nested structure element, recursed into it, found no <c>/K</c> of its own, and emitted no
/// block — so <c>ExtractText</c> returned the <b>empty string</b> for a document whose content is
/// properly marked. <c>PdfStructureTreeReader</c> reported a phantom child typed
/// <c>"StructElem"</c>. Neither threw; both returned a clean, wrong answer.</para>
///
/// <para>Asserted on the extracted text and the parsed element, never on "it returned something" —
/// an empty extraction is the correct answer for most PDFs, which is exactly why the naive
/// assertion is worthless here.</para>
/// </remarks>
public class TypelessMarkedContentReferenceTests
{
    [Theory]
    [InlineData(false)]   // /K << /MCID 0 >>
    [InlineData(true)]    // /K [ << /MCID 0 >> ]  — the array spelling, equally legal
    public void TypelessMcr_TextIsStillExtracted(bool asArray)
    {
        using var ms = new MemoryStream(TypelessMcrPdf(asArray));

        string text = PdfTaggedTextExtractor.ExtractText(ms);

        Assert.Contains("Alpha", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypelessMcr_IsReportedAsALeafNotAPhantomElement(bool asArray)
    {
        using var ms = new MemoryStream(TypelessMcrPdf(asArray));

        var tree = new[] { PdfStructureTreeReader.ReadStructureTree(ms) };

        var paragraph = Flatten(tree).Single(e => e.ElementType == "P");
        var kid = Assert.Single(paragraph.Children);

        // A leaf, named for what it is — not "StructElem" with the parent's page index.
        Assert.Equal("MCR", kid.ElementType);
        Assert.Equal(-1, kid.PageIndex);
        Assert.Empty(kid.Children);
    }

    /// <summary>
    /// The floor: a genuine nested structure element must still be recursed into, so the rule
    /// cannot be "treat every typeless dictionary as a leaf".
    /// </summary>
    [Fact]
    public void TypelessDictionaryWithAnS_IsStillAnElement()
    {
        using var ms = new MemoryStream(NestedElementPdf());

        var tree = new[] { PdfStructureTreeReader.ReadStructureTree(ms) };

        // <P> -> <Span> -> MCID leaf: the middle element must not have been flattened away.
        Assert.Contains(Flatten(tree), e => e.ElementType == "Span");
        Assert.Contains("Alpha", PdfTaggedTextExtractor.ExtractText(new MemoryStream(NestedElementPdf())));
    }

    // ------------------------------------------------------------------ helpers

    private static System.Collections.Generic.IEnumerable<PdfStructElement> Flatten(
        System.Collections.Generic.IEnumerable<PdfStructElement> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in Flatten(n.Children)) yield return c;
        }
    }

    private const string Content =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Alpha) Tj ET\nEMC\n";

    private static byte[] TypelessMcrPdf(bool asArray)
    {
        string k = asArray ? "[ << /MCID 0 >> ]" : "<< /MCID 0 >>";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R >> >> /StructParents 0 >>",
            "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture) +
            " >>\nstream\n" + Content + "endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K " + k + " >>");
    }

    private static byte[] NestedElementPdf() => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
        " /Resources << /Font << /F1 5 0 R >> >> /StructParents 0 >>",
        "<< /Length " + Content.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + Content + "endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        "<< /Type /StructTreeRoot /K [7 0 R] >>",
        // The inner element carries /S but NO /Type — it is still an element, not an MCR.
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K << /S /Span /Pg 3 0 R /K [0] >> >>");

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
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
