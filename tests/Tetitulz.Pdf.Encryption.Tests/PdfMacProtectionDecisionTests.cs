// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using ContentInfo = Org.BouncyCastle.Asn1.Cms.ContentInfo;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// "Is this document protected by a PDF MAC?" (ISO/TS 32004) had two answers in this assembly, and one document could get
/// both: <see cref="PdfEncryptedIncrementalUpdate"/> sealed its appended revision, while <see cref="PdfEncryptedRoundTrip"/>
/// re-encrypted it with no token at all.
/// </summary>
/// <remarks>
/// <para><b>The two answers.</b> The appended-revision seal said yes for a 32-byte <c>/KDFSalt</c> with permission bit 13
/// clear <i>or</i> an <c>/AuthCode</c> already in the trailer. The round trip read the salt and the bit only —
/// <see cref="EncryptionInfo"/> carried nothing from the trailer — so a document holding a token with bit 13 set lost the
/// token on every round trip. The two also read an unreadable <c>/P</c> differently.</para>
/// <para><b>Which answer is right.</b> ISO/TS 32004 makes bit 13 the <i>requirement</i>, not the definition of use. Table 3:
/// "When zero, indicates that a PDF MAC token is required to be present in all revisions of the document." Table 2 calls
/// <c>/KDFSalt</c> "required in documents that make use of PDF MAC"; §6.5.1 applies to any "document with a PDF MAC token":
/// "If the most recent revision in a document with a PDF MAC token is an unsigned revision, then the ByteRange of the
/// AuthCode dictionary shall cover the entire PDF file"; and Annex B.2.2.2 reads a token that stops short as one that "has
/// become stale, e.g. because another PDF processor failed to apply a new MAC after incrementally updating the document".
/// A document carrying a token uses PDF MAC whatever bit 13 says. Writing it back without one removes integrity protection
/// the input had — the silent downgrade <see cref="PdfEncryptedRoundTrip.TryDeriveParameters"/> says it refuses to
/// make.</para>
/// </remarks>
public class PdfMacProtectionDecisionTests
{
    private const int Bit13 = 0x1000;
    private const string UserPassword = "user-secret";

