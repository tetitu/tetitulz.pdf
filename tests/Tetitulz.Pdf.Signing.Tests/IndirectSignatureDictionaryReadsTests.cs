// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Everything a signature dictionary declares may be written as an indirect reference (§7.3.10),
/// and the readers of <c>/ByteRange</c>, <c>/FT</c> and <c>/T</c> must all follow one.
/// </summary>
/// <remarks>
/// <para><b>On a well-formed file every one of these readers agreed</b> — a directly-written array
/// of four directly-written integers is what <c>PdfSigner</c> and <c>PdfDocumentTimestamper</c>
/// emit and what every fixture in this suite carried. The separating input is an indirect value
/// where a direct one was assumed, so each theory below runs the same assertion over all three
/// spellings with <c>Direct</c> as the control. A test that only exercised the indirect spelling
/// could not tell "the reader works" from "the fixture is wrong".</para>
///
/// <para><b>Why the byte ranges are asserted rather than the verdict.</b> <c>/ByteRange</c> decides
/// WHICH BYTES a signature covers. Asserting only that verification returned something would pass
/// with a reader that silently covers a shorter prefix — and shorter is the dangerous direction,
/// because bytes outside the covered range are bytes an attacker may rewrite. Every assertion here
/// names the covered end offset explicitly.</para>
///
/// <para><b>The fixtures are genuinely valid in all three spellings.</b> The CMS is minted over the
/// two ranges the file finally declares, after the byte-range placeholders have been patched, so
/// the only difference between the three is how the four integers are written down.</para>
/// </remarks>
public sealed class IndirectSignatureDictionaryReadsTests
{
    /// <summary>How the fixture writes the four <c>/ByteRange</c> integers.</summary>
    public enum ByteRangeSpelling
    {
        /// <summary><c>/ByteRange [0 a b c]</c> — the control; every producer writes this.</summary>
        Direct,

        /// <summary><c>/ByteRange 8 0 R</c>, object 8 holding the array.</summary>
        IndirectArray,

        /// <summary><c>/ByteRange [8 0 R 9 0 R 10 0 R 11 0 R]</c> — four number objects.</summary>
        IndirectElements,
    }

    // ------------------------------------------------------------------ M18 / M3: /ByteRange

    /// <summary>
    /// The verifier must cover the same bytes whichever way the array is written.
    /// </summary>
    /// <remarks>
    /// <c>ByteRangeCoverageEnd</c> is the assertion that matters: it is set from the DECLARED range
    /// before the CMS is checked, and a <c>0</c> there disables the shadow/append check for this
    /// signature and stops the DocMDP block matching it. Under the old element type-check an
    /// indirect element failed the field outright, so a valid signature was reported invalid AND
    /// its coverage silently became 0.
    /// </remarks>
    [Theory]
    [InlineData(ByteRangeSpelling.Direct)]
    [InlineData(ByteRangeSpelling.IndirectArray)]
    [InlineData(ByteRangeSpelling.IndirectElements)]
    public void TheVerifierCoversTheWholeFile_WhicheverWayByteRangeIsSpelled(ByteRangeSpelling spelling)
    {
        var root = SigningTestCrypto.Ca("CN=ByteRange Root");
        var signer = SigningTestCrypto.Leaf(root, "CN=ByteRange Signer");
        byte[] pdf = AppendCmsSignature(SignedPdfFixtures.MinimalPdf(), signer, root, "Signature1", spelling);

        var result = VerifyField(pdf, "Signature1", root);

        Assert.True(result.IsIntegrityValid,
            "integrity: " + string.Join("; ", result.ValidationErrors));
        Assert.Equal(pdf.Length, result.ByteRangeCoverageEnd);
        Assert.True(result.CoversEntireDocument,
            $"The signature was read as covering only {result.ByteRangeCoverageEnd} of " +
            $"{pdf.Length} bytes, so bytes it actually signed are reported as unsigned.");
        Assert.True(result.CoversRevision);
    }

