// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Searching pointed Hebrew — <c>PdfTextSearch</c>'s <see cref="TextSearchOptions"/> overloads.
/// </summary>
/// <remarks>
/// <para><b>The everyday failure this closes:</b> a pointed document — liturgy, scripture, a
/// children's book, anything vowelled — extracts with its niqqud present, so a query typed the way
/// a person types it finds nothing. Not a wrong answer; no answer.</para>
///
/// <para><b>Every case asserts the hit's coordinates as well as its existence.</b> Matching happens
/// in folded space, and folding shortens the string, so a hit reported at the folded offset would
/// point into the wrong part of the page. <c>TextSearchHit.CharOffset</c> is documented as an index
/// into the extracted text and <c>MatchText</c> as the substring as it appears there — a fold that
/// satisfied the matcher and broke those two would look entirely correct from a "did it find it"
/// test.</para>
/// </remarks>
public class HebrewSearchFoldingTests
{
    private const string PlainShalom = "שלום";
    private const string PointedShalom = "שָׁלוֹם";

    /// <summary>The claim: an unpointed query finds a pointed document.</summary>
    [Fact]
    public void AnUnpointedQuery_FindsPointedText()
    {
        using var pdf = new MemoryStream(PageShowing(PointedShalom));

        var hits = PdfTextSearch.Search(pdf, PlainShalom,
            new TextSearchOptions { HebrewFolding = HebrewFolding.Default });

        Assert.Single(hits);
    }

    /// <summary>
    /// <b>A hit's offset comes from the index map even when folding removed characters
    /// <i>before</i> the match.</b>
    /// </summary>
    /// <remarks>
    /// <para>This is the case that separates the map from the folded index, and the existing
    /// coordinate test does not reach it: its fixture puts an <i>unpointed</i> prefix before the
    /// match, so nothing is removed ahead of it and the two numbers coincide. A mutation replacing
    /// <c>source[idx]</c> with <c>idx</c> survived it.</para>
    ///
    /// <para>Here the prefix is pointed, so folding removes several characters before the match and
    /// the folded index is smaller than the real one. <c>CharOffset</c> indexes the extracted text,
    /// and every rectangle a caller derives from it moves if this is wrong — which for a redactor
    /// means covering the wrong words.</para>
    /// </remarks>
    [Fact]
    public void AnOffsetIsCorrectWhenFoldingRemovedCharactersBeforeTheMatch()
    {
        // Pointed shalom, a space, then plain shalom. The query matches the SECOND word, and the
        // first contributes the removed points that shift the folded index.
        string page = PointedShalom + " " + PlainShalom;
        using var pdf = new MemoryStream(PageShowing(page));

        var hit = PdfTextSearch.Search(pdf, PlainShalom,
            new TextSearchOptions { HebrewFolding = HebrewFolding.Niqqud }).Last();

        // The extracted text is the page's own; the offset must index into it.
        Assert.Equal(PlainShalom, hit.MatchText);
        Assert.True(hit.CharOffset > PlainShalom.Length,
            $"offset {hit.CharOffset} looks like a folded index, not one into the original text");
    }

