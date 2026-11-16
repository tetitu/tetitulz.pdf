// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// A merge or a plan over input it could not read in full says so, or refuses. A merge whose second
/// source's page tree was unreadable returned the first source's pages with every warning null; an
/// unreadable object was left out without a word; a page held directly in <c>/Kids</c> was dropped;
/// and a plan over an encrypted or pageless file was empty, with nothing said.
/// </summary>
public sealed class MergePlanSilentPartialTests
{
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>";

    [Fact]
    public void ASourceWhosePageTreeCannotBeRead_IsSaidToContributeNoPages()
    {
        byte[] broken = Assemble("<< /Type /Catalog /Pages 2 0 R >>", "]", Page);

        int pages = MergedPageCount(OnePage(), broken);

        Assert.Equal(1, pages);
        string warning = PdfMerger.LastObjectWarning ?? "";
        Assert.Contains("could not be read", warning, StringComparison.Ordinal);
        Assert.Contains("Input(s) 1 contributed no pages", warning, StringComparison.Ordinal);
    }

    /// <summary>The control: two readable sources, every page carried, nothing said.</summary>
    [Fact]
    public void ReadableSources_AreMergedWithNothingSaid()
    {
        Assert.Equal(2, MergedPageCount(OnePage(), OnePage()));
        Assert.Null(PdfMerger.LastObjectWarning);
    }

    [Fact]
    public void APageHeldDirectlyInKids_IsMerged()
    {
        byte[] direct = Assemble("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] >>] /Count 1 >>");

        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(OnePage()), new MemoryStream(direct));
        using var merged = PdfDocument.Open(new MemoryStream(output.ToArray()));

        Assert.Equal(2, merged.Pages.Count);
        Assert.Equal(300, ((Tetitulz.Pdf.Core.Objects.PdfNumber)merged.Pages[1].MediaBox[3]).IntValue);
    }

    [Fact]
    public void APlanOverAPagelessDocument_IsRefused()
    {
        byte[] pageless = Assemble("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [] /Count 0 >>");

        var ex = Assert.Throws<PdfException>(() => PdfSplitPlanner.PlanEveryNPages(new MemoryStream(pageless), 1));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
        Assert.Throws<PdfException>(() => PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(pageless), 1_000_000));
    }

    [Fact]
    public void APlanOverAnEncryptedDocument_IsRefused()
    {
        byte[] encrypted = Assemble(new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", Page,
            "<< /Filter /Standard /V 1 /R 2 /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>" +
            " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> /P -4 >>" }, " /Encrypt 4 0 R");

        Assert.Throws<PdfEncryptedDocumentException>(() => PdfSplitPlanner.PlanEveryNPages(new MemoryStream(encrypted), 1));
    }

    /// <summary>The control: a readable document is planned.</summary>
    [Fact]
    public void APlanOverAReadableDocument_IsMade()
        => Assert.Single(PdfSplitPlanner.PlanEveryNPages(new MemoryStream(OnePage()), 1));

    // ----------------------------------------------------------------

    private static byte[] OnePage()
        => Assemble("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", Page);

    private static int MergedPageCount(byte[] a, byte[] b)
    {
        using var output = new MemoryStream();
        PdfMerger.Merge(output, new MemoryStream(a), new MemoryStream(b));
        using var merged = PdfDocument.Open(new MemoryStream(output.ToArray()));
        return merged.Pages.Count;
    }

    private static byte[] Assemble(params string[] bodies) => Assemble(bodies, "");

    private static byte[] Assemble(string[] bodies, string trailerExtras)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R").Append(trailerExtras)
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
