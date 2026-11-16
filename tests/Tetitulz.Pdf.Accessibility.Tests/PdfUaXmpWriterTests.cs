// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for <see cref="PdfUaXmpWriter"/>, the pass that writes the <c>pdfuaid:part</c>
/// declaration — the only thing that can close UA03 — and, because it runs <b>last</b> in
/// <c>PdfAccessibility.RemediateWithReport</c>, the pass whose appended cross-reference section is
/// the one every reader of a remediated file lands on first.
///
/// <para>
/// The regression these tests exist for: the writer used to delegate to
/// <c>PdfMetadataWriter.WriteXmpMetadata</c>, which calls the legacy six-argument
/// <c>IncrementalUpdateWriter.WriteUpdate</c>. That overload <i>always</i> emits a classic
/// <c>xref</c> table plus a <c>trailer</c> copied wholesale from the parser's merged trailer. Run
/// over a document whose cross-reference section is a <b>stream</b>, it produced a hybrid whose
/// classic trailer carried <c>/Type /XRef</c>, <c>/W [1 2 2]</c> and a stale <c>/Length</c>, with
/// <c>/Prev</c> pointing at an xref stream — and returned <c>true</c>. Independent checkers reject that,
/// and a reader that only understands xref streams cannot follow the chain at all.
/// </para>
///
/// <para>Every document is built in memory (CLAUDE.md: no test reads a PDF from disk).</para>
/// </summary>
public class PdfUaXmpWriterTests
{
    // ===============================================================
    //  In-memory PDF construction — classic xref table
    // ===============================================================

    private static string StreamObject(string dictionaryExtras, string content)
        => "<< " + dictionaryExtras + " /Length " +
           content.Length.ToString(CultureInfo.InvariantCulture) +
           " >>\nstream\n" + content + "\nendstream";

    /// <summary>
    /// Assembles a classic-xref PDF from bodies indexed 1..N (index 0 is unused). Everything is
    /// ASCII, so <c>StringBuilder.Length</c> is a byte offset.
    /// </summary>
    private static byte[] BuildClassicPdf(IReadOnlyList<string> bodies, string trailerExtras)
    {
        int count = bodies.Count - 1;
        var sb = new StringBuilder(4096);
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int n = 1; n <= count; n++)
        {
            offsets[n] = sb.Length;
            sb.Append(n.ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[n]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        for (int n = 1; n <= count; n++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[n]);

        sb.Append("trailer\n<< /Size ").Append((count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R ").Append(trailerExtras ?? string.Empty).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>Classic-xref PDF, one page, no <c>/Metadata</c> in the catalog.</summary>
    private static byte[] ClassicPdfWithoutMetadata()
        => BuildClassicPdf(new[]
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        }, string.Empty);

    /// <summary>The <c>dc:creator</c> value carried by <see cref="ClassicPdfWithMetadata"/>.</summary>
    private const string ExistingCreator = "Original Creator";

    /// <summary>An XMP packet with one Dublin Core property and no PDF/UA identifier.</summary>
    private static string ExistingXmpPacket()
        => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
           "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
           "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
           "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
           "<dc:creator>" + ExistingCreator + "</dc:creator>" +
           "</rdf:Description></rdf:RDF></x:xmpmeta>\n" +
           "<?xpacket end=\"w\"?>";

    /// <summary>Classic-xref PDF whose catalog already references a <c>/Metadata</c> stream (object 4).</summary>
    private static byte[] ClassicPdfWithMetadata()
        => BuildClassicPdf(new[]
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", ExistingXmpPacket()),
        }, string.Empty);

    // ===============================================================
    //  In-memory PDF construction — cross-reference stream
    // ===============================================================

    /// <summary>
    /// A PDF whose cross-reference section is a <b>cross-reference stream</b> (object 5), not a
    /// classic table. Uncompressed with <c>/W [1 2 2]</c> so it can be built by hand. The catalog
    /// carries no <c>/Metadata</c>, so the writer has to mint one *and* revise the catalog.
    /// </summary>
    private static byte[] XrefStreamPdfWithoutMetadata()
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

    private static byte[] Apply(byte[] original, int part = 1, string title = null, string language = null)
    {
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part, title, language),
            "SetPdfUaIdentifier failed: " + (PdfUaXmpWriter.LastError ?? "(no error recorded)"));
        return output.ToArray();
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

    /// <summary>The offset named by the file's last <c>startxref</c>, or -1.</summary>
    private static long LastStartXref(byte[] pdf)
    {
        const string Token = "startxref";
        int windowStart = Math.Max(0, pdf.Length - 2048);
        string tail = Encoding.Latin1.GetString(pdf, windowStart, pdf.Length - windowStart);

        int i = tail.LastIndexOf(Token, StringComparison.Ordinal);
        if (i < 0) return -1;

        i += Token.Length;
        while (i < tail.Length && char.IsWhiteSpace(tail[i])) i++;

        long value = 0;
        bool any = false;
        while (i < tail.Length && tail[i] >= '0' && tail[i] <= '9')
        {
            value = value * 10 + (tail[i] - '0');
            any = true;
            i++;
        }
        return any ? value : -1;
    }

    /// <summary>The first 48 bytes of the cross-reference section the file's <c>startxref</c> names.</summary>
    private static string SectionAtStartXref(byte[] pdf)
    {
        long offset = LastStartXref(pdf);
        Assert.InRange(offset, 1L, (long)(pdf.Length - 1));
        return Encoding.Latin1.GetString(pdf, (int)offset, (int)Math.Min(48L, pdf.Length - offset));
    }

    private static int CountOccurrences(byte[] pdf, string needle)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int n = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    /// <summary>The decoded text of the catalog's <c>/Metadata</c> stream, or null.</summary>
    private static string XmpOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfDictionary catalog = doc.Catalog?.Dictionary;
        Assert.NotNull(catalog);

        var stream = doc.ResolveObject(catalog.Get(PdfName.Metadata)) as PdfStream;
        return stream == null ? null : Encoding.UTF8.GetString(stream.GetDecodedData());
    }

