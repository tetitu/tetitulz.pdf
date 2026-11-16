// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Net;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// A page's scripts are untrusted code: every limit in
/// <see cref="HtmlScriptLimits"/> stops a hostile page, says which limit did, and leaves the page
/// laid out; and nothing a script does reaches the network or outlives the call.
/// </summary>
/// <remarks>
/// <para><b>Each hostile page runs on its own thread with a generous join</b>, so a guard that stops
/// guarding fails the test instead of hanging the run — and each asserts the guard's <i>evidence</i>
/// (its report, and a marker set by the script after it), never a wall clock.</para>
/// <para><b>The limits are set tight here</b>: a ceiling only production scale reaches is tested as a
/// parameter, and <c>sweep-html-script-limits.ps1</c> mutates each one's forwarding into Jint.</para>
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class ScriptLimitTests
{
    private static readonly TimeSpan Join = TimeSpan.FromSeconds(60);

    private static HtmlScriptLimits Tight(Action<HtmlScriptLimits>? adjust = null)
    {
        var limits = new HtmlScriptLimits
        {
            MaxStatementsPerScript = int.MaxValue,
            MaxMemoryBytesPerScript = long.MaxValue,
            MaxMemoryBytesPerPage = long.MaxValue,
            ScriptTimeout = TimeSpan.FromSeconds(30),
            PageTimeout = TimeSpan.FromSeconds(30),
            MaxCallStackDepth = 2_000,
        };
        adjust?.Invoke(limits);
        return limits;
    }

    /// <summary>Converts on a dedicated thread, failing — not hanging — if it does not return.</summary>
    private static (string Text, List<string> Reports) ConvertBounded(
        string html, HtmlScriptLimits limits, Action<HtmlToPdfOptions>? configure = null)
    {
        (string, List<string>) result = default;
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { result = Convert(html, limits, configure); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(Join), "the conversion did not return: a script limit is not being applied");
        if (error != null) throw new InvalidOperationException("conversion failed", error);
        return result;
    }

    private static (string Text, List<string> Reports) Convert(
        string html, HtmlScriptLimits limits, Action<HtmlToPdfOptions>? configure)
    {
        var plugin = new JintHtmlScriptPlugin(limits);
        var reports = new List<string>();
        var options = new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d =>
            {
                if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (reports) reports.Add(d.Detail);
            },
        };
        configure?.Invoke(options);

        PdfPluginHost.Instance.Register(plugin);
        try
        {
            using var output = new MemoryStream();
            HtmlToPdfConverter.ConvertHtml(html, output, options);
            output.Position = 0;
            using var doc = PdfDocument.Open(output);
            return (Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc), reports);
        }
        finally { PdfPluginHost.Instance.Unregister(plugin.Name); }
    }

    private const string After = "<script>document.getElementById('m').textContent = 'AFTER';</script>";

    /// <summary>
    /// Forty scripts each keeping a string under the per-script memory limit held forty times that
    /// limit together; the page's cap stops the scripts after it and says so. The control is the same
    /// page under a cap it does not reach, whose marker script runs.
    /// </summary>
    [Theory]
    [InlineData(16L * 1024 * 1024, true)]
    [InlineData(long.MaxValue, false)]
    public void AdversarialScriptsEachUnderTheirOwnMemoryLimit_AreBoundedTogetherByThePages(long pageCap, bool stopped)
    {
        var scripts = new System.Text.StringBuilder();
        for (int i = 0; i < 40; i++)
            scripts.Append("<script>window['k").Append(i).Append("']='x'.repeat(900000);</script>");
        string html = "<html><body><p id='m'>BEFORE</p>" + scripts + After + "</body></html>";

        var (text, reports) = ConvertBounded(html, Tight(l =>
        {
            l.MaxMemoryBytesPerScript = 4L * 1024 * 1024;
            l.MaxMemoryBytesPerPage = pageCap;
        }));

        Assert.Equal(stopped, reports.Exists(r => r.Contains("HtmlScriptLimits.MaxMemoryBytesPerPage")));
        Assert.Contains(stopped ? "BEFORE" : "AFTER", text);
        Assert.DoesNotContain(stopped ? "AFTER" : "BEFORE", text);
    }

    private static string Page(string hostileScript) =>
        "<html><body><p id='m'>BEFORE</p><script>" + hostileScript + "</script>" + After + "</body></html>";

    /// <summary>A page whose one text run sits <paramref name="depth"/> divs deep, with the marker script after it.</summary>
    private static string Nested(int depth) =>
        "<html><body><p id='m'>BEFORE</p>" +
        string.Concat(Enumerable.Repeat("<div>", depth)) + "x" +
        string.Concat(Enumerable.Repeat("</div>", depth)) + After + "</body></html>";

    // ------------------------------------------------------------------ per-script limits

    [Fact]
    public void AnInfiniteLoopIsStoppedAtTheStatementLimit()
    {
        var (text, reports) = ConvertBounded(Page("while (true) {}"),
            Tight(l => l.MaxStatementsPerScript = 100_000));

        Assert.Contains(reports, r => r.Contains("statement limit", StringComparison.Ordinal)
                                      && r.Contains("100000", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInfiniteLoopIsStoppedAtTheTimeLimit()
    {
        var (text, reports) = ConvertBounded(Page("while (true) {}"),
            Tight(l => l.ScriptTimeout = TimeSpan.FromMilliseconds(300)));

        Assert.Contains(reports, r => r.Contains("time limit", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A backtracking pattern runs inside one call, out of the statement timeout's reach, so the
    /// regex timeout is the script's: at a one-second script timeout the page ends well inside its
    /// four-second deadline, where Jint's own ten seconds ran the page past it — the evidence is that
    /// deadline's report, absent.
    /// </summary>
    [Fact]
    public void AdversarialBacktrackingPatternStopsAtTheScriptTimeout()
    {
        var (text, reports) = ConvertBounded(
            Page("/^(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab');"),
            Tight(l => { l.ScriptTimeout = TimeSpan.FromSeconds(1); l.PageTimeout = TimeSpan.FromSeconds(4); }));

        Assert.Contains(reports, r => r.Contains("time limit", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prepared script is cached for every document, and its patterns keep the timeout they were
    /// prepared with, so the cache knows the timeout too: the same script prepared under six seconds
    /// is prepared again under one, and the page ends inside its four-second deadline.
    /// </summary>
    [Fact]
    public void AdversarialBacktrackingPatternPreparedUnderALongerTimeout_IsPreparedAgainUnderThisOne()
    {
        string page = Page("/^(b+)+$/.test('bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbba');");
        ConvertBounded(page, Tight(l => { l.ScriptTimeout = TimeSpan.FromSeconds(6); l.PageTimeout = TimeSpan.FromSeconds(10); }));

        var (text, reports) = ConvertBounded(page,
            Tight(l => { l.ScriptTimeout = TimeSpan.FromSeconds(1); l.PageTimeout = TimeSpan.FromSeconds(4); }));

        Assert.Contains(reports, r => r.Contains("time limit", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemoryBombIsStoppedAtTheMemoryLimit()
    {
        var (text, reports) = ConvertBounded(
            Page("var hoard = []; while (true) { hoard.push(new Array(100000).join('x')); }"),
            // The time limit is a backstop, short on purpose: if the memory limit's forwarding is
            // mutated away this test must fail by naming the wrong limit, not exhaust the machine.
            Tight(l => { l.MaxMemoryBytesPerScript = 16L * 1024 * 1024; l.ScriptTimeout = TimeSpan.FromSeconds(3); }));

        Assert.Contains(reports, r => r.Contains("memory limit", StringComparison.Ordinal));
        Assert.Contains("AFTER", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The call depth is the configured one, not the vendored default: a recursion 500 deep passes
    /// at the default and fails at 100. That is what proves the setting is forwarded at all.
    /// </summary>
    [Fact]
    public void RecursionPastTheConfiguredCallDepthFails()
    {
        const string Recurse =
            "function down(n) { return n === 0 ? 0 : 1 + down(n - 1); } " +
            "document.getElementById('m').setAttribute('data-depth', '' + down(500));";
        string page = "<html><body><p id='m'>BEFORE</p><script>" + Recurse + "</script>" +
                      "<script>document.getElementById('m').textContent = " +
                      "document.getElementById('m').getAttribute('data-depth') || 'NODEPTH';</script></body></html>";

        var (shallow, shallowReports) = ConvertBounded(page, Tight(l => l.MaxCallStackDepth = 100));
        Assert.Contains("NODEPTH", shallow, StringComparison.Ordinal);
        Assert.Contains(shallowReports, r => r.Contains("threw", StringComparison.Ordinal));

        // The control: the same page under the default depth recurses to the end.
        var (deep, _) = ConvertBounded(page, Tight());
        Assert.Contains("500", deep, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page nested past <see cref="HtmlScriptLimits.MaxDocumentDepth"/> does not have its
    /// scripts run — reported — and is laid out as written. The ceiling is tested as a parameter
    /// (CLAUDE.md, Mutation): a page deep enough to overflow AngleSharp's recursive walks at the
    /// default is also deep enough to take the managed engine's own layout past a minute, so the
    /// test sets a low limit and nests just past it.
    /// </summary>
    [Fact]
    public void AHostilelyDeepPageIsNotScriptedAndSaysSo()
    {
        var (text, reports) = ConvertBounded(Nested(200), Tight(l => l.MaxDocumentDepth = 50));

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AFTER", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("MaxDocumentDepth", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control: a page nested within the same limit is scripted as usual — so the refusal above
    /// is about depth, not about any page with nesting.
    /// </summary>
    [Fact]
    public void APageNestedWithinTheLimitIsScripted()
    {
        var (text, reports) = ConvertBounded(Nested(40), Tight(l => l.MaxDocumentDepth = 50));

        Assert.Contains("AFTER", text, StringComparison.Ordinal);
        Assert.DoesNotContain(reports, r => r.Contains("MaxDocumentDepth", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ the page deadline

    /// <summary>
    /// Many scripts, each cheap enough for every per-script limit, are stopped by the page deadline:
    /// the last one never runs, and the deadline is reported once.
    /// </summary>
    /// <remarks>
    /// Each script busy-waits 5 ms <b>by the clock</b> rather than looping a fixed count: the first
    /// version looped 200 000 times, and Jint ran all 400 inside the 500 ms deadline — so it proved
    /// nothing. Four hundred 5 ms scripts need at least 2 s on any machine.
    /// </remarks>
    [Fact]
    public void ThePageDeadlineStopsManyIndividuallyCheapScripts()
    {
        string cheap = "<script>var t = Date.now(); while (Date.now() - t < 5) {}</script>";
        string page = "<html><body><p id='m'>BEFORE</p>" +
                      string.Concat(Enumerable.Repeat(cheap, 400)) + After + "</body></html>";

        var (text, reports) = ConvertBounded(page, Tight(l => l.PageTimeout = TimeSpan.FromMilliseconds(500)));

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AFTER", text, StringComparison.Ordinal);
        Assert.Single(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        // Once, for the page — not a "threw" per script the deadline stopped.
        Assert.DoesNotContain(reports, r => r.Contains("threw", StringComparison.Ordinal));
    }

    /// <summary>
    /// A script <b>already running</b> when the page deadline passes is stopped by it — not left to
    /// run on to its own, far longer, time limit. Only the deadline handed to Jint can do this: the
    /// check before each script cannot reach one that has started.
    /// </summary>
    [Fact]
    public void ThePageDeadlineStopsAScriptAlreadyRunning()
    {
        var (text, reports) = ConvertBounded(Page("while (true) {}"),
            Tight(l => { l.PageTimeout = TimeSpan.FromMilliseconds(500); l.ScriptTimeout = TimeSpan.FromSeconds(30); }));

        Assert.Contains(reports, r => r.Contains("ran out of time", StringComparison.Ordinal));
        Assert.DoesNotContain(reports, r => r.Contains("time limit", StringComparison.Ordinal));
        Assert.DoesNotContain("AFTER", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A timer the page leaves running does not outlive the call: a <c>setInterval</c> that burns the
    /// CPU on every tick stops once the conversion returns. Asserted on this process's CPU time, not
    /// on a wall clock.
    /// </summary>
    /// <remarks>
    /// <b>The quietest of three windows, not one.</b> Process CPU time is shared state: a garbage
    /// collection or the JIT's background tier can burn a burst in any one window, and under a
    /// whole-solution run one did (a single 1500 ms window read over its 750 ms bound, the suite
    /// green alone). A timer left running burns in every window — its busy loop keeps a core full —
    /// so the least of three consecutive windows still sees it, while a burst lands in one.
    /// <para><b>The potency half: the timer did run</b> — its tick marks the page, and the page
    /// laid out carries the mark. Without it a timer that never fired would pass too.</para>
    /// <para><b>Whose property this is.</b> AngleSharp runs a page's timers only while its event
    /// loop is awaited, so nothing fires once <c>RunAsync</c> returns: removing every disposal in
    /// the plugin — the context, the document, the page deadline and its cancel — leaves this test
    /// green (measured 2026-09-29). The plugin's disposals are defence in depth
    /// (sweep-html-script-limits.ps1, L7); this test is what would notice AngleSharp changing.</para>
    /// </remarks>
    [Fact]
    public void ATimerThePageLeavesRunningDoesNotOutliveTheCall()
    {
        string page = "<html><body><p id='m'>BEFORE</p><script>" +
                      "setInterval(function () { var t = Date.now(); while (Date.now() - t < 200) {} " +
                      "document.getElementById('m').textContent = 'TICKED'; }, 0);" +
                      "</script></body></html>";

        // A settle window long enough that the first tick lands however loaded the machine: with
        // the default 100 ms a whole-solution run once returned before it had, and the potency
        // assertion below failed for a reason that is not this test's.
        var (text, _) = ConvertBounded(page, Tight(l =>
        {
            l.ScriptTimeout = TimeSpan.FromSeconds(30);
            l.TimerSettleTime = TimeSpan.FromSeconds(2);
        }));
        Assert.Contains("TICKED", text, StringComparison.Ordinal);

        Thread.Sleep(300);  // let anything already scheduled finish or die
        var process = Process.GetCurrentProcess();
        var windows = new double[3];
        for (int i = 0; i < windows.Length; i++)
        {
            process.Refresh();
            TimeSpan before = process.TotalProcessorTime;
            Thread.Sleep(500);
            process.Refresh();
            windows[i] = (process.TotalProcessorTime - before).TotalMilliseconds;
        }

        Assert.True(windows.Min() < 250,
            $"CPU in three 500 ms quiet windows: {string.Join(", ", windows.Select(w => w.ToString("F0")))} ms — " +
            "the page's timer is still running");
    }

    // ------------------------------------------------------------------ no network

    /// <summary>
    /// Nothing a script does reaches the network: an <c>XMLHttpRequest</c>, a remote
    /// <c>&lt;script src&gt;</c> (refused by the converter's own rule — no loader is set) and an image
    /// all aimed at a listener on this machine, which receives nothing. The control request proves
    /// the listener would have noticed.
    /// </summary>
    [Fact]
    public async Task NothingAScriptDoesReachesTheNetwork()
    {
        int port = FreePort();
        string origin = "http://127.0.0.1:" + port + "/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(origin);
        listener.Start();
        int hits = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext ctx = await listener.GetContextAsync();
                    Interlocked.Increment(ref hits);
                    ctx.Response.StatusCode = 200;
                    ctx.Response.Close();
                }
                catch { return; }
            }
        });

        string page = "<html><body><p id='m'>BEFORE</p>" +
                      "<script src='" + origin + "remote.js'></script>" +
                      "<script>try { var x = new XMLHttpRequest(); x.open('GET', '" + origin + "xhr'); x.send(); } catch (e) {}" +
                      "try { var i = document.createElement('img'); i.src = '" + origin + "img.png'; document.body.appendChild(i); } catch (e) {}" +
                      "document.getElementById('m').textContent = 'RAN';</script></body></html>";

        var (text, reports) = ConvertBounded(page, Tight());
        Thread.Sleep(500);

        Assert.Contains("RAN", text, StringComparison.Ordinal);   // the scripts did run
        Assert.Equal(0, Volatile.Read(ref hits));
        Assert.Contains(reports, r => r.Contains("remote.js", StringComparison.Ordinal));

        // The control: the listener does count a request that is really made.
        using (var client = new HttpClient()) await client.GetAsync(origin + "control");
        SpinWait.SpinUntil(() => Volatile.Read(ref hits) > 0, TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref hits));
        listener.Stop();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ------------------------------------------------------------------ <script src>

    /// <summary>A local <c>&lt;script src&gt;</c> inside the base directory runs, inlined.</summary>
    [Fact]
    public void ALocalScriptSourceRuns()
    {
        WithBase(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "app.js"), "document.getElementById('m').textContent = 'FROMFILE';");
            var (text, _) = ConvertBounded(
                "<html><body><p id='m'>BEFORE</p><script src='app.js'></script></body></html>",
                Tight(), o => o.BasePath = dir);
            Assert.Contains("FROMFILE", text, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A <c>defer</c> script runs after the document is parsed: it reaches an element that comes
    /// after it in the markup, which a script run where it stands could not.
    /// </summary>
    [Fact]
    public void ADeferredScriptRunsAfterTheDocumentIsParsed()
    {
        WithBase(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "late.js"), "document.getElementById('late').textContent = 'DEFERRED';");
            var (text, reports) = ConvertBounded(
                "<html><body><script defer src='late.js'></script><p id='late'>BEFORE</p></body></html>",
                Tight(), o => o.BasePath = dir);
            Assert.Contains("DEFERRED", text, StringComparison.Ordinal);
            Assert.DoesNotContain(reports, r => r.Contains("threw", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// Inlined code that contains <c>&lt;/script&gt;</c> — in a string, as code often does — still
    /// runs whole: the tag must not close the inlined element early.
    /// </summary>
    [Fact]
    public void AnInlinedScriptContainingAClosingTagRunsWhole()
    {
        WithBase(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "tag.js"),
                "document.getElementById('m').textContent = 'LEN' + 'x</script>y'.length;");
            var (text, _) = ConvertBounded(
                "<html><body><p id='m'>BEFORE</p><script src='tag.js'></script></body></html>",
                Tight(), o => o.BasePath = dir);
            Assert.Contains("LEN11", text, StringComparison.Ordinal);
        });
    }

    /// <summary>A module script is not run, and says so.</summary>
    [Fact]
    public void AModuleScriptIsReportedNotRun()
    {
        var (text, reports) = ConvertBounded(
            "<html><body><p id='m'>BEFORE</p>" +
            "<script type='module'>document.getElementById('m').textContent = 'MODULE';</script></body></html>",
            Tight());

        Assert.Contains("BEFORE", text, StringComparison.Ordinal);
        Assert.Contains(reports, r => r.Contains("module", StringComparison.Ordinal));
    }

    private static void WithBase(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-htmlscript-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { body(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
