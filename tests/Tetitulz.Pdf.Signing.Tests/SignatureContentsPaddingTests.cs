// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The <c>/Contents</c> hole is written to a fixed reserve and the CMS is shorter than it, so every
/// signature this library produces carries a run of padding after the signature object. These pin
/// what that padding is and what the CMS parser currently does with it.
/// </summary>
/// <remarks>
/// <para><b>Security.</b> The padding lies inside the <c>/ByteRange</c> gap, which is by
/// construction <b>not covered by the signature</b>. What the gap holds is read
/// (<c>SignedByteRanges.GapHoldsOnlyContents</c>), and a non-zero padding is reported
/// (<c>SignatureVerificationResult.HasNonZeroContentsPadding</c>).</para>
///
/// <para><b>Compatibility.</b> The four consumers of <c>PdfString.RawBytes</c> — signing, document
/// timestamps, counter-signatures and LTV refresh — hand the CMS parser <c>/Contents</c> through
/// <c>SigningDefaults.TrimToAsn1Object</c>, never the <b>padded</b> array: BouncyCastle 2.6.2
/// ignores a trailing region and 2.7.0 refuses it with <c>CmsException: Exception reading
/// content</c>.</para>
///
/// <para><b>The encoding is DER</b> (ISO 32000-2 §12.8.3.3.1;
/// <c>CmsSignatureGenerator.EncodeForContents</c>), so the length <i>is</i> in the header. BER with
/// an indefinite length would put <c>0x80</c> in <c>/Contents</c>' second byte and end the object at
/// end-of-contents octets — <c>00 00</c>, indistinguishable from the padding that follows.</para>
///
/// <para><b>A trim helper must not scan for zeros.</b> The signature is an OCTET STRING of
/// essentially random bytes, so its own last byte is zero about one time in 256 — and nothing
/// separates those zeros from the reserve's. <c>SigningDefaults.TrimToAsn1Object</c> reads the
/// object. <b>Nor may it read a length off the header by hand</b>: a DER length is there, but
/// reading it by hand re-implements an ASN.1 reader badly — which asserting <c>lengthOfLength</c>
/// in 1..4 catches.</para>
///
/// <para><b>These read raw bytes on purpose.</b> <c>PdfSigner</c> writes the signature dictionary
/// as raw bytes and the padding is not a modelled value, so a test going through the object reader
/// would be asking a parser about something the parser normalises away — CLAUDE.md's first blind
/// spot. The hex is located through the <c>/ByteRange</c> by scanning ASCII, which makes it a
/// second opinion rather than a restatement of the writer.</para>
/// </remarks>
public sealed class SignatureContentsPaddingTests
{
    /// <summary>
    /// A real signature's <c>/Contents</c> ends in a long run of zero bytes, preceded by a
    /// non-zero one — i.e. the reserve is padding, and the boundary is where the signature stops.
    /// </summary>
    /// <remarks>
    /// The run is required to be substantial rather than merely non-empty. A CMS can legitimately
    /// end in a zero byte or two, so "the last byte is zero" would be satisfied by a hole with no
    /// reserve at all; thousands of trailing zeros can only be the reserve.
    /// </remarks>
    [Fact]
    public void AContentsHoleEndsInALongUnsignedRunOfZeroBytes()
    {
        byte[] contents = SignedContents();

        int lastNonZero = Array.FindLastIndex(contents, b => b != 0);
        Assert.True(lastNonZero >= 0, "/Contents is entirely zero, so no signature was written");

        int padding = contents.Length - 1 - lastNonZero;
        Assert.True(padding > 1024,
            $"only {padding} trailing zero bytes in a {contents.Length}-byte hole; this fixture "
            + "cannot demonstrate the reserve");

        // The boundary is real: everything after lastNonZero is zero by construction, so the claim
        // that carries information is that the signature ends before the hole does.
        Assert.True(lastNonZero + 1 < contents.Length);
    }

