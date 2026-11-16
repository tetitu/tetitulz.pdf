// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Two properties of what <see cref="PdfEncryptor"/> actually <b>writes</b>, both of which its
/// round-trip tests structurally cannot see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Generation numbers.</b> §7.6.2 Algorithm 1 mixes two low-order bytes of an object's
/// generation number into its per-object key. The encryptor took that generation from the parsed
/// object (<c>stream.GenerationNumber</c>, filled in from the <c>N G obj</c> header) but emitted
/// every object as <c>N 0 obj</c>, so the ciphertext was produced under one generation and every
/// reader — including this library's own <see cref="PdfDecryptor"/>, which reads the generation
/// back off the file — derives the key under another.
/// </para>
/// <para>
/// <b>/Size.</b> §7.5.5 requires <c>/Size</c> to be one greater than the highest object number
/// used. It was written as <c>objectCount + 2</c>, which is the same number only when the source's
/// live object numbers are exactly 1..N with no gaps. The <c>/Encrypt</c> dictionary is allocated
/// above <c>GetMaxObjectNumber()</c> — which counts free entries — so on a sparse xref it is
/// always the object that falls outside the declared <c>/Size</c>.
/// </para>
/// <para>
/// <b>Why the existing suite is green either way.</b> Every encryption test round-trips this
/// implementation against itself with gap-free, generation-0 fixtures: the wrong key agrees with
/// itself, and the decryptor never validates <c>/Size</c>. The fixtures below are the two shapes
/// that break that symmetry.
/// </para>
/// </remarks>
public class EncryptorOutputStructureTests
{
    /// <summary>The literal drawn by the content stream that carries a non-zero generation.</summary>
    private const string Marker = "GEN-MARKER-7391";

    /// <summary>
    /// An object written as <c>4 3 obj</c> must still decrypt. RC4 loses the text silently and
    /// AES-128 aborts the decrypt outright, so the assertion is on the recovered plaintext rather
    /// than on the call completing.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnObjectWithANonZeroGeneration_RoundTripsThroughEncryptAndDecrypt(
        EncryptionAlgorithm algorithm)
    {
        byte[] source = BuildPdf(contentGeneration: 3, withHighFreeEntry: false);

        string decrypted = Latin1(RoundTrip(source, algorithm, "secret"));

        Assert.Contains(Marker, decrypted);
    }

    /// <summary>
    /// The control: the same fixture at generation 0 must behave identically, so a failure above
    /// can only be about the generation and not about the fixture.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    public void TheSameFixtureAtGenerationZero_RoundTrips(EncryptionAlgorithm algorithm)
    {
        byte[] source = BuildPdf(contentGeneration: 0, withHighFreeEntry: false);

        string decrypted = Latin1(RoundTrip(source, algorithm, "secret"));

        Assert.Contains(Marker, decrypted);
    }

    /// <summary>
    /// With a free entry above the live objects, the encrypted output's <c>/Size</c> must still
    /// exceed every object number in the file — above all the <c>/Encrypt</c> dictionary's, which
    /// a reader that bounds object numbers by <c>/Size</c> otherwise cannot resolve, leaving the
    /// document unopenable.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void SizeExceedsEveryObjectNumber_OnASparseCrossReferenceTable(
        EncryptionAlgorithm algorithm)
    {
        byte[] source = BuildPdf(contentGeneration: 0, withHighFreeEntry: true);
        byte[] encrypted = Encrypt(source, algorithm, "secret");

        using var ms = new MemoryStream(encrypted);
        var parser = new PdfParser(ms);
        parser.ParseHeader();
        parser.ParseCrossReferences();

        int size = parser.Trailer.GetInteger(PdfName.Size) ?? 0;
        var encryptRef = parser.Trailer[PdfName.Encrypt] as PdfReference;
        int highestWritten = parser.CrossReferenceTable.GetMaxObjectNumber();

        Assert.NotNull(encryptRef);
        Assert.True(size > encryptRef!.ReferenceObjectNumber,
            $"/Size {size} does not cover the /Encrypt dictionary at object " +
            $"{encryptRef.ReferenceObjectNumber}; §7.5.5 requires one more than the highest " +
            "object number used.");
        Assert.True(size > highestWritten,
            $"/Size {size} does not cover the highest written object {highestWritten}.");
    }

    // ---------------------------------------------------------------- helpers

    private static byte[] RoundTrip(byte[] source, EncryptionAlgorithm algorithm, string password)
    {
        byte[] encrypted = Encrypt(source, algorithm, password);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] source, EncryptionAlgorithm algorithm, string password)
    {
        using var plain = new MemoryStream(source);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = password,
            OwnerPassword = password,
            Algorithm = algorithm,
        });
        return encrypted.ToArray();
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    /// <summary>
    /// A five-object PDF whose content stream object carries <paramref name="contentGeneration"/>
    /// in both its object header and its xref entry, optionally followed by a free entry for
    /// object 9 (a number above every live object — the shape any file that deleted an object
    /// has).
    /// </summary>
    private static byte[] BuildPdf(int contentGeneration, bool withHighFreeEntry)
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new Dictionary<int, int>();
        void Obj(int number, string body) { offsets[number] = sb.Length; sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj(1, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3, $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
             + $"/Contents 4 {contentGeneration} R "
             + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(4, $"4 {contentGeneration} obj\n<< /Length {content.Length} >>\n"
             + $"stream\n{content}endstream\nendobj\n");
        Obj(5, "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        for (int n = 1; n <= 5; n++)
            sb.AppendFormat("{0:D10} {1:D5} n \n", offsets[n], n == 4 ? contentGeneration : 0);
        if (withHighFreeEntry)
            sb.Append("9 1\n0000000000 65535 f \n");

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            withHighFreeEntry ? 10 : 6, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
