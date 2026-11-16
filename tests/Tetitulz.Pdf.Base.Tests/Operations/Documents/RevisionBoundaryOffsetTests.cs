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
/// A <c>%%EOF</c> ends a revision only when the <c>startxref</c> before it names an offset that is
/// <b>strictly positive</b> and points <b>strictly backwards</b>.
/// </summary>
/// <remarks>
/// <para><b>Half of that rule had no coverage.</b> A 2026-08-29 liveness sweep found the
/// <c>offset &gt; 0</c> half defended — two tests redden when it is relaxed — and the
/// <c>offset &lt; eofPos</c> half defended by nothing at all: dropping it left the whole Base suite
/// green.</para>
///
/// <para><b>Why the backward half matters as much as the positive one.</b> Both exist to stop a
/// <c>startxref</c> that cannot name a real earlier section from counting as a revision boundary,
/// and <c>PdfRevisions</c> is what makes this library's central signing caveat checkable — an
/// incremental update cannot destroy the old bytes, they stay in the revision the first signature's
/// digest covers, and this is the tool that hands them back. A boundary in the wrong place means
/// <c>Extract</c> writes a prefix that satisfies no signature while still producing a file that
/// opens.</para>
///
/// <para>Asserted on the <b>count</b> of revisions, never on the call succeeding: a scanner that
/// accepted every <c>%%EOF</c> would still return a list, and every extraction would still produce
/// bytes.</para>
/// </remarks>
public class RevisionBoundaryOffsetTests
{
    /// <summary>The control: an ordinary two-revision file is seen as two.</summary>
    [Fact]
    public void AWellFormedIncrementalUpdateIsTwoRevisions()
        => Assert.Equal(2, Revisions(TwoRevisionPdf()));

    /// <summary>
    /// <c>startxref 0</c> — what a <b>linearized</b> file writes near the front. Offset 0 addresses
    /// the <c>%PDF</c> header, which is never a cross-reference section, so it is one revision and
    /// not two. This is the half that was already defended.
    /// </summary>
    [Fact]
    public void AZeroFirstOffsetDoesNotEndARevision()
        => Assert.Equal(1, Revisions(TwoRevisionPdf(firstOffset: 0)));

    /// <summary>
    /// <b>The half that nothing defended.</b> The first revision's <c>startxref</c> points forward —
    /// at the <i>second</i> revision's cross-reference section, which does not exist yet at that
    /// point in the file — so that <c>%%EOF</c> does not end a revision either.
    /// </summary>
    /// <remarks>
    /// <b>The offset has to be inside the file, and a wrong fixture taught me that.</b> The obvious
    /// version overrode the <i>final</i> <c>startxref</c> with a large number and passed under the
    /// mutation — an <c>offset &gt; bytes.Length</c> guard earlier in the same loop rejects that
    /// before the backward test is ever consulted. Only a value beyond <i>this</i> <c>%%EOF</c> yet
    /// still within the file separates the two rules.
    /// </remarks>
    [Fact]
    public void AForwardPointingFirstOffsetDoesNotEndARevision()
        => Assert.Equal(1, Revisions(TwoRevisionPdf(firstOffset: SecondXrefOffset())));

    /// <summary>How many revisions the scanner finds in these bytes.</summary>
    private static int Revisions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfRevisions.List(ms).Count;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The offset of the second revision's cross-reference section in the unmodified file — a
    /// position that is forward of the first <c>%%EOF</c> and still inside the document.
    /// </summary>
    private static int SecondXrefOffset()
    {
        Build(null, out int secondXref);
        return secondXref;
    }

    /// <summary>
    /// A two-revision file. <paramref name="firstOffset"/> overrides the first revision's
    /// <c>startxref</c>; passing <c>null</c> writes the real one, which is what makes the control a
    /// control.
    /// </summary>
    private static byte[] TwoRevisionPdf(int? firstOffset = null) => Build(firstOffset, out _);

    private static byte[] Build(int? firstOffset, out int secondXref)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length; sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length; sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length; sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int xref1 = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3 }) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append((firstOffset ?? xref1).ToString(CultureInfo.InvariantCulture))
          .Append("\n%%EOF\n");

        int o3b = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] >>\nendobj\n");

        secondXref = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n3 1\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o3b);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R /Prev ").Append(xref1).Append(" >>\n");
        sb.Append("startxref\n").Append(secondXref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