    /// <summary>
    /// The bytes the production readers actually hand to the CMS parser — <c>/Contents</c> put
    /// through <c>SigningDefaults.TrimToAsn1Object</c> — parse, and carry the one signer.
    /// </summary>
    /// <remarks>
    /// <para><b>This asserts the invariant, not one library version's behaviour.</b> Whether the
    /// <i>padded</i> array parses is version-dependent, and that is the whole reason the trim
    /// exists: measured under both, BouncyCastle 2.6.2 ignores the trailing region while 2.7.0
    /// refuses it with <c>CmsException: Exception reading content</c> wrapping
    /// <c>IOException: extra data found after object</c>. A test of the padded array would pin one
    /// version's side of that, not the trim.</para>
    /// <para><b>The security half of the observation stands either way.</b> A parser that ignores
    /// the tail would leave anything parked in it — inside the <c>/ByteRange</c> gap, so unsigned —
    /// invisible to every verdict. It is reported:
    /// <c>SignatureVerificationResult.HasNonZeroContentsPadding</c> carries the fact
    /// unconditionally and <c>SignatureVerificationOptions.RejectNonZeroContentsPadding</c> turns
    /// it into a validation error, with <c>ContentsPaddingPolicyTests</c> holding that behaviour.
    /// These tests stay about the trim itself.</para>
    /// </remarks>
    [Fact]
    public void TheTrimmedContentsParsesAndCarriesTheSigner()
    {
        byte[] contents = SignedContents();
        Assert.True(contents[contents.Length - 1] == 0, "the fixture is not padded");

        var cms = new CmsSignedData(SigningDefaults.TrimToAsn1Object(contents));
        Assert.Single(cms.GetSignerInfos().GetSigners().Cast<SignerInformation>());
    }

    /// <summary>
    /// <b>The CMS in <c>/Contents</c> is DER: its length is declared in its header, and it is the
    /// length the object actually occupies.</b>
    /// </summary>
    /// <remarks>
    /// <para>ISO 32000-2 §12.8.3.3.1 requires a DER-encoded CMS object, and the CAdES/PAdES
    /// profiles say the same. BouncyCastle's default encoder does not produce it — it writes
    /// indefinite lengths (<c>30 80 … 00 00</c>), which is valid BER — so this library wrote BER
    /// until <c>CmsSignatureGenerator.EncodeForContents</c> existed.</para>
    ///
    /// <para><b>Nothing that reads the structure can see this, which is why it is asserted on the
    /// bytes.</b> Both forms parse to the same <c>SignedData</c>: every round trip, every
    /// verification and every assertion about what the signature <i>means</i> holds under either.
    /// Comparing two of our own outputs cannot see it either, since a change moves both. The
    /// declared length is the one thing only DER has, and comparing it against what an ASN.1 reader
    /// walks makes the claim "this header is honest" rather than merely "this byte is not 0x80".</para>
    /// </remarks>
    [Fact]
    public void TheContentsCmsIsDerWithAHeaderLengthThatMatchesTheObject()
    {
        byte[] contents = SignedContents();

        Assert.Equal(0x30, contents[0]);                    // SEQUENCE
        Assert.NotEqual(0x80, contents[1]);                 // not the indefinite form

        // A CMS is thousands of bytes, so DER spells the length in the long form: 0x80 | n, then n
        // big-endian bytes. Anything else here would mean a signature small enough to be suspect.
        Assert.True(contents[1] > 0x80, "the CMS is too short for a long-form length; is it real?");

        int lengthBytes = contents[1] & 0x7F;
        Assert.InRange(lengthBytes, 1, 4);

        int declared = 0;
        for (int i = 0; i < lengthBytes; i++) declared = (declared << 8) | contents[2 + i];

        Assert.Equal(Asn1ObjectLength(contents), 2 + lengthBytes + declared);
    }

