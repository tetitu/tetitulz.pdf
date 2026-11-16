// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A <c>/V 4</c> document whose crypt-filter chain is written with indirect references must reach
/// the same verdict as the inline spelling — it was refused with a reason untrue of the file.
/// </summary>
/// <remarks>
/// <para><c>CryptFilterMethod</c> walks <c>/CF</c> → <c>/StdCF</c> → <c>/CFM</c> in three
/// consecutive dictionary reads, none of which followed a reference. Any one being indirect
/// returned null, which falls through every branch of <c>TryDetectAlgorithm</c>'s <c>/V 4</c> case
/// to <i>"the document uses /V 4 with an unsupported crypt filter method (/CFM absent), which this
/// library cannot reproduce"</i> — <b>before decryption is even attempted</b>. Sanitize, optimize
/// and redact then refuse an ordinary AES-128 document, and the stated reason describes a file the
/// caller does not have.</para>
///
/// <para><b>The assertion is that the two spellings agree, not that the round trip succeeds.</b>
/// That is the actual claim — the spelling of a dictionary must not change the verdict — and it is
/// testable without a genuinely decryptable fixture, which would need real <c>/O</c> and <c>/U</c>
/// values for a password. A hand-written <c>/Encrypt</c> is all this path reads, exactly as
/// <c>CryptFilterDiagnosticTests</c> established.</para>
///
/// <para>The floor is the second test: the reason must not mention the crypt filter at all.
/// Without it, "the two agree" would be satisfied by both failing the same wrong way.</para>
/// </remarks>
public class IndirectCryptFilterTests
{
    /// <summary>The chain written inline — the control, which always worked.</summary>
    private const string InlineChain =
        "/CF << /StdCF << /Type /CryptFilter /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> "
        + "/StmF /StdCF /StrF /StdCF";

    /// <summary>The same chain, split across objects 5, 6 and 7.</summary>
    private const string IndirectChain = "/CF 5 0 R /StmF /StdCF /StrF /StdCF";

    [Fact]
    public void TheVerdictIsTheSame_WhicheverWayTheCryptFilterIsSpelled()
    {
        string direct = FailureReasonFor(InlineChain);
        string viaRefs = FailureReasonFor(IndirectChain);

        Assert.Equal(direct, viaRefs);
    }

    /// <summary>
    /// The floor: neither spelling may be refused <i>for the crypt filter</i>. Without this, the
    /// equality above would be satisfied by both failing the same wrong way.
    /// </summary>
    [Theory]
    [InlineData(InlineChain)]
    [InlineData(IndirectChain)]
    public void TheCryptFilterIsNeverTheStatedReason(string chain)
    {
        string reason = FailureReasonFor(chain) ?? "";

        Assert.DoesNotContain("crypt filter", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/CFM", reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the round trip and returns why it declined. The fixture's <c>/O</c> and <c>/U</c> are
    /// placeholders, so it always declines — on the password. Which is the point: the crypt-filter
    /// walk happens earlier, and must not be what stops it.
    /// </summary>
    private static string FailureReasonFor(string cryptFilterKeys)
    {
        using var input = new MemoryStream(EncryptedShell(cryptFilterKeys));
        using var output = new MemoryStream();

        var result = PdfEncryptedRoundTrip.Run(
            input, output,
            new EncryptedRoundTripOptions { OpenPassword = "pw" },
            (inner, outer) => inner.CopyTo(outer));

        Assert.False(result.Succeeded,
            "the placeholder /O and /U were accepted, so this fixture no longer isolates the walk");
        return result.FailureReason;
    }

    /// <summary>
    /// A document carrying a hand-written <c>/Encrypt</c> dictionary, which is all the crypt-filter
    /// resolution path reads. Objects 5–7 hold the chain for the indirect spelling and are simply
    /// unreferenced by the inline one.
    /// </summary>
    private static byte[] EncryptedShell(string cryptFilterKeys)
    {
        string hex = new string('a', 64);

        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        Obj($"4 0 obj\n<< /Filter /Standard /V 4 /R 4 /Length 128 /P -1 {cryptFilterKeys} "
          + $"/O <{hex}> /U <{hex}> >>\nendobj\n");
        Obj("5 0 obj\n<< /StdCF 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /CryptFilter /CFM 7 0 R /AuthEvent /DocOpen /Length 16 >>\nendobj\n");
        Obj("7 0 obj\n/AESV2\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R /Encrypt 4 0 R "
                      + "/ID [<0102030405060708090a0b0c0d0e0f10> <0102030405060708090a0b0c0d0e0f10>] "
                      + ">>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
