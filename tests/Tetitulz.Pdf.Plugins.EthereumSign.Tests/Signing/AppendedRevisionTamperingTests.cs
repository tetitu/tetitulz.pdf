// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

/// <summary>
/// The proof covers the document <b>prefix</b> named by <c>/HashedLength</c> and says nothing about
/// bytes appended after it — and a PDF is append-only editable, so an attacker never has to touch
/// the notarized bytes at all. One incremental update supersedes the page's content stream, the
/// document renders entirely different text, and every byte the hash covers is untouched and
/// re-hashes to exactly the value on the chain.
///
/// <para>That made <c>TransactionFound &amp;&amp; HashMatches &amp;&amp; SignerMatches</c> all true
/// and <see cref="BlockchainVerificationResult.IsValid"/> <c>true</c> for a document showing the
/// attacker's content. The <c>/HashedLength</c> round closed "the notarized bytes were edited"; it
/// could not see this, which is the opposite attack.</para>
///
/// <para><b>Offline.</b> These pass an already-cancelled token, which
/// <c>BlockchainSignatureProvider.VerifyHashAsync</c> observes before it constructs a Web3 client —
/// but only after recording the hash it was handed on
/// <see cref="BlockchainVerificationResult.ComputedHash"/>. That field is therefore an exact
/// read-out of which bytes the verifier presented to the chain, which is what makes the attack
/// visible without a node.</para>
/// </summary>
public class AppendedRevisionTamperingTests
{
    private static BlockchainParameters LocalParameters() =>
        new BlockchainParameters { RpcUrl = "http://127.0.0.1:8545", ChainId = 31337 };

    [Fact]
    public async Task A_revision_appended_after_the_proof_is_refused_although_the_prefix_still_hashes_true()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        string notarizedHash = Sha256Hex(original);

        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = notarizedHash,
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        // The attack: append — never edit — an incremental update that supersedes the page's
        // content stream. Not one byte of the notarized prefix changes.
        byte[] attacked = AppendContentStreamOverride(withProof, "PAYOUT 900 USD");

        Assert.True(attacked.Length > withProof.Length);
        for (int i = 0; i < withProof.Length; i++)
            Assert.True(withProof[i] == attacked[i],
                $"byte {i} was modified; this fixture must be a pure append or it tests the " +
                "wrong attack.");

        using var ms = new MemoryStream(attacked);
        using var doc = PdfDocument.Open(ms);

        // Fixture check: the append really did take effect — the document now renders the
        // attacker's text. Without this the assertions below could be satisfied by an append that
        // changed nothing.
        var content = doc.GetObject(4) as PdfStream;
        Assert.NotNull(content);
        Assert.Contains("PAYOUT 900 USD",
                        Encoding.ASCII.GetString(content!.GetDecodedData()));

        // And the proof is still intact and still points at the original bytes.
        var proofDict = doc.Catalog.Dictionary[PdfName.BlockchainProof] as PdfDictionary;
        Assert.NotNull(proofDict);
        Assert.Equal(notarizedHash, (proofDict![new PdfName("DocumentHash")] as PdfString)?.GetText());

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        // The hash the verifier presents to the chain is STILL the on-chain value — the notarized
        // prefix is genuinely untouched. That is exactly why the hash check alone cannot see this.
        Assert.Equal(notarizedHash, result.ComputedHash);

        // The load-bearing pair.
        Assert.False(result.IsLastRevision);
        Assert.False(result.IsValid);
        Assert.Contains("appended", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <b>The rewrite in the proof's own revision.</b> Nothing follows the revision and the prefix still
    /// hashes true, so the check on appended bytes has nothing to see: the forger replaces the genuine
    /// tail with one carrying the same proof and the page they want.
    /// </summary>
    [Fact]
    public async Task AdversarialRewrite_InTheProofsOwnRevision_IsRefused()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        string notarizedHash = Sha256Hex(original);
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = notarizedHash,
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        byte[] forged = ReviseWithProof(original, withProof, "4 0 obj\n<< /Length 18 >>\nstream\n(PAYOUT 900 USD) Tj\nendstream\nendobj\n", 4);

        using var ms = new MemoryStream(forged);
        using var doc = PdfDocument.Open(ms);
        Assert.Contains("PAYOUT 900 USD", Encoding.ASCII.GetString((doc.GetObject(4) as PdfStream)!.GetDecodedData()));

        var result = await new PdfBlockchainVerifier().VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        Assert.Equal(notarizedHash, result.ComputedHash);
        Assert.False(result.IsLastRevision);
        Assert.False(result.IsValid);
        Assert.Contains("obj 4", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A catalog changed beside the proof is the same forgery, there being no other object to see.</summary>
    [Fact]
    public async Task AdversarialCatalogEntry_BesideTheProof_IsRefused()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = Sha256Hex(original),
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        string tail = Encoding.Latin1.GetString(withProof, original.Length, withProof.Length - original.Length);
        byte[] forged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(original) + tail.Replace("/BlockchainProof", "/OpenAction 4 0 R /BlockchainProof"));
        // Same bytes up to the catalog, moved along by the added entry: the table's offset for it stays true.
        forged = FixStartXref(forged, original.Length);

        using var ms = new MemoryStream(forged);
        using var doc = PdfDocument.Open(ms);
        var result = await new PdfBlockchainVerifier().VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        Assert.False(result.IsLastRevision);
        Assert.Contains("OpenAction", result.Error, StringComparison.Ordinal);
    }