    /// <summary>
    /// Trimming at the <b>last non-zero byte</b> can cut into the object; trimming at the length an
    /// <b>ASN.1 reader</b> reports cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the trap, and it is the reason a trim helper cannot scan for zeros.</b> The
    /// obvious implementation of "drop the padding" — walk back from the end while the bytes are
    /// zero — cannot tell the object's own trailing zeros from the reserve's, because nothing
    /// separates them.</para>
    ///
    /// <para><b>The demonstration is a hand-built object rather than a signature, and that is the
    /// point of this version of the test.</b> While the CMS was written as BER it ended in
    /// end-of-contents octets — <c>00 00</c> — so every signature exhibited the trap and a real one
    /// could stand as the fixture. Now that it is written as DER (§12.8.3.3.1's requirement; see
    /// <c>CmsSignatureGenerator.EncodeForContents</c>) the last byte is whatever the signature's own
    /// last byte is, which is zero about one time in 256. The hazard is unchanged and the
    /// <i>fixture</i> is what broke: a signature would now assert this intermittently, which is
    /// worse than not asserting it. Seven bytes that provably end in a zero make the same claim
    /// every run.</para>
    ///
    /// <para>So the two halves are asserted together. Anyone who replaces the ASN.1 read with a
    /// cheaper scan has to make the first half pass, and it cannot.</para>
    /// </remarks>
    [Fact]
    public void TrimmingAtTheLastNonZeroByteCanCutIntoTheObject_TrimmingAtTheAsn1LengthCannot()
    {
        // SEQUENCE { OCTET STRING { 01 02 00 } } — a well-formed DER object whose final content
        // byte is zero, which is the case a zero-scanning trim gets wrong.
        byte[] endsInZero = { 0x30, 0x05, 0x04, 0x03, 0x01, 0x02, 0x00 };
        byte[] padded = endsInZero.Concat(new byte[64]).ToArray();

        int lastNonZero = Array.FindLastIndex(padded, b => b != 0);
        Assert.True(lastNonZero + 1 < endsInZero.Length,
            "the fixture does not end in a zero byte, so it cannot demonstrate the trap");

        // The scan would hand a parser a truncated object; the ASN.1 read hands it the whole one.
        Assert.Equal(endsInZero.Length, SigningDefaults.TrimToAsn1Object(padded).Length);
        Assert.Equal(endsInZero, SigningDefaults.TrimToAsn1Object(padded));

        // And on a real signature the trim is exact: what it returns is the object and nothing else.
        byte[] contents = SignedContents();
        int asn1Length = Asn1ObjectLength(contents);
        Assert.True(asn1Length < contents.Length, "nothing was trimmed, so this proves nothing");
        Assert.Equal(asn1Length, SigningDefaults.TrimToAsn1Object(contents).Length);

        var cms = new CmsSignedData(Prefix(contents, asn1Length));
        Assert.Single(cms.GetSignerInfos().GetSigners().Cast<SignerInformation>());
    }

    // ------------------------------------------------------------------

    private static byte[] Prefix(byte[] source, int length)
    {
        var result = new byte[length];
        Array.Copy(source, result, length);
        return result;
    }

