// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A paragraph that holds many separate single-space runs — the shape an HTML source of
/// <c>x&lt;i&gt;&lt;/i&gt; &lt;i&gt;&lt;/i&gt; … y</c> produces, a space text node between each empty
/// element — accumulated its pending space text with <c>pendingSpaceText += sp.Text</c>, one character
/// per run, committed once. That copied Σk ≈ n²/2 characters per layout pass (tens of GB for 200 000
/// runs). A reused <c>StringBuilder</c> makes it linear; the allocation is the work signal here, so no
/// wall clock is needed to see the quadratic cost (2026-10-23).
/// </summary>
public class ParagraphSpaceRunCostTests
{
    [Fact]
    public void ManySpaceRunsInAParagraph_DoNotAllocateQuadratically()
    {
        const int n = 50_000;

        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "x" });
        for (int i = 0; i < n; i++) p.Runs.Add(new TextRun { Text = " " });
        p.Runs.Add(new TextRun { Text = "y" });

        var doc = new Document();
        var section = new Section();
        section.Children.Add(p);
        doc.Sections.Add(section);

        // Per-thread, not GC.GetTotalAllocatedBytes: the O(n²) churn is in LayoutParagraphPass on this
        // thread, and a process-wide counter would also catch other xUnit classes allocating in parallel.
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] pdf = doc.ToPdf();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEmpty(pdf);
        // Pre-fix Σk ≈ n²/2 characters ≈ 2.5 GB at n = 50 000; post-fix the StringBuilder is linear.
        // A quadratic layout cannot come in under this bound, whatever the machine's speed.
        Assert.True(allocated < 400_000_000,
            $"laying out {n:N0} space runs allocated {allocated:N0} bytes — the pending-space accumulation is quadratic");
    }
}
