// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Which origin an appended revision's byte offsets count from. ISO 32000-2 §7.5.2: "byte offsets
/// shall be calculated from the PERCENT SIGN (25h)" of the header, and NOTE 1 allows "arbitrary bytes
/// preceding the %PDF-". An original with such bytes has every section counted from its header, so
/// the revision appended to it must be too, or a reader following the chain finds the two halves on
/// different bases.
/// </summary>
/// <remarks>
/// <para><b>Two kinds of evidence, on purpose.</b> The written numbers are read back as raw bytes —
/// the writer emits raw bytes, and the parser normalises exactly the thing under test away, since it
/// reads a misplaced section through a byte-0 fallback and reports nothing. The parser is then used
/// too, with <see cref="PdfParser.SectionsReadFromByteZero"/> asserted at zero: that is what says
/// the chain was read <i>as written</i> rather than rescued.</para>
/// <para>Both overloads used to record output positions and probe the original at the bare offset,
/// so a prefixed original was never chained: its section sat one prefix-length away from where the
/// probe looked, the whole document was re-declared, and the offsets that were written were wrong by
/// the same amount.</para>
/// </remarks>
public class PrefixedOriginalUpdateTests
{
    /// <summary>Fifteen bytes that no PDF token could start.</summary>
    private const string Prefix = "junk-junk-junk\n";

    private const string FileId = "[<00112233445566778899AABBCCDDEEFF> <FFEEDDCCBBAA99887766554433221100>]";

    // ================================================================
    //  The six-argument overload
    // ================================================================

