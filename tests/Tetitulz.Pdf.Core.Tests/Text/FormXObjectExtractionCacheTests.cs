// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="PdfTextExtractor"/> re-parsed a Form XObject's content stream and rebuilt its
/// <see cref="PdfFont"/> map on <b>every</b> <c>Do</c> — the cost the render path had already
/// closed with its own operation cache. <see cref="TextExtractionCache"/> closes it here.
///
/// <para><b>These tests are about the text, not the speed.</b> A memo is only ever a defect
/// when it hands back an answer built for a different question, so the assertions are the
/// extracted strings for the four shapes that can go wrong: one invocation, many invocations of
/// one form, the <i>same</i> form reached through two different resource dictionaries, and a
/// nested form. The last of those is the one the design turns on — a Form XObject with no
/// <c>/Resources</c> of its own inherits the invoker's (§8.10.1), so its fonts are a function of
/// the resource dictionary in scope and <b>not</b> of the form. Keying the font memo on the form
/// would make the second invocation draw the first one's fonts.</para>
///
/// <para>One measurement test guards the win itself, because every other test here passes just
/// as happily with the cache deleted.</para>
/// </summary>
public class FormXObjectExtractionCacheTests
{
    // ------------------------------------------------------------------ extracted text

    [Fact]
    public void FormInvokedOnce_ExtractsTheFormsText()
    {
        byte[] pdf = SelfContainedFormPdf(invocations: 1);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Equal(1, CountOccurrences(text, "XXX"));
        Assert.Null(PdfTextExtractor.LastFormXObjectError);
    }

    [Fact]
    public void SameFormInvoked50Times_ExtractsItsTextOncePerInvocation()
    {
        byte[] pdf = SelfContainedFormPdf(invocations: 50);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        // Memoising the parse must not collapse repeated invocations into one, and must not
        // duplicate them either: 50 Do operators, 50 copies of the form's text.
        Assert.Equal(50, CountOccurrences(text, "XXX"));
        Assert.Null(PdfTextExtractor.LastFormXObjectError);
    }

    [Fact]
    public void SameFormOnTwoPagesWithDifferentResources_UsesEachPagesOwnFont()
    {
        // Object 5 is ONE form with no /Resources of its own, drawn by both pages. Page 1's /F1
        // maps the byte 'A' to 'X'; page 2's maps it to 'Y'. A font memo keyed on the form — or on
        // its content bytes — would give page 2 page 1's answer.
        byte[] pdf = SharedFormTwoPagesPdf();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string page1 = PdfTextExtractor.ExtractText(doc.Pages[0]);
        string page2 = PdfTextExtractor.ExtractText(doc.Pages[1]);

        Assert.Equal("XXX", page1);
        Assert.Equal("YYY", page2);
    }

    [Fact]
    public void SameFormReachedThroughTwoResourceScopesInOneExtraction_UsesEachScopesOwnFont()
    {
        // The same divergence as above, inside a SINGLE ExtractText call — which is the scope the
        // cache actually lives in, so this is the case that can catch a mis-keyed memo. The page
        // draws /Fm directly (page /F1 → 'X') and then draws /Outer, whose own /Resources give a
        // different /F1 ('Y'), and /Outer draws the very same /Fm.
        byte[] pdf = SharedFormTwoScopesOnOnePagePdf();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Equal(1, CountOccurrences(text, "XXX"));
        Assert.Equal(1, CountOccurrences(text, "YYY"));
        Assert.True(text.IndexOf("XXX", StringComparison.Ordinal)
                    < text.IndexOf("YYY", StringComparison.Ordinal),
            "the page's own invocation is drawn before the one inside /Outer; got: " + text);
    }

    [Fact]
    public void NestedForms_ExtractBothLevelsInOrder()
    {
        byte[] pdf = NestedFormPdf();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Contains("Outer", text, StringComparison.Ordinal);
        Assert.Contains("Inner", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Outer", StringComparison.Ordinal)
                    < text.IndexOf("Inner", StringComparison.Ordinal),
            "the outer form shows its own text before invoking the inner one; got: " + text);
    }

    // ------------------------------------------------------------------ the win itself

