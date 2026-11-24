// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The whole-file encryptors carry a source's content, not its layout: an input written with object streams and a
/// cross-reference stream does not come out with its containers and stale cross-reference stream written back,
/// encrypted and unreferenced, beside the members the parser already handed over — one rule with the decryptor,
/// <see cref="FileLayoutObjects"/>. With the compressed layout kept, the output holds layout objects of its
/// <b>own</b> — one object stream and one cross-reference stream, numbered past the source's — and none of the
/// source's; written classic, it holds none at all.
/// </summary>
public class EncryptorLayoutObjectTests
{
    private const string Password = "layout-pw";

    /// <summary>The public-key round trip's one-page source, rewritten with object streams and a cross-reference stream: objects 1 to 5, the object stream 6, the cross-reference stream 7.</summary>
    private static byte[] CompressedSource()
    {
        using var doc = PdfDocument.Open(new MemoryStream(PublicKeyRoundTripTests.SourcePdf()));
        var objects = doc.Parser.EnumerateAllObjects().Select(kv => (kv.Key, kv.Value)).ToList();
        using var output = new MemoryStream();
        PdfCompressedFileWriter.Write(output, doc.Version, objects, doc.Trailer);
        return output.ToArray();
    }

    /// <summary>Every object the file's cross-reference table lists and the parser can read, by number — read without decrypting, so an encrypted object stream's members are not among them.</summary>
    private static Dictionary<int, PdfObject> ObjectsOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return doc.Parser.EnumerateAllObjects().ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static int[] LayoutObjectNumbers(Dictionary<int, PdfObject> objects)
        => objects.Where(kv => kv.Value is PdfStream s
                               && (s.GetTypeName() == PdfName.ObjStm || s.GetTypeName() == PdfName.XRef))
                  .Select(kv => kv.Key).OrderBy(k => k).ToArray();

    private static int LayoutObjectCount(Dictionary<int, PdfObject> objects) => LayoutObjectNumbers(objects).Length;

    private static string Latin1(byte[] bytes) => Encoding.GetEncoding(28591).GetString(bytes);

    /// <summary>The output's layout objects are its own two, numbered past the source's 6 and 7 and past the <c>/Encrypt</c> dictionary; nothing of the source's layout is carried.</summary>
    private static void AssertOwnLayoutOnly(Dictionary<int, PdfObject> written, int encryptDictionaryNumber)
    {
        int[] layout = LayoutObjectNumbers(written);
        Assert.Equal(2, layout.Length);
        Assert.All(layout, n => Assert.True(n > encryptDictionaryNumber && n > 7, "layout object " + n + " is not the writer's own"));
        Assert.DoesNotContain(6, written.Keys);
        Assert.DoesNotContain(7, written.Keys);
    }

    private static int EncryptDictionaryNumber(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Assert.IsType<PdfReference>(doc.Trailer[PdfName.Encrypt]).ReferenceObjectNumber;
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_40R3)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    [InlineData(EncryptionAlgorithm.AES256GCM)]
    public void PasswordEncryptor_CarriesNoLayoutObjectOfTheSources_WritingItsOwnTwoOrNone(EncryptionAlgorithm algorithm)
    {
        byte[] source = CompressedSource();
        Dictionary<int, PdfObject> sourceObjects = ObjectsOf(source);
        // Potency: the source holds both kinds of layout object, beside its five content objects.
        Assert.Equal(new[] { 6, 7 }, LayoutObjectNumbers(sourceObjects));
        Assert.Equal(7, sourceObjects.Count);

        // The layout kept: the file's own object stream and cross-reference stream, the source's not carried.
        byte[] kept = Encrypt(source, algorithm, useObjectStreams: null);
        AssertOwnLayoutOnly(ObjectsOf(kept), EncryptDictionaryNumber(kept));
        Assert.DoesNotContain(PublicKeyRoundTripTests.Marker, Latin1(kept));
        byte[] decrypted = Decrypt(kept);
        Assert.Contains(PublicKeyRoundTripTests.Marker, Latin1(decrypted));
        // The decryptor unpacks: the five content objects, and no layout object at all.
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ObjectsOf(decrypted).Keys.OrderBy(k => k).ToArray());

