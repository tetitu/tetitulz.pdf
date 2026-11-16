// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The PDF/UA-2 checks read an attribute object the elements share once a walk, not once an element:
/// elements × the shared object's entries is a product of two counts the file chooses. The object is
/// named by <c>/A</c>, or as a class by <c>/C</c> through the <c>/ClassMap</c>; two distinct objects
/// are read twice (the control), so the count is the memo's, not a constant.
/// </summary>
public sealed class Ua2SharedAttributeReadTests
{
    private static byte[] Pdf(int elements, bool distinct, bool byClass)
    {
        var kids = string.Join(" ", Enumerable.Range(0, elements).Select(i => byClass
            ? $"<< /Type /StructElem /S /P /P 4 0 R /A 5 0 R /C /{(distinct && i % 2 == 1 ? "Cls2" : "Cls")} >>"
            : $"<< /Type /StructElem /S /P /P 4 0 R /A {(distinct && i % 2 == 1 ? 6 : 5)} 0 R >>"));
        string keys = string.Concat(Enumerable.Range(0, 200).Select(i => $" /K{i} {i}"));
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /StructTreeRoot /K [" + kids + "] /ClassMap << /Cls 5 0 R /Cls2 6 0 R >> >>",
            "<< /O /Layout" + keys + " >>",
            "<< /O /Layout /Other 1 >>",
        };
        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static long ReadingsFor(byte[] pdf)
    {
        PdfUa2StructureChecker.Readings = 0;
        PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
        return PdfUa2StructureChecker.Readings;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdversarialSharedAttributeObject_IsReadOnceAWalk_NotOnceAnElement(bool byClass)
    {
        long shared = ReadingsFor(Pdf(2000, distinct: false, byClass));
        long two = ReadingsFor(Pdf(2000, distinct: true, byClass));

        Assert.InRange(shared, 1, 10);       // a handful of walks, each reading the one object once
        Assert.InRange(two, shared + 1, 2 * shared);   // two distinct objects: more readings, at most twice
    }
}
