// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="PdfSignatureField"/> answers the same values whichever way the signature dictionary's
/// entries are spelled — direct, or as indirect references (§7.3.10).
/// </summary>
/// <remarks>
/// <para>Its readers used the typed getters, which do not follow a reference, so
/// <c>/SubFilter 9 0 R</c> read as <c>null</c>: <c>PdfOperations.GetSignatures</c> reported no
/// sub-filter for a signature whose verifier — which resolves its own reads — reported the real one.
/// Two public answers about one file.</para>
///
/// <para><b><c>/Contents</c> stays a direct read on purpose.</b> Its bytes are the hole the
/// <c>/ByteRange</c> leaves out, so a conforming signer cannot have written it anywhere but inline
/// (§12.8.1, Table 255); resolving a reference there would let a malformed file answer "has
/// contents" for a value the range could not have excluded.</para>
/// </remarks>
public sealed class SignatureFieldIndirectReadsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryEntryReadsTheSame_WhicheverWayItIsSpelled(bool indirect)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirect)));

        var field = doc.AcroForm.GetSignatureFields().Single();

        Assert.Equal("Adobe.PPKLite", field.Filter);
        Assert.Equal("adbe.pkcs7.detached", field.SubFilter);
        Assert.Equal("a stated reason", field.Reason);
        Assert.Equal("a stated location", field.Location);
        Assert.Equal("D:20260917120000Z", field.SigningTime);
        Assert.Equal(new long[] { 0, 10, 20, 10 }, field.ByteRange!.Select(e => ((PdfNumber)e).LongValue));
        Assert.Equal(new byte[] { 0 }, field.Contents!.RawBytes);
    }

    [Fact]
    public void AnIndirectContentsIsNotFollowed()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(indirect: false, indirectContents: true)));

        var field = doc.AcroForm.GetSignatureFields().Single();

        Assert.Null(field.Contents);
        Assert.Equal("adbe.pkcs7.detached", field.SubFilter);   // the rest still reads
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// One signed field. With <paramref name="indirect"/>, every value of the signature dictionary
    /// but <c>/Contents</c> lives in objects 7–12; with <paramref name="indirectContents"/>,
    /// <c>/Contents</c> is object 13.
    /// </summary>
    private static byte[] Pdf(bool indirect, bool indirectContents = false)
    {
        string filter = indirect ? "7 0 R" : "/Adobe.PPKLite";
        string subFilter = indirect ? "8 0 R" : "/adbe.pkcs7.detached";
        string byteRange = indirect ? "9 0 R" : "[0 10 20 10]";
        string reason = indirect ? "10 0 R" : "(a stated reason)";
        string location = indirect ? "11 0 R" : "(a stated location)";
        string m = indirect ? "12 0 R" : "(D:20260917120000Z)";
        string contents = indirectContents ? "13 0 R" : "<00>";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /FT /Sig /T (Signature1) /V 6 0 R >>",
            $"<< /Type /Sig /Filter {filter} /SubFilter {subFilter} /ByteRange {byteRange} " +
                $"/Contents {contents} /Reason {reason} /Location {location} /M {m} >>",
            "/Adobe.PPKLite",
            "/adbe.pkcs7.detached",
            "[0 10 20 10]",
            "(a stated reason)",
            "(a stated location)",
            "(D:20260917120000Z)",
            "<00>");
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
