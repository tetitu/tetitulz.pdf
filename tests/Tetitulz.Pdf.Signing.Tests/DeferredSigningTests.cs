// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfDeferredSigner"/> — write the placeholder now, obtain the CMS elsewhere, inject it
/// later.
/// </summary>
/// <remarks>
/// <para><b>The prepared file is asserted as RAW BYTES.</b> A parser normalises away exactly what a
/// placeholder writer can get wrong — a <c>/ByteRange</c> that brackets the wrong span still parses
/// into four perfectly good integers, and a <c>/Contents</c> string one byte short of its reserve is
/// still a valid hex string. <c>SignatureDictionaryKeyUniquenessTests</c> is the precedent and
/// records the same reasoning.</para>
///
/// <para><b>The completed file is asserted through the verifier, against a CMS built out of band.</b>
/// <see cref="CmsSignatureGenerator"/> is called directly on the descriptor's
/// <see cref="PreparedSignature.SignedBytes"/> — never through <c>PdfSigner</c> — because the claim
/// is that a signature produced by something that never saw the document verifies over it. Building
/// the CMS through the signer would prove only that the signer agrees with itself.</para>
///
/// <para><b>Every "it verifies" assertion carries the prepared file beside it as a control.</b> A
/// verifier that reported everything valid would pass the positive case alone.</para>
/// </remarks>
public sealed class DeferredSigningTests
{
    /// <summary>Small enough that an over-reserve is visible, large enough for a real 2-cert CMS.</summary>
    private const int Reserve = 12_000;

    private static readonly DateTime FixedTime = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Signer
    {
        public SigningTestCrypto.Issued Ca = null!;
        public SigningTestCrypto.Issued Leaf = null!;

        public IList<byte[]> ChainDer => new List<byte[]>
        {
            Leaf.Cert.GetEncoded(), Ca.Cert.GetEncoded(),
        };

        public IList<X509Certificate> Chain => new List<X509Certificate> { Leaf.Cert, Ca.Cert };
    }

    /// <summary>
    /// A signer whose certificate was already valid at <see cref="FixedTime"/>: a signature the
    /// fixtures make claims that time in its CMS, where BouncyCastle checks the certificate.
    /// </summary>
    private static Signer NewSigner()
    {
        var ca = SigningTestCrypto.Ca();
        return new Signer
        {
            Ca = ca,
            Leaf = SigningTestCrypto.Leaf(ca, notBefore: FixedTime.AddYears(-1), notAfter: DateTime.UtcNow.AddYears(5)),
        };
    }

    private static SignatureParameters Params(string field = "Signature1") => new()
    {
        SignatureFieldName = field,
        EstimatedSignatureSize = Reserve,
        SigningTime = FixedTime,
        Reason = "deferred",
    };

    /// <summary>Prepares <paramref name="pdf"/> and hands back the bytes plus the descriptor.</summary>
    private static (byte[] prepared, PreparedSignature descriptor) Prepare(
        byte[] pdf, Signer signer, SignatureParameters? parameters = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        var descriptor = PdfDeferredSigner.Prepare(
            input, output, signer.ChainDer, parameters ?? Params());
        return (output.ToArray(), descriptor);
    }

    /// <summary>
    /// The CMS the far side of the boundary would return: built from the descriptor's bytes with no
    /// reference to the document, the signer, or anything else this library wrote.
    /// </summary>
    private static byte[] CmsFor(PreparedSignature descriptor, Signer signer)
        => CmsSignatureGenerator.GenerateDetachedSignature(
            descriptor.SignedBytes, signer.Leaf.Private, signer.Leaf.Cert,
            signer.Chain, descriptor.DigestAlgorithm);

    private static byte[] Complete(byte[] prepared, PreparedSignature descriptor, byte[] cms)
    {
        using var source = new MemoryStream(prepared);
        using var output = new MemoryStream();
        PdfDeferredSigner.Complete(source, descriptor, cms, output);
        return output.ToArray();
    }

