// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Diagnostic-slot behaviour for crypt filters: what the decryptor tells a caller about how the
/// document's <c>/StmF</c> and <c>/StrF</c> actually resolved.
/// </summary>
/// <remarks>
/// <para>These are not cryptographic assertions — no fallback here weakens a key. They matter
/// because the slot is the only channel by which a caller learns that a document presenting itself
/// as encrypted left half of itself in cleartext, or that its crypt filter was guessed rather than
/// read. A host deciding "is this document protected?" reads this and nothing else.</para>
///
/// <para><b>The two defects pinned here have the same root.</b> <c>ResolveCryptFilter</c> is called
/// twice per document — once for <c>/StmF</c>, once for <c>/StrF</c> — so clearing the slot on
/// entry and assigning would let a clean string filter destroy the stream finding, and the
/// commonest shape of "clean" (<c>/StrF</c> absent or <c>/Identity</c>) returns within two lines of
/// such a reset. It is invisible on the ordinary document where both keys name the same filter,
/// because both calls then produce identical text — so it fails precisely on the asymmetric
/// documents worth auditing.</para>
/// </remarks>
public class CryptFilterDiagnosticTests
{
    /// <summary>
    /// A <c>/StmF</c> finding must survive a <c>/StrF</c> that resolves without incident.
    /// </summary>
    [Fact]
    public void StreamFilterFinding_IsNotErasedByACleanStringFilter()
    {
        // /StmF names a filter absent from /CF (a finding); /StrF is /Identity (returns immediately).
        byte[] pdf = EncryptedShell(
            "/StmF /NoSuchFilter /StrF /Identity /CF << /StdCF << /CFM /AESV2 >> >>");

        Decrypt(pdf);

        string slot = PdfDecryptor.LastCryptFilterFallback;

        Assert.NotNull(slot);
        Assert.Contains("NoSuchFilter", slot);
    }

    /// <summary>
    /// Both halves report, and each says which key it is about — otherwise a caller reading one
    /// message cannot tell whether it concerns the streams or the strings.
    /// </summary>
    [Fact]
    public void BothCryptFilterKeys_AreReportedAndAttributed()
    {
        byte[] pdf = EncryptedShell(
            "/StmF /MissingA /StrF /MissingB /CF << /StdCF << /CFM /AESV2 >> >>");

        Decrypt(pdf);

        string slot = PdfDecryptor.LastCryptFilterFallback;

        Assert.NotNull(slot);
        Assert.Contains("StmF", slot);
        Assert.Contains("StrF", slot);
        Assert.Contains("MissingA", slot);
        Assert.Contains("MissingB", slot);
    }

    /// <summary>
    /// <c>/Identity</c> means "these objects are in cleartext". A document that declares itself
    /// encrypted while saying that must not resolve in silence.
    /// </summary>
    [Fact]
    public void IdentityStreamFilter_InsideAnEncryptedDocument_IsReported()
    {
        byte[] pdf = EncryptedShell(
            "/StmF /Identity /StrF /StdCF /CF << /StdCF << /CFM /AESV2 >> >>");

        Decrypt(pdf);

        // Asserted as content, not as nullness. Leading with `slot != null` reads as "something came
        // back", which is the shape NoUnfalsifiableAssertionsTests rejects — and rightly: a slot
        // populated by any unrelated finding would satisfy it. The sentinel keeps the failure
        // message readable when nothing was reported at all.
        string slot = PdfDecryptor.LastCryptFilterFallback ?? "<the decryptor reported nothing>";

        Assert.Contains("Identity", slot);
        Assert.Contains("StmF", slot);
    }

    /// <summary>
    /// The floor. A document whose crypt filters resolve cleanly must leave the slot empty, or the
    /// assertions above pass against a slot that is simply always populated.
    /// </summary>
    [Fact]
    public void CleanlyResolvedCryptFilters_LeaveNoFinding()
    {
        byte[] pdf = EncryptFixture();

        Decrypt(pdf, "secret");

        Assert.Null(PdfDecryptor.LastCryptFilterFallback);
    }

    /// <summary>
    /// Per-thread storage is not per-document scope: a finding from one document must not still be
    /// in the slot while a caller inspects the next one.
    /// </summary>
    /// <remarks>
    /// CLAUDE.md records this exact confusion as the cautionary tale for <c>LastPermsValidation</c>.
    /// <c>LastCryptFilterFallback</c> and <c>LastV5KeyDerivationIssue</c> had the same defect: their
    /// resets lived on the branch that writes them rather than at the start of the operation, so
    /// they were inherited by any document that did not take that branch.
    /// </remarks>
    [Fact]
    public void CryptFilterFinding_DoesNotBleedIntoTheNextDocument()
    {
        Decrypt(EncryptedShell("/StmF /MissingA /StrF /MissingB /CF << /StdCF << /CFM /AESV2 >> >>"));
        Assert.NotNull(PdfDecryptor.LastCryptFilterFallback);   // the slot is dirty going in…

        Decrypt(EncryptFixture(), "secret");

        Assert.Null(PdfDecryptor.LastCryptFilterFallback);
    }

    // ---------------------------------------------------------------- helpers

    private static void Decrypt(byte[] pdf, string password = "x")
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        try { PdfDecryptor.Decrypt(input, output, password); }
        catch (Tetitulz.Pdf.Core.PdfException)
        {
            // Several of these fixtures carry a deliberately unopenable /Encrypt dictionary. The
            // crypt-filter resolution the slot reports happens before any password check, so the
            // finding is recorded either way and refusing to open is not what is under test here.
        }
    }

    /// <summary>A genuine AES-256 document produced by the encryptor, for the floor cases.</summary>
    private static byte[] EncryptFixture()
    {
        using var plain = new MemoryStream(PlainPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = "secret",
            OwnerPassword = "owner",
            Algorithm     = EncryptionAlgorithm.AES256,
        });
        return encrypted.ToArray();
    }

    /// <summary>
    /// A document carrying a hand-written <c>/Encrypt</c> dictionary, which is all the crypt-filter
    /// resolution path reads.
    /// </summary>
    private static byte[] EncryptedShell(string cryptFilterKeys)
    {
        string hex = new string('a', 64);

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj($"4 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 /P -1 {cryptFilterKeys} "
          + $"/O <{hex}> /U <{hex}> >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R /Encrypt 4 0 R "
                      + "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] "
                      + ">>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static byte[] PlainPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (plain) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
