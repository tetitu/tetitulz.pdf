// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfEncryptionOperations.VerifyMdp"/> answers <b>both halves</b> of a DocMDP verdict
/// for an encrypted certified document: whether it is certified and at what <c>/P</c>, and whether
/// it has been modified since.
/// </summary>
/// <remarks>
/// <para><b>Base refuses this outright</b>, and correctly — a DocMDP verdict draws on two views of
/// the file that decryption pulls apart. The certification half needs the object graph, which an
/// encrypted document does not expose; the modification half is decided by comparing
/// cross-reference <b>offsets</b> against the certification <c>/ByteRange</c>, and a decrypted copy
/// is re-serialised, so its offsets are its own while the stored <c>/ByteRange</c> still indexes
/// the original.</para>
///
/// <para><b>So the discriminating test is the appended-revision one.</b> Answering "is it
/// certified" proves only that something got decrypted. What separates this implementation from the
/// naive "decrypt the copy and analyse that" is where the <i>offsets</i> came from: the decrypted
/// copy is a single fresh serialisation and contains no appended revision at all, so it can never
/// report one. The original does.</para>
/// </remarks>
public sealed class EncryptedDocMdpVerdictTests
{
    private const string Password = "mdp-pw";

    /// <summary>
    /// The certification half: an encrypted certified document is reported certified, at its
    /// declared <c>/P</c>.
    /// </summary>
    /// <remarks>
    /// Impossible through <c>Base</c>, which refuses — an encrypted document loads no catalog, so
    /// the AcroForm walk finds nothing and the honest answer there is a refusal rather than
    /// "uncertified".
    /// </remarks>
    [Fact]
    public void AnEncryptedCertifiedDocumentIsReportedCertifiedAtItsPermissionLevel()
    {
        byte[] encrypted = EncryptedCertifiedPdf();

        using var input = new MemoryStream(encrypted, writable: false);
        DocMdpAnalysis result = PdfEncryptionOperations.VerifyMdp(input, Password);

        Assert.True(result.HasCertification, "the certification signature was not found");
        Assert.Equal(2, result.PermissionLevel);
    }

    /// <summary>
    /// The verdict measures the <b>original</b> file, not the decrypted copy.
    /// </summary>
    /// <remarks>
    /// The two lengths are asserted to differ first. Without that this passes vacuously on any
    /// document where decryption happens to preserve the size, which is the shape of test that
    /// looks like evidence and is not.
    /// </remarks>
    [Fact]
    public void TheVerdictMeasuresTheOriginalFileRatherThanTheDecryptedCopy()
    {
        byte[] encrypted = EncryptedCertifiedPdf();

        using var forCopy = new MemoryStream(encrypted, writable: false);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(forCopy, decrypted, Password);
        Assert.NotEqual(encrypted.Length, decrypted.Length);

        using var input = new MemoryStream(encrypted, writable: false);
        DocMdpAnalysis result = PdfEncryptionOperations.VerifyMdp(input, Password);

        Assert.Equal(encrypted.Length, result.FileLength);
    }

    /// <summary>
    /// The modification half, and the assertion that separates this from analysing the decrypted
    /// copy: a revision appended after certification is <b>seen</b>.
    /// </summary>
    /// <remarks>
    /// <para>The decrypted copy is one fresh serialisation with a single cross-reference section —
    /// it carries no appended revision whatever the original had, so an implementation reading
    /// offsets from it reports <c>HasIncrementalUpdates = false</c> here no matter what was
    /// appended. Reading them from the original is the only way this can be true.</para>
    /// <para>Paired with the unmodified control below, because "there are updates" is also what a
    /// broken comparison returns for everything.</para>
    /// </remarks>
    [Fact]
    public void ARevisionAppendedAfterCertificationIsSeen()
    {
        byte[] encrypted = AppendTrailingRevision(EncryptedCertifiedPdf());

        using var input = new MemoryStream(encrypted, writable: false);
        DocMdpAnalysis result = PdfEncryptionOperations.VerifyMdp(input, Password);

        Assert.True(result.HasCertification);
        Assert.True(result.HasIncrementalUpdates,
            "the appended revision was not seen; the offsets are not coming from the original file");
    }

