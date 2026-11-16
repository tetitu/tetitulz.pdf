// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The encrypted output's <c>/Size</c> must satisfy §7.5.5 — one greater than the highest object
/// number used — on a <b>sparse</b> cross-reference table as well as a dense one.
/// </summary>
/// <remarks>
/// <para>
/// <c>/Size</c> was written as <c>encryptedObjects.Count + 2</c>, which is only correct when the
/// source's live object numbers are exactly 1..N with no gaps. The two quantities come from
/// different places and disagree the moment there is a gap: <c>encryptDictObjNum</c> is
/// <c>CrossReferenceTable.GetMaxObjectNumber() + 1</c>, computed over a table that <b>includes</b>
/// free entries, while <c>Count</c> comes from <c>PdfParser.EnumerateAllObjects()</c>, which skips
/// free entries and objects that fail to parse.
/// </para>
/// <para>
/// Because the <c>/Encrypt</c> dictionary is always allocated at the very top, it is always the
/// object that falls outside an understated <c>/Size</c> — so a conforming reader that bounds
/// object numbers by <c>/Size</c> cannot resolve <c>/Encrypt</c>, and the document does not open at
/// all. This library's own decryptor never validates <c>/Size</c>, which is exactly why the existing
/// round-trip tests stayed green; asserting that the output decrypts therefore cannot see this, and
/// these tests read the written <c>/Size</c> instead.
/// </para>
/// </remarks>
public class EncryptedTrailerSizeTests
{
    /// <summary>
    /// Object 9 is declared free above the five live objects, so the encryptor allocates /Encrypt at
    /// 10 while only five objects are enumerated. <c>Count + 2</c> gave 7.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void SparseXref_EncryptedTrailerSizeExceedsTheEncryptDictionaryObjectNumber(
        EncryptionAlgorithm algorithm)
    {
        (int size, int encryptObjNum) = EncryptAndReadTrailer(SparsePdf(), algorithm);

        Assert.True(size > encryptObjNum,
            $"{algorithm}: /Size {size} must exceed the /Encrypt object number {encryptObjNum} "
            + "(ISO 32000-1 §7.5.5); a reader bounding object numbers by /Size cannot otherwise "
            + "resolve /Encrypt, and the document does not open at all.");
    }

    /// <summary>
    /// The dense case, where the old count-based arithmetic happened to be right. It pins that the
    /// fix did not change the answer where the answer was already correct — <c>/Size</c> must be
    /// exactly one past /Encrypt, not merely larger.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void DenseXref_EncryptedTrailerSizeIsExactlyOnePastTheEncryptDictionaryObjectNumber(
        EncryptionAlgorithm algorithm)
    {
        (int size, int encryptObjNum) = EncryptAndReadTrailer(DensePdf(), algorithm);

        Assert.Equal(encryptObjNum + 1, size);
    }

    private static (int Size, int EncryptObjNum) EncryptAndReadTrailer(
        byte[] plainPdf, EncryptionAlgorithm algorithm)
    {
        using var plain = new MemoryStream(plainPdf);
        using var encrypted = new MemoryStream();

        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = "secret",
            OwnerPassword = "owner",
            Algorithm     = algorithm,
        });

        encrypted.Position = 0;
        using var doc = PdfDocument.Open(encrypted);

        int size = doc.Trailer.GetInteger(PdfName.Size) ?? -1;
        var encryptRef = doc.Trailer[PdfName.Encrypt] as PdfReference;
        Assert.NotNull(encryptRef);

        return (size, encryptRef!.ReferenceObjectNumber);
    }

    /// <summary>Objects 1..5 live; 6..9 declared free, so the table's highest number is 9.</summary>
    private static byte[] SparsePdf() => BuildPdf(trailingFreeEntries: 4);

    /// <summary>Objects 1..5 live and nothing above them.</summary>
    private static byte[] DensePdf() => BuildPdf(trailingFreeEntries: 0);

    private static byte[] BuildPdf(int trailingFreeEntries)
    {
        const string content = "BT /F1 12 Tf 72 700 Td (payload) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int entryCount = offsets.Count + 1 + trailingFreeEntries;

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", entryCount);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        for (int i = 0; i < trailingFreeEntries; i++)
            sb.Append("0000000000 00000 f \n");

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            entryCount, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
