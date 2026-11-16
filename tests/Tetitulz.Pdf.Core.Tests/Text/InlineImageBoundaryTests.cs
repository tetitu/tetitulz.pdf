// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Where <see cref="ContentStreamParser"/> ends an inline image, at the edges the overnight sweep
/// found no test reaching: a declared <c>/L</c>, the heuristic's four-byte look-ahead, and an image
/// with no <c>EI</c> at all.
/// </summary>
public class InlineImageBoundaryTests
{
    // ---------------------------------------------------------------- a declared /L

    /// <summary>
    /// A declared <c>/L</c> is taken first. CCITT is the filter that shows it: it carries no
    /// end marker and its encoded size does not follow from the geometry, so without <c>/L</c> the
    /// parser falls back to the heuristic, and this payload holds a decoy <c>EI</c> the heuristic
    /// would stop at.
    /// </summary>
    [Fact]
    public void ADeclaredLengthIsTakenFirst()
    {
        byte[] payload = Ascii("x EI y");
        var (image, ops) = Parse("BI /W 8 /H 1 /F /CCF /L 6 ID ", payload, " EI Q\n");

        Assert.Equal(payload, image.RawData);
        Assert.Equal(PdfOperators.Restore, ops.Last().Operator);
    }

    /// <summary>
    /// A declared length the data contradicts — no <c>EI</c> where it says the payload ends — is
    /// set aside, and the image is measured another way. Believing it would end the image early and
    /// read the rest of the samples as operators. The bytes where it says the payload ends are two
    /// letters and a space, so only the <c>EI</c> comparison can tell them from a terminator.
    /// </summary>
    [Fact]
    public void ADeclaredLengthNotFollowedByEiIsSetAside()
    {
        byte[] payload = Ascii("abcde fgh");
        var (image, ops) = Parse("BI /W 8 /H 1 /F /CCF /L 3 ID ", payload, " EI Q\n");

        Assert.Equal(payload, image.RawData);
        Assert.Equal(2, ops.Count);
    }

    /// <summary>
    /// <c>/L 0</c> is a length, not an unknown: the image is empty and the operator after it is
    /// read as one.
    /// </summary>
    [Fact]
    public void ADeclaredLengthOfZeroIsAnEmptyImage()
    {
        var (image, ops) = Parse("BI /W 8 /H 1 /F /CCF /L 0 ID ", new byte[0], "EI Q\n");

        Assert.Empty(image.RawData);
        Assert.Equal(PdfOperators.Restore, ops.Last().Operator);
    }

    // ---------------------------------------------------------------- the heuristic's look-ahead

    /// <summary>
    /// A high byte in <b>any</b> of the four bytes after a candidate <c>EI</c> marks it as sample
    /// data, not the operator, so the image goes on to the real one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AHighByteAnywhereInTheFourAfterEiKeepsTheImageGoing(int offset)
    {
        var after = new byte[] { (byte)'Z', (byte)'Z', (byte)'Z', (byte)'Z' };
        after[offset - 1] = 0xFF;
        byte[] payload = Concat(Ascii("AB EI"), after, Ascii("CD"));

        var (image, _) = Parse(FlateHeader, payload, " EI Q\n");

        Assert.Equal(payload, image.RawData);
    }

    /// <summary>
    /// 0x7F is ASCII, not a high byte: an <c>EI</c> followed by it is the operator. The test
    /// mirrors another reader, whose look-ahead tests <c>&gt; 0x7F</c>.
    /// </summary>
    [Fact]
    public void DelAfterEiIsTextSoTheImageEndsThere()
    {
        byte[] payload = Concat(Ascii("AB EI"), new byte[] { 0x7F }, Ascii("CD"));

        var (image, _) = Parse(FlateHeader, payload, " EI Q\n");

        Assert.Equal(Ascii("AB"), image.RawData);
    }

    // ---------------------------------------------------------------- no EI at all

    /// <summary>
    /// An image with no <c>EI</c> runs to the end of the stream. It keeps the documented
    /// one-byte-short length its byte-at-a-time predecessor produced, and leaves the parser at the
    /// end of the stream, so the stream's last byte is not read as an operator.
    /// </summary>
    [Fact]
    public void AnUnterminatedImageEndsOneShortAndConsumesTheStream()
    {
        var (image, ops) = Parse(FlateHeader, Ascii("ABCDQ"), "");

        Assert.Equal(Ascii("ABCD"), image.RawData);
        Assert.Single(ops);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary><c>/F /Fl</c> with no <c>/L</c>, and a payload that is not Flate: the heuristic decides.</summary>
    private const string FlateHeader = "BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID ";

    private static (PdfStream Image, List<ContentStreamOperation> Ops) Parse(
        string head, byte[] payload, string tail)
    {
        var ops = ContentStreamParser.Parse(Concat(Ascii(head), payload, Ascii(tail))).ToList();
        var image = ops.Single(o => o.Operator == PdfOperators.BeginInlineImage);
        return (Assert.IsType<PdfStream>(image.Operands[0]), ops);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
        return ms.ToArray();
    }
}
