// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfCompressedWriteOptions.ObjectEncryptor"/>: the compressed layout written encrypted.
/// ISO 32000-1 §7.5.7 encrypts an object stream whole, under its own number, and its members not at
/// all — a reader decrypts the container, then reads them; §7.6.2 leaves the <c>/Encrypt</c>
/// dictionary's strings alone and §7.5.8.2 the cross-reference stream. The encryptor here is a byte
/// XOR, so each claim is read back from the bytes.
/// </summary>
public class CompressedWriterEncryptorTests
{
    private const byte Key = 0x5A;

    private sealed class XorEncryptor : IPdfObjectEncryptor
    {
        public bool EncryptsStrings => true;
        public bool EncryptsStreams => true;
        public bool EncryptsMetadataStreams => true;
        public byte[] EncryptString(byte[] plaintext, int objectNumber, int generationNumber) => Xor(plaintext);
        public byte[] EncryptStream(byte[] plaintext, int objectNumber, int generationNumber) => Xor(plaintext);
    }

    private static byte[] Xor(byte[] data)
    {
        var o = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) o[i] = (byte)(data[i] ^ Key);
        return o;
    }

    /// <summary>
    /// A dictionary with a string is packed; a top-level stream and the <c>/Encrypt</c> dictionary stay
    /// top-level. The object stream's data is the encryptor's output and inflates, once decrypted, to the
    /// member with its string in the clear; the stream object's data is encrypted; the <c>/Encrypt</c>
    /// dictionary's <c>/O</c> is not; the cross-reference stream parses as written. The control: the same
    /// trailer with no encryptor is written in the clear and reported.
    /// </summary>
    [Fact]
    public void WithAnEncryptor_ObjectStreamsAreEncryptedWhole_MembersClearInside_EncryptDictionaryAndXrefStreamClear()
    {
        byte[] pdf = Write(new XorEncryptor());
        Assert.Null(PdfCompressedFileWriter.LastWriteWarning);

        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();

        // Objects 1..5 are the caller's; the writer numbers its object stream 6 and the xref stream 7.
        var objStm = Assert.IsType<PdfStream>(parser.GetObject(6));
        Assert.Equal(PdfName.ObjStm, objStm.GetName(PdfName.Type));
        byte[] ciphertext = objStm.RawData;
        Assert.DoesNotContain("secret", Latin1(ciphertext), StringComparison.Ordinal);
        objStm.RawData = Xor(ciphertext);
        string members = Latin1(objStm.GetDecodedData());
        Assert.Contains("/Marker (secret)", members, StringComparison.Ordinal);
        Assert.Contains("/Type /Pages", members, StringComparison.Ordinal);

        var topLevel = Assert.IsType<PdfStream>(parser.GetObject(4));
        Assert.Equal("STREAM-DATA", Latin1(Xor(topLevel.RawData)));
        Assert.NotEqual("STREAM-DATA", Latin1(topLevel.RawData));

        var encrypt = Assert.IsType<PdfDictionary>(parser.GetObject(5));
        Assert.Equal("owner-bytes", Latin1(Assert.IsType<PdfString>(encrypt[PdfName.O]).RawBytes));

        var xref = Assert.IsType<PdfStream>(parser.GetObject(7));
        Assert.Equal(PdfName.XRef, xref.GetName(PdfName.Type));
        Assert.Equal(5, Assert.IsType<PdfReference>(xref[PdfName.Encrypt]).ReferenceObjectNumber);

        byte[] clear = Write(null);
        Assert.Contains("has no encryptor", PdfCompressedFileWriter.LastWriteWarning, StringComparison.Ordinal);
        var clearParser = new PdfParser(new MemoryStream(clear));
        clearParser.ParseHeader();
        clearParser.ParseCrossReferences();
        Assert.Equal("STREAM-DATA", Latin1(Assert.IsType<PdfStream>(clearParser.GetObject(4)).RawData));
    }

    /// <summary>A trailer's PDF MAC placeholder travels into the cross-reference stream's dictionary, where the seal looks for it.</summary>
    [Fact]
    public void ATrailersAuthCode_IsCarriedIntoTheCrossReferenceStreamDictionary()
    {
        var authCode = new PdfDictionary();
        authCode.Set(PdfName.MACLocation, PdfName.Standalone);
        byte[] pdf = Write(new XorEncryptor(), authCode);

        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var xref = Assert.IsType<PdfStream>(parser.GetObject(7));
        var carried = Assert.IsType<PdfDictionary>(xref[PdfName.AuthCode]);
        Assert.Equal(PdfName.Standalone, carried.GetName(PdfName.MACLocation));
    }

    private static byte[] Write(IPdfObjectEncryptor encryptor, PdfDictionary authCode = null)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));
        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));
        var marker = new PdfDictionary();
        marker.Set(new PdfName("Marker"), new PdfString(Encoding.ASCII.GetBytes("secret")));
        var stream = new PdfStream();
        stream.RawData = Encoding.ASCII.GetBytes("STREAM-DATA");
        var encrypt = new PdfDictionary();
        encrypt.Set(PdfName.Filter, PdfName.Standard);
        encrypt.Set(PdfName.O, new PdfString(Encoding.ASCII.GetBytes("owner-bytes")));

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        trailer.Set(PdfName.Encrypt, new PdfReference(5, 0));
        if (authCode != null) trailer.Set(PdfName.AuthCode, authCode);

        using var ms = new MemoryStream();
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 7),
            new List<(int, PdfObject)> { (1, catalog), (2, pages), (3, marker), (4, stream), (5, encrypt) },
            trailer, new PdfCompressedWriteOptions { ObjectEncryptor = encryptor });
        return ms.ToArray();
    }

    private static string Latin1(byte[] bytes) => Core.Text.Latin1Text.Decode(bytes, 0, bytes.Length);
}
