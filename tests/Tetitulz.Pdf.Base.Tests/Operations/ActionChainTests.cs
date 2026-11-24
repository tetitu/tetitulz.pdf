// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>/Next</c> action chains (§12.6.3): every action a trigger fires must reach the host, not just
/// the first one.
/// </summary>
/// <remarks>
/// <para><b>Why this is a security property and not a completeness nicety.</b> This library's whole
/// model is that it never navigates, submits or launches anything — it raises an event and the host
/// decides. So the host's handler <i>is</i> the sandbox, and a sandbox that is shown one of a
/// document's five actions is not making the decision it thinks it is. <c>PdfActionRunner</c> read
/// the head of each chain and nothing after it; only <c>PdfSanitizer</c> knew <c>/Next</c>
/// existed.</para>
///
/// <para>Because <c>/Next</c> is attacker-controlled it is bounded three ways — depth, a cycle
/// guard, and a total-actions budget for chains that fan out through arrays rather than nesting —
/// and each refusal reports rather than truncating in silence.</para>
/// </remarks>
public class ActionChainTests
{
    [Fact]
    public void OpenAction_WithANextChain_SurfacesEveryLinkNotJustTheHead()
    {
        // Three URI actions chained head -> 6 -> 7.
        byte[] pdf = Pdf(
            openAction: "5 0 R",
            extra: new List<string>
            {
                Obj(5, "<< /Type /Action /S /URI /URI (https://first.example) /Next 6 0 R >>"),
                Obj(6, "<< /Type /Action /S /URI /URI (https://second.example) /Next 7 0 R >>"),
                Obj(7, "<< /Type /Action /S /URI /URI (https://third.example) >>"),
            });

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(pdf, uris);

        Assert.NotNull(result);
        Assert.Equal(
            new[] { "https://first.example", "https://second.example", "https://third.example" },
            uris);
        Assert.Equal(2, result.ChainedEvents.Count);
    }

    [Fact]
    public void OpenAction_WithANextArray_SurfacesEveryBranch()
    {
        byte[] pdf = Pdf(
            openAction: "5 0 R",
            extra: new List<string>
            {
                Obj(5, "<< /Type /Action /S /URI /URI (https://head.example) /Next [6 0 R 7 0 R] >>"),
                Obj(6, "<< /Type /Action /S /URI /URI (https://branch-a.example) >>"),
                Obj(7, "<< /Type /Action /S /URI /URI (https://branch-b.example) >>"),
            });

        var uris = new List<string>();
        RunOpenAction(pdf, uris);

        Assert.Contains("https://branch-a.example", uris);
        Assert.Contains("https://branch-b.example", uris);
    }

