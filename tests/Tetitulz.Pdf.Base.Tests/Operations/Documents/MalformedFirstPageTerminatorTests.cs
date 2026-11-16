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
/// Annex F.3.4's first-page terminator is <c>startxref</c>, <b>an integer</b>, and <c>%%EOF</c>. A
/// <c>startxref</c> with no integer is not that form, and recognising it as one costs a revision.
/// </summary>
/// <remarks>
/// <para><b>Why the no-digit refusal is not redundant, which is how it first read.</b> The refusal
/// returns <c>-1</c>, and for ordinary revision counting that is indistinguishable from returning
/// <c>0</c>: <c>HasValidStartXrefBefore</c> requires a strictly positive offset, so a digitless
/// trailer is never a boundary either way. The difference appears in exactly one place —
/// <c>LinearizedFirstPageTerminator</c> tests <c>&lt; 0</c>, so <c>0</c> reads as <i>found</i> and
/// the search <b>stops at the malformed terminator</b> instead of walking on to the real one.</para>
///
/// <para>The real one then still carries a valid <c>startxref</c>, so it is counted as a revision
/// boundary rather than ignored — and the file reports <b>two</b> revisions where it has one, with
/// <c>Extract(0)</c> handing back a document truncated after part 3. That is precisely the failure
/// <c>LinearizedFirstPageTerminator</c> exists to prevent, arriving by a different door.</para>
///
/// <para>A damaged or truncated producer output is where this shape comes from; the fixture is
/// adversarial rather than conformant, which is the point.</para>
/// </remarks>
public class MalformedFirstPageTerminatorTests
{
    /// <summary>
    /// Part 3 ends with a digitless <c>startxref … %%EOF</c> and then the real terminator. The
    /// file is one revision, and the malformed pair must not be mistaken for Annex F's.
    /// </summary>
    [Fact]
    public void ADigitlessStartxrefInPartThreeIsNotTheFirstPageTerminator()
    {
        byte[] pdf = LinearizedWithMalformedTerminator();

        using var ms = new MemoryStream(pdf);
        var revisions = PdfRevisions.List(ms);

        var only = Assert.Single(revisions);
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>
    /// The acceptance control: the same fixture without the malformed pair is also one revision, so
    /// the test above is pinning the refusal and not a file that simply has one terminator.
    /// </summary>
    [Fact]
    public void TheSameFileWithoutTheMalformedPairIsAlsoOneRevision()
    {
        byte[] pdf = LinearizedWithMalformedTerminator(includeMalformed: false);

        using var ms = new MemoryStream(pdf);
        var only = Assert.Single(PdfRevisions.List(ms));
        Assert.Equal(pdf.Length, only.Length);
    }

    // ------------------------------------------------------------------ fixture

    private static byte[] LinearizedWithMalformedTerminator(bool includeMalformed = true)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n%âãÏÓ\n");

        // Part 2: the linearization parameter dictionary, first object, inside the 1024-byte window.
        sb.Append("1 0 obj\n<< /Linearized 1 /L 0 /H [0 0] /O 3 /E 0 /N 1 /T 0 >>\nendobj\n");

        int part3 = sb.Length;
        sb.Append("xref\n1 3\n");
        for (int i = 0; i < 3; i++) sb.Append("0000000000 00000 n \n");
        sb.Append("trailer\n<< /Size 5 /Root 2 0 R /Prev 0 >>\n");

        // The malformed pair: `startxref`, no integer at all, then the marker.
        if (includeMalformed) sb.Append("startxref\n\n%%EOF\n");

        // ...and the real first-page terminator, which Annex F.3.4 says to ignore.
        sb.Append("startxref\n").Append(part3.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        sb.Append("2 0 obj\n<< /Type /Catalog /Pages 4 0 R >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 4 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        sb.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 5 >>\nstartxref\n")
          .Append(part3.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