    /// <summary>
    /// <b><c>Contains</c> and <c>Search</c> answer the same question, including for a query that
    /// folds away to nothing.</b>
    /// </summary>
    /// <remarks>
    /// <para>A query written entirely of niqqud survives the empty-string check — it has characters
    /// — and then folds to <c>""</c>. <c>IndexOf("")</c> is 0, so <c>Contains</c> answered
    /// <b>true for every document with any text at all</b>, while <c>Search</c>, which guards the
    /// folded length, returned no hits. Two methods documented as answering one question, answering
    /// it oppositely.</para>
    ///
    /// <para><b>Both halves are asserted together on purpose</b>, because the claim is that the two
    /// agree: pinning either alone leaves the divergence reachable from the other side. And
    /// <c>Contains</c> is the cheap pre-check a caller runs before the expensive one, so a caller
    /// told "yes" and then handed an empty list has no way to tell that from a bug of its own.</para>
    ///
    /// <para>Such a query is not contrived on this path: it is what concatenating user input with
    /// diacritics produces when the base letters are dropped.</para>
    /// </remarks>
    [Fact]
    public void AQueryThatFoldsToNothingIsFoundByNeitherContainsNorSearch()
    {
        // Two combining points and nothing else: sheva and hiriq, both inside U+0591–U+05C7.
        const string OnlyNiqqud = "ְִ";
        var options = new TextSearchOptions { HebrewFolding = HebrewFolding.Niqqud };

        using var forContains = new MemoryStream(PageShowing(PointedShalom));
        using var forSearch = new MemoryStream(PageShowing(PointedShalom));

        Assert.False(PdfTextSearch.Contains(forContains, OnlyNiqqud, options));
        Assert.Empty(PdfTextSearch.Search(forSearch, OnlyNiqqud, options));
    }

    /// <summary>
    /// The control: with a query that does survive folding, the two still agree — and agree on
    /// <i>yes</i>. Without this the test above passes for an implementation that never matches.
    /// </summary>
    [Fact]
    public void ContainsAndSearchAgreeOnAQueryThatDoesMatch()
    {
        var options = new TextSearchOptions { HebrewFolding = HebrewFolding.Default };

        using var forContains = new MemoryStream(PageShowing(PointedShalom));
        using var forSearch = new MemoryStream(PageShowing(PointedShalom));

        Assert.True(PdfTextSearch.Contains(forContains, PlainShalom, options));
        Assert.Single(PdfTextSearch.Search(forSearch, PlainShalom, options));
    }

    /// <summary>
    /// The control that makes it mean something: without folding the same query finds nothing, so
    /// the hit above is produced by the normalisation and not by the extractor happening to drop
    /// the points.
    /// </summary>
    [Fact]
    public void WithoutFolding_TheSameQueryFindsNothing()
    {
        using var pdf = new MemoryStream(PageShowing(PointedShalom));

        Assert.Empty(PdfTextSearch.Search(pdf, PlainShalom, new TextSearchOptions()));
    }

    /// <summary>
    /// <b>The hit is reported in the document's own spelling, not the folded one.</b> A caller
    /// showing <c>MatchText</c> to a user must show what the page says — pointed — and a caller
    /// using <c>CharOffset</c> to locate the match must land on the first character of the real
    /// word.
    /// </summary>
    [Fact]
    public void TheHitIsReportedInTheOriginalTextsCoordinates()
    {
        using var pdf = new MemoryStream(PageShowing("א " + PointedShalom));

        var hit = PdfTextSearch.Search(pdf, PlainShalom,
            new TextSearchOptions { HebrewFolding = HebrewFolding.Default }).Single();

        // MatchText is the POINTED substring — longer than the query that found it.
        Assert.Equal(PointedShalom, hit.MatchText);
        Assert.True(hit.MatchText.Length > PlainShalom.Length,
            "the match was reported in folded form, so the points were lost");

        // CharOffset indexes the extracted text: "א " is two characters, so the word starts at 2.
        Assert.Equal(2, hit.CharOffset);
    }

    /// <summary>
    /// Latin search is byte-for-byte what it always was, through the new overload. The folding path
    /// must not become a second, subtly different matcher for the 99% of documents that have no
    /// Hebrew in them at all.
    /// </summary>
    [Fact]
    public void LatinSearchIsUnchangedByTheFoldingPath()
    {
        byte[] page = PageShowing("Hello world");

        using var a = new MemoryStream(page);
        using var b = new MemoryStream(page);

        var withFolding = PdfTextSearch.Search(a, "world",
            new TextSearchOptions { HebrewFolding = HebrewFolding.Default }).Single();
        var without = PdfTextSearch.Search(b, "world", ignoreCase: true, contextChars: 40).Single();

        Assert.Equal(without.CharOffset, withFolding.CharOffset);
        Assert.Equal(without.MatchText, withFolding.MatchText);
        Assert.Equal(without.ContextSnippet, withFolding.ContextSnippet);
    }