    [Fact]
    public void Six_argument_overload_counts_the_appended_revision_from_the_percent_sign_of_a_prefixed_original()
    {
        byte[] original = ClassicOriginal(Prefix, countFromByteZero: false);
        long previous = StartxrefValue(original);

        byte[] produced = AppendWithSixArguments(original, previous);

        Assert.Null(IncrementalUpdateWriter.LastUnchainedRecovery);
        AssertRevisionIsCountedFromTheHeader(original, produced, Prefix.Length, previous);
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    /// <summary>
    /// With no verifiable previous section the overload re-declares the original's objects at the
    /// offsets the parser recovered — which are stream positions, so they too have to be converted
    /// to the revision's basis.
    /// </summary>
    [Fact]
    public void Six_argument_overload_re_declares_a_prefixed_originals_objects_from_the_percent_sign_when_unchained()
    {
        byte[] original = ClassicOriginal(Prefix, countFromByteZero: false);

        byte[] produced = AppendWithSixArguments(original, previousXrefOffset: 0);

        Assert.Contains("re-declared", IncrementalUpdateWriter.LastUnchainedRecovery ?? "", StringComparison.Ordinal);

        string tail = Encoding.ASCII.GetString(produced, original.Length, produced.Length - original.Length);
        Assert.Equal(IndexOfAscii(produced, "3 0 obj", 0) - Prefix.Length, ClassicEntryOffset(tail, 3));
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    // ================================================================
    //  The options overload
    // ================================================================

    [Fact]
    public void Options_overload_counts_the_appended_revision_from_the_percent_sign_of_a_prefixed_original()
    {
        byte[] original = ClassicOriginal(Prefix, countFromByteZero: false);
        long previous = StartxrefValue(original);

        byte[] produced = AppendWithOptions(original, out List<string> reported);

        Assert.DoesNotContain(reported, m => m.Contains("/Prev is omitted", StringComparison.Ordinal));
        Assert.DoesNotContain(reported, m => m.Contains("re-declared", StringComparison.Ordinal));
        AssertRevisionIsCountedFromTheHeader(original, produced, Prefix.Length, previous);
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    /// <summary>
    /// A cross-reference-stream original gets a cross-reference-stream revision, whose type-1
    /// entries, its own self-entry and its <c>startxref</c> all count from the header.
    /// </summary>
    [Fact]
    public void Options_overload_appends_a_cross_reference_stream_revision_from_the_percent_sign_of_a_prefixed_original()
    {
        byte[] original = XrefStreamOriginal(Prefix);

        byte[] produced = AppendWithOptions(original, out List<string> reported);

        Assert.DoesNotContain(reported, m => m.Contains("/Prev is omitted", StringComparison.Ordinal));
        string tail = Encoding.Latin1.GetString(produced, original.Length, produced.Length - original.Length);
        Assert.Contains("/Type /XRef", tail, StringComparison.Ordinal);

        // The startxref names the revision's cross-reference stream object, counted from the header:
        // the `N 0 obj` line before the dictionary that declares /Type /XRef.
        int xrefDictionary = IndexOfAscii(produced, "/Type /XRef", original.Length);
        int objKeyword = LastIndexOfAscii(produced, " obj", xrefDictionary);
        int xrefHeader = LastIndexOfAscii(produced, "\n", objKeyword) + 1;
        Assert.Equal(xrefHeader - Prefix.Length, StartxrefValue(produced));

        var parser = ParseWhole(produced);
        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(0, parser.SectionsReadFromByteZero);

        // Entries are stream positions once read: the appended object's, and the stream's own.
        Assert.Equal(IndexOfAscii(produced, "4 0 obj", original.Length), parser.CrossReferenceTable.GetEntry(4).Offset);
        int xrefObjectNumber = int.Parse(
            Encoding.ASCII.GetString(produced, xrefHeader, IndexOfAscii(produced, " ", xrefHeader) - xrefHeader),
            CultureInfo.InvariantCulture);
        Assert.Equal(xrefHeader, parser.CrossReferenceTable.GetEntry(xrefObjectNumber).Offset);
        Assert.Equal(2, MarkerOf(parser.GetObject(4)));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
    }

    /// <summary>
    /// Unchained through the options overload: the original's <c>startxref</c> is zeroed, so neither
    /// the parser's value nor the tail scan names a section, and the whole document is re-declared.
    /// </summary>
    [Fact]
    public void Options_overload_re_declares_a_prefixed_originals_objects_from_the_percent_sign_when_unchained()
    {
        byte[] original = WithStartxrefZeroed(ClassicOriginal(Prefix, countFromByteZero: false));

        byte[] produced = AppendWithOptions(original, out List<string> reported);

        Assert.Contains(reported, m => m.Contains("re-declared", StringComparison.Ordinal));
        string tail = Encoding.ASCII.GetString(produced, original.Length, produced.Length - original.Length);
        Assert.Equal(IndexOfAscii(produced, "3 0 obj", 0) - Prefix.Length, ClassicEntryOffset(tail, 3));
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    // ================================================================
    //  The prefix is the only difference
    // ================================================================

    /// <summary>
    /// The revision appended to a prefixed original is byte for byte the one appended to the same
    /// document without the prefix. This is what pins the unprefixed output as unchanged: every
    /// assertion above holds of the prefixed output, and this makes the two the same bytes.
    /// </summary>
    [Fact]
    public void A_prefixed_original_gets_the_same_revision_bytes_as_the_unprefixed_one()
    {
        byte[] plain = ClassicOriginal("", countFromByteZero: false);
        byte[] prefixed = ClassicOriginal(Prefix, countFromByteZero: false);
        long previous = StartxrefValue(plain);
        Assert.Equal(previous, StartxrefValue(prefixed));

        byte[] plainSix = AppendWithSixArguments(plain, previous);
        byte[] prefixedSix = AppendWithSixArguments(prefixed, previous);
        Assert.Equal(plainSix, Strip(Prefix.Length, prefixedSix));

        byte[] plainOptions = AppendWithOptions(plain, out _);
        byte[] prefixedOptions = AppendWithOptions(prefixed, out _);
        Assert.Equal(plainOptions, Strip(Prefix.Length, prefixedOptions));
    }

    // ================================================================
    //  What does not change
    // ================================================================

    /// <summary>
    /// The mirror control: an unprefixed original at output position 0 keeps output positions, which
    /// under §7.5.2 are the same numbers.
    /// </summary>
    [Fact]
    public void An_unprefixed_original_at_output_position_zero_keeps_writing_output_positions()
    {
        byte[] original = ClassicOriginal("", countFromByteZero: false);
        long previous = StartxrefValue(original);

        byte[] produced = AppendWithSixArguments(original, previous);

        AssertRevisionIsCountedFromTheHeader(original, produced, 0, previous);
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    /// <summary>
    /// A stream with no <c>%PDF-</c> header at all keeps counting from byte 0, exactly as before —
    /// the parser folds a missing header to origin 0, and so must the writer.
    /// </summary>
    [Fact]
    public void A_headerless_original_keeps_writing_output_positions()
    {
        byte[] original = ClassicOriginal("", countFromByteZero: false, header: false);
        long previous = StartxrefValue(original);

        byte[] produced = AppendWithSixArguments(original, previous);

        Assert.Null(IncrementalUpdateWriter.LastUnchainedRecovery);
        AssertRevisionIsCountedFromTheHeader(original, produced, 0, previous);
        AssertHeaderlessChainReads(produced);

        // The options overload locates the header in a byte array rather than a stream; the same
        // folding of "no header" to origin 0 has to hold there. The document cannot be opened
        // without a header, so the previous offset is supplied.
        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, new List<IndirectObject> { Marker(4, 2) },
            new IncrementalUpdateOptions { OriginalTrailer = RootTrailer(), PreviousXrefOffset = previous, Size = 5 });
        byte[] producedByOptions = output.ToArray();

        AssertRevisionIsCountedFromTheHeader(original, producedByOptions, 0, previous);
        AssertHeaderlessChainReads(producedByOptions);
    }

    /// <summary>
    /// The header is looked for inside the first 1024 bytes and nowhere else — by the parser and,
    /// through the same locator, by the writer. An original whose header lies past the window has no
    /// header to either: the parser counts from byte 0, so the writer must too, or the revision it
    /// appends counts from a point the reader cannot see. The options overload locates the header in
    /// a byte array, where nothing but the locator's own bound limits the search.
    /// </summary>
    [Fact]
    public void A_header_beyond_the_search_window_is_no_header_for_the_writer_either()
    {
        string longPrefix = new string('j', 1023) + "\n";           // the percent sign lands at byte 1024
        byte[] original = ClassicOriginal(longPrefix, countFromByteZero: true);
        long previous = StartxrefValue(original);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, new List<IndirectObject> { Marker(4, 2) },
            new IncrementalUpdateOptions { OriginalTrailer = RootTrailer(), PreviousXrefOffset = previous, Size = 5 });
        byte[] produced = output.ToArray();

        AssertRevisionIsCountedFromTheHeader(original, produced, 0, previous);
        AssertHeaderlessChainReads(produced);
    }

    /// <summary>Like <see cref="AssertChainReadsAsWritten"/>, for a file the parser cannot <c>ParseHeader</c>.</summary>
    private static void AssertHeaderlessChainReads(byte[] produced)
    {
        var parser = ParseWhole(produced);
        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(0L, parser.HeaderOffset);
        Assert.Equal(2, MarkerOf(parser.GetObject(4)));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
    }

    /// <summary>
    /// An output already holding bytes — a <c>FileMode.Append</c> destination, one part of a multipart
    /// body — with an unprefixed original: the header sits where the original landed, and the
    /// revision counts from there, so the output reads as one file and reads with the lead
    /// stripped. The options overload used to write output positions here, with a <c>/Prev</c>
    /// shifted to match, and the result read neither way: the original's sections count from its
    /// header, and the revision's did not.
    /// </summary>
    [Fact]
    public void A_pre_positioned_output_with_an_unprefixed_original_counts_from_where_the_original_landed()
    {
        const int Preamble = 37;
        byte[] original = ClassicOriginal("", countFromByteZero: false);
        long previous = StartxrefValue(original);

        using var doc = PdfDocument.Open(new MemoryStream(original));
        using var output = new MemoryStream();
        output.Write(new byte[Preamble], 0, Preamble);
        IncrementalUpdateWriter.WriteUpdate(original, output, new List<IndirectObject> { Marker(4, 2) },
            new IncrementalUpdateOptions { Document = doc, OriginalTrailer = doc.Trailer });
        byte[] produced = output.ToArray();

        int xrefKeyword = IndexOfAscii(produced, "xref", Preamble + original.Length);
        Assert.Equal(xrefKeyword - Preamble, StartxrefValue(produced));
        string tail = Encoding.ASCII.GetString(produced, Preamble + original.Length,
            produced.Length - Preamble - original.Length);
        Assert.Equal(IndexOfAscii(produced, "4 0 obj", Preamble + original.Length) - Preamble, ClassicEntryOffset(tail, 4));
        Assert.Equal(previous, PrevValue(tail));

        // As one file, and as the file the lead was stripped from.
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
        AssertChainReadsAsWritten(Strip(Preamble, produced), expectedMarker: 2, expectedFallbacks: 0);
    }

    /// <summary>
    /// A prefixed original embedded at a non-zero output position: the percent sign is where it sits
    /// in the output, and the revision counts from there.
    /// </summary>
    [Fact]
    public void A_pre_positioned_output_with_a_prefixed_original_counts_from_the_percent_sign_in_the_output()
    {
        const int Preamble = 37;
        byte[] original = ClassicOriginal(Prefix, countFromByteZero: false);
        long previous = StartxrefValue(original);

        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        output.Write(new byte[Preamble], 0, Preamble);
        IncrementalUpdateWriter.WriteUpdate(input, output, new List<IndirectObject> { Marker(4, 2) },
            RootTrailer(), previous, size: 5);
        byte[] produced = output.ToArray();

        int origin = Preamble + Prefix.Length;
        int xrefKeyword = IndexOfAscii(produced, "xref", Preamble + original.Length);
        Assert.Equal(xrefKeyword - origin, StartxrefValue(produced));
        string tail = Encoding.ASCII.GetString(produced, Preamble + original.Length,
            produced.Length - Preamble - original.Length);
        Assert.Equal(IndexOfAscii(produced, "4 0 obj", Preamble + original.Length) - origin, ClassicEntryOffset(tail, 4));
        Assert.Equal(previous, PrevValue(tail));

        // Read as one file, whose header sits Preamble + Prefix bytes in: every section is where its
        // offset says.
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 0);
    }

    // ================================================================
    //  A prefixed original whose own sections count from byte 0
    // ================================================================

    /// <summary>
    /// What this writer itself produced before it honoured §7.5.2: a prefixed file whose sections
    /// and entries are output positions. It must still chain, through the same byte-0 reading the
    /// parser keeps for it — and the <c>/Prev</c> written must be the value the original wrote, not
    /// one recomputed from where its section was found: that section's entries count from byte 0,
    /// and only the value as written makes a reader take that reading again.
    /// </summary>
    [Fact]
    public void A_prefixed_original_whose_sections_count_from_byte_zero_still_chains_and_still_resolves()
    {
        byte[] original = ClassicOriginal(Prefix, countFromByteZero: true);
        long previous = StartxrefValue(original);

        byte[] produced = AppendWithSixArguments(original, previous);

        Assert.Null(IncrementalUpdateWriter.LastUnchainedRecovery);
        string tail = Encoding.ASCII.GetString(produced, original.Length, produced.Length - original.Length);
        Assert.Equal(previous, PrevValue(tail));

        // The new section is read as written; the legacy one through the fallback, with its own
        // byte-0 entries — which is what an object only revision 1 defines proves.
        AssertChainReadsAsWritten(produced, expectedMarker: 2, expectedFallbacks: 1);

        // The options overload resolves the previous section itself, and must write the same value.
        byte[] producedByOptions = AppendWithOptions(original, out List<string> reported);
        Assert.DoesNotContain(reported, m => m.Contains("/Prev is omitted", StringComparison.Ordinal));
        string optionsTail = Encoding.ASCII.GetString(producedByOptions, original.Length,
            producedByOptions.Length - original.Length);
        Assert.Equal(previous, PrevValue(optionsTail));
        AssertChainReadsAsWritten(producedByOptions, expectedMarker: 2, expectedFallbacks: 1);
    }

    // ================================================================
    //  The probes
    // ================================================================

    [Fact]
    public void The_probes_index_a_prefixed_original_at_the_header_relative_position()
    {
        byte[] classic = ClassicOriginal(Prefix, countFromByteZero: false);
        long previous = StartxrefValue(classic);

        Assert.Equal(IncrementalUpdateWriter.XrefSectionKind.Classic,
            IncrementalUpdateWriter.ClassifyXrefSectionAt(classic, previous));
        Assert.True(IncrementalUpdateWriter.PointsAtCrossReferenceSection(new MemoryStream(classic), previous));
        // Three bytes before the keyword is inside `endobj` under either reading, so leading white
        // space — which the classifier skips — cannot rescue it.
        Assert.False(IncrementalUpdateWriter.PointsAtCrossReferenceSection(new MemoryStream(classic), previous - 3));

        byte[] stream = XrefStreamOriginal(Prefix);
        Assert.Equal(IncrementalUpdateWriter.XrefSectionKind.Stream,
            IncrementalUpdateWriter.ClassifyXrefSectionAt(stream, StartxrefValue(stream)));
    }

    /// <summary>The legacy reading, for a prefixed original whose <c>startxref</c> is an output position.</summary>
    [Fact]
    public void The_probes_fall_back_to_the_byte_zero_reading_for_a_legacy_prefixed_original()
    {
        byte[] legacy = ClassicOriginal(Prefix, countFromByteZero: true);
        long previous = StartxrefValue(legacy);

        Assert.Equal(IncrementalUpdateWriter.XrefSectionKind.Classic,
            IncrementalUpdateWriter.ClassifyXrefSectionAt(legacy, previous));
        Assert.True(IncrementalUpdateWriter.PointsAtCrossReferenceSection(new MemoryStream(legacy), previous));
    }

    // ================================================================
    //  Shared assertions
    // ================================================================

    /// <summary>
    /// Raw-byte reading of the appended classic revision: its <c>startxref</c>, its entry for the
    /// rewritten object 4 and its <c>/Prev</c> are all offsets from the percent sign at
    /// <paramref name="headerOffset"/>.
    /// </summary>
    private static void AssertRevisionIsCountedFromTheHeader(byte[] original, byte[] produced, int headerOffset, long previous)
    {
        Assert.True(produced.Length > original.Length, "a revision must have been appended");
        for (int i = 0; i < original.Length; i++)
            Assert.True(original[i] == produced[i], "byte " + i + " of the original was rewritten");

        int xrefKeyword = IndexOfAscii(produced, "xref", original.Length);
        Assert.Equal(xrefKeyword - headerOffset, StartxrefValue(produced));

        string tail = Encoding.ASCII.GetString(produced, original.Length, produced.Length - original.Length);
        Assert.Equal(IndexOfAscii(produced, "4 0 obj", original.Length) - headerOffset, ClassicEntryOffset(tail, 4));
        Assert.Equal(previous, PrevValue(tail));
    }

    /// <summary>
    /// The parser's reading of the whole output: nothing skipped, the number of sections it could
    /// find only at the byte-0 reading, the rewritten object 4 and an object only revision 1 defines.
    /// </summary>
    private static void AssertChainReadsAsWritten(byte[] produced, int expectedMarker, int expectedFallbacks)
    {
        var parser = ParseWhole(produced);
        Assert.Null(PdfParser.LastXrefSectionError);
        Assert.Equal(expectedFallbacks, parser.SectionsReadFromByteZero);

        Assert.Equal(expectedMarker, MarkerOf(parser.GetObject(4)));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    private static PdfParser ParseWhole(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseCrossReferences();
        return parser;
    }

    private static long MarkerOf(PdfObject obj)
    {
        var dict = Assert.IsType<PdfDictionary>(obj);
        return Assert.IsType<PdfNumber>(dict[new PdfName("Marker")]).LongValue;
    }

    // ================================================================
    //  Writing
    // ================================================================

    private static IndirectObject Marker(int objectNumber, int marker)
    {
        var dict = new PdfDictionary();
        dict.Set(new PdfName("Marker"), new PdfNumber(marker));
        return new IndirectObject(objectNumber, 0, dict);
    }

    private static PdfDictionary RootTrailer()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(1, 0));
        return trailer;
    }

    private static byte[] AppendWithSixArguments(byte[] original, long previousXrefOffset)
    {
        using var input = new MemoryStream(original);
        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(input, output, new List<IndirectObject> { Marker(4, 2) },
            RootTrailer(), previousXrefOffset, size: 5);
        return output.ToArray();
    }

    private static byte[] AppendWithOptions(byte[] original, out List<string> reported)
    {
        var messages = new List<string>();
        using var doc = PdfDocument.Open(new MemoryStream(original));
        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(original, output, new List<IndirectObject> { Marker(4, 2) },
            new IncrementalUpdateOptions
            {
                Document = doc,
                OriginalTrailer = doc.Trailer,
                DiagnosticSink = (operation, detail, exception) => messages.Add(detail ?? ""),
            });
        reported = messages;
        return output.ToArray();
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>
    /// <paramref name="prefix"/>, then a classic file: catalog 1, page tree 2, page 3 and
    /// <c>4 0 obj &lt;&lt; /Marker 1 &gt;&gt;</c>, with a trailer <c>/ID</c> so an appended revision
    /// carries it rather than synthesising one from the bytes. Offsets count from the header, or —
    /// the legacy shape — from byte 0 of the buffer, prefix included.
    /// </summary>
    private static byte[] ClassicOriginal(string prefix, bool countFromByteZero, bool header = true)
    {
        var ms = new MemoryStream();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }

        Ascii(prefix);
        long origin = countFromByteZero ? 0 : ms.Position;
        if (header) Ascii("%PDF-1.4\n");

        var positions = new long[5];
        positions[1] = ms.Position; Ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        positions[2] = ms.Position; Ascii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        positions[3] = ms.Position; Ascii("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        positions[4] = ms.Position; Ascii("4 0 obj\n<< /Marker 1 >>\nendobj\n");

        long xref = ms.Position;
        Ascii("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++)
            Ascii((positions[n] - origin).ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Ascii("trailer\n<< /Size 5 /Root 1 0 R /ID " + FileId + " >>\nstartxref\n" +
              (xref - origin).ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// <paramref name="prefix"/>, then the same document written by <see cref="PdfCompressedFileWriter"/>
    /// — a cross-reference stream, with offsets counted from where the writer started, which is the
    /// header.
    /// </summary>
    private static byte[] XrefStreamOriginal(string prefix)
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(2, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        var kids = new PdfArray();
        kids.Add(new PdfReference(3, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));

        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Parent, new PdfReference(2, 0));
        var box = new PdfArray();
        foreach (int v in new[] { 0, 0, 612, 792 }) box.Add(new PdfNumber(v));
        page.Set(PdfName.MediaBox, box);

        var marker = new PdfDictionary();
        marker.Set(new PdfName("Marker"), new PdfNumber(1));

        var trailer = RootTrailer();
        var id = new PdfArray();
        id.Add(new PdfString(new byte[16], true));
        id.Add(new PdfString(new byte[16], true));
        trailer.Set(PdfName.ID, id);

        var ms = new MemoryStream();
        byte[] head = Encoding.ASCII.GetBytes(prefix);
        ms.Write(head, 0, head.Length);
        PdfCompressedFileWriter.Write(ms, new PdfVersion(1, 5),
            new List<(int, PdfObject)> { (1, catalog), (2, pages), (3, page), (4, marker) }, trailer);
        return ms.ToArray();
    }

    /// <summary>The same file with the digits of its final <c>startxref</c> overwritten by zeroes.</summary>
    private static byte[] WithStartxrefZeroed(byte[] pdf)
    {
        byte[] copy = (byte[])pdf.Clone();
        int start = LastIndexOfAscii(copy, "startxref", copy.Length) + "startxref".Length;
        while (copy[start] == (byte)'\n' || copy[start] == (byte)'\r') start++;
        while (start < copy.Length && copy[start] >= (byte)'0' && copy[start] <= (byte)'9') copy[start++] = (byte)'0';
        return copy;
    }

    private static byte[] Strip(int count, byte[] bytes)
    {
        var rest = new byte[bytes.Length - count];
        Buffer.BlockCopy(bytes, count, rest, 0, rest.Length);
        return rest;
    }

    // ================================================================
    //  Raw-byte reading
    // ================================================================

    private static int IndexOfAscii(byte[] haystack, string needle, int from)
    {
        for (int i = Math.Max(0, from); i + needle.Length <= haystack.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == (byte)needle[j]) j++;
            if (j == needle.Length) return i;
        }
        Assert.Fail("'" + needle + "' not found from byte " + from);
        return -1;
    }

    private static int LastIndexOfAscii(byte[] haystack, string needle, int before)
    {
        for (int i = Math.Min(before, haystack.Length) - needle.Length; i >= 0; i--)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == (byte)needle[j]) j++;
            if (j == needle.Length) return i;
        }
        Assert.Fail("'" + needle + "' not found before byte " + before);
        return -1;
    }

    /// <summary>The operand of the last <c>startxref</c> in <paramref name="pdf"/>.</summary>
    private static long StartxrefValue(byte[] pdf)
    {
        int at = LastIndexOfAscii(pdf, "startxref", pdf.Length) + "startxref".Length;
        while (pdf[at] == (byte)'\n' || pdf[at] == (byte)'\r' || pdf[at] == (byte)' ') at++;
        int end = at;
        while (end < pdf.Length && pdf[end] >= (byte)'0' && pdf[end] <= (byte)'9') end++;
        Assert.True(end > at, "startxref carries no operand");
        return long.Parse(Encoding.ASCII.GetString(pdf, at, end - at), CultureInfo.InvariantCulture);
    }

    /// <summary>The <c>/Prev</c> value in an appended classic revision's trailer.</summary>
    private static long PrevValue(string appended)
    {
        int at = appended.IndexOf("/Prev", StringComparison.Ordinal);
        Assert.True(at >= 0, "the appended trailer carries no /Prev");
        at += "/Prev".Length;
        while (appended[at] == ' ') at++;
        int end = at;
        while (end < appended.Length && char.IsDigit(appended[end])) end++;
        return long.Parse(appended.Substring(at, end - at), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The 10-digit offset the appended classic table records for <paramref name="objectNumber"/>,
    /// read from the subsection lines (§7.5.4) rather than through the parser.
    /// </summary>
    private static long ClassicEntryOffset(string appended, int objectNumber)
    {
        int at = appended.IndexOf("xref", StringComparison.Ordinal);
        Assert.True(at >= 0, "the appended revision carries no xref keyword");
        at = appended.IndexOf('\n', at) + 1;

        while (at < appended.Length)
        {
            int lineEnd = appended.IndexOf('\n', at);
            string line = appended.Substring(at, lineEnd - at).Trim();
            if (line == "trailer" || line.Length == 0) break;
            string[] header = line.Split(' ');
            int first = int.Parse(header[0], CultureInfo.InvariantCulture);
            int count = int.Parse(header[1], CultureInfo.InvariantCulture);
            at = lineEnd + 1;
            for (int i = 0; i < count; i++, at += 20)
            {
                if (first + i == objectNumber)
                    return long.Parse(appended.Substring(at, 10), CultureInfo.InvariantCulture);
            }
        }

        Assert.Fail("the appended table has no entry for object " + objectNumber);
        return -1;
    }
}
