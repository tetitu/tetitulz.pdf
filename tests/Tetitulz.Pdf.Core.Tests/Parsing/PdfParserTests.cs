// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

public class PdfParserTests
{
    [Fact]
    public void ParseDirectObject_ParsesInteger()
    {
        var parser = CreateParser("42");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfNumber>(obj);
        Assert.Equal(42, ((PdfNumber)obj).IntValue);
    }

    [Fact]
    public void ParseDirectObject_ParsesReal()
    {
        var parser = CreateParser("3.14");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfNumber>(obj);
        Assert.Equal(3.14, ((PdfNumber)obj).DoubleValue, 5);
    }

    [Fact]
    public void ParseDirectObject_ParsesString()
    {
        var parser = CreateParser("(Hello)");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfString>(obj);
        Assert.Equal("Hello", ((PdfString)obj).GetText());
    }

    [Fact]
    public void ParseDirectObject_ParsesHexString()
    {
        var parser = CreateParser("<48656C6C6F>");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfString>(obj);
        Assert.Equal("Hello", ((PdfString)obj).GetText());
    }

    [Fact]
    public void ParseDirectObject_ParsesName()
    {
        var parser = CreateParser("/Type");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfName>(obj);
        Assert.Equal("Type", ((PdfName)obj).Value);
    }

    [Fact]
    public void ParseDirectObject_ParsesBoolean()
    {
        var parser = CreateParser("true");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfBoolean>(obj);
        Assert.True(((PdfBoolean)obj).Value);
    }

    [Fact]
    public void ParseDirectObject_ParsesNull()
    {
        var parser = CreateParser("null");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfNull>(obj);
    }

    [Fact]
    public void ParseDirectObject_ParsesArray()
    {
        var parser = CreateParser("[1 2 3]");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfArray>(obj);
        var arr = (PdfArray)obj;
        Assert.Equal(3, arr.Count);
        Assert.Equal(1, arr.GetInteger(0));
        Assert.Equal(2, arr.GetInteger(1));
        Assert.Equal(3, arr.GetInteger(2));
    }

    [Fact]
    public void ParseDirectObject_ParsesDictionary()
    {
        var parser = CreateParser("<< /Type /Catalog /Pages 2 0 R >>");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfDictionary>(obj);
        var dict = (PdfDictionary)obj;
        Assert.Equal("Catalog", dict.GetName(PdfName.Type)?.Value);
    }

    [Fact]
    public void ParseDirectObject_ParsesIndirectReference()
    {
        var parser = CreateParser("10 0 R");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfReference>(obj);
        var reference = (PdfReference)obj;
        Assert.Equal(10, reference.ReferenceObjectNumber);
        Assert.Equal(0, reference.ReferenceGenerationNumber);
    }

    [Fact]
    public void ParseDirectObject_ParsesNestedDictionary()
    {
        var parser = CreateParser("<< /Resources << /Font << /F1 5 0 R >> >> >>");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfDictionary>(obj);
        var dict = (PdfDictionary)obj;
        var resources = dict.GetDictionary(new PdfName("Resources"));
        Assert.NotNull(resources);
        var font = resources.GetDictionary(PdfName.Font);
        Assert.NotNull(font);
    }

    [Fact]
    public void ParseDirectObject_ParsesMixedArray()
    {
        var parser = CreateParser("[/Name (String) 42 true null 10 0 R]");
        var obj = parser.ParseDirectObject();

        Assert.IsType<PdfArray>(obj);
        var arr = (PdfArray)obj;
        Assert.Equal(6, arr.Count);
        Assert.IsType<PdfName>(arr[0]);
        Assert.IsType<PdfString>(arr[1]);
        Assert.IsType<PdfNumber>(arr[2]);
        Assert.IsType<PdfBoolean>(arr[3]);
        Assert.IsType<PdfNull>(arr[4]);
        Assert.IsType<PdfReference>(arr[5]);
    }

    /// <summary>
    /// GetObject must return PdfNull.Instance (not throw) when a Normal XRef entry
    /// points to garbage bytes that cannot be parsed as a valid indirect object.
    /// Before the fix, ParseIndirectObject() threw PdfParseException and the
    /// exception propagated uncaught to the caller.
    /// </summary>
    [Fact]
    public void GetObject_NormalEntryWithCorruptBody_ReturnsPdfNull()
    {
        byte[] pdf = BuildPdfWithCorruptNormalObject();
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        // Object 4 has a Normal XRef entry pointing at garbage bytes.
        var ex = Record.Exception(() => parser.GetObject(4));
        Assert.Null(ex);
        var obj = parser.GetObject(4);
        Assert.IsType<PdfNull>(obj);
        Assert.NotNull(PdfParser.LastObjectParseError);
    }

    /// <summary>
    /// Builds a minimal PDF-1.4 file where object 4 has a Normal XRef entry that
    /// points to a byte offset containing garbage (not a valid indirect object).
    /// </summary>
    private static byte[] BuildPdfWithCorruptNormalObject()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        // Object 3: valid font (unused but structurally sound)
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        // Object 4: corrupt — points at garbage bytes that are not "4 0 obj"
        int o4 = sb.Length;
        sb.Append("GARBAGE_NOT_A_PDF_OBJECT\n");