    [Theory]
    [InlineData(-4, 32, false, false)]            // bit 13 set, no token: the salt alone is not a use of PDF MAC
    [InlineData(-4 & ~Bit13, 32, false, true)]    // bit 13 clear: a token is required in every revision (Table 3)
    [InlineData(-4, 32, true, true)]              // a token present, bit 13 set: §6.5.1, Annex B.2.2.2
    [InlineData(-4 & ~Bit13, 32, true, true)]
    [InlineData(-4 & ~Bit13, 16, true, false)]    // Table 2's salt is 32 bytes; no other length keys a token (§6.4)
    [InlineData(-4, 0, true, false)]              // no salt: nothing to key a token with
    // /P unreadable. Read as 0, as EncryptionInfo.PValue and the key derivation both read it, so bit 13 reads clear and the
    // protection is kept. ValidatePerms refuses that reading on purpose — there 0 would accuse an untouched file of
    // tampering — but here it only keeps a token.
    [InlineData(null, 32, false, true)]
    public void AppendedRevisionAndRoundTrip_GiveTheSameAnswer(
        int? permissions, int saltBytes, bool trailerAuthCode, bool protectedByMac)
    {
        byte[] pdf = EncryptDictionaryOnly(permissions, saltBytes, trailerAuthCode);

        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            Assert.Equal(protectedByMac, PdfMacTokenSeal.SaltWhenProtected(doc) != null);

        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(pdf));
        Assert.True(PdfEncryptedRoundTrip.TryDeriveParameters(info, new EncryptedRoundTripOptions(),
            out EncryptionParameters parameters, out string reason), reason);
        Assert.Equal(protectedByMac, parameters.EnablePdfMac);
    }

    [Fact]
    public void RoundTrip_OfADocumentHoldingATokenWithBit13Set_WritesATokenCoveringItsOutput()
    {
        using var input = new MemoryStream(TokenBearingDocumentWithBit13Set());
        using var output = new MemoryStream();

        RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword }, (plain, produced) => plain.CopyTo(produced));

        Assert.True(result.Succeeded, result.FailureReason);
        AssertNewestTokenCoversTheWholeFile(output.ToArray());
    }

    /// <summary>The other half of the reported divergence, on the same document: an appended revision is sealed.</summary>
    [Fact]
    public void AppendedRevision_OfADocumentHoldingATokenWithBit13Set_CarriesATokenCoveringTheWholeFile()
    {
        byte[] original = TokenBearingDocumentWithBit13Set();
        using var output = new MemoryStream();

        PdfEncryptedIncrementalUpdate.Run(new MemoryStream(original), output, UserPassword,
            (doc, firstFree) => new[] { new IndirectObject(firstFree, 0, new PdfString("appended under a MAC")) });

        long[] range = AssertNewestTokenCoversTheWholeFile(output.ToArray());
        Assert.True(range[1] > original.Length, "the newest /AuthCode is still the original revision's");
    }

    /// <summary>
    /// A trailer naming an <c>/Encrypt</c> dictionary with the given <c>/P</c> (absent when null) and <c>/KDFSalt</c>
    /// length, and optionally an <c>/AuthCode</c> — enough for both answers, neither of which derives a key.
    /// </summary>
    private static byte[] EncryptDictionaryOnly(int? permissions, int saltBytes, bool trailerAuthCode)
    {
        string p = permissions.HasValue ? $" /P {permissions.Value}" : "";
        string salt = saltBytes > 0 ? " /KDFSalt <" + new string('A', saltBytes * 2) + ">" : "";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Filter /Standard /V 5 /R 6{p}{salt} >>",
        };

        var sb = new StringBuilder("%PDF-2.0\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R /ID [<00> <00>]")
          .Append(trailerAuthCode ? " /AuthCode << /MACLocation /Standalone >>" : "")
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// An AES-256 document <see cref="PdfEncryptor"/> sealed with a PDF MAC token, then given permission bit 13 set, a
    /// <c>/Perms</c> that authenticates the new <c>/P</c>, and a token resealed over the edited bytes: a document that
    /// carries a token without requiring one in every revision, coherent in every byte a validator reads.
    /// </summary>
    private static byte[] TokenBearingDocumentWithBit13Set()
    {
        byte[] pdf;
        using (var source = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("mac-protection-decision")))
        using (var sealedByEncryptor = new MemoryStream())
        {
            PdfEncryptor.Encrypt(source, sealedByEncryptor, new EncryptionParameters
            {
                UserPassword = UserPassword,
                OwnerPassword = "owner-secret",
                Algorithm = EncryptionAlgorithm.AES256,
                Permissions = PdfPermissions.None,
                EnablePdfMac = true,
            });
            pdf = sealedByEncryptor.ToArray();
        }

        byte[] fileKey, salt, perms;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            fileKey = PdfDecryptor.ResolveEncryption(doc, UserPassword).Key;
            salt = Assert.IsType<PdfString>(doc.EncryptionDictionary[PdfName.KDFSalt]).RawBytes;
            perms = Assert.IsType<PdfString>(doc.EncryptionDictionary[PdfName.Perms]).RawBytes;
        }

        // PdfPermissions.None is /P -3904; the encryptor cleared bit 13 for the MAC, giving -8000. Set it again — the same
        // number of bytes, so no offset in the file moves.
        const int P = -3904;
        ReplaceOnce(pdf, "/P -8000", "/P " + P);

        // ISO 32000-2 Algorithm 10: /P extended to 64 bits, "T" (metadata is encrypted), "adb", four ignored bytes, then
        // AES-256 in ECB mode under the file key. The file key does not depend on /P for revision 6, so it still opens.
        var block = new byte[16];
        for (int i = 0; i < 8; i++) block[i] = (byte)((long)P >> (8 * i));
        block[8] = (byte)'T';
        block[9] = (byte)'a';
        block[10] = (byte)'d';
        block[11] = (byte)'b';
        using (var aes = Aes.Create())
        {
            aes.Key = fileKey;
            ReplaceOnce(pdf, "/Perms <" + Convert.ToHexString(perms) + ">",
                "/Perms <" + Convert.ToHexString(aes.EncryptEcb(block, PaddingMode.None)) + ">");
        }

        byte[] result;
        using (var resealed = new MemoryStream())
        {
            resealed.Write(pdf, 0, pdf.Length);
            new PdfMacTokenSeal(fileKey, salt).Seal(resealed, TestPdfs.LegacyRc4.XrefOffsetOf(pdf));
            result = resealed.ToArray();
        }

        // The fixture is what it claims to be.
        using (var doc = PdfDocument.Open(new MemoryStream(result)))
            Assert.NotEqual(0, PdfPermissionValue.FromNumber(doc.EncryptionDictionary[PdfName.P])!.Value & Bit13);
        using (var plain = new MemoryStream())
            PdfDecryptor.Decrypt(new MemoryStream(result), plain, UserPassword);
        Assert.Equal("OK", PdfDecryptor.LastPermsValidation);
        AssertNewestTokenCoversTheWholeFile(result);

        return result;
    }

    /// <summary>
    /// Asserts that the newest <c>/AuthCode</c>'s <c>/ByteRange</c> ends at the end of <paramref name="pdf"/> and that its
    /// token's data digest is the SHA-256 of exactly those bytes, and returns the range.
    /// </summary>
    private static long[] AssertNewestTokenCoversTheWholeFile(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int authCode = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        Assert.True(authCode >= 0, "no /AuthCode was written");

        int open = text.IndexOf('[', text.IndexOf("/ByteRange", authCode, StringComparison.Ordinal));
        int close = text.IndexOf(']', open);
        long[] range = text.Substring(open + 1, close - open - 1)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();

        // Coverage before anything is sliced: an /AuthCode carried over from another file names that file's offsets.
        Assert.True(range.Length == 4 && range[0] == 0 && range[1] < range[2] && range[2] + range[3] == pdf.Length,
            $"the newest /AuthCode's /ByteRange [{string.Join(" ", range)}] does not cover this {pdf.Length}-byte file");

        byte[] token = Convert.FromHexString(text.Substring((int)range[1] + 1, (int)(range[2] - range[1]) - 2));
        byte[] covered = pdf.Take((int)range[1]).Concat(pdf.Skip((int)range[2])).ToArray();
        Assert.Equal(SHA256.HashData(covered), DataDigest(token));
        return range;
    }

    private static byte[] DataDigest(byte[] token)
    {
        var authenticatedData = AuthenticatedData.GetInstance(
            ContentInfo.GetInstance(Asn1Object.FromByteArray(token)).Content);
        byte[] integrityInfo = Asn1OctetString.GetInstance(authenticatedData.EncapsulatedContentInfo.Content).GetOctets();
        return Asn1OctetString.GetInstance(Asn1Sequence.GetInstance(integrityInfo)[1]).GetOctets();
    }

    private static void ReplaceOnce(byte[] pdf, string find, string replacement)
    {
        Assert.Equal(find.Length, replacement.Length);
        string text = Encoding.Latin1.GetString(pdf);
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(find, at + 1, StringComparison.Ordinal) < 0,
            $"expected exactly one occurrence of '{find}'");
        Encoding.ASCII.GetBytes(replacement).CopyTo(pdf, at);
    }
}
