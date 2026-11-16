// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Checking an ISO/TS 32004 PDF MAC token against the document it claims to protect — both
/// placements (GAPS.md §22b).
/// </summary>
/// <remarks>
/// <para><b>Writing a token and checking it are not the same claim, and only the second one is
/// falsifiable by a document.</b> This library wrote tokens for some time with nothing able to read
/// one back, so the digests it computed were asserted only against themselves. These tests are what
/// make a wrong digest, a wrong key or a swapped payload visible.</para>
///
/// <para><b>Every failing case alters a document that verified a moment earlier</b>, so the fixture
/// is proved potent by the passing case beside it rather than by inspection.</para>
/// </remarks>
public sealed class PdfMacVerificationTests
{
    private const string Password = "mac-verify-3k";

    [Fact]
    public void AStandaloneTokenVerifiesAgainstTheDocumentItSeals()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);

        PdfMacVerification result = Verify(sealedDoc);

        Assert.True(result.IsProtected);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal("Standalone", result.Location);
        Assert.Null(result.Error);
    }

    /// <summary>
    /// The token written as a literal string takes half the gap the <c>/ByteRange</c> leaves out, and
    /// the rest — bytes no digest covers — closes <c>/AuthCode</c> and adds a trailer key the parser
    /// obeys; the digest, the coverage and the HMAC are all unchanged. The gap is read, not sized: it
    /// holds exactly '&lt;', the token's hex digits and '&gt;' (ISO/TS 32004 Table 6).
    /// </summary>
    [Fact]
    public void AdversarialTokenRewrittenAsALiteralString_WithTrailerKeysInTheRest_IsReported()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        string text = Encoding.Latin1.GetString(sealedDoc);
        int lt = text.LastIndexOf("/MAC <", StringComparison.Ordinal) + "/MAC ".Length;
        int gt = text.IndexOf('>', lt);
        byte[] token = Convert.FromHexString(text.Substring(lt + 1, gt - lt - 1));
        var spliced = new StringBuilder("(");
        foreach (byte b in token)
        {
            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\') spliced.Append('\\');
            spliced.Append((char)b);
        }
        spliced.Append(")>>/Zz<<");
        Assert.True(spliced.Length <= gt - lt + 1, "the literal form must fit the hex form's gap");
        string gap = spliced.ToString().PadRight(gt - lt + 1);
        byte[] tampered = Encoding.Latin1.GetBytes(text.Substring(0, lt) + gap + text.Substring(gt + 1));

        using (var view = PdfDocument.Open(new MemoryStream(tampered)))
            Assert.NotNull(view.Trailer[new PdfName("Zz")]);   // the splice is a trailer key the parser obeys

        PdfMacVerification result = Verify(tampered);
        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("Table 6", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A byte changed inside the covered range is caught by <c>dataDigest</c> — the property the
    /// whole mechanism exists for.
    /// </summary>
    [Fact]
    public void AByteChangedInsideTheCoveredRangeIsReported()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        PdfMacVerification result = Verify(WithAlteredHeaderVersion(sealedDoc));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("dataDigest", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A token that stops short of the end of the file is reported, even though its digest still
    /// matches.</b>
    /// </summary>
    /// <remarks>
    /// <para>This is the case a digest comparison structurally cannot catch. Appending bytes leaves
    /// everything the <c>/ByteRange</c> names exactly where it was, so the digest over that range is
    /// unchanged and the token verifies — against a prefix of a document that is now longer.
    /// ISO/TS 32004 §6.5.1 requires the range to cover the entire file, and Annex B.2.2.2 reads a
    /// short one as tampering "or … that the PDF MAC token has become stale, e.g. because another
    /// PDF processor failed to apply a new MAC after incrementally updating the document."</para>
    ///
    /// <para><b>Found by mutation, in the obvious place and the wrong direction.</b> A test
    /// asserting that an appended revision keeps its token valid passed with the resealing deleted —
    /// because the stale token still verified. The test's name claimed more than its body checked,
    /// and the reason was a missing check rather than a weak assertion.</para>
    /// </remarks>
    [Fact]
    public void ATokenThatStopsShortOfTheEndOfTheFileIsReported()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before bytes are added");

        // Appended after everything the range names, so the digest is untouched and only the
        // extent is wrong. More than the one EOL marker §12.8.1 allows a range to stop short of.
        byte[] extended = sealedDoc.Concat(Encoding.ASCII.GetBytes("\n% appended by something else\n")).ToArray();

        PdfMacVerification result = Verify(extended);

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("stops before the end of the file", result.Error, StringComparison.Ordinal);

        // And not for the digest's sake: the message names coverage, which is the distinction the
        // check exists to make.
        Assert.DoesNotContain("dataDigest", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The token is keyed on the file encryption key, so another document's token does not verify
    /// here even though it is a perfectly well-formed token.
    /// </summary>
    [Fact]
    public void ATokenFromAnotherDocumentDoesNotVerify()
    {
        byte[] mine = Encrypt(MinimalPdf(), pdfMac: true);
        byte[] theirs = Encrypt(MinimalPdf("Another document entirely"), pdfMac: true);

        byte[] transplanted = WithMacOf(mine, theirs);

        PdfMacVerification result = Verify(transplanted);

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// <b>An encrypted document with no token is unprotected, not invalid.</b>
    /// </summary>
    /// <remarks>
    /// Collapsing the two would report every pre-PDF-2.0 encrypted document as tampered with, which
    /// is the failure mode a caller would learn to ignore — and then miss a real one.
    /// </remarks>
    [Fact]
    public void AnEncryptedDocumentWithoutATokenIsReportedUnprotectedRatherThanInvalid()
    {
        PdfMacVerification result = Verify(Encrypt(MinimalPdf(), pdfMac: false));

        Assert.False(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Null(result.Error);
        Assert.Null(result.Location);
    }

    /// <summary>
    /// <b>A token removed from the trailer is tampering, not absence.</b> The trailer is plain
    /// text: deleting the <c>/AuthCode</c> entry needs no key. The document still says, in a
    /// permission word <c>/Perms</c> authenticates, that every revision carries a token — so the
    /// answer is "invalid", where reading the trailer alone answers "unprotected".
    /// </summary>
    [Fact]
    public void ATokenRemovedFromTheTrailerIsReported_NotReadAsUnprotected()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        PdfMacVerification result = Verify(Rename(sealedDoc, "/AuthCode", "/AuthCodX"));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("requires a PDF MAC token in every revision", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Removing the salt with the token changes nothing: the requirement is the permission bit,
    /// which the tamperer cannot rewrite, not the two entries they can delete.
    /// </summary>
    [Fact]
    public void ATokenRemovedWithItsSaltIsStillReported()
    {
        byte[] stripped = Rename(Rename(Encrypt(MinimalPdf(), pdfMac: true), "/AuthCode", "/AuthCodX"), "/KDFSalt", "/KDFSalX");

        PdfMacVerification result = Verify(stripped);

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("requires a PDF MAC token in every revision", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The last move: set bit 13 in <c>/P</c> so the document no longer says a token is required.
    /// <c>/P</c> is plain text too, but <c>/Perms</c> holds the same word under the file key, and
    /// the two no longer agree — which is reported, where trusting the edited <c>/P</c> would call
    /// the stripped document unprotected. Deleting <c>/Perms</c> as well leaves <c>/P</c> with
    /// nothing vouching for it, and is reported the same way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATokenRemovedBehindAnEditedPermissionWordIsReported(bool permsRemovedToo)
    {
        byte[] stripped = Rename(Rename(Encrypt(MinimalPdf(), pdfMac: true), "/AuthCode", "/AuthCodX"), "/KDFSalt", "/KDFSalX");

        string text = Encoding.Latin1.GetString(stripped);
        var p = System.Text.RegularExpressions.Regex.Match(text, @"/P (-?\d+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.True(p.Success, "the fixture's encryption dictionary has no /P to edit");
        int original = int.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, original & 0x1000);                       // the sealed fixture requires a token
        string edited = (original | 0x1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(edited.Length <= p.Groups[1].Length, "the edited /P must fit where the original stood");
        text = text.Substring(0, p.Groups[1].Index) + edited.PadRight(p.Groups[1].Length) + text.Substring(p.Groups[1].Index + p.Groups[1].Length);

        byte[] forged = Encoding.Latin1.GetBytes(text);
        if (permsRemovedToo) forged = Rename(forged, "/Perms", "/PermX");

        PdfMacVerification result = Verify(forged);

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("is not authenticated by /Perms", result.Error, StringComparison.Ordinal);
        Assert.Contains(permsRemovedToo ? "Absent" : "PMismatch", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The encryptor writes no token below <c>/V 5</c> (ISO/TS 32004 Table 5), which is where the
    /// limit pinned below lives: the refusal names the algorithm and the rule, and nothing is
    /// written. AES-256 through the same door is the control.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128, false)]
    [InlineData(EncryptionAlgorithm.AES128, false)]
    [InlineData(EncryptionAlgorithm.AES256, true)]
    public void TheEncryptorWritesATokenOnlyFromVersion5(EncryptionAlgorithm algorithm, bool written)
    {
        using var input = new MemoryStream(MinimalPdf(), writable: false);
        using var output = new MemoryStream();
        var parameters = new EncryptionParameters
        {
            UserPassword = Password, OwnerPassword = Password, Algorithm = algorithm, EnablePdfMac = true,
        };

        if (written)
        {
            PdfEncryptor.Encrypt(input, output, parameters);
            Assert.True(Verify(output.ToArray()).IsValid);
        }
        else
        {
            var ex = Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(input, output, parameters));
            Assert.Contains("at least 5", ex.Message, StringComparison.Ordinal);
            Assert.Contains(algorithm.ToString(), ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// Before revision 6 nothing authenticates <c>/P</c> and the bit had no meaning, so the salt is
    /// the witness: a document whose encryption dictionary carries a <c>/KDFSalt</c> used PDF MAC,
    /// and its token removed is reported.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    public void BeforeAes256ATokenRemovedIsReportedWhileTheSaltStands(EncryptionAlgorithm algorithm)
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true, algorithm);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        PdfMacVerification result = Verify(Rename(sealedDoc, "/AuthCode", "/AuthCodX"));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("carries a /KDFSalt", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The limit of that witness, pinned: with the salt removed as well, a document encrypted
    /// before revision 6 says nothing a tamperer could not have written, and reads as unprotected —
    /// where the same two deletions under AES-256 are reported
    /// (<see cref="ATokenRemovedWithItsSaltIsStillReported"/>).
    /// </summary>
    [Fact]
    public void BeforeAes256ATokenRemovedWithItsSaltReadsAsUnprotected()
    {
        byte[] stripped = Rename(
            Rename(Encrypt(MinimalPdf(), pdfMac: true, EncryptionAlgorithm.AES128), "/AuthCode", "/AuthCodX"),
            "/KDFSalt", "/KDFSalX");

        PdfMacVerification result = Verify(stripped);

        Assert.False(result.IsProtected);
        Assert.Null(result.Error);
    }

    /// <summary>Table 5 has the entry direct. One written as a reference is a token no reader is told to look for, and is reported rather than taken for none.</summary>
    [Fact]
    public void AnAuthCodeThatIsNotADirectDictionaryIsReported()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        string text = Encoding.Latin1.GetString(sealedDoc);
        int key = text.IndexOf("/AuthCode", StringComparison.Ordinal);
        int open = text.IndexOf("<<", key, StringComparison.Ordinal);
        int close = text.IndexOf(">>", open, StringComparison.Ordinal) + 2;
        Assert.True(key >= 0 && open > key && close > open, "the fixture's /AuthCode is not the dictionary this test expects");

        string indirect = text.Substring(0, open) + "1 0 R".PadRight(close - open) + text.Substring(close);

        PdfMacVerification result = Verify(Encoding.Latin1.GetBytes(indirect));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("not a direct dictionary", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A PDF MAC is keyed on a file encryption key, so an unencrypted document cannot have one.</summary>
    [Fact]
    public void AnUnencryptedDocumentIsRefused()
    {
        var ex = Assert.Throws<PdfException>(
            () => PdfMacVerifier.Verify(new MemoryStream(MinimalPdf(), writable: false), Password));

        Assert.Contains("not encrypted", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A signed revision's token verifies through the signature it is attached to</b> —
    /// §6.5.2 and Annex B.2.3, the placement this library gained alongside this checker.
    /// </summary>
    [Fact]
    public void AnAttachedToSigTokenVerifiesAgainstTheSignatureItRidesOn()
    {
        byte[] signed = SignSealed();

        PdfMacVerification result = Verify(signed);

        Assert.True(result.IsProtected);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal("AttachedToSig", result.Location);
    }

    /// <summary>
    /// The attached placement's gap is the signature's own, and holds its <c>/Contents</c> and nothing else
    /// (ISO 32000-2 §12.8.1): the same bytes written as a literal string parse to the same token and leave
    /// room no digest covers. The token, its digest and its coverage are unchanged; the gap is read.
    /// </summary>
    [Fact]
    public void AdversarialSignatureContentsRewrittenAsALiteralString_IsReported()
    {
        byte[] signed = SignSealed();
        Assert.True(Verify(signed).IsValid, "the fixture must verify before it is altered");

        string text = Encoding.Latin1.GetString(signed);
        int lt = -1, gt = -1;
        for (int i = text.IndexOf("/Contents <", StringComparison.Ordinal); i >= 0;
             i = text.IndexOf("/Contents <", i + 1, StringComparison.Ordinal))
        {
            int open = i + "/Contents ".Length, close = text.IndexOf('>', open);
            if (close - open > 1000) { lt = open; gt = close; }
        }
        Assert.True(lt > 0, "the fixture has no signature /Contents");
        byte[] contents = Convert.FromHexString(text.Substring(lt + 1, gt - lt - 1));
        var literal = new StringBuilder("(");
        foreach (byte b in contents)
        {
            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\') literal.Append('\\').Append((char)b);
            else if (b == (byte)'\r') literal.Append("\\r");
            else if (b == (byte)'\n') literal.Append("\\n");
            else literal.Append((char)b);
        }
        literal.Append(')');
        Assert.True(literal.Length <= gt - lt + 1, "the literal form must fit the hex form's gap");
        byte[] tampered = Encoding.Latin1.GetBytes(text.Substring(0, lt) + literal.ToString().PadRight(gt - lt + 1) + text.Substring(gt + 1));

        PdfMacVerification result = Verify(tampered);
        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("12.8.1", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The range checked is the <b>signature's</b>, so a byte appended after it — which the
    /// signature does not cover — is a different finding from one changed inside it.
    /// </summary>
    [Fact]
    public void AByteChangedInsideTheSignedRangeIsReportedForAnAttachedToSigToken()
    {
        byte[] signed = SignSealed();
        Assert.True(Verify(signed).IsValid, "the fixture must verify before it is altered");

        PdfMacVerification result = Verify(WithAlteredHeaderVersion(signed));

        Assert.False(result.IsValid);
        Assert.Contains("dataDigest", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same coverage rule for a signed revision: §6.5.2 requires the <b>signature's</b> range
    /// to cover the whole file, and B.2.3.2 reads it exactly as the standalone case.
    /// </summary>
    /// <remarks>
    /// Asserted separately from the standalone case because the two ranges are read from different
    /// dictionaries — the <c>AuthCode</c>'s and the signature's — so one check cannot stand in for
    /// the other. A mutation removing this one survived until this test existed.
    /// </remarks>
    [Fact]
    public void AnAttachedToSigTokenThatStopsShortOfTheEndOfTheFileIsReported()
    {
        byte[] signed = SignSealed();
        Assert.True(Verify(signed).IsValid, "the fixture must verify before bytes are added");

        byte[] extended = signed.Concat(Encoding.ASCII.GetBytes("\n% appended by something else\n")).ToArray();

        PdfMacVerification result = Verify(extended);

        Assert.Equal("AttachedToSig", result.Location);
        Assert.False(result.IsValid);
        Assert.Contains("stops before the end of the file", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A <c>/ByteRange</c> value too large for a 64-bit integer is refused, and the guard that
    /// refuses it is the one that claims to.</b>
    /// </summary>
    /// <remarks>
    /// <para>The check read <c>DoubleValue &lt; 0</c> and then cast: a huge <i>positive</i> value
    /// passed the test for negatives and came out of the cast as <c>long.MinValue</c>, so the guard
    /// against negative offsets was itself producing one. <c>PdfNumber</c> says so at the property —
    /// "the IntValue / LongValue casts below would silently truncate (e.g. 1e30 → long.MinValue)" —
    /// and exposes <c>IsInteger</c>, which is false outside <c>long</c>'s range, for this reason.</para>
    ///
    /// <para><b>The value carries a decimal point on purpose, and that is the whole fixture.</b>
    /// <c>PdfParser</c> builds a <c>PdfNumber</c> from the token's <c>LongValue</c> when the literal
    /// has no period, so an oversized <i>integer</i> literal is already clamped before it arrives and
    /// never reaches this guard. §7.3.3 permits a real written with a period and forbids exponent
    /// notation, so a period is the only way a file can hand a range element a value outside
    /// <c>long</c>'s range — which is precisely the malformed input the check exists for. The first
    /// version of this test used digits alone, passed through the parser as a clamped <c>long</c>,
    /// and failed for an unrelated reason.</para>
    ///
    /// <para><b>The assertion names the rule rather than the outcome.</b> Splicing bytes into the
    /// trailer moves bytes the token covers, so this document is refused by the coverage and digest
    /// checks too; asserting only "invalid" would stay green with the range validation deleted.</para>
    /// </remarks>
    [Fact]
    public void AByteRangeValueTooLargeForAnInt64IsRefusedRatherThanTruncated()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        string text = Encoding.Latin1.GetString(sealedDoc);
        int at = text.IndexOf("/ByteRange", StringComparison.Ordinal);
        Assert.True(at >= 0, "the sealed fixture carries no /ByteRange to edit");
        int open = text.IndexOf('[', at);
        int close = text.IndexOf(']', open);
        Assert.True(open > 0 && close > open, "the /ByteRange is not the array this test expects");

        // 1e30 written as a §7.3.3 real: within double's range, far outside long's. The period is
        // what routes it through the parser's double path — see the remarks.
        string spliced = text.Substring(0, open)
                       + "[0 1000000000000000000000000000000.0 100 200]"
                       + text.Substring(close + 1);

        PdfMacVerification result = Verify(Encoding.Latin1.GetBytes(spliced));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("nonnegative integer", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Table 6's conditional-presence rules are enforced, not merely documented.</b> An
    /// <c>AuthCode</c> that says <c>AttachedToSig</c> while carrying a <c>/ByteRange</c> is
    /// describing one placement and using another, and a checker that ignored the extra entry would
    /// let a producer state a coverage it never computed.
    /// </summary>
    [Fact]
    public void AnAttachedToSigAuthCodeCarryingAByteRangeIsRefused()
    {
        byte[] signed = SignSealed();

        // Rewritten in the raw bytes: the trailer is plain text and this is exactly the malformed
        // shape a producer would emit by reusing the standalone template.
        // The APPENDED revision's AuthCode, not the original's. A sealed-then-signed document has
        // two: the standalone one the seal wrote and the attached one the signature revision wrote,
        // and the first occurrence in the bytes is the standalone one — editing that instead
        // produces a document that fails for an unrelated reason.
        string text = Encoding.Latin1.GetString(signed);
        int at = text.IndexOf("/MACLocation /AttachedToSig", StringComparison.Ordinal);
        Assert.True(at >= 0, "the signed fixture carries no AttachedToSig AuthCode to edit");

        string spliced = text.Insert(at, "/ByteRange [0 1 2 3] ");
        PdfMacVerification result = Verify(Encoding.Latin1.GetBytes(spliced));

        Assert.False(result.IsValid);

        // "Table 6 forbids", not "/ByteRange". Splicing bytes into the trailer also moves bytes the
        // signature covers, so the dataDigest check would refuse this document too — and its
        // message names /ByteRange as well. Asserting on the substring they share let the whole
        // Table 6 rule be deleted with this test still green; a mutation is what said so.
        Assert.Contains("Table 6 forbids", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The MAC authenticates the attributes; only <c>messageDigest</c> ties them to the
    /// payload.</b>
    /// </summary>
    /// <remarks>
    /// This is the check whose absence would be invisible to every other test here. Replacing the
    /// token's encapsulated content leaves the MAC over the authenticated attributes perfectly
    /// valid — it was computed over the attributes and they did not change — so a checker that
    /// verified the MAC and then read <c>dataDigest</c> out of the content would accept digests an
    /// attacker chose. RFC 5652 §9 and ISO/TS 32004 §6.3.6.3 close that by hashing the eContent
    /// into an authenticated attribute; this asserts the closing actually happens.
    /// </remarks>
    [Fact]
    public void ATokenWhoseContentWasReplacedIsRefusedEvenThoughItsMacStillMatches()
    {
        byte[] key = new byte[32];
        byte[] salt = new byte[32];
        for (int i = 0; i < 32; i++) { key[i] = (byte)i; salt[i] = (byte)(255 - i); }

        byte[] honest = PdfMacToken.BuildStandaloneToken(key, salt, Digest("the real document"), null);
        byte[] forged = PdfMacToken.BuildStandaloneToken(key, salt, Digest("a different document"), null);

        // A control: each token verifies against the digest it was built for, so the transplant
        // below is the only thing under test.
        Assert.Null(PdfMacToken.Verify(honest, key, salt, Digest("the real document"), null));
        Assert.Null(PdfMacToken.Verify(forged, key, salt, Digest("a different document"), null));

        // The forged payload carried by the honest token's authenticated attributes and MAC.
        byte[] swapped = WithContentOf(honest, forged);

        string problem = PdfMacToken.Verify(swapped, key, salt, Digest("a different document"), null);

        Assert.NotNull(problem);
        Assert.Contains("messageDigest", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.6.2 makes <c>signatureDigest</c>'s <b>absence</b> a requirement in an unsigned revision,
    /// so a standalone token carrying one is refused rather than having the extra field ignored.
    /// </summary>
    [Fact]
    public void AStandaloneTokenCarryingASignatureDigestIsRefused()
    {
        byte[] key = new byte[32];
        byte[] salt = new byte[32];
        for (int i = 0; i < 32; i++) { key[i] = (byte)(i * 3); salt[i] = (byte)(i + 7); }

        byte[] dataDigest = Digest("a document");
        byte[] token = PdfMacToken.BuildAttachedToSigToken(key, salt, dataDigest, Digest("a signature"), null);

        string problem = PdfMacToken.Verify(token, key, salt, dataDigest, expectedSignatureDigest: null);

        Assert.NotNull(problem);
        Assert.Contains("6.6.2", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The MAC value itself is compared.</b>
    /// </summary>
    /// <remarks>
    /// Every other failing case here fails for a reason that reaches the token before its MAC does —
    /// a wrong key cannot unwrap, a replaced payload breaks <c>messageDigest</c>, altered bytes
    /// break <c>dataDigest</c>. Altering the MAC and nothing else is the only input that isolates
    /// the comparison, and without it removing that comparison leaves every test green.
    /// </remarks>
    [Fact]
    public void AnAlteredMacValueIsRefused()
    {
        (byte[] key, byte[] salt) = Material(1);
        byte[] dataDigest = Digest("a document");

        byte[] token = PdfMacToken.BuildStandaloneToken(key, salt, dataDigest, null);
        Assert.Null(PdfMacToken.Verify(token, key, salt, dataDigest, null));

        // unauthAttrs is absent (§6.3.7) and the encoding is DER, so `mac` is the structure's last
        // field and its final byte is the token's final byte.
        var altered = (byte[])token.Clone();
        altered[altered.Length - 1] ^= 0xFF;

        string problem = PdfMacToken.Verify(altered, key, salt, dataDigest, null);

        Assert.NotNull(problem);
        Assert.Contains("MAC does not match", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// §6.6.3's <c>signatureDigest</c> is compared, so a token bound to a different signature is
    /// refused even though it covers this document correctly.
    /// </summary>
    /// <remarks>
    /// This is the field that makes the attached placement worth having: without comparing it, a
    /// token could be lifted from one signature onto another over the same bytes.
    /// </remarks>
    [Fact]
    public void ATokenBoundToADifferentSignatureIsRefused()
    {
        (byte[] key, byte[] salt) = Material(2);
        byte[] dataDigest = Digest("a document");

        byte[] token = PdfMacToken.BuildAttachedToSigToken(
            key, salt, dataDigest, Digest("the signature it was made for"), null);

        // The control: it verifies against the signature it names.
        Assert.Null(PdfMacToken.Verify(
            token, key, salt, dataDigest, Digest("the signature it was made for")));

        string problem = PdfMacToken.Verify(
            token, key, salt, dataDigest, Digest("some other signature"));

        Assert.NotNull(problem);
        Assert.Contains("signatureDigest", problem, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Two tokens on one signature are refused rather than the first being chosen.</b>
    /// </summary>
    /// <remarks>
    /// The attribute is <i>unsigned</i>, so the signature protects nothing about it and anyone may
    /// append a second one. A validator that took the first would be making a choice an attacker
    /// prepared for it — and the two shapes differ: one attribute with two values, or two
    /// attributes with one each. This is the second, which an attribute-table lookup returns the
    /// first of.
    /// </remarks>
    [Fact]
    public void ASignatureCarryingTwoMacTokensIsRefused()
    {
        (byte[] key, byte[] salt) = Material(3);
        byte[] signed = SignSealed();

        byte[] contents;
        using (var view = PdfDocument.Open(new MemoryStream(Decrypt(signed, Password))))
            contents = view.AcroForm!.GetSignatureFields().Single()
                           .SignatureValue!.GetString(PdfName.Contents)!.RawBytes;

        // The control: one token is found.
        Assert.True(PdfMacToken.TryExtractAttachedToken(contents, out _, out _));

        // A second attachment of the same attribute type.
        byte[] twice = PdfMacToken.AttachToSignature(contents, Digest("anything"), key, salt);

        Assert.False(PdfMacToken.TryExtractAttachedToken(twice, out _, out _));
    }

    /// <summary>
    /// An <c>/AuthCode</c> naming a placement this implementation does not know is refused, not
    /// passed over.
    /// </summary>
    /// <remarks>
    /// Table 6 allows other values as second-class names and Annex B.2.1 makes the outcome
    /// implementation-dependent — which is licence to refuse, not licence to report a document
    /// protected when its protection was never checked.
    /// </remarks>
    [Fact]
    public void AnUnknownMacLocationIsRefused()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        PdfMacVerification result = Verify(WithMacLocation(sealedDoc, "/Elsewhere "));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("neither Standalone nor AttachedToSig", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// §B.2.1: "Absence of the MACLocation entry is indicative of an invalid MAC."
    /// </summary>
    [Fact]
    public void AnAuthCodeWithoutAMacLocationIsRefused()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), pdfMac: true);
        Assert.True(Verify(sealedDoc).IsValid, "the fixture must verify before it is altered");

        // Renamed rather than removed, so the dictionary keeps its shape and only the entry the
        // rule is about goes missing.
        PdfMacVerification result = Verify(Rename(sealedDoc, "/MACLocation", "/MACLocatioX"));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);

        // "has no /MACLocation entry", not "/MACLocation". With the entry gone the value reads as
        // null, which falls through to the unknown-placement branch — whose message names
        // /MACLocation too. Asserting the shared substring let the absence check be deleted with
        // this test still green, which is exactly what a mutation reported.
        Assert.Contains("has no /MACLocation entry", result.Error, StringComparison.Ordinal);
        Assert.Null(result.Location);
    }

    /// <summary>
    /// An <c>/AuthCode</c> promising an attached token on a signature that carries none is refused.
    /// </summary>
    /// <remarks>
    /// The trailer's claim and the signature's contents are written by different steps, so they can
    /// disagree — and a checker that treated "the token is not there" as "there is no token to
    /// check" would report the document unprotected while its trailer says otherwise.
    /// </remarks>
    [Fact]
    public void AnAttachedToSigClaimOverASignatureWithoutATokenIsRefused()
    {
        byte[] signed = SignSealed();
        Assert.True(Verify(signed).IsValid, "the fixture must verify before it is altered");

        // The attribute is removed by renaming its OID, which keeps every byte offset — so the
        // document still parses, the trailer still claims AttachedToSig, and the only thing that
        // changed is that the token it promises is no longer findable.
        //
        // 1.0.32004.1.2 is DER 06 06 28 81 FA 04 01 02 (32004 is three base-128 digits: 1, 122, 4),
        // and /Contents holds the CMS as a HEX STRING, so what is in the file is those bytes
        // spelled out. Changing the final digit makes it 1.0.32004.1.3 — a well-formed OID that
        // names nothing.
        string text = Encoding.Latin1.GetString(signed);
        const string oidHex = "06062881FA040102";
        int at = text.IndexOf(oidHex, StringComparison.OrdinalIgnoreCase);
        Assert.True(at >= 0, "the signed fixture carries no id-attr-pdfMacData OID to rename");

        string renamed = text.Substring(0, at + oidHex.Length - 1) + "3" +
                         text.Substring(at + oidHex.Length);

        PdfMacVerification result = Verify(Encoding.Latin1.GetBytes(renamed));

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("id-attr-pdfMacData", result.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A distinct, deterministic key and salt per test.</summary>
    private static (byte[] Key, byte[] Salt) Material(int seed)
    {
        var key = new byte[32];
        var salt = new byte[32];
        for (int i = 0; i < 32; i++)
        {
            key[i] = (byte)(i * 7 + seed);
            salt[i] = (byte)(i * 11 + seed);
        }
        return (key, salt);
    }

    /// <summary>The document with its <c>/MACLocation</c> value replaced, keeping every offset.</summary>
    /// <remarks>
    /// The replacement must be the same length as <c>/Standalone</c> so nothing downstream shifts;
    /// padding it with spaces keeps it a valid name followed by whitespace.
    /// </remarks>
    private static byte[] WithMacLocation(byte[] pdf, string replacement)
    {
        const string original = "/Standalone";
        Assert.Equal(original.Length, replacement.Length);

        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains(original, text, StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(ReplaceFirst(text, original, replacement));
    }

    /// <summary>Renames one key, which must keep its length so no offset moves.</summary>
    private static byte[] Rename(byte[] pdf, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains(from, text, StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(ReplaceFirst(text, from, to));
    }

    private static string ReplaceFirst(string text, string from, string to)
    {
        int at = text.IndexOf(from, StringComparison.Ordinal);
        return text.Substring(0, at) + to + text.Substring(at + from.Length);
    }

    private static PdfMacVerification Verify(byte[] pdf) =>
        PdfMacVerifier.Verify(new MemoryStream(pdf, writable: false), Password);

    /// <summary>
    /// The document with one byte of its <c>%PDF-x.y</c> header changed.
    /// </summary>
    /// <remarks>
    /// <para><b>The minor version digit, chosen because it is inert.</b> It sits in the first
    /// covered range under any <c>/ByteRange</c> — the first array element is required to be zero —
    /// and nothing downstream branches on it, so the document still parses, still decrypts and
    /// still presents its token. The only thing that changed is a byte the digest covered, which is
    /// precisely the claim.</para>
    /// <para>A PDF MAC forces a 2.0 header, so this is a 2.0 document; the offset is derived from
    /// the marker rather than assumed, because the version the fixture carries is the encryptor's
    /// choice and not this test's.</para>
    /// </remarks>
    private static byte[] WithAlteredHeaderVersion(byte[] pdf)
    {
        const string marker = "%PDF-";
        int at = Encoding.Latin1.GetString(pdf).IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture has no PDF header to alter");

        int minor = at + marker.Length + 2;          // major digit, '.', minor digit
        var altered = (byte[])pdf.Clone();
        altered[minor] = altered[minor] == (byte)'0' ? (byte)'1' : (byte)'0';
        return altered;
    }

    private static byte[] Digest(string text)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// <paramref name="host"/> with the <c>/MAC</c> hex string of <paramref name="donor"/>.
    /// </summary>
    /// <remarks>
    /// Both documents are sealed the same way, so the two strings are the same length and the
    /// substitution shifts no offset — the host file stays structurally intact and differs only in
    /// the token, which is what makes this a test about the key rather than about parsing.
    /// </remarks>
    private static byte[] WithMacOf(byte[] host, byte[] donor)
    {
        string hostText = Encoding.Latin1.GetString(host);
        string donorText = Encoding.Latin1.GetString(donor);

        string hostMac = MacString(hostText);
        string donorMac = MacString(donorText);
        Assert.Equal(hostMac.Length, donorMac.Length);

        return Encoding.Latin1.GetBytes(hostText.Replace(hostMac, donorMac));
    }

    /// <summary>The <c>/MAC &lt;…&gt;</c> hex string, brackets included.</summary>
    private static string MacString(string pdf)
    {
        int at = pdf.IndexOf("/MAC <", StringComparison.Ordinal);
        if (at < 0) at = pdf.IndexOf("/MAC<", StringComparison.Ordinal);
        Assert.True(at >= 0, "the document carries no /MAC entry");

        int open = pdf.IndexOf('<', at);
        int close = pdf.IndexOf('>', open);
        Assert.True(close > open, "the /MAC entry is not a hex string");
        return pdf.Substring(open, close - open + 1);
    }

    /// <summary>
    /// <paramref name="host"/>'s token with <paramref name="donor"/>'s encapsulated content
    /// substituted, leaving the authenticated attributes and the MAC untouched.
    /// </summary>
    /// <remarks>
    /// Done on the encoded bytes rather than by rebuilding the structure, because rebuilding would
    /// recompute the very attribute under test. Both payloads are the same length — the digests are
    /// both SHA-256 — so the surrounding lengths stay valid.
    /// </remarks>
    private static byte[] WithContentOf(byte[] host, byte[] donor)
    {
        byte[] hostPayload = IntegrityInfoOf(host);
        byte[] donorPayload = IntegrityInfoOf(donor);
        Assert.Equal(hostPayload.Length, donorPayload.Length);

        int at = IndexOf(host, hostPayload);
        Assert.True(at >= 0, "the host token does not contain its own payload");

        byte[] swapped = (byte[])host.Clone();
        Array.Copy(donorPayload, 0, swapped, at, donorPayload.Length);
        return swapped;
    }

    /// <summary>The DER of a token's <c>PdfMacIntegrityInfo</c>, found by its structure.</summary>
    /// <remarks>
    /// A <c>PdfMacIntegrityInfo</c> with only a <c>dataDigest</c> is a SEQUENCE holding an INTEGER 0
    /// and a 32-byte OCTET STRING: <c>30 25 02 01 00 04 20 …</c>. That prefix is what identifies it
    /// without decoding the whole CMS in a test.
    /// </remarks>
    private static byte[] IntegrityInfoOf(byte[] token)
    {
        byte[] prefix = { 0x30, 0x25, 0x02, 0x01, 0x00, 0x04, 0x20 };
        int at = IndexOf(token, prefix);
        Assert.True(at >= 0, "the token carries no recognisable PdfMacIntegrityInfo");
        return token.Skip(at).Take(prefix.Length + 32).ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            int k = 0;
            while (k < needle.Length && haystack[i + k] == needle[k]) k++;
            if (k == needle.Length) return i;
        }
        return -1;
    }

    private static byte[] SignSealed()
    {
        var (pfx, pw) = TestCertificate();
        using var output = new MemoryStream();
        PdfEncryptionOperations.Sign(
            new MemoryStream(Encrypt(MinimalPdf(), pdfMac: true), writable: false),
            output, Password,
            new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx),
                CertificatePassword = pw,
                SignatureFieldName = "Signature1",
                Reason = "MAC verification fixture",
            });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] plaintext, bool pdfMac, EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
    {
        using var input = new MemoryStream(plaintext, writable: false);
        using var output = new MemoryStream();
        // The internal door with the refusal off: a token below /V 5 is another producer's
        // document, which the verifier has to recognise and this library's encryptor will not write.
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
            Algorithm = algorithm,
            EnablePdfMac = pdfMac,
        }, refuseTokenBelowVersion5: false);
        return output.ToArray();
    }

    private static byte[] MinimalPdf(string text = "PDF MAC verification fixture")
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({text}) Tj ET\n";
        var bodies = new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        };

        using var ms = new MemoryStream();
        byte[] head = Encoding.Latin1.GetBytes("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new System.Collections.Generic.List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            byte[] b = Encoding.Latin1.GetBytes(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] tail = Encoding.Latin1.GetBytes(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static (byte[] Pfx, string Password) TestCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=PDF MAC Verification Test",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        using System.Security.Cryptography.X509Certificates.X509Certificate2 certificate =
            request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        const string pw = "cert-pw";
        return (certificate.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, pw), pw);
    }
}
