// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Round-trip integrity tests for <see cref="DocumentRevisionWriter"/> and for every
/// public editing entry point that writes through it.
///
/// <para>
/// Each defect below is invisible to a parse-only assertion, so the assertions here are
/// deliberately specific rather than "the output opens":
/// </para>
/// <list type="bullet">
///   <item>a <see cref="PdfStream"/>-valued object serialised as the literal text <c>null</c> —
///     the file still parses, it just loses the stream;</item>
///   <item>generation numbers hardcoded to 0 in both the object header and the xref entry, so a
///     strict reader drops the revision of any gen &gt; 0 object;</item>
///   <item><c>/ID</c> omitted, which PDF/UA-1 and PDF/A both require;</item>
///   <item>strings encoded through code page 28591 (Latin-1), turning every Hebrew and CJK
///     <c>/TU</c> tooltip, <c>/Alt</c> text and <c>/Title</c> into <c>?</c>.</item>
/// </list>
///
/// <para>Every document is built in memory (CLAUDE.md: no test reads a PDF from disk).</para>
/// </summary>
public class IncrementalWriterTests
{
    // ===============================================================
    //  In-memory PDF construction
    // ===============================================================

    /// <summary>One indirect object of a hand-built classic-xref document.</summary>
    private readonly struct ObjectSpec
    {
        internal ObjectSpec(int number, int generation, string body)
        {
            Number = number;
            Generation = generation;
            Body = body;
        }

        internal int Number { get; }
        internal int Generation { get; }
        internal string Body { get; }
    }

    private static ObjectSpec Obj(int number, string body) => new ObjectSpec(number, 0, body);

    /// <summary>
    /// Assembles a classic-xref PDF. Object numbers must be contiguous from 1 so a single xref
    /// subsection covers them; everything is ASCII so <c>StringBuilder.Length</c> is a byte offset.
    /// </summary>
    private static byte[] BuildClassicPdf(IReadOnlyList<ObjectSpec> objects, string trailerExtras)
    {
        var sb = new StringBuilder(64 * objects.Count + 256);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(objects[i].Number.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(objects[i].Generation.ToString(CultureInfo.InvariantCulture))
              .Append(" obj\n").Append(objects[i].Body).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        for (int i = 0; i < objects.Count; i++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} {1:D5} n \n",
                offsets[i], objects[i].Generation);

        sb.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R ").Append(trailerExtras ?? string.Empty).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (Original body text) Tj ET\n";

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) +
           " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// Objects 1–5: catalog, page tree, one content-bearing page, its content stream and a font.
    /// <paramref name="catalogExtras"/> is spliced into the catalog dictionary.
    /// </summary>
    private static List<ObjectSpec> CorePrefix(string catalogExtras = "")
    {
        return new List<ObjectSpec>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R " + catalogExtras + " >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Obj(4, StreamObject(PageContent)),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
    }