    /// <summary>
    /// The counter-signer locates the <c>/Contents</c> hole from the same four integers, resolved and
    /// type-checked: read with <c>GetArray</c>/<c>GetLong</c>, it would REFUSE a signature
    /// <c>PdfSignatureVerifier</c> verifies happily.
    /// </summary>
    /// <remarks>
    /// The output is re-verified rather than merely inspected: the counter-signature goes into the
    /// excluded hole, so a counter-signer that mislocated the hole by even one byte would corrupt
    /// bytes the original signature covers and the document digest would stop matching. That is the
    /// failure this asserts against — and the covered range is asserted again afterwards, because
    /// the hole must not have moved either.
    /// </remarks>
    [Theory]
    [InlineData(ByteRangeSpelling.Direct)]
    [InlineData(ByteRangeSpelling.IndirectArray)]
    [InlineData(ByteRangeSpelling.IndirectElements)]
    public void TheCounterSignerFindsTheContentsHole_WhicheverWayByteRangeIsSpelled(
        ByteRangeSpelling spelling)
    {
        var root = SigningTestCrypto.Ca("CN=CounterSign Root");
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        byte[] signed = AppendCmsSignature(
            SignedPdfFixtures.MinimalPdf(), signer, root, "Signature1", spelling);

        using var input = new MemoryStream(signed);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw);
        byte[] countersigned = output.ToArray();

        // The counter-signature really landed.
        var cms = new CmsSignedData(ContentsOf(countersigned, "Signature1"));
        var counterSigners = cms.GetSignerInfos().GetSigners()
                                .Cast<SignerInformation>()
                                .SelectMany(s => s.GetCounterSignatures().GetSigners().Cast<SignerInformation>())
                                .ToList();
        Assert.NotEmpty(counterSigners);

