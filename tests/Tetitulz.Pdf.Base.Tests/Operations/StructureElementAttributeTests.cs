// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §14.7.5 structure-element attributes on <see cref="PdfStructElement.Attributes"/> — both the
/// inline <c>/A</c> form and the <c>/C</c> + <c>/ClassMap</c> form.
/// </summary>
/// <remarks>
/// <para><b>The engine was <c>private static</c> on a feature.</b> It powered the deep checker's
/// UA12 table rules and nothing else could reach it — <c>PdfOperations.ReadStructureTree</c>, the
/// public way to read a tagged tree, exposed no attributes at all, so a caller could not see
/// <c>/ColSpan</c>, <c>/Scope</c>, <c>/Headers</c>, <c>/Placement</c> or <c>/ListNumbering</c>. It
/// moved down into <c>Core.Document.PdfStructureAttributes</c> rather than being copied.</para>
///
/// <para><b>The fixture exercises every shape a producer may legally choose</b>, because each is a
/// separate way for a naive reader to see nothing: an indirect <c>/A</c>, a <c>/C</c> class name,
/// and an <c>/A</c> array that interleaves a revision number and carries two different owners.</para>
/// </remarks>
public class StructureElementAttributeTests
{
    /// <summary>
    /// The inline form, written as an indirect reference — legal under §7.3.10, and invisible to
    /// <c>PdfDictionary</c>'s indexer.
    /// </summary>
    [Fact]
    public void AnIndirectInlineAttribute_IsRead()
    {
        var cell = CellA();

        Assert.Equal("Column", ((PdfName)cell.Attributes["Table"]["Scope"]).Value);
    }

    /// <summary>
    /// The class form. While <c>/C</c> went unread, a checker-clean table that declared its merges
    /// through a ClassMap had every span read as 1.
    /// </summary>
    [Fact]
    public void AClassMapAttribute_IsRead()
    {
        var cell = CellB();

        Assert.Equal(3, ((PdfNumber)cell.Attributes["Table"]["ColSpan"]).IntValue);
    }

    /// <summary>
    /// An <c>/A</c> array may interleave revision numbers and may carry attribute objects for
    /// several owners (§14.7.5.4). Both owners must survive, filed separately.
    /// </summary>
    [Fact]
    public void AnAttributeArray_KeepsEveryOwner()
    {
        var cell = CellB();

        Assert.Equal(2, ((PdfNumber)cell.Attributes["Table"]["RowSpan"]).IntValue);
        Assert.Equal("Block", ((PdfName)cell.Attributes["Layout"]["Placement"]).Value);
    }

    /// <summary>
    /// The inline attribute wins over a class one for the same owner and key — it is the more
    /// specific statement about that one element, and it is the order the lookup path applies.
    /// </summary>
    [Fact]
    public void AnInlineAttributeBeatsTheClassMap()
    {
        var cell = CellB();

        // The ClassMap gives ColSpan 3 and RowSpan 9; the inline array gives RowSpan 2.
        Assert.Equal(2, ((PdfNumber)cell.Attributes["Table"]["RowSpan"]).IntValue);
        Assert.Equal(3, ((PdfNumber)cell.Attributes["Table"]["ColSpan"]).IntValue);
    }

    /// <summary>
    /// An element declaring neither <c>/A</c> nor <c>/C</c> has an <b>empty</b> map, not null — the
    /// contract the property documents, and what stops every caller writing a null check.
    /// </summary>
    [Fact]
    public void AnElementWithNoAttributes_HasAnEmptyMapRatherThanNull()
    {
        var row = Row();

        Assert.NotNull(row.Attributes);
        Assert.Empty(row.Attributes);
    }

    /// <summary>The owner is not invented: <c>/Layout</c> attributes are not filed under Table.</summary>
    [Fact]
    public void OwnersAreKeptApart()
    {
        var cell = CellB();

        Assert.False(cell.Attributes["Table"].ContainsKey("Placement"));
        Assert.False(cell.Attributes["Layout"].ContainsKey("RowSpan"));
    }

    // ------------------------------------------------------------------

    private static PdfStructElement Root() =>
        PdfOperations.ReadStructureTree(new MemoryStream(TaggedTable()));

    private static PdfStructElement Row() => Descend(Root(), "TR");
    private static PdfStructElement CellA() => Row().Children.First(c => c.ElementType == "TD");
    private static PdfStructElement CellB() => Row().Children.Last(c => c.ElementType == "TD");

    private static PdfStructElement Descend(PdfStructElement from, string type)
    {
        if (from.ElementType == type) return from;
        foreach (var child in from.Children ?? System.Array.Empty<PdfStructElement>())
        {
            var found = Descend(child, type);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// A tagged one-page document: <c>/Table</c> → <c>/TR</c> → two <c>/TD</c>. Cell A takes the
    /// indirect-<c>/A</c> route; cell B takes both the <c>/C</c> route and an <c>/A</c> array with
    /// an interleaved revision number and two owners.
    /// </summary>
    private static byte[] TaggedTable() => Build(
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
        StreamObj(""),
        "<< /Type /StructTreeRoot /K [6 0 R] /ClassMap << /span3 11 0 R >> >>",
        "<< /Type /StructElem /S /Table /P 5 0 R /K [7 0 R] >>",
        "<< /Type /StructElem /S /TR /P 6 0 R /K [8 0 R 9 0 R] >>",
        "<< /Type /StructElem /S /TD /P 7 0 R /A 10 0 R >>",
        "<< /Type /StructElem /S /TD /P 7 0 R /C /span3"
            + " /A [ << /O /Layout /Placement /Block >> 0 << /O /Table /RowSpan 2 >> ] >>",
        "<< /O /Table /Scope /Column >>",
        "<< /O /Table /ColSpan 3 /RowSpan 9 >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

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
