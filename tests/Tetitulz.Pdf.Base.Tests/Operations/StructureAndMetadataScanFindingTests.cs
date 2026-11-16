// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression tests for the <c>Operations/Structure/</c>, <c>Operations/Metadata/</c> and
/// <c>Operations/Documents/</c> findings of the 2026-08 assembly scan.
/// </summary>
public class StructureAndMetadataScanFindingTests
{
    // ------------------------------------------------------------------
    //  In-memory PDF construction
    // ------------------------------------------------------------------

    private static byte[] Latin1(string s) => Encoding.GetEncoding("iso-8859-1").GetBytes(s);

    private static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";

    private static string StreamObj(int n, string dictExtras, string content)
        => $"{n} 0 obj\n<< /Length {Latin1(content).Length}{dictExtras} >>\nstream\n{content}\nendstream\nendobj\n";

    private static byte[] Assemble(IReadOnlyList<string> bodies)
    {
        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);

        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    private static byte[] OnePage(string catalogBody, params string[] extra)
    {
        var bodies = new List<string>
        {
            Obj(1, catalogBody),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
        };
        bodies.AddRange(extra);
        return Assemble(bodies);
    }

    // ==================================================================
    //  PdfNamedDestinations — the legacy /Dests dictionary
    // ==================================================================

    /// <summary>
    /// ListDestinations reads the PDF 1.1 catalog <c>/Dests</c> dictionary; RemoveDestination read
    /// only the name tree, took its "no /Names" early exit, and returned <b>false</b> for a
    /// destination the same class had just listed — leaving it in the file.
    /// </summary>
    [Fact]
    public void RemoveDestination_FindsANameHeldOnlyByTheLegacyDestsDictionary()
    {
        byte[] pdf = OnePage(
            "<< /Type /Catalog /Pages 2 0 R /Dests << /Intro [3 0 R /XYZ null null null] >> >>");

        // The premise: the destination really is listed.
        Assert.Contains(PdfNamedDestinations.ListDestinations(new MemoryStream(pdf)),
            d => d.Name == "Intro");

        using var outMs = new MemoryStream();
        bool removed = PdfNamedDestinations.RemoveDestination(new MemoryStream(pdf), outMs, "Intro");

        Assert.True(removed);
        Assert.DoesNotContain(PdfNamedDestinations.ListDestinations(new MemoryStream(outMs.ToArray())),
            d => d.Name == "Intro");
    }

    /// <summary>A name in neither store is still reported as not found, and the file is untouched.</summary>
    [Fact]
    public void RemoveDestination_UnknownName_StillReportsNotFound()
    {
        byte[] pdf = OnePage(
            "<< /Type /Catalog /Pages 2 0 R /Dests << /Intro [3 0 R /XYZ null null null] >> >>");

        using var outMs = new MemoryStream();
        bool removed = PdfNamedDestinations.RemoveDestination(new MemoryStream(pdf), outMs, "Missing");

        Assert.False(removed);
        Assert.Contains(PdfNamedDestinations.ListDestinations(new MemoryStream(outMs.ToArray())),
            d => d.Name == "Intro");
    }

