// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A <c>/Kids</c> cycle in the AcroForm field tree must cut the repeat and leave the rest of the
/// tree walkable — in <b>both</b> Base walks over that tree.
/// </summary>
/// <remarks>
/// <para><b>What was wrong.</b> <c>PdfActionRunner.FindFieldIn</c> and
/// <c>PdfActionDispatcher.CollectFieldActions</c> each carried only
/// <c>depth &gt;= PdfLimits.MaxTreeDepth</c>. A depth cap bounds one branch, never the number of
/// branches: with each of two fields naming the other twice, every level doubles the work, so the
/// cap of 100 permits 2<sup>100</sup> nodes. <c>FindFieldIn</c> <i>returns</i> at the cap, so it
/// backtracked and kept exploring — the public <c>RunFieldAction</c> never returned.
/// <c>CollectFieldActions</c> <i>threw</i> at the cap, which terminates but discards every action
/// in the document, including the fields listed after the cycle.</para>
/// <para><b>Why the assertions are what they are.</b> Termination alone is not evidence — a walk
/// that gave up entirely also terminates — so each test asserts the action belonging to the field
/// that sits <i>after</i> the cycle in <c>/Fields</c>. That separates "the guard cut the repeat and
/// carried on" from both "it spun forever" and "it aborted the whole walk". The watchdog runs the
/// call on its own thread rather than trusting a stopwatch: elapsed time in a full-solution run
/// measures the machine's backlog, and a dedicated thread removes the thread-pool from the claim.</para>
/// </remarks>
public class AcroFormKidsCycleTests
{
    private const string AfterCycleUri = "https://after-the-cycle.example";

    [Fact]
    public void FieldKidsCycle_RunFieldAction_CutsTheRepeatAndStillReachesTheFieldAfterIt()
    {
        byte[] pdf = CyclicFieldTree();

        var uris = OnItsOwnThread(() =>
        {
            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            var runner = new PdfActionRunner(doc);
            var seen = new List<string>();
            runner.UriRequested += (s, e) => seen.Add(e.Uri);
            runner.RunFieldAction("target", "MouseUp");
            return seen;
        });

        Assert.Equal(new[] { AfterCycleUri }, uris);
    }

    [Fact]
    public void FieldKidsCycle_ExtractActions_CutsTheRepeatAndStillCollectsTheFieldAfterIt()
    {
        byte[] pdf = CyclicFieldTree();

        var actions = OnItsOwnThread(() =>
        {
            using var ms = new MemoryStream(pdf);
            return new List<PdfAction>(PdfActionDispatcher.ExtractActions(ms));
        });

        var target = Assert.Single(actions, a => a.FieldName == "target");
        Assert.Equal(PdfActionType.Uri, target.ActionType);
        Assert.Equal(AfterCycleUri, target.Target);

        // The cycle's two fields carry no action of their own, so a walk that cut at the repeat
        // reports exactly one action; one that expanded the cycle would report the same /A over and
        // over (it is reachable through both of a's kids, and both of b's).
        Assert.Single(actions);
    }

    // ------------------------------------------------------------------ fixture

    // /Fields [10 0 R 12 0 R], where 10 and 11 name each other twice — the scanner's own input —
    // and 12 sits after the bomb so an aborted walk is distinguishable from a cut one.
    private static byte[] CyclicFieldTree() => Pdf(new List<string>
    {
        Obj(10, "<< /T (a) /FT /Tx /Kids [11 0 R 11 0 R] >>"),
        Obj(11, "<< /T (b) /FT /Tx /Kids [10 0 R 10 0 R] >>"),
        Obj(12, "<< /T (target) /FT /Tx /AA << /U 13 0 R >> >>"),
        Obj(13, $"<< /Type /Action /S /URI /URI ({AfterCycleUri}) >>"),
    });

    private static T OnItsOwnThread<T>(Func<T> work)
    {
        T value = default;
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { value = work(); }
            catch (Exception ex) { failure = ex; }
        }, 16 * 1024 * 1024)
        { IsBackground = true };

        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60)))
            Assert.Fail("the AcroForm /Kids walk did not terminate: the cycle guard never cut the repeat.");
        if (failure != null)
            Assert.Fail("the AcroForm /Kids walk threw instead of cutting the repeat: " + failure);
        return value;
    }

    private static string Obj(int num, string body) => $"{num} 0 obj\n{body}\nendobj\n";

    private static byte[] Pdf(List<string> extra)
    {
        var bodies = new List<string>
        {
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R 12 0 R] >> >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>"),
            StreamObj(4, "BT ET\n"),
        };
        bodies.AddRange(extra);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<(int Number, int Offset)>();
        foreach (string body in bodies)
        {
            int number = int.Parse(body.Substring(0, body.IndexOf(' ')), CultureInfo.InvariantCulture);
            offsets.Add((number, sb.Length));
            sb.Append(body);
        }

        int maxNumber = 0;
        foreach (var (number, _) in offsets) maxNumber = Math.Max(maxNumber, number);

        var byNumber = new Dictionary<int, int>();
        foreach (var (number, offset) in offsets) byNumber[number] = offset;

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(maxNumber + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= maxNumber; i++)
        {
            if (byNumber.TryGetValue(i, out int off))
                sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            else
                sb.Append("0000000000 65535 f \n");
        }

        sb.Append("trailer\n<< /Size ").Append(maxNumber + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string StreamObj(int num, string content) =>
        $"{num} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n";
}
