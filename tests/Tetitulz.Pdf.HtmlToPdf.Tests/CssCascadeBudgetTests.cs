// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The CSS cascade and custom-property substitution are bounded by a per-conversion character budget,
/// because each is a product of two counts the markup chooses. A rule's megabyte-long value matched by
/// ten thousand elements wrote twenty gigabytes of style; a chain where each custom property names the
/// previous one twice doubled to a gigabyte in under a kilobyte of input (the "billion laughs" of CSS
/// Variables 1). The budgets are lowered here so the fixtures stay small.
/// </summary>
[Collection("HtmlMapperLimits")]
public class CssCascadeBudgetTests
{
    private static Document Model(string html, List<string>? reports = null) =>
        HtmlToBuilderMapper.MapToModel(html, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });

    private static List<TextRun> Runs(Document model) =>
        model.Sections[0].Children.OfType<Paragraph>().SelectMany(p => p.Runs).OfType<TextRun>().ToList();

    private static bool FoldReported(List<string> reports) =>
        reports.Any(r => r.Contains("folding budget", StringComparison.Ordinal));

    private static bool VarReported(List<string> reports) =>
        reports.Any(r => r.Contains("custom-property substitution exceeded", StringComparison.Ordinal));

    private static bool CostReported(List<string> reports) =>
        reports.Any(r => r.Contains("cost too much to match", StringComparison.Ordinal));

    private static string NestedDivs(string style, int depth, string inner)
    {
        var sb = new StringBuilder("<style>").Append(style).Append("</style>");
        for (int i = 0; i < depth; i++) sb.Append("<div>");
        sb.Append(inner);
        for (int i = 0; i < depth; i++) sb.Append("</div>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ selector match cost

    /// <summary>
    /// A selector whose estimated match cost — its combinators times the document's size and depth —
    /// exceeds the budget is skipped, reported once (HtmlToPdf-1:5). The compound cap bounds the
    /// selector's shape, not its cost: AngleSharp walks a descendant combinator up every matched
    /// element's ancestors. With the budget lowered, <c>div div</c> over forty nested divs (40×1×40 =
    /// 1600 &gt; 1000) is not applied and its text is not bold.
    /// </summary>
    [Fact]
    public void AnExpensiveSelector_IsSkippedAtTheMatchWorkBudget_AndReportedOnce()
    {
        long saved = HtmlToBuilderMapper.MaxSelectorMatchWork;
        try
        {
            HtmlToBuilderMapper.MaxSelectorMatchWork = 1000;
            var reports = new List<string>();
            var runs = Runs(Model(NestedDivs("div div{font-weight:bold}", 40, "deep"), reports));

            Assert.False(Assert.Single(runs).IsBold);   // the rule was not applied
            Assert.True(CostReported(reports));
            Assert.Equal(1, reports.FindAll(r => r.Contains("cost too much to match", StringComparison.Ordinal)).Count);
        }
        finally { HtmlToBuilderMapper.MaxSelectorMatchWork = saved; }
    }

    /// <summary>
    /// The control: under the default budget the same selector is applied and reports nothing — the
    /// gate discriminates by cost, it is not an always-skip.
    /// </summary>
    [Fact]
    public void TheSameSelectorUnderTheDefaultBudget_IsApplied()
    {
        var reports = new List<string>();
        var runs = Runs(Model(NestedDivs("div div{font-weight:bold}", 40, "deep"), reports));

        Assert.True(Assert.Single(runs).IsBold);
        Assert.False(CostReported(reports));
    }

    /// <summary>
    /// At real scale: a 32-compound descendant selector over a document nesting 25 000 deep used to
    /// backtrack for tens of billions of steps and hang the whole conversion (the compound cap let a
    /// 32-compound selector through). The match-cost gate skips it before the DOM query, so the
    /// conversion finishes. Run on a dedicated thread: a regression that dropped the gate fails the
    /// join in a minute rather than hanging the test host indefinitely.
    /// </summary>
    [Fact]
    public void A32CompoundSelectorOverADeepDocument_DoesNotHang()
    {
        var sb = new StringBuilder("<style>.none");
        for (int i = 0; i < 31; i++) sb.Append(" div");
        sb.Append("{color:red}</style>");
        for (int i = 0; i < 25000; i++) sb.Append("<div>");
        sb.Append("x");
        for (int i = 0; i < 25000; i++) sb.Append("</div>");
        string html = sb.ToString();

        var reports = new List<string>();
        Document? model = null;
        var worker = new Thread(() => model = Model(html, reports), 32 * 1024 * 1024) { IsBackground = true };
        worker.Start();
        // Ten minutes, not one: the evidence of the gate is CostReported below, and the join only
        // tells a hang from a slow run. Under coverage instrumentation beside the other assemblies
        // the gated conversion itself can pass 60 s; an ungated one never returns.
        bool finished = worker.Join(TimeSpan.FromMinutes(10));

        Assert.True(finished, "the cascade did not finish within 10 minutes: the selector matched unbounded");
        Assert.NotNull(model);
        Assert.True(CostReported(reports));
    }

    // ------------------------------------------------------------------ the cascade fold

    /// <summary>
    /// A rule matched past the fold budget stops being applied: with the budget lowered, one rule that
    /// bolds every paragraph reaches the first paragraphs and not the last, the drop reported once.
    /// </summary>
    [Fact]
    public void AdversarialCascadeFold_StopsAtTheBudget_AndIsReportedOnce()
    {
        long saved = HtmlToBuilderMapper.MaxCssCascadeChars;
        try
        {
            // "font-weight:bold" is 16 chars; a 2000-char budget folds it onto ~125 paragraphs.
            HtmlToBuilderMapper.MaxCssCascadeChars = 2000;
            var sb = new StringBuilder("<html><head><style>p{font-weight:bold}</style></head><body>");
            for (int i = 0; i < 1000; i++) sb.Append("<p>t").Append(i).Append("</p>");
            sb.Append("</body></html>");

            var reports = new List<string>();
            var runs = Runs(Model(sb.ToString(), reports));

            Assert.Equal(1000, runs.Count);
            Assert.True(runs[0].IsBold);
            Assert.False(runs[999].IsBold);
            Assert.True(FoldReported(reports));
            Assert.Equal(1, reports.FindAll(r => r.Contains("folding budget", StringComparison.Ordinal)).Count);
        }
        finally { HtmlToBuilderMapper.MaxCssCascadeChars = saved; }
    }

    /// <summary>The control: the same document under the default budget bolds every paragraph and reports nothing.</summary>
    [Fact]
    public void TheSameCascadeUnderTheDefaultBudget_AppliesToEveryElement()
    {
        var sb = new StringBuilder("<html><head><style>p{font-weight:bold}</style></head><body>");
        for (int i = 0; i < 1000; i++) sb.Append("<p>t").Append(i).Append("</p>");
        sb.Append("</body></html>");

        var reports = new List<string>();
        var runs = Runs(Model(sb.ToString(), reports));

        Assert.All(runs, r => Assert.True(r.IsBold));
        Assert.False(FoldReported(reports));
    }

    // ------------------------------------------------------------------ var() substitution

    /// <summary>
    /// A chain where each custom property names the previous one twice doubles; with the budget lowered,
    /// the conversion completes instead of filling memory, the overrun reported once, and the element
    /// after it is still laid out. The chain is one element's inline style, which the substitution reads
    /// in source order (a stylesheet's <c>:root</c> declarations come out of the cascade sort unordered).
    /// </summary>
    [Fact]
    public void AdversarialVarDoubling_StopsAtTheBudget_AndIsReportedOnce()
    {
        long saved = HtmlToBuilderMapper.MaxCssVarExpansionChars;
        try
        {
            HtmlToBuilderMapper.MaxCssVarExpansionChars = 4096;
            var sb = new StringBuilder("<html><body><div style=\"--a0:0123456789abcdef;");
            for (int k = 1; k <= 20; k++)
                sb.Append("--a").Append(k).Append(":var(--a").Append(k - 1).Append(")var(--a").Append(k - 1).Append(");");
            sb.Append("color:var(--a20)\">done</div></body></html>");

            var reports = new List<string>();
            var runs = Runs(Model(sb.ToString(), reports));

            Assert.Equal("done", Assert.Single(runs).Text);
            Assert.True(VarReported(reports));
            Assert.Equal(1, reports.FindAll(r => r.Contains("custom-property substitution exceeded", StringComparison.Ordinal)).Count);
        }
        finally { HtmlToBuilderMapper.MaxCssVarExpansionChars = saved; }
    }

    /// <summary>
    /// The control: a var() within the budget is substituted and reports nothing — the paragraph's colour
    /// comes from the custom property, so a fix that always refused would lose it.
    /// </summary>
    [Fact]
    public void AVarWithinTheBudget_IsSubstituted_AndReportsNothing()
    {
        long saved = HtmlToBuilderMapper.MaxCssVarExpansionChars;
        try
        {
            HtmlToBuilderMapper.MaxCssVarExpansionChars = 4096;
            var reports = new List<string>();
            var runs = Runs(Model(
                "<html><body><div style=\"--w:bold;font-weight:var(--w)\">text</div></body></html>", reports));

            Assert.True(Assert.Single(runs).IsBold);
            Assert.False(VarReported(reports));
        }
        finally { HtmlToBuilderMapper.MaxCssVarExpansionChars = saved; }
    }
}
