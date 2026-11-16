// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="PdfLinearizer"/> refuses a signed document unless the caller sets
/// <see cref="LinearizationOptions.AllowSignatureInvalidation"/>.
/// </summary>
/// <remarks>
/// Linearization gives every object a new number and a new offset, so a signature's
/// <c>/ByteRange</c> names bytes that no longer exist. The optimizer beside it refused on
/// <c>PdfSignatureDetection</c>'s answer; the linearizer never asked, and a signed corpus file came
/// out of it looking signed and verifying as corrupt.
/// </remarks>
public sealed class LinearizerSignatureRefusalTests
{
    /// <summary>
    /// A one-page document whose form has one signature field — signed, with a <c>/V</c>, or an empty
    /// placeholder without one.
    /// </summary>
    private static byte[] Pdf(bool signed)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }

        Obj("<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Obj("<< /FT /Sig /T (Signature1)" + (signed ? " /V 5 0 R" : "") + " >>");
        Obj("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
            "/ByteRange [0 100 200 100] /Contents <00> >>");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void ASignedDocumentIsRefusedAndNothingIsWritten()
    {
        using var output = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(
            () => PdfLinearizer.Linearize(new MemoryStream(Pdf(signed: true)), output));

        Assert.Contains("LinearizationOptions.AllowSignatureInvalidation", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void OptingInLinearizesTheSignedDocument()
    {
        using var output = new MemoryStream();

        PdfLinearizer.Linearize(new MemoryStream(Pdf(signed: true)), output,
                                new LinearizationOptions { AllowSignatureInvalidation = true });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Single(doc.Pages);
    }

    [Fact]
    public void AnUnsignedSignatureFieldNeedsNoOptIn()
    {
        using var output = new MemoryStream();

        PdfLinearizer.Linearize(new MemoryStream(Pdf(signed: false)), output);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        Assert.Single(doc.Pages);
    }
}
