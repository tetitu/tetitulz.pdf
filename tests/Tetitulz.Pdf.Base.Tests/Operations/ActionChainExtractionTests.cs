// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfActionDispatcher.ExtractActions(Stream)"/> and the <c>/Next</c> chain (§12.6.3).
/// </summary>
/// <remarks>
/// <para><b>The defect these were written for was silence, not an error.</b> The dispatcher read the
/// action a trigger names and stopped, so an <c>/OpenAction</c> whose second link launches an
/// executable or runs a script was described as one harmless <c>/GoTo</c> — and nothing in the
/// output said a chain had been ignored, because nothing knew one was there. This is the method a
/// scanner reaches, and a scanner acts on an absence.</para>
///
/// <para><b>`PdfActionRunner` had followed `/Next` all along</b>, so the two disagreed about the
/// same document: one would execute ten actions and the other described one. Where two components
/// answer the same question, the test that matters asserts they agree.</para>
/// </remarks>
public class ActionChainExtractionTests
{
    // ================================================================ fixtures

    /// <summary>
    /// A one-page document whose catalog <c>/OpenAction</c> is <paramref name="chainLength"/>
    /// actions long: a <c>/GoTo</c> head followed by numbered JavaScript links.
    /// </summary>
    /// <remarks>
    /// The links are JavaScript with distinct script text so each is identifiable in the output —
    /// asserting "five actions came back" would pass for five copies of the head.
    /// </remarks>
    private static byte[] ChainedOpenAction(int chainLength)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        };

        // Object 4 is the head; 5..(4+chainLength-1) are the links.
        bodies.Add(chainLength > 1
            ? "<< /S /GoTo /D [3 0 R /Fit] /Next 5 0 R >>"
            : "<< /S /GoTo /D [3 0 R /Fit] >>");

        for (int link = 1; link < chainLength; link++)
        {
            int self = 4 + link;
            string next = link + 1 < chainLength ? $" /Next {self + 1} 0 R" : string.Empty;
            bodies.Add($"<< /S /JavaScript /JS (link{link}){next} >>");
        }

        return Assemble(bodies);
    }

    /// <summary>A chain that loops back on itself: 4 → 5 → 4.</summary>
    private static byte[] CyclicOpenAction() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /S /JavaScript /JS (head) /Next 5 0 R >>",
        "<< /S /JavaScript /JS (loop) /Next 4 0 R >>",
    });

    /// <summary>
    /// One level deep and <paramref name="branches"/> wide: a head whose <c>/Next</c> array names
    /// that many leaf actions.
    /// </summary>
    private static byte[] WideOpenAction(int branches)
    {
        var refs = string.Join(" ", Enumerable.Range(0, branches).Select(i => $"{5 + i} 0 R"));

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /S /GoTo /D [3 0 R /Fit] /Next [{refs}] >>",
        };

        for (int i = 0; i < branches; i++)
            bodies.Add($"<< /S /JavaScript /JS (wide{i}) >>");

        return Assemble(bodies);
    }

    /// <summary>An array-valued <c>/Next</c> — §12.6.3 allows a branch, not just a link.</summary>
    private static byte[] BranchingOpenAction() => Assemble(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        "<< /S /GoTo /D [3 0 R /Fit] /Next [5 0 R 6 0 R] >>",
        "<< /S /JavaScript /JS (branchA) >>",
        "<< /S /Launch /F (branchB.exe) >>",
    });

    private static byte[] Assemble(List<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static IReadOnlyList<PdfAction> Extract(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfActionDispatcher.ExtractActions(ms);
    }

    // ================================================================ the chain is followed

    /// <summary>
    /// <b>Every action on the chain is reported, in order, with the script that identifies it.</b>
    /// </summary>
    /// <remarks>
    /// Asserting the count alone would pass for five copies of the head, so this reads the
    /// JavaScript out of each link. The <c>/Launch</c>-style case is what a scanner is looking for
    /// and it never appears at depth 0.
    /// </remarks>
    [Fact]
    public void EveryActionOnTheChainIsReportedInOrder()
    {
        var actions = Extract(ChainedOpenAction(chainLength: 5))
            .Where(a => a.Trigger == "OpenDocument").ToList();

        Assert.Equal(5, actions.Count);

        Assert.Equal(PdfActionType.GoTo, actions[0].ActionType);
        Assert.Equal(0, actions[0].ChainDepth);

        for (int link = 1; link < 5; link++)
        {
            Assert.Equal(PdfActionType.JavaScript, actions[link].ActionType);
            Assert.Equal("link" + link, actions[link].Target);
            Assert.Equal(link, actions[link].ChainDepth);
        }
    }

    /// <summary>
    /// The whole chain belongs to the trigger that started it — that is what makes it one event.
    /// </summary>
    [Fact]
    public void EveryActionOnTheChainCarriesTheTriggerThatStartedIt()
    {
        var actions = Extract(ChainedOpenAction(chainLength: 4));

        Assert.Equal(4, actions.Count);
        Assert.All(actions, a => Assert.Equal("OpenDocument", a.Trigger));
    }

    /// <summary>
    /// <b>An array-valued <c>/Next</c> is a branch, and both arms are reported.</b>
    /// </summary>
    /// <remarks>
    /// This is also why a depth cap alone could not bound the walk: an array makes the chain a tree.
    /// </remarks>
    [Fact]
    public void AnArrayValuedNextReportsBothBranches()
    {
        var actions = Extract(BranchingOpenAction());

        Assert.Equal(3, actions.Count);
        Assert.Equal(PdfActionType.GoTo, actions[0].ActionType);

        Assert.Contains(actions, a => a.ActionType == PdfActionType.JavaScript && a.Target == "branchA");
        Assert.Contains(actions, a => a.ActionType == PdfActionType.Launch && a.Target == "branchB.exe");
        Assert.All(actions.Skip(1), a => Assert.Equal(1, a.ChainDepth));
    }

    /// <summary>The control: a document with no chain is unchanged by any of this.</summary>
    [Fact]
    public void AnUnchainedActionIsStillReportedExactlyOnce()
    {
        var actions = Extract(ChainedOpenAction(chainLength: 1));

        PdfAction only = Assert.Single(actions);
        Assert.Equal(PdfActionType.GoTo, only.ActionType);
        Assert.Equal(0, only.ChainDepth);
        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }

    // ================================================================ bounds, and saying so

    /// <summary>
    /// <b>A long linear chain is cut at the depth cap, and the cut is reported.</b>
    /// </summary>
    /// <remarks>
    /// Silence is the one unacceptable answer: a caller that cannot tell "the document has three
    /// actions" from "we stopped counting" has been told something false about the file.
    /// </remarks>
    [Fact]
    public void AnOverlongLinearChainIsCutAtTheDepthCapAndSaysSo()
    {
        var actions = Extract(ChainedOpenAction(chainLength: PdfLimits.MaxActionChainDepth + 40));

        // Depths 0..64 inclusive are described; the action at depth 65 is where it stops.
        Assert.Equal(PdfLimits.MaxActionChainDepth + 1, actions.Count);
        Assert.Equal(PdfLimits.MaxActionChainDepth, actions[^1].ChainDepth);

        Assert.NotNull(PdfActionDispatcher.LastChainDiagnostic);
        Assert.Contains("levels", PdfActionDispatcher.LastChainDiagnostic);
        Assert.Contains("not listed", PdfActionDispatcher.LastChainDiagnostic);
    }

    /// <summary>
    /// <b>A wide chain is cut at the node budget, which the depth cap cannot reach.</b>
    /// </summary>
    /// <remarks>
    /// This is the pair to the test above and the reason there are two bounds rather than one. A
    /// depth cap bounds one branch, never the number of branches: this fixture is <b>one</b> level
    /// deep and holds three hundred actions, so a walk bounded only by depth would describe every
    /// one of them, and a document could make <c>ExtractActions</c> as expensive as it liked by
    /// widening rather than lengthening.
    /// </remarks>
    [Fact]
    public void AnOversizedBranchingChainIsCutAtTheNodeBudgetAndSaysSo()
    {
        var actions = Extract(WideOpenAction(branches: 300));

        Assert.Equal(PdfActionDispatcher.MaxActionsPerChain, actions.Count);
        Assert.All(actions.Skip(1), a => Assert.Equal(1, a.ChainDepth));

        Assert.NotNull(PdfActionDispatcher.LastChainDiagnostic);
        Assert.Contains("ceiling", PdfActionDispatcher.LastChainDiagnostic);
    }

    /// <summary>
    /// A chain that loops terminates, reports each distinct action once, and stays <b>silent</b> —
    /// nothing was truncated.
    /// </summary>
    /// <remarks>
    /// A repeat is dedupe, not a bound. Describing an action twice tells a caller nothing, and a
    /// diamond is legal; this is where extraction and execution differ, and <c>PdfActionRunner</c>
    /// keeps a separate path set for exactly that reason.
    /// </remarks>
    [Fact]
    public void ACyclicChainTerminatesAndIsNotReportedAsTruncation()
    {
        var actions = Extract(CyclicOpenAction());

        Assert.Equal(2, actions.Count);
        Assert.Equal("head", actions[0].Target);
        Assert.Equal("loop", actions[1].Target);
        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }

    /// <summary>
    /// <b>When two different bounds are hit, the reported reason is the first one.</b>
    /// </summary>
    /// <remarks>
    /// <para>The walk starts at the document's entry point, so the first truncation is the one
    /// nearest what a reader does on opening the file — here the <c>/OpenAction</c> chain, which is
    /// too long, ahead of a page's wide one, which is too broad. The last would be whichever node
    /// happened to be enumerated last, and a document could bury the interesting cut behind a
    /// thousand uninteresting ones.</para>
    /// <para><b>This fixture exists because a mutation survived.</b> Every other case here trips at
    /// most one bound, so "keep the first" and "keep the last" were indistinguishable and the line
    /// choosing between them was untested. A single slot needs a stated rule, and a stated rule
    /// needs a document that can tell the two apart.</para>
    /// </remarks>
    [Fact]
    public void WhenTwoBoundsAreHitTheFirstReasonIsTheOneReported()
    {
        Extract(TooLongThenTooWide());

        Assert.NotNull(PdfActionDispatcher.LastChainDiagnostic);
        Assert.Contains("levels", PdfActionDispatcher.LastChainDiagnostic);
        Assert.DoesNotContain("ceiling", PdfActionDispatcher.LastChainDiagnostic);
    }

    /// <summary>
    /// An over-long <c>/OpenAction</c> chain, walked first, and an over-wide page <c>/AA /O</c>
    /// chain, walked after it.
    /// </summary>
    private static byte[] TooLongThenTooWide()
    {
        const int deep = 70;    // past MaxActionChainDepth
        const int wide = 300;   // past MaxActionsPerChain

        // 1 catalog, 2 pages, 3 page, 4 = head of the deep chain, then the deep links,
        // then the head of the wide chain, then its leaves.
        int firstDeepLink = 5;
        int wideHead = firstDeepLink + deep - 1;
        int firstWideLeaf = wideHead + 1;

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /AA << /O {wideHead} 0 R >> >>",
            $"<< /S /GoTo /D [3 0 R /Fit] /Next {firstDeepLink} 0 R >>",
        };

        for (int link = 1; link < deep; link++)
        {
            int self = 4 + link;
            string next = link + 1 < deep ? $" /Next {self + 1} 0 R" : string.Empty;
            bodies.Add($"<< /S /JavaScript /JS (deep{link}){next} >>");
        }

        string refs = string.Join(" ", Enumerable.Range(0, wide).Select(i => $"{firstWideLeaf + i} 0 R"));
        bodies.Add($"<< /S /GoTo /D [3 0 R /Fit] /Next [{refs}] >>");
        for (int i = 0; i < wide; i++)
            bodies.Add($"<< /S /JavaScript /JS (wide{i}) >>");

        return Assemble(bodies);
    }

    /// <summary>The diagnostic is cleared by the next call, not left standing from the last.</summary>
    [Fact]
    public void TheDiagnosticDoesNotSurviveIntoTheNextExtraction()
    {
        Extract(ChainedOpenAction(chainLength: PdfLimits.MaxActionChainDepth + 40));
        Assert.NotNull(PdfActionDispatcher.LastChainDiagnostic);

        Extract(ChainedOpenAction(chainLength: 2));

        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }

    /// <summary>
    /// It is cleared before the argument check too, so a throw cannot leave a stale answer standing.
    /// </summary>
    [Fact]
    public void TheDiagnosticIsClearedEvenWhenTheCallThrows()
    {
        Extract(ChainedOpenAction(chainLength: PdfLimits.MaxActionChainDepth + 40));
        Assert.NotNull(PdfActionDispatcher.LastChainDiagnostic);

        Assert.Throws<ArgumentNullException>(
            () => PdfActionDispatcher.ExtractActions((Stream)null));

        Assert.Null(PdfActionDispatcher.LastChainDiagnostic);
    }

    // ================================================================ the two components agree

    /// <summary>
    /// <b>The dispatcher's ceiling matches the runner's</b>, so a document the runner executes in
    /// full is not one the dispatcher describes in part.
    /// </summary>
    /// <remarks>
    /// The two numbers being equal is the claim; a comment asserting they agree would be the
    /// duplication report this repository writes tests instead of.
    /// </remarks>
    [Fact]
    public void TheDispatcherAndTheRunnerBoundTheChainTheSameWay()
    {
        Assert.Equal(PdfActionRunner.MaxActionsPerTrigger, PdfActionDispatcher.MaxActionsPerChain);
        Assert.Equal(64, PdfLimits.MaxActionChainDepth);
    }

    // ================================================================ the raw dictionary escape hatch

    /// <summary>
    /// <b><see cref="PdfAction.Dictionary"/> is the actual action dictionary the walk visited</b> —
    /// not a re-parse, the same instance <see cref="PdfDocument.ResolveObject(Core.Objects.PdfObject)"/>
    /// resolved. A caller asking a question the fixed <see cref="PdfAction"/> fields do not
    /// anticipate (does this <c>/Launch</c> carry a deprecated <c>/Win</c> entry?) reads it from
    /// here rather than needing a new field added for every such question.
    /// </summary>
    [Fact]
    public void DictionaryIsTheResolvedActionDictionaryItself()
    {
        byte[] pdf = Assemble(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /S /Launch /F (app.exe) /Win << /F (app.exe) >> >>",
        });

        var action = Assert.Single(Extract(pdf));

        Assert.NotNull(action.Dictionary);
        Assert.Equal(PdfActionType.Launch, action.ActionType);
        Assert.NotNull(action.Dictionary[Core.Objects.PdfName.Win]);
    }

    // ================================================================ the PdfDocument overload

    /// <summary>
    /// <see cref="PdfActionDispatcher.ExtractActions(PdfDocument)"/> walks the same document an
    /// already-open <see cref="PdfDocument"/> represents — for a caller (a conformance checker,
    /// say) that has one open already and would otherwise have to re-serialize it to a stream just
    /// to reach this walk. Same answer as the stream overload on the same bytes.
    /// </summary>
    [Fact]
    public void ThePdfDocumentOverloadAgreesWithTheStreamOverload()
    {
        byte[] pdf = ChainedOpenAction(chainLength: 3);

        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var fromDoc = PdfActionDispatcher.ExtractActions(doc);

        var fromStream = Extract(pdf);

        Assert.Equal(fromStream.Count, fromDoc.Count);
        for (int i = 0; i < fromStream.Count; i++)
        {
            Assert.Equal(fromStream[i].ActionType, fromDoc[i].ActionType);
            Assert.Equal(fromStream[i].Target, fromDoc[i].Target);
            Assert.Equal(fromStream[i].ChainDepth, fromDoc[i].ChainDepth);
        }
    }

    [Fact]
    public void ThePdfDocumentOverloadRejectsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => PdfActionDispatcher.ExtractActions((PdfDocument)null));
    }
}
