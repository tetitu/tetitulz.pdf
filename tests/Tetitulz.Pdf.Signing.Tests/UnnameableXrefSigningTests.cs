// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Signing a document whose cross-reference chain cannot be named is <b>refused</b>, before a byte
/// reaches the caller's stream.
/// </summary>
/// <remarks>
/// <para><b>What it used to do, and why it is the worst of the three possible outcomes.</b> The
/// appended revision declares only the three objects a signature writes — the signature dictionary,
/// the signature field and a cloned catalog — and with no nameable previous section it writes no
/// <c>/Prev</c>, so nothing re-declares the rest. On reopening, <c>/Root</c> resolves (it is one of
/// the three), <c>/Pages</c> resolves to nothing, and <c>PdfPageTree.GetPages</c> returns an empty
/// list <i>without throwing</i>. Every page, content stream and font is still physically in the
/// file; nothing names them.</para>
///
/// <para><b>And the signature verified.</b> <c>PdfSignatureVerifier</c> reaches signatures through
/// <c>AcroForm.GetSignatureFields()</c> and never consults the page tree, so every object it needs
/// is among the three the revision declared. The digest is over bytes nobody touched, so the result
/// was a cryptographically valid attestation over a document whose content the signer's own write
/// had made unreachable. A relying party had no way to tell.</para>
///
/// <para><b>Why refusal rather than recovery, when the edit path recovers.</b> The asymmetry is the
/// argument: a broken edit is loudly broken and retryable; a broken signature is quietly
/// authoritative. Core's <c>AddRecoveryEntries</c> re-declares the document from a scan-recovered
/// table — and that scan has no concept of a <i>free</i> entry, so an object deleted in a later
/// revision is resurrected. Recovering and then signing would attest to content the document had
/// deliberately removed. There is deliberately no opt-out flag: a caller who wants that should
/// repair the file explicitly and sign the repaired bytes, which is a separate inspectable step.</para>
///
/// <para><b>The fixture matters more than the assertions here.</b> See
/// <see cref="ZeroTheStartXrefOffset"/>.</para>
/// </remarks>
public sealed class UnnameableXrefSigningTests
{
    [Fact]
    public void SigningADocumentWhoseXrefChainCannotBeNamedIsRefused()
    {
        var (pfx, pw) = Certificate();
        byte[] damaged = ZeroTheStartXrefOffset(SignedPdfFixtures.MinimalPdf());
        AssertFixtureIsWhatItClaims(damaged);

        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(() => new PdfSigner().Sign(
            input, output, Parameters(pfx, pw)));

        Assert.Contains("cross-reference", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The assertion that separates "refused" from "refused after leaving a partial file on the
        // caller's disk". PdfSigner copies the input to the output early, so the check has to come
        // before that copy — this is what says it does.
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The floor. Without it the refusal degenerates into "refuse everything" and every assertion
    /// above still passes.
    /// </summary>
    [Fact]
    public void SigningAnUndamagedDocumentStillProducesAVerifiableSignatureOverItsPages()
    {
        var (pfx, pw) = Certificate();

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);

        using (var ms = new MemoryStream(signed))
        using (var doc = PdfDocument.Open(ms))
        {
            Assert.NotNull(doc.Catalog);
            Assert.Single(doc.Pages);   // the page survived — the thing the damage destroys
        }

        using var verify = new MemoryStream(signed);
        var results = new PdfSignatureVerifier().VerifyAll(PdfDocument.Open(verify));
        Assert.True(results.Single().IsIntegrityValid);
    }

    /// <summary>
    /// The same refusal from the two writers that build their own trailers the same way. Fixing
    /// only <c>PdfSigner</c> would be the "landed one level short" pattern this repository keeps
    /// recording.
    /// </summary>
    [Fact]
    public void TheTimestamperRefusesTheSameDocument()
    {
        byte[] damaged = ZeroTheStartXrefOffset(SignedPdfFixtures.MinimalPdf());
        AssertFixtureIsWhatItClaims(damaged);

        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();

        // The TSA URL is unreachable on purpose: the refusal must come first, so the test never
        // makes a network call. If this ever starts timing out instead of throwing, the check has
        // moved after the TSA request.
        var ex = Assert.Throws<PdfSigningException>(() =>
            Tetitulz.Pdf.Signing.Timestamping.PdfDocumentTimestamper.AddTimestamp(
                input, output, "http://tsa.invalid/"));

        Assert.Contains("cross-reference", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The premise, asserted before every use. <b>The second line is the load-bearing half</b>:
    /// without it a test cannot distinguish "the writer lost the page" from "the fixture never had
    /// one".
    /// </summary>
    private static void AssertFixtureIsWhatItClaims(byte[] damaged)
    {
        using var probe = new MemoryStream(damaged);
        using var doc = PdfDocument.Open(probe);

        Assert.Equal(0, doc.Parser.StartXrefOffset);   // the chain really is unnameable
        Assert.Single(doc.Pages);              // and it really is a one-page document
    }

    /// <summary>
    /// Overwrites the digits after the last <c>startxref</c> with zeroes — <b>same length</b>, so
    /// every other byte offset in the file stays valid.
    /// </summary>
    /// <remarks>
    /// <b>The obvious fixture does not work, and using it would produce a test that passes with the
    /// feature absent.</b> <c>UnusablePreviousXrefTests.WithTrailingJunk</c> appends 2000 bytes,
    /// which defeats the two 1 KB tail scans but sits comfortably inside
    /// <c>IncrementalUpdateWriter.StartXrefScanWindowBytes</c> (4096) — so the options overload
    /// finds the real <c>startxref</c> and chains normally on it. Offset <b>0</b> is the only value
    /// that defeats all three discovery mechanisms at once: it fails both tail scans by value and
    /// is rejected outright by the verifying classifier's <c>offset &lt;= 0</c> guard, so no wider
    /// re-scan can rescue it.
    /// </remarks>
    private static byte[] ZeroTheStartXrefOffset(byte[] pdf)
    {
        var copy = (byte[])pdf.Clone();
        string text = Encoding.ASCII.GetString(copy);
        int keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(keyword >= 0, "fixture: the writer must emit a startxref");

        int i = keyword + "startxref".Length;
        while (i < copy.Length && (copy[i] == (byte)'\r' || copy[i] == (byte)'\n' || copy[i] == (byte)' ')) i++;
        int digits = 0;
        while (i + digits < copy.Length && copy[i + digits] >= (byte)'0' && copy[i + digits] <= (byte)'9') digits++;
        Assert.True(digits > 0, "fixture: startxref must be followed by an offset");

        for (int d = 0; d < digits; d++) copy[i + d] = (byte)'0';
        return copy;
    }

    private static (byte[] Pfx, string Password) Certificate()
    {
        var root = SigningTestCrypto.Ca("CN=Unnameable Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Unnameable Leaf");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }

    private static SignatureParameters Parameters(byte[] pfx, string pw) => new()
    {
        CertificateStream = new MemoryStream(pfx),
        CertificatePassword = pw,
        SignatureFieldName = "Signature1",
        Reason = "test",
    };
}