    [Fact]
    public void ManyInvocationsOfOneForm_DoNotPayForItsParseAndFontsPerInvocation()
    {
        // Every other test in this file passes with the cache deleted, so this is the one that
        // holds the row closed. ALLOCATION rather than a wall clock: bytes allocated on this
        // thread do not move with machine load, and CLAUDE.md's flakiness rules are about elapsed
        // time. The claim is the MARGINAL cost of one extra Do, which isolates the per-invocation
        // work from everything fixed (opening the document, the page's own content, the answer
        // string).
        //
        // Measured on this fixture: 45.6 KB per invocation before the cache, 2.05 KB after — the
        // form's ~110-byte content stream re-tokenised and its 100-entry /ToUnicode re-parsed each
        // time. The 16 KB ceiling sits ~8x below the old figure and ~8x above the new one.
        byte[] small = MeasurableFormPdf(invocations: 10);
        byte[] large = MeasurableFormPdf(invocations: 200);

        // Warm-up: JIT, the static encoding tables, and PdfStream's own decode memo must not be
        // charged to either measurement.
        Warm(small);
        Warm(large);

        long smallBytes = AllocationOfOneExtraction(small);
        long largeBytes = AllocationOfOneExtraction(large);

        long marginalPerInvocation = (largeBytes - smallBytes) / 190;

        Assert.True(marginalPerInvocation > 0,
            "the fixture must actually cost something per invocation, or the ceiling below is vacuous");
        Assert.True(marginalPerInvocation < 16 * 1024,
            $"each extra Do of the same form cost {marginalPerInvocation} bytes: the parse and the " +
            "font load are being redone per invocation.");
    }

    private static void Warm(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        for (int i = 0; i < 3; i++) PdfTextExtractor.ExtractText(doc.Pages[0]);
    }

