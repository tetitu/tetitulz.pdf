// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using AngleSharp;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// The settle window waits for every <c>setTimeout</c> due inside it to have run
/// (<see cref="TimerSettleLog"/>), instead of sleeping its length and racing the thread pool that
/// queues AngleSharp's timer callbacks — the race that, under a whole-solution coverage run on
/// 2026-10-21, laid a page out without its 30 ms timer.
/// </summary>
/// <remarks>
/// The race itself is the thread pool's and no test can schedule it; these assert what can be
/// asserted deterministically: the log's rules, that the binding tells it (the hook's liveness),
/// and the two ways waiting could go wrong — a cleared timer, or one that throws, holding the page
/// open until its deadline. The ten-run theory in <see cref="PageScriptTests"/> is the end-to-end case.
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class TimerSettleTests
{
    [Fact]
    public void ATimerIsDueByItsDelay_AndNoLongerOnceItRanOrWasCleared()
    {
        var log = new TimerSettleLog();
        object soon = log.Scheduled(0);
        object late = log.Scheduled(60_000);

        Assert.True(log.AnyDueBy(TimerSettleLog.After(TimeSpan.FromSeconds(1))));
        Assert.True(log.AnyDueBy(TimerSettleLog.After(TimeSpan.FromMinutes(2))));

        log.Ran(soon);
        Assert.False(log.AnyDueBy(TimerSettleLog.After(TimeSpan.FromSeconds(1))));   // only the late one is left
        Assert.True(log.AnyDueBy(TimerSettleLog.After(TimeSpan.FromMinutes(2))));

        log.Handle(late, 7);
        log.Cleared(8);                                                              // another handle: nothing
        Assert.Equal(1, log.Pending);
        log.Cleared(7);
        Assert.Equal(0, log.Pending);
        Assert.Equal(2, log.Seen);
    }

    [Fact]
    public void AHandleReportedAfterItsTimerRan_IsNotRecorded()
    {
        var log = new TimerSettleLog();
        object token = log.Scheduled(0);
        log.Ran(token);            // a zero-delay timer can run before setTimeout returns its handle
        log.Handle(token, 3);
        log.Cleared(3);
        Assert.Equal(0, log.Pending);
    }

    /// <summary>
    /// The binding's interception, against a real AngleSharp window: <c>setTimeout</c>'s callback is
    /// wrapped so running it clears the record, and <c>clearTimeout</c> clears it by handle; any other
    /// DOM method, and a window whose page has no log, are left alone.
    /// </summary>
    [Fact]
    public async Task Intercept_RecordsSetTimeout_AndItsCallbackOrClearTimeoutEndsTheRecord()
    {
        var log = new TimerSettleLog();
        using IBrowsingContext context = BrowsingContext.New(Configuration.Default.With(log));
        IDocument document = await context.OpenNewAsync();
        IWindow window = document.DefaultView;

        var set = typeof(IWindowTimers).GetMethod(nameof(IWindowTimers.SetTimeout))!;
        var clear = typeof(IWindowTimers).GetMethod(nameof(IWindowTimers.ClearTimeout))!;
        bool ran = false;
        Action<IWindow> original = _ => ran = true;
        object?[] parameters = { original, 10 };

        Action<object?>? afterCall = TimerSettleLog.Intercept(set, window, parameters);
        Assert.NotNull(afterCall);
        Assert.NotSame(original, parameters[0]);
        Assert.Equal(1, log.Pending);

        ((Action<IWindow>)parameters[0]!)(window);
        Assert.True(ran);
        Assert.Equal(0, log.Pending);

        object?[] second = { original, 10 };
        TimerSettleLog.Intercept(set, window, second)!(42);   // the handle setTimeout returned
        Assert.Equal(1, log.Pending);
        Assert.Null(TimerSettleLog.Intercept(clear, window, new object?[] { 42 }));
        Assert.Equal(0, log.Pending);

        // Not a timer method, and a page with no log: untouched.
        Assert.Null(TimerSettleLog.Intercept(typeof(INonElementParentNode).GetMethod(nameof(INonElementParentNode.GetElementById))!, document, new object?[] { "x" }));
        using IBrowsingContext bare = BrowsingContext.New(Configuration.Default);
        IDocument other = await bare.OpenNewAsync();
        object?[] untouched = { original, 10 };
        Assert.Null(TimerSettleLog.Intercept(set, other.DefaultView, untouched));
        Assert.Same(original, untouched[0]);
    }

    /// <summary>The vendored binding tells the page's log every <c>setTimeout</c>, and a timer that ran leaves nothing pending.</summary>
    [Fact]
    public void APagesTimers_ReachTheLog()
    {
        var (text, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "setTimeout(function () { document.getElementById('a').textContent = 'SETTLED'; }, 20);" +
            "setTimeout(function () { }, 600000);" +
            "</script></body></html>");

        TimerSettleLog? log = JintHtmlScriptPlugin.TimersOfThisThreadsPage;
        Assert.NotNull(log);
        Assert.Equal(2, log!.Seen);
        Assert.Equal(1, log.Pending);   // the ten-minute timer, not waited for
        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.Contains("SETTLED", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timer the page clears, though due inside the window, holds nothing open: the page is laid out
    /// as the script left it, within its deadline. Were the clearing lost, the window would wait for a
    /// callback that never comes, until the deadline said so.
    /// </summary>
    [Fact]
    public void AClearedTimerDueInsideTheWindow_DoesNotHoldThePage()
    {
        var (text, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "var t = setTimeout(function () { document.getElementById('a').textContent = 'NOT'; }, 30);" +
            "clearTimeout(t);" +
            "</script></body></html>");

        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.Equal(0, JintHtmlScriptPlugin.TimersOfThisThreadsPage!.Pending);
    }

    /// <summary>A timer whose callback throws has still run: it holds nothing open either, and the timer after it runs.</summary>
    [Fact]
    public void ATimerThatThrows_DoesNotHoldThePage()
    {
        var (text, reports) = Convert(
            "<html><body><p id='a'>BEFORE</p><script>" +
            "setTimeout(function () { throw new Error('boom'); }, 10);" +
            "setTimeout(function () { document.getElementById('a').textContent = 'AFTER'; }, 20);" +
            "</script></body></html>");

        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.Equal(0, JintHtmlScriptPlugin.TimersOfThisThreadsPage!.Pending);
    }

    /// <summary>
    /// Converted under a ten-second page deadline, so a timer the window wrongly waits for shows as
    /// the deadline's report, checked first, rather than as a slow test. Three seconds was too close:
    /// under a parallel suite run the deadline itself cut a page short once, before its second timer.
    /// </summary>
    private static (string Text, List<string> Reports) Convert(string html)
    {
        var plugin = new JintHtmlScriptPlugin(new HtmlScriptLimits { PageTimeout = TimeSpan.FromSeconds(10) });
        var reports = new List<string>();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var output = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
            {
                RenderEngine = HtmlRenderEngine.Managed,
                OnDiagnostic = d =>
                {
                    if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (reports) reports.Add(d.Detail);
                },
            });
            output.Position = 0;
            using var doc = PdfDocument.Open(output);
            return (Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc), reports);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }
}
