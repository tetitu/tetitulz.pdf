// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// Read-only operations in Base accept a password and answer from the decrypted document, without
/// the caller reaching for the Encryption assembly's mirrors.
/// </summary>
/// <remarks>
/// <para>Base cannot reference <c>Tetitulz.Pdf.Encryption</c> — the dependency runs the other way
/// — so an encrypted read used to mean calling <c>PdfEncryptedForms.GetFields</c>, which decrypts
/// and calls straight back into <see cref="PdfFormReader"/>. The inversion through
/// <see cref="PdfDecryptorHolder"/> that the renderer already used made the detour unnecessary for
/// <b>reads</b>. Writes still route through Encryption and must: they re-encrypt.</para>
/// <para>Each assertion names the field or image the document actually contains. An
/// implementation that returned an empty list for an encrypted document — which is what these
/// entry points did before, and is indistinguishable from a document with no fields — fails
/// every one.</para>
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedReadOperationsTests
{
    private const string UserPassword = "s3cret";

    public EncryptedReadOperationsTests() => PdfEncryptionOperations.Register();

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All
        });
        return output.ToArray();
    }

    /// <summary>
    /// The named field comes back from an encrypted document, with the same name the plaintext
    /// document yields.
    /// </summary>
    [Fact]
    public void GetFields_WithPassword_ReadsTheFieldsOfAnEncryptedDocument()
    {
        byte[] plain = FormPdfFixtures.TextField(fieldName: "surname");

        using var plainStream = new MemoryStream(plain);
        string[] expected = PdfFormReader.GetFields(plainStream).Select(f => f.FullyQualifiedName).ToArray();
        Assert.Contains("surname", expected);

        using var encrypted = new MemoryStream(Encrypt(plain));
        string[] actual = PdfFormReader.GetFields(encrypted, UserPassword)
                                       .Select(f => f.FullyQualifiedName).ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The complement: a wrong password must fail loudly. Returning an empty list would be
    /// indistinguishable from a document that has no fields, which is the failure mode this whole
    /// entry point exists to remove.
    /// </summary>
    [Fact]
    public void GetFields_WithWrongPassword_ThrowsRatherThanReturningNoFields()
    {
        using var encrypted = new MemoryStream(
            Encrypt(FormPdfFixtures.TextField(fieldName: "surname")));

        Assert.ThrowsAny<Exception>(() => PdfFormReader.GetFields(encrypted, "not-the-password"));
    }

    /// <summary>
    /// An unencrypted document reaches the same answer through the password overload, so a caller
    /// that does not know whether its input is encrypted can use one entry point.
    /// </summary>
    [Fact]
    public void GetFields_WithPassword_OnAPlainDocument_MatchesTheNoPasswordOverload()
    {
        byte[] plain = FormPdfFixtures.TextField(fieldName: "surname");

        using var a = new MemoryStream(plain);
        using var b = new MemoryStream(plain);

        Assert.Equal(PdfFormReader.GetFields(a).Select(f => f.FullyQualifiedName),
                     PdfFormReader.GetFields(b, UserPassword).Select(f => f.FullyQualifiedName));
    }

    /// <summary>
    /// <see cref="PdfImageReplacer.FindImages(Stream, string)"/> enumerates the same image objects
    /// from an encrypted document as from its plaintext original.
    /// </summary>
    [Fact]
    public void FindImages_WithPassword_EnumeratesTheImagesOfAnEncryptedDocument()
    {
        byte[] plain = ImageDocument();

        using var plainStream = new MemoryStream(plain);
        int[] expected = PdfImageReplacer.FindImages(plainStream)
                                         .Select(i => i.ObjectNumber).OrderBy(n => n).ToArray();
        Assert.NotEmpty(expected);

        using var encrypted = new MemoryStream(Encrypt(plain));
        int[] actual = PdfImageReplacer.FindImages(encrypted, UserPassword)
                                       .Select(i => i.ObjectNumber).OrderBy(n => n).ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// With no decryptor registered the read must say so rather than answer from the encrypted
    /// bytes. Asserted by clearing the registry for the duration of the call.
    /// </summary>
    [Fact]
    public void ReadDecrypted_WithNoRegisteredDecryptor_ThrowsNamingTheRegistration()
    {
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var encrypted = new MemoryStream(Encrypt(FormPdfFixtures.TextField()));

            var ex = Assert.Throws<InvalidOperationException>(
                () => PdfDecryptedInput.ReadDecrypted(encrypted, UserPassword));
            Assert.Contains("PdfEncryptionOperations.Register", ex.Message);
        }
        finally
        {
            PdfDecryptorHolder.Current = saved;
        }
    }

    /// <summary>
    /// An encrypted document's transparency is listed when the password is given — the half-opaque
    /// graphics state its page carries — where an encrypted catalog read without it lists nothing.
    /// </summary>
    [Fact]
    public void FindTransparency_WithPassword_ReadsAnEncryptedDocument()
    {
        byte[] encrypted = Encrypt(FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 << /ca 0.5 >> >> >> >>",
            FormPdfFixtures.StreamBody("", "/GS0 gs 0 0 10 10 re f")));

        var findings = PdfOperations.FindTransparency(new MemoryStream(encrypted), UserPassword);

        Assert.Contains(findings, f => f.Kind == Tetitulz.Pdf.Base.Operations.Documents.TransparencyKind.ConstantAlpha
                                       && f.Detail == "/GS0");
    }

    /// <summary>A one-page document carrying a single image XObject.</summary>
    private static byte[] ImageDocument() => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
        " /Resources << /XObject << /Im0 5 0 R >> >> >>",
        FormPdfFixtures.StreamBody("", "q 10 0 0 10 10 10 cm /Im0 Do Q"),
        FormPdfFixtures.StreamBody(
            "/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray" +
            " /BitsPerComponent 8", ""));
}
