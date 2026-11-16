// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The two Base readers of a signature's <c>/ByteRange</c> — <see cref="PdfIncrementalSaveValidator"/>
/// and <see cref="PdfRevisions"/> — must report the same covered ranges whichever way the four
/// integers are written.
/// </summary>
/// <remarks>
/// <para><b>These two were the weakest of the six copies of this read.</b> Both used
/// <c>PdfDictionary.GetArray</c>, which tests the RAW value's type and does not follow an indirect
/// reference (§7.3.10 permits one), and then <c>PdfArray.GetLong</c>, which THROWS for anything
/// that is not a number — including the reference §7.3.10 permits for an element.</para>
///
/// <para><b>Both failure modes are silent, and the second is worse than it looks.</b> An
/// unresolved array read as <i>absent</i>, so the signature simply vanished from the report — in
/// <c>PdfRevisions</c> without even reaching the <c>unattributed</c> list, so a caller was told the
/// revision carried no signature rather than that one could not be read. An unresolved ELEMENT
/// threw, and both call sites sit inside a blanket <c>catch</c>, so one malformed signature
/// discarded <b>every signature after it</b> as well.</para>
///
/// <para><b>On a directly-written array they always agreed</b>, which is why <c>Direct</c> runs as
/// the control in every theory below: it is what every producer emits and what the pre-existing
/// tests cover. No cryptography is involved — neither of these two verifies a digest — so the
/// fixture declares a self-consistent range and nothing more.</para>
/// </remarks>
public sealed class IndirectByteRangeRevisionReadTests
{
    public enum ByteRangeSpelling { Direct, IndirectArray, IndirectElements }

    /// <summary>
    /// The validator must report the same four integers, not merely "a signature exists".
    /// </summary>
    [Theory]
    [InlineData(ByteRangeSpelling.Direct)]
    [InlineData(ByteRangeSpelling.IndirectArray)]
    [InlineData(ByteRangeSpelling.IndirectElements)]
    public void TheIncrementalSaveValidatorReportsTheCoveredRanges_WhicheverWayByteRangeIsSpelled(
        ByteRangeSpelling spelling)
    {
        byte[] pdf = SignedPdf(spelling, out long[] expected);

        using var ms = new MemoryStream(pdf);
        var info = PdfIncrementalSaveValidator.Inspect(ms);

        var report = Assert.Single(info.SignatureReports);
        Assert.Equal("Signature1", report.FieldName);
        Assert.Equal(expected, report.ByteRange);
        Assert.True(report.CoversWholeFile,
            $"The signature was read as covering {report.ByteRange[2] + report.ByteRange[3]} of " +
            $"{pdf.Length} bytes.");
        Assert.Null(report.Issue);
    }

