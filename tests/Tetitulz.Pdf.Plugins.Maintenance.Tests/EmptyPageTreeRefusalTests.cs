// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The optimizer refuses a document whose page tree yields no page, rather than writing an empty
/// one and reporting the difference as a saving.
/// </summary>
/// <remarks>
/// <para><b>Found by measurement (GAPS.md §19h).</b> Over the another reader corpus, three files whose page
/// tree this reader could not follow — <c>operator_list_cycle</c> (the root's offset is 4294967295),
/// <c>GHOSTSCRIPT-698804-1-fuzzed</c> (a garbled subsection), <c>Brotli-Prototype-FileA</c> (an
/// unreadable object stream) — came out as documents with <c>/Pages null</c>. Being smaller than
/// their inputs, they passed <c>NeverGrow</c>; one went from 1.3 MB to 652 bytes. An independent checker reads a page
/// in the first two, and since the parser reads an object from its own header and refuses a
/// subsection start past <c>int</c> to the scan (§19h items 5b and 5c), so does this library:
/// both are optimized with their page. The third is refused.</para>
/// </remarks>
public sealed class EmptyPageTreeRefusalTests
{
    public static IEnumerable<object[]> Presets() => new[]
    {
        new object[] { "Default" },
        new object[] { "Lossless" },
    };

    /// <summary>
    /// The page tree's entry points past the end of the file and the tree is nowhere in it: the
    /// reader finds no page, and the optimizer refuses rather than writing an empty document and
    /// reporting the difference as a saving.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void APageTreeMissingFromTheFile_IsRefusedNotWrittenEmpty(string preset)
    {
        byte[] input = OnePageDocument(PageTree.Missing);
        using var sink = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOptimizer.Optimize(new MemoryStream(input), sink, Options(preset)));

        Assert.Contains("yields no page", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, sink.Length);
    }

    /// <summary>
    /// The page tree's entry points past the end of the file, but the tree is in it under its own
    /// <c>2 0 obj</c> header: the parser reads it from there (GAPS §19h, item 5b) and the page is
    /// written. Before that recovery this document was the refusal above — this test's earlier
    /// name, <c>APageTreeBehindAnOffsetOutsideTheFileIsRefusedNotWrittenEmpty</c>, said so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void APageTreeBehindAnOffsetOutsideTheFile_IsReadFromItsHeader_AndOptimized(string preset)
    {
        byte[] input = OnePageDocument(PageTree.OffsetOutsideTheFile);
        using var sink = new MemoryStream();

        PdfOptimizer.Optimize(new MemoryStream(input), sink, Options(preset));

        using var doc = PdfDocument.Open(new MemoryStream(sink.ToArray()));
        Assert.Single(doc.Pages);
    }

    /// <summary>The control: the same document with its offset right is optimized, page and all.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void TheSameDocumentWithItsOffsetRightIsOptimized(string preset)
    {
        byte[] input = OnePageDocument(PageTree.InPlace);
        using var sink = new MemoryStream();

        PdfOptimizer.Optimize(new MemoryStream(input), sink, Options(preset));

        using var doc = PdfDocument.Open(new MemoryStream(sink.ToArray()));
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// A well-formed document with no pages is refused as well: the guard cannot tell it from a
    /// page tree it failed to read, and refusing costs nothing — there is nothing to optimize.
    /// </summary>
    [Fact]
    public void AWellFormedEmptyPageTreeIsRefusedToo()
    {
        byte[] input = Document(
            new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [] /Count 0 >>",
            },
            brokenObject: 0);
        using var sink = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOptimizer.Optimize(new MemoryStream(input), sink, OptimizationOptions.Default));

        Assert.Contains("yields no page", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, sink.Length);
    }

    // ------------------------------------------------------------------

    private static OptimizationOptions Options(string preset) =>
        preset == "Lossless" ? OptimizationOptions.Lossless : OptimizationOptions.Default;

    /// <summary>Where the page tree (object 2) is: where its entry says, past the end of the file with its body still in it, or nowhere.</summary>
    private enum PageTree { InPlace, OffsetOutsideTheFile, Missing }

    private static byte[] OnePageDocument(PageTree pageTree) => Document(
        new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            "<< /Length 14 >>\nstream\n0 0 10 10 re f\nendstream",
        },
        brokenObject: pageTree == PageTree.InPlace ? 0 : 2,
        omitBrokenObject: pageTree == PageTree.Missing);

    /// <summary>
    /// Objects numbered from 1, with a classic cross-reference table in which
    /// <paramref name="brokenObject"/>'s entry (when non-zero) points past the end of the file —
    /// and, with <paramref name="omitBrokenObject"/>, that object is not written at all, so no
    /// header of its own is anywhere to recover it from.
    /// </summary>
    private static byte[] Document(string[] objects, int brokenObject, bool omitBrokenObject = false)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            if (omitBrokenObject && i + 1 == brokenObject) continue;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n")
              .Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objects.Length + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        for (int i = 0; i < offsets.Count; i++)
        {
            int offset = i + 1 == brokenObject ? 999_999 : offsets[i];
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        }
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture))
          .Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
