// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Tests.Constants;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// The recovery scan reads the whole file in windows of <see cref="PdfLimits.MaxXrefRecoveryScanBytes"/>.
/// It used to read the first window alone, so a newer copy of an object appended past it lost to an
/// older one inside it, the trailer at the end of such a file was never read, and an object only a
/// skipped section named was not found past the window either (the review of batch 131).
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class RecoveryScanWindowTests
{
    private static readonly PdfName Marker = new PdfName("Marker");
    private const int Window = 16 * 1024;

    /// <summary>
    /// A file of three windows whose <c>startxref</c> is broken: object 5's newer copy and the trailer
    /// naming the catalog sit in the last window. The scan reads both — the control under the default
    /// window reads the same file to the same answers.
    /// </summary>
    [Fact]
    public void FullRecovery_ReadsTheNewestCopyAndTheTrailer_PastTheFirstWindow()
    {
        byte[] pdf = BrokenFile(out int newestMarker);
        Assert.True(pdf.Length > 2 * Window, "the fixture does not reach a third window");

        try
        {
            PdfLimits.MaxXrefRecoveryScanBytes = Window;
            AssertRecovered(pdf, newestMarker);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
        AssertRecovered(pdf, newestMarker);
    }

    /// <summary>
    /// The header of object 6 is cut by the first window's end — <c>6 0 obj</c> begins three bytes before
    /// it — and object 6 is read from its header all the same, since the next window begins where the
    /// last header taken did.
    /// </summary>
    [Fact]
    public void FullRecovery_ReadsAHeaderTheWindowBoundaryCutsThrough()
    {
        byte[] pdf = BrokenFile(out _, headerAcross: Window);
        Assert.Equal("6 0 obj", Encoding.ASCII.GetString(pdf, Window - 3, 7));

        try
        {
            PdfLimits.MaxXrefRecoveryScanBytes = Window;
            PdfParser parser = ParserOver(pdf);
            Assert.Equal(6, Assert.IsType<PdfDictionary>(parser.GetObject(6)).GetInteger(Marker));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A valid update whose older section is damaged: the objects only that section named are filled from
    /// their headers — object 7, written past the first window, among them.
    /// </summary>
    [Fact]
    public void GapFill_FindsAnObjectPastTheFirstWindow()
    {
        byte[] pdf = SkippedSectionFile();
        Assert.True(pdf.Length > 2 * Window, "the fixture does not reach a third window");

        try
        {
            PdfLimits.MaxXrefRecoveryScanBytes = Window;
            PdfParser parser = ParserOver(pdf);
            Assert.Contains("was skipped", PdfParser.LastXrefSectionError, StringComparison.Ordinal);
            Assert.Equal(7, Assert.IsType<PdfDictionary>(parser.GetObject(7)).GetInteger(Marker));
            Assert.Equal(1, Assert.IsType<PdfDictionary>(parser.GetObject(1)).GetInteger(Marker));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static void AssertRecovered(byte[] pdf, int newestMarker)
    {
        PdfParser parser = ParserOver(pdf);
        Assert.Equal(newestMarker, Assert.IsType<PdfDictionary>(parser.GetObject(5)).GetInteger(Marker));
        var root = Assert.IsType<PdfDictionary>(parser.ResolveObject(parser.Trailer[PdfName.Root]));
        Assert.Equal(PdfName.Catalog, root.GetName(PdfName.Type));
        Assert.Equal(1, Assert.IsType<PdfDictionary>(parser.GetObject(1)).GetInteger(Marker));
    }

    private static PdfParser ParserOver(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    /// <summary>
    /// Objects 1 to 5 and a padding stream longer than a window, a <c>startxref</c> aimed at nothing,
    /// then — past the second window — a newer object 5 and the only trailer naming <c>/Root</c>. With
    /// <paramref name="headerAcross"/>, object 6's header is placed so that it begins three bytes before
    /// that offset.
    /// </summary>
    private static byte[] BrokenFile(out int newestMarker, int headerAcross = -1)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        void Obj(int n, string body) => sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /Marker 1 >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        Obj(5, "<< /Marker 1 >>");
        if (headerAcross > 0)
        {
            // A comment of the exact length puts "6 0 obj" three bytes before headerAcross.
            int commentLength = headerAcross - 3 - sb.Length - 2;
            sb.Append('%').Append(new string('p', commentLength)).Append('\n');
            Assert.Equal(headerAcross - 3, sb.Length);
            Obj(6, "<< /Marker 6 >>");
        }
        string padding = new string('x', Window + Window / 2);
        Obj(4, $"<< /Length {padding.Length} >>\nstream\n{padding}\nendstream");
        sb.Append("startxref\n999999999\n%%EOF\n");
        // The second revision: past the second window, a newer object 5 and the trailer.
        sb.Append('%').Append(new string('q', Window)).Append('\n');
        newestMarker = 2;
        Obj(5, "<< /Marker 2 >>");
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\nstartxref\n999999999\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Revision 1: objects 1 to 4 (a padding stream longer than a window) under a classic section whose
    /// entries are then damaged; revision 2, past the second window: objects 5 and 7, a valid section
    /// naming object 5 alone, and a trailer whose <c>/Prev</c> leads to the damaged one. Object 7, named
    /// by no section, is filled from its header.
    /// </summary>
    private static byte[] SkippedSectionFile()
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj(1, "<< /Type /Catalog /Pages 2 0 R /Marker 1 >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        string padding = new string('x', Window + Window / 2);
        Obj(4, $"<< /Length {padding.Length} >>\nstream\n{padding}\nendstream");
        int xref1 = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \nDAMAGED ENTRIES\n");
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n").Append(xref1).Append("\n%%EOF\n");

        sb.Append('%').Append(new string('q', Window)).Append('\n');
        Obj(5, "<< /Producer (revision two) >>");
        Obj(7, "<< /Marker 7 >>");
        int xref2 = sb.Length;
        sb.Append("xref\n0 1\n0000000000 65535 f \n5 1\n");
        sb.AppendFormat("{0:D10} 00000 n \n", offsets[5]);
        sb.Append("trailer\n<< /Size 8 /Root 1 0 R /Info 5 0 R /Prev ").Append(xref1)
          .Append(" >>\nstartxref\n").Append(xref2).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
