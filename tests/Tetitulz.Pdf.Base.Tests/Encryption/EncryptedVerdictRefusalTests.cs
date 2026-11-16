// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// The read-only entry points that cannot take a password must <b>refuse</b> an encrypted document
/// rather than answer as though it were empty.
/// </summary>
/// <remarks>
/// <para><b>"Cannot take a password" and "may answer anyway" are different claims, and only the
/// first was ever argued.</b> Each of these is on
/// <c>FacadePasswordCoverageTests.DeliberateExclusions</c> with a sound reason for declining the
/// parameter — two documents can need two different passwords; a DocMDP digest covers the file as
/// signed, which decrypting changes. None of those reasons licenses the answer that was actually
/// returned.</para>
///
/// <para><b>What was returned.</b> An encrypted document leaves <c>PdfDocument</c> with no readable
/// catalog, so it measures <b>zero pages</b> — and zero pages against zero pages is "equal". Two
/// completely different encrypted documents compared as <c>AreTextEqual = true</c>, and a certified
/// one reported "No DocMDP certification signature found". Both are verdicts, and both were wrong
/// in the direction a caller acts on.</para>
///
/// <para><b>Every test here is paired with the unencrypted control</b> for the same fixture, because
/// a refusal that fires on everything is not a fix — it is the same wrong answer with an exception
/// on it.</para>
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedVerdictRefusalTests
{
    private const string Alpha = "BT /F1 24 Tf 72 700 Td (Alpha document) Tj ET";
    private const string Beta = "BT /F1 24 Tf 72 700 Td (Totally different) Tj ET";

    /// <summary>
    /// Two different encrypted documents must not compare equal. This is the defect verbatim:
    /// before the refusal it returned <c>AreTextEqual = true</c> with zero pages on both sides.
    /// </summary>
    [Fact]
    public void ComparePdfText_RefusesAnEncryptedSide()
    {
        using var l = new MemoryStream(Encrypted(Alpha), writable: false);
        using var r = new MemoryStream(Encrypted(Beta), writable: false);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOperations.ComparePdfText(l, r));

        Assert.Contains("encrypted", ex.Message);
        Assert.Contains("identical", ex.Message);
    }

    /// <summary>Only one side needs to be encrypted, and the message says which.</summary>
    [Fact]
    public void ComparePdfText_NamesTheEncryptedSide()
    {
        using var l = new MemoryStream(Plain(Alpha), writable: false);
        using var r = new MemoryStream(Encrypted(Beta), writable: false);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOperations.ComparePdfText(l, r));

        Assert.Contains("right document", ex.Message);
    }

    /// <summary>
    /// The control. Two plain documents still compare, and the comparison still WORKS — a refusal
    /// that fired on everything would pass every assertion above.
    /// </summary>
    [Fact]
    public void ComparePdfText_StillComparesPlainDocuments()
    {
        using var l = new MemoryStream(Plain(Alpha), writable: false);
        using var r = new MemoryStream(Plain(Beta), writable: false);

        var result = PdfOperations.ComparePdfText(l, r);

        Assert.False(result.AreTextEqual, "these two documents differ and must be reported as differing");
        Assert.Equal(1, result.LeftPageCount);
        Assert.Equal(1, result.RightPageCount);
    }

    /// <summary>And two identical plain documents still compare equal, so the control cuts both ways.</summary>
    [Fact]
    public void ComparePdfText_StillReportsIdenticalPlainDocumentsAsEqual()
    {
        using var l = new MemoryStream(Plain(Alpha), writable: false);
        using var r = new MemoryStream(Plain(Alpha), writable: false);

        Assert.True(PdfOperations.ComparePdfText(l, r).AreTextEqual);
    }

    /// <summary>The pixel comparison has the same failure and the same fix.</summary>
    [Fact]
    public void ComparePdfVisual_RefusesAnEncryptedSide()
    {
        using var l = new MemoryStream(Encrypted(Alpha), writable: false);
        using var r = new MemoryStream(Encrypted(Beta), writable: false);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOperations.ComparePdfVisual(l, r));

        Assert.Contains("encrypted", ex.Message);
    }

    /// <summary>
    /// A DocMDP verdict on an encrypted document reported "no certification signature found" — a
    /// security answer about a document it had not read.
    /// </summary>
    [Fact]
    public void VerifyMdp_RefusesAnEncryptedDocument()
    {
        using var input = new MemoryStream(Encrypted(Alpha), writable: false);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfOperations.VerifyMdp(input));

        Assert.Contains("encrypted", ex.Message);
        Assert.Contains("uncertified", ex.Message);
    }

    /// <summary>The control: a plain document still gets its verdict.</summary>
    [Fact]
    public void VerifyMdp_StillVerifiesAPlainDocument()
    {
        using var input = new MemoryStream(Plain(Alpha), writable: false);

        var result = PdfOperations.VerifyMdp(input);

        Assert.False(result.HasCertificationSignature);
        Assert.True(result.FileLength > 0, "the verdict must be about a document it actually read");
    }

    /// <summary>
    /// <b>A byte-level reader must NOT start refusing.</b> <c>GetRevisions</c> reads the
    /// <c>%%EOF</c>/<c>startxref</c> chain, which is not encrypted, and answers correctly for an
    /// encrypted file — so a blanket "refuse encrypted input" sweep would have broken a working
    /// operation. It is on the same exclusions list for the opposite reason.
    /// </summary>
    [Fact]
    public void GetRevisions_StillReadsAnEncryptedDocument()
    {
        byte[] pdf = Encrypted(Alpha);
        using var input = new MemoryStream(pdf, writable: false);

        var revisions = PdfOperations.GetRevisions(input);

        // One revision, spanning the whole file. Asserting the LENGTH is what makes this
        // falsifiable: "a non-empty list came back" would pass for a walk that returned a
        // placeholder, and the claim here is that the byte-level read genuinely works through
        // encryption rather than merely surviving it.
        var only = Assert.Single(revisions);
        Assert.Equal(0, only.Index);
        Assert.Equal(pdf.Length, only.Length);
    }

    // ------------------------------------------------------------------

    private static byte[] Plain(string content) =>
        MinimalPdfBuilder.CreateMinimalPdf(contentStream: content);

    private static byte[] Encrypted(string content)
    {
        global::Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

        using var input = new MemoryStream(Plain(content));
        using var output = new MemoryStream();
        global::Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(
            input, output, new global::Tetitulz.Pdf.Base.Encryption.EncryptionParameters
            {
                UserPassword = "s3cret",
                OwnerPassword = "owner-only",
                Algorithm = global::Tetitulz.Pdf.Base.Encryption.EncryptionAlgorithm.AES128,
                Permissions = global::Tetitulz.Pdf.Base.Encryption.PdfPermissions.All,
            });
        return output.ToArray();
    }
}
