// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A font's own streams decode under their own ceilings (<see cref="FontStreamBytes"/>): a CMap under
/// <see cref="PdfLimits.MaxCMapStreamBytes"/>, a font program under
/// <see cref="PdfLimits.MaxFontProgramBytes"/>, never the general half-gigabyte. A font whose
/// <c>/ToUnicode</c> is refused still loads, without it, and says why.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class FontStreamCeilingTests
{
    private const string Mapping = "beginbfchar\n<41> <05D0>\nendbfchar\n";

    [Fact]
    public void ACMapPastItsCeiling_IsRefused_AndOneAtIt_Decodes()
    {
        try
        {
            byte[] cmap = CMapText(padding: 4000);
            PdfLimits.MaxCMapStreamBytes = cmap.Length;
            Assert.Equal(cmap, FontStreamBytes.CMap(Flated(cmap)));

            PdfLimits.MaxCMapStreamBytes = cmap.Length - 1;
            var refused = Assert.Throws<PdfFilterException>(() => FontStreamBytes.CMap(Flated(cmap)));
            Assert.Contains("CMap", refused.Message);

            // The font-program ceiling is another setting: the same bytes pass as a program.
            Assert.Equal(cmap, FontStreamBytes.Program(Flated(cmap)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void AFontProgramPastItsCeiling_IsRefused_AndOneAtIt_Decodes()
    {
        try
        {
            byte[] program = new byte[10_000];
            for (int i = 0; i < program.Length; i++) program[i] = (byte)(i * 7);
            PdfLimits.MaxFontProgramBytes = program.Length;
            Assert.Equal(program, FontStreamBytes.Program(Flated(program)));

            PdfLimits.MaxFontProgramBytes = program.Length - 1;
            var refused = Assert.Throws<PdfFilterException>(() => FontStreamBytes.Program(Flated(program)));
            Assert.Contains("font program", refused.Message);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void TheDefaultsAreSizedForReal_CMapsAndPrograms_AndBelowTheGeneralCeiling()
    {
        Assert.Equal(16L * 1024 * 1024, PdfLimits.DefaultMaxCMapStreamBytes);
        Assert.Equal(64L * 1024 * 1024, PdfLimits.DefaultMaxFontProgramBytes);
        Assert.True(PdfLimits.DefaultMaxFontProgramBytes < PdfLimits.DefaultMaxDecompressedBytes);
    }

    [Fact]
    public void AdversarialToUnicodeBomb_LeavesTheFontLoaded_WithoutItsCMap_AndSaysWhy()
    {
        try
        {
            using PdfDocument doc = MinimalDoc();
            byte[] cmap = CMapText(padding: 4000);

            // The control: under the ceiling, the CMap maps the code and nothing is reported.
            PdfFont mapped = PdfFont.Create(FontWith(Flated(cmap)), doc);
            Assert.Null(PdfFont.LastToUnicodeError);
            Assert.Equal("א", mapped.Decode(new byte[] { 0x41 }));

            PdfLimits.MaxCMapStreamBytes = 1024;
            PdfFont unmapped = PdfFont.Create(FontWith(Flated(cmap)), doc);
            Assert.Contains("CMap", PdfFont.LastToUnicodeError);
            Assert.Equal("A", unmapped.Decode(new byte[] { 0x41 }));   // the encoding's reading

            // The slot belongs to the call: the next font's clean read clears it.
            PdfLimits.ResetToDefaults();
            PdfFont.Create(FontWith(Flated(cmap)), doc);
            Assert.Null(PdfFont.LastToUnicodeError);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void AnEmbeddedEncodingCMapPastItsCeiling_IsIdentity_AndSaysWhy()
    {
        try
        {
            using PdfDocument doc = MinimalDoc();
            byte[] cmap = System.Text.Encoding.ASCII.GetBytes("%" + new string(' ', 4000) +
                "\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n1 begincidrange\n<00> <FF> 0\nendcidrange\n");
            var type0 = new PdfDictionary { [PdfName.Subtype] = PdfName.Type0, [PdfName.Encoding] = Flated(cmap) };

            // The control: under the ceiling the stream's one-byte codespace is read.
            Assert.Equal(2, CMapCodec.FromFontEncoding(type0, doc).Decode(new byte[] { 1, 2 }).Count);   // one byte, one code
            Assert.Null(CMapCodec.LastEncodingError);

            PdfLimits.MaxCMapStreamBytes = 1024;
            type0[PdfName.Encoding] = Flated(cmap);
            CMapCodec refused = CMapCodec.FromFontEncoding(type0, doc);
            Assert.Contains("CMap stream was not decoded", CMapCodec.LastEncodingError);
            Assert.Single(refused.Decode(new byte[] { 1, 2 }));   // Identity: two bytes, one code
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void AnEmbeddedProgramPastItsCeiling_IsRefusedByTheProgramReader()
    {
        try
        {
            using PdfDocument doc = MinimalDoc();
            var descriptor = new PdfDictionary { [PdfName.FontFile3] = Flated(new byte[5000]) };
            var font = new PdfDictionary { [PdfName.FontDescriptor] = descriptor };
            PdfLimits.MaxFontProgramBytes = 4096;
            var refused = Assert.Throws<PdfFilterException>(() => EmbeddedFontProgram.ReadCff(font, doc));
            Assert.Contains("font program", refused.Message);

            // The control: at the ceiling the bytes are read, and five thousand zeros are no CFF.
            PdfLimits.MaxFontProgramBytes = 5000;
            descriptor[PdfName.FontFile3] = Flated(new byte[5000]);
            Assert.Null(EmbeddedFontProgram.ReadCff(font, doc));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static byte[] CMapText(int padding)
        => Encoding.ASCII.GetBytes("%" + new string(' ', padding) + "\n" + Mapping);

    private static PdfStream Flated(byte[] data)
    {
        var stream = new PdfStream { RawData = FlateEncodeFilter.Encode(data) };
        stream[PdfName.Filter] = PdfName.FlateDecode;
        return stream;
    }

    private static PdfDictionary FontWith(PdfStream toUnicode)
    {
        var font = new PdfDictionary();
        font[PdfName.Type] = PdfName.Font;
        font[PdfName.Subtype] = new PdfName("Type1");
        font[PdfName.BaseFont] = new PdfName("Helvetica");
        font[PdfName.ToUnicode] = toUnicode;
        return font;
    }

    private static PdfDocument MinimalDoc()
    {
        string pdfText =
            "%PDF-1.4\n" +
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
            "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
            "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n" +
            "trailer\n<< /Size 3 /Root 1 0 R >>\n" +
            "startxref\n107\n%%EOF\n";
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(pdfText)));
    }
}
