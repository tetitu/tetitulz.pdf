// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="SignatureDictionaryShape"/>: which dictionaries are signature dictionaries, and the
/// one consequence ISO 32000-2 §7.6.2 draws — their <c>/Contents</c> is not encrypted — as
/// <see cref="PdfWriter"/> applies it.
/// </summary>
/// <remarks>
/// The writer half is asserted on the <b>raw bytes</b>: an encrypted string and a clear one both
/// parse back as a string, so only the bytes can say which the writer produced.
/// </remarks>
public class SignatureDictionaryShapeTests
{
    [Fact]
    public void ATypeSigDictionaryIsASignatureValue()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Type, PdfName.Sig);
        Assert.True(SignatureDictionaryShape.IsSignatureValue(d, SignatureDictionaryShape.Direct));
    }

    [Fact]
    public void ADocumentTimestampDictionaryIsASignatureValue()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.Type, new PdfName("DocTimeStamp"));
        Assert.True(SignatureDictionaryShape.IsSignatureValue(d, SignatureDictionaryShape.Direct));
    }

    /// <summary>Table 255 makes <c>/Type</c> optional, so the shape alone has to be enough.</summary>
    [Fact]
    public void AByteRangeBesideAContentsIsASignatureValueWithoutAType()
    {
        var d = new PdfDictionary();
        d.Set(PdfName.ByteRange, new PdfArray());
        d.Set(PdfName.Contents, new PdfString(new byte[] { 0x30 }, isHex: true));
        Assert.True(SignatureDictionaryShape.IsSignatureValue(d, SignatureDictionaryShape.Direct));
    }

    [Fact]
    public void APageAndATextAnnotationAreNotSignatureValuesThoughBothCarryContents()
    {
        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Contents, new PdfReference(4, 0));

        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Annot);
        annot.Set(PdfName.Contents, new PdfString("a note"));

        Assert.False(SignatureDictionaryShape.IsSignatureValue(page, SignatureDictionaryShape.Direct));
        Assert.False(SignatureDictionaryShape.IsSignatureValue(annot, SignatureDictionaryShape.Direct));
        Assert.False(SignatureDictionaryShape.IsSignatureValue(null, SignatureDictionaryShape.Direct));
    }

    [Fact]
    public void OnlyTheContentsEntryOfASignatureDictionaryIsUnencrypted()
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Sig);
        sig.Set(PdfName.Contents, new PdfString(new byte[] { 0x30 }, isHex: true));
        sig.Set(PdfName.Reason, new PdfString("why"));

        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Annot);
        annot.Set(PdfName.Contents, new PdfString("a note"));

        Assert.True(SignatureDictionaryShape.IsUnencryptedEntry(sig, PdfName.Contents));
        Assert.False(SignatureDictionaryShape.IsUnencryptedEntry(sig, PdfName.Reason));
        Assert.False(SignatureDictionaryShape.IsUnencryptedEntry(annot, PdfName.Contents));
    }

    // ------------------------------------------------------------------ the writer

    /// <summary>
    /// The §7.6.2 exemption as the writer applies it: the signature's <c>/Contents</c> hex digits
    /// reach the file as written, while the <c>/Reason</c> beside them are ciphertext.
    /// </summary>
    [Fact]
    public void PdfWriterLeavesASignatureContentsInTheClearAndEncryptsItsOtherStrings()
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Sig);
        sig.Set(PdfName.Contents, new PdfString(new byte[] { 0x30, 0x81, 0x02 }, isHex: true));
        sig.Set(PdfName.Reason, new PdfString("secret"));

        string text = Write(sig, new XorEncryptor());

        Assert.Contains("/Contents <308102>", text);
        Assert.DoesNotContain("(secret)", text);
        Assert.Contains("/Reason (" + XorEncryptor.Apply("secret") + ")", text);
    }

    /// <summary>The exemption is the signature dictionary's alone: an annotation's <c>/Contents</c> is a string like any other.</summary>
    [Fact]
    public void PdfWriterEncryptsTheContentsOfADictionaryThatIsNotASignature()
    {
        var annot = new PdfDictionary();
        annot.Set(PdfName.Type, PdfName.Annot);
        annot.Set(PdfName.Contents, new PdfString("a note"));

        string text = Write(annot, new XorEncryptor());

        Assert.DoesNotContain("(a note)", text);
        Assert.Contains("/Contents (" + XorEncryptor.Apply("a note") + ")", text);
    }

    /// <summary>The control: with no encryptor every string is written as it is.</summary>
    [Fact]
    public void WithoutAnEncryptorEveryStringIsWrittenAsItIs()
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Sig);
        sig.Set(PdfName.Contents, new PdfString(new byte[] { 0x30 }, isHex: true));
        sig.Set(PdfName.Reason, new PdfString("secret"));

        string text = Write(sig, null);

        Assert.Contains("/Contents <30>", text);
        Assert.Contains("/Reason (secret)", text);
    }

    private static string Write(PdfDictionary dictionary, IPdfObjectEncryptor encryptor)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms) { ObjectEncryptor = encryptor };
        writer.WriteIndirectObject(9, 0, dictionary);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>
    /// An encryptor whose output is visibly different from its input and stays printable, so the
    /// assertions above can name the bytes they expect: every byte XOR 0x01.
    /// </summary>
    private sealed class XorEncryptor : IPdfObjectEncryptor
    {
        public bool EncryptsStrings => true;
        public bool EncryptsStreams => true;
        public bool EncryptsMetadataStreams => true;

        public byte[] EncryptString(byte[] plaintext, int objectNumber, int generationNumber) => Apply(plaintext);
        public byte[] EncryptStream(byte[] plaintext, int objectNumber, int generationNumber) => Apply(plaintext);

        public static string Apply(string text) => Encoding.Latin1.GetString(Apply(Encoding.Latin1.GetBytes(text)));

        private static byte[] Apply(byte[] bytes)
        {
            var result = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) result[i] = (byte)(bytes[i] ^ 0x01);
            return result;
        }
    }
}
