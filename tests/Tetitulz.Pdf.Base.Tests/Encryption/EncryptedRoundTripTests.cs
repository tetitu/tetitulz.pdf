// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// <see cref="PdfEncryptedRoundTrip"/> — decrypt → operate → re-encrypt.
///
/// <para>Every assertion here reads the <b>produced document</b>: its encryption version, its
/// permission bits, or its decrypted bytes. None of them is satisfied by "a byte array came
/// back", which is the point — a passthrough implementation must fail these.</para>
/// </summary>
public sealed class EncryptedRoundTripTests
{
    private const string Password = "s3cret";

    private static byte[] Encrypt(byte[] plain, EncryptionAlgorithm algorithm,
                                  PdfPermissions permissions = PdfPermissions.All,
                                  string user = Password, string owner = "owner-only")
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = user,
            OwnerPassword = owner,
            Algorithm     = algorithm,
            Permissions   = permissions
        });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password = Password)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    private static EncryptionInfo InfoOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfDecryptor.GetEncryptionInfo(ms);
    }

    /// <summary>An operation that changes nothing, for tests about the trip rather than the work.</summary>
    private static void Passthrough(Stream input, Stream output) => input.CopyTo(output);

    // ------------------------------------------------------------------
    //  1 — the algorithm survives the trip, for every algorithm we support
    // ------------------------------------------------------------------

    /// <remarks>
    /// <see cref="EncryptionAlgorithm.RC4_40R3"/> is the revision-3 40-bit form ISO 32000-1's own PDF
    /// carries (<c>/V 1 /R 3 /Length 40</c>); the round trip wrote revision 2 for it, a different key
    /// derivation and <c>/U</c> for the same cipher (GAPS §19h item 7). The fixture pins that the
    /// encryptor writes revision 3 for it before the trip is judged.
    /// </remarks>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_40R3)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void RoundTrip_ReEncryptsWithTheSameAlgorithm(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("algo"), algorithm);
        var before = InfoOf(encrypted);
        if (algorithm == EncryptionAlgorithm.RC4_40R3) { Assert.Equal(1, before.V); Assert.Equal(3, before.R); Assert.Equal(40, before.KeyLengthBits); }
        if (algorithm == EncryptionAlgorithm.RC4_40) Assert.Equal(2, before.R);
        Assert.Contains("algo", Encoding.ASCII.GetString(Decrypt(encrypted)));   // the revision-3 key schedule opens it

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = Password }, Passthrough);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(algorithm, result.AlgorithmIn);
        Assert.Equal(algorithm, result.AlgorithmOut);

        var after = InfoOf(output.ToArray());
        Assert.Equal(before.V, after.V);
        Assert.Equal(before.R, after.R);
        Assert.Equal(before.KeyLengthBits, after.KeyLengthBits);
    }

    // ------------------------------------------------------------------
    //  2 — permission bits are carried, not reset to "All"
    // ------------------------------------------------------------------

    [Fact]
    public void RoundTrip_CarriesThePermissionBits()
    {
        // Printing denied, copying allowed. A default-constructed EncryptionParameters would
        // grant everything, so this fails if /P is not read off the input.
        const PdfPermissions restricted = PdfPermissions.Copy | PdfPermissions.ExtractForAccessibility;

        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("perms"),
                                   EncryptionAlgorithm.AES128, restricted);

        // Pin the fixture: the input really does deny printing, so a failure below is the round
        // trip's fault and not the encryptor's.
        var before = (PdfPermissions)(InfoOf(encrypted).PValue & (int)PdfPermissions.All);
        Assert.Equal(restricted, before);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = Password }, Passthrough);

        Assert.True(result.Succeeded, result.FailureReason);

        var after = (PdfPermissions)(InfoOf(output.ToArray()).PValue & (int)PdfPermissions.All);
        Assert.Equal(restricted, after);
        Assert.False(after.HasFlag(PdfPermissions.Print));
    }

    /// <summary>
    /// <c>PdfEncryptor.ComputePermissionValue</c> does not OR <c>0x4</c> into <c>/P</c> as if bit 3
    /// were reserved. Bit 3 <b>is</b> the print permission, so that would make <b>every</b> document
    /// this library encrypts grant printing regardless of what the caller asked for — a caller who
    /// denied printing would get a printable file and no indication of it. Bits 1-2 are the ones
    /// that shall be zero, and the 0xFFC mask leaves them clear.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void Encrypt_WithPrintingDenied_DoesNotGrantPrinting(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("no-print"),
                                   algorithm, PdfPermissions.Copy);

        int p = InfoOf(encrypted).PValue;

        Assert.Equal(0, p & (int)PdfPermissions.Print);
        Assert.NotEqual(0, p & (int)PdfPermissions.Copy);
        Assert.Equal(0, p & 0x3);              // bits 1-2 shall be zero
        Assert.NotEqual(0, p & 0xC0);          // bits 7-8 shall be one
    }

    // ------------------------------------------------------------------
    //  3 — the documented lossy step is announced, not hidden
    // ------------------------------------------------------------------

    [Fact]
    public void RoundTrip_ReuseOpenPassword_ReportsThatTheOwnerPasswordChanged()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("owner"), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = Password }, Passthrough);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.True(result.OwnerPasswordChanged);

        // And it is really gone: the original owner password no longer opens the output.
        byte[] produced = output.ToArray();
        Assert.Throws<PdfException>(() => Decrypt(produced, "owner-only"));
        Assert.NotEmpty(Decrypt(produced, Password));
    }

    // ------------------------------------------------------------------
    //  4 — the operation really ran: sanitizing removes the JavaScript
    // ------------------------------------------------------------------

    [Fact]
    public void Sanitize_ThroughTheRoundTrip_RemovesJavaScriptFromTheEncryptedDocument()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreatePdfWithJavaScript(), EncryptionAlgorithm.AES128);

        // Baseline: the sanitizer refuses this document on its own. That refusal is what this
        // whole feature routes around, and it must still be there.
        using (var direct = new MemoryStream(encrypted))
        using (var refused = new MemoryStream())
            Assert.Throws<PdfEncryptedDocumentException>(
                () => PdfSanitizer.Sanitize(direct, refused));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = Password },
            (plain, produced) => PdfSanitizer.Sanitize(plain, produced,
                new SanitizeOptions { RemoveJavaScript = true, RemoveOpenAction = true }));

        Assert.True(result.Succeeded, result.FailureReason);

        // Assert on the DECRYPTED bytes: an encrypted output would hide the string either way,
        // so checking the ciphertext would pass even if nothing had been sanitized.
        string decrypted = Encoding.ASCII.GetString(Decrypt(output.ToArray()));
        Assert.DoesNotContain("app.alert", decrypted, StringComparison.Ordinal);
        Assert.DoesNotContain("/JavaScript", decrypted, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(Decrypt(output.ToArray())));
        Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.OpenAction));
    }

    // ------------------------------------------------------------------
    //  5 — optimize through the trip: smaller, and still openable
    // ------------------------------------------------------------------

    [Fact]
    public void Optimize_ThroughTheRoundTrip_RemovesTheUnusedObjectAndStillOpens()
    {
        byte[] plain = MinimalPdfBuilder.CreatePdfWithUnusedObject();
        byte[] encrypted = Encrypt(plain, EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = Password },
            (p, produced) => PdfOptimizer.Optimize(p, produced));

        Assert.True(result.Succeeded, result.FailureReason);

        // The fixture's object 6 is an unreferenced /Times-Roman font; object 5 (/Helvetica) is
        // the one the page actually uses. Naming both directions is what makes this fail on a
        // passthrough: "smaller" alone would not.
        string decrypted = Encoding.ASCII.GetString(Decrypt(output.ToArray()));
        Assert.DoesNotContain("Times-Roman", decrypted, StringComparison.Ordinal);
        Assert.Contains("Helvetica", decrypted, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(Decrypt(output.ToArray())));
        Assert.Single(doc.Pages);
    }

    // ------------------------------------------------------------------
    //  6 — the guards stay in place; this feature routes around them,
    //      it does not weaken them
    // ------------------------------------------------------------------

    [Fact]
    public void DirectCall_OnEncryptedInput_StillFailsClosed()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("guard"), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();

        Assert.Throws<PdfEncryptedDocumentException>(() => PdfSanitizer.Sanitize(input, output));
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------
    //  7 — a wrong password produces nothing at all
    // ------------------------------------------------------------------

    [Fact]
    public void WrongPassword_WritesNothingAndReportsWhy()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("wrong-pw"), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(
            input, output, new EncryptedRoundTripOptions { OpenPassword = "not-the-password" },
            Passthrough);

        Assert.False(result.Succeeded);
        Assert.Equal(0, output.Length);
        Assert.Contains("could not be decrypted", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(result.FailureReason, PdfEncryptedRoundTrip.LastError);
    }

    // ------------------------------------------------------------------
    //  8 — a signed document is refused, and the opt-out works
    // ------------------------------------------------------------------

    [Fact]
    public void SignedDocument_IsRefusedUnlessInvalidationIsAllowed()
    {
        byte[] signed = PdfWithSignatureField();
        byte[] encrypted = Encrypt(signed, EncryptionAlgorithm.AES128);

        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            var refused = PdfEncryptedRoundTrip.Run(
                input, output, new EncryptedRoundTripOptions { OpenPassword = Password }, Passthrough);

            Assert.False(refused.Succeeded);
            Assert.Equal(0, output.Length);
            Assert.Contains("digital signature", refused.FailureReason, StringComparison.Ordinal);
        }

        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            var allowed = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions
                {
                    OpenPassword = Password,
                    AllowSignatureInvalidation = true
                },
                Passthrough);

            Assert.True(allowed.Succeeded, allowed.FailureReason);
            Assert.NotEqual(0, output.Length);
            Assert.Contains(allowed.Warnings, w => w.Contains("invalidated", StringComparison.Ordinal));
        }
    }

    // ------------------------------------------------------------------
    //  9 — a failing operation leaves the caller's stream untouched
    // ------------------------------------------------------------------

    [Fact]
    public void OperationThrows_LeavesTheOutputStreamUntouchedAndPropagatesTheType()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("throws"), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        byte[] sentinel = Encoding.ASCII.GetBytes("PRE-EXISTING");
        output.Write(sentinel, 0, sentinel.Length);

        Assert.Throws<InvalidTimeZoneException>(() =>
            PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions { OpenPassword = Password },
                (_, _) => throw new InvalidTimeZoneException("from the operation")));

        Assert.Equal(sentinel.Length, output.Length);
        Assert.Equal("PRE-EXISTING", Encoding.ASCII.GetString(output.ToArray()));
    }

    // ------------------------------------------------------------------
    // 10 — an unencrypted input is not an error
    // ------------------------------------------------------------------

    [Fact]
    public void UnencryptedInput_RunsTheOperationDirectly()
    {
        byte[] plain = MinimalPdfBuilder.CreatePdfWithJavaScript();

        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output, null,
            (p, produced) => PdfSanitizer.Sanitize(p, produced,
                new SanitizeOptions { RemoveJavaScript = true, RemoveOpenAction = true }));

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ReEncryptionPolicy.None, result.PolicyApplied);
        Assert.Null(result.AlgorithmIn);
        Assert.False(result.OwnerPasswordChanged);

        // Not re-encrypted, and the operation ran.
        string produced = Encoding.ASCII.GetString(output.ToArray());
        Assert.DoesNotContain("app.alert", produced, StringComparison.Ordinal);
        Assert.DoesNotContain("/Encrypt", produced, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // 11 — Policy.None decrypts on the way out, by explicit request
    // ------------------------------------------------------------------

    [Fact]
    public void PolicyNone_EmitsADecryptedDocument()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("strip"), EncryptionAlgorithm.AES256);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output,
            new EncryptedRoundTripOptions { OpenPassword = Password, Policy = ReEncryptionPolicy.None },
            Passthrough);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(EncryptionAlgorithm.AES256, result.AlgorithmIn);
        Assert.Null(result.AlgorithmOut);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.False(doc.IsEncrypted);
    }

    // ------------------------------------------------------------------
    // 12 — an unreproducible scheme fails with a reason, never a silent downgrade
    // ------------------------------------------------------------------

    [Fact]
    public void UnknownEncryptionVersion_FailsWithAReasonRatherThanDowngrading()
    {
        var info = new EncryptionInfo
        {
            V = 9,                      // no such version
            R = 9,
            KeyLengthBits = 128,
            PValue = -1,
            EncryptMetadata = true,
            EncryptDict = new PdfDictionary()
        };

        bool mapped = PdfEncryptedRoundTrip.TryDeriveParameters(
            info, new EncryptedRoundTripOptions { OpenPassword = Password },
            out var parameters, out string reason);

        Assert.False(mapped);
        Assert.Null(parameters);
        Assert.Contains("/V 9", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitPolicy_UsesTheCallersParametersVerbatim()
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("explicit"),
                                   EncryptionAlgorithm.RC4_128, PdfPermissions.All);

        var explicitParameters = new EncryptionParameters
        {
            UserPassword  = "new-user",
            OwnerPassword = "new-owner",
            Algorithm     = EncryptionAlgorithm.AES256,
            Permissions   = PdfPermissions.Print
        };

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedRoundTrip.Run(input, output,
            new EncryptedRoundTripOptions
            {
                OpenPassword       = Password,
                Policy             = ReEncryptionPolicy.Explicit,
                ExplicitParameters = explicitParameters
            },
            Passthrough);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(EncryptionAlgorithm.RC4_128, result.AlgorithmIn);
        Assert.Equal(EncryptionAlgorithm.AES256, result.AlgorithmOut);

        // The caller's algorithm and password really were used.
        var after = InfoOf(output.ToArray());
        Assert.Equal(5, after.V);
        Assert.NotEmpty(Decrypt(output.ToArray(), "new-user"));
    }

    [Fact]
    public void ExplicitPolicyWithoutParameters_ThrowsArgumentException()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("bad-args"));
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfEncryptedRoundTrip.Run(
            input, output,
            new EncryptedRoundTripOptions { Policy = ReEncryptionPolicy.Explicit },
            Passthrough));
    }

    // ------------------------------------------------------------------
    //  Fixture: a document whose /AcroForm declares a signature.
    // ------------------------------------------------------------------

    private static byte[] PdfWithSignatureField()
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        int o1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>\nendobj\n");
        int o2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");

        const string content = "BT ET";
        int o4 = pdf.Length;
        pdf.Append("4 0 obj\n<< /Length ").Append(content.Length).Append(" >>\nstream\n")
           .Append(content).Append("\nendstream\nendobj\n");

        int o5 = pdf.Length;
        pdf.Append("5 0 obj\n<< /FT /Sig /T (Signature1) /V 6 0 R >>\nendobj\n");
        int o6 = pdf.Length;
        pdf.Append("6 0 obj\n<< /Type /Sig /Contents <00112233> /ByteRange [0 0 0 0] >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int offset in new[] { o1, o2, o3, o4, o5, o6 })
            pdf.AppendFormat("{0:D10} 00000 n \n", offset);
        pdf.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