        int xref = sb.Length;
        sb.Append("xref\n0 5\n");
        sb.Append("0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4); // Normal entry → garbage
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Regression test: GetObject must return PdfNull.Instance (not throw) when the
    /// requested object is stored inside an ObjStm whose /Filter is unknown.
    /// Before the fix, FilterPipeline passed raw bytes through and PdfParser threw
    /// on the garbage token stream.
    /// </summary>
    [Fact]
    public void GetObject_CompressedInObjStmWithUnknownFilter_ReturnsPdfNull()
    {
        byte[] pdf = BuildPdfWithBrokenObjStm();
        using var ms = new MemoryStream(pdf);
        var parser = new PdfParser(ms);
        parser.ParseCrossReferences();

        // Object 4 is registered as compressed in ObjStm 3 (which has /Filter /XXXDecode).
        // The fix wraps ParseObjectFromStream in a try/catch and returns PdfNull.Instance.
        var ex = Record.Exception(() => parser.GetObject(4));
        Assert.Null(ex);
        var obj = parser.GetObject(4);
        Assert.IsType<PdfNull>(obj);
    }

    /// <summary>
    /// Builds a minimal PDF-1.5 file where object 4 is registered as compressed inside
    /// ObjStm object 3.  ObjStm 3 declares /Filter /XXXDecode so its stream content is
    /// passed through unchanged (raw compressed bytes), which cannot be parsed as PDF.
    /// </summary>
    private static byte[] BuildPdfWithBrokenObjStm()
    {
        // We build the body objects first to know their offsets, then emit the
        // cross-reference stream last (it must appear at startxref).
        //
        //  Obj 1: Catalog
        //  Obj 2: Pages (empty)
        //  Obj 3: ObjStm with /Filter /XXXDecode — garbage stream data
        //  Obj 4: (compressed in ObjStm 3, index 0) — will be PdfNull after fix
        //  Obj 5: XRef stream
        //
        // XRef W=[1 2 1]: 1-byte type, 2-byte field2, 1-byte field3 → 4 bytes/entry.

        var body = new System.Collections.Generic.List<byte>();
        void Append(string s) { foreach (char c in s) body.Add((byte)c); }
        void AppendBytes(byte[] b) { body.AddRange(b); }

        Append("%PDF-1.5\n");

        int o1 = body.Count;
        Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = body.Count;
        Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        // ObjStm 3: N=1 object, First=4, garbage data
        byte[] garbage = new byte[] { 0xFF, 0xFE, 0xAB, 0xCD, 0x12, 0x34 };
        int o3 = body.Count;
        string objStmHeader = $"3 0 obj\n<< /Type /ObjStm /Filter /XXXDecode /N 1 /First 4 /Length {garbage.Length} >>\nstream\n";
        Append(objStmHeader);
        AppendBytes(garbage);
        Append("\nendstream\nendobj\n");

        // XRef stream (obj 5): 5 entries for objects 0-4, plus itself at position 5.
        // We need to patch the offset of obj 5 after we know where it goes.
        // Entries: W=[1 2 1]
        //   Obj 0: free      → 00 00 00 FF
        //   Obj 1: normal    → 01 HI(o1) LO(o1) 00
        //   Obj 2: normal    → 01 HI(o2) LO(o2) 00
        //   Obj 3: normal    → 01 HI(o3) LO(o3) 00
        //   Obj 4: compressed in stream 3, index 0 → 02 00 03 00
        //   Obj 5: normal    → 01 HI(o5) LO(o5) 00  (placeholder, patched below)

        int o5Placeholder = body.Count; // will be patched
        byte[] xrefData = new byte[6 * 4]; // 6 entries × 4 bytes each
        // Entry 0: free
        xrefData[0] = 0; xrefData[1] = 0; xrefData[2] = 0; xrefData[3] = 0xFF;
        // Entry 1: normal at o1
        xrefData[4] = 1; xrefData[5] = (byte)(o1 >> 8); xrefData[6] = (byte)o1; xrefData[7] = 0;
        // Entry 2: normal at o2
        xrefData[8] = 1; xrefData[9] = (byte)(o2 >> 8); xrefData[10] = (byte)o2; xrefData[11] = 0;
        // Entry 3: normal at o3
        xrefData[12] = 1; xrefData[13] = (byte)(o3 >> 8); xrefData[14] = (byte)o3; xrefData[15] = 0;
        // Entry 4: compressed in ObjStm 3, index 0
        xrefData[16] = 2; xrefData[17] = 0; xrefData[18] = 3; xrefData[19] = 0;
        // Entry 5: normal at o5 (placeholder — patched after we know the offset)
        xrefData[20] = 1; xrefData[21] = 0; xrefData[22] = 0; xrefData[23] = 0;

        int o5 = body.Count;
        // Patch entry 5 offset
        xrefData[21] = (byte)(o5 >> 8);
        xrefData[22] = (byte)o5;

        string xrefHeader = $"5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 1] /Root 1 0 R /Length {xrefData.Length} >>\nstream\n";
        Append(xrefHeader);
        AppendBytes(xrefData);
        Append("\nendstream\nendobj\n");
        Append($"startxref\n{o5}\n%%EOF\n");

        return body.ToArray();
    }

    private static PdfParser CreateParser(string input)
    {
        var stream = new MemoryStream(Encoding.ASCII.GetBytes(input));
        return new PdfParser(stream);
    }
}