    private static SignatureVerificationResult VerifyOnly(byte[] pdf, string fieldName)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return new PdfSignatureVerifier().Verify(doc, fieldName);
    }

    // ------------------------------------------------------------------ raw bytes

    /// <summary>
    /// The placeholder is exactly the reserve the caller asked for, holds nothing but zeros, and the
    /// <c>/ByteRange</c> excludes it to the byte — read straight out of the file rather than through
    /// a parser.
    /// </summary>
    [Fact]
    public void PreparedPlaceholderAndByteRangeAreExactInRawBytes()
    {
        var signer = NewSigner();
        var (prepared, descriptor) = Prepare(SignedPdfFixtures.MinimalPdf(), signer);

        string text = Encoding.ASCII.GetString(prepared);

        // The hex run: 2 chars per reserved byte, all of them '0'. ReservedBytes is asserted here
        // rather than only through the oversize refusal, because that refusal fires at
        // ReservedBytes + 1 whatever ReservedBytes says — so a reserve reported one byte short of
        // the hole would refuse a CMS that fits and no test would see it.
        Assert.Equal(Reserve, descriptor.ReservedBytes);
        Assert.Equal(Reserve * 2, descriptor.ContentsEnd - descriptor.ContentsStart);
        for (long i = descriptor.ContentsStart; i < descriptor.ContentsEnd; i++)
            Assert.Equal((byte)'0', prepared[i]);

        // The delimiters really are delimiters, and nothing else in the file claims to be this one.
        Assert.Equal((byte)'<', prepared[descriptor.ContentsStart - 1]);
        Assert.Equal((byte)'>', prepared[descriptor.ContentsEnd]);

        // /ByteRange, read as the four ASCII integers the writer emitted. The first range must start
        // at byte 0 and stop at the '<'; the second must resume at the byte after the '>' and run to
        // the end of the file. Every one of those is a place an off-by-one lands and still parses.
        long[] range = ReadRawByteRange(text);
        Assert.Equal(descriptor.ByteRange, range);
        Assert.Equal(0, range[0]);
        Assert.Equal(descriptor.ContentsStart - 1, range[0] + range[1]);
        Assert.Equal(descriptor.ContentsEnd + 1, range[2]);
        Assert.Equal(prepared.Length, range[2] + range[3]);
    }

    /// <summary>
    /// The signed bytes are the file minus the hole, in order — not a digest, and not the whole file.
    /// </summary>
    [Fact]
    public void SignedBytesAreTheFileWithTheHoleRemoved()
    {
        var signer = NewSigner();
        var (prepared, descriptor) = Prepare(SignedPdfFixtures.MinimalPdf(), signer);

        var expected = new byte[descriptor.ByteRange[1] + descriptor.ByteRange[3]];
        Array.Copy(prepared, descriptor.ByteRange[0], expected, 0, descriptor.ByteRange[1]);
        Array.Copy(prepared, descriptor.ByteRange[2], expected,
                   descriptor.ByteRange[1], descriptor.ByteRange[3]);

        Assert.Equal(expected, descriptor.SignedBytes);
        Assert.Equal(prepared.Length - (Reserve * 2 + 2), descriptor.SignedBytes.Length);
    }

    // ------------------------------------------------------------------ the round trip

    /// <summary>
    /// A CMS built out of band over the descriptor's bytes verifies over the completed document —
    /// and the prepared document it came from does not, so the assertion is about
    /// <see cref="PdfDeferredSigner.Complete"/> and not about a permissive verifier.
    /// </summary>
    [Fact]
    public void CompletedDocumentVerifiesAndThePreparedOneDoesNot()
    {
        var signer = NewSigner();
        var (prepared, descriptor) = Prepare(SignedPdfFixtures.MinimalPdf(), signer);

        Assert.False(VerifyOnly(prepared, descriptor.FieldName).IsIntegrityValid,
            "a zero-filled placeholder must not verify — if it does, the verifier is not looking.");

        byte[] completed = Complete(prepared, descriptor, CmsFor(descriptor, signer));

        var result = VerifyOnly(completed, descriptor.FieldName);
        Assert.True(result.IsIntegrityValid);
        Assert.True(result.CoversEntireDocument);
        Assert.Equal("CN=Test Signer", result.SignerSubject);
    }

    /// <summary>
    /// Completing changes the hex digits and nothing else: every byte outside the placeholder is the
    /// byte the prepared document had.
    /// </summary>
    /// <remarks>
    /// This is what makes the <c>/ByteRange</c> written in phase one still true in phase two, and it
    /// is invisible to the verifier — a completion that also rewrote the trailer would produce a
    /// file that verifies (the digest is recomputed over whatever is there) while silently no longer
    /// being the document that was reviewed and approved.
    /// </remarks>
    [Fact]
    public void CompletingTouchesOnlyThePlaceholder()
    {
        var signer = NewSigner();
        var (prepared, descriptor) = Prepare(SignedPdfFixtures.MinimalPdf(), signer);
        byte[] completed = Complete(prepared, descriptor, CmsFor(descriptor, signer));

        Assert.Equal(prepared.Length, completed.Length);
        for (int i = 0; i < prepared.Length; i++)
        {
            if (i >= descriptor.ContentsStart && i < descriptor.ContentsEnd) continue;
            if (prepared[i] != completed[i])
                Assert.Fail($"byte {i} changed outside the placeholder: " +
                            $"0x{prepared[i]:X2} -> 0x{completed[i]:X2}");
        }

        // ...and the placeholder itself did change, or the loop above is vacuous.
        Assert.NotEqual(
            new ArraySegment<byte>(prepared, (int)descriptor.ContentsStart, 32),
            new ArraySegment<byte>(completed, (int)descriptor.ContentsStart, 32));
    }

    // ------------------------------------------------------------------ parity with direct signing

    /// <summary>
    /// The bytes <see cref="PdfDeferredSigner.Prepare"/> reports are byte-identical to the bytes
    /// <c>PdfSigner.Sign</c> presents for signing over the same input.
    /// </summary>
    /// <remarks>
    /// The two paths must not drift, and nothing else can see it: a prepared document that digested
    /// a slightly different span would still complete, still verify against its own CMS, and simply
    /// be a signature over a different set of bytes than a directly signed file would have been.
    /// The same parity rule the two renderers live under.
    /// </remarks>
    [Fact]
    public void PreparedSignedBytesMatchWhatDirectSigningPresents()
    {
        var signer = NewSigner();
        byte[] pdf = SignedPdfFixtures.MinimalPdf();

        var (_, descriptor) = Prepare(pdf, signer);

        var recorder = new RecordingSigner(signer.ChainDer, Reserve);
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            var direct = Params();
            direct.ExternalSigner = recorder;
            new PdfSigner().Sign(input, output, direct);
        }

        Assert.NotNull(recorder.Presented);
        Assert.Equal(recorder.Presented, descriptor.SignedBytes);
    }

    // ------------------------------------------------------------------ signing over a signature

    /// <summary>
    /// Preparing over an already-signed document describes the signature it just added — not the one
    /// that was there — and completing leaves the existing signature valid.
    /// </summary>
    /// <remarks>
    /// <c>PdfSigner</c> renames a colliding field, so the caller's <c>SignatureFieldName</c> still
    /// says "Signature1" while the field written is "Signature2". A descriptor that trusted the
    /// parameter would name the <b>first</b> signature, and <c>Complete</c> would then write this
    /// CMS over a signature that was already valid — destroying it, and producing a file where the
    /// wrong signature is broken.
    /// </remarks>
    [Fact]
    public void PreparingOverASignedDocumentDescribesTheNewSignature()
    {
        var first = NewSigner();
        var (pfx, password) = SigningTestCrypto.Pfx(first.Leaf, first.Ca.Cert);
        byte[] signedOnce = SignedPdfFixtures.Sign(
            SignedPdfFixtures.MinimalPdf(), pfx, password, "Signature1", FixedTime);

        var second = NewSigner();
        var (prepared, descriptor) = Prepare(signedOnce, second, Params("Signature1"));

        Assert.Equal("Signature2", descriptor.FieldName);

        byte[] completed = Complete(prepared, descriptor, CmsFor(descriptor, second));

        Assert.True(VerifyOnly(completed, "Signature1").IsIntegrityValid,
            "the pre-existing signature must survive: its bytes were never touched.");
        Assert.True(VerifyOnly(completed, "Signature2").IsIntegrityValid);
    }

    // ------------------------------------------------------------------ refusals

    /// <summary>A CMS that does not fit is refused rather than truncated into the reserve.</summary>
    [Fact]
    public void CompleteRefusesACmsLargerThanTheReserve()
    {
        var signer = NewSigner();
        var (prepared, descriptor) = Prepare(SignedPdfFixtures.MinimalPdf(), signer);

        var ex = Assert.Throws<PdfSigningException>(() =>
            Complete(prepared, descriptor, new byte[descriptor.ReservedBytes + 1]));

        Assert.Contains("reserves", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A descriptor from a different preparation is refused, rather than writing a valid CMS at an
    /// offset that means nothing.
    /// </summary>
    /// <remarks>
    /// <para>The two inputs are of different lengths — one is the bare fixture, the other already
    /// carries a signature — so <b>both</b> ends of the placeholder move. Only the input length can
    /// shift it: everything <c>PdfSigner</c> writes ahead of <c>/Contents</c> is fixed-width, and
    /// <c>/adbe.pkcs7.detached</c> and <c>/ETSI.CAdES.detached</c> are even the same length. A first
    /// attempt at this fixture varied <c>/Reason</c>, which <c>PdfSigner</c> writes <i>after</i> the
    /// placeholder, so the two preparations agreed to the byte and the test passed vacuously.</para>
    ///
    /// <para>Without the check the result is a file whose signature is simply broken — which reads
    /// as a signing failure rather than as the mix-up it is.</para>
    /// </remarks>
    [Fact]
    public void CompleteRefusesADescriptorFromADifferentPreparation()
    {
        var signer = NewSigner();
        var (pfx, password) = SigningTestCrypto.Pfx(signer.Leaf, signer.Ca.Cert);

        byte[] shortInput = SignedPdfFixtures.MinimalPdf();
        byte[] longInput = SignedPdfFixtures.Sign(shortInput, pfx, password, "Existing", FixedTime);

        var (prepared, mine) = Prepare(shortInput, signer);
        var (_, other) = Prepare(longInput, signer);

        // The fixture is only potent if the two placeholders really are in different places, and
        // both ends have to move or one half of the check is never exercised.
        Assert.NotEqual(mine.ContentsStart, other.ContentsStart);
        Assert.NotEqual(mine.ContentsEnd, other.ContentsEnd);

        var ex = Assert.Throws<PdfSigningException>(() =>
            Complete(prepared, other, new byte[16]));

        Assert.Contains("different Prepare", ex.Message);
    }

    /// <summary>
    /// <see cref="SignatureParameters.EmbedLtv"/> would wrap the placeholder in a revision that
    /// nothing signs, so it is refused rather than silently dropped.
    /// </summary>
    [Fact]
    public void PrepareRefusesEmbedLtv()
    {
        var signer = NewSigner();
        var p = Params();
        p.EmbedLtv = true;
        p.TimestampAuthorityUrl = null;

        var ex = Assert.Throws<PdfSigningException>(
            () => Prepare(SignedPdfFixtures.MinimalPdf(), signer, p));
        Assert.Contains("EmbedLtv", ex.Message);
    }

    /// <summary>
    /// A TSA URL would make phase one fetch a token over the network and throw it away, so it is
    /// refused. The refusal is what stops a caller believing their prepared signature is timestamped.
    /// </summary>
    [Fact]
    public void PrepareRefusesATimestampAuthorityUrl()
    {
        var signer = NewSigner();
        var p = Params();
        p.TimestampAuthorityUrl = "http://tsa.example.invalid/";

        var ex = Assert.Throws<PdfSigningException>(
            () => Prepare(SignedPdfFixtures.MinimalPdf(), signer, p));
        Assert.Contains("TimestampAuthorityUrl", ex.Message);
    }

    /// <summary>
    /// The chain is required: the signature dictionary names the certificate, so there is no
    /// signature to prepare without one.
    /// </summary>
    [Fact]
    public void PrepareRefusesAnEmptyCertificateChain()
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() => PdfDeferredSigner.Prepare(
            input, output, new List<byte[]>(), Params()));
        Assert.Contains("certificate chain", ex.Message);
    }

    // ------------------------------------------------------------------ parameter travel

    /// <summary>
    /// Every settable member of <see cref="SignatureParameters"/> reaches phase one except the four
    /// deliberately dropped ones.
    /// </summary>
    /// <remarks>
    /// The guard is against a member added later shaping a directly-signed document and silently not
    /// shaping a prepared one — which is the single failure this whole design is built to avoid, and
    /// which no end-to-end test would show, because both files would still verify.
    /// </remarks>
    [Fact]
    public void EveryParameterTravelsIntoPhaseOneExceptTheKeyMaterial()
    {
        var source = new SignatureParameters
        {
            CertificatePath = "ignored.pfx",
            CertificatePassword = "ignored",
            CertificateStream = new MemoryStream(),
            DigestAlgorithm = "SHA-512",
            SignatureAlgorithmOid = "1.2.840.113549.1.1.10",
            Reason = "r", Location = "l", ContactInfo = "c",
            SignatureFieldName = "Field",
            EstimatedSignatureSize = 4242,
            SigningTime = FixedTime,
            TimestampAuthorityUrl = "http://tsa.example.invalid/",
            TimestampDigestAlgorithm = "SHA-384",
            EmbedLtv = true,
            UseEtsiPadesSubFilter = true,
            DocMdpPermission = DocMdpPermission.NoChanges,
            UsageRights = UsageRights.FormFillIn,
        };

        var clone = PdfDeferredSigner.ForPreparation(source, new RecordingSigner(
            new List<byte[]>(), 1));

        foreach (var p in typeof(SignatureParameters).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite) continue;

            object? from = p.GetValue(source, null);
            object? to = p.GetValue(clone, null);

            if (Array.IndexOf(PdfDeferredSigner.ParametersNotCarried, p.Name) >= 0)
            {
                Assert.False(Equals(from, to),
                    $"{p.Name} is on the drop list but arrived unchanged, so the list does nothing.");
                continue;
            }

            Assert.Equal(from, to);
        }

        // The fixture has to differ from a default-constructed instance, or "it travelled" is a
        // statement about the defaults rather than about the copy.
        var fresh = new SignatureParameters();
        Assert.NotEqual(fresh.DigestAlgorithm, clone.DigestAlgorithm);
        Assert.NotEqual(fresh.EstimatedSignatureSize, clone.EstimatedSignatureSize);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Reads the four <c>/ByteRange</c> integers out of the raw file text, without a parser.
    /// </summary>
    private static long[] ReadRawByteRange(string text)
    {
        int at = text.IndexOf("/ByteRange", StringComparison.Ordinal);
        Assert.True(at >= 0, "the prepared file has no /ByteRange at all");
        Assert.Equal(-1, text.IndexOf("/ByteRange", at + 1, StringComparison.Ordinal));

        int open = text.IndexOf('[', at);
        int close = text.IndexOf(']', open);
        Assert.True(open > 0 && close > open, "/ByteRange is not an array");

        string[] parts = text.Substring(open + 1, close - open - 1)
                             .Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, parts.Length);

        var values = new long[4];
        for (int i = 0; i < 4; i++) values[i] = long.Parse(parts[i]);
        return values;
    }

    /// <summary>
    /// An external signer that records what it was asked to sign and returns a blob of the size it
    /// reserved, so a direct signing run can be compared against a prepared one.
    /// </summary>
    private sealed class RecordingSigner : IExternalSigner
    {
        private readonly IList<byte[]> _chain;

        internal RecordingSigner(IList<byte[]> chain, int reserve)
        {
            _chain = chain;
            EstimatedSignatureSize = reserve;
        }

        internal byte[]? Presented { get; private set; }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            Presented = data;
            return new byte[] { 0 };
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm)
            => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => _chain;

        public int EstimatedSignatureSize { get; }
    }
}
