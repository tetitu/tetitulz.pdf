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
/// Annex F.3.4 (ISO 32000-2, lines 81948–81949 of <c>docs/iso/iso32000-2.txt</c>): a linearized
/// file's first-page trailer "may optionally end with startxref, an integer, and %%EOF, just as in
/// an ordinary trailer. This information shall be ignored." The integer is not constrained to zero,
/// and a linearized file is one revision whatever it says.
/// </summary>
/// <remarks>
/// <para><b><c>PdfRevisions</c> rejected only <c>startxref 0</c></b> — the conventional filler,
/// which the annex's own Example 3 (81700–81703) and this library's linearizer write — so a
/// first-page trailer whose integer named a real earlier position split the file into a phantom
/// revision 0 ending after part 3 and a "revision 1" that is the whole file. <c>Extract(0)</c> then
/// handed back a truncated document, and a signature covering the file was attributed to the wrong
/// revision.</para>
///
/// <para>The recogniser reads the structure the annex defines: the first object is the linearization
/// parameter dictionary, within the first 1024 bytes (F.3.3, 81846–81855), and part 3 — the
/// first-page cross-reference table and trailer — follows it directly (F.3.4, 81936–81946). The
/// <c>%%EOF</c> that closes part 3 is the first one after that object with nothing but the
/// cross-reference section between them; that is the terminator the annex says to ignore.</para>
///
/// <para>Asserted on the <b>count</b> and <b>length</b> of revisions, never on the call succeeding:
/// a scanner that accepted every <c>%%EOF</c> would still return a list.</para>
/// </remarks>
public class LinearizedRevisionTests
{
    /// <summary><b>The defect.</b> A non-zero first-page integer, pointing backwards at a real position.</summary>
    [Fact]
    public void AFirstPageTrailerWithANonzeroStartxrefIsNotARevision()
    {
        byte[] pdf = Linearized(firstPageStartxref: FirstPageXrefOffset());

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>The conventional filler, which was already handled, still is.</summary>
    [Fact]
    public void AFirstPageTrailerWithStartxrefZeroIsNotARevision()
    {
        byte[] pdf = Linearized(firstPageStartxref: 0);

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>
    /// The terminator is optional. Without it the first <c>%%EOF</c> in the file is the main
    /// trailer's, and that one must still count — skipping "the first <c>%%EOF</c>" blindly would
    /// leave a linearized file with no revision at all.
    /// </summary>
    [Fact]
    public void AFirstPageTrailerWithoutATerminatorLeavesTheMainTrailerAsTheOneRevision()
    {
        byte[] pdf = Linearized(firstPageStartxref: null);

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>A cross-reference stream (PDF 1.5+) in part 3 is recognised as the section too.</summary>
    [Fact]
    public void ACrossReferenceStreamFirstPageSectionIsNotARevisionEither()
    {
        byte[] pdf = Linearized(firstPageStartxref: FirstPageXrefOffset(xrefStream: true), xrefStream: true);

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>
    /// An incremental update appended to a linearized file is a real second revision, and revision 0
    /// is the whole linearized original — not the fragment ending after part 3.
    /// </summary>
    [Fact]
    public void AnUpdateAppendedToALinearizedFileIsASecondRevision()
    {
        byte[] original = Linearized(firstPageStartxref: FirstPageXrefOffset());
        byte[] updated = AppendUpdate(original);

        var revisions = Revisions(updated);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(original.Length, revisions[0].Length);
        Assert.Equal(updated.Length, revisions[1].Length);
    }

    /// <summary>
    /// What lies between the parameter dictionary and the terminator must be a cross-reference
    /// section. One ordinary object there means the terminator is not part 3's, and it counts —
    /// the same file is two revisions, as it looks.
    /// </summary>
    [Fact]
    public void ATerminatorPrecededByAnOrdinaryObjectIsNotTakenForTheFirstPageOne()
    {
        byte[] pdf = Linearized(
            firstPageStartxref: FirstPageXrefOffset(strayObject: true), strayObject: true);

        Assert.Equal(2, Revisions(pdf).Count);
    }

    /// <summary>
    /// <b><c>/XRefStm</c> is not <c>/XRef</c>.</b> The single object allowed between part 2 and the
    /// terminator has to <i>be</i> a cross-reference stream; §7.5.8.4's hybrid-reference entry merely
    /// names one, and it is an ordinary trailer key that a linearized file may legitimately carry.
    /// Matching the name without checking what follows it accepts the longer key, so this file —
    /// whose one object is an ordinary object, not a section — would be read as part 3 and its real
    /// terminator ignored, losing a revision.
    /// </summary>
    /// <remarks>
    /// <see cref="ACrossReferenceStreamFirstPageSectionIsNotARevisionEither"/> is the acceptance
    /// control: where the object genuinely is an <c>/XRef</c> stream, the terminator still is
    /// ignored. Without that pairing this test would also pass on a recogniser that had stopped
    /// recognising the stream form at all.
    /// </remarks>
    [Fact]
    public void AFirstPageTrailerNamingXRefStmIsNotTakenForACrossReferenceStreamSection()
    {
        byte[] pdf = Linearized(
            firstPageStartxref: FirstPageXrefOffset(strayObject: true, hybrid: true),
            strayObject: true, hybrid: true);

        Assert.Equal(2, Revisions(pdf).Count);
    }

    /// <summary>
    /// A cross-reference stream's data may contain the five bytes <c>%%EOF</c>. With no
    /// <c>startxref</c> before it that is not a terminator, so the real one after it is still the
    /// first-page trailer's — and still ignored.
    /// </summary>
    [Fact]
    public void ACrossReferenceStreamWhoseDataContainsTheMarkerIsStillOneRevision()
    {
        byte[] pdf = Linearized(
            firstPageStartxref: FirstPageXrefOffset(xrefStream: true),
            xrefStream: true, markerInData: true);

        var only = Assert.Single(Revisions(pdf));
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>
    /// The recogniser fires on the <c>/Linearized</c> dictionary, not on the shape alone: the same
    /// bytes with an ordinary first object are the two revisions they look like.
    /// </summary>
    [Fact]
    public void ANonLinearizedFileOfTheSameShapeIsStillTwoRevisions()
    {
        byte[] pdf = Linearized(firstPageStartxref: FirstPageXrefOffset(), linearized: false);

        Assert.Equal(2, Revisions(pdf).Count);
    }

    /// <summary>
    /// The scan <c>PdfIncrementalSaveValidator</c> uses answers "where does the revision starting
    /// here end" — for a linearized file, at the main trailer, not at part 3's ignored terminator.
    /// </summary>
    [Fact]
    public void TheValidatedEofScanSkipsTheFirstPageTerminatorToo()
    {
        byte[] pdf = Linearized(firstPageStartxref: FirstPageXrefOffset());
        string text = Encoding.Latin1.GetString(pdf);

        long end = PdfRevisions.FindValidatedEofAfter(pdf, 0);

        int lastEof = text.LastIndexOf("%%EOF", System.StringComparison.Ordinal);
        Assert.Equal(lastEof + 5, end);
    }

    // ------------------------------------------------------------------ helpers

    private static System.Collections.Generic.IReadOnlyList<PdfRevisionInfo> Revisions(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfRevisions.List(ms);
    }

    /// <summary>Where part 3 begins in the fixture — a real, backward-pointing position.</summary>
    private static int FirstPageXrefOffset(
        bool xrefStream = false, bool strayObject = false, bool hybrid = false)
    {
        Build(firstPageStartxref: 0, xrefStream, linearized: true, strayObject, markerInData: false,
              hybrid, out int part3);
        return part3;
    }

    private static byte[] Linearized(
        int? firstPageStartxref, bool xrefStream = false, bool linearized = true,
        bool strayObject = false, bool markerInData = false, bool hybrid = false)
        => Build(firstPageStartxref, xrefStream, linearized, strayObject, markerInData, hybrid, out _);

    /// <summary>
    /// A file laid out as Annex F.3 describes: header; the linearization parameter dictionary as
    /// object 1; part 3 (a table, or with <paramref name="xrefStream"/> a cross-reference stream
    /// object) ending in <c>startxref &lt;firstPageStartxref&gt; %%EOF</c> — or in nothing when that
    /// is null; the catalog, page and page tree; and the main table and trailer.
    /// <paramref name="strayObject"/> puts an ordinary object between parts 2 and 3;
    /// <paramref name="markerInData"/> starts the cross-reference stream's data with <c>%%EOF</c>;
    /// <paramref name="hybrid"/> gives the first-page trailer a <c>/XRefStm</c> entry (§7.5.8.4).
    /// </summary>
    private static byte[] Build(
        int? firstPageStartxref, bool xrefStream, bool linearized, bool strayObject,
        bool markerInData, bool hybrid, out int part3)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n%âãÏÓ\n");

        sb.Append(linearized
            ? "1 0 obj\n<< /Linearized 1 /L 0 /H [0 0] /O 3 /E 0 /N 1 /T 0 >>\nendobj\n"
            : "1 0 obj\n<< /Type /Metadata /Subtype /XML /Length 0 >>\nendobj\n");

        if (strayObject) sb.Append("9 0 obj\n<< /Stray true >>\nendobj\n");

        part3 = sb.Length;
        if (!xrefStream)
        {
            sb.Append("xref\n1 3\n");
            for (int i = 0; i < 3; i++) sb.Append("0000000000 00000 n \n");
            sb.Append("trailer\n<< /Size 5 /Root 2 0 R /Prev 0")
              .Append(hybrid ? " /XRefStm 12" : "").Append(" >>\n");
        }
        else
        {
            // Uncompressed, so the fixture's own text is what the scanner sees: three 4-byte rows.
            string rows = markerInData ? "%%EOF0000000000000000000" : "010000000100000001000000";
            sb.Append("5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 1] /Index [1 3] /Root 2 0 R /Prev 0")
              .Append(" /Length ").Append(rows.Length).Append(" >>\nstream\n").Append(rows)
              .Append("\nendstream\nendobj\n");
        }
        if (firstPageStartxref != null)
            sb.Append("startxref\n").Append(firstPageStartxref.Value.ToString(CultureInfo.InvariantCulture))
              .Append("\n%%EOF\n");

        sb.Append("2 0 obj\n<< /Type /Catalog /Pages 4 0 R >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 4 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        sb.Append("xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 5 >>\nstartxref\n")
          .Append(part3.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>An ordinary incremental update: object 3 rewritten, a table for it, a trailer chained by <c>/Prev</c>.</summary>
    private static byte[] AppendUpdate(byte[] original)
    {
        var sb = new StringBuilder();
        int objectOffset = original.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 4 0 R /MediaBox [0 0 300 300] >>\nendobj\n");
        int xref = original.Length + sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n3 1\n")
          .Append(objectOffset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n")
          .Append("trailer\n<< /Size 5 /Prev 9 >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");

        byte[] tail = Encoding.Latin1.GetBytes(sb.ToString());
        var updated = new byte[original.Length + tail.Length];
        System.Buffer.BlockCopy(original, 0, updated, 0, original.Length);
        System.Buffer.BlockCopy(tail, 0, updated, original.Length, tail.Length);
        return updated;
    }
}
