// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// A node a name or number tree names that cannot be parsed is <see cref="PdfTreeTruncation.UnreadableNode"/>,
/// not an empty subtree. The parser answers both an undefined object and an unparseable one with
/// the null object; only <c>PdfParser.LastObjectParseError</c> tells them apart, and a walk reading
/// both as absent would call the tree whole — a lookup's "no such key" standing where the answer is
/// "the tree could not be read".
/// </summary>
public sealed class UnreadableTreeNodeTests
{
    [Fact]
    public void AKidThatCannotBeParsed_StopsTheWalk_AndNamesTheReason()
    {
        PdfTreeTruncation why = Walk("]", out bool complete, out int read);

        Assert.Equal(PdfTreeTruncation.UnreadableNode, why);
        Assert.False(complete);
        Assert.Equal(0, read);
    }

    /// <summary>The control: the null object is what the file holds (§7.3.9), an empty subtree, and the tree is whole.</summary>
    [Fact]
    public void AKidThatIsTheNullObject_IsAnEmptySubtree_AndTheTreeIsWhole()
    {
        PdfTreeTruncation why = Walk("null", out bool complete, out int read);

        Assert.Equal(PdfTreeTruncation.None, why);
        Assert.True(complete);
        Assert.Equal(0, read);
    }

    /// <summary>A readable kid before the unreadable one is still read; the result is partial, not empty.</summary>
    /// <remarks>The unreadable body is a stray array close. The parser tolerates a great deal — an
    /// unterminated dictionary reads on into the next object and comes back as a dictionary — and a
    /// tolerated object is read, not unreadable; a close with nothing open is refused.</remarks>
    [Fact]
    public void AReadableKidBeforeTheUnreadableOne_IsStillRead()
    {
        PdfTreeTruncation why = Walk("]", out bool complete, out int read, readableSiblingFirst: true);

        Assert.Equal(PdfTreeTruncation.UnreadableNode, why);
        Assert.False(complete);
        Assert.Equal(1, read);
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// Object 4 is the tree root, <c>/Kids [5 0 R]</c> (or <c>[6 0 R 5 0 R]</c> with a readable leaf
    /// first); object 5's body is <paramref name="kidBody"/>, as written.
    /// </summary>
    private static PdfTreeTruncation Walk(string kidBody, out bool complete, out int read, bool readableSiblingFirst = false)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            readableSiblingFirst ? "<< /Kids [6 0 R 5 0 R] >>" : "<< /Kids [5 0 R] >>",
            kidBody,
            "<< /Nums [7 (seven)] >>",
        };

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(Assemble(bodies)));
        var root = Assert.IsType<PdfDictionary>(doc.GetObject(4));
        var into = new List<KeyValuePair<PdfObject, PdfObject>>();
        int budget = 100;
        complete = PdfNumberTree.TryFlatten(root, doc, into, ref budget, 10, 10, out PdfTreeTruncation truncation);
        read = into.Count;
        return truncation;
    }

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
