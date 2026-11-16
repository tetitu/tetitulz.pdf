// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Refusals that must be typed and honest, the chain walk's behaviour when several certificates
/// share a SubjectDN, and the culture-independence of a written PDF date.
/// </summary>
public sealed class SigningRefusalAndChainTests
{
    // ------------------------------------------------------------------ chain search

    /// <summary>
    /// A cross-signed intermediate — the same subject and key issued by two different roots,
    /// which is exactly what a CA root rollover produces — must not break the chain. Taking the
    /// first SubjectDN match and never backtracking reported <c>UntrustedRoot</c> whenever the bag
    /// listed the legacy-root copy first.
    /// </summary>
    /// <remarks>
    /// Driven through <c>CertificateChainValidator</c> directly rather than through a signed PDF:
    /// the order of a CMS certificate bag is BouncyCastle's to choose, so a PDF-level fixture
    /// would exercise the failing order only by luck. Here the store's order is the test's.
    /// </remarks>
    [Fact]
    public void ACrossSignedIntermediate_StillAnchorsToTheSuppliedRoot()
    {
        var legacyRoot = SigningTestCrypto.Ca("CN=Legacy Root");
        var currentRoot = SigningTestCrypto.Ca("CN=Current Root");

        var intermediateKeys = SigningTestCrypto.Keys();
        var viaLegacy = SigningTestCrypto.IssuedIntermediate(legacyRoot, intermediateKeys, "CN=Rolled Intermediate");
        var viaCurrent = SigningTestCrypto.IssuedIntermediate(currentRoot, intermediateKeys, "CN=Rolled Intermediate");
        var leaf = SigningTestCrypto.Leaf(viaLegacy, "CN=Rollover Signer");

        // The legacy-root copy first: the order that used to dead-end at an unanchored root.
        var store = CollectionUtilities.CreateStore(new List<X509Certificate>
        {
            leaf.Cert, viaLegacy.Cert, viaCurrent.Cert, legacyRoot.Cert, currentRoot.Cert,
        });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf.Cert, store, new[] { currentRoot.Cert }, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Trusted, status);
    }

    /// <summary>
    /// The attacker half of the same defect: a CMS certificate bag is unauthenticated, so anyone
    /// can prepend a certificate with a matching SubjectDN and a different key. Committing to the
    /// first match made the link verification fail and reported <c>Invalid</c> — a remote denial
    /// of trust on a genuine signature.
    /// </summary>
    [Fact]
    public void ASameSubjectDecoyCertificate_DoesNotInvalidateAGenuineChain()
    {
        var root = SigningTestCrypto.Ca("CN=Real Root");
        var decoy = SigningTestCrypto.Ca("CN=Real Root");   // same DN, different key
        var leaf = SigningTestCrypto.Leaf(root, "CN=Genuine Signer");

        var store = CollectionUtilities.CreateStore(new List<X509Certificate>
        {
            leaf.Cert, decoy.Cert, root.Cert,   // decoy first
        });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf.Cert, store, new[] { root.Cert }, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Trusted, status);
    }

    /// <summary>
    /// The control: an unanchored root is still untrusted. Trying every candidate must not turn
    /// into accepting any candidate.
    /// </summary>
    [Fact]
    public void AnUnanchoredRoot_IsStillReportedUntrusted()
    {
        var root = SigningTestCrypto.Ca("CN=Some Other Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Signer");

        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { leaf.Cert, root.Cert });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf.Cert, store, new[] { SigningTestCrypto.Ca("CN=Unrelated Anchor").Cert },
            DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.UntrustedRoot, status);
    }

    // ------------------------------------------------------------------ encrypted refusals

    /// <summary>
    /// "Document has no AcroForm." is true of an unencrypted file and false of this one: for an
    /// encrypted document the catalog is never read, so the form is unreadable rather than absent.
    /// An operator reading the old message concludes the file is not signed and stops.
    /// </summary>
    [Fact]
    public void CounterSigningAnEncryptedDocument_SaysItIsEncrypted()
    {
        var (pfx, pw) = SigningTestCrypto.Pfx(SigningTestCrypto.Leaf(SigningTestCrypto.Ca()));

        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() =>
            PdfCounterSigner.AddCounterSignature(input, output, "Signature1", pfx, pw));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The timestamper dereferenced the (null) catalog of an encrypted document and threw
    /// <see cref="NullReferenceException"/> — after it had already copied the input into the
    /// caller's stream, leaving a partial file behind. Both halves are asserted: a typed refusal,
    /// and nothing written.
    /// </summary>
    [Fact]
    public void TimestampingAnEncryptedDocument_RefusesBeforeWritingAnything()
    {
        using var input = new MemoryStream(EncryptedPdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfException>(() =>
            PdfSigningOperations.AddDocumentTimestamp(input, output, "http://localhost:1/tsa"));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------ culture

    /// <summary>
    /// <c>DateTime.ToString("yyyyMMddHHmmss")</c> uses the current culture's CALENDAR, so under
    /// th-TH the year is written in the Buddhist era (2569 for 2026) and the <c>/M</c> date becomes
    /// nonsense no reader accepts. The TSA call fails (there is no server), which is fine: the
    /// bytes written before it are what this test reads.
    /// </summary>
    [Fact]
    public void TheTimestampFieldDate_IsWrittenInTheInvariantCalendar()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");

            using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
            using var output = new MemoryStream();
            Record.Exception(() =>
                PdfSigningOperations.AddDocumentTimestamp(input, output, "http://localhost:1/tsa"));

            string written = Encoding.Latin1.GetString(output.ToArray());
            int at = written.IndexOf("/M (D:", StringComparison.Ordinal);
            Assert.True(at >= 0, "the timestamp revision was not written, so there is no /M to check");

            string year = written.Substring(at + "/M (D:".Length, 4);
            Assert.True(year.StartsWith("20", StringComparison.Ordinal),
                $"/M was written with year '{year}' — a non-Gregorian calendar reached the file.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // ------------------------------------------------------------------ weak-algorithm default

    /// <summary>
    /// Pins the default the public docs describe. Two doc blocks claimed the opposite (SHA-1
    /// "still verifies") long after the default flipped, and a caller writing
    /// <c>if (!r.IsValid) Reject(); else if (r.WeakAlgorithm) Log();</c> from them would reject
    /// their whole SHA-1 archive.
    /// </summary>
    [Fact]
    public void RejectWeakAlgorithms_DefaultsToTrue()
    {
        Assert.True(new SignatureVerificationOptions().RejectWeakAlgorithms);
        Assert.True(SignatureVerificationOptions.Default.RejectWeakAlgorithms);
    }

    // ------------------------------------------------------------------ helpers

    private static SignatureVerificationResult Verify(byte[] pdf, X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { anchor },
        });
        Assert.NotEmpty(results);
        return results[0];
    }

    /// <summary>A document carrying an <c>/Encrypt</c> dictionary, which is all these paths read.</summary>
    private static byte[] EncryptedPdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string hex = new string('a', 64);

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj($"4 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 /O <{hex}> /U <{hex}> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat(
            "trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
            + "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] >>\n"
            + "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
