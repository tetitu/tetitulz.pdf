// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Elements that name the same attribute objects share one reading of them. Each copied every entry
/// into a map of its own and kept it: elements × the entries of a shared attribute object, a product
/// of two counts the file chooses.
/// </summary>
public sealed class StructureAttributeSharingTests
{
    [Theory]
    [InlineData("8 0 R")]     // /A names the object
    [InlineData("[8 0 R]")]   // each element's own array names it
    public void AdversarialElementsNamingOneAttributeObject_ShareOneReading(string a)
    {
        const int elements = 2000, keys = 500;
        var root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Tree(elements, keys, a)));

        var all = root.Children.Single().Children;
        Assert.Equal(elements, all.Count);
        var first = all[0].Attributes;
        Assert.All(all, e => Assert.Same(first, e.Attributes));

        var layout = first["Layout"];
        Assert.Equal(keys, layout.Count);
        Assert.Equal(7, ((PdfNumber)layout["K7"]).IntValue);
    }

    /// <summary>The control: elements naming different attribute objects keep their own.</summary>
    [Fact]
    public void ElementsNamingDifferentAttributeObjects_KeepTheirOwn()
    {
        var root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(Tree(2, 3, "8 0 R", secondA: "9 0 R")));

        var all = root.Children.Single().Children;
        Assert.NotSame(all[0].Attributes, all[1].Attributes);
        Assert.Equal(2, ((PdfNumber)all[0].Attributes["Layout"]["K2"]).IntValue);
        Assert.Equal(20, ((PdfNumber)all[1].Attributes["Layout"]["K2"]).IntValue);
    }

    // A /Document (object 7) of `elements` paragraphs from object 10 on, each /A `a` — the first
    // `secondA` when given instead for the second; object 8 the attribute object of `keys` /Kn n,
    // object 9 one of /Kn 10n.
    private static byte[] Tree(int elements, int keys, string a, string secondA = null)
    {
        var attrs8 = new StringBuilder("<< /O /Layout");
        var attrs9 = new StringBuilder("<< /O /Layout");
        for (int k = 0; k < keys; k++)
        {
            attrs8.Append(" /K").Append(k).Append(' ').Append(k);
            attrs9.Append(" /K").Append(k).Append(' ').Append(10 * k);
        }
        attrs8.Append(" >>");
        attrs9.Append(" >>");

        var kids = string.Join(" ", Enumerable.Range(0, elements).Select(i => (10 + i).ToString(CultureInfo.InvariantCulture) + " 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /StructTreeRoot /K 7 0 R >>",
            "null",
            "null",
            "<< /Type /StructElem /S /Document /P 4 0 R /K [" + kids + "] >>",
            attrs8.ToString(),
            attrs9.ToString(),
        };
        for (int i = 0; i < elements; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 7 0 R /A " + (i == 1 && secondA != null ? secondA : a) + " >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