    /// <summary>The object number the catalog's <c>/Metadata</c> reference names, or -1.</summary>
    private static int MetadataObjectNumberOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var reference = doc.Catalog?.Dictionary?.Get(PdfName.Metadata) as PdfReference;
        return reference?.ReferenceObjectNumber ?? -1;
    }

    // ===============================================================
    //  Fixture guards — without these the assertions below prove nothing
    // ===============================================================

    [Fact]
    public void XrefStreamFixture_ReallyUsesACrossReferenceStream()
    {
        byte[] original = XrefStreamPdfWithoutMetadata();

        // startxref must point at an "N G obj" header, not at the classic `xref` token.
        Assert.Matches(new Regex(@"^\s*\d+\s+\d+\s+obj"), SectionAtStartXref(original));
        Assert.Equal(0, CountOccurrences(original, "\nxref\n"));

        using var doc = PdfDocument.Open(new MemoryStream(original));
        Assert.Single(doc.Pages);
        Assert.Null(doc.Catalog.Dictionary.Get(PdfName.Metadata));
    }

    [Fact]
    public void ClassicFixtures_ReallyUseAClassicXrefTable()
    {
        Assert.StartsWith("xref", SectionAtStartXref(ClassicPdfWithoutMetadata()), StringComparison.Ordinal);
        Assert.StartsWith("xref", SectionAtStartXref(ClassicPdfWithMetadata()), StringComparison.Ordinal);
    }

    // ===============================================================
    //  The regression: cross-reference form must match what it supersedes
    // ===============================================================

    /// <summary>
    /// The defect this whole file exists for. Pre-fix this produced
    /// <c>xref\n1 1\n… trailer\n&lt;&lt; /Type /XRef /W [1 2 2] /Length 30 /Prev … &gt;&gt;</c>
    /// — a classic table with xref-stream keys in its trailer, chained to an xref stream — and
    /// returned <c>true</c>.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_OverAnXrefStreamDocument_AppendsAnXrefStreamNotAClassicTable()
    {
        byte[] original = XrefStreamPdfWithoutMetadata();
        byte[] produced = Apply(original, part: 1, title: "T", language: "en-US");

        AssertStrictAppend(original, produced);

        // The section the file now points at must be a cross-reference *stream*: an indirect
        // object header, never the classic `xref` keyword.
        string section = SectionAtStartXref(produced);
        Assert.Matches(new Regex(@"^\s*\d+\s+\d+\s+obj"), section);
        Assert.DoesNotContain("xref", section, StringComparison.Ordinal);

        // …and no classic table was appended anywhere.
        Assert.Equal(0, CountOccurrences(produced, "\nxref\n"));

        // The appended object really is one.
        Assert.Contains("/Type /XRef", AppendedText(original, produced), StringComparison.Ordinal);
    }

    [Fact]
    public void SetPdfUaIdentifier_OverAnXrefStreamDocument_ProducesAReadablePdfUaDeclaration()
    {
        byte[] original = XrefStreamPdfWithoutMetadata();
        byte[] produced = Apply(original, part: 1, title: "Quarterly Report", language: "en-US");

        string xmp = XmpOf(produced);
        Assert.NotNull(xmp);
        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", xmp, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"<pdfuaid:part[^>]*>\s*1\s*</pdfuaid:part>"), xmp);
        Assert.Contains("Quarterly Report", xmp, StringComparison.Ordinal);
        Assert.Contains("en-US", xmp, StringComparison.Ordinal);

        // Objects that only exist in the original revision must still resolve, which only works
        // if the appended section's /Prev chains correctly to the xref stream it supersedes.
        using var after = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(after.Pages);
        Assert.NotNull(after.Catalog?.Dictionary);
    }

    [Fact]
    public void SetPdfUaIdentifier_OverAClassicDocument_StillAppendsAClassicTable()
    {
        // The fix must match the form of the previous section, not swap one hard-coded form for
        // another: an xref stream appended to a classic-xref file is the mirror-image defect.
        byte[] original = ClassicPdfWithoutMetadata();
        byte[] produced = Apply(original, part: 1, language: "en-US");

        AssertStrictAppend(original, produced);
        Assert.StartsWith("xref", SectionAtStartXref(produced), StringComparison.Ordinal);
        Assert.DoesNotContain("/Type /XRef", AppendedText(original, produced), StringComparison.Ordinal);

        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", XmpOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// The legacy path copied the parser's merged trailer wholesale. On an xref-stream document
    /// that dragged the stream's own plumbing into the appended trailer; the whitelisting writer
    /// carries only /Root, /Info, /ID and /Encrypt forward.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_OverAClassicDocument_AppendedTrailerCarriesRootAndId()
    {
        byte[] original = ClassicPdfWithoutMetadata();
        byte[] produced = Apply(original);

        string appended = AppendedText(original, produced);
        Assert.Contains("trailer", appended, StringComparison.Ordinal);
        Assert.Contains("/Root 1 0 R", appended, StringComparison.Ordinal);

        // /ID is required by PDF/UA-1; the original fixture has none, so it must be synthesised.
        using var after = PdfDocument.Open(new MemoryStream(produced));
        var id = after.ResolveObject(after.Trailer.Get(PdfName.ID)) as PdfArray;
        Assert.NotNull(id);
        Assert.Equal(2, id.Count);
    }

    // ===============================================================
    //  Object allocation and merge semantics
    // ===============================================================

    [Fact]
    public void SetPdfUaIdentifier_ExistingMetadataStream_IsRevisedInPlaceAndKeepsOtherProperties()
    {
        byte[] original = ClassicPdfWithMetadata();
        Assert.Equal(4, MetadataObjectNumberOf(original));
        Assert.DoesNotContain("pdfua", XmpOf(original), StringComparison.OrdinalIgnoreCase);

        byte[] produced = Apply(original, part: 1, title: "New Title");

        // The existing object is revised, not shadowed by a freshly allocated one — otherwise the
        // document ends up with two /Metadata streams and the catalog patched for no reason.
        Assert.Equal(4, MetadataObjectNumberOf(produced));

        string xmp = XmpOf(produced);
        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", xmp, StringComparison.Ordinal);
        Assert.Contains("New Title", xmp, StringComparison.Ordinal);

        // "Merge, don't clobber": every property the original packet carried survives.
        Assert.Contains(ExistingCreator, xmp, StringComparison.Ordinal);
    }

    [Fact]
    public void SetPdfUaIdentifier_NoMetadataEntry_MintsOneAndWiresItIntoTheCatalog()
    {
        byte[] original = ClassicPdfWithoutMetadata();
        Assert.Equal(-1, MetadataObjectNumberOf(original));

        byte[] produced = Apply(original);

        // The freshly allocated object must be the one the catalog names, not merely present.
        int number = MetadataObjectNumberOf(produced);
        Assert.True(number > 0, "the revised catalog must reference the new /Metadata object");
        Assert.NotNull(XmpOf(produced));
    }

    [Fact]
    public void SetPdfUaIdentifier_TwoIdenticalRuns_ProduceByteIdenticalOutput()
    {
        // No timestamp, no Random, and an /ID synthesised as a pure function of the input bytes.
        byte[] original = ClassicPdfWithoutMetadata();
        Assert.Equal(Apply(original, 1, "T", "en-US"), Apply(original, 1, "T", "en-US"));
    }

    [Fact]
    public void SetPdfUaIdentifier_AppliedTwice_LeavesExactlyOnePdfUaPart()
    {
        byte[] once = Apply(ClassicPdfWithoutMetadata(), part: 1);
        byte[] twice = Apply(once, part: 1);

        string xmp = XmpOf(twice);
        Assert.NotNull(xmp);

        // A duplicated pdfuaid:part is itself a PDF/UA violation, so the merge must replace the
        // existing declaration rather than append a second one.
        int occurrences = 0;
        for (int i = xmp.IndexOf("<pdfuaid:part", StringComparison.Ordinal); i >= 0;
             i = xmp.IndexOf("<pdfuaid:part", i + 1, StringComparison.Ordinal))
        {
            occurrences++;
        }
        Assert.Equal(1, occurrences);
    }

    // ===============================================================
    //  Failure paths — the caller's document is never damaged
    // ===============================================================

    [Fact]
    public void SetPdfUaIdentifier_EmptyInput_ReturnsFalseAndRecordsAnError()
    {
        using var input = new MemoryStream(Array.Empty<byte>());
        using var output = new MemoryStream();

        Assert.False(PdfUaXmpWriter.SetPdfUaIdentifier(input, output));
        Assert.False(string.IsNullOrEmpty(PdfUaXmpWriter.LastError));
    }

    [Fact]
    public void SetPdfUaIdentifier_UnparseableInput_CopiesTheSourceBytesThrough()
    {
        byte[] garbage = Encoding.ASCII.GetBytes("not a pdf at all, not even close\n");

        using var input = new MemoryStream(garbage);
        using var output = new MemoryStream();

        Assert.False(PdfUaXmpWriter.SetPdfUaIdentifier(input, output));

        // A chained remediation pipeline reads this stream next; it must not be empty or truncated.
        Assert.Equal(garbage, output.ToArray());
        Assert.False(string.IsNullOrEmpty(PdfUaXmpWriter.LastError));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void SetPdfUaIdentifier_UnsupportedPart_Throws(int part)
    {
        using var input = new MemoryStream(ClassicPdfWithoutMetadata());
        using var output = new MemoryStream();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part));
    }

    // ===============================================================
    //  Packet construction (no PDF involved)
    // ===============================================================

    [Fact]
    public void BuildPdfUaXmpPacket_MalformedExistingPacket_FallsBackToAFreshOne()
    {
        // Refusing to write would leave the UA03 violation unfixable, so a broken packet is
        // replaced rather than propagated.
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket("<x:xmpmeta><unclosed>", part: 1);

        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
        Assert.Contains("<?xpacket end=\"w\"?>", packet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adversarial input for the <see cref="PdfUaXmpWriter.MaxSearchDepth"/> guard: a packet
    /// nested far deeper than the cap must neither hang nor overflow the stack, and must still
    /// produce a usable declaration.
    /// </summary>
    [Fact]
    public void BuildPdfUaXmpPacket_DeeplyNestedExistingPacket_StillProducesADeclaration()
    {
        const int Depth = 400;   // >> MaxSearchDepth (64)

        var sb = new StringBuilder(16 * Depth + 512);
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">")
          .Append("<rdf:Description rdf:about=\"\">");
        for (int i = 0; i < Depth; i++) sb.Append("<n>");
        for (int i = 0; i < Depth; i++) sb.Append("</n>");
        sb.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");

        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(sb.ToString(), part: 1);

        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPdfUaXmpPacket_Part2_DeclaresPartTwo()
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(null, part: 2);
        Assert.Matches(new Regex(@"<pdfuaid:part[^>]*>\s*2\s*</pdfuaid:part>"), packet);
    }

    // ===============================================================
    //  Adversarial input — packet amplification (DoS guards)
    // ===============================================================
    //
    // The defect: BuildPdfUaXmpPacket re-serialised the *whole* existing document through an
    // XmlWriter with Indent = true / IndentChars = "  ". Indented output prefixes both the start
    // and the end tag of an element at nesting level d with a newline and 2*d spaces, so a packet
    // nested D levels serialises to Θ(D²) characters however few bytes it took on input — and
    // "<a>" x N compresses to nothing under /FlateDecode. Neither existing guard helped:
    // MaxSearchDepth bounds only FindElement/FindAttribute (an attacker puts rdf:RDF at the top so
    // EnsureDescription succeeds, and hangs the deep subtree off a *sibling*), and
    // DtdProcessing.Prohibit stops entity expansion, which this needs none of. The oversized
    // packet was then written into the file as a real revision and SetPdfUaIdentifier returned
    // true. Measured before the fix: an 861-byte input (depth 20000) produced an 800,263,847-byte
    // PDF after ~6 s and ~10 GB of allocation; a 1,191-byte input (depth 60000) killed the
    // operation with OutOfMemoryException — process death in a memory-capped host.

    /// <summary>
    /// A well-formed packet whose <c>rdf:RDF</c> / <c>rdf:Description</c> sit at the top — so
    /// <c>EnsureDescription</c> succeeds at depth 1 and the document is kept — with the deep
    /// nesting hanging off a <b>sibling</b> subtree that no depth-capped search ever visits.
    /// That is the shape the <see cref="PdfUaXmpWriter.MaxSearchDepth"/> guard does not catch.
    /// </summary>
    private static string DeeplyNestedXmpPacket(int depth)
    {
        var sb = new StringBuilder(8 * depth + 512);
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">")
          .Append("<rdf:Description rdf:about=\"\"/>")
          .Append("</rdf:RDF>")
          .Append("<d>");
        for (int i = 0; i < depth; i++) sb.Append("<a>");
        for (int i = 0; i < depth; i++) sb.Append("</a>");
        sb.Append("</d></x:xmpmeta>");
        return sb.ToString();
    }

    /// <summary>
    /// Classic-xref PDF whose catalog references a <c>/FlateDecode</c>d <c>/Metadata</c> stream
    /// (object 4). Built byte-wise rather than through <see cref="BuildClassicPdf"/> because the
    /// compressed payload is binary and would not survive an ASCII round trip. Compression is the
    /// point: it is what makes the hostile file tiny.
    /// </summary>
    private static byte[] ClassicPdfWithCompressedMetadata(string xmp)
    {
        byte[] compressed = FlateEncodeFilter.Encode(Encoding.UTF8.GetBytes(xmp));

        var ms = new MemoryStream();
        void Ascii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Ascii("%PDF-1.7\n");

        var offsets = new int[5];
        string[] bodies =
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        };

        for (int n = 1; n <= 3; n++)
        {
            offsets[n] = (int)ms.Position;
            Ascii(n.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + bodies[n] + "\nendobj\n");
        }

        offsets[4] = (int)ms.Position;
        Ascii("4 0 obj\n<< /Type /Metadata /Subtype /XML /Filter /FlateDecode /Length " +
              compressed.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        ms.Write(compressed, 0, compressed.Length);
        Ascii("\nendstream\nendobj\n");

        int xref = (int)ms.Position;
        Ascii("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++)
            Ascii(string.Format(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[n]));

        Ascii("trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n" +
              xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// Adversarial input for the nesting-depth budget. Pre-fix, a ~16,000-character packet came
    /// back out as ~8,000,000 characters.
    /// </summary>
    [Fact]
    public void BuildPdfUaXmpPacket_DeepSiblingSubtree_DoesNotAmplifyTheOutput()
    {
        const int Depth = 2000;   // >> MaxSearchDepth (64)
        string hostile = DeeplyNestedXmpPacket(Depth);

        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(hostile, part: 1);

        Assert.True(packet.Length < 64 * 1024,
            "a " + hostile.Length + "-character packet was re-serialised as " + packet.Length +
            " characters (" + (packet.Length / Math.Max(1, hostile.Length)) +
            "x); indented re-serialisation is quadratic in nesting depth, so the packet must be " +
            "discarded rather than rewritten");

        // Discarding the hostile packet must still close UA03 — refusing to write would leave
        // the violation unfixable, which is the failure mode the fallback path exists to avoid.
        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"<pdfuaid:part[^>]*>\s*1\s*</pdfuaid:part>"), packet);
    }

    /// <summary>
    /// The guard must not be over-broad: a legitimately shaped packet (XMP nests under ten levels)
    /// is still merged, properties and all. Without this, tightening the cap would silently start
    /// destroying every document's existing metadata.
    /// </summary>
    [Fact]
    public void BuildPdfUaXmpPacket_LegitimatelyShapedPacket_IsStillMerged()
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(ExistingXmpPacket(), part: 1);

        Assert.Contains(ExistingCreator, packet, StringComparison.Ordinal);
        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
    }

    /// <summary>
    /// End-to-end form of the repro: a small <c>/FlateDecode</c>d <c>/Metadata</c> stream that
    /// decodes to a deeply nested packet. The whole point is that the attacker's file is tiny and
    /// the victim's is not.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_CompressedDeeplyNestedMetadata_StaysProportionalToTheInput()
    {
        const int Depth = 8000;
        byte[] original = ClassicPdfWithCompressedMetadata(DeeplyNestedXmpPacket(Depth));

        Assert.True(original.Length < 8 * 1024,
            "the fixture must be a small file for the amplification to mean anything; it is " +
            original.Length + " bytes");

        byte[] produced = Apply(original, part: 1, title: "T", language: "en");

        // Pre-fix: ~128 MB out of a sub-kilobyte input (and 800 MB at depth 20000, OOM at 60000).
        Assert.True(produced.Length < original.Length + 64 * 1024,
            "a " + original.Length + "-byte input produced a " + produced.Length +
            "-byte document — " + (produced.Length / Math.Max(1, original.Length)) +
            "x amplification");

        // Still a correct, readable remediation — not a refusal.
        AssertStrictAppend(original, produced);
        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", XmpOf(produced), StringComparison.Ordinal);
    }

    /// <summary>
    /// The second bound: a packet that is <b>shallow but enormous</b> never trips the depth budget,
    /// so it needs its own ceiling. <c>PdfStream.GetDecodedData()</c> is bounded only by
    /// <c>PdfLimits.MaxDecompressedBytes</c> (512 MB) — a filter-level cap sized for image data,
    /// not for a metadata packet.
    /// </summary>
    [Fact]
    public void SetPdfUaIdentifier_OversizedCompressedMetadata_DiscardsItRatherThanRewritingIt()
    {
        const int TargetChars = 3 * 1024 * 1024;   // > MaxExistingPacketBytes (2 MB)

        var sb = new StringBuilder(TargetChars + 256);
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">")
          .Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">")
          .Append("<rdf:Description rdf:about=\"\"/></rdf:RDF><d>");
        while (sb.Length < TargetChars) sb.Append("<a>x</a>");
        sb.Append("</d></x:xmpmeta>");

        byte[] original = ClassicPdfWithCompressedMetadata(sb.ToString());

        Assert.True(original.Length < 128 * 1024,
            "the fixture must stay far smaller than the packet it decodes to; it is " +
            original.Length + " bytes");

        byte[] produced = Apply(original, part: 1);

        Assert.True(produced.Length < original.Length + 64 * 1024,
            "a " + original.Length + "-byte input produced a " + produced.Length +
            "-byte document; a multi-megabyte \"metadata\" packet must be discarded, not " +
            "re-indented and embedded");

        Assert.Contains("http://www.aiim.org/pdfua/ns/id/", XmpOf(produced), StringComparison.Ordinal);
    }

    // ===============================================================
    //  A PDF/A packet: pdfuaid described through pdfaExtension:schemas
    // ===============================================================

    /// <summary>A packet declaring PDF/A-<paramref name="pdfaPart"/>b, optionally with an extension container.</summary>
    private static string PdfAPacket(int pdfaPart, string extraProperties = "")
        => "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
           "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
           "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
           "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"" +
           " xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"" +
           " xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\">" +
           "<pdfaid:part>" + pdfaPart.ToString(CultureInfo.InvariantCulture) + "</pdfaid:part>" +
           "<pdfaid:conformance>B</pdfaid:conformance>" + extraProperties +
           "</rdf:Description></rdf:RDF></x:xmpmeta>\n" +
           "<?xpacket end=\"w\"?>";

    private static Tetitulz.Pdf.Plugins.Conformance.PdfAValidationResult ValidatePdfA(string packet, int part)
    {
        byte[] pdf = BuildClassicPdf(new[]
        {
            null,
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", packet),
        }, string.Empty);
        using var ms = new MemoryStream(pdf);
        return Tetitulz.Pdf.Plugins.Conformance.PdfAValidator.Validate(ms,
            (Tetitulz.Pdf.Plugins.Conformance.PdfAConformancePart)part);
    }

    private static int PdfUaSchemaDescriptions(string packet)
        => Regex.Matches(packet, @"<pdfaSchema:namespaceURI>\s*http://www\.aiim\.org/pdfua/ns/id/\s*</pdfaSchema:namespaceURI>").Count;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void BuildPdfUaXmpPacket_OverAPdfAPacket_DescribesThePdfUaSchema_SoThePdfADeclarationStillHolds(int pdfaPart)
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(PdfAPacket(pdfaPart), part: 1);

        Assert.Equal(1, PdfUaSchemaDescriptions(packet));
        var result = ValidatePdfA(packet, pdfaPart);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A34" || v.Code == "A35" || v.Code == "A36");

        // Potency: pdfuaid:part added without the description is exactly what the rule refuses.
        string bare = PdfAPacket(pdfaPart, "<pdfuaid:part xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">1</pdfuaid:part>");
        var a34 = Assert.Single(ValidatePdfA(bare, pdfaPart).Violations, v => v.Code == "A34");
        Assert.Contains("pdfuaid:part", a34.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPdfUaXmpPacket_WithoutAPdfADeclaration_AddsNoExtensionSchema()
    {
        string packet = PdfUaXmpWriter.BuildPdfUaXmpPacket(ExistingXmpPacket(), part: 1);
        Assert.Contains(PdfUaXmpWriter.PdfUaIdNamespace, packet, StringComparison.Ordinal);
        Assert.DoesNotContain("pdfa/ns/extension", packet, StringComparison.Ordinal);
        // Nor for PDF/A-4, which has no predefined-schema rule.
        Assert.Equal(0, PdfUaSchemaDescriptions(PdfUaXmpWriter.BuildPdfUaXmpPacket(PdfAPacket(4), part: 2)));
    }

    [Fact]
    public void BuildPdfUaXmpPacket_AppliedTwice_DescribesThePdfUaSchemaOnce_AndKeepsAnotherSchemasDescription()
    {
        const string other =
            "<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">" +
            "<pdfaSchema:schema>Other</pdfaSchema:schema>" +
            "<pdfaSchema:namespaceURI>http://example.com/other/</pdfaSchema:namespaceURI>" +
            "<pdfaSchema:prefix>ot</pdfaSchema:prefix>" +
            "</rdf:li></rdf:Bag></pdfaExtension:schemas>";
        string once = PdfUaXmpWriter.BuildPdfUaXmpPacket(PdfAPacket(2, other), part: 1);
        string twice = PdfUaXmpWriter.BuildPdfUaXmpPacket(once, part: 1);

        Assert.Equal(1, PdfUaSchemaDescriptions(twice));
        Assert.Contains("http://example.com/other/", twice, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(twice, "<pdfaExtension:schemas"));
        Assert.DoesNotContain(ValidatePdfA(twice, 2).Violations, v => v.Code == "A34" || v.Code == "A36");
    }
}
