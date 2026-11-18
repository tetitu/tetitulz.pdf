// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The one-page generators' skeleton (<see cref="SinglePageDocumentWriter"/>, under the SVG converter
/// and the barcode writer) names the version its objects use: 1.4, raised by a built object that needs
/// more — an OpenType font program, PDF 1.6.
/// </summary>
public sealed class OnePageHeaderVersionTests
{
    private static readonly string Cyrillic = new string(Enumerable.Range(0x0410, 3).Select(c => (char)c).ToArray());

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

    private static byte[] Svg(System.Func<string, byte[]> provider) => SvgToPdfConverter.Convert(
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><text x=\"10\" y=\"20\">" + Cyrillic + "</text></svg>",
        new SvgConversionOptions { FontProvider = provider });

    [Fact]
    public void SvgText_InAnOpenTypeFont_IsWrittenAsPdf16()
    {
        byte[] pdf = Svg(_ => MinimalOpenTypeCff.Build(Cyrillic));

        Assert.Contains("/OpenType", Encoding.Latin1.GetString(pdf));   // the font program the raise is for
        Assert.Equal("%PDF-1.6", Header(pdf));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(PdfVersion.Pdf16, doc.Version);
    }

    [Fact]
    public void SvgText_InATrueTypeFont_StaysPdf14()
    {
        byte[] pdf = Svg(_ => MinimalTrueType.Build(Cyrillic));

        Assert.Contains("/FontFile2", Encoding.Latin1.GetString(pdf));  // a font was embedded, just not OpenType
        Assert.Equal("%PDF-1.4", Header(pdf));
    }
}