    /// <summary>
    /// A signature verifies whatever length of padding the reserve left behind it.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the property 2.7.0's strictness actually threatened, and nothing asserted
    /// it.</b> Every other fixture in this suite signs at the one default reserve, so "a signature
    /// verifies" and "a signature verifies with 15 KB of zeros after it" were the same test. The
    /// reserve is a caller-set number, and a document in the wild carries whatever its producer
    /// chose.</para>
    ///
    /// <para><b>The distinct-lengths assertion is the load-bearing one.</b> Three reserves that
    /// happened to produce the same padding would be one case run three times — and since
    /// <c>SignatureParameters</c> silently raises a reserve below the minimum, that is not a
    /// hypothetical.</para>
    /// </remarks>
    [Fact]
    public void ASignatureVerifiesWhateverPaddingTheReserveLeftBehindIt()
    {
        var root = SigningTestCrypto.Ca("CN=Reserve Independence Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Reserve Independence Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        var paddingLengths = new System.Collections.Generic.List<int>();

        foreach (int reserve in new[] { 20000, 32768, 60000 })
        {
            byte[] signed = SignWithReserve(pfx, pw, reserve);
            byte[] contents = ContentsOf(signed);

            paddingLengths.Add(contents.Length - Asn1ObjectLength(contents));

            using var ms = new System.IO.MemoryStream(signed, writable: false);
            using Tetitulz.Pdf.Core.Document.PdfDocument doc =
                Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

            var results = new Tetitulz.Pdf.Signing.PdfSignatureVerifier().VerifyAll(
                doc, new Tetitulz.Pdf.Signing.Models.SignatureVerificationOptions
                {
                    TrustAnchors = new[] { root.Cert },
                });

            Assert.True(results.Count == 1,
                $"reserve {reserve} produced {results.Count} signatures");
            Assert.True(results[0].IsIntegrityValid,
                $"a signature reserved at {reserve} bytes did not verify");
            Assert.False(results[0].HasNonZeroContentsPadding);
        }

        Assert.Equal(3, paddingLengths.Distinct().Count());
        Assert.All(paddingLengths, n => Assert.True(n > 0, "a reserve left no padding at all"));
    }

    /// <summary>Signs the minimal fixture with an explicit <c>/Contents</c> reserve.</summary>
    private static byte[] SignWithReserve(byte[] pfx, string password, int reserve)
    {
        using var input = new System.IO.MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new System.IO.MemoryStream();

        new Tetitulz.Pdf.Signing.PdfSigner().Sign(input, output,
            new Tetitulz.Pdf.Signing.Models.SignatureParameters
            {
                CertificateStream = new System.IO.MemoryStream(pfx),
                CertificatePassword = password,
                SignatureFieldName = "Signature1",
                EstimatedSignatureSize = reserve,
                Reason = "reserve independence",
            });

        return output.ToArray();
    }

    /// <summary>
    /// Length of the first ASN.1 object in <paramref name="data"/>, from an ASN.1 reader rather
    /// than from the header — which is what the production trim does, so this measures the same
    /// thing rather than a second opinion about it.
    /// </summary>
    private static int Asn1ObjectLength(byte[] data)
    {
        using var ms = new System.IO.MemoryStream(data, writable: false);
        using var ais = new Org.BouncyCastle.Asn1.Asn1InputStream(ms);
        Org.BouncyCastle.Asn1.Asn1Object obj = ais.ReadObject();
        Assert.NotNull(obj);
        return (int)ms.Position;
    }

    // ------------------------------------------------------------------

    /// <summary>Signs the minimal fixture and returns the decoded <c>/Contents</c> bytes.</summary>
    private static byte[] SignedContents()
    {
        var root = SigningTestCrypto.Ca("CN=Contents Padding Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Contents Padding Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        return ContentsOf(SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw));
    }

    /// <summary>The decoded <c>/Contents</c> bytes of a signed file.</summary>
    private static byte[] ContentsOf(byte[] signed)
    {
        // Located through the /ByteRange rather than by matching "/Contents", because the gap the
        // signature excludes IS the /Contents string and its delimiters — already pinned by
        // SignatureByteRangeTests, so this reuses an established fact rather than a fresh guess.
        string ascii = Encoding.ASCII.GetString(signed);
        Match m = Regex.Match(ascii, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(m.Success, "the fixture produced no /ByteRange, so it is not a signed file");

        int gapStart = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int gapEnd = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

        Assert.Equal((byte)'<', signed[gapStart]);
        Assert.Equal((byte)'>', signed[gapEnd - 1]);

        string hex = ascii.Substring(gapStart + 1, gapEnd - gapStart - 2);
        Assert.True(hex.Length % 2 == 0, "the /Contents hex has an odd number of digits");

        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture);
        return bytes;
    }
}
