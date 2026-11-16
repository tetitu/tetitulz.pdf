// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Renaming controls that share a name is linear, not quadratic. <c>FieldNameFor</c> (and
    /// <c>RadioSpec</c> for a radio group's values) probed <c>name_2</c>, <c>name_3</c> … from 2 for
    /// every colliding control, so N controls sharing a name cost ≈ N²/2 string builds and set look-ups
    /// (~10 minutes for 100 000 inputs). A per-base next-suffix cache makes each O(1); the rename
    /// sequence is unchanged because names are only ever added (2026-10-23).
    /// </summary>
    public class FormFieldRenameCostTests
    {
        /// <summary>The rename sequence is still name, name_2, name_3 … — the cache only skips re-probing.</summary>
        [Fact]
        public void ControlsSharingAName_AreRenamedInSequence()
        {
            var result = HtmlToPdfConverter.ConvertHtmlWithFormFields(
                "<p><input name='dup'><input name='dup'><input name='dup'></p>", Managed());

            Assert.Equal(new[] { "dup", "dup_2", "dup_3" }, result.FormFields.Select(f => f.Field.Name).ToArray());
        }

        [Fact]
        public void ManySharedNameControls_AreRenamedInLinearTime()
        {
            const int n = 10_000;
            string html = "<p>" + string.Concat(Enumerable.Repeat("<input name='dup'>", n)) + "</p>";

            // Per-thread, not GC.GetTotalAllocatedBytes: the suffix probe runs on this thread, and a
            // process-wide counter would also catch other xUnit classes allocating in parallel.
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = HtmlToPdfConverter.ConvertHtmlWithFormFields(html, Managed());
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(n, result.FormFields.Count);
            // Pre-fix the suffix probe alone copied ≈ n²/2 strings (~1.5 GB at n=10 000); post-fix it is
            // linear. The allocation is the work signal — a quadratic probe cannot fit under this bound.
            Assert.True(allocated < 700_000_000,
                $"renaming {n:N0} same-named controls allocated {allocated:N0} bytes — the suffix probe is quadratic");
        }
    }
}
