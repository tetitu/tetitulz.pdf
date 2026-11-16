// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// One validation's checks share each page's scan and its parse (<see cref="ScanReuse"/>). The
/// answers cannot show it, so these assert its liveness, and the two rules that keep it from
/// changing an answer: a cached scan is handed out only where the walk would have completed within
/// the caller's document ceiling, and the cache holds a bounded amount.
/// </summary>
public sealed class ScanReuseTests
{
    [Fact]
    public void A_pdfa_validation_reuses_its_page_scans_and_their_parse()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        using (ScanReuse.Begin())
        {
            var result = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);

            Assert.NotEmpty(result.Violations);   // the fixture is no PDF/A file: the checks ran
            Assert.True(ScanReuse.Current.ScanHits > 0, "no check was handed an earlier check's scan");
            Assert.True(ScanReuse.Current.ParseHits > 0, "no check reused an earlier check's parse");
        }
        Assert.Null(ScanReuse.Current);
    }

    [Fact]
    public void The_pdfa_and_pdfx_validators_open_a_scope_of_their_own()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        // A scope opened and closed with nothing in it leaves zero; only a validator's own scope,
        // with no other open around the call, can leave more.
        using (ScanReuse.Begin()) { }
        Assert.Equal(0, ScanReuse.ClosedScanHits);

        PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);
        Assert.True(ScanReuse.ClosedScanHits > 0, "PDF/A validated without sharing its page scans");

        using (ScanReuse.Begin()) { }
        PdfXValidator.Validate(new MemoryStream(pdf), PdfXConformanceLevel.X4);
        Assert.True(ScanReuse.ClosedScanHits > 0, "PDF/X validated without sharing its page scans");
    }

    /// <summary>
    /// The PDF/UA-1 checker's painted fonts (UA21) and painted codes (UA57) walk every page through
    /// the same scan; until 2026-10-21 it opened no scope, so each walked and parsed every page
    /// again (72.5 MB a validation of a thousand-page text document, 57.0 MB with the scope). The
    /// same evidence as the PDF/A and PDF/X validators', with the same empty-scope control.
    /// </summary>
    [Fact]
    public void The_pdfua_checker_opens_a_scope_of_its_own()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        using (ScanReuse.Begin()) { }
        Assert.Equal(0, ScanReuse.ClosedScanHits);

        PdfUaChecker.Validate(new MemoryStream(pdf));
        Assert.True(ScanReuse.ClosedScanHits > 0, "PDF/UA validated without sharing its page scans");
    }

    [Fact]
    public void A_validation_leaves_no_scope_open_and_a_joined_scope_is_not_closed_by_the_inner_one()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(1);

        PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);
        Assert.Null(ScanReuse.Current);

        using (ScanReuse.Begin())
        {
            var outer = ScanReuse.Current;
            PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2);
            Assert.Same(outer, ScanReuse.Current);
        }
        Assert.Null(ScanReuse.Current);
    }

    [Fact]
    public void A_cached_scan_is_handed_out_only_where_the_walk_would_have_completed()
    {
        var page = new PdfDictionary();
        var scan = new PdfXValidator.PageScan();

        using (ScanReuse.Begin())
        {
            var reuse = ScanReuse.Current;
            reuse.Keep(page, 500, 1000, true, false, scan, bytes: 100);

            long total = 950;   // 950 + 100 passes the ceiling: the walk would have stopped part-way
            Assert.False(reuse.TryGet(page, 500, ref total, 1000, true, false, out _));
            Assert.Equal(950, total);

            total = 1000;       // at the ceiling the walk refuses before it starts
            Assert.False(reuse.TryGet(page, 500, ref total, 1000, true, false, out _));

            total = 900;        // 900 + 100 reaches it exactly, as the walk's own sum would
            Assert.True(reuse.TryGet(page, 500, ref total, 1000, true, false, out var reused));
            Assert.Same(scan, reused);
            Assert.Equal(1000, total);

            total = 0;          // other parameters are another scan
            Assert.False(reuse.TryGet(page, 400, ref total, 1000, true, false, out _));
            Assert.False(reuse.TryGet(page, 500, ref total, 1000, true, true, out _));
            Assert.False(reuse.TryGet(page, 500, ref total, 1000, false, false, out _));
            Assert.False(reuse.TryGet(new PdfDictionary(), 500, ref total, 1000, true, false, out _));
        }
    }

    [Fact]
    public void An_incomplete_scan_and_a_scan_past_the_cache_size_are_not_kept()
    {
        var page = new PdfDictionary();
        var incomplete = new PdfXValidator.PageScan { Incomplete = "a ceiling" };
        var large = new PdfXValidator.PageScan();

        using (ScanReuse.Begin())
        {
            var reuse = ScanReuse.Current;
            reuse.Keep(page, 500, long.MaxValue, true, false, incomplete, bytes: 10);
            long total = 0;
            Assert.False(reuse.TryGet(page, 500, ref total, long.MaxValue, true, false, out _));

            reuse.Keep(page, 600, long.MaxValue, true, false, large, bytes: ScanReuse.MaxCachedContentBytes + 1);
            Assert.False(reuse.TryGet(page, 600, ref total, long.MaxValue, true, false, out _));

            // The control: the same kind of scan at the size limit is kept.
            reuse.Keep(page, 700, long.MaxValue, true, false, large, bytes: ScanReuse.MaxCachedContentBytes);
            Assert.True(reuse.TryGet(page, 700, ref total, long.MaxValue, true, false, out _));
        }
    }

    [Fact]
    public void A_kept_scans_content_is_parsed_once_a_scope_and_anything_else_every_time()
    {
        byte[] content = System.Text.Encoding.ASCII.GetBytes("0 0 m 10 10 l S");
        byte[] other = System.Text.Encoding.ASCII.GetBytes("0 0 m 10 10 l S");
        var scan = new PdfXValidator.PageScan();
        scan.ContentStreams.Add(content);

        Assert.NotSame(ScanReuse.Parse(content), ScanReuse.Parse(content));   // no scope

        using (ScanReuse.Begin())
        {
            ScanReuse.Current.Keep(new PdfDictionary(), 500, long.MaxValue, true, false, scan, bytes: content.Length);

            var first = ScanReuse.Parse(content);
            Assert.Equal(3, first.Count);
            Assert.Same(first, ScanReuse.Parse(content));
            Assert.Equal(1, ScanReuse.Current.ParseHits);
            Assert.NotSame(ScanReuse.Parse(other), ScanReuse.Parse(other));   // not a kept scan's array
        }
    }

    [Fact]
    public void A_page_content_kept_by_parse_and_keep_is_charged_once_and_bounded()
    {
        byte[] content = Encoding.ASCII.GetBytes("0 0 m 10 10 l S");
        Assert.NotSame(ScanReuse.ParseAndKeep(content), ScanReuse.ParseAndKeep(content));   // no scope

        using (ScanReuse.Begin())
        {
            var reuse = ScanReuse.Current;
            var first = ScanReuse.ParseAndKeep(content);
            Assert.Equal(3, first.Count);
            Assert.Same(first, ScanReuse.ParseAndKeep(content));
            Assert.Same(first, ScanReuse.Parse(content));   // the scans' door reads it too
            Assert.Equal(2, reuse.ParseHits);

            // A scan holding the same array is not charged for it again: asking for the cache's
            // whole size, it fits only because the array's bytes are already counted.
            var scan = new PdfXValidator.PageScan();
            scan.ContentStreams.Add(content);
            var page = new PdfDictionary();
            reuse.Keep(page, 500, long.MaxValue, true, false, scan, bytes: ScanReuse.MaxCachedContentBytes);
            long total = 0;
            Assert.True(reuse.TryGet(page, 500, ref total, long.MaxValue, true, false, out _));

            // The cache is full now, so other content is parsed afresh every time.
            byte[] other = Encoding.ASCII.GetBytes("1 1 m S");
            Assert.NotSame(ScanReuse.ParseAndKeep(other), ScanReuse.ParseAndKeep(other));
        }
    }

    /// <summary>
    /// PDF/UA's marked-content walk (UA02 on every page, UA50 on a tagged one) parses a page's own
    /// content through <see cref="ScanReuse.ParseAndKeep"/>, and the font walks' scans of the same
    /// decoded array reuse it — until 2026-10-21 that walk parsed every page again for each of its
    /// runs. The control is the same document with each <c>/Contents</c> a one-element array, which
    /// the walk concatenates afresh on every run and so does not keep: the difference is one hit a page.
    /// </summary>
    [Fact]
    public void The_pdfua_marked_content_walk_shares_a_single_streams_parse_with_the_font_walk()
    {
        const int Pages = 3;
        byte[] single = MinimalPdfBuilder.CreateMultiPagePdf(Pages);
        string text = Encoding.ASCII.GetString(single);
        // The same length, so every cross-reference offset still holds.
        string arrayed = Regex.Replace(text, @"/Contents (\d+) 0 R ", "/Contents[$1 0 R]");
        Assert.Equal(text.Length, arrayed.Length);
        Assert.NotEqual(text, arrayed);

        (int Hits, string Codes) Validate(byte[] pdf)
        {
            using (ScanReuse.Begin())
            {
                var result = PdfUaChecker.Validate(new MemoryStream(pdf));
                string codes = string.Join(",", result.Violations.Select(x => x.Code));
                return (ScanReuse.Current.ParseHits, codes);
            }
        }

        var shared = Validate(single);
        var control = Validate(Encoding.ASCII.GetBytes(arrayed));
        Assert.Equal(control.Codes, shared.Codes);   // the same answers: both were read
        Assert.Contains("UA01", shared.Codes);
        Assert.Equal(Pages, shared.Hits - control.Hits);
    }
}