    /// <summary>
    /// A chain that loops back must terminate and say it was cut, not spin.
    /// </summary>
    [Fact]
    public void CyclicNextChain_TerminatesAndReportsTheLoop()
    {
        byte[] pdf = Pdf(
            openAction: "5 0 R",
            extra: new List<string>
            {
                Obj(5, "<< /Type /Action /S /URI /URI (https://a.example) /Next 6 0 R >>"),
                Obj(6, "<< /Type /Action /S /URI /URI (https://b.example) /Next 5 0 R >>"),
            });

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(pdf, uris);

        Assert.NotNull(result);

        // Each action ran exactly once; the second visit to the head was refused.
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, uris);
        Assert.Contains(result.Warnings, w => w.Contains("loops back to an action already on this chain"));
    }

    /// <summary>
    /// A converging (diamond) chain is acyclic and conforming, and must not be reported as a loop.
    /// </summary>
    /// <remarks>
    /// A with <c>/Next [B C]</c>, and B and C both with <c>/Next D</c>. D is legitimately reached
    /// twice by two different paths. "Already seen" is not "a loop", and conflating them would make
    /// the cycle guard accuse conforming input. D is run once; nothing warns.
    /// </remarks>
    [Fact]
    public void ConvergingNextTree_IsNotReportedAsACycle()
    {
        byte[] pdf = Pdf(
            openAction: "5 0 R",
            extra: new List<string>
            {
                Obj(5, "<< /Type /Action /S /URI /URI (https://a.example) /Next [6 0 R 7 0 R] >>"),
                Obj(6, "<< /Type /Action /S /URI /URI (https://b.example) /Next 8 0 R >>"),
                Obj(7, "<< /Type /Action /S /URI /URI (https://c.example) /Next 8 0 R >>"),
                Obj(8, "<< /Type /Action /S /URI /URI (https://d.example) >>"),
            });

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(pdf, uris);

        Assert.NotNull(result);
        Assert.Contains("https://d.example", uris);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("cycle"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("loop"));
    }

    /// <summary>
    /// The floor. An action with no <c>/Next</c> must behave exactly as before — one event, no
    /// chained entries, no warnings — or the chain support has changed the ordinary case.
    /// </summary>
    [Fact]
    public void OpenAction_WithoutAChain_IsUnchanged()
    {
        byte[] pdf = Pdf(
            openAction: "5 0 R",
            extra: new List<string>
            {
                Obj(5, "<< /Type /Action /S /URI /URI (https://only.example) >>"),
            });

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(pdf, uris);

        Assert.NotNull(result);
        Assert.Equal(new[] { "https://only.example" }, uris);
        Assert.Empty(result.ChainedEvents);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// A <c>/Next</c> chain deeper than the cap stops there and says so.
    /// </summary>
    /// <remarks>
    /// Adversarial input. <c>PdfActionRunner</c> documents three bounds — a cycle guard, this depth
    /// cap, and the per-trigger budget below — and only the cycle guard was tested: the deepest
    /// fixture in this file was 4 links and the widest 4 actions, so both blocks were unreachable
    /// from every test in the tree. <c>/Next</c> is attacker-controlled, which is what makes them
    /// guards rather than tidiness.
    /// <para>The assertion is the guard's own evidence — how many actions actually reached the host,
    /// plus the refusal text — never a wall clock. A DoS-guard test that finishes fast has quite
    /// possibly not run.</para>
    /// </remarks>
    [Fact]
    public void DeepNextChain_StopsAtTheDepthCapAndReportsIt()
    {
        // Head plus MaxActionChainDepth levels is what may run; three more prove the cut.
        int links = PdfLimits.MaxActionChainDepth + 3;
        var extra = new List<string>();
        for (int i = 0; i < links; i++)
        {
            int num = 5 + i;
            string next = i == links - 1 ? "" : $" /Next {num + 1} 0 R";
            extra.Add(Obj(num, $"<< /Type /Action /S /URI /URI (https://n{i}.example){next} >>"));
        }

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(Pdf("5 0 R", extra), uris);

        Assert.Equal(PdfLimits.MaxActionChainDepth + 1, uris.Count);
        Assert.Equal($"https://n{PdfLimits.MaxActionChainDepth}.example", uris[uris.Count - 1]);
        Assert.DoesNotContain($"https://n{PdfLimits.MaxActionChainDepth + 1}.example", uris);
        Assert.Contains(result.Warnings,
            w => w.Contains($"exceeded {PdfLimits.MaxActionChainDepth} levels"));
    }

    /// <summary>
    /// A <c>/Next</c> array wider than the per-trigger budget stops at the budget and says so.
    /// </summary>
    /// <remarks>
    /// The fan-out sits entirely at depth 1, so the depth cap provably is not what stopped it —
    /// asserted by the absence of a "levels" warning. That is the distinction the budget exists for:
    /// a depth cap bounds one branch, never the number of branches.
    /// <para>The refusal must also be reported <b>once</b>. Every sibling past the budget takes the
    /// refusing branch, so this fixture's four over-budget entries would have produced four
    /// identical strings, and a 10 000-entry array ~9 745 — a warnings list a caller cannot read
    /// anything else out of.</para>
    /// </remarks>
    [Fact]
    public void OversizedNextFanOut_StopsAtThePerTriggerBudgetAndReportsItOnce()
    {
        const int branches = PdfActionRunner.MaxActionsPerTrigger + 4;
        var refs = new StringBuilder();
        var branchObjs = new List<string>();
        for (int i = 0; i < branches; i++)
        {
            int num = 6 + i;
            refs.Append(num).Append(" 0 R ");
            branchObjs.Add(Obj(num, $"<< /Type /Action /S /URI /URI (https://b{i}.example) >>"));
        }

        var extra = new List<string>
        {
            Obj(5, "<< /Type /Action /S /URI /URI (https://head.example) /Next [" +
                   refs.ToString().TrimEnd() + "] >>"),
        };
        extra.AddRange(branchObjs);

        var uris = new List<string>();
        PdfActionResult result = RunOpenAction(Pdf("5 0 R", extra), uris);

        // Head plus budget-1 branches ran; the remainder were refused, not silently dropped.
        Assert.Equal(PdfActionRunner.MaxActionsPerTrigger, uris.Count);
        Assert.Single(result.Warnings,
            w => w.Contains($"exceeded {PdfActionRunner.MaxActionsPerTrigger} actions for one trigger"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("levels"));
    }

    /// <summary>
    /// A chain of JavaScript actions executes at most
    /// <see cref="PdfActionRunner.MaxJavaScriptActionsPerTrigger"/> of them, and <b>reports</b> the
    /// rest rather than dropping them.
    /// </summary>
    /// <remarks>
    /// <para>The 256-action budget bounds how many actions the host is <i>told about</i>, which is
    /// this type's whole product, so it has to stay generous. But every JavaScript action reaching
    /// the plugin builds a fresh engine with its own ceilings — currently 5 seconds and 32 MB — so
    /// 256 of them is roughly 21 minutes of CPU from one attacker-controlled <c>/Next</c> chain. The
    /// per-action timeout is precisely what makes the total look bounded.</para>
    /// <para><b>The assertion works with no JavaScript plugin registered</b>, which is what makes it
    /// cheap and deterministic. Every executed script reports "no engine"; a capped one reports the
    /// cap instead. Counting the two apart separates "the budget stopped it" from "there was no
    /// engine anyway" — without that split the test would pass with the cap deleted.</para>
    /// </remarks>
    [Fact]
    public void LongJavaScriptChain_ExecutesAtMostTheScriptBudget_AndReportsTheRest()
    {
        const int scripts = PdfActionRunner.MaxJavaScriptActionsPerTrigger + 4;
        var extra = new List<string>();
        for (int i = 0; i < scripts; i++)
        {
            int num = 5 + i;
            string next = i == scripts - 1 ? "" : $" /Next {num + 1} 0 R";
            extra.Add(Obj(num, $"<< /Type /Action /S /JavaScript /JS (app.alert\\({i}\\);){next} >>"));
        }

        PdfActionResult result = RunOpenAction(Pdf("5 0 R", extra), new List<string>());

        // Reached the engine (and found none registered) exactly budget times — no more.
        int attempted = result.Warnings.FindAll(w => w.Contains("JavaScript action skipped")).Count;
        Assert.Equal(PdfActionRunner.MaxJavaScriptActionsPerTrigger, attempted);

        // The remainder were refused, once, and named as reported-but-not-executed.
        Assert.Single(result.Warnings, w => w.Contains("JavaScript actions for one trigger"));

        // Enumeration survives the cap: every REFUSED script still raises its event, so a host
        // asking "what will this document do?" is told the scripts are there even though they were
        // not run. That is the whole point of capping executions rather than truncating the walk.
        //
        // The executed ones contribute nothing here, and that is pre-existing rather than something
        // the cap changed: with no plugin registered RunAction breaks out before setting
        // EventRaised. So this count is exactly the refused tail.
        Assert.Equal(scripts - PdfActionRunner.MaxJavaScriptActionsPerTrigger,
                     result.ChainedEvents.Count);
        Assert.False(result.JavaScriptExecuted, "no engine is registered, so nothing can have run");
    }

    // ---------------------------------------------------------------- helpers

    private static PdfActionResult RunOpenAction(byte[] pdf, List<string> uris)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var runner = new PdfActionRunner(doc);
        runner.UriRequested += (s, e) => uris.Add(e.Uri);

        return runner.RunCatalogOpenAction();
    }

    private static string Obj(int num, string body) => $"{num} 0 obj\n{body}\nendobj\n";

    private static byte[] Pdf(string openAction, List<string> extra)
    {
        var bodies = new List<string>
        {
            Obj(1, $"<< /Type /Catalog /Pages 2 0 R /OpenAction {openAction} >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"),
            StreamObj(4, "BT ET\n"),
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in bodies) { offsets.Add(sb.Length); sb.Append(body); }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string StreamObj(int num, string content) =>
        $"{num} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n";
}
