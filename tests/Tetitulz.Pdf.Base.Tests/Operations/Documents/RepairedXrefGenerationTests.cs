// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// A rebuilt cross-reference table must state the generation each object's own <c>N G obj</c> header
/// declares, because <see cref="PdfRepair"/> copies those headers through verbatim.
/// </summary>
/// <remarks>
/// <para><c>ScanForObjects</c> had to locate the generation digits in order to recognise a header at
/// all — and then discarded them, storing only the offset. <c>WriteXref</c> then wrote
/// <c>PdfXrefFormat.Entry(off)</c> with the default generation 0. So a source object written
/// <c>12 3 obj</c> came out of repair with an xref row claiming generation 0 against a header saying
/// 3.</para>
///
/// <para><b>That mismatch is not cosmetic.</b> §7.5.4 requires the two to agree, and
/// <c>PdfWriter</c>'s own doc comment records what strict readers do about it: treat the entry as
/// stale and silently drop the object. A repair that loses objects is worse than one that
/// refuses.</para>
/// </remarks>
public class RepairedXrefGenerationTests
{
    /// <summary>
    /// <b>The defect.</b> An object at generation 3 must get an xref row reading <c>00003</c>.
    /// </summary>
    [Fact]
    public void ARepairedObjectKeepsTheGenerationItsHeaderDeclares()
    {
        byte[] repaired = Repair(DamagedPdfWithGeneration(3));

        Assert.Equal(3, XrefGenerationFor(repaired, ObjectNumberWithGeneration));
    }

    /// <summary>
    /// The header and the xref must agree — asserted directly, since agreement is the actual §7.5.4
    /// requirement and the number above is only how it shows up here.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(65535)]
    public void TheRebuiltXrefRowAgreesWithTheCopiedObjectHeader(int generation)
    {
        byte[] repaired = Repair(DamagedPdfWithGeneration(generation));

        int inHeader = HeaderGenerationFor(repaired, ObjectNumberWithGeneration);
        int inXref = XrefGenerationFor(repaired, ObjectNumberWithGeneration);

        Assert.Equal(generation, inHeader);
        Assert.Equal(inHeader, inXref);
    }

    /// <summary>
    /// Every row stays exactly <see cref="PdfXrefFormat.EntryLength"/> bytes. A five-digit
    /// generation is the case that would overflow the slot if it were written unclamped, and it is
    /// the one input above that could break the table's fixed-width invariant rather than just its
    /// contents.
    /// </summary>
    [Fact]
    public void EveryRebuiltXrefRowIsExactlyTwentyBytes()
    {
        byte[] repaired = Repair(DamagedPdfWithGeneration(65535));

        string text = Encoding.Latin1.GetString(repaired);
        int xref = XrefTableStart(text);
        Assert.True(xref >= 0, "the repaired file has no classic xref table");

        int firstRow = text.IndexOf('\n', text.IndexOf('\n', xref) + 1) + 1;
        int trailer = text.IndexOf("trailer", firstRow, StringComparison.Ordinal);
        Assert.True(trailer > firstRow);

        int span = trailer - firstRow;
        Assert.Equal(0, span % PdfXrefFormat.EntryLength);
    }

    // ------------------------------------------------------------------

    private const int ObjectNumberWithGeneration = 4;

    private static byte[] Repair(byte[] damaged)
    {
        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();
        PdfRepair.Repair(input, output);
        byte[] result = output.ToArray();
        Assert.True(result.Length > 0, "repair produced nothing to assert about");
        return result;
    }

    /// <summary>The generation in the rebuilt xref row for <paramref name="objNum"/>.</summary>
    private static int XrefGenerationFor(byte[] pdf, int objNum)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int xref = XrefTableStart(text);
        Assert.True(xref >= 0, "the repaired file has no classic xref table");

        // "xref\n" then the "0 N\n" subsection header, then one fixed-width row per object.
        int firstRow = text.IndexOf('\n', text.IndexOf('\n', xref) + 1) + 1;
        int row = firstRow + objNum * PdfXrefFormat.EntryLength;
        Assert.True(row + PdfXrefFormat.EntryLength <= text.Length, "xref row is off the end");

        return int.Parse(text.Substring(row + 11, 5));
    }

    /// <summary>
    /// Start of the classic cross-reference <b>table</b>: an <c>xref</c> keyword at the beginning of
    /// a line.
    /// </summary>
    /// <remarks>
    /// Anchoring on the line start is not fussiness. The obvious
    /// <c>LastIndexOf("xref\n")</c> matches the <c>"xref\n"</c> inside <b><c>startxref\n</c></b>,
    /// which sits later in every file — so it found the trailer's offset line instead of the table,
    /// and every case in this class failed including the ones that were already correct. Worth
    /// recording: a locator that is wrong in the same way for every input looks exactly like a
    /// broken feature.
    /// </remarks>
    private static int XrefTableStart(string text)
    {
        int at = text.Length;
        while (true)
        {
            at = text.LastIndexOf("xref\n", at - 1, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (at == 0 || text[at - 1] == '\n') return at;
        }
    }

    /// <summary>The generation in the copied <c>N G obj</c> header for <paramref name="objNum"/>.</summary>
    private static int HeaderGenerationFor(byte[] pdf, int objNum)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf($"\n{objNum} ", StringComparison.Ordinal);
        Assert.True(at >= 0, $"object {objNum} is not in the repaired file");

        int genStart = at + 1 + objNum.ToString().Length + 1;
        int genEnd = text.IndexOf(' ', genStart);
        return int.Parse(text.Substring(genStart, genEnd - genStart));
    }

    /// <summary>
    /// A document with no usable xref — so repair must rebuild one — in which object 4 carries the
    /// requested generation.
    /// </summary>
    private static byte[] DamagedPdfWithGeneration(int generation)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 "
                  + generation + " R >>\nendobj\n");
        sb.Append($"{ObjectNumberWithGeneration} {generation} obj\n"
                  + "<< /Length 8 >>\nstream\nq 1 0 Q\nendstream\nendobj\n");
        // No xref, no startxref: the whole point is that the table must be rebuilt.
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