    /// <summary>An acronym typeset with gershayim is found by one typed with an ASCII quote.</summary>
    [Fact]
    public void AnAsciiQuoteFindsATypographicGershayim()
    {
        using var pdf = new MemoryStream(PageShowing("צה״ל"));

        var hits = PdfTextSearch.Search(pdf, "צה\"ל",
            new TextSearchOptions { HebrewFolding = HebrewFolding.Default });

        Assert.Single(hits);
        Assert.Equal("צה״ל", hits[0].MatchText);
    }

    /// <summary><c>Contains</c> folds on the same terms as <c>Search</c>.</summary>
    [Fact]
    public void ContainsFoldsToo()
    {
        using var yes = new MemoryStream(PageShowing(PointedShalom));
        using var no = new MemoryStream(PageShowing(PointedShalom));

        Assert.True(PdfTextSearch.Contains(yes, PlainShalom,
            new TextSearchOptions { HebrewFolding = HebrewFolding.Default }));
        Assert.False(PdfTextSearch.Contains(no, PlainShalom, new TextSearchOptions()));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page PDF whose content stream shows <paramref name="text"/> through a Type0 font with
    /// an identity <c>/ToUnicode</c>, so the extractor reads back exactly these code points —
    /// including the niqqud, which is the whole point of the fixture.
    /// </summary>
    /// <remarks>
    /// <para><b>The glyphs go into the content stream in VISUAL order, and getting that wrong is
    /// the trap this fixture exists to avoid.</b> A PDF stores glyphs in the order they are painted
    /// — for Hebrew, right to left — and <c>PdfTextExtractor</c> applies bidi reordering to hand
    /// back <i>logical</i> order, which is what a person types and therefore what a search must
    /// match. A fixture that writes logical order into the stream gets it reordered on the way out
    /// and extracts <b>reversed</b>: the first draft of this file did exactly that, and every
    /// folding assertion failed for a reason that had nothing to do with folding.</para>
    ///
    /// <para>The two controls in this file could not tell the difference on their own — "an
    /// unfolded search finds nothing" is equally true of a broken fixture — which is why the
    /// diagnosis came from reading the extracted code points rather than from the suite.</para>
    /// </remarks>
    private static byte[] PageShowing(string text)
    {
        // Reversed only for Hebrew: see the remarks. A pure-RTL run's visual order is its logical
        // order backwards, which is all these fixtures need — none mixes scripts within a line.
        // Reversing the Latin control would be the same mistake in the other direction.
        bool rtl = text.Any(c => c >= '֐' && c <= '׿');
        string visual = rtl ? new string(text.Reverse().ToArray()) : text;

        var hex = new StringBuilder();
        foreach (char c in visual) hex.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));

        string content = "BT /F1 12 Tf 50 700 Td <" + hex + "> Tj ET";

        var toUnicode = new StringBuilder();
        toUnicode.Append("/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n");
        toUnicode.Append("1 begincodespacerange <0000> <FFFF> endcodespacerange\n");
        toUnicode.Append(text.Length).Append(" beginbfchar\n");
        foreach (char c in text)
            toUnicode.Append('<').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture))
                     .Append("> <").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture))
                     .Append(">\n");
        toUnicode.Append("endbfchar endcmap CMapName currentdict /CMap defineresource pop end end");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H " +
                "/DescendantFonts [6 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                "/FontDescriptor 8 0 R /DW 1000 >>",
            Stream(toUnicode.ToString()),
            "<< /Type /FontDescriptor /FontName /Test /Flags 4 /ItalicAngle 0 " +
                "/Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 " +
                "/FontBBox [0 -200 1000 800] >>");
    }

    private static string Stream(string data)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(data);
        return "<< /Length " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
               " >>\nstream\n" + data + "\nendstream";
    }

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
