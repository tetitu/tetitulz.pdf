// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Encrypting a document that contains an object with a non-zero generation number must still
/// produce a document this library can decrypt.
/// </summary>
/// <remarks>
/// <para>
/// ISO 32000-1 §7.6.2 Algorithm 1 mixes two bytes of the object's generation number into the
/// per-object key. <c>PdfEncryptor.EncryptStream</c> / <c>EncryptDictionary</c> took that generation
/// off the <em>parsed source</em> object (<c>stream.GenerationNumber</c>, which <c>PdfParser</c>
/// fills from the <c>N G obj</c> header), but the writer emits every object as <c>N 0 obj</c>:
/// <c>WriteIndirectObject(objNum, 0, …)</c> plus the non-generation <c>WriteXrefTable</c> overload.
/// So an object arriving as <c>4 3 obj</c> — the ordinary shape for a file incrementally updated
/// after an object number was freed and reused — was enciphered under generation 3 and written as
/// generation 0, and every reader (including this library's own <c>PdfDecryptor</c>, which correctly
/// reads the generation back off the file it is given) derived a different key.
/// </para>
/// <para>
/// <b>Why the assertion is on the recovered bytes.</b> With RC4 the mismatch produces silent
/// garbage: the decrypt completes, the output is a valid non-empty PDF, and only the payload is
/// wrong. Asserting that decryption "succeeded" or that the output is non-empty passes with the
/// defect present. The generation-0 case is asserted alongside as the control — it round-tripped
/// before the fix and must still, so a fix that simply broke per-object keys cannot pass this pair.
/// </para>
/// <para>
/// AES-256 is included deliberately even though it is structurally immune (V=5 encrypts with the
/// file key directly and derives no per-object key), because a theory over the whole enum is what
/// stops the next algorithm being added on the wrong side of this.
/// </para>
/// </remarks>
public class NonZeroGenerationRoundTripTests
{
    private const string Marker = "confidential-payload";

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void ContentStreamWithGenerationThree_SurvivesTheEncryptDecryptRoundTrip(
        EncryptionAlgorithm algorithm)
    {
        byte[] recovered = RoundTrip(algorithm, contentStreamGeneration: 3);

        Assert.Contains(Marker, Encoding.ASCII.GetString(recovered), StringComparison.Ordinal);
    }

    /// <summary>The generation-0 control: it always worked, and must keep working.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void ContentStreamWithGenerationZero_SurvivesTheEncryptDecryptRoundTrip(
        EncryptionAlgorithm algorithm)
    {
        byte[] recovered = RoundTrip(algorithm, contentStreamGeneration: 0);

        Assert.Contains(Marker, Encoding.ASCII.GetString(recovered), StringComparison.Ordinal);
    }

    /// <summary>
    /// The encrypted intermediate must not already contain the plaintext — otherwise the round-trip
    /// assertions above would pass on a fixture that was never encrypted at all, which is the
    /// "fixture never reached the feature" failure mode.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void TheEncryptedIntermediateDoesNotContainThePlaintextMarker(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(PdfWithContentStreamGeneration(3), algorithm);

        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(encrypted), StringComparison.Ordinal);
    }

    private static byte[] RoundTrip(EncryptionAlgorithm algorithm, int contentStreamGeneration)
    {
        byte[] encrypted = Encrypt(PdfWithContentStreamGeneration(contentStreamGeneration), algorithm);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "secret");
        return output.ToArray();
    }

    private static byte[] Encrypt(byte[] plainPdf, EncryptionAlgorithm algorithm)
    {
        using var plain = new MemoryStream(plainPdf);
        using var encrypted = new MemoryStream();

        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = "secret",
            OwnerPassword = "owner",
            Algorithm     = algorithm,
        });

        return encrypted.ToArray();
    }

    /// <summary>
    /// A minimal document whose content stream is object 4 at the requested generation — written
    /// both in the <c>4 G obj</c> header and in the xref entry's generation field, which is what a
    /// file that reused a freed object number really looks like.
    /// </summary>
    private static byte[] PdfWithContentStreamGeneration(int generation)
    {
        string content = "BT /F1 12 Tf 72 700 Td (" + Marker + ") Tj ET\n";

        var sb = new StringBuilder();
        var entries = new System.Collections.Generic.List<(int offset, int gen)>();
        void Obj(int gen, string body) { entries.Add((sb.Length, gen)); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj(0, "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(0, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(0, $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 {generation} R "
               + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(generation, $"4 {generation} obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj(0, "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", entries.Count + 1);
        foreach (var (off, gen) in entries) sb.AppendFormat("{0:D10} {1:D5} n \n", off, gen);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            entries.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
