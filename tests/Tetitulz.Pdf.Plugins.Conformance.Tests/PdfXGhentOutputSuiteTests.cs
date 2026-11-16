// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Plugins.Conformance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The Ghent Workgroup's PDF/X-4 output suite (<c>tools/corpus-fetch</c>) is a certified PDF/X-4
/// file — the one acceptance control the public-source rules (GAPS.md §12b) have. A rule that
/// reports it is a rule written wrong, whatever its source says; the brochure's one-liners are
/// judged against a file the standard's own community certified. Skips when the file is not in
/// the corpus.
/// </summary>
public class PdfXGhentOutputSuiteTests
{
    private const string X4Pages = "Ghent_PDF-Output-Test-V50_ALL_X4.pdf";

    [CorpusFact(X4Pages)]
    public void TheCertifiedX4Suite_ReportsNoViolationAtX4_AndIsRefusedAtX1a()
    {
        string path = TestCorpus.Find(X4Pages)!;

        var x4 = PdfXValidator.Validate(path, PdfXConformanceLevel.X4);
        Assert.True(x4.IsConforming,
            "a certified PDF/X-4 file reported: " + string.Join(" | ", x4.Violations.Select(v => v.Code + " " + v.Message)));

        // Potency: the suite exists to exercise what X-4 added over X-1a — ICC-based objects and
        // transparency — so at X-1a the same file must fail, by those rules.
        var x1a = PdfXValidator.Validate(path, PdfXConformanceLevel.X1a);
        Assert.False(x1a.IsConforming);
        Assert.Contains(x1a.Violations, v => v.Code == "X08" || v.Code == "X09");
    }
}