    /// <summary>The catalog and proof copied from the genuine tail, written with the rewrite, under one table.</summary>
    private static byte[] ReviseWithProof(byte[] original, byte[] withProof, string extra, int extraNumber)
    {
        string tail = Encoding.Latin1.GetString(withProof, original.Length, withProof.Length - original.Length);
        int end = tail.IndexOf("endobj", StringComparison.Ordinal) + "endobj\n".Length;
        string catalog = tail.Substring(0, end);
        int catalogNumber = int.Parse(catalog.Substring(0, catalog.IndexOf(' ')), System.Globalization.CultureInfo.InvariantCulture);

        string prefix = Encoding.Latin1.GetString(original);
        int prev = prefix.LastIndexOf("startxref", StringComparison.Ordinal);
        long prevXref = long.Parse(prefix.Substring(prev + 9, prefix.IndexOf("%%EOF", prev, StringComparison.Ordinal) - prev - 9).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        long extraAt = original.Length;
        long catalogAt = extraAt + extra.Length;
        long xref = catalogAt + catalog.Length;
        var first = extraNumber < catalogNumber ? (extraNumber, extraAt) : (catalogNumber, catalogAt);
        var second = extraNumber < catalogNumber ? (catalogNumber, catalogAt) : (extraNumber, extraAt);
        string table = "xref\n"
            + first.Item1 + " 1\n" + first.Item2.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n"
            + second.Item1 + " 1\n" + second.Item2.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n"
            + "trailer\n<< /Size 100 /Root " + catalogNumber + " 0 R /Prev " + prevXref + " >>\nstartxref\n" + xref + "\n%%EOF\n";
        return Encoding.Latin1.GetBytes(prefix + extra + catalog + table);
    }

    /// <summary>The last <c>startxref</c> pointed at the table again, after an edit moved it.</summary>
    private static byte[] FixStartXref(byte[] pdf, int revisionStart)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int table = text.IndexOf("xref\n", revisionStart, StringComparison.Ordinal);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(text.Substring(0, at) + "startxref\n" + table + "\n%%EOF\n");
    }

    /// <summary>
    /// The control that keeps the check narrow: an untouched notarized document must still report
    /// <see cref="BlockchainVerificationResult.IsLastRevision"/> true. A guard that answered false
    /// for everything would satisfy the test above and reject every genuine proof.
    /// </summary>
    [Fact]
    public async Task An_untouched_notarized_document_is_still_the_last_revision()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = Sha256Hex(original),
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        using var ms = new MemoryStream(withProof);
        using var doc = PdfDocument.Open(ms);

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        Assert.True(result.IsLastRevision,
            "an untampered notarized document was reported as having appended content.");
    }

    /// <summary>
    /// Trailing whitespace after <c>%%EOF</c> is not an appended revision. Producers and transports
    /// add a newline routinely, and a check that called that tampering would fire on conformant
    /// files — a channel that fires on good input is one a caller learns to ignore.
    /// </summary>
    [Fact]
    public async Task Trailing_whitespace_is_not_an_appended_revision()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = Sha256Hex(original),
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        byte[] padded = new byte[withProof.Length + 4];
        Array.Copy(withProof, padded, withProof.Length);
        padded[withProof.Length + 0] = (byte)'\r';
        padded[withProof.Length + 1] = (byte)'\n';
        padded[withProof.Length + 2] = (byte)' ';
        padded[withProof.Length + 3] = (byte)'\n';

        using var ms = new MemoryStream(padded);
        using var doc = PdfDocument.Open(ms);

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        Assert.True(result.IsLastRevision,
            "a trailing newline was mistaken for an appended revision.");
    }

    // ── fixture helpers ──────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Appends a real incremental update superseding object 4 — the content stream
    /// <see cref="MinimalPdfBuilder"/> writes — with one drawing <paramref name="newText"/>.
    /// Nothing before the append is touched, which is the whole point of the attack.
    /// </summary>
    private static byte[] AppendContentStreamOverride(byte[] pdf, string newText)
    {
        string content = $"BT /F1 12 Tf 100 700 Td ({newText}) Tj ET";

        var tail = new StringBuilder();
        tail.Append('\n');
        int objOffset = pdf.Length + tail.Length;

        tail.Append("4 0 obj\n")
            .Append("<< /Length ").Append(content.Length).Append(" >>\n")
            .Append("stream\n").Append(content).Append("\nendstream\nendobj\n");

        int xrefOffset = pdf.Length + tail.Length;
        tail.Append("xref\n")
            .Append("0 1\n0000000000 65535 f \n")
            .Append("4 1\n").AppendFormat("{0:D10} 00000 n \n", objOffset);

        // /Prev points at the proof revision's own cross-reference section, so a reader follows the
        // chain back through the notarized bytes and this override wins for object 4.
        long prev = LastStartXref(pdf);
        tail.Append("trailer\n<< /Size 7 /Root 1 0 R /Prev ").Append(prev).Append(" >>\n")
            .AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        byte[] appended = Encoding.ASCII.GetBytes(tail.ToString());
        byte[] result = new byte[pdf.Length + appended.Length];
        Array.Copy(pdf, result, pdf.Length);
        Array.Copy(appended, 0, result, pdf.Length, appended.Length);
        return result;
    }

    private static long LastStartXref(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture carries no startxref.");

        int i = at + "startxref".Length;
        while (i < text.Length && !char.IsDigit(text[i])) i++;
        int start = i;
        while (i < text.Length && char.IsDigit(text[i])) i++;

        Assert.True(i > start, "the fixture's startxref carried no offset.");
        return long.Parse(text.Substring(start, i - start));
    }
}
