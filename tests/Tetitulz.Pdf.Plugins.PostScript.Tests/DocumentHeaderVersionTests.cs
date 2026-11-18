// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A converted document's header names what its objects use, read off them
/// (<c>Core.Writing.PdfFeatureVersions</c>): 1.4, raised to 1.5 by a 16-bit image (ISO 32000-2
/// Table 87). The header is read from the file's own bytes.
/// </summary>
public sealed class DocumentHeaderVersionTests
{
    private static byte[] ConvertDocument(string ps)
    {
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(ps));
        using var output = new MemoryStream();
        PostScriptConversion.ConvertDocument(source, output);
        return output.ToArray();
    }

    private static string Header(byte[] pdf) => Encoding.ASCII.GetString(pdf, 0, 8);

    [Fact]
    public void ASixteenBitImage_MakesTheFile15()
    {
        byte[] pdf = ConvertDocument("%!PS\n100 100 scale 1 1 16 [1 0 0 -1 0 1] <0102> image showpage\n");

        Assert.Contains("/BitsPerComponent 16", Encoding.Latin1.GetString(pdf));
        Assert.Equal("%PDF-1.5", Header(pdf));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Equal(PdfVersion.Pdf15, doc.Version);
    }

    [Fact]
    public void AnEightBitImage_LeavesTheFileAt14()
    {
        byte[] pdf = ConvertDocument("%!PS\n100 100 scale 1 1 8 [1 0 0 -1 0 1] <80> image showpage\n");

        Assert.Contains("/BitsPerComponent 8", Encoding.Latin1.GetString(pdf));
        Assert.Equal("%PDF-1.4", Header(pdf));
    }

    [Fact]
    public void APlainPage_Is14_AndOpens()
    {
        byte[] pdf = ConvertDocument("%!PS\n0 0 1 setrgbcolor newpath 10 10 moveto 90 10 lineto 90 90 lineto closepath fill showpage\n");

        Assert.Equal("%PDF-1.4", Header(pdf));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Single(doc.Pages);
    }
}