    /// <summary>Classic PDF with an /Info dictionary (object 6) and no /ID in the trailer.</summary>
    private static byte[] ClassicPdfWithoutId()
    {
        var objects = CorePrefix();
        objects.Add(Obj(6, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 0 R");
    }

    /// <summary>The two halves of the /ID carried by <see cref="ClassicPdfWithId"/>.</summary>
    private const string IdFirstHalf = "0123456789ABCDEF0123456789ABCDEF";
    private const string IdSecondHalf = "FEDCBA9876543210FEDCBA9876543210";

    /// <summary>Classic PDF that already carries a well-formed two-part /ID.</summary>
    private static byte[] ClassicPdfWithId()
    {
        var objects = CorePrefix();
        objects.Add(Obj(6, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 0 R /ID [<" + IdFirstHalf + "> <" + IdSecondHalf + ">]");
    }

    /// <summary>Classic PDF with no /Info entry in the trailer at all.</summary>
    private static byte[] ClassicPdfWithoutInfo()
        => BuildClassicPdf(CorePrefix(), string.Empty);

    /// <summary>
    /// Classic PDF whose /Info dictionary lives at <b>generation 3</b> — both the object header
    /// and the xref entry say so, and the trailer references it as <c>6 3 R</c>.
    /// </summary>
    private static byte[] ClassicPdfWithGeneration3Info()
    {
        var objects = CorePrefix();
        objects.Add(new ObjectSpec(6, 3, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 3 R");
    }

    /// <summary>Tagged PDF whose single Figure (object 8) is a real indirect object with no /Alt.</summary>
    private static byte[] TaggedPdfWithFigure()
    {
        var objects = CorePrefix("/Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R");
        objects.Add(Obj(6, "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1 >>"));
        objects.Add(Obj(7, "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R] >>"));
        objects.Add(Obj(8, "<< /Type /StructElem /S /Figure /P 7 0 R /Pg 3 0 R /K [0] >>"));
        objects.Add(Obj(9, "<< /Nums [0 [8 0 R]] >>"));
        return BuildClassicPdf(objects, string.Empty);
    }

    /// <summary>
    /// AcroForm PDF with one text field. <paramref name="hexPartialName"/>, when supplied,
    /// is written as a UTF-16BE hex string so the field's /T itself is non-Latin-1.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so the encrypted-route tests can reach a document that really has a form.
    /// Testing a tooltip guard against a document with no <c>/AcroForm</c> is vacuous — the plan
    /// returns early at the form check and the guard never runs, which mutation demonstrated.
    /// </remarks>
    internal static byte[] AcroFormPdf(string hexPartialName = null)
    {
        string name = hexPartialName != null ? "<" + hexPartialName + ">" : "(FullName)";
        var objects = CorePrefix("/AcroForm 6 0 R");
        objects.Add(Obj(6, "<< /Fields [7 0 R] >>"));
        objects.Add(Obj(7, "<< /FT /Tx /T " + name + " /Rect [72 600 300 620] >>"));
        return BuildClassicPdf(objects, string.Empty);
    }

    /// <summary>
    /// A PDF whose cross-reference section is a <b>cross-reference stream</b> (object 5), not a
    /// classic table. The stream is uncompressed with <c>/W [1 2 2]</c> so it can be built by
    /// hand and read back without a filter round-trip.
    /// </summary>
    private static byte[] XrefStreamPdf()
    {
        var ms = new MemoryStream();
        void Ascii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Ascii("%PDF-1.5\n");

        var offsets = new int[6];
        string[] bodies =
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Title (Xref Stream Original) >>",
        };

        for (int n = 1; n <= 4; n++)
        {
            offsets[n] = (int)ms.Position;
            Ascii(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + bodies[n] + "\nendobj\n");
        }

        // The xref stream is itself an indirect object and needs an entry for itself, so its
        // offset must be known before its payload is built.
        offsets[5] = (int)ms.Position;

        var data = new byte[6 * 5];   // 6 entries, /W [1 2 2] => 5 bytes each
        void Entry(int i, byte type, int offset, int generation)
        {
            int p = i * 5;
            data[p] = type;
            data[p + 1] = (byte)((offset >> 8) & 0xFF);
            data[p + 2] = (byte)(offset & 0xFF);
            data[p + 3] = (byte)((generation >> 8) & 0xFF);
            data[p + 4] = (byte)(generation & 0xFF);
        }

        Entry(0, 0, 0, 65535);                                  // the free-list head
        for (int n = 1; n <= 5; n++) Entry(n, 1, offsets[n], 0);

        Ascii("5 0 obj\n<< /Type /XRef /Size 6 /W [1 2 2] /Root 1 0 R /Info 4 0 R /Length " +
              data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        ms.Write(data, 0, data.Length);
        Ascii("\nendstream\nendobj\n");
        Ascii("startxref\n" + offsets[5].ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return ms.ToArray();
    }

    // ===============================================================
    //  Helpers
    // ===============================================================

    /// <summary>
    /// Opens <paramref name="original"/>, lets <paramref name="edit"/> build the object maps and
    /// appends exactly one revision through the writer under test.
    /// </summary>
    private static byte[] AppendRevision(
        byte[] original,
        Action<PdfDocument, PdfObjectIndex, Dictionary<int, PdfObject>, Dictionary<int, PdfObject>> edit,
        int newTrailerInfoObjectNumber = -1)
    {
        using var doc = PdfDocument.Open(new MemoryStream(original));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        var modified = new Dictionary<int, PdfObject>();
        var created = new Dictionary<int, PdfObject>();
        edit(doc, index, modified, created);

        using var output = new MemoryStream();
        DocumentRevisionWriter.Write(
            output, original, doc, index, modified, created, newTrailerInfoObjectNumber);
        return output.ToArray();
    }

    private static PdfDictionary Clone(PdfDictionary source)
    {
        var clone = new PdfDictionary();
        if (source != null)
            foreach (KeyValuePair<PdfName, PdfObject> entry in source)
                clone[entry.Key] = entry.Value;
        return clone;
    }

    /// <summary>Asserts <paramref name="original"/> is a byte-for-byte prefix of <paramref name="produced"/>.</summary>
    private static void AssertStrictAppend(byte[] original, byte[] produced)
    {
        Assert.True(produced.Length > original.Length,
            "an applied edit must append a revision, not rewrite the file (original " +
            original.Length + " bytes, produced " + produced.Length + ")");

        for (int i = 0; i < original.Length; i++)
            Assert.True(original[i] == produced[i],
                "byte " + i + " of the original was rewritten (0x" + original[i].ToString("X2") +
                " became 0x" + produced[i].ToString("X2") + "); an incremental update must leave " +
                "every earlier revision intact");
    }

    /// <summary>The appended revision only, decoded byte-for-byte (Latin-1 never fails).</summary>
    private static string AppendedText(byte[] original, byte[] produced)
        => Encoding.Latin1.GetString(produced, original.Length, produced.Length - original.Length);

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    private static string TitleOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var info = doc.ResolveObject(doc.Trailer?.Get(PdfName.Info)) as PdfDictionary;
        return (doc.ResolveObject(info?.Get(new PdfName("Title"))) as PdfString)?.GetText();
    }

    // ===============================================================
    //  Argument guards
    // ===============================================================

    [Fact]
    public void Write_NullOutput_Throws()
    {
        byte[] original = ClassicPdfWithoutId();
        using var doc = PdfDocument.Open(new MemoryStream(original));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        Assert.Throws<ArgumentNullException>(() => DocumentRevisionWriter.Write(
            null, original, doc, index,
            new Dictionary<int, PdfObject>(), new Dictionary<int, PdfObject>()));
    }

    [Fact]
    public void Write_NullOriginalBytes_Throws()
    {
        byte[] original = ClassicPdfWithoutId();
        using var doc = PdfDocument.Open(new MemoryStream(original));
        PdfObjectIndex index = PdfObjectIndex.Build(doc);

        Assert.Throws<ArgumentNullException>(() => DocumentRevisionWriter.Write(
            new MemoryStream(), null, doc, index,
            new Dictionary<int, PdfObject>(), new Dictionary<int, PdfObject>()));
    }

    // ===============================================================
    //  The revised object must actually be visible through the update
    // ===============================================================

    [Fact]
    public void Write_ModifiedCatalog_IsVisibleThroughTheNewRevision()
    {
        byte[] original = ClassicPdfWithoutId();

        // Guard against a vacuous pass: the value must not already be there.
        using (var before = PdfDocument.Open(new MemoryStream(original)))
            Assert.Null(before.Catalog.Dictionary.Get(new PdfName("Lang")));

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            PdfDictionary catalog = doc.Catalog.Dictionary;
            PdfDictionary revised = Clone(catalog);
            revised[new PdfName("Lang")] = new PdfString("fr-CA");
            modified[index.GetObjectNumber(catalog)] = revised;
        });

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var lang = after.ResolveObject(after.Catalog.Dictionary.Get(new PdfName("Lang"))) as PdfString;

        Assert.NotNull(lang);
        Assert.Equal("fr-CA", lang.GetText());

        // …and the objects the revision did not touch must still resolve through /Prev.
        Assert.Single(after.Pages);
    }

    [Fact]
    public void Write_ModifiedObject_LeavesTheOriginalBytesUntouched()
    {
        byte[] original = ClassicPdfWithoutId();

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Revised Title");
            modified[index.GetObjectNumber(info)] = revised;
        });

        AssertStrictAppend(original, produced);
        Assert.Equal("Revised Title", TitleOf(produced));
    }

    [Fact]
    public void Write_NoModifiedAndNoNewObjects_ProducesAByteIdenticalPassthrough()
    {
        byte[] original = ClassicPdfWithoutId();

        byte[] produced = AppendRevision(original, (doc, index, modified, created) => { });

        // No revision at all: a caller that hands over unconditionally must not pay for an
        // empty one, and must not see its document grow.
        Assert.Equal(original.Length, produced.Length);
        Assert.Equal(original, produced);
    }

    [Fact]
    public void Write_NewObject_IsReachableAsTheTrailerInfoDictionary()
    {
        byte[] original = ClassicPdfWithoutInfo();

        using (var before = PdfDocument.Open(new MemoryStream(original)))
            Assert.Null(before.Trailer.Get(PdfName.Info));

        // The number goes into the appended trailer, so the caller has to know it before the
        // revision is built. The allocator is a pure function of the document, so it can be
        // asked ahead of time and the answer cross-checked during the edit.
        int expectedNumber;
        using (var probe = PdfDocument.Open(new MemoryStream(original)))
            expectedNumber = PdfObjectIndex.Build(probe).AllocateObjectNumber();

        Assert.True(expectedNumber > 0);

        byte[] produced = AppendRevision(
            original,
            (doc, index, modified, created) =>
            {
                int number = index.AllocateObjectNumber();
                Assert.Equal(expectedNumber, number);

                var info = new PdfDictionary();
                info[new PdfName("Title")] = new PdfString("Created By The Update");
                created[number] = info;
            },
            newTrailerInfoObjectNumber: expectedNumber);

        AssertStrictAppend(original, produced);
        Assert.Equal("Created By The Update", TitleOf(produced));

        // The new object must be the one the trailer names, not merely present in the file.
        using var after = PdfDocument.Open(new MemoryStream(produced));
        var infoRef = after.Trailer.Get(PdfName.Info) as PdfReference;
        Assert.NotNull(infoRef);
        Assert.Equal(expectedNumber, infoRef.ReferenceObjectNumber);
    }

    // ===============================================================
    //  /ID
    // ===============================================================

    [Fact]
    public void Write_DocumentWithoutId_SynthesisesTwoSixteenByteHalves()
    {
        byte[] original = ClassicPdfWithoutId();

        using (var before = PdfDocument.Open(new MemoryStream(original)))
            Assert.Null(before.Trailer.Get(PdfName.ID));

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Needs An Id");
            modified[index.GetObjectNumber(info)] = revised;
        });

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var id = after.ResolveObject(after.Trailer.Get(PdfName.ID)) as PdfArray;

        Assert.NotNull(id);
        Assert.Equal(2, id.Count);

        var first = id[0] as PdfString;
        var second = id[1] as PdfString;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(16, first.RawBytes.Length);
        Assert.Equal(16, second.RawBytes.Length);
        Assert.NotEqual(first.RawBytes, second.RawBytes);
    }