    private static long AllocationOfOneExtraction(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var page = doc.Pages[0];
        PdfTextExtractor.ExtractText(page);   // pay the decode memo outside the measurement

        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = PdfTextExtractor.ExtractText(page);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0, text.Length);      // a fixture that extracts nothing measures nothing
        return allocated;
    }

    // ------------------------------------------------------------------ the cache itself

    [Fact]
    public void GetOrParse_SameArrayInstance_HandsBackTheSameParsedList()
    {
        var cache = new TextExtractionCache();
        byte[] data = Encoding.ASCII.GetBytes("BT (A) Tj ET\n");

        var first = cache.GetOrParse(data);
        var second = cache.GetOrParse(data);

        Assert.Same(first, second);
        Assert.Equal(1, cache.OperationEntryCount);
        Assert.True(cache.ChargedOperationBytes > 0);
    }

    /// <summary>
    /// The ceiling admits a graph charged at exactly <see cref="TextExtractionCache.MaxOperationBytes"/>
    /// and refuses one 48 bytes over. A <c>q</c> is charged 48 and a <c>1 w</c> 80, and 349 522 of the
    /// first with 2 of the second come to 16 MiB to the byte.
    /// </summary>
    [Fact]
    public void GetOrParse_AGraphAtExactlyTheCeiling_IsAdmitted_AndOneOperationMoreIsNot()
    {
        var exact = new TextExtractionCache();
        exact.GetOrParse(Encoding.ASCII.GetBytes(new StringBuilder().Insert(0, "q\n", 349_522).Append("1 w\n1 w\n").ToString()));
        Assert.Equal(TextExtractionCache.MaxOperationBytes, exact.ChargedOperationBytes);
        Assert.Equal(1, exact.OperationEntryCount);

        var over = new TextExtractionCache();
        over.GetOrParse(Encoding.ASCII.GetBytes(new StringBuilder().Insert(0, "q\n", 349_523).Append("1 w\n1 w\n").ToString()));
        Assert.Equal(0, over.OperationEntryCount);
    }

    /// <summary>
    /// A second map offered for a dictionary already held is ignored: the first stays, and the font
    /// count is not charged twice — or a dictionary re-offered often enough would fill the ceiling.
    /// </summary>
    [Fact]
    public void AddFonts_TheSameDictionaryTwice_KeepsTheFirstMap()
    {
        var cache = new TextExtractionCache();
        var resources = new PdfDictionary();
        var first = new Dictionary<string, PdfFont>();
        var second = new Dictionary<string, PdfFont>();
        cache.AddFonts(resources, first);
        cache.AddFonts(resources, second);

        Assert.True(cache.TryGetFonts(resources, out var held));
        Assert.Same(first, held);
        Assert.Equal(1, cache.FontEntryCount);
    }

    [Fact]
    public void GetOrParse_EqualContentInTwoArrays_ParsesEachSeparately()
    {
        // Reference identity, never value equality — hashing megabytes of content to merge two
        // streams that happen to agree would cost more than the parse it saves.
        var cache = new TextExtractionCache();
        byte[] a = Encoding.ASCII.GetBytes("BT (A) Tj ET\n");
        byte[] b = Encoding.ASCII.GetBytes("BT (A) Tj ET\n");

        var first = cache.GetOrParse(a);
        var second = cache.GetOrParse(b);

        Assert.NotSame(first, second);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(2, cache.OperationEntryCount);
    }

    [Fact]
    public void GetOrParse_OversizedOperationGraph_IsRefusedByTheCeilingAndStillParsesCorrectly()
    {
        // Past TextExtractionCache.MaxOperationBytes nothing new is admitted; the caller gets the
        // same correct operations it got before the cache existed, just without the memo.
        var cache = new TextExtractionCache();
        byte[] data = RepeatedOperators("1 2 m\n", 160_000);   // ~17.9 MB of charge, over the 16 MB cap

        var first = cache.GetOrParse(data);
        var second = cache.GetOrParse(data);

        Assert.Equal(160_000, first.Count);
        Assert.Equal(first.Count, second.Count);
        Assert.NotSame(first, second);
        Assert.Equal(0, cache.OperationEntryCount);
        Assert.Equal(0, cache.ChargedOperationBytes);
    }

    [Fact]
    public void AddFonts_PastTheRetainedFontCeiling_StopsAdmittingEntries()
    {
        var cache = new TextExtractionCache();
        var admitted = new List<PdfDictionary>();

        for (int i = 0; i < TextExtractionCache.MaxCachedFonts; i++)
        {
            var resources = new PdfDictionary();
            admitted.Add(resources);
            cache.AddFonts(resources, OneFontMap());
        }

        Assert.Equal(TextExtractionCache.MaxCachedFonts, cache.FontEntryCount);
        Assert.True(cache.TryGetFonts(admitted[0], out _));

        var overflow = new PdfDictionary();
        cache.AddFonts(overflow, OneFontMap());

        Assert.Equal(TextExtractionCache.MaxCachedFonts, cache.FontEntryCount);
        Assert.False(cache.TryGetFonts(overflow, out _));
    }

    [Fact]
    public void TryGetFonts_ForADictionaryNeverAdded_Misses()
    {
        var cache = new TextExtractionCache();
        var a = new PdfDictionary();
        var b = new PdfDictionary();
        cache.AddFonts(a, OneFontMap());

        Assert.True(cache.TryGetFonts(a, out var hit));
        Assert.Single(hit);
        Assert.False(cache.TryGetFonts(b, out _));
        Assert.False(cache.TryGetFonts(null!, out _));
    }

    private static Dictionary<string, PdfFont> OneFontMap() =>
        new Dictionary<string, PdfFont> { ["F1"] = PdfFont.Create(null, null) };

    private static byte[] RepeatedOperators(string op, int count)
    {
        var sb = new StringBuilder(op.Length * count);
        for (int i = 0; i < count; i++) sb.Append(op);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }
        return n;
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>
    /// One page invoking one form <paramref name="invocations"/> times. The form carries its own
    /// /Resources, so its font is the same on every invocation and the text is "XXX" each time.
    /// </summary>
    private static byte[] SelfContainedFormPdf(int invocations)
    {
        var invoke = new StringBuilder();
        for (int i = 0; i < invocations; i++) invoke.Append("/Fm Do ");

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm 5 0 R >> >> >>",
            StreamObject(invoke.ToString()),
            FormObject(ShowThreeAs, "<< /Font << /F1 6 0 R >> >>"),
            FontObject(7),
            StreamObject(ToUnicodeCMap('X')),
        });
    }

    /// <summary>
    /// Two pages drawing the SAME form (object 5, no /Resources of its own) through two different
    /// /Font dictionaries.
    /// </summary>
    private static byte[] SharedFormTwoPagesPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Fm 5 0 R >> /Font << /F1 6 0 R >> >> >>",
        StreamObject("/Fm Do "),
        FormObject(ShowThreeAs, resources: null),
        FontObject(7),
        StreamObject(ToUnicodeCMap('X')),
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 9 0 R " +
            "/Resources << /XObject << /Fm 5 0 R >> /Font << /F1 10 0 R >> >> >>",
        StreamObject("/Fm Do "),
        FontObject(11),
        StreamObject(ToUnicodeCMap('Y')),
    });

    /// <summary>
    /// One page, one form (object 5, no /Resources of its own) reached twice in a single
    /// extraction through two different resource dictionaries: directly from the page, and from
    /// inside /Outer, whose own /Resources name a different /F1.
    /// </summary>
    private static byte[] SharedFormTwoScopesOnOnePagePdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Fm 5 0 R /Outer 8 0 R >> /Font << /F1 6 0 R >> >> >>",
        StreamObject("/Fm Do /Outer Do "),
        FormObject(ShowThreeAs, resources: null),
        FontObject(7),
        StreamObject(ToUnicodeCMap('X')),
        FormObject("/Fm Do ", "<< /XObject << /Fm 5 0 R >> /Font << /F1 9 0 R >> >>"),
        FontObject(10),
        StreamObject(ToUnicodeCMap('Y')),
    });

    /// <summary>Page → /Outer → /Inner, each showing its own literal text.</summary>
    private static byte[] NestedFormPdf() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /XObject << /Outer 5 0 R >> >> >>",
        StreamObject("/Outer Do "),
        FormObject("BT (Outer) Tj ET /Inner Do ", "<< /XObject << /Inner 6 0 R >> >>"),
        FormObject("BT (Inner) Tj ET ", resources: null),
    });

    /// <summary>
    /// The allocation fixture: one form with a 100-entry /ToUnicode and a 256-entry /Widths —
    /// an ordinary embedded-font shape, and the work that used to be redone per invocation.
    /// </summary>
    private static byte[] MeasurableFormPdf(int invocations)
    {
        var invoke = new StringBuilder();
        for (int i = 0; i < invocations; i++) invoke.Append("/Fm Do ");

        var widths = new StringBuilder();
        for (int i = 0; i < 256; i++) widths.Append(500 + (i % 7)).Append(' ');

        var cmap = new StringBuilder();
        cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        cmap.Append("1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
        cmap.Append("100 beginbfchar\n");
        for (int i = 0; i < 100; i++) cmap.AppendFormat("<{0:X2}> <{1:X4}>\n", 32 + i, 32 + i);
        cmap.Append("endbfchar\nendcmap\nend\nend\n");

        return Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Fm 5 0 R >> >> >>",
            StreamObject(invoke.ToString()),
            FormObject("BT /F1 12 Tf 20 700 Td (Hello world from the form) Tj ET ",
                "<< /Font << /F1 6 0 R >> >>"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 0 /LastChar 255 " +
                $"/Widths [{widths}] /ToUnicode 7 0 R >>",
            StreamObject(cmap.ToString()),
        });
    }

    /// <summary>Shows the three bytes 'A' 'A' 'A' through whatever /F1 is in scope.</summary>
    private const string ShowThreeAs = "BT /F1 12 Tf (AAA) Tj ET ";

    /// <summary>A simple font whose /ToUnicode is the object numbered <paramref name="toUnicode"/>.</summary>
    private static string FontObject(int toUnicode) =>
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 65 " +
        $"/Widths [500] /ToUnicode {toUnicode} 0 R >>";

    /// <summary>A /ToUnicode CMap mapping the single byte 'A' (0x41) to <paramref name="to"/>.</summary>
    private static string ToUnicodeCMap(char to) =>
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n" +
        $"1 beginbfchar\n<41> <{(int)to:X4}>\nendbfchar\n" +
        "endcmap\nend\nend\n";

    private static string FormObject(string content, string? resources) =>
        "<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
        (resources == null ? string.Empty : "/Resources " + resources + " ") +
        $"/Length {content.Length} >>\nstream\n{content}\nendstream";

    private static string StreamObject(string content) =>
        $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] Assemble(List<string> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