    /// <summary>
    /// The control: with nothing appended, the same document reports no updates.
    /// </summary>
    /// <remarks>
    /// Without this the test above is satisfied by an implementation that reports updates
    /// unconditionally — which is exactly what a comparison against the wrong stream's length
    /// tends to do.
    /// </remarks>
    [Fact]
    public void WithNothingAppendedTheSameDocumentReportsNoUpdates()
    {
        byte[] encrypted = EncryptedCertifiedPdf();

        using var input = new MemoryStream(encrypted, writable: false);
        DocMdpAnalysis result = PdfEncryptionOperations.VerifyMdp(input, Password);

        Assert.True(result.HasCertification);
        Assert.False(result.HasIncrementalUpdates,
            "an unmodified document reported modifications; the byte-range comparison is wrong");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Encrypts a synthetic certified document. The certification signature is <b>structural</b> —
    /// <c>/Perms /DocMDP</c> pointing at a signature dictionary carrying
    /// <c>/Reference /TransformMethod /DocMDP</c> and <c>/TransformParams /P 2</c>.
    /// </summary>
    /// <remarks>
    /// <c>DocMdpAnalyzer</c> reads the certification level and byte range; it does not verify the
    /// signature, which is <c>PdfSignatureVerifier</c>'s job. A real CMS here would add a
    /// dependency on the signing stack for nothing this test asserts.
    /// </remarks>
    private static byte[] EncryptedCertifiedPdf()
    {
        // Iterated to a fixed point, and the iteration IS the point: a document is certified
        // AFTER it is encrypted (certifying first and then encrypting would invalidate the
        // signature), so the certification /ByteRange has to name the ENCRYPTED file's end. The
        // first version of this fixture set it on the plaintext and encrypted afterwards, which
        // moved the end and made an unmodified document report modifications - a fixture defect
        // that looked exactly like the code defect being tested for.
        //
        // Only the digit count of the embedded number can change the length, so this converges in
        // two or three passes; the loop asserts that it did rather than assuming it.
        byte[] encrypted = Encrypt(CertifiedPdf(0));
        for (int i = 0; i < 5; i++)
        {
            byte[] next = Encrypt(CertifiedPdf(encrypted.Length));
            if (next.Length == encrypted.Length) return next;
            encrypted = next;
        }

        Assert.Fail("the certified fixture's length did not settle; /ByteRange cannot name the file end");
        return null;
    }

    private static byte[] Encrypt(byte[] plaintext)
    {
        using var plain = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
            Algorithm = EncryptionAlgorithm.AES128,
        });
        return encrypted.ToArray();
    }

    /// <summary>
    /// Appends bytes past the last <c>%%EOF</c> so the file is longer than the range the
    /// certification signature covers.
    /// </summary>
    /// <remarks>
    /// A whole valid incremental revision is not needed and would obscure what is being tested: the
    /// question is whether the analysis measures the original stream, and the certification
    /// <c>/ByteRange</c> covering less than the file is exactly that condition.
    /// </remarks>
    private static byte[] AppendTrailingRevision(byte[] encrypted)
    {
        byte[] tail = Encoding.ASCII.GetBytes(
            "\n9 0 obj\n<< /Type /Appended >>\nendobj\n%%EOF\n");
        var combined = new byte[encrypted.Length + tail.Length];
        System.Buffer.BlockCopy(encrypted, 0, combined, 0, encrypted.Length);
        System.Buffer.BlockCopy(tail, 0, combined, encrypted.Length, tail.Length);
        return combined;
    }

    /// <summary>
    /// A one-page document whose catalog carries <c>/Perms /DocMDP</c>. The signature's
    /// <c>/ByteRange</c> covers the file as written.
    /// </summary>
    private static byte[] CertifiedPdf(int signedEnd)
    {
        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP 5 0 R >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]"
            + " /Contents 4 0 R >>\nendobj\n");

        const string content = "BT /F1 12 Tf 20 100 Td (certified) Tj ET\n";
        Obj(string.Format(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}endstream\nendobj\n",
            content.Length, content));

        // The certification signature. /ByteRange is filled in below, once the total is known.
        Obj("5 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached"
            + " /ByteRange [0 0 0 " + signedEnd.ToString(CultureInfo.InvariantCulture) + "] /Contents <00>"
            + " /Reference [<< /Type /SigRef /TransformMethod /DocMDP"
            + " /TransformParams << /Type /TransformParams /P 2 /V /1.2 >> >>] >>\nendobj\n");

        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture)
                  + " /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
