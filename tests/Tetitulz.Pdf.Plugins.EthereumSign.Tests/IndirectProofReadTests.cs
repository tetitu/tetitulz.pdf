// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.EthereumSign;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests;

/// <summary>
/// A blockchain proof whose <c>/HashedLength</c> is an indirect reference must not be read as
/// absent — because "absent" is what this verifier treats as <b>evidence of tampering</b>.
/// </summary>
/// <remarks>
/// <para><b>The failure direction is a false accusation.</b> Unresolved, <c>/HashedLength</c> reads
/// as 0 and verification is refused with <i>"The proof dictionary was modified after signing"</i>.
/// The document's only unusual property is legal object placement (§7.3.10), and the tool tells its
/// caller the file was tampered with. For a verifier that is the worst way to be wrong: a false
/// negative is a missed attack, but a false accusation is one the caller acts on.</para>
///
/// <para>The refusal is deliberate and correct <i>when the key really is absent</i> — the comment
/// above it explains that hashing the embedded <c>/DocumentHash</c> out of an edited file compares
/// the chain against itself. This fix does not weaken that; it only stops a legally-written
/// document being mistaken for an edited one.</para>
///
/// <para><b>No network is involved.</b> The fixture's <c>/HashedLength</c> exceeds the file length,
/// so the verifier refuses at the next guard — before the chain lookup. That makes the two spellings
/// distinguishable by <i>which</i> refusal they produce, which is exactly the claim under test.</para>
/// </remarks>
public class IndirectProofReadTests
{
    /// <summary>
    /// The over-long <c>/HashedLength</c> is seen as over-long whichever way it is spelled, rather
    /// than as missing. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /HashedLength 999999
    [InlineData(true)]   // /HashedLength 9 0 R
    public async Task TheHashedLengthIsRead_WhicheverWayItIsSpelled(bool indirect)
    {
        string error = await VerifyErrorAsync(ProofPdf(hashedLength: "999999", indirect: indirect));

        Assert.Contains("exceeds the document length", error, StringComparison.Ordinal);
        Assert.DoesNotContain("modified after signing", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor: a proof that genuinely has no <c>/HashedLength</c> must still be refused as
    /// modified. Without this the assertion above would be met by deleting the guard.
    /// </summary>
    [Fact]
    public async Task AProofWithoutHashedLengthIsStillRefusedAsModified()
    {
        string error = await VerifyErrorAsync(ProofPdf(hashedLength: null, indirect: false));

        Assert.Contains("modified after signing", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>/HashedLength</c> above the <c>int</c> range is read as the over-long value it is, not as
    /// its low thirty-two bits. 4294967296 (2^32) is a valid integer the whole file is far shorter than;
    /// a plain <c>(int)</c> cast made it 0, so the verifier refused with "modified after signing" — a
    /// false accusation — where a value of 2^32 + 500 would instead have wrapped to 500 and verified a
    /// document edited after its first 500 bytes. It is now refused as exceeding the document length.
    /// </summary>
    [Fact]
    public async Task AHashedLengthAboveTheIntRange_IsReadAsOverLong_NotAsItsLowBits()
    {
        string error = await VerifyErrorAsync(ProofPdf(hashedLength: "4294967296", indirect: false));

        Assert.Contains("exceeds the document length", error, StringComparison.Ordinal);
        Assert.DoesNotContain("modified after signing", error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static async Task<string> VerifyErrorAsync(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var result = await new PdfBlockchainVerifier().VerifyAsync(
            doc, new BlockchainParameters { RpcUrl = "http://127.0.0.1:1" });

        Assert.NotNull(result.Error);
        return result.Error;
    }

    /// <summary>
    /// A document carrying a <c>/BlockchainProof</c>. Object 9 holds the length when
    /// <paramref name="indirect"/> — the only difference between the two fixtures.
    /// </summary>
    private static byte[] ProofPdf(string hashedLength, bool indirect)
    {
        string entry = hashedLength == null
            ? ""
            : " /HashedLength " + (indirect ? "9 0 R" : hashedLength);

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /BlockchainProof 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /TxHash (0xabc) /DocumentHash (0xdef) /Network (sepolia)"
                + " /SignerAddress (0x1234) /BlockNumber 42" + entry + " >>",
            "null", "null", "null",
            hashedLength ?? "0");
    }

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