        // And it landed INSIDE the hole: the original signature still covers the same bytes and
        // still verifies over them.
        var result = VerifyField(countersigned, "Signature1", root);
        Assert.True(result.IsIntegrityValid,
            "Counter-signing moved bytes the original signature covers: " +
            string.Join("; ", result.ValidationErrors));
        Assert.Equal(signed.Length, result.ByteRangeCoverageEnd);
        Assert.Equal(signed.Length, countersigned.Length);
    }

    /// <summary>
    /// <c>DocMdpAnalyzer</c> reads the certified region from the same four integers, and its own
    /// doc comment promises it "never throws on malformed input". It resolved the array but called
    /// <c>PdfArray.GetLong</c> on the elements, which THROWS for a <see cref="PdfReference"/> — so
    /// that exception escaped a public API.
    /// </summary>
    /// <remarks>
    /// The assertion is the certified END OFFSET, not merely that a certification was found: a
    /// mis-read range understates the certified region, and understating it is what lets a later
    /// incremental update pass as if it had been certified.
    /// </remarks>
    [Theory]
    [InlineData(ByteRangeSpelling.Direct)]
    [InlineData(ByteRangeSpelling.IndirectArray)]
    [InlineData(ByteRangeSpelling.IndirectElements)]
    public void TheDocMdpAnalyzerReadsTheCertifiedEnd_WhicheverWayByteRangeIsSpelled(
        ByteRangeSpelling spelling)
    {
        byte[] pdf = CertifiedPdf(spelling);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);

        Assert.True(analysis.HasCertification,
            "The DocMDP certification signature was not found, so the fixture never reached the " +
            "byte-range read under test.");
        Assert.Equal(pdf.Length, analysis.CertByteRangeEnd);
        Assert.False(analysis.HasIncrementalUpdates);
        Assert.True(analysis.IsCompliant,
            "violations: " + string.Join("; ", analysis.Violations));
    }

    /// <summary>
    /// The floor. Resolving must not become "accept anything": a <c>/ByteRange</c> element that is
    /// a string is still refused, whether written directly or behind a reference. Without this the
    /// theories above would be satisfied by deleting the element type-check outright.
    /// </summary>
    [Theory]
    [InlineData("[0 (nope) 100 5]")]
    [InlineData("[0 8 0 R 100 5]")]   // object 8 is a string, not a number
    public void AByteRangeElementThatIsNotANumber_IsStillRefused(string byteRangeLiteral)
    {
        byte[] pdf = PdfWithLiteralByteRange(byteRangeLiteral);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions());
        var only = Assert.Single(results);

        Assert.False(only.IsIntegrityValid);
        Assert.Contains(only.ValidationErrors,
            e => e.Contains("/ByteRange", StringComparison.Ordinal));
        Assert.Equal(0, only.ByteRangeCoverageEnd);

        // And the analyser refuses the same file without throwing — the contract its own summary
        // states.
        var analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);
        Assert.Equal(0, analysis.CertByteRangeEnd);
    }

    // ------------------------------------------------------------------ M4: /FT and /T adoption

    /// <summary>
    /// An empty signature field whose <c>/FT</c> and <c>/T</c> are indirect references is still the
    /// field that gets signed.
    /// </summary>
    /// <remarks>
    /// <para><c>FindAdoptableSignatureField</c> read both with <c>GetName</c>/<c>GetString</c>,
    /// which test the RAW value's type, so an indirect spelling read as ABSENT and the candidate
    /// was skipped. <c>SignatureFieldRegistration.FieldNames</c>, sixty lines below, DOES resolve <c>/T</c> — so the
    /// two halves disagreed about one document: adoption saw no candidate while the collision scan
    /// saw the name. The signer renamed itself and wrote a second, invisible field to carry the
    /// <c>/V</c>, leaving the stamped field permanently unsigned — exactly the outcome adoption
    /// exists to prevent.</para>
    /// <para>The assertion is the ADOPTED FIELD'S IDENTITY: one field, under the requested name,
    /// carrying both the signature value and the stamp rectangle. Counting fields alone would pass
    /// if the signer adopted the wrong one.</para>
    /// </remarks>
    [Theory]
    [InlineData(false)]  // /FT /Sig  /T (VisualSig1)  — the control
    [InlineData(true)]   // /FT 7 0 R /T 8 0 R
    public void AnEmptyVisibleField_IsAdopted_WhicheverWayItsFtAndTAreSpelled(bool indirect)
    {
        const string fieldName = "VisualSig1";
        byte[] signed = Sign(PdfWithEmptySignatureWidget(fieldName, indirect), fieldName);

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        var fields = doc.AcroForm!.GetSignatureFields().ToList();
        Assert.True(fields.Count == 1,
            "The stamped field was not adopted, so the document carries a second, invisible " +
            $"signature field. Found {fields.Count}: " +
            string.Join(", ", fields.Select(f => f.FieldName ?? "<null>")) + ".");
        Assert.Equal(fieldName, fields[0].FieldName);
        Assert.True(fields[0].HasSignature);

        // The signed field is the STAMPED one: its /Rect is the stamp rectangle, not [0 0 0 0].
        var acro = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]) as PdfDictionary;
        var arr = acro!.GetArray(PdfName.Fields);
        Assert.Equal(1, arr!.Count);
        var widget = doc.ResolveObject(arr[0]) as PdfDictionary;
        var rect = widget!.GetArray(PdfName.Rect);
        Assert.NotNull(rect);
        Assert.Equal(new[] { 50d, 60d, 250d, 140d },
                     Enumerable.Range(0, 4).Select(i => rect!.GetDouble(i)).ToArray());

        var result = new PdfSignatureVerifier().Verify(doc, fieldName);
        Assert.True(result.IsIntegrityValid,
            "Signature over the adopted field failed integrity: " +
            string.Join("; ", result.ValidationErrors));
    }

    /// <summary>
    /// The floor for adoption. Resolving <c>/T</c> must not turn adoption into "take the first
    /// empty signature field": a field whose (indirect) name is something else is still a
    /// different field, so the requested name is created alongside it.
    /// </summary>
    [Fact]
    public void AnEmptyFieldWithADifferentIndirectName_IsNotAdopted()
    {
        byte[] signed = Sign(PdfWithEmptySignatureWidget("SomeOtherField", indirect: true), "VisualSig1");

        using var ms = new MemoryStream(signed);
        using var doc = PdfDocument.Open(ms);

        var names = doc.AcroForm!.GetSignatureFields().Select(f => f.FieldName).ToList();
        Assert.Equal(2, names.Count);
        Assert.Contains("SomeOtherField", names);
        Assert.Contains("VisualSig1", names);
    }

    // ================================================================== fixtures

    private const int ContentsReserveBytes = 8192;
    private const string ByteRangePlaceholder = "[0000000000 0000000000 0000000000 0000000000]";

    /// <summary>
    /// Appends an <c>adbe.pkcs7.detached</c> approval-signature revision whose <c>/ByteRange</c> is
    /// written in the requested spelling, with the CMS minted over the two ranges the finished file
    /// declares.
    /// </summary>
    /// <remarks>
    /// Hand-written rather than driven through <see cref="PdfSigner"/> because the signer has no
    /// way to emit an indirect <c>/ByteRange</c> — the whole point of the fixture. The byte-range
    /// arithmetic still comes from the library's own <c>SignatureByteRange</c>, so the fixture
    /// cannot drift from the convention the production writers use.
    /// </remarks>
    private static byte[] AppendCmsSignature(
        byte[] pdf, SigningTestCrypto.Issued signer, SigningTestCrypto.Issued root,
        string fieldName, ByteRangeSpelling spelling)
    {
        const int sigDictObj = 6;
        const int fieldObj = 7;
        const int firstExtraObj = 8;
        int extraCount = spelling switch
        {
            ByteRangeSpelling.IndirectArray => 1,
            ByteRangeSpelling.IndirectElements => 4,
            _ => 0,
        };

        long prevXref = SignedPdfFixtures.FindStartXref(pdf);
        var body = new StringBuilder();
        long Mark() => pdf.Length + body.Length;

        long sigDictOffset = Mark();
        body.Append($"{sigDictObj} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite ");
        body.Append("/SubFilter /adbe.pkcs7.detached /ByteRange ");

        long inlineByteRangePos = -1;
        switch (spelling)
        {
            case ByteRangeSpelling.IndirectArray:
                body.Append($"{firstExtraObj} 0 R");
                break;
            case ByteRangeSpelling.IndirectElements:
                body.Append('[');
                for (int i = 0; i < 4; i++)
                    body.Append(i == 0 ? "" : " ").Append($"{firstExtraObj + i} 0 R");
                body.Append(']');
                break;
            default:
                inlineByteRangePos = Mark();
                body.Append(ByteRangePlaceholder);
                break;
        }

        body.Append(" /Contents <");
        long contentsStart = Mark();
        body.Append(new string('0', ContentsReserveBytes * 2));
        long contentsEnd = Mark();
        body.Append("> >>\nendobj\n");

        long fieldOffset = Mark();
        body.Append($"{fieldObj} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T ({fieldName})")
            .Append($" /V {sigDictObj} 0 R /F 4 /Rect [0 0 0 0] /P 3 0 R >>\nendobj\n");

        var extraOffsets = new long[extraCount];
        var extraValuePos = new long[extraCount];
        for (int i = 0; i < extraCount; i++)
        {
            extraOffsets[i] = Mark();
            body.Append($"{firstExtraObj + i} 0 obj\n");
            extraValuePos[i] = Mark();
            body.Append(spelling == ByteRangeSpelling.IndirectArray
                ? ByteRangePlaceholder
                : new string('0', 10));
            body.Append("\nendobj\n");
        }

        long catalogOffset = Mark();
        body.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [")
            .Append($"{fieldObj} 0 R] /SigFlags 3 >> >>\nendobj\n");

        long xrefOffset = Mark();
        body.Append("xref\n");
        body.Append("1 1\n").AppendFormat("{0:D10} 00000 n \n", catalogOffset);
        body.Append($"{sigDictObj} {2 + extraCount}\n")
            .AppendFormat("{0:D10} 00000 n \n", sigDictOffset)
            .AppendFormat("{0:D10} 00000 n \n", fieldOffset);
        for (int i = 0; i < extraCount; i++)
            body.AppendFormat("{0:D10} 00000 n \n", extraOffsets[i]);
        body.Append($"trailer\n<< /Size {firstExtraObj + extraCount} /Root 1 0 R /Prev {prevXref} >>\n");
        body.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        byte[] file = Concat(pdf, Encoding.ASCII.GetBytes(body.ToString()));

        // ---- Patch the real byte range, through the library's own arithmetic. ----
        var range = SignatureByteRange.Compute(contentsStart, contentsEnd, file.Length);
        PatchByteRange(file, spelling, range, inlineByteRangePos, extraValuePos);

        // ---- Mint the CMS over exactly the two ranges the file now declares. ----
        byte[] toSign = new byte[range.SignedLength];
        Array.Copy(file, 0, toSign, 0, (int)range.BeforeLength);
        Array.Copy(file, (int)range.AfterStart, toSign, (int)range.BeforeLength, (int)range.AfterLength);

        byte[] cms = CmsSignatureGenerator.GenerateDetachedSignature(
            toSign, signer.Private, signer.Cert,
            new List<X509Certificate> { signer.Cert, root.Cert }, "SHA-256");

        if (cms.Length > ContentsReserveBytes)
            throw new InvalidOperationException($"CMS of {cms.Length} bytes exceeds the reserve.");

        string hex = BitConverter.ToString(cms).Replace("-", "").ToLowerInvariant()
                                 .PadRight(ContentsReserveBytes * 2, '0');
        Encoding.ASCII.GetBytes(hex).CopyTo(file, contentsStart);

        return file;
    }

    private static void PatchByteRange(
        byte[] file, ByteRangeSpelling spelling, SignatureByteRange range,
        long inlineByteRangePos, long[] extraValuePos)
    {
        if (spelling == ByteRangeSpelling.IndirectElements)
        {
            long[] values = { 0, range.BeforeLength, range.AfterStart, range.AfterLength };
            for (int i = 0; i < 4; i++)
            {
                string text = values[i].ToString(CultureInfo.InvariantCulture).PadRight(10);
                if (text.Length != 10)
                    throw new InvalidOperationException("Byte-range element does not fit its placeholder.");
                Encoding.ASCII.GetBytes(text).CopyTo(file, extraValuePos[i]);
            }
            return;
        }

        long at = spelling == ByteRangeSpelling.IndirectArray ? extraValuePos[0] : inlineByteRangePos;
        Encoding.ASCII.GetBytes(range.Format(ByteRangePlaceholder.Length)).CopyTo(file, at);
    }

    /// <summary>
    /// A single-revision PDF carrying a DocMDP certification signature whose <c>/ByteRange</c>
    /// covers the whole file, written in the requested spelling. No cryptography:
    /// <c>DocMdpAnalyzer</c> is pure-structure analysis, so a self-consistent range is the only
    /// thing it reads here.
    /// </summary>
    private static byte[] CertifiedPdf(ByteRangeSpelling spelling)
    {
        const int reserve = 64;
        const int fieldObj = 6;
        const int sigDictObj = 7;
        const int firstExtraObj = 8;
        int extraCount = spelling switch
        {
            ByteRangeSpelling.IndirectArray => 1,
            ByteRangeSpelling.IndirectElements => 4,
            _ => 0,
        };

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj($"{fieldObj} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (CertSig)"
          + $" /V {sigDictObj} 0 R /Rect [0 0 0 0] >>\nendobj\n");

        offsets.Add(sb.Length);
        sb.Append($"{sigDictObj} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite ");
        sb.Append("/SubFilter /adbe.pkcs7.detached /Reference [<< /Type /SigRef ");
        sb.Append("/TransformMethod /DocMDP /TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>] ");
        sb.Append("/ByteRange ");

        long inlineByteRangePos = -1;
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
                inlineByteRangePos = sb.Length;
                sb.Append(ByteRangePlaceholder);
                break;
        }

        sb.Append(" /Contents <");
        long contentsStart = sb.Length;
        sb.Append(new string('0', reserve * 2));
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
        var range = SignatureByteRange.Compute(contentsStart, contentsEnd, file.Length);
        PatchByteRange(file, spelling, range, inlineByteRangePos, extraValuePos);
        return file;
    }

    /// <summary>
    /// A one-page PDF with one signature field whose <c>/ByteRange</c> is written out verbatim, so
    /// a test can plant an element of the wrong type. Nothing here is a valid signature; the point
    /// is that the readers refuse it cleanly rather than throwing or accepting.
    /// </summary>
    private static byte[] PdfWithLiteralByteRange(string byteRangeLiteral)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1)"
          + " /V 7 0 R /Rect [0 0 0 0] >>\nendobj\n");
        Obj("7 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
          + "/Reference [<< /Type /SigRef /TransformMethod /DocMDP "
          + "/TransformParams << /Type /TransformParams /P 2 >> >>] "
          + $"/ByteRange {byteRangeLiteral} /Contents <00> >>\nendobj\n");
        Obj("8 0 obj\n(not a number)\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A one-page PDF whose AcroForm holds a single EMPTY signature field: a widget with a real
    /// <c>/Rect</c> and an <c>/AP /N</c> appearance and no <c>/V</c> — the shape
    /// <c>VisualSignatureBuilder</c> writes before handing the file to the signer.
    /// <paramref name="indirect"/> writes its <c>/FT</c> and <c>/T</c> as references instead.
    /// </summary>
    private static byte[] PdfWithEmptySignatureWidget(string fieldName, bool indirect)
    {
        string ft = indirect ? "7 0 R" : "/Sig";
        string t = indirect ? "8 0 R" : $"({fieldName})";

        var objects = new List<(int num, string body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>"),
            (4, "<< /Length 44 >>\nstream\nBT /F1 12 Tf 72 720 Td (Stamped) Tj ET\nendstream"),
            (5, $"<< /Type /Annot /Subtype /Widget /FT {ft} /T {t} /F 132 "
              + "/Rect [50 60 250 140] /P 3 0 R /AP << /N 6 0 R >> >>"),
            (6, "<< /Type /XObject /Subtype /Form /BBox [0 0 200 80] /Length 32 >>\n"
              + "stream\n1 0 0 RG 2 w 5 5 190 70 re S\nendstream"),
            (7, "/Sig"),
            (8, $"({fieldName})"),
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Count + 1];
        foreach (var (num, body) in objects)
        {
            offsets[num] = sb.Length;
            sb.Append(num).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xrefPos = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
            sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefPos).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ================================================================== helpers

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    private static byte[] ContentsOf(byte[] pdf, string fieldName)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var field = doc.AcroForm!.FindSignatureField(fieldName);
        Assert.NotNull(field);
        var contents = field!.SignatureValue!.GetString(PdfName.Contents);
        Assert.NotNull(contents);
        // Trimmed of the /Contents reserve padding, exactly as the production readers do
        // (SigningDefaults.TrimToAsn1Object). A stand-in that parses the hole rather than
        // the object is not standing in for what ships: BouncyCastle 2.6.2 tolerates the
        // tail and 2.7.0 refuses it, so without this the fixture pins the wrong contract.
        return SigningDefaults.TrimToAsn1Object(contents!.RawBytes);
    }

    private static SignatureVerificationResult VerifyField(
        byte[] pdf, string fieldName, SigningTestCrypto.Issued root)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
        });

        var match = results.SingleOrDefault(r => r.FieldName == fieldName);
        Assert.True(match != null,
            $"No result for '{fieldName}'; the fixture never reached the code under test. " +
            $"Fields seen: {string.Join(", ", results.Select(r => r.FieldName ?? "<null>"))}");
        return match!;
    }

    private static byte[] Sign(byte[] pdf, string fieldName)
    {
        var root = SigningTestCrypto.Ca("CN=Adoption Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Adoption Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        using var cert = new MemoryStream(pfx);
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = cert,
            CertificatePassword = password,
            SignatureFieldName = fieldName,
        });
        return output.ToArray();
    }
}