    /// <summary>
    /// Revision attribution reads elements 2 and 3 to decide which revision a signature closes.
    /// </summary>
    /// <remarks>
    /// A one-revision file, so the answer is unambiguous: the single revision is signed by
    /// <c>Signature1</c> and nothing is unattributed. An unresolved read produced an EMPTY
    /// <c>SignedBy</c> with an empty <c>Unattributed</c> beside it — the shape that says "this
    /// document has no signatures".
    /// </remarks>
    [Theory]
    [InlineData(ByteRangeSpelling.Direct)]
    [InlineData(ByteRangeSpelling.IndirectArray)]
    [InlineData(ByteRangeSpelling.IndirectElements)]
    public void RevisionAttributionNamesTheSignature_WhicheverWayByteRangeIsSpelled(
        ByteRangeSpelling spelling)
    {
        byte[] pdf = SignedPdf(spelling, out _);

        using var ms = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(ms);

        Assert.Null(attribution.NotExaminedReason);
        var last = attribution.Revisions[attribution.Revisions.Count - 1];
        Assert.Contains("Signature1", last.SignedBy);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    /// <summary>
    /// The floor. Resolving must not become "accept anything": an element that is a string is
    /// still not a byte range, so the signature is left out of the report rather than reported
    /// with invented offsets.
    /// </summary>
    [Fact]
    public void AByteRangeElementThatIsNotANumber_IsStillLeftOutOfTheReport()
    {
        byte[] pdf = SignedPdf(ByteRangeSpelling.Direct, out _, byteRangeOverride: "[0 (nope) 100 5]");

        using var ms = new MemoryStream(pdf);
        var info = PdfIncrementalSaveValidator.Inspect(ms);

        Assert.Empty(info.SignatureReports);
    }

    // ================================================================== fixture

    private const int ContentsReserveBytes = 32;
    private const string ByteRangePlaceholder = "[0000000000 0000000000 0000000000 0000000000]";

    /// <summary>
    /// A one-revision PDF with a single signature field whose <c>/ByteRange</c> covers the whole
    /// file, written in the requested spelling. <paramref name="expected"/> receives the four
    /// integers the file finally declares, so the assertions name a computed truth rather than a
    /// literal that could drift with the fixture.
    /// </summary>
    private static byte[] SignedPdf(
        ByteRangeSpelling spelling, out long[] expected, string byteRangeOverride = null)
    {
        const int fieldObj = 6;
        const int sigDictObj = 7;
        const int firstExtraObj = 8;
        int extraCount = spelling == ByteRangeSpelling.IndirectArray ? 1
                       : spelling == ByteRangeSpelling.IndirectElements ? 4 : 0;

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"{fieldObj} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1)"
          + $" /V {sigDictObj} 0 R /Rect [0 0 0 0] >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append($"{sigDictObj} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite ");
        sb.Append("/SubFilter /adbe.pkcs7.detached /ByteRange ");

        long inlinePos = -1;
        if (byteRangeOverride != null)
        {
            sb.Append(byteRangeOverride);
        }
        else
        {
            switch (spelling)
            {
                case ByteRangeSpelling.IndirectArray:
                    sb.Append($"{firstExtraObj} 0 R");
                    break;
                case ByteRangeSpelling.IndirectElements:
                    sb.Append('[');
                    for (int i = 0; i < 4; i++)
                        sb.Append(i == 0 ? "" : " ").Append($"{firstExtraObj + i} 0 R");
                    sb.Append(']');
                    break;
                default:
                    inlinePos = sb.Length;
                    sb.Append(ByteRangePlaceholder);
                    break;
            }
        }

        sb.Append(" /Contents <");
        long contentsStart = sb.Length;
        sb.Append(new string('0', ContentsReserveBytes * 2));
        long contentsEnd = sb.Length;
        sb.Append("> >>\nendobj\n");

        var extraValuePos = new long[extraCount];
        for (int i = 0; i < extraCount; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{firstExtraObj + i} 0 obj\n");
            extraValuePos[i] = sb.Length;
            sb.Append(spelling == ByteRangeSpelling.IndirectArray
                ? ByteRangePlaceholder
                : new string('0', 10));
            sb.Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        byte[] file = Encoding.ASCII.GetBytes(sb.ToString());

        // The library's own write-side arithmetic, so the fixture cannot drift from the convention
        // the production signers use.
        var range = SignatureByteRange.Compute(contentsStart, contentsEnd, file.Length);
        expected = new[] { 0L, range.BeforeLength, range.AfterStart, range.AfterLength };

        if (byteRangeOverride == null)
        {
            if (spelling == ByteRangeSpelling.IndirectElements)
            {
                for (int i = 0; i < 4; i++)
                {
                    string text = expected[i].ToString(CultureInfo.InvariantCulture).PadRight(10);
                    if (text.Length != 10)
                        throw new InvalidOperationException("Element does not fit its placeholder.");
                    Encoding.ASCII.GetBytes(text).CopyTo(file, extraValuePos[i]);
                }
            }
            else
            {
                long at = spelling == ByteRangeSpelling.IndirectArray ? extraValuePos[0] : inlinePos;
                Encoding.ASCII.GetBytes(range.Format(ByteRangePlaceholder.Length)).CopyTo(file, at);
            }
        }

        return file;
    }
}