    [Fact]
    public void Write_SynthesisedId_IsDeterministicAcrossTwoIdenticalRuns()
    {
        byte[] original = ClassicPdfWithoutId();

        // The revised object is built from scratch (not cloned) so the two runs cannot differ
        // for any reason other than the writer itself: a Random or a timestamp in the /ID
        // synthesiser would show up here and nowhere else.
        void Edit(PdfDocument doc, PdfObjectIndex index,
                  Dictionary<int, PdfObject> modified, Dictionary<int, PdfObject> created)
        {
            var info = new PdfDictionary();
            info[new PdfName("Title")] = new PdfString("Deterministic");
            modified[6] = info;
        }

        byte[] first = AppendRevision(original, Edit);
        byte[] second = AppendRevision(original, Edit);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Write_ExistingId_IsCarriedForwardUnchanged()
    {
        byte[] original = ClassicPdfWithId();

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Keeps Its Id");
            modified[index.GetObjectNumber(info)] = revised;
        });

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var id = after.ResolveObject(after.Trailer.Get(PdfName.ID)) as PdfArray;

        Assert.NotNull(id);
        Assert.Equal(2, id.Count);

        var first = id[0] as PdfString;
        var second = id[1] as PdfString;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(IdFirstHalf, ToHex(first.RawBytes));
        Assert.Equal(IdSecondHalf, ToHex(second.RawBytes));
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // ===============================================================
    //  Generation numbers
    // ===============================================================

