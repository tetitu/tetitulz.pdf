// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// ISO 32000-2 §7.3.4.3: "If the final digit of a hexadecimal string is missing — that is, if there
/// is an odd number of digits — the final digit shall be assumed to be 0."
/// </summary>
/// <remarks>
/// <para><b>This rule was asserted by nothing.</b> The document parser and the content-stream parser
/// each carried a byte-identical copy of it, and merging them onto
/// <c>PdfConstants.HexStringToBytes</c> turned "which fixtures reach this line?" into one question
/// with one answer: deleting the padding step left the whole 2 988-test Core suite green. Every hex
/// string in the tree happens to have an even digit count, so the last byte of an odd one was free
/// to vanish.</para>
///
/// <para><b>The failure it prevents is silent and truncating.</b> Without the rule
/// <c>&lt;901FA&gt;</c> decodes to two bytes instead of three — the trailing nibble is simply
/// dropped, so a string, a signature <c>/Contents</c> or an <c>/ID</c> comes back one byte short
/// with no error anywhere.</para>
///
/// <para>Asserted at all three arrivals: the shared helper, a hex string in a document body, and a
/// hex string as a content-stream operand — because the two parsers are what the rule was
/// duplicated across.</para>
/// </remarks>
public sealed class HexStringOddDigitTests
{
    [Theory]
    [InlineData("901FA", new byte[] { 0x90, 0x1F, 0xA0 })]
    [InlineData("A", new byte[] { 0xA0 })]
    [InlineData("ABC", new byte[] { 0xAB, 0xC0 })]
    public void AnOddDigitCount_CompletesWithATrailingZero(string hex, byte[] expected)
        => Assert.Equal(expected, PdfConstants.HexStringToBytes(hex));

    /// <summary>The even-count control: no padding happens where none is owed.</summary>
    [Theory]
    [InlineData("901FA0", new byte[] { 0x90, 0x1F, 0xA0 })]
    [InlineData("", new byte[0])]
    public void AnEvenDigitCount_IsTakenAsWritten(string hex, byte[] expected)
        => Assert.Equal(expected, PdfConstants.HexStringToBytes(hex));

    /// <summary>
    /// The same rule through the <b>document</b> parser, which is one of the two places it was
    /// written out.
    /// </summary>
    [Fact]
    public void AnOddDigitHexString_InADocument_KeepsItsLastByte()
    {
        using var doc = PdfDocument.Open(new MemoryStream(PdfWithHexString("901FA")));

        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        var title = doc.ResolveObject(info[PdfName.Title]) as PdfString;

        Assert.NotNull(title);
        Assert.Equal(new byte[] { 0x90, 0x1F, 0xA0 }, title.RawBytes);
    }

    /// <summary>
    /// And through the <b>content-stream</b> parser, which is the other. A <c>Tj</c> operand is the
    /// ordinary way a hex string reaches it.
    /// </summary>
    [Fact]
    public void AnOddDigitHexString_AsAContentStreamOperand_KeepsItsLastByte()
    {
        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes("BT <901FA> Tj ET"));

        PdfString operand = ops
            .SelectMany(op => op.Operands)
            .OfType<PdfString>()
            .Single();

        Assert.Equal(new byte[] { 0x90, 0x1F, 0xA0 }, operand.RawBytes);
    }

    // ------------------------------------------------------------------

    /// <summary>A one-page document whose <c>/Info /Title</c> is the given hex string.</summary>
    private static byte[] PdfWithHexString(string hex)
    {
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            $"<< /Title <{hex}> >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R /Info 4 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
