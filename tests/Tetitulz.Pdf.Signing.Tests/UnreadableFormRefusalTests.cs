// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Signing refuses a form it cannot read. Read as none, an unreadable <c>/AcroForm</c> or
/// <c>/Fields</c> array would make the signed revision write a form holding only the signature
/// field, judged against no existing signatures — and a reader whose parser can read the old form
/// would show every field gone from the signed document.
/// </summary>
public sealed class UnreadableFormRefusalTests
{
    [Theory]
    [InlineData(Unreadable.Form, "/AcroForm could not be read")]
    [InlineData(Unreadable.Fields, "/Fields array could not be read")]
    public void Adversarial_unreadable_form_refuses_the_signature(Unreadable what, string reason)
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() => Sign(Pdf(what), output));

        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The other three writers refuse the same unreadable form through the same door
    /// (<c>SignatureFieldRegistration.FormOf</c>): the plan, the visible signature and the document
    /// timestamp. The last is refused before its TSA, a loopback port nothing listens on, is dialled.
    /// </summary>
    [Theory]
    [InlineData("plan")]
    [InlineData("visible")]
    [InlineData("timestamp")]
    public void Adversarial_unreadable_form_refuses_every_signature_writer(string writer)
    {
        byte[] input = Pdf(Unreadable.Form);
        var ex = Assert.Throws<PdfSigningException>(() =>
        {
            switch (writer)
            {
                case "plan":
                    var (pfx, password) = Certificate();
                    using (var doc = PdfDocument.Open(new MemoryStream(input)))
                        new PdfSigner().Plan(doc, 0, new SignatureParameters
                        {
                            CertificateStream = new MemoryStream(pfx),
                            CertificatePassword = password,
                            SignatureFieldName = "Signature1",
                        });
                    break;
                case "visible":
                    var (vpfx, vpassword) = Certificate();
                    string path = Path.Combine(Path.GetTempPath(), $"unreadable-form-{Guid.NewGuid():N}.pfx");
                    File.WriteAllBytes(path, vpfx);
                    try
                    {
                        VisualSignatureBuilder.Sign(new MemoryStream(input), new MemoryStream(), path, vpassword,
                            Jpeg(), pageNumber: 1, x: 10, y: 50, width: 40, height: 20);
                    }
                    finally { File.Delete(path); }
                    break;
                default:
                    Timestamping.PdfDocumentTimestamper.AddTimestamp(
                        new MemoryStream(input), new MemoryStream(), "http://127.0.0.1:9/");
                    break;
            }
        });

        Assert.Contains("/AcroForm could not be read", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a readable form keeps its text field beside the new signature field.</summary>
    [Fact]
    public void A_readable_form_keeps_its_fields_when_signed()
    {
        var output = new MemoryStream();
        Sign(Pdf(Unreadable.Nothing), output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var form = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Catalog.Dictionary[PdfName.AcroForm]));
        var fields = Assert.IsType<PdfArray>(doc.ResolveObject(form[PdfName.Fields]));
        Assert.Equal(2, fields.Count);
        Assert.Equal(6, Assert.IsType<PdfReference>(fields[0]).ReferenceObjectNumber);
    }

    public enum Unreadable { Nothing, Form, Fields }

    private static (byte[] Pfx, string Password) Certificate()
    {
        var root = SigningTestCrypto.Ca("CN=Unreadable Form Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Unreadable Form Leaf");
        return SigningTestCrypto.Pfx(leaf, root.Cert);
    }

    /// <summary>A JPEG carrying only a baseline frame header, which is all the stamp embedder reads.</summary>
    private static byte[] Jpeg()
        => new byte[] { 0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 16, 0, 16, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xFF, 0xD9 };

    private static void Sign(byte[] input, Stream output)
    {
        var (pfx, password) = Certificate();
        new PdfSigner().Sign(new MemoryStream(input), output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            SignatureFieldName = "Signature1",
        });
    }

    /// <summary>
    /// A one-page document whose form (object 4) lists its fields through object 5, an array holding
    /// one text field (object 6). The unreadable one is a lone <c>]</c>.
    /// </summary>
    private static byte[] Pdf(Unreadable what)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [6 0 R] >>",
            what == Unreadable.Form ? "]" : "<< /Fields 5 0 R /DA (/Helv 0 Tf 0 g) >>",
            what == Unreadable.Fields ? "]" : "[6 0 R]",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /V (x) /Rect [10 10 60 30] /P 3 0 R >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
