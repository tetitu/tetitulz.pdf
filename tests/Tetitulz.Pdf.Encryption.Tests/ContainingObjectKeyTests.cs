// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A string is enciphered under the identifier of the indirect object that contains it, however deep inside that object
/// it sits, and decryption must use the same identifier — generation number included.
/// </summary>
/// <remarks>
/// <para>ISO 32000-2 §7.6.3.2, Algorithm 1 step (a): "Obtain the object number and generation number from the object
/// identifier of the string or stream to be encrypted (see 7.3.10, "Indirect objects"). If the string is a direct
/// object, use the identifier of the indirect object containing it."</para>
/// <para><c>PdfDecryptor</c> took the generation from the wrong object in two places. A dictionary nested inside an
/// indirect object was keyed by its <i>own</i> generation, which for a direct object is always 0; and a top-level array or
/// string was handed a hard-coded 0. Nearly every object has generation 0, which is why it survived: the generation that
/// exposes it is the one §7.5.4 gives a reused object number — "each time the entry is reused, it is given a new
/// generation number" — the ordinary shape of a file that has been updated incrementally.</para>
/// <para><b>How it fails depends on the cipher, so both are here.</b> Under RC4 the wrong key decrypts to noise and the
/// decrypt succeeds. Under AES-128 the noise fails PKCS#7 padding and the whole document refuses to decrypt. Both derive a
/// per-object key; AES-256 enciphers with the file key directly and cannot show it.</para>
/// <para><b>One string per document.</b> Under AES a single wrong key anywhere fails the entire decrypt, so a shared
/// fixture would turn every location red at once, and the locations that were always right would stop being
/// controls.</para>
/// </remarks>
public class ContainingObjectKeyTests
{
    private const string Plaintext = "enciphered-under-the-containing-object";

    /// <summary>The indirect object holding the string under test.</summary>
    private const int Subject = 5;

    /// <summary>An indirect object <c>PdfParser</c> resolves into the subject's stream dictionary.</summary>
    private const int Resolved = 6;

    private static readonly byte[] FileId =
    {
        0xC4, 0x1D, 0x72, 0x09, 0xE3, 0x58, 0xAF, 0x36,
        0x0B, 0x94, 0x6E, 0xD1, 0x27, 0x8C, 0x45, 0xFA
    };

    public static TheoryData<LegacyCipher, int> CiphersAndGenerations => new()
    {
        { LegacyCipher.Rc4With40BitKey, 0 },
        { LegacyCipher.Rc4With40BitKey, 1 },
        { LegacyCipher.Aes128, 0 },
        { LegacyCipher.Aes128, 1 },
    };

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInTheIndirectDictionaryItself_DecryptsUnderThatObjectsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation, $"<< /T {pdf.EncryptedString(Subject, generation, Plaintext)} >>"),
            doc => StringBytes(SubjectDictionary(doc)[PdfName.T]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInADictionaryNestedInsideIt_DecryptsUnderTheContainingObjectsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation,
                $"<< /Type /Annot /Subtype /Widget /MK << /CA {pdf.EncryptedString(Subject, generation, Plaintext)} >> >>"),
            doc => StringBytes(Dictionary(SubjectDictionary(doc)[PdfName.MK])[PdfName.CA]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInAnArrayInsideItsDictionary_DecryptsUnderTheContainingObjectsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation, $"<< /Opt [ {pdf.EncryptedString(Subject, generation, Plaintext)} ] >>"),
            doc => StringBytes(Assert.IsType<PdfArray>(SubjectDictionary(doc)[PdfName.Opt])[0]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInADictionaryInsideAnArray_DecryptsUnderTheContainingObjectsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation,
                $"<< /Opt [ << /T {pdf.EncryptedString(Subject, generation, Plaintext)} >> ] >>"),
            doc => StringBytes(Dictionary(Assert.IsType<PdfArray>(SubjectDictionary(doc)[PdfName.Opt])[0])[PdfName.T]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInATopLevelArray_DecryptsUnderTheArraysIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation, $"[ {pdf.EncryptedString(Subject, generation, Plaintext)} ]"),
            doc => StringBytes(Assert.IsType<PdfArray>(doc.Parser.GetObject(Subject))[0]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void TopLevelStringObject_DecryptsUnderItsOwnIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Object(Subject, generation, pdf.EncryptedString(Subject, generation, Plaintext)),
            doc => StringBytes(doc.Parser.GetObject(Subject)));

    /// <summary>An embedded file stream's <c>/Params /ModDate</c>: a string one dictionary below a stream dictionary.</summary>
    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInADictionaryInsideAStreamDictionary_DecryptsUnderTheStreamsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Stream(Subject, generation, "embedded-file-data",
                $"/Type /EmbeddedFile /Params << /ModDate {pdf.EncryptedString(Subject, generation, Plaintext)} >>"),
            doc => StringBytes(Dictionary(SubjectDictionary(doc)[PdfName.Params])[PdfName.ModDate]));

    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StreamData_DecryptsUnderTheStreamsIdentifier(LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf => pdf.Stream(Subject, generation, Plaintext),
            doc => Assert.IsType<PdfStream>(doc.Parser.GetObject(Subject)).RawData);

    /// <summary>
    /// A value that is not direct at all: <c>/DecodeParms 6 G R</c>, which <c>PdfParser</c> resolves into the stream
    /// dictionary while reading it. Its string was enciphered as part of object 6 — at a generation deliberately different
    /// from the stream's — and keeps that identifier wherever the parser has put it.
    /// </summary>
    [Theory]
    [MemberData(nameof(CiphersAndGenerations))]
    public void StringInAnObjectTheParserResolvedIntoAStreamDictionary_DecryptsUnderThatObjectsIdentifier(
        LegacyCipher cipher, int generation)
        => AssertDecrypts(cipher,
            pdf =>
            {
                int resolvedGeneration = generation + 1;
                pdf.Stream(Subject, generation, "stream-data", $"/DecodeParms {Resolved} {resolvedGeneration} R");
                pdf.Object(Resolved, resolvedGeneration,
                    $"<< /T {pdf.EncryptedString(Resolved, resolvedGeneration, Plaintext)} >>");
            },
            doc => StringBytes(Dictionary(SubjectDictionary(doc)[PdfName.DecodeParms])[PdfName.T]));

    /// <summary>
    /// Writes a one-revision document around the object(s) <paramref name="write"/> adds, and asserts that the value
    /// <paramref name="locate"/> finds is ciphertext before decryption and <see cref="Plaintext"/> after.
    /// </summary>
    private static void AssertDecrypts(
        LegacyCipher cipher, Action<TestPdfs.LegacyRc4.RevisionWriter> write, Func<PdfDocument, byte[]> locate)
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId, cipher);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [] /Count 0 >>");
        pdf.EncryptDictionary(4);
        write(pdf);
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 4);
        byte[] encrypted = pdf.ToArray();

        // The fixture really enciphered the value: read without decrypting, it is not the plaintext.
        using (var raw = PdfDocument.Open(new MemoryStream(encrypted)))
            Assert.NotEqual(Plaintext, Encoding.ASCII.GetString(locate(raw)));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "");

        using var decrypted = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(Plaintext, Encoding.ASCII.GetString(locate(decrypted)));
    }

    private static PdfDictionary SubjectDictionary(PdfDocument doc) => Dictionary(doc.Parser.GetObject(Subject));

    private static PdfDictionary Dictionary(PdfObject value) => Assert.IsAssignableFrom<PdfDictionary>(value);

    private static byte[] StringBytes(PdfObject value) => Assert.IsType<PdfString>(value).RawBytes;
}
