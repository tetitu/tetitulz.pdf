// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// A file with no <c>%PDF-</c> header, or a version after it that does not parse, opens: the version
/// is taken as 1.4 and <see cref="PdfParser.LastHeaderRecovery"/> says so, as another reader opens
/// such public-corpus files. A file that is not a PDF at all still refuses, at the catalog, and the refusal says the
/// header was missing too. A header that reads as written leaves the slot null.
/// </summary>
public class HeaderLenienceTests
{
    private const string CatalogBody = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PagesBody   = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string PageBody    = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

    [Fact]
    public void NoHeader_Opens_AsVersion14_AndSaysSo()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic(header: null)));

        Assert.Equal(new PdfVersion(1, 4), doc.Version);
        Assert.Single(doc.Pages);
        Assert.Equal(612d, ((PdfNumber)doc.Pages[0].MediaBox[2]).DoubleValue);
        Assert.Contains("no %PDF- header", PdfParser.LastHeaderRecovery!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("%PDF-x.y\n", "'' does not parse")]
    [InlineData("%PDF-1.\n", "'1.' does not parse")]
    [InlineData("%PDF-\n", "'' does not parse")]
    [InlineData("%PDF-.7\n", "'.7' does not parse")]
    public void AnUnparsableVersion_Opens_AsVersion14_AndSaysWhatWasWritten(string header, string said)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic(header)));

        Assert.Equal(new PdfVersion(1, 4), doc.Version);
        Assert.Single(doc.Pages);
        Assert.Contains(said, PdfParser.LastHeaderRecovery!, StringComparison.Ordinal);
    }

    /// <summary>The control: a header that reads as written is the version, and the slot stays null — the minor digits only, as before.</summary>
    [Theory]
    [InlineData("%PDF-1.7\n", 1, 7)]
    [InlineData("%PDF-2.0\n", 2, 0)]
    [InlineData("%PDF-1.4%\xE2\xE3\n", 1, 4)]
    public void AReadableHeader_IsTheVersion_AndNothingIsSaid(string header, int major, int minor)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Classic(header)));

        Assert.Equal(new PdfVersion(major, minor), doc.Version);
        Assert.Null(PdfParser.LastHeaderRecovery);
    }

    /// <summary>A file that is not a PDF refuses at the catalog, and the refusal names the header as well.</summary>
    [Fact]
    public void NotAPdfAtAll_StillRefuses_NamingTheHeaderToo()
    {
        byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 1, 0, 0, 0, 1 };

        var ex = Assert.Throws<PdfParseException>(() => PdfDocument.Open(new MemoryStream(png)));

        Assert.Contains("Cannot find document catalog", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no readable %PDF- header", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSlot_IsClearedByTheNextParse()
    {
        using (PdfDocument.Open(new MemoryStream(Classic(header: null)))) { }
        Assert.NotNull(PdfParser.LastHeaderRecovery);

        using (PdfDocument.Open(new MemoryStream(Classic("%PDF-1.7\n")))) { }
        Assert.Null(PdfParser.LastHeaderRecovery);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A well-formed classic file under <paramref name="header"/> (none when null), its offsets counted from byte 0 of the stream — which is the header's position, or 0.</summary>
    private static byte[] Classic(string? header)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); file.Write(b, 0, b.Length); }

        if (header != null) Write(header);
        long o1 = file.Position; Write("1 0 obj\n" + CatalogBody + "\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n" + PagesBody + "\nendobj\n");
        long o3 = file.Position; Write("3 0 obj\n" + PageBody + "\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }
}
