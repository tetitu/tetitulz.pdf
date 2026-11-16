// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The CSS readers that recurse into parentheses stop at a depth. A selector's specificity recursed once
/// a nested <c>:not(</c> and the media evaluator once a nested pair, both before any cap looked at the
/// text: twenty-five kilobytes overflowed a one-megabyte stack, which cannot be caught, so the host died
/// rather than getting a bad PDF (2026-10-22).
/// </summary>
public class CssNestingDepthTests
{
    private static string Nest(string open, string inner, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + inner + string.Concat(Enumerable.Repeat(close, depth));

    private static byte[] Convert(string html, List<string>? reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });
        return output.ToArray();
    }

    /// <summary>
    /// A nest of <c>:not(</c> counts its innermost type (Selectors Level 4 §17); fifty thousand deep, the
    /// levels past the cap count nothing, and the reader returns.
    /// </summary>
    [Fact]
    public void AHostileSelectorOfNestedNots_HasASpecificity_WithoutRecursingPastTheCap()
    {
        Assert.Equal(1, HtmlToBuilderMapper.Specificity(Nest(":not(", "p", ")", 4)));
        Assert.Equal(0, HtmlToBuilderMapper.Specificity(Nest(":not(", "p", ")", 50_000)));
    }

    /// <summary>
    /// A media condition nested past the cap is unknown, and unknown applies — the engine's standing
    /// approximation — where inside the cap it is evaluated: ten thousand pixels wide is false at a
    /// 600-pixel viewport seventeen pairs deep, unknown at eighteen and at fifty thousand.
    /// </summary>
    [Fact]
    public void AHostileMediaConditionNestedPastTheCap_IsUnknown_AndOneInsideItIsEvaluated()
    {
        var viewport = new HtmlToBuilderMapper.MediaViewport(600, 800);

        Assert.False(HtmlToBuilderMapper.MediaApplies(Nest("(", "min-width: 10000px", ")", 4), viewport));
        Assert.False(HtmlToBuilderMapper.MediaApplies(Nest("(", "min-width: 10000px", ")", 17), viewport));
        Assert.True(HtmlToBuilderMapper.MediaApplies(Nest("(", "min-width: 10000px", ")", 18), viewport));
        Assert.True(HtmlToBuilderMapper.MediaApplies(Nest("(", "min-width: 10000px", ")", 50_000), viewport));
    }

    /// <summary>
    /// End to end: a style sheet whose rule's selector is <c>:not(</c> nested fifty thousand deep
    /// converts, the rule skipped before AngleSharp's parser is handed it and said so once, and the rule
    /// beside it applies.
    /// </summary>
    [Fact]
    public void AHostileNestedSelectorInAStyleSheet_IsSkippedAndReported_WhileOrdinaryRulesApply()
    {
        string html = "<html><head><style>" + Nest(":not(", "p", ")", 50_000) + "{color:red} " +
                      "p.k{font-weight:bold}</style></head><body><p class=\"k\">bold text</p></body></html>";

        var reports = new List<string>();
        byte[] pdf = Convert(html, reports);

        Assert.Single(reports, r => r.Contains("selector functions", StringComparison.Ordinal));
        Assert.Contains("Helvetica-Bold", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    /// <summary>
    /// End to end: a style element whose <c>media</c> nests fifty thousand pairs deep converts, and the
    /// unknown condition applies its rule, as an unknown one does.
    /// </summary>
    [Fact]
    public void AHostileNestedMediaAttribute_Converts()
    {
        string html = "<html><head><style media=\"" + Nest("(", "min-width: 1px", ")", 50_000) + "\">" +
                      "p{font-weight:bold}</style></head><body><p>bold text</p></body></html>";

        byte[] pdf = Convert(html);

        Assert.Contains("Helvetica-Bold", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }
}
