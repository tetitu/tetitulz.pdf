// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

/// <summary>
/// <see cref="PdfBlockchainSigner.EmbedBlockchainProof"/> is a public static that takes an
/// arbitrary <c>byte[]</c>, so every trailer shape a file can carry reaches it. Dereferencing the
/// parse result unchecked, a dangling <c>/Root</c> would escape as a
/// <see cref="NullReferenceException"/> out of an API whose every other failure is a
/// <see cref="PdfSigningException"/>, and a <i>direct</i> <c>/Root</c> would give object number 0 —
/// reserved as the head of the free list — so the catalog would be written unreferenced and the
/// proof end up in a malformed file that reported success.
///
/// <para>These assert the guard's own evidence: a typed exception naming the condition. None of
/// them measures time, and none reaches the network.</para>
/// </summary>
public class EmbedBlockchainProofHostileInputTests
{
    private static BlockchainProof AProof() => new BlockchainProof
    {
        TransactionHash = "0xfeed",
        BlockNumber = 7,
        Timestamp = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        DocumentHash = "abcd",
        Network = "hardhat",
        SignerAddress = "0xsigner",
    };

    [Fact]
    public void A_dangling_Root_is_refused_with_a_typed_exception()
    {
        // /Root points at object 9, which the file never defines.
        byte[] pdf = PdfWithTrailerRoot("9 0 R");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, AProof()));

        Assert.Contains("/Root", ex.Message);
        Assert.Contains("catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_Root_resolving_to_a_non_dictionary_is_refused_with_a_typed_exception()
    {
        // Object 6 exists but is a number, not a catalog dictionary.
        byte[] pdf = PdfWithTrailerRoot("6 0 R", extraBody: "42");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, AProof()));

        Assert.Contains("catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A direct <c>/Root</c> has no object number to supersede. The old code answered 0 and wrote
    /// <c>0 0 obj</c> plus an xref entry for it — object 0 is the free-list head, so the catalog it
    /// emitted was unreachable and the proof unfindable, with no error anywhere.
    /// </summary>
    [Fact]
    public void A_direct_Root_dictionary_is_refused_rather_than_written_as_object_zero()
    {
        byte[] pdf = PdfWithTrailerRoot("<< /Type /Catalog /Pages 2 0 R >>");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, AProof()));

        Assert.Contains("indirect reference", ex.Message);
    }

    /// <summary>
    /// An encrypted document's strings are ciphertext under a per-object key. The proof written
    /// here is clear text, so a reader would "decrypt" it into noise — and this assembly holds no
    /// decryptor by design (isolating Nethereum is the point of the package; dragging the crypto
    /// stack in beside it is not). Fail closed, with the reason.
    /// </summary>
    [Fact]
    public void An_encrypted_document_is_refused_with_a_reason()
    {
        byte[] pdf = EncryptedPdf();

        // Fixture check: the document really is the condition under test.
        using (var probe = new MemoryStream(pdf))
        using (var doc = PdfDocument.Open(probe))
            Assert.True(doc.IsEncrypted, "the fixture is not encrypted, so it tests nothing.");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, AProof()));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(null, AProof()));
        Assert.Throws<ArgumentNullException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(
                MinimalPdfBuilder.CreateMinimalPdf("x"), null));
    }

    /// <summary>
    /// The proof is an <b>incremental update</b>: the notarized bytes must survive verbatim as the
    /// file's prefix, because <c>/HashedLength</c> is the length of exactly that prefix and the
    /// verifier re-hashes it. A writer that rewrote any earlier byte would break every proof it
    /// produced while still parsing perfectly.
    /// </summary>
    [Fact]
    public void The_original_bytes_survive_verbatim_as_the_prefix()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");

        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, AProof());

        Assert.True(withProof.Length > original.Length);
        for (int i = 0; i < original.Length; i++)
            Assert.True(original[i] == withProof[i],
                $"byte {i} of the notarized prefix was rewritten; /HashedLength can no longer " +
                "identify the bytes the proof covers.");

        using var ms = new MemoryStream(withProof);
        using var doc = PdfDocument.Open(ms);
        var proofDict = doc.Catalog.Dictionary[PdfName.BlockchainProof] as PdfDictionary;
        Assert.NotNull(proofDict);
        Assert.Equal(original.Length, proofDict.GetInteger(new PdfName("HashedLength")) ?? -1);
    }

    /// <summary>
    /// The appended trailer must carry <c>/ID</c>. The hand-rolled writer this replaced carried
    /// none, so every notarized document lost the file identifier a reader uses to tell one
    /// revision's provenance from another's.
    /// </summary>
    [Fact]
    public void The_appended_revision_carries_a_file_identifier()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("id test");

        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, AProof());

        using var ms = new MemoryStream(withProof);
        using var doc = PdfDocument.Open(ms);
        var id = doc.Trailer[PdfName.ID] as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id.Count);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A four-object PDF whose trailer's <c>/Root</c> is written verbatim from
    /// <paramref name="rootEntry"/>, so the caller chooses the shape under test.
    /// <paramref name="extraBody"/>, when supplied, becomes object 6.
    /// </summary>
    private static byte[] PdfWithTrailerRoot(string rootEntry, string extraBody = null)
    {
        var bodies = extraBody == null
            ? new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            }
            : new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
                "<< /Dummy 1 >>",
                "<< /Dummy 2 >>",
                extraBody,
            };

        return Build($"/Size {bodies.Length + 1} /Root {rootEntry}", bodies);
    }

    /// <summary>
    /// Declares <c>/Encrypt</c> in the trailer, which is the whole of what
    /// <c>PdfDocument.Load</c> tests — so this is a genuinely encrypted document as far as every
    /// reader in this assembly is concerned, built without referencing
    /// <c>Tetitulz.Pdf.Encryption</c> (which these plugins deliberately do not).
    /// </summary>
    private static byte[] EncryptedPdf() => Build(
        "/Size 5 /Root 1 0 R /Encrypt 4 0 R",
        new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
                "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                "/U <202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F> >>",
        });

    private static byte[] Build(string trailerEntries, string[] bodies)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = pdf.Length;
            pdf.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++)
            pdf.AppendFormat("{0:D10} 00000 n \n", offsets[i]);

        pdf.Append("trailer\n<< ").Append(trailerEntries).Append(" >>\n");
        pdf.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
