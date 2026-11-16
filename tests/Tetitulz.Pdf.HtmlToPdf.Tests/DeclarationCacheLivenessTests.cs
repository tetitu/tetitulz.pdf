// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.HtmlToPdf;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The per-element parsed-style cache behind <c>StyleValue</c> is <b>live</b>.
///
/// <para><b>Why liveness needs its own test.</b> The cache is a pure optimisation: an answer served
/// from the map and an answer re-lexed from the <c>style</c> attribute are identical by
/// construction. So every other test in this assembly — all 419 of them — passes with the cache
/// switched off, and none of them can tell. That is the "non-functional behind passing tests" class
/// CLAUDE.md records three times, and the reason <c>CMapCodec.UsesCidRangeIndex</c> exists; this
/// file is the same instrument for the same reason.</para>
///
/// <para><b>The specific way it can die.</b> <c>DeclarationMap</c> validates a cached entry with
/// <c>ReferenceEquals(cached.Source, style)</c>, so that a <c>SetAttribute("style", …)</c> after an
/// element was cached invalidates it. If AngleSharp ever stopped returning the same
/// <see cref="string"/> instance for repeated <c>GetAttribute("style")</c> calls, that check would
/// fail every time, every element would be re-lexed on each of its ~35 lookups, and the only visible
/// symptom would be the allocation profile — 5× worse, measured, and invisible to any assertion
/// about the output.</para>
/// </summary>
public class DeclarationCacheLivenessTests
{
    /// <summary>
    /// A document whose stylesheet the cascade folds onto every element, so each one carries a
    /// multi-declaration <c>style</c> attribute — the routine post-fold state, and the case the
    /// cache exists for.
    /// </summary>
    private static string StyledDocument(int sections)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><style>")
          .Append("h2 { font-size:16pt; color:#113355; margin-top:12pt; border-bottom:1px solid #ccc; }")
          .Append("p  { margin:0 0 6pt 0; padding:0; text-align:justify; line-height:1.4; }")
          .Append("div { border:1px solid #ddd; padding:8pt; background:#fafafa; }")
          .Append("</style></head><body>");
        for (int i = 0; i < sections; i++)
            sb.Append("<h2>Section ").Append(i).Append("</h2>")
              .Append("<div><p>Lorem ipsum dolor sit amet, consectetur adipiscing elit ")
              .Append(i).Append(".</p></div>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Convert(string html) =>
        HtmlToPdfConverter.ConvertHtmlToBytes(
            html, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });

    /// <summary>
    /// Most lookups are served from the cache.
    /// </summary>
    /// <remarks>
    /// The ratio is roughly 35:1 in practice — <c>ApplyBlockStyle</c> alone accounts for about
    /// seventeen lookups on one element, and <c>IsHidden</c>, the four layout-container predicates,
    /// <c>ResolveAlign</c>, <c>ReportEngineLayoutLimits</c> and four <c>HasForcedPageBreak</c> probes
    /// supply the rest. The assertion is deliberately far looser than that: it has to fail when the
    /// cache is dead (where parses <b>equal</b> lookups) without being a tripwire on the exact number
    /// of times some future partial happens to consult an element.
    /// </remarks>
    [Fact]
    public void Most_style_lookups_are_served_from_the_cache()
    {
        Convert(StyledDocument(40));

        int lookups = HtmlToBuilderMapper.DeclarationLookupCount;
        int parses = HtmlToBuilderMapper.DeclarationParseCount;

        Assert.True(lookups > 0, "no style lookups happened at all, so this proves nothing");
        Assert.True(parses * 5 < lookups,
            $"the parsed-style cache is not being hit: {parses} parses for {lookups} lookups. " +
            "With the cache dead the two are equal — check the ReferenceEquals validation in " +
            "DeclarationMap, which fails open if GetAttribute stops returning a stable instance.");
    }

    /// <summary>
    /// No element is parsed twice: the parse count cannot exceed the number of elements.
    /// </summary>
    /// <remarks>
    /// The stronger half of the claim. A cache that thrashed — evicting one element to admit the
    /// next, which a single-entry memo would do the moment a mapper alternated between a container
    /// and its child — could still satisfy the ratio above on a document with long runs, while
    /// re-parsing the same element many times over. Counting parses against the element count rules
    /// that out: a document of ~120 elements must not parse more than ~120 times.
    /// </remarks>
    [Fact]
    public void No_element_is_parsed_more_than_once()
    {
        const int Sections = 40;
        // 3 elements per section (h2, div, p) plus html/head/style/body.
        const int ElementCount = Sections * 3 + 4;

        Convert(StyledDocument(Sections));

        Assert.True(HtmlToBuilderMapper.DeclarationParseCount <= ElementCount,
            $"{HtmlToBuilderMapper.DeclarationParseCount} parses for at most {ElementCount} " +
            "elements, so at least one element was re-lexed — the cache is thrashing rather " +
            "than missing.");
    }

    /// <summary>
    /// The counters describe the conversion that just ran, not an accumulation across calls.
    /// </summary>
    /// <remarks>
    /// They are reset at the <b>start</b> of a conversion and left standing afterwards, which is the
    /// rule CLAUDE.md states for every <c>Last*</c> slot: a value read after an operation must belong
    /// to that operation. Accumulating instead would make the ratio above drift upward with every
    /// test that ran before it on the same thread, so the assertion would weaken silently over time.
    /// </remarks>
    [Fact]
    public void The_counters_describe_only_the_most_recent_conversion()
    {
        Convert(StyledDocument(40));
        int big = HtmlToBuilderMapper.DeclarationLookupCount;

        Convert(StyledDocument(2));
        int small = HtmlToBuilderMapper.DeclarationLookupCount;

        Assert.True(small > 0, "the small conversion recorded no lookups");
        Assert.True(small < big,
            $"a 2-section document recorded {small} lookups against a 40-section document's " +
            $"{big}; the counters are accumulating across conversions rather than being reset.");
    }
}
