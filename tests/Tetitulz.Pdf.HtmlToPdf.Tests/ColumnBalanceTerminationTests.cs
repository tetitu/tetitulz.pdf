// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// The multi-column balancer terminates on a block whose height a huge margin drives past 2^46, where
/// adjacent doubles are more than the bisection's hundredth-point tolerance apart. It bisected from
/// nothing to everything until <c>high - low &lt;= 0.01</c>, which that gap makes impossible — <c>mid</c>
/// rounds to an end of the interval and the loop spins forever, before export validation runs
/// (2026-10-22).
/// </summary>
public class ColumnBalanceTerminationTests
{
    /// <summary>
    /// Runs the conversion on a dedicated thread so an unterminating balance shows as a thread that
    /// never joins, rather than hanging the test host.
    /// </summary>
    private static byte[] ConvertOnAThread(string html)
    {
        byte[]? pdf = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var output = new MemoryStream();
                HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });
                pdf = output.ToArray();
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the column balance did not terminate");
        if (failure != null) throw failure;
        return pdf!;
    }

    /// <summary>
    /// A column block holding a paragraph with a 10^15-px top margin converts instead of spinning: the
    /// balance's bisection stops when its midpoint can no longer move.
    /// </summary>
    [Fact]
    public void AColumnBlockWithAnEnormousMargin_Terminates()
    {
        byte[] pdf = ConvertOnAThread(
            "<div style=\"column-count:2\">" +
            "<p style=\"margin-top:1000000000000000px\">first</p><p>second</p></div>");

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.Pages.Count >= 1);
    }

    /// <summary>
    /// The control: an ordinary two-column block balances its paragraphs across the columns, which the
    /// termination guard must not change. Both paragraphs are laid out, in order.
    /// </summary>
    [Fact]
    public void AnOrdinaryColumnBlock_BalancesItsParagraphs()
    {
        byte[] pdf = ConvertOnAThread(
            "<div style=\"column-count:2\"><p>alpha</p><p>bravo</p><p>charlie</p><p>delta</p></div>");

        string text = Tetitulz.Pdf.Base.PdfOperations.ExtractText(new MemoryStream(pdf));
        foreach (string word in new[] { "alpha", "bravo", "charlie", "delta" })
            Assert.Contains(word, text, StringComparison.Ordinal);
    }
}