    /// <summary>
    /// "Replaces any existing entry with same name" has to mean both stores: writing the name tree
    /// while leaving the legacy entry produced two live destinations under one name.
    /// </summary>
    [Fact]
    public void SetDestination_DoesNotLeaveADuplicateInTheLegacyDictionary()
    {
        byte[] pdf = OnePage(
            "<< /Type /Catalog /Pages 2 0 R /Dests << /Intro [3 0 R /XYZ null null null] >> >>");

        using var outMs = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), outMs, "Intro", 0, 10, 20, 1);

        var listed = PdfNamedDestinations.ListDestinations(new MemoryStream(outMs.ToArray()));
        Assert.Equal(1, listed.Count(d => d.Name == "Intro"));
    }

    // ==================================================================
    //  PdfOcrLayerWriter — the empty-words early exit
    // ==================================================================

    /// <summary>
    /// The natural call sequence renders the page first, leaving the stream at its end. Copying
    /// from there produced a zero-byte output file — a destroyed document, reported as success.
    /// </summary>
    [Fact]
    public void AddLayer_WithNoWordsAndANonRewoundStream_StillCopiesTheWholeDocument()
    {
        byte[] pdf = OnePage("<< /Type /Catalog /Pages 2 0 R >>");

        var input = new MemoryStream(pdf);
        input.Position = input.Length;   // what rendering the page leaves behind

        using var outMs = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(input, 0, Array.Empty<PdfOcrWord>(), outMs);

        Assert.Equal(pdf, outMs.ToArray());
    }

    // ==================================================================
    //  PdfStructureTreeReader — indirect /S and /Alt
    // ==================================================================

    /// <summary>
    /// §7.3.10 lets any value except a stream be indirect, and object streams make it common.
    /// PdfDictionary's indexer does not follow a reference, so the reader returned the
    /// "StructElem" fallback type and a null /Alt — while PdfTaggedTextExtractor, reading the same
    /// four keys through ResolveObject, reported the real values.
    /// </summary>
    [Fact]
    public void StructureTreeReader_ResolvesIndirectElementTypeAndAltText()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            Obj(4, "<< /Type /StructTreeRoot /K [5 0 R] >>"),
            // /S and /Alt written as indirect references.
            Obj(5, "<< /Type /StructElem /S 6 0 R /Alt 7 0 R /Pg 3 0 R /K 0 >>"),
            Obj(6, "/Figure"),
            Obj(7, "(A pie chart of quarterly revenue)"),
        };
        byte[] pdf = Assemble(bodies);

        var root = PdfStructureTreeReader.ReadStructureTree(new MemoryStream(pdf));

        var elem = Assert.Single(root.Children);
        Assert.Equal("Figure", elem.ElementType);
        Assert.Equal("A pie chart of quarterly revenue", elem.AltText);
    }

    // ==================================================================
    //  PdfIccEmbedder
    // ==================================================================

    /// <summary>
    /// A minimal ICC profile header: the 132-byte floor, the <c>'acsp'</c> file signature at byte 36
    /// and an RGB data colour space at byte 16 — what the embedder's header gate requires.
    /// </summary>
    private static byte[] FakeIccProfile()
    {
        var icc = new byte[132];
        Encoding.ASCII.GetBytes("RGB ").CopyTo(icc, 16);
        Encoding.ASCII.GetBytes("acsp").CopyTo(icc, 36);
        return icc;
    }

    /// <summary>
    /// A catalog holding <c>/OutputIntents 7 0 R</c> failed the raw <c>is PdfArray</c> test, so the
    /// preservation loop was skipped and the document's existing PDF/X intent was deleted — with
    /// EmbedProfile returning void, nothing said so.
    /// </summary>
    [Fact]
    public void EmbedProfile_PreservesAnIndirectOutputIntentsArray()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /OutputIntents 4 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            Obj(4, "[5 0 R]"),
            Obj(5, "<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (FOGRA39) >>"),
        };
        byte[] pdf = Assemble(bodies);

        using var outMs = new MemoryStream();
        PdfIccEmbedder.EmbedProfile(new MemoryStream(pdf), outMs, FakeIccProfile(),
            subtype: "GTS_PDFA1");

        using var doc = PdfDocument.Open(new MemoryStream(outMs.ToArray()));
        var oi = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OutputIntents]) as PdfArray;
        Assert.NotNull(oi);
        Assert.Equal(2, oi.Count);

        // The original PDF/X intent is one of them — assert on the subtype, not just the count.
        var subtypes = Enumerable.Range(0, oi.Count)
            .Select(i => (doc.ResolveObject(oi[i]) as PdfDictionary))
            .Select(d => (doc.ResolveObject(d[PdfName.S]) as PdfName)?.Value)
            .ToList();
        Assert.Contains("GTS_PDFX", subtypes);
        Assert.Contains("GTS_PDFA1", subtypes);
    }

    /// <summary>
    /// PdfDocument.Load returns before assigning Catalog for an encrypted file, so the embedder
    /// dereferenced null and surfaced a NullReferenceException from a public entry point whose
    /// documented failures are PdfException / ArgumentException.
    /// </summary>
    [Fact]
    public void EmbedProfile_EncryptedDocument_FailsClosedWithAReason()
    {
        byte[] pdf = EncryptedFixture();

        using var outMs = new MemoryStream();
        var ex = Assert.Throws<PdfEncryptedDocumentException>(
            () => PdfIccEmbedder.EmbedProfile(new MemoryStream(pdf), outMs, FakeIccProfile()));

        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The refusal must point somewhere actionable, not merely decline.
        Assert.Contains("Tetitulz.Pdf.Encryption", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document the parser reports as encrypted. The /Encrypt dictionary need only be present and
    /// well-formed enough for PdfDocument.Load to set IsEncrypted and return early.
    /// </summary>
    private static byte[] EncryptedFixture()
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"),
            Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                   "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                   "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>"),
        };

        using var ms = new MemoryStream();
        var head = Latin1("%PDF-1.7\n");
        ms.Write(head, 0, head.Length);
        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add((int)ms.Length);
            var b = Latin1(body);
            ms.Write(b, 0, b.Length);
        }

        int xref = (int)ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1)
          .Append(" /Root 1 0 R /Encrypt 4 0 R /ID [<01020304050607080910111213141516> " +
                  "<01020304050607080910111213141516>] >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        var tail = Latin1(sb.ToString());
        ms.Write(tail, 0, tail.Length);
        return ms.ToArray();
    }

    // ==================================================================
    //  PdfColorSpaceConverter — colour-space-dependent keys
    // ==================================================================

    // A three-component /Decode and a colour-key /Mask cannot survive onto a DeviceGray image
    // (Table 89). The two tests that stood here pinned dropping both, with a warning; the samples
    // now carry /Decode's mapping and the key becomes an explicit mask — see
    // ColorSpaceConverterReachAndKeyTests, which also holds the case that is still dropped (a key
    // that cannot be evaluated) and its warning.

    /// <summary>An image with neither key converts silently — the warning must not cry wolf.</summary>
    [Fact]
    public void ConvertRgbToGrayscale_PlainImage_ReportsNoWarning()
    {
        byte[] pdf = RgbImagePdf("");

        using var outMs = new MemoryStream();
        int converted = PdfColorSpaceConverter.ConvertRgbToGrayscale(new MemoryStream(pdf), outMs);

        Assert.Equal(1, converted);
        Assert.Null(PdfColorSpaceConverter.LastConversionWarning);
    }

    /// <summary>A 2x2 unfiltered DeviceRGB image XObject as object 4, with caller-supplied extras.</summary>
    private static byte[] RgbImagePdf(string extraImageKeys)
    {
        string samples = new string('\x40', 2 * 2 * 3);
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Resources << /XObject << /Im0 4 0 R >> >> >>"),
            StreamObj(4,
                " /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 " +
                "/ColorSpace /DeviceRGB " + extraImageKeys,
                samples),
        };
        return Assemble(bodies);
    }

    // ==================================================================
    //  PdfDocumentOpener — the un-rewound buffer
    // ==================================================================

    /// <summary>
    /// A caller that sniffs the first eight bytes for "%PDF-1." and then hands the same stream over
    /// got a parse exception for a perfectly healthy file, with LastRepairOutcome null so nothing
    /// pointed at the real cause.
    /// </summary>
    [Fact]
    public void OpenWithRepair_RewindsAStreamTheCallerAlreadyRead()
    {
        byte[] pdf = OnePage("<< /Type /Catalog /Pages 2 0 R >>");

        var input = new MemoryStream(pdf);
        var sniff = new byte[8];
        input.Read(sniff, 0, sniff.Length);          // the caller's header check
        Assert.Equal("%PDF-1.", Encoding.ASCII.GetString(sniff, 0, 7));

        using var doc = PdfDocumentOpener.OpenWithRepair(input);

        Assert.Single(doc.Pages);
        // Healthy input is returned untouched — no repair was needed or reported.
        Assert.Null(PdfDocumentOpener.LastRepairOutcome);
    }
}
