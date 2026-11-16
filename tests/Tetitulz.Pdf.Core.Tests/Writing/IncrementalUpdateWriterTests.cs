// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Coverage for <see cref="IncrementalUpdateWriter"/>'s format-aware overload.
///
/// <para>
/// The capability it adds — appending a cross-reference <i>stream</i> when the section being
/// superseded is one — used to live in the Accessibility feature assembly, where the Conformance
/// plugin could not reach it (plugin → feature is a dependency cycle). Handed an xref-stream
/// document, the plugin could only append a classic table and warn: that hybrid is rejected by
/// strict validators and unreadable to a reader that only understands xref streams. These tests
/// pin the behaviour down in Core, where both callers reach it.
/// </para>
///
/// <para>
/// Assertions are deliberately stronger than "the output parses". A revision whose new objects
/// are unreachable still parses — it just silently loses the edit — so every round-trip test
/// resolves the value it wrote and the objects it did <i>not</i> touch.
/// </para>
///
/// <para>Every document is built in memory (CLAUDE.md: no test reads a PDF from disk).</para>
/// </summary>
public class IncrementalUpdateWriterTests
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

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (Original body text) Tj ET\n";

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) +
           " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// Assembles a classic-xref PDF. Object numbers are contiguous from 1 so a single xref
    /// subsection covers them; everything is ASCII so <c>StringBuilder.Length</c> is a byte offset.
    /// </summary>
    /// <param name="startxrefOverride">
    /// When &gt;= 0, written into <c>startxref</c> instead of the real offset — used to build the
    /// "the previous cross-reference offset cannot be trusted" fixture.
    /// </param>
    private static byte[] BuildClassicPdf(
        IReadOnlyList<ObjectSpec> objects, string trailerExtras, long startxrefOverride = -1)
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
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n",
            startxrefOverride >= 0 ? startxrefOverride : xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Objects 1–5: catalog, page tree, one content-bearing page, its content stream, a font.</summary>
    private static List<ObjectSpec> CorePrefix()
    {
        return new List<ObjectSpec>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                   "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"),
            Obj(4, StreamObject(PageContent)),
            Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
    }

    /// <summary>Classic PDF with an /Info dictionary (object 6) and no /ID in the trailer.</summary>
    private static byte[] ClassicPdf(long startxrefOverride = -1)
    {
        var objects = CorePrefix();
        objects.Add(Obj(6, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 0 R", startxrefOverride);
    }

    private const string IdFirstHalf = "0123456789ABCDEF0123456789ABCDEF";
    private const string IdSecondHalf = "FEDCBA9876543210FEDCBA9876543210";

    /// <summary>Classic PDF that already carries a well-formed two-part /ID.</summary>
    private static byte[] ClassicPdfWithId()
    {
        var objects = CorePrefix();
        objects.Add(Obj(6, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 0 R /ID [<" + IdFirstHalf + "> <" + IdSecondHalf + ">]");
    }

    /// <summary>Classic PDF whose /Info dictionary (object 6) lives at <b>generation 3</b>.</summary>
    private static byte[] ClassicPdfWithGeneration3Info()
    {
        var objects = CorePrefix();
        objects.Add(new ObjectSpec(6, 3, "<< /Title (Original Title) >>"));
        return BuildClassicPdf(objects, "/Info 6 3 R");
    }

    /// <summary>
    /// A PDF whose cross-reference section is a <b>cross-reference stream</b> (object 5), not a
    /// classic table. The stream is uncompressed with <c>/W [1 2 2]</c> so it can be built by hand
    /// and read back without a filter round-trip.
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
    //  /ID and encryption
    // ===============================================================

    /// <summary>
    /// An encrypted document without an <c>/ID</c> keeps having none: a revision-4-or-lower file key is
    /// derived from the first identifier, so a synthesised one would be a key the password no longer
    /// derives. An unencrypted document without one gets the deterministic identifier.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnAppendedTrailer_SynthesisesAnIdOnlyForAnUnencryptedDocument(bool encrypted)
    {
        var objects = CorePrefix();
        objects.Add(Obj(6, "<< /Title (Original Title) >>"));
        objects.Add(Obj(7, "<< /Filter /Standard /V 1 /R 2 /Length 40 /P -1 /O <00> /U <00> >>"));
        byte[] original = BuildClassicPdf(objects, encrypted ? "/Info 6 0 R /Encrypt 7 0 R" : "/Info 6 0 R");

        var reports = new List<string>();
        byte[] updated = AppendRevision(original, (doc, objs) =>
        {
            var info = new PdfDictionary();
            info.Set(PdfName.Title, new PdfString("Replaced Title"));
            objs.Add(new IndirectObject(6, 0, info));
        }, o => o.DiagnosticSink = (where, message, ex) => reports.Add(message));

        string appendedTrailer = Encoding.ASCII.GetString(updated, original.Length, updated.Length - original.Length);
        Assert.Equal(!encrypted, appendedTrailer.Contains("/ID", StringComparison.Ordinal));
        Assert.Equal(encrypted, reports.Exists(r => r.Contains("no /ID, so none is written", StringComparison.Ordinal)));
    }

    // ===============================================================
    //  Helpers
    // ===============================================================

    /// <summary>
    /// Opens <paramref name="original"/>, lets <paramref name="edit"/> build the object list and
    /// appends exactly one revision through the format-aware overload.
    /// </summary>
    private static byte[] AppendRevision(
        byte[] original,
        Action<PdfDocument, List<IndirectObject>> edit,
        Action<IncrementalUpdateOptions> configure = null)
    {
        using var doc = PdfDocument.Open(new MemoryStream(original));

        var objects = new List<IndirectObject>();
        edit(doc, objects);

        var options = new IncrementalUpdateOptions { Document = doc };
        configure?.Invoke(options);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, objects, options);
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
                "byte " + i + " of the original was rewritten; an incremental update must leave " +
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

    /// <summary>Replaces object 6's /Title, which the trailer names as /Info.</summary>
    private static void ReviseTitle(PdfDocument doc, List<IndirectObject> objects, string title)
    {
        var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
        PdfDictionary revised = Clone(info);
        revised[new PdfName("Title")] = new PdfString(title);
        objects.Add(new IndirectObject(6, 0, revised));
    }

    // ===============================================================
    //  Argument guards
    // ===============================================================

    [Fact]
    public void WriteUpdate_NullOriginalBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => IncrementalUpdateWriter.WriteUpdate(
            null, new MemoryStream(), new List<IndirectObject>(), new IncrementalUpdateOptions()));
    }

    [Fact]
    public void WriteUpdate_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => IncrementalUpdateWriter.WriteUpdate(
            ClassicPdf(), null, new List<IndirectObject>(), new IncrementalUpdateOptions()));
    }

    [Fact]
    public void WriteUpdate_NoObjects_ProducesAByteIdenticalPassthrough()
    {
        byte[] original = ClassicPdf();

        byte[] produced = AppendRevision(original, (doc, objects) => { });

        // A caller that hands over unconditionally must not pay for an empty revision.
        Assert.Equal(original, produced);
    }

    [Fact]
    public void WriteUpdate_NullOptions_StillAppendsAReadableRevision()
    {
        byte[] original = ClassicPdf();

        var objects = new List<IndirectObject>();
        using (var doc = PdfDocument.Open(new MemoryStream(original)))
            ReviseTitle(doc, objects, "No Options At All");

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, objects, null);
        byte[] produced = output.ToArray();

        AssertStrictAppend(original, produced);
        Assert.Equal("No Options At All", TitleOf(produced));
    }

    // ===============================================================
    //  Classic table in, classic table out
    // ===============================================================

    [Fact]
    public void ClassicOriginal_AppendsAClassicTableAndTrailer()
    {
        byte[] original = ClassicPdf();

        byte[] produced = AppendRevision(original, (doc, objects) =>
            ReviseTitle(doc, objects, "Classic Revised"));

        AssertStrictAppend(original, produced);

        string appended = AppendedText(original, produced);

        Assert.Contains("xref\n", appended, StringComparison.Ordinal);
        Assert.Contains("trailer\n", appended, StringComparison.Ordinal);
        Assert.Contains("startxref\n", appended, StringComparison.Ordinal);

        // Appending an xref *stream* to a classic-table document is the same hybrid, mirrored:
        // the appended section must match the one it supersedes.
        Assert.DoesNotContain("/Type /XRef", appended, StringComparison.Ordinal);

        // Object 0 heads the free list and the section this one chains to already declared it.
        Assert.DoesNotContain("65535 f", appended, StringComparison.Ordinal);

        // The chain has to be followable, and the value has to be the new one.
        Assert.Contains("/Prev", appended, StringComparison.Ordinal);
        Assert.Equal("Classic Revised", TitleOf(produced));

        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(after.Pages);
    }

    [Fact]
    public void ClassicOriginal_SizeCoversEveryObjectNumberWritten()
    {
        byte[] original = ClassicPdf();

        // Object 40 is far beyond the original's /Size 7; a trailer that still said 7 would make
        // a strict reader reject the entry as out of bounds and drop the whole update.
        byte[] produced = AppendRevision(original, (doc, objects) =>
            objects.Add(new IndirectObject(40, 0, new PdfString("far out"))));

        using var after = PdfDocument.Open(new MemoryStream(produced));
        long? size = after.Trailer.GetLong(PdfName.Size);

        Assert.NotNull(size);
        Assert.True(size.Value > 40, "/Size " + size + " must be greater than the highest object number (40)");

        var written = after.GetObject(40) as PdfString;
        Assert.NotNull(written);
        Assert.Equal("far out", written.GetText());
    }

    // ===============================================================
    //  Cross-reference stream in, cross-reference stream out
    // ===============================================================

    [Fact]
    public void XrefStreamPdf_IsReadableBeforeAnyEdit()
    {
        // Guards every assertion below: if the fixture were not really an xref-stream document,
        // the "appended revision is an xref stream" tests would be testing nothing.
        byte[] original = XrefStreamPdf();
        using var doc = PdfDocument.Open(new MemoryStream(original));

        Assert.Single(doc.Pages);
        Assert.Equal("Xref Stream Original", TitleOf(original));
    }

    [Fact]
    public void XrefStreamOriginal_AppendsAnXrefStreamWhoseNewObjectsResolve()
    {
        byte[] original = XrefStreamPdf();

        byte[] produced = AppendRevision(original, (doc, objects) =>
        {
            // A revision of an existing object…
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            Assert.NotNull(info);
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Xref Stream Revised");
            objects.Add(new IndirectObject(4, 0, revised));

            // …and a brand-new object that only the appended section can name.
            var minted = new PdfDictionary();
            minted[new PdfName("Marker")] = new PdfString("minted in the update");
            objects.Add(new IndirectObject(6, 0, minted));
        });

        AssertStrictAppend(original, produced);

        string appended = AppendedText(original, produced);

        // The xref-stream dictionary is never compressed, so this token is a deterministic
        // marker of the format decision. A classic `trailer` keyword must NOT appear.
        Assert.Contains("/Type /XRef", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("\ntrailer\n", appended, StringComparison.Ordinal);
        Assert.Contains("/Prev", appended, StringComparison.Ordinal);

        // "It parses" is not the assertion: the appended objects must actually resolve.
        Assert.Equal("Xref Stream Revised", TitleOf(produced));

        using var after = PdfDocument.Open(new MemoryStream(produced));

        var minted6 = after.GetObject(6) as PdfDictionary;
        Assert.NotNull(minted6);
        Assert.Equal("minted in the update",
            (after.ResolveObject(minted6.Get(new PdfName("Marker"))) as PdfString)?.GetText());

        // …and so must the objects that exist only in the original revision, which only works
        // if /Prev points at the section it supersedes.
        Assert.Single(after.Pages);
        Assert.NotNull(after.Catalog?.Dictionary);
    }

    [Fact]
    public void XrefStreamOriginal_XrefStreamObjectIsNumberedBeyondEveryWrittenObject()
    {
        byte[] original = XrefStreamPdf();

        byte[] produced = AppendRevision(original, (doc, objects) =>
            objects.Add(new IndirectObject(6, 0, new PdfString("appended"))));

        // The cross-reference stream is an indirect object and needs an entry for itself; it may
        // not reuse a number this revision (or the original) already defines, or the two entries
        // collide and one of them is lost.
        string appended = AppendedText(original, produced);
        Assert.Matches(new Regex(@"7\s+0\s+obj\s*<<\s*/Type\s*/XRef"), appended);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Equal("appended", (after.GetObject(6) as PdfString)?.GetText());
    }

    [Fact]
    public void FormatOverride_ForcesAClassicTableOntoAnXrefStreamDocument()
    {
        byte[] original = XrefStreamPdf();

        byte[] produced = AppendRevision(
            original,
            (doc, objects) => objects.Add(new IndirectObject(6, 0, new PdfString("forced"))),
            options => options.Format = XrefSectionFormat.ClassicTable);

        string appended = AppendedText(original, produced);

        // The override exists for callers that know their reader; Auto would have written a stream.
        Assert.Contains("\ntrailer\n", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", appended, StringComparison.Ordinal);
    }

    // ===============================================================
    //  /ID
    // ===============================================================

    [Fact]
    public void ExistingId_IsCarriedForwardUnchanged()
    {
        byte[] original = ClassicPdfWithId();

        byte[] produced = AppendRevision(original, (doc, objects) =>
            ReviseTitle(doc, objects, "Keeps Its Id"));

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

    [Fact]
    public void MissingId_IsSynthesisedAsTwoSixteenByteHalves()
    {
        byte[] original = ClassicPdf();

        using (var before = PdfDocument.Open(new MemoryStream(original)))
            Assert.Null(before.Trailer.Get(PdfName.ID));

        byte[] produced = AppendRevision(original, (doc, objects) =>
            ReviseTitle(doc, objects, "Needs An Id"));

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
    public void SynthesisedId_IsDeterministicAcrossTwoIdenticalRuns()
    {
        byte[] original = ClassicPdf();

        // The revised object is built from scratch (not cloned) so the two runs cannot differ for
        // any reason other than the writer itself: a Random or a timestamp in the /ID synthesiser
        // would show up here and nowhere else.
        void Edit(PdfDocument doc, List<IndirectObject> objects)
        {
            var info = new PdfDictionary();
            info[new PdfName("Title")] = new PdfString("Deterministic");
            objects.Add(new IndirectObject(6, 0, info));
        }

        Assert.Equal(AppendRevision(original, Edit), AppendRevision(original, Edit));
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
    public void Generation3Object_UsesThatGenerationInBothHeaderAndXrefEntry()
    {
        byte[] original = ClassicPdfWithGeneration3Info();

        byte[] produced = AppendRevision(original, (doc, objects) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            Assert.NotNull(info);
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString("Generation Three Revised");
            objects.Add(new IndirectObject(6, 3, revised));
        });

        string appended = AppendedText(original, produced);

        Assert.Contains("6 3 obj", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("6 0 obj", appended, StringComparison.Ordinal);

        // The xref entry's 5-digit generation field must agree with the object header, or a strict
        // reader treats the entry as stale and silently ignores the whole update.
        Assert.Matches(new Regex(@"\n\d{10} 00003 n "), appended);

        Assert.Equal("Generation Three Revised", TitleOf(produced));
    }

    [Fact]
    public void Generation3Object_InAnXrefStreamRevision_KeepsItsGenerationField()
    {
        byte[] original = ClassicPdfWithGeneration3Info();

        byte[] produced = AppendRevision(
            original,
            (doc, objects) =>
            {
                var revised = new PdfDictionary();
                revised[new PdfName("Title")] = new PdfString("Generation Three Streamed");
                objects.Add(new IndirectObject(6, 3, revised));
            },
            options => options.Format = XrefSectionFormat.CrossReferenceStream);

        // Field 3 of the binary entry is the generation; it must survive the encoding, which the
        // parser proves by resolving 6 *3* R out of the appended stream.
        Assert.Contains("6 3 obj", AppendedText(original, produced), StringComparison.Ordinal);
        Assert.Equal("Generation Three Streamed", TitleOf(produced));
    }

    // ===============================================================
    //  Stream-valued objects
    // ===============================================================

    [Fact]
    public void PdfStreamValuedObject_KeepsItsDataAndGetsAMatchingLength()
    {
        byte[] original = ClassicPdf();
        const string NewContent = "BT /F1 12 Tf 72 700 Td (Rewritten body text) Tj ET\n";

        byte[] produced = AppendRevision(original, (doc, objects) =>
        {
            var stream = new PdfStream();
            stream.RawData = Encoding.ASCII.GetBytes(NewContent);
            objects.Add(new IndirectObject(4, 0, stream));
        });

        string appended = AppendedText(original, produced);

        // A serialiser that falls through its type ladder writes the literal token `null` here:
        // the file still parses, the page just goes blank.
        Assert.DoesNotMatch(new Regex(@"4\s+0\s+obj\s+null"), appended);
        Assert.Contains("endstream", appended, StringComparison.Ordinal);

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var contents = after.ResolveObject(after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;

        Assert.NotNull(contents);
        Assert.Equal(NewContent, Encoding.ASCII.GetString(contents.GetDecodedData()));

        // /Length must describe the data that was actually written, not the data it replaced.
        Assert.Equal(NewContent.Length, contents.GetInteger(PdfName.Length) ?? -1);
    }

    [Fact]
    public void PdfStreamValuedObject_SurvivesAnXrefStreamRevisionToo()
    {
        byte[] original = XrefStreamPdf();
        const string NewContent = "BT /F1 12 Tf 72 700 Td (Streamed content) Tj ET\n";

        byte[] produced = AppendRevision(original, (doc, objects) =>
        {
            var stream = new PdfStream();
            stream.RawData = Encoding.ASCII.GetBytes(NewContent);
            objects.Add(new IndirectObject(6, 0, stream));

            PdfDictionary page = doc.Pages[0].Dictionary;
            PdfDictionary revisedPage = Clone(page);
            revisedPage[PdfName.Contents] = new PdfReference(6, 0);
            objects.Add(new IndirectObject(3, 0, revisedPage));
        });

        using var after = PdfDocument.Open(new MemoryStream(produced));
        var contents = after.ResolveObject(after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;

        Assert.NotNull(contents);
        Assert.Equal(NewContent, Encoding.ASCII.GetString(contents.GetDecodedData()));
    }

    // ===============================================================
    //  /Prev must be verified, never guessed
    // ===============================================================

    [Fact]
    public void UntrustworthyPreviousOffset_OmitsPrevAndWritesACompleteTable()
    {
        // startxref points at byte 1 — inside the "%PDF-1.7" header, which is neither a classic
        // `xref` section nor an `N G obj` header introducing a /Type /XRef stream. The parser
        // recovers the document by scanning, but keeps that bogus value in StartXrefOffset, so a
        // writer that trusts it emits a /Prev pointing at nothing: the chain breaks and every
        // object of the original revision — the catalog included — becomes unreachable. A file
        // the parser could still recover would end up impossible to open, and the edit would
        // report success.
        byte[] original = ClassicPdf(startxrefOverride: 1);

        using (var probe = PdfDocument.Open(new MemoryStream(original)))
            Assert.Single(probe.Pages);   // recovery really did rebuild the document

        byte[] produced = AppendRevision(original, (doc, objects) =>
            ReviseTitle(doc, objects, "Recovered Revision"));

        AssertStrictAppend(original, produced);

        string appended = AppendedText(original, produced);

        Assert.DoesNotContain("/Prev", appended, StringComparison.Ordinal);

        // With no chain to inherit from, the appended section has to be a complete table — free
        // list head included — or a reader following startxref finds no catalog at all.
        Assert.Contains("65535 f", appended, StringComparison.Ordinal);

        Assert.Equal("Recovered Revision", TitleOf(produced));

        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(after.Pages);

        var contents = after.ResolveObject(after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;
        Assert.NotNull(contents);
        Assert.Equal(PageContent, Encoding.ASCII.GetString(contents.GetDecodedData()));
    }

    [Fact]
    public void ExplicitOutOfRangePreviousOffset_IsIgnoredInFavourOfTheRealOne()
    {
        byte[] original = ClassicPdf();

        byte[] produced = AppendRevision(
            original,
            (doc, objects) => ReviseTitle(doc, objects, "Bad Offset Supplied"),
            options => options.PreviousXrefOffset = original.Length + 10_000);

        string appended = AppendedText(original, produced);

        // The supplied offset is past the end of the file; the literal `startxref` token in the
        // tail still names a real section, so the chain is kept rather than thrown away.
        Assert.DoesNotContain("/Prev " + (original.Length + 10_000).ToString(CultureInfo.InvariantCulture),
            appended, StringComparison.Ordinal);
        Assert.Contains("/Prev", appended, StringComparison.Ordinal);

        Assert.Equal("Bad Offset Supplied", TitleOf(produced));

        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(after.Pages);
    }

    // ===============================================================
    //  Stacked revisions
    // ===============================================================

    [Fact]
    public void ThreeStackedClassicRevisions_KeepAResolvablePrevChain()
    {
        byte[] original = ClassicPdf();

        byte[] first = AppendRevision(original, (doc, objects) => ReviseTitle(doc, objects, "First"));
        byte[] second = AppendRevision(first, (doc, objects) => ReviseTitle(doc, objects, "Second"));
        byte[] third = AppendRevision(second, (doc, objects) => ReviseTitle(doc, objects, "Third"));

        AssertStrictAppend(original, first);
        AssertStrictAppend(first, second);
        AssertStrictAppend(second, third);

        // One /Prev per appended revision; the original trailer had none.
        Assert.Equal(0, CountOccurrences(Encoding.Latin1.GetString(original), "/Prev"));
        Assert.Equal(3, CountOccurrences(Encoding.Latin1.GetString(third), "/Prev"));

        Assert.Equal("Third", TitleOf(third));

        // Objects that were never revised still resolve, which only works if every /Prev points
        // at the section it supersedes.
        using var after = PdfDocument.Open(new MemoryStream(third));
        Assert.Single(after.Pages);

        var contents = after.ResolveObject(after.Pages[0].Dictionary.Get(PdfName.Contents)) as PdfStream;
        Assert.NotNull(contents);
        Assert.Equal(PageContent, Encoding.ASCII.GetString(contents.GetDecodedData()));
    }

    [Fact]
    public void TwoStackedXrefStreamRevisions_KeepAResolvablePrevChain()
    {
        byte[] original = XrefStreamPdf();

        byte[] Revise(byte[] input, string title) => AppendRevision(input, (doc, objects) =>
        {
            var info = doc.ResolveObject(doc.Trailer.Get(PdfName.Info)) as PdfDictionary;
            PdfDictionary revised = Clone(info);
            revised[new PdfName("Title")] = new PdfString(title);
            objects.Add(new IndirectObject(4, 0, revised));
        });

        byte[] first = Revise(original, "Streamed First");
        byte[] second = Revise(first, "Streamed Second");

        AssertStrictAppend(original, first);
        AssertStrictAppend(first, second);

        // The original's own section plus one per appended revision — every revision matched the
        // form of the one it superseded rather than degrading to a hybrid part-way down the chain.
        Assert.Equal(3, CountOccurrences(Encoding.Latin1.GetString(second), "/Type /XRef"));

        Assert.Equal("Streamed Second", TitleOf(second));

        using var after = PdfDocument.Open(new MemoryStream(second));
        Assert.Single(after.Pages);
        Assert.NotNull(after.Catalog?.Dictionary);
    }

    // ===============================================================
    //  Diagnostics
    // ===============================================================

    [Fact]
    public void UntrustworthyPreviousOffset_IsReportedToTheDiagnosticSink()
    {
        byte[] original = ClassicPdf(startxrefOverride: 1);
        var reported = new List<string>();

        AppendRevision(
            original,
            (doc, objects) => ReviseTitle(doc, objects, "Reported"),
            options => options.DiagnosticSink = (operation, detail, ex) => reported.Add(operation + ": " + detail));

        // Silence would be the wrong answer: the writer dropped the caller's /Prev and rewrote
        // the section as a complete table, and the host has to be able to find that out.
        Assert.Contains(reported, m => m.Contains("/Prev is omitted from the appended revision"));
        Assert.Contains(reported, m => m.Contains("re-declared at their recovered offsets"));
    }

    // ===============================================================
    //  The original overload is unchanged public API
    // ===============================================================

    [Fact]
    public void LegacyOverload_StillAppendsAClassicTableAndCopiesTheWholeTrailer()
    {
        byte[] original = ClassicPdf();

        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        trailer.Set(new PdfName("CustomKey"), new PdfName("CustomValue"));

        var revised = new PdfDictionary();
        revised[new PdfName("Title")] = new PdfString("Legacy Path");

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        WriteResult result = IncrementalUpdateWriter.WriteUpdate(
            input, output,
            new List<IndirectObject> { new IndirectObject(6, 0, revised) },
            trailer,
            previousXrefOffset: 0,
            size: 7);

        byte[] produced = output.ToArray();
        string appended = AppendedText(original, produced);

        // Unchanged from the day it shipped: a classic table, the caller's trailer copied
        // wholesale (custom keys and all), and no /Prev for an offset of 0.
        Assert.Contains("trailer", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", appended, StringComparison.Ordinal);
        Assert.Contains("/CustomKey /CustomValue", appended, StringComparison.Ordinal);
        Assert.DoesNotContain("/Prev", appended, StringComparison.Ordinal);
        Assert.True(result.XrefOffset > original.Length);
    }
}