    [Fact]
    public void Write_Generation3Object_UsesThatGenerationInBothHeaderAndXrefEntry()
    {
        byte[] original = ClassicPdfWithGeneration3Info();

        // Guard against a vacuous pass: the fixture must really carry generation 3.
        using (var before = PdfDocument.Open(new MemoryStream(original)))
        {
            PdfObjectIndex index = PdfObjectIndex.Build(before);
            Assert.Equal(3, index.GetGeneration(6));
        }

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            Assert.NotNull(info);
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Generation Three Revised");
            modified[6] = revised;
        });

        string appended = AppendedText(original, produced);

        Assert.Contains("6 3 obj", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("6 0 obj", appended, StringComparison.Ordinal);

        // The xref entry's 5-digit generation field must agree with the object header, or a
        // strict reader treats the entry as stale and silently ignores the whole update.
        Assert.Matches(new Regex(@"\n\d{10} 00003 n "), appended);

        Assert.Equal("Generation Three Revised", TitleOf(produced));
    }

    // ===============================================================
    //  Stream-valued objects
    // ===============================================================

    [Fact]
    public void Write_PdfStreamValuedObject_KeepsItsDataInsteadOfSerialisingNull()
    {
        byte[] original = ClassicPdfWithoutId();
        const string NewContent = "BT /F1 12 Tf 72 700 Td (Rewritten body text) Tj ET\n";

        byte[] produced = AppendRevision(original, (doc, index, modified, created) =>
        {
            var stream = new PdfStream();
            stream.RawData = Encoding.ASCII.GetBytes(NewContent);
            modified[4] = stream;
        });

        string appended = AppendedText(original, produced);

        // The output of a stream falling through PdfWriter's type ladder: the literal token
        // `null`, so the file still parses but the page is blank.
        Assert.DoesNotMatch(new Regex(@"4\s+0\s+obj\s+null"), appended);
        Assert.Contains("endstream", appended, StringComparison.Ordinal);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var contents = after.ResolveObject(
            after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;

        Assert.NotNull(contents);
        Assert.Equal(NewContent, Encoding.ASCII.GetString(contents.GetDecodedData()));

        // /Length must describe the data that was actually written, not the data it replaced.
        Assert.Equal(NewContent.Length, contents.GetInteger(PdfName.Length) ?? -1);
    }

    // ===============================================================
    //  Cross-reference streams
    // ===============================================================

    [Fact]
    public void XrefStreamPdf_IsReadableBeforeAnyEdit()
    {
        // Guards every assertion below: if the fixture were not really an xref-stream document,
        // the "appended revision is an xref stream" test would be testing nothing.
        using var doc = PdfDocument.Open(new MemoryStream(XrefStreamPdf()));
        Assert.Single(doc.Pages);
        Assert.Equal("Xref Stream Original", TitleOf(XrefStreamPdf()));
    }

    [Fact]
    public void Write_OverAnXrefStreamDocument_AppendsAnXrefStreamThatResolves()
    {
        byte[] original = XrefStreamPdf();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, "Xref Stream Revised"));

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        string appended = AppendedText(original, produced);

        // Appending a classic xref/trailer section to an xref-stream document produces a hybrid
        // file that strict validators reject and xref-stream-only readers cannot follow, so the
        // update must be written in the same form as the section it supersedes. The dictionary
        // is never compressed, so this token is a deterministic marker of that decision.
        Assert.Contains("/Type /XRef", appended, StringComparison.Ordinal);

        // And the appended revision must be readable: the new value resolves, and so do the
        // objects that only exist in the original revision.
        Assert.Equal("Xref Stream Revised", TitleOf(produced));

        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(after.Pages);
        Assert.NotNull(after.Catalog?.Dictionary);
    }

    // ===============================================================
    //  Stacked revisions
    // ===============================================================

    [Fact]
    public void TwoEditsInSequence_BothSurviveAndThePrevChainResolves()
    {
        byte[] original = ClassicPdfWithoutId();

        byte[] first;
        using (var input = new MemoryStream(original))
        using (var output = new MemoryStream())
        {
            Assert.True(PdfAccessibility.SetDocumentTitle(input, output, "First Revision Title"));
            first = output.ToArray();
        }

        byte[] second;
        using (var input = new MemoryStream(first))
        using (var output = new MemoryStream())
        {
            Assert.True(PdfAccessibility.SetDocumentLanguage(input, output, "he-IL"));
            second = output.ToArray();
        }

        // Each revision is appended to the whole of the previous document.
        AssertStrictAppend(original, first);
        AssertStrictAppend(first, second);

        using var after = PdfDocument.Open(new MemoryStream(second));

        var title = after.ResolveObject(
            (after.ResolveObject(after.Trailer.Get(PdfName.Info)) as PdfDictionary)
                ?.Get(new PdfName("Title"))) as PdfString;
        Assert.NotNull(title);
        Assert.Equal("First Revision Title", title.GetText());

        var lang = after.ResolveObject(after.Catalog.Dictionary.Get(new PdfName("Lang"))) as PdfString;
        Assert.NotNull(lang);
        Assert.Equal("he-IL", lang.GetText());

        // Objects that were never revised still resolve, which only works if both /Prev links
        // point at the section they supersede.
        Assert.Single(after.Pages);
        var contents = after.ResolveObject(after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;
        Assert.NotNull(contents);
        Assert.Equal(PageContent, Encoding.ASCII.GetString(contents.GetDecodedData()));

        // Exactly one /Prev per appended revision; the original trailer had none.
        Assert.Equal(0, CountOccurrences(Encoding.Latin1.GetString(original), "/Prev"));
        Assert.Equal(2, CountOccurrences(Encoding.Latin1.GetString(second), "/Prev"));
    }

    // ===============================================================
    //  Non-Latin-1 text must survive every editing entry point
    // ===============================================================

    private const string HebrewTitle = "דוח שנתי";
    private const string HebrewAlt = "תרשים מכירות";
    private const string CjkTitle = "年次報告書";

    [Theory]
    [InlineData(HebrewTitle)]
    [InlineData(CjkTitle)]
    public void SetDocumentTitle_NonLatin1_RoundTrips(string title)
    {
        byte[] original = ClassicPdfWithoutId();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, title));

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        // A writer encoding through code page 28591 would turn every character above U+00FF
        // into '?'. Equality — not "is non-empty" — is the assertion that catches it.
        Assert.Equal(title, TitleOf(produced));
    }

    [Fact]
    public void SetFigureAltText_Hebrew_RoundTrips()
    {
        byte[] original = TaggedPdfWithFigure();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        SetAltTextResult result = PdfStructureTreeEditor.SetFigureAltText(input, output, HebrewAlt);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Failed);

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var figure = after.GetObject(8) as PdfDictionary;
        Assert.NotNull(figure);

        var alt = after.ResolveObject(figure.Get(new PdfName("Alt"))) as PdfString;
        Assert.NotNull(alt);
        Assert.Equal(HebrewAlt, alt.GetText());
    }

    [Fact]
    public void SetFieldTooltip_Cjk_RoundTrips()
    {
        byte[] original = AcroFormPdf();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfStructureTreeEditor.SetFieldTooltip(input, output, "FullName", CjkTitle));

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var field = after.GetObject(7) as PdfDictionary;
        Assert.NotNull(field);

        var tooltip = after.ResolveObject(field.Get(new PdfName("TU"))) as PdfString;
        Assert.NotNull(tooltip);
        Assert.Equal(CjkTitle, tooltip.GetText());
    }

    /// <summary>
    /// <c>PdfAccessibilityFixer</c> is the entry point that actually shipped the code-page-28591
    /// bug, so it gets the strictest form of this test: the tooltip it generates is entirely
    /// non-Latin-1, from a field whose /T is itself a UTF-16BE hex string.
    /// </summary>
    [Fact]
    public void PdfAccessibilityFixer_HebrewFieldName_ProducesAHebrewTooltip()
    {
        // U+05E9 U+05DD ("shin", "final mem") as UTF-16BE behind the mandatory U+FEFF BOM.
        byte[] original = AcroFormPdf(hexPartialName: "FEFF05E905DD");

        // Read the name back out of the fixture rather than restating it as a source literal:
        // the assertion below then compares the tooltip against exactly what the document says,
        // and cannot be defeated by an editor reordering a bidirectional string literal.
        string fieldName;
        using (var check = PdfDocument.Open(new MemoryStream(original)))
        {
            var field = check.GetObject(7) as PdfDictionary;
            Assert.NotNull(field);

            fieldName = (field.Get(PdfName.T) as PdfString)?.GetText();
            Assert.NotNull(fieldName);
            Assert.Equal(2, fieldName.Length);
            Assert.Equal('ש', fieldName[0]);
            Assert.Equal('ם', fieldName[1]);

            Assert.Null(field.Get(new PdfName("TU")));
        }

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        AccessibilityFixResult result = PdfAccessibilityFixer.Fix(
            input, output,
            new AccessibilityFixOptions { DefaultTooltipTemplate = "{name}" });

        Assert.NotNull(result);

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var revised = after.GetObject(7) as PdfDictionary;
        Assert.NotNull(revised);

        var tooltip = after.ResolveObject(revised.Get(new PdfName("TU"))) as PdfString;
        Assert.NotNull(tooltip);
        Assert.Equal(fieldName, tooltip.GetText());
    }

    /// <summary>
    /// The mixed case: a Hebrew template around an ASCII field name. UTF-16BE encoding of ASCII
    /// characters produces NUL high bytes inside a literal string, which is exactly where a
    /// naive encoder truncates.
    /// </summary>
    [Fact]
    public void PdfAccessibilityFixer_HebrewTemplateAroundAsciiName_RoundTripsWholeString()
    {
        byte[] original = AcroFormPdf();

        // Expected is derived from the template, never restated: the two literals would have to
        // be byte-identical, and a bidirectional string literal is the one place that is not
        // safe to assume.
        const string Template = "שדה: {name}";
        string expected = Template.Replace("{name}", "FullName");

        // Guard: the template really is outside Latin-1, which a Latin-1 writer would destroy.
        bool hasNonLatin1 = false;
        foreach (char c in Template) if (c > 0x00FF) { hasNonLatin1 = true; break; }
        Assert.True(hasNonLatin1, "the fixture must contain characters above U+00FF");

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        PdfAccessibilityFixer.Fix(input, output,
            new AccessibilityFixOptions { DefaultTooltipTemplate = Template });

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var revised = after.GetObject(7) as PdfDictionary;
        Assert.NotNull(revised);

        var tooltip = after.ResolveObject(revised.Get(new PdfName("TU"))) as PdfString;
        Assert.NotNull(tooltip);
        Assert.Equal(expected, tooltip.GetText());
    }

    // ===============================================================
    //  Every editing entry point produces a well-formed appended revision
    // ===============================================================

    [Fact]
    public void SetActualText_Hebrew_RoundTripsAndAppends()
    {
        byte[] original = TaggedPdfWithFigure();
        const string ActualText = "פסקה ראשונה";

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        int count = PdfStructureTreeEditor.SetActualText(
            input, output, "Figure", (index, existing) => ActualText);

        Assert.Equal(1, count);

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var figure = after.GetObject(8) as PdfDictionary;
        var actual = after.ResolveObject(figure?.Get(new PdfName("ActualText"))) as PdfString;

        Assert.NotNull(actual);
        Assert.Equal(ActualText, actual.GetText());
    }

    [Fact]
    public void SetDocumentLanguage_OnADocumentWithNoInfo_StillAppendsAReadableRevision()
    {
        byte[] original = ClassicPdfWithoutInfo();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentLanguage(input, output, "he-IL"));

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var lang = after.ResolveObject(after.Catalog.Dictionary.Get(new PdfName("Lang"))) as PdfString;
        Assert.NotNull(lang);
        Assert.Equal("he-IL", lang.GetText());

        // Even a document that had no /ID and no /Info gets a conformant trailer.
        Assert.NotNull(after.ResolveObject(after.Trailer.Get(PdfName.ID)) as PdfArray);
    }

    [Fact]
    public void SetDocumentTitle_OnADocumentWithNoInfo_MintsOneAndWiresItIntoTheTrailer()
    {
        byte[] original = ClassicPdfWithoutInfo();

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfAccessibility.SetDocumentTitle(input, output, "Minted Info"));

        byte[] produced = output.ToArray();
        AssertStrictAppend(original, produced);

        // The freshly allocated object number must be referenced from the appended trailer, or
        // the new /Info is unreachable dead weight.
        Assert.Equal("Minted Info", TitleOf(produced));
    }
}
