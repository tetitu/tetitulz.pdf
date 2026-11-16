// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// Editing an encrypted document <b>in place</b> — appending a revision whose objects are
/// encrypted with the document's own file key, instead of rewriting the whole file.
///
/// <para>Every assertion here decrypts the output and reads the value back. That is the only check
/// that means anything: an appended revision written in clear text produces a file that opens
/// perfectly well and whose field values decrypt to noise, so "it wrote a valid PDF" is precisely
/// the assertion that cannot tell the fix from the bug.</para>
/// </summary>
public class EncryptedIncrementalUpdateTests
{
    private const string UserPassword = "u";
    private const string OwnerPassword = "o";

    private static byte[] Encrypt(byte[] plain, EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
        });
        return output.ToArray();
    }

    private static byte[] FillEncrypted(byte[] encryptedPdf, string password,
        (string Name, string Value) value, out FormFillResult result)
    {
        using var input = new MemoryStream(encryptedPdf);
        using var output = new MemoryStream();
        result = PdfEncryptedForms.Fill(input, output, password,
            new Dictionary<string, string> { [value.Name] = value.Value });
        return output.ToArray();
    }

    /// <summary>Decrypts and returns the field's stored value, as a reader would see it.</summary>
    private static string ReadFieldValue(byte[] encryptedPdf, string password, int fieldObjectNumber)
    {
        using var input = new MemoryStream(encryptedPdf);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(input, decrypted, password);
        decrypted.Position = 0;

        using var doc = PdfDocument.Open(decrypted);
        var field = (PdfDictionary)doc.GetObject(fieldObjectNumber);
        return (doc.ResolveObject(field[PdfName.V]) as PdfString)?.GetText();
    }

    // ================================================================
    //  The three algorithm families
    // ================================================================

    /// <summary>
    /// Values here are deliberately longer than one AES block. Under 16 bytes the decryptor's
    /// "too short to hold an IV" short-circuit returns the bytes unchanged, so a value written in
    /// clear text would read back correctly and the test could not tell the two apart.
    /// </summary>
    private const string LongValue = "Ada Lovelace, Countess of Lovelace";

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void FilledValue_DecryptsBackToWhatWasWritten(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), algorithm);

        byte[] filled = FillEncrypted(encrypted, UserPassword, ("name", LongValue), out var result);

        Assert.Contains("name", result.FilledFields);
        Assert.Equal(LongValue, ReadFieldValue(filled, UserPassword, 6));
    }

    /// <summary>
    /// The characteristic failure of a clear-text append: the file opens, the object is there, and
    /// the value decrypts to noise. Writing the plaintext into the ciphertext slot must therefore
    /// be detectable — the stored bytes cannot equal the plaintext.
    /// </summary>
    [Fact]
    public void FilledValue_IsStoredAsCiphertext_NotPlaintext()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), EncryptionAlgorithm.AES128);
        byte[] filled = FillEncrypted(encrypted, UserPassword, ("name", LongValue), out _);

        // The appended revision is everything after the original's %%EOF.
        string appended = Encoding.Latin1.GetString(filled, encrypted.Length, filled.Length - encrypted.Length);
        Assert.DoesNotContain(LongValue, appended, StringComparison.Ordinal);

        // …and it is nonetheless readable with the password.
        Assert.Equal(LongValue, ReadFieldValue(filled, UserPassword, 6));
    }

    /// <summary>
    /// The whole point of appending rather than rewriting: byte zero to the end of the original is
    /// unchanged, so a signature taken over it still verifies and the owner password still works.
    /// </summary>
    [Fact]
    public void OriginalBytes_ArePreservedExactly()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), EncryptionAlgorithm.AES128);
        byte[] filled = FillEncrypted(encrypted, UserPassword, ("name", "Ada"), out _);

        Assert.True(filled.Length > encrypted.Length, "nothing was appended");
        Assert.Equal(encrypted, filled[..encrypted.Length]);
    }

    [Fact]
    public void OwnerPassword_StillOpensTheEditedDocument()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), EncryptionAlgorithm.AES128);
        byte[] filled = FillEncrypted(encrypted, UserPassword, ("name", LongValue), out _);

        // The round-trip alternative cannot do this: /O and /U derive one-way from both passwords,
        // so re-encrypting loses the owner password.
        Assert.Equal(LongValue, ReadFieldValue(filled, OwnerPassword, 6));
    }

    /// <summary>
    /// <c>/AS</c> and <c>/V</c> are <b>names</b> here, and §7.6.2 never encrypts a name — so this
    /// pins that the revision lands and the check-box logic survives the encrypted path, not that
    /// anything was enciphered. The ciphertext claims are the string and stream tests above.
    /// </summary>
    [Fact]
    public void CheckboxAppearanceState_SurvivesTheEncryptedAppend()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.Checkbox(), EncryptionAlgorithm.AES128);
        byte[] filled = FillEncrypted(encrypted, UserPassword, ("agree", "true"), out var result);

        Assert.Contains("agree", result.FilledFields);

        using var input = new MemoryStream(filled);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(input, decrypted, UserPassword);
        decrypted.Position = 0;

        using var doc = PdfDocument.Open(decrypted);
        var widget = (PdfDictionary)doc.GetObject(6);
        Assert.Equal("Yes", widget.GetName(PdfName.AS)?.Value);
        Assert.Equal("Yes", widget.GetName(PdfName.V)?.Value);
    }

    /// <summary>
    /// An appearance stream is encrypted as a stream, with its own per-object key — and its
    /// <c>/Length</c> must describe the ciphertext, which AES makes longer than the plaintext.
    /// A <c>/Length</c> taken from the plaintext leaves the reader parsing past the end of the
    /// data.
    /// </summary>
    [Fact]
    public void AppearanceStream_IsEncryptedAndItsLengthDescribesTheCiphertext()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), EncryptionAlgorithm.AES128);
        byte[] filled = FillEncrypted(encrypted, UserPassword, ("name", "Ada"), out _);

        using var input = new MemoryStream(filled);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(input, decrypted, UserPassword);
        decrypted.Position = 0;

        using var doc = PdfDocument.Open(decrypted);
        var field = (PdfDictionary)doc.GetObject(6);
        var ap = doc.ResolveObject(field[PdfName.AP]) as PdfDictionary;
        var normal = doc.ResolveObject(ap[PdfName.N]) as PdfStream;

        Assert.NotNull(normal);
        string content = Encoding.Latin1.GetString(normal.GetDecodedData());
        Assert.Contains("(Ada) Tj", content, StringComparison.Ordinal);
    }

    // ================================================================
    //  Image replacement — the other in-place editor
    // ================================================================

    /// <summary>
    /// The replaced pixels have to survive the encrypted append and render: the image XObject's
    /// samples are a stream, so they are enciphered under the image object's own key.
    /// </summary>
    [Fact]
    public void ReplacedImage_RendersAsTheNewPixels_AfterAnEncryptedAppend()
    {
        byte[] plain = ImageFixtures.OneImagePdf(ImageFixtures.Red, 1, 1);
        byte[] encrypted = Encrypt(plain, EncryptionAlgorithm.AES128);

        int objectNumber;
        using (var probe = new MemoryStream(encrypted))
        {
            // Deliberately the deprecated member: it is still public API and must keep working.
            // Its Base replacement is covered by EncryptedReadOperationsTests.
#pragma warning disable CS0618
            var images = PdfEncryptedImages.FindImages(probe, UserPassword);
#pragma warning restore CS0618
            objectNumber = Assert.Single(images).ObjectNumber;
        }

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        var result = PdfEncryptedImages.ReplaceByObjectNumber(
            input, output, UserPassword, objectNumber,
            ReplacementImage.Rgb24(ImageFixtures.Blue, 1, 1));

        Assert.Equal(objectNumber, result.ObjectNumber);

        // Decrypt and render: the page must now be blue, not red.
        byte[] edited = output.ToArray();
        using var editedStream = new MemoryStream(edited);
        using var decrypted = new MemoryStream();
        PdfDecryptor.Decrypt(editedStream, decrypted, UserPassword);

        using var bmp = Tetitulz.Pdf.Base.Tests.Rendering.PixelAssert.Render(decrypted.ToArray(), 72);
        var centre = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(centre.Blue > 200 && centre.Red < 80,
            $"expected the replacement's blue after decryption, got {centre}");
    }

    // ================================================================
    //  Guard rails
    // ================================================================

    [Fact]
    public void WrongPassword_IsRefused()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.TextField(), EncryptionAlgorithm.AES128);

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfEncryptedForms.Fill(input, output, "not-the-password",
                new Dictionary<string, string> { ["name"] = "Ada" }));
        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unencrypted document has no key to encrypt with; producing an "encryptor" for it would
    /// yield a revision no reader can decrypt, so it is refused rather than tolerated.
    /// </summary>
    [Fact]
    public void UnencryptedDocument_IsRefusedByTheEncryptedPath()
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField());
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfEncryptedForms.Fill(input, output, UserPassword,
                new Dictionary<string, string> { ["name"] = "Ada" }));
        Assert.Contains("not encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryCreate_OnAnUnencryptedDocument_ReportsWhyRatherThanThrowing()
    {
        using var input = new MemoryStream(FormPdfFixtures.TextField());

        bool created = PdfIncrementalEncryption.TryCreate(input, UserPassword, out var encryptor, out string error);

        Assert.False(created);
        Assert.Null(encryptor);
        Assert.Contains("not encrypted", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetFields_ReadsAnEncryptedFormsFieldNames()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.ChoiceField(), EncryptionAlgorithm.AES256);

        using var input = new MemoryStream(encrypted);
        // Deliberately the deprecated member -- see the note above.
#pragma warning disable CS0618
        var fields = PdfEncryptedForms.GetFields(input, UserPassword);
#pragma warning restore CS0618

        var field = Assert.Single(fields);
        Assert.Equal("country", field.FullyQualifiedName);
        Assert.Equal(2, field.Options.Count);
        Assert.Equal("Israel", field.Options[0].DisplayText);
    }

    /// <summary>
    /// Reading an encrypted form through the ordinary reader is refused — an encrypted document
    /// exposes no catalog, and the empty list it used to return claimed a form with no fields —
    /// and the refusal names the encrypted-aware entry point.
    /// </summary>
    [Fact]
    public void PlainReader_OnAnEncryptedForm_Refuses()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.ChoiceField(), EncryptionAlgorithm.AES256);

        using var input = new MemoryStream(encrypted);
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfFormReader.GetFields(input));
        Assert.Contains("GetFields(Stream, string)", ex.Message, StringComparison.Ordinal);
    }
}
