// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>/PageLabels</c> is a NUMBER TREE (§12.4.2), so its entries may live in leaves below a root.
///
/// <para><b>Why this exists.</b> <c>PdfOperations.GetPageLabels</c> read
/// <c>catalog /PageLabels /Nums</c> directly and returned <c>null</c> when that key was absent —
/// but an intermediate node carries <c>/Kids</c> and no <c>/Nums</c> at all. A document whose
/// labels sit below a root node was reported as having <b>no page labels</b>, which is exactly what
/// a document with none returns. Meanwhile <c>Operations.Structure.PdfPageLabels</c>, the real
/// number-tree walker, read the same file correctly — two readers, one file, two answers.</para>
/// </summary>
public class PageLabelNumberTreeTests
{
    /// <summary>Labels stored under a <c>/Kids</c> node are found and formatted.</summary>
    [Fact]
    public void GetPageLabels_FindsLabelsStoredUnderKids()
    {
        byte[] pdf = ThreePagePdfWithLabelTree(twoLevel: true);

        var labels = PdfOperations.GetPageLabels(new MemoryStream(pdf));

        Assert.NotNull(labels);
        Assert.Equal(new[] { "i", "ii", "A-1" }, labels);
    }

    /// <summary>
    /// The single-level tree still works — the control, so the test above cannot pass by the reader
    /// having been swapped for one that only handles <c>/Kids</c>.
    /// </summary>
    [Fact]
    public void GetPageLabels_StillReadsAFlatNumsArray()
    {
        byte[] pdf = ThreePagePdfWithLabelTree(twoLevel: false);

        var labels = PdfOperations.GetPageLabels(new MemoryStream(pdf));

        Assert.NotNull(labels);
        Assert.Equal(new[] { "i", "ii", "A-1" }, labels);
    }

    /// <summary>A document with no <c>/PageLabels</c> still reports none.</summary>
    [Fact]
    public void GetPageLabels_WithoutAPageLabelTree_ReturnsNull()
    {
        byte[] pdf = ThreePagePdfWithLabelTree(twoLevel: false, omitLabels: true);

        Assert.Null(PdfOperations.GetPageLabels(new MemoryStream(pdf)));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Three pages: roman numerals from page 0, then prefix "A-" decimal from page 2. When
    /// <paramref name="twoLevel"/> the /Nums array is moved into a /Kids leaf, which is the shape
    /// a leaf-only reader cannot see.
    /// </summary>
    private static byte[] ThreePagePdfWithLabelTree(bool twoLevel, bool omitLabels = false)
    {
        const string nums = "/Nums [0 << /S /r >> 2 << /S /D /P (A-) >>]";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        string labelsEntry = omitLabels ? "" : " /PageLabels 8 0 R";

        Emit("%PDF-1.7\n");
        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R{labelsEntry} >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>\nendobj\n");
        Obj("5 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Length 3 >>\nstream\nq Q\nendstream\nendobj\n");
        // 7 is the leaf used only in the two-level shape; kept in both so object numbering matches.
        Obj($"7 0 obj\n<< {nums} >>\nendobj\n");
        Obj(twoLevel
            ? "8 0 obj\n<< /Kids [7 0 R] >>\nendobj\n"
            : $"8 0 obj\n<< {nums} >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
