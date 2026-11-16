// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// What counts as the <c>startxref &lt;integer&gt; %%EOF</c> that ends a revision — the shape of the
/// terminator itself, rather than the Annex F structure around it that
/// <see cref="LinearizedRevisionTests"/> covers.
/// </summary>
/// <remarks>
/// <para><b>Why the shape is a security question and not a parsing nicety.</b> A revision boundary is
/// found in the bytes, so the validation in <c>StartXrefOffsetBefore</c> is the only thing standing
/// between the five bytes <c>%%EOF</c> occurring inside a compressed stream and a phantom revision
/// whose <c>PdfRevisions.Extract</c> hands back a truncated file — and whose existence shifts every
/// signature attribution one revision along.</para>
///
/// <para>Each fixture appends a second terminator to a well-formed document, so the assertion is
/// <i>how many revisions the file has</i> rather than whether the call returned a list. A scanner
/// that accepted everything, and one that accepted nothing, both return a list.
/// <see cref="AWellFormedAppendedTerminatorIsASecondRevision"/> is the acceptance control: without
/// it, every refusal below would also pass on a scanner that had stopped counting entirely.</para>
/// </remarks>
public class StartXrefTerminatorShapeTests
{
    /// <summary>
    /// The control. The same fixture with a well-formed offset <b>is</b> two revisions, so the
    /// refusals below are refusing the terminator's shape and not the fixture.
    /// </summary>
    [Fact]
    public void AWellFormedAppendedTerminatorIsASecondRevision()
    {
        byte[] pdf = WithAppendedTerminator(offsetText: "9");

        var revisions = Revisions(pdf);

        Assert.Equal(2, revisions.Count);
        Assert.Equal(pdf.Length, revisions[1].Length);
    }

    /// <summary>
    /// <b>Adversarial: an offset written with enough digits to overflow the accumulator.</b>
    /// <c>18446744073709551716</c> is 2<sup>64</sup> + 100, so accumulating it digit by digit in an
    /// unchecked <c>long</c> wraps to exactly <b>100</b> — a small, strictly positive,
    /// backward-pointing offset that passes every later test and mints a revision. The accumulator
    /// stops at the file length for precisely this reason, and the saturated value is then rejected
    /// as pointing past the marker.
    /// </summary>
    [Fact]
    public void AnOffsetWhoseDigitsOverflowTheAccumulatorDoesNotEndARevision()
    {
        byte[] pdf = WithAppendedTerminator(offsetText: "18446744073709551716");

        var revisions = Revisions(pdf);

        // The wrapped value 100 points backwards into this fixture and is smaller than the marker's
        // position, so nothing downstream would have caught it.
        Assert.True(100 < pdf.Length, "the fixture must be long enough for the wrapped value to look valid");
        var only = Assert.Single(revisions);
        Assert.Equal(FirstRevisionLength(pdf), only.Length);
    }

    /// <summary>
    /// <b>Adversarial: a byte that is neither a digit nor whitespace between the keyword and the
    /// marker.</b> This is the guard that keeps a <c>%%EOF</c> inside stream data from inventing a
    /// revision when the bytes before it happen to contain <c>startxref</c> and a number: a trailer
    /// is a trailer only if nothing else lies between them.
    ///
    /// <para>The stray byte follows the digits <b>directly</b>, with no space. Whitespace after a
    /// digit ends the number (see <see cref="AMalformedSecondIntegerDoesNotExtendTheFirst"/>), so
    /// <c>9 X</c> would break out of the scan before the <c>X</c> was ever examined and would
    /// exercise nothing.</para>
    /// </summary>
    [Fact]
    public void AStrayByteBetweenTheKeywordAndTheMarkerIsNotATrailer()
    {
        byte[] pdf = WithAppendedTerminator(offsetText: "9X");

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(FirstRevisionLength(pdf), only.Length);
    }

    /// <summary>
    /// <b>Characterisation, not a spec rule.</b> ISO 32000-2 §7.5.5 gives a trailer one integer, so a
    /// second one is malformed and the standard says nothing about it. The reader takes the
    /// <i>first</i> — whitespace after a digit ends the number — and this pins that choice so a
    /// change to it is loud. Without the rule the two runs concatenate into <c>9123456789</c>, which
    /// points past the file and silently drops the revision.
    /// </summary>
    [Fact]
    public void AMalformedSecondIntegerDoesNotExtendTheFirst()
    {
        byte[] pdf = WithAppendedTerminator(offsetText: "9 123456789");

        var revisions = Revisions(pdf);

        Assert.Equal(2, revisions.Count);
        Assert.Equal(pdf.Length, revisions[1].Length);
    }

    // ------------------------------------------------------------------ helpers

    private static System.Collections.Generic.IReadOnlyList<PdfRevisionInfo> Revisions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfRevisions.List(ms);
    }

    /// <summary>Bytes through the first revision's <c>%%EOF</c> and the end-of-line after it.</summary>
    private static int FirstRevisionLength(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        return text.IndexOf("%%EOF", System.StringComparison.Ordinal) + "%%EOF\n".Length;
    }

    /// <summary>
    /// A well-formed single-revision document, with a second object and a second terminator appended
    /// whose offset text is <paramref name="offsetText"/> — the one thing under test.
    /// </summary>
    private static byte[] WithAppendedTerminator(string offsetText)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        // The appended revision. Its own terminator carries the shape under test.
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>\nendobj\n");
        sb.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 4 /Prev ")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append(" >>\nstartxref\n")
          .Append(offsetText).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
