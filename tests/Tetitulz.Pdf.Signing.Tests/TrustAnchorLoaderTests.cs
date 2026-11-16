// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="TrustAnchorLoader"/> — getting anchors into
/// <see cref="SignatureVerificationOptions.TrustAnchors"/>.
/// </summary>
/// <remarks>
/// <para><b>Why this type had to exist:</b> <c>TrustAnchors</c> takes
/// <c>Org.BouncyCastle.X509.X509Certificate</c> and this assembly's own certificate parsing is
/// <c>internal</c>, so a caller wanting <c>IsFullyTrusted</c> — the library's full verification
/// verdict — had to reach for BouncyCastle's API themselves. The end-to-end test below is the one
/// that matters: it loads an anchor from a PEM file and gets a <b>trusted</b> verdict on a real
/// signature, which is the whole point of the class.</para>
///
/// <para><b>Every failure case asserts the REASON, not just the empty list.</b> A load that finds
/// nothing returns empty rather than throwing, and an empty anchor list silently turns every
/// signature into "valid but not trusted" — indistinguishable from a correctly-loaded set that
/// simply does not cover this signer. Asserting only <c>Assert.Empty</c> would pass for a loader
/// that never reads anything at all.</para>
/// </remarks>
public sealed class TrustAnchorLoaderTests : IDisposable
{
    private readonly string _dir;

