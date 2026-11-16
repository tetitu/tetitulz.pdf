// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A <c>%%EOF</c> only ends a revision when a <c>startxref</c> with a strictly positive,
/// backward-pointing offset precedes it — and that rule now has one implementation, reached by both
/// callers that need it.
/// </summary>
/// <remarks>
/// <para>Three places in the tree scan for <c>%%EOF</c>. <c>PdfRevisions</c> validates, because it
/// walks the whole file. <c>Plugins.EthereumSign.PdfBlockchainVerifier</c> does not, and
/// <b>documents why it is safe not to</b>: its input is a revision this library wrote itself,
/// holding one dictionary, an xref section and a trailer, with no stream of any kind.</para>
///
/// <para><b><c>PdfIncrementalSaveValidator</c> also did not validate, and had no such
/// justification.</b> Its scan starts at a cross-reference section's offset — and for a PDF 1.5+
/// file that section is an <b>xref stream</b>, whose Flate-compressed payload can contain the five
/// bytes <c>%%EOF</c>. The scan then stopped inside it, the first revision was measured too short,
/// and <c>SignaturesPreserved</c> could answer <c>true</c> for a file that had overwritten it. A
/// wrong answer to a security question, not a missing one.</para>
///
/// <para>These drive the shared rule directly with hand-laid buffers rather than through a crafted
/// xref stream: what is under test is which <c>%%EOF</c> counts, and a fixture that has to defeat a
/// Flate encoder to pose the question would hide that.</para>
/// </remarks>
public class ValidatedEofScanTests
{
    /// <summary>
    /// A bare <c>%%EOF</c> with no <c>startxref</c> before it — the shape that occurs inside stream
    /// data — is skipped, and the real terminator after it is found instead.
    /// </summary>
    [Fact]
    public void ABareEofInsideTheFileIsSkippedForTheRealTerminator()
    {
        // A decoy at offset 10, then a genuine trailer.
        string text = "%PDF-1.7\n\n%%EOF\n<< junk >>\nstartxref\n9\n%%EOF\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        long end = PdfRevisions.FindValidatedEofAfter(bytes, 0);

        int decoy = text.IndexOf("%%EOF", System.StringComparison.Ordinal);
        int real = text.LastIndexOf("%%EOF", System.StringComparison.Ordinal);
        Assert.NotEqual(decoy + 5, end);          // not the decoy
        Assert.Equal(real + 5, end);              // the one a startxref vouches for
    }

    /// <summary>
    /// The validated terminator is found, and the returned offset is just past the marker — the
    /// end-of-line is deliberately not swallowed, because this answers "where did the terminator
    /// finish" rather than "where does an extracted revision end".
    /// </summary>
    [Fact]
    public void TheReturnedOffsetIsJustPastTheMarker()
    {
        string text = "%PDF-1.7\nstartxref\n9\n%%EOF\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        long end = PdfRevisions.FindValidatedEofAfter(bytes, 0);

        Assert.Equal(text.IndexOf("%%EOF", System.StringComparison.Ordinal) + 5, end);
        Assert.Equal((byte)'\n', bytes[end]);     // the EOL is still ahead of the returned offset
    }

    /// <summary>
    /// <c>startxref 0</c> does not vouch for a terminator. Offset 0 addresses the <c>%PDF</c>
    /// header, which is never a cross-reference section — and a linearized file carries exactly
    /// that near its front, so accepting it would split one revision into two.
    /// </summary>
    [Fact]
    public void AZeroStartxrefDoesNotVouchForATerminator()
    {
        string text = "%PDF-1.7\nstartxref\n0\n%%EOF\nbody\nstartxref\n9\n%%EOF\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        long end = PdfRevisions.FindValidatedEofAfter(bytes, 0);

        Assert.Equal(text.LastIndexOf("%%EOF", System.StringComparison.Ordinal) + 5, end);
    }

    /// <summary>
    /// A forward-pointing <c>startxref</c> does not vouch either — a cross-reference section always
    /// precedes the terminator that names it.
    /// </summary>
    [Fact]
    public void AForwardPointingStartxrefDoesNotVouchForATerminator()
    {
        string text = "%PDF-1.7\nstartxref\n9999\n%%EOF\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        Assert.Equal(-1, PdfRevisions.FindValidatedEofAfter(bytes, 0));
    }

    /// <summary>
    /// Nothing at all is <c>-1</c>, not 0 — the callers distinguish "no terminator" from "one at
    /// the start of the file", and <c>PdfIncrementalSaveValidator</c> turns the former into the
    /// file length so it refuses to claim preservation rather than silently claiming it.
    /// </summary>
    [Fact]
    public void NoTerminatorAtAllIsMinusOne()
    {
        Assert.Equal(-1, PdfRevisions.FindValidatedEofAfter(Encoding.ASCII.GetBytes("%PDF-1.7\nbody\n"), 0));
        Assert.Equal(-1, PdfRevisions.FindValidatedEofAfter(null, 0));
    }

    /// <summary>
    /// The scan starts where it is told: a validated terminator before <c>from</c> is not returned.
    /// Without this the caller asking "where does revision <i>n</i> end" would be handed revision
    /// 1's answer.
    /// </summary>
    [Fact]
    public void TheScanHonoursItsStartOffset()
    {
        string text = "%PDF-1.7\nstartxref\n9\n%%EOF\nmore\nstartxref\n30\n%%EOF\n";
        byte[] bytes = Encoding.ASCII.GetBytes(text);

        int first = text.IndexOf("%%EOF", System.StringComparison.Ordinal);
        long second = PdfRevisions.FindValidatedEofAfter(bytes, first + 5);

        Assert.Equal(text.LastIndexOf("%%EOF", System.StringComparison.Ordinal) + 5, second);
    }
}