        // Written classic: the five content objects and the /Encrypt dictionary — nothing else.
        byte[] classic = Encrypt(source, algorithm, useObjectStreams: false);
        Dictionary<int, PdfObject> written = ObjectsOf(classic);
        Assert.Equal(0, LayoutObjectCount(written));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }.Concat(new[] { written.Keys.Max() }).ToArray(),
                     written.Keys.OrderBy(k => k).ToArray());
        Assert.DoesNotContain(PublicKeyRoundTripTests.Marker, Latin1(classic));
        Assert.Contains(PublicKeyRoundTripTests.Marker, Latin1(Decrypt(classic)));
    }

    [Fact]
    public void PublicKeyEncryptor_CarriesNoLayoutObjectOfTheSources_WritingItsOwnTwo()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] source = CompressedSource();
        Assert.Equal(new[] { 6, 7 }, LayoutObjectNumbers(ObjectsOf(source)));

        using var plain = new MemoryStream(source);
        using var encrypted = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(plain, encrypted, certDer, PdfPermissions.All, EncryptionAlgorithm.AES256);

        byte[] output = encrypted.ToArray();
        AssertOwnLayoutOnly(ObjectsOf(output), EncryptDictionaryNumber(output));
        Assert.DoesNotContain(PublicKeyRoundTripTests.Marker, Latin1(output));

        byte[] decrypted = PublicKeyRoundTripTests.Decrypt(output, pfx);
        Assert.Contains(PublicKeyRoundTripTests.Marker, Latin1(decrypted));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ObjectsOf(decrypted).Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void RoundTrip_OperationWritingObjectStreams_KeepsThatLayout_CarryingNoLayoutObjectOfIt()
    {
        // An encrypted classic document; the operation rewrites the decrypted copy with object streams and a
        // cross-reference stream, as the optimizer does, and the round trip encrypts that again — in the
        // layout the operation chose, with the file's own containers, not the operation's — not every
        // object unpacked and written back out under a classic table, larger than its input.
        using var plain = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var protectedCopy = new MemoryStream();
        PdfEncryptor.Encrypt(plain, protectedCopy, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = EncryptionAlgorithm.AES128,
        });

        int[] layoutWrittenByOperation = null;
        using var input = new MemoryStream(protectedCopy.ToArray());
        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            input, output,
            new EncryptedRoundTripOptions { OpenPassword = Password },
            (inner, outer) =>
            {
                using var doc = PdfDocument.Open(inner);
                var objects = doc.Parser.EnumerateAllObjects().Select(kv => (kv.Key, kv.Value)).ToList();
                using var compressed = new MemoryStream();
                PdfCompressedFileWriter.Write(compressed, doc.Version, objects, doc.Trailer);
                layoutWrittenByOperation = LayoutObjectNumbers(ObjectsOf(compressed.ToArray()));
                compressed.Position = 0;
                compressed.CopyTo(outer);
            });

        Assert.True(result.Succeeded, result.FailureReason);
        // Potency: the operation's output held both kinds of layout object, numbered 6 and 7.
        Assert.Equal(new[] { 6, 7 }, layoutWrittenByOperation);
        byte[] reEncrypted = output.ToArray();
        AssertOwnLayoutOnly(ObjectsOf(reEncrypted), EncryptDictionaryNumber(reEncrypted));

        byte[] decrypted = Decrypt(reEncrypted);
        Assert.Contains(PublicKeyRoundTripTests.Marker, Latin1(decrypted));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, ObjectsOf(decrypted).Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void PasswordEncryptor_ClassicSource_KeepsEveryObject()
    {
        // Control: a source with no layout objects loses nothing — the five objects and the /Encrypt dictionary.
        byte[] source = PublicKeyRoundTripTests.SourcePdf();
        Assert.Equal(0, LayoutObjectCount(ObjectsOf(source)));

        byte[] encrypted = Encrypt(source, EncryptionAlgorithm.AES128, useObjectStreams: null);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, ObjectsOf(encrypted).Keys.OrderBy(k => k).ToArray());
    }

    private static byte[] Encrypt(byte[] source, EncryptionAlgorithm algorithm, bool? useObjectStreams)
    {
        using var plain = new MemoryStream(source);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password,
            Algorithm = algorithm,
            UseObjectStreams = useObjectStreams,
        });
        return encrypted.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted)
    {
        using var input = new MemoryStream(encrypted);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(input, decrypted, Password);
        return decrypted.ToArray();
    }
}