    public TrustAnchorLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tetitulz-anchors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------
    //  The claim: a loaded anchor produces a trusted verdict
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The end-to-end case.</b> An anchor loaded from a PEM file makes a real signature verify as
    /// trusted — and the same signature without anchors does not, which is what makes the first half
    /// mean anything rather than reporting a verdict the verifier would have given regardless.
    /// </summary>
    [Fact]
    public void AnAnchorLoadedFromPem_ProducesATrustedVerdict()
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);
        byte[] signed = Sign(leaf, root);

        string pem = Path.Combine(_dir, "root.pem");
        File.WriteAllText(pem, ToPem(root.Cert.GetEncoded()));

        var anchors = TrustAnchorLoader.LoadFile(pem);
        Assert.Single(anchors);
        Assert.Null(TrustAnchorLoader.LastError);

        Assert.True(VerifyIsTrusted(signed, anchors),
            "the signature did not verify as trusted against the anchor that issued it");

        // The control: without anchors the same signature cannot be trusted, so the assertion above
        // is about the anchors and not about the verifier being permissive.
        Assert.False(VerifyIsTrusted(signed, null));
    }

    /// <summary>DER is read by the same call — the parser sniffs the encoding.</summary>
    [Fact]
    public void ADerFileIsReadByTheSameCall()
    {
        var root = SigningTestCrypto.Ca();
        string der = Path.Combine(_dir, "root.cer");
        File.WriteAllBytes(der, root.Cert.GetEncoded());

        var anchors = TrustAnchorLoader.LoadFile(der);

        Assert.Single(anchors);
        Assert.Equal(root.Cert.SubjectDN.ToString(), anchors[0].SubjectDN.ToString());
    }

    /// <summary>
    /// A PEM bundle holding several certificates yields all of them. Reading only the first would
    /// silently trust a fraction of the set the caller supplied — and would look like success.
    /// </summary>
    [Fact]
    public void APemBundle_YieldsEveryCertificateInIt()
    {
        var a = SigningTestCrypto.Ca();
        var b = SigningTestCrypto.Ca();

        string bundle = Path.Combine(_dir, "bundle.pem");
        File.WriteAllText(bundle, ToPem(a.Cert.GetEncoded()) + ToPem(b.Cert.GetEncoded()));

        Assert.Equal(2, TrustAnchorLoader.LoadFile(bundle).Count);
    }

    [Fact]
    public void FromDer_WrapsBytesTheCallerAlreadyHas()
    {
        var root = SigningTestCrypto.Ca();

        var anchors = TrustAnchorLoader.FromDer(new List<byte[]> { root.Cert.GetEncoded() });

        Assert.Single(anchors);
        Assert.Null(TrustAnchorLoader.LastError);
    }

    // ------------------------------------------------------------------
    //  A directory of certificates
    // ------------------------------------------------------------------

    /// <summary>
    /// A directory yields every certificate in it, and the files that are not certificates — a
    /// README, an index, a hash symlink, all of which a real <c>ca-certificates</c> directory holds
    /// — are skipped rather than failing the whole bundle.
    /// </summary>
    [Fact]
    public void ADirectory_LoadsWhatItCanAndReportsWhatItSkipped()
    {
        File.WriteAllText(Path.Combine(_dir, "a.pem"), ToPem(SigningTestCrypto.Ca().Cert.GetEncoded()));
        File.WriteAllText(Path.Combine(_dir, "b.pem"), ToPem(SigningTestCrypto.Ca().Cert.GetEncoded()));
        File.WriteAllText(Path.Combine(_dir, "README"), "not a certificate");

        var anchors = TrustAnchorLoader.LoadDirectory(_dir);

        Assert.Equal(2, anchors.Count);

        // Named, not merely counted: "I loaded 2 of 3" has to be visible, or a directory where
        // most files silently failed looks the same as one that was fully read.
        Assert.Contains("1 of 3", TrustAnchorLoader.LastError, StringComparison.Ordinal);
    }

    /// <summary>A fully readable directory reports no reason at all.</summary>
    [Fact]
    public void ADirectoryOfOnlyCertificates_ReportsNothing()
    {
        File.WriteAllText(Path.Combine(_dir, "a.pem"), ToPem(SigningTestCrypto.Ca().Cert.GetEncoded()));

        Assert.Single(TrustAnchorLoader.LoadDirectory(_dir));
        Assert.Null(TrustAnchorLoader.LastError);
    }

    // ------------------------------------------------------------------
    //  Refusals — each asserts the reason in its own name
    // ------------------------------------------------------------------

    [Fact]
    public void AMissingFile_SaysSo()
    {
        Assert.Empty(TrustAnchorLoader.LoadFile(Path.Combine(_dir, "absent.pem")));
        Assert.Contains("does not exist", TrustAnchorLoader.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotACertificate_SaysSoRatherThanThrowing()
    {
        string junk = Path.Combine(_dir, "junk.pem");
        File.WriteAllText(junk, "-----BEGIN CERTIFICATE-----\nbm90IGEgY2VydA==\n-----END CERTIFICATE-----\n");

        Assert.Empty(TrustAnchorLoader.LoadFile(junk));
        Assert.NotNull(TrustAnchorLoader.LastError);
    }

    [Fact]
    public void AnEmptyDirectory_SaysItMatchedNoFiles()
    {
        Assert.Empty(TrustAnchorLoader.LoadDirectory(_dir));
        Assert.Contains("matched no files", TrustAnchorLoader.LastError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A successful load clears the reason a failed one left, so a caller reading it after a load
    /// sees that load — the per-thread-is-not-per-call distinction CLAUDE.md records for
    /// <c>LastPermsValidation</c>.
    /// </summary>
    [Fact]
    public void ASuccessfulLoad_ClearsTheReasonAFailedOneLeft()
    {
        Assert.Empty(TrustAnchorLoader.LoadFile(Path.Combine(_dir, "absent.pem")));
        Assert.NotNull(TrustAnchorLoader.LastError);

        string pem = Path.Combine(_dir, "root.pem");
        File.WriteAllText(pem, ToPem(SigningTestCrypto.Ca().Cert.GetEncoded()));

        Assert.Single(TrustAnchorLoader.LoadFile(pem));
        Assert.Null(TrustAnchorLoader.LastError);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => TrustAnchorLoader.Load(null!));
        Assert.Throws<ArgumentNullException>(() => TrustAnchorLoader.LoadFile(null!));
        Assert.Throws<ArgumentNullException>(() => TrustAnchorLoader.LoadDirectory(null!));
        Assert.Throws<ArgumentNullException>(() => TrustAnchorLoader.FromDer(null!));
    }

    // ------------------------------------------------------------------

    private static bool VerifyIsTrusted(byte[] signed, IReadOnlyList<Org.BouncyCastle.X509.X509Certificate>? anchors)
    {
        using var doc = Core.Document.PdfDocument.Open(new MemoryStream(signed));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = anchors,
        });

        return results.Count > 0 && results[0].IsFullyTrusted;
    }

    private static byte[] Sign(SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root)
    {
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);
        string path = Path.Combine(Path.GetTempPath(), $"anchor-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, pfx);
        try
        {
            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            using var output = new MemoryStream();
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificatePath = path,
                CertificatePassword = pw,
            });
            return output.ToArray();
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static string ToPem(byte[] der)
        => "-----BEGIN CERTIFICATE-----\n" +
           Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks) +
           "\n-----END CERTIFICATE-----\n";
}
