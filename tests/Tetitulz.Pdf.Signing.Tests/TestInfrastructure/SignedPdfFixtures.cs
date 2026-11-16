// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Tests.TestInfrastructure;

/// <summary>
/// Builders for the multi-revision signed documents these tests need: a plain page, an approval
/// signature (through the real <see cref="PdfSigner"/>), and a hand-written
/// <c>/DocTimeStamp</c> revision whose token is minted locally rather than fetched from a TSA.
/// </summary>
/// <remarks>
/// The timestamp revision is written here rather than through
/// <c>PdfDocumentTimestamper.AddTimestamp</c> because that method reaches a real TSA over HTTP.
/// The bytes it produces are the same shape: an incremental update carrying a
/// <c>/SubFilter /ETSI.RFC3161</c> signature dictionary whose <c>/Contents</c> hole is excluded
/// from its own <c>/ByteRange</c>.
/// </remarks>
internal static class SignedPdfFixtures
{
    /// <summary>Bytes reserved for the hex-encoded timestamp token.</summary>
    private const int TokenReserveBytes = 8192;

    /// <summary>A one-page PDF: objects 1..5, no AcroForm.</summary>
    public static byte[] MinimalPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (signed content) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new List<int>();
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

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Objects 1..n, in order, with a correct cross-reference table and a trailer naming object 1
    /// as <c>/Root</c> — for a test whose point is the shape of the object graph. Bodies must be
    /// ASCII.
    /// </summary>
    public static byte[] Assemble(params string[] objects)
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
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Appends an approval signature through the production signer.
    /// <para>
    /// <paramref name="signingTime"/> is the <c>/M</c> entry and, this being an
    /// <c>adbe.pkcs7.detached</c> signature, the CMS <c>signing-time</c> attribute too. It matters
    /// for more than the dictionary: BouncyCastle's <c>SignerInformation.Verify</c> checks the signer
    /// certificate's validity against the attribute, so a fixture whose certificate has since expired
    /// must claim a time inside that certificate's window or the CMS will not verify at all — which
    /// is also exactly what an attacker does, the claimed time being unauthenticated.
    /// </para>
    /// </summary>
    public static byte[] Sign(byte[] pdf, byte[] pfx, string password,
        string fieldName = "Signature1", DateTime? signingTime = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            SignatureFieldName = fieldName,
            SigningTime = signingTime,
            Reason = "test",
        });
        return output.ToArray();
    }

    /// <summary>
    /// Appends a <c>/DocTimeStamp</c> revision asserting <paramref name="genTime"/>, signed by
    /// <paramref name="tsa"/>. The returned file's timestamp covers every byte written before it
    /// and nothing written after.
    /// </summary>
    /// <param name="finalEndOfLineUnsigned">
    /// End the range at <c>%%EOF</c>, leaving the end-of-line after it unsigned — the other shape
    /// ISO 32000-2 §12.8.1 allows ("possibly followed by an optional EOL marker").
    /// </param>
    public static byte[] AppendDocumentTimestamp(
        byte[] pdf,
        SigningTestCrypto.Issued tsa,
        DateTime genTime,
        Org.BouncyCastle.X509.X509Certificate[] chain,
        int sigDictObjNum,
        int sigFieldObjNum,
        string existingFieldRefs = "",
        string fieldName = "DocTS1",
        bool finalEndOfLineUnsigned = false)
    {
        var ms = new MemoryStream();
        ms.Write(pdf, 0, pdf.Length);

        long prevXref = FindStartXref(pdf);
        var body = new StringBuilder();

        long Mark() => ms.Length + body.Length;

        long sigDictOffset = Mark();
        body.Append($"{sigDictObjNum} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /ETSI.RFC3161");
        body.Append(" /ByteRange ");
        long byteRangePos = Mark();
        string brPlaceholder = "[0000000000000 0000000000000 0000000000000 0000000000000]";
        body.Append(brPlaceholder);
        body.Append(" /Contents <");
        long contentsStart = Mark();
        body.Append(new string('0', TokenReserveBytes * 2));
        long contentsEnd = Mark();
        body.Append(">");
        body.Append(" >>\nendobj\n");

        long sigFieldOffset = Mark();
        body.Append($"{sigFieldObjNum} 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T ({fieldName})"
                  + $" /V {sigDictObjNum} 0 R /Rect [0 0 0 0] /P 3 0 R >>\nendobj\n");

        long catalogOffset = Mark();
        body.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields ["
                  + existingFieldRefs + $"{sigFieldObjNum} 0 R] /SigFlags 3 >> >>\nendobj\n");

        long xrefOffset = Mark();
        body.Append("xref\n");
        body.Append("1 1\n").AppendFormat("{0:D10} 00000 n \n", catalogOffset);
        body.Append($"{sigDictObjNum} 2\n")
            .AppendFormat("{0:D10} 00000 n \n", sigDictOffset)
            .AppendFormat("{0:D10} 00000 n \n", sigFieldOffset);
        body.Append($"trailer\n<< /Size {sigFieldObjNum + 1} /Root 1 0 R /Prev {prevXref} >>\n");
        body.Append($"startxref\n{xrefOffset}\n%%EOF\n");

        byte[] bodyBytes = Encoding.ASCII.GetBytes(body.ToString());
        ms.Write(bodyBytes, 0, bodyBytes.Length);

        byte[] file = ms.ToArray();

        // ByteRange over everything except the /Contents hex hole (delimiters included), through
        // the SAME arithmetic the library uses. This fixture's remarks claim it produces "the same
        // shape" as PdfDocumentTimestamper; until that call existed the claim was prose, and it was
        // false — the production method left the '<' inside the signed range and this fixture did
        // not, so the shipped arithmetic was reached by no test at all.
        var range = SignatureByteRange.Compute(
            contentsStart, contentsEnd, finalEndOfLineUnsigned ? file.Length - 1 : file.Length);
        long br1 = range.BeforeLength;
        long br2 = range.AfterStart;
        long br3 = range.AfterLength;
        Encoding.ASCII.GetBytes(range.Format(brPlaceholder.Length)).CopyTo(file, byteRangePos);

        // Hash exactly what the verifier will re-read, then mint the token over it.
        byte[] signed = new byte[br1 + br3];
        Array.Copy(file, 0, signed, 0, (int)br1);
        Array.Copy(file, (int)br2, signed, (int)br1, (int)br3);

        byte[] token = SigningTestCrypto.TimestampToken(signed, tsa, genTime, chain);
        if (token.Length > TokenReserveBytes)
            throw new InvalidOperationException("Timestamp token does not fit the reserved space.");

        string hex = BitConverter.ToString(token).Replace("-", "").ToLowerInvariant()
                                 .PadRight(TokenReserveBytes * 2, '0');
        Encoding.ASCII.GetBytes(hex).CopyTo(file, contentsStart);

        return file;
    }

    /// <summary>Digest helper kept beside the fixture so a test can restate what was hashed.</summary>
    public static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    /// <summary>
    /// Offset of the most recent cross-reference section, so a caller hand-writing a further
    /// incremental revision can chain its <c>/Prev</c> to it.
    /// </summary>
    internal static long FindStartXref(byte[] pdf)
    {
        string tail = Encoding.ASCII.GetString(pdf, Math.Max(0, pdf.Length - 2048),
                                               Math.Min(2048, pdf.Length));
        int idx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0) throw new InvalidOperationException("Fixture has no startxref.");
        string rest = tail.Substring(idx + "startxref".Length).TrimStart('\r', '\n', ' ');
        int end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        return long.Parse(rest.Substring(0, end));
    }
}
