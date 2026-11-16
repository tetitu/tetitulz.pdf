// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Signature wrapping: the signed blocks stay byte for byte, the second moved along the file, and the
/// gap between them — which no digest covers — made wide enough for the forger's own objects. The size
/// of the gap cannot tell: a padded <c>/Contents</c> written as a literal string takes half the room its
/// hex form is allowed. What the gap holds has to be read.
/// </summary>
public sealed class SignatureWrappingTests
{
    private const string Field = "Signature1";
    private const string Forged = "BT /F1 12 Tf 72 700 Td (PAY THE ATTACKER 1000000) Tj ET\n";

    private static (long A, long B, long C) ByteRangeOf(byte[] pdf)
    {
        Match m = Regex.Matches(Encoding.Latin1.GetString(pdf), @"/ByteRange\s*\[\s*0\s+(\d+)\s+(\d+)\s+(\d+)\s*\]").Last();
        return (long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
    }

    private static string LiteralOf(byte[] raw)
    {
        var sb = new StringBuilder(raw.Length + 64);
        foreach (byte b in raw)
        {
            if (b == (byte)'(' || b == (byte)')' || b == (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b == 13) sb.Append("\\r");
            else if (b == 10) sb.Append("\\n");
            else sb.Append((char)b);
        }
        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="signed"/> rewrapped: its two blocks unchanged, the second moved past a gap holding
    /// a new signature dictionary naming the moved range, a new content stream, and the table the old
    /// <c>startxref</c> now lands on.
    /// </summary>
    private static byte[] Wrap(byte[] signed, Func<byte[], int, string> contents)
    {
        var (a, b, c) = ByteRangeOf(signed);
        string text = Encoding.Latin1.GetString(signed);
        string hex = text.Substring((int)a + 1, (int)(b - a) - 2);
        byte[] cms = Convert.FromHexString(hex);

        int oldXref = int.Parse(Regex.Matches(text, @"startxref\s+(\d+)").Last().Groups[1].Value, CultureInfo.InvariantCulture);

        using var doc = PdfDocument.Open(new MemoryStream(signed));
        int signatureObject = int.Parse(Regex.Matches(text, @"(\d+) 0 obj\s*<<(?:(?!endobj).)*?/ByteRange", RegexOptions.Singleline).Last().Groups[1].Value, CultureInfo.InvariantCulture);
        List<CrossReferenceEntry> entries = doc.CrossReferenceTable.AllEntries.Where(e => e.Type == CrossReferenceEntry.EntryType.Normal).ToList();
        string sigHead = text.Substring((int)entries.First(e => e.ObjectNumber == signatureObject).Offset);
        string filterEntries = Regex.Match(sigHead, @"/Filter\s*/\S+\s*/SubFilter\s*/[^\s/>]+").Value;

        const int Room = 16384;
        int pad = cms.Length + Room + (oldXref - (int)a);
        byte[] padded = cms.Concat(new byte[pad]).ToArray();

        long gapEnd = 0;

        // The table must sit at the old startxref's offset, so the objects go after it and it names them.
        var head = new StringBuilder();
        int maxObject = entries.Max(e => e.ObjectNumber);
        head.Append(new string(' ', oldXref - (int)a));

        var after = new StringBuilder();
        long tableLength = "xref\n0 ".Length + (maxObject + 1).ToString(CultureInfo.InvariantCulture).Length + 1 + 20 * (maxObject + 1)
                           + ("trailer\n<< /Size " + (maxObject + 1) + " /Root 1 0 R >>\n").Length;
        long objectsAt = oldXref + tableLength;

        for (int round = 0; round < 3; round++)
        {
            after.Clear();
            long dictAt = objectsAt + after.Length;
            after.Append(signatureObject).Append(" 0 obj\n<< /Type /Sig ").Append(filterEntries)
                 .Append(" /ByteRange [0 ").Append(a).Append(' ').Append(gapEnd.ToString("D10", CultureInfo.InvariantCulture)).Append(' ').Append(c)
                 .Append("] /Contents ").Append(contents(padded, cms.Length)).Append(" >>\nendobj\n");
            long contentAt = objectsAt + after.Length;
            after.Append("4 0 obj\n<< /Length ").Append(Forged.Length).Append(" >>\nstream\n").Append(Forged).Append("endstream\nendobj\n");
            gapEnd = objectsAt + after.Length;

            if (round < 2) continue;

            long shift = gapEnd - b;
            var table = new StringBuilder("xref\n0 ").Append(maxObject + 1).Append('\n').Append("0000000000 65535 f \n");
            for (int n = 1; n <= maxObject; n++)
            {
                CrossReferenceEntry e = entries.FirstOrDefault(x => x.ObjectNumber == n);
                long at = n == signatureObject ? dictAt
                        : n == 4 ? contentAt
                        : e == null ? 0
                        : e.Offset < a ? e.Offset : e.Offset + shift;
                table.Append(at.ToString("D10", CultureInfo.InvariantCulture)).Append(e == null && n != 4 ? " 00000 f \n" : " 00000 n \n");
            }
            table.Append("trailer\n<< /Size ").Append(maxObject + 1).Append(" /Root 1 0 R >>\n");
            Assert.Equal(tableLength, table.Length);

            byte[] s1 = signed.Take((int)a).ToArray();
            byte[] s2 = signed.Skip((int)b).ToArray();
            return s1.Concat(Encoding.Latin1.GetBytes(head.ToString() + table + after)).Concat(s2).ToArray();
        }
        throw new InvalidOperationException();
    }

    private static SignatureVerificationResult Verify(byte[] pdf, Org.BouncyCastle.X509.X509Certificate anchor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions { TrustAnchors = new[] { anchor } });
        return results.Single(r => r.FieldName == Field);
    }

    /// <summary>
    /// <b>The attack, with a real signature.</b> The bytes the CMS names are all there, unchanged, so it
    /// verifies; the range reaches the end of the file; and the page the reader draws is the forger's.
    /// </summary>
    [Fact]
    public void AdversarialWrappedSignature_ALiteralContentsInAGapOfForgedObjects_IsRefused()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
        byte[] forged = Wrap(signed, (padded, _) => "(" + LiteralOf(padded) + ")");

        // The wrap did what it set out to: the digest still holds, and the reader sees the new page.
        using (var doc = PdfDocument.Open(new MemoryStream(forged)))
            Assert.Contains("PAY THE ATTACKER", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);

        SignatureVerificationResult result = Verify(forged, ca.Cert);

        Assert.True(result.IsIntegrityValid, "the wrap must leave the digest intact, or this tests nothing");
        Assert.True(result.CoversEntireDocument);
        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors, e => e.Contains("gap", StringComparison.Ordinal));
    }

    /// <summary>The control: what the signer wrote passes, whether or not the delimiters are in the gap.</summary>
    [Fact]
    public void AnOrdinarySignature_ItsGapHoldingItsOwnContents_Passes()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw, Field);
        SignatureVerificationResult result = Verify(signed, ca.Cert);

        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.DoesNotContain(result.ValidationErrors, e => e.Contains("gap", StringComparison.Ordinal));
    }
}
