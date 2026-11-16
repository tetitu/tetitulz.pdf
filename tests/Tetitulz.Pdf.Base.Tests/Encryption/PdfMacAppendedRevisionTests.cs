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
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// ISO/TS 32004 PDF MAC outside <see cref="PdfEncryptor"/>'s whole-file write: a revision appended by
/// <see cref="PdfEncryptedIncrementalUpdate"/> to a MAC-protected document carries a fresh standalone
/// token covering the whole file (§6.5.1 — the old one stops covering it, which Annex B.2.2.2 reads as
/// possible tampering), and <see cref="PdfEncryptedRoundTrip.TryDeriveParameters"/> keeps the protection.
/// </summary>
public class PdfMacAppendedRevisionTests
{
    private const string UserPassword = "user-secret";
    private const string OwnerPassword = "owner-secret";

    /// <summary>
    /// An encrypted fixture, AES-256 unless a test names another algorithm. Through the internal
    /// door with the refusal off: a token below <c>/V 5</c> is what another producer may have
    /// written and the public door refuses to, and some fixtures here are that document.
    /// </summary>
    private static byte[] Encrypt(bool pdfMac, EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("appended-mac"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
            EnablePdfMac = pdfMac,
        }, refuseTokenBelowVersion5: false);
        return output.ToArray();
    }

    private static byte[] AppendOneObject(byte[] encrypted, bool withObject = true)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(input, output, UserPassword, (doc, firstFree) => withObject
            ? new[] { new IndirectObject(firstFree, 0, new PdfString("appended under a MAC")) }
            : Array.Empty<IndirectObject>());
        return output.ToArray();
    }

    /// <summary>The newest <c>/AuthCode</c>'s byte range and token, read from the raw bytes.</summary>
    private static (long[] Range, byte[] Token) NewestAuthCode(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int authCode = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        Assert.True(authCode >= 0, "no /AuthCode was written");

        int open = text.IndexOf('[', text.IndexOf("/ByteRange", authCode, StringComparison.Ordinal));
        int close = text.IndexOf(']', open);
        long[] range = text.Substring(open + 1, close - open - 1)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
        byte[] token = Convert.FromHexString(text.Substring((int)range[1] + 1, (int)(range[2] - range[1]) - 2));
        return (range, token);
    }

    [Fact]
    public void AppendedRevision_OfAMacProtectedDocument_CarriesATokenCoveringTheWholeFile()
    {
        byte[] original = Encrypt(pdfMac: true);
        byte[] appended = AppendOneObject(original);

        Assert.True(appended.Length > original.Length);
        Assert.True(appended.Take(original.Length).SequenceEqual(original), "the original bytes were changed");

        (long[] range, byte[] token) = NewestAuthCode(appended);
        Assert.True(range[1] >= original.Length, "the newest /AuthCode is the original revision's");
        Assert.Equal(0, range[0]);
        Assert.Equal((byte)'<', appended[range[1]]);
        Assert.Equal((byte)'>', appended[range[2] - 1]);
        Assert.Equal(appended.Length, range[2] + range[3]);

        byte[] covered = appended.Take((int)range[1]).Concat(appended.Skip((int)range[2])).ToArray();
        var authenticatedData = AuthenticatedData.GetInstance(ContentInfo.GetInstance(Asn1Object.FromByteArray(token)).Content);
        byte[] integrityInfo = Asn1OctetString.GetInstance(authenticatedData.EncapsulatedContentInfo.Content).GetOctets();
        byte[] dataDigest = Asn1OctetString.GetInstance(Asn1Sequence.GetInstance(integrityInfo)[1]).GetOctets();
        Assert.Equal(SHA256.HashData(covered), dataDigest);
    }

    /// <summary>
    /// The token is bound to the document's own key: the KEK derived from the file key and the
    /// unchanged <c>/KDFSalt</c> unwraps the MAC key, which recomputes the token's MAC. Under
    /// AES-256, where the standard has a token, and under AES-128, where another producer's
    /// document may carry one and an appended revision must not leave it stale.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES256)]
    [InlineData(EncryptionAlgorithm.AES128)]
    public void AppendedRevisionToken_VerifiesUnderTheDocumentsFileKeyAndSalt(EncryptionAlgorithm algorithm)
    {
        byte[] appended = AppendOneObject(Encrypt(pdfMac: true, algorithm));

        using var doc = PdfDocument.Open(new MemoryStream(appended));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        byte[] kdfSalt = (encryptDict[PdfName.KDFSalt] as PdfString).RawBytes;
        byte[] fileKey = PdfDecryptor.ResolveEncryption(doc, UserPassword).Key;

        var authenticatedData = AuthenticatedData.GetInstance(
            ContentInfo.GetInstance(Asn1Object.FromByteArray(NewestAuthCode(appended).Token)).Content);
        var recipient = PasswordRecipientInfo.GetInstance(RecipientInfo.GetInstance(authenticatedData.RecipientInfos[0]).Info);

        byte[] macKey = PdfMacToken.UnwrapKey(PdfMacToken.DeriveKek(fileKey, kdfSalt), recipient.EncryptedKey.GetOctets());
        byte[] recomputed = PdfMacToken.ComputeHmacSha256(macKey, authenticatedData.AuthAttrs.GetEncoded(Asn1Encodable.Der));
        Assert.Equal(authenticatedData.Mac.GetOctets(), recomputed);
    }

    [Fact]
    public void AppendedRevision_OfADocumentWithoutPdfMac_CarriesNoAuthCode()
    {
        byte[] appended = AppendOneObject(Encrypt(pdfMac: false));
        Assert.DoesNotContain("/AuthCode", Encoding.Latin1.GetString(appended));
    }

    [Fact]
    public void NothingAppended_ToAMacProtectedDocument_LeavesItsBytesAndTokenUnchanged()
    {
        byte[] original = Encrypt(pdfMac: true);
        Assert.Equal(original, AppendOneObject(original, withObject: false));
    }

    [Fact]
    public void AppendedRevision_StillOpensWithTheUserPassword()
    {
        byte[] appended = AppendOneObject(Encrypt(pdfMac: true, EncryptionAlgorithm.AES256));

        using var input = new MemoryStream(appended);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, UserPassword);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Contains("appended-mac", Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()));
    }

    /// <summary>
    /// A PDF whose trailer names an <c>/Encrypt</c> dictionary with <paramref name="p"/> and, when given,
    /// a <c>/KDFSalt</c> of <paramref name="saltBytes"/> bytes — enough for the protection decision, which
    /// reads dictionaries and derives no key.
    /// </summary>
    private static PdfDocument EncryptDictionaryFixture(int p, int saltBytes, bool trailerAuthCode)
    {
        string salt = saltBytes > 0 ? " /KDFSalt <" + new string('A', saltBytes * 2) + ">" : "";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Filter /Standard /V 5 /R 6 /P {p}{salt} >>",
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
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R /ID [<00> <00>]")
          .Append(trailerAuthCode ? " /AuthCode << /MACLocation /Standalone >>" : "")
          .Append(" >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    [Theory]
    [InlineData(-4, 32, false, false)]          // bit 13 set, no token: never protected
    [InlineData(-4 & ~0x1000, 32, false, true)] // bit 13 clear: a token is required in every revision
    [InlineData(-4, 32, true, true)]            // a token already present
    [InlineData(-4 & ~0x1000, 16, true, false)] // a salt of the wrong size cannot key one
    [InlineData(-4 & ~0x1000, 0, true, false)]  // no salt at all
    public void ProtectionDecision_NeedsA32ByteSaltAndEitherBit13ClearOrAToken(
        int p, int saltBytes, bool trailerAuthCode, bool protectedByMac)
    {
        using PdfDocument doc = EncryptDictionaryFixture(p, saltBytes, trailerAuthCode);
        Assert.Equal(protectedByMac, PdfMacTokenSeal.SaltWhenProtected(doc) != null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoundTripDerivedParameters_KeepPdfMacExactlyWhenTheInputHasIt(bool pdfMac)
    {
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(Encrypt(pdfMac)));

        Assert.True(PdfEncryptedRoundTrip.TryDeriveParameters(info,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword }, out EncryptionParameters parameters, out string reason), reason);
        Assert.Equal(pdfMac, parameters.EnablePdfMac);
    }

    /// <summary>
    /// A token below <c>/V 5</c> is neither written again nor dropped by a re-encryption: the
    /// derivation fails and says which policies answer it. <c>KeepOriginal</c> writes the document's
    /// own dictionary and reseals under its own key, so it still derives.
    /// </summary>
    [Fact]
    public void RoundTripDerivedParameters_ForATokenBelowVersion5_AreRefusedUnlessTheOriginalIsKept()
    {
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(
            new MemoryStream(Encrypt(pdfMac: true, EncryptionAlgorithm.AES128)));

        Assert.False(PdfEncryptedRoundTrip.TryDeriveParameters(info,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword }, out _, out string reason));
        Assert.Contains("at least 5", reason, StringComparison.Ordinal);
        Assert.Contains("KeepOriginal", reason, StringComparison.Ordinal);

        Assert.True(PdfEncryptedRoundTrip.TryDeriveParameters(info,
            new EncryptedRoundTripOptions { Policy = ReEncryptionPolicy.KeepOriginal, OpenPassword = UserPassword },
            out EncryptionParameters kept, out string keptReason), keptReason);
        Assert.True(kept.EnablePdfMac);
        Assert.Equal(EncryptionAlgorithm.AES128, kept.Algorithm);
    }

    /// <summary>Explicit parameters asking for a token below <c>/V 5</c> are refused before anything runs.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128, false)]
    [InlineData(EncryptionAlgorithm.AES256, true)]
    public void RoundTripExplicitParameters_AreHeldToTheSameRule(EncryptionAlgorithm algorithm, bool accepted)
    {
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(Encrypt(pdfMac: false)));
        var options = new EncryptedRoundTripOptions
        {
            Policy = ReEncryptionPolicy.Explicit,
            ExplicitParameters = new EncryptionParameters
            {
                UserPassword = UserPassword, OwnerPassword = OwnerPassword, Algorithm = algorithm, EnablePdfMac = true,
            },
        };

        Assert.Equal(accepted, PdfEncryptedRoundTrip.TryDeriveParameters(info, options, out _, out string reason));
        if (!accepted) Assert.Contains("at least 5", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripDerivedParameters_ASaltWithBit13Set_DoNotEnablePdfMac()
    {
        // The salt alone is not the requirement; §5.1.2's cleared bit is — and so is a token already in
        // the trailer (§6.5.1 binds any document "with a PDF MAC token"), which this fixture carries
        // because Encrypt(pdfMac: true) seals it. The claim here is about a salt with bit 13 set and
        // NO token, so the token is taken away too; PdfMacProtectionDecisionTests pins the other rows.
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(new MemoryStream(Encrypt(pdfMac: true)));
        info.PValue |= 0x1000;
        info.TrailerHasAuthCode = false;

        Assert.True(PdfEncryptedRoundTrip.TryDeriveParameters(info,
            new EncryptedRoundTripOptions { OpenPassword = UserPassword }, out EncryptionParameters parameters, out string reason), reason);
        Assert.False(parameters.EnablePdfMac);
    }
}
