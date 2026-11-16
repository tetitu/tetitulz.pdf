// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A named family with no font program is drawn in a standard-14 face and reported — once per
/// family per export. It was reported for every piece of every run: a page set in an unavailable
/// Georgia gave 169 diagnostics for one fact.
/// </summary>
public class FamilySubstitutionReportTests
{
    private static List<BuilderDiagnostic> Export(params (string Family, string Text)[] paragraphs)
    {
        var section = new Section();
        foreach (var (family, text) in paragraphs)
        {
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = text, FontName = family });
            section.Children.Add(p);
        }
        var doc = new Document();
        doc.Sections.Add(section);
        var seen = new List<BuilderDiagnostic>();
        doc.ToPdf(new PdfExportOptions { AllowSystemFontSubstitution = false, OnDiagnostic = seen.Add });
        return seen.Where(d => d.Kind == BuilderDiagnosticKind.FontNotResolved).ToList();
    }

    [Fact]
    public void AFamilyIsReportedOnceHoweverMuchTextItHas()
    {
        var reports = Export(("NoSuchFamilyA", "one two three four"), ("NoSuchFamilyA", "five six seven"));
        var d = Assert.Single(reports);
        Assert.Contains("'NoSuchFamilyA'", d.Detail, System.StringComparison.Ordinal);
    }

    /// <summary>Two missing families are two facts: one report each, each naming its own.</summary>
    [Fact]
    public void EachFamilyIsReported()
    {
        var reports = Export(("NoSuchFamilyA", "one two"), ("NoSuchFamilyB", "three four"), ("NoSuchFamilyA", "five"));
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, d => d.Detail.Contains("'NoSuchFamilyA'", System.StringComparison.Ordinal));
        Assert.Contains(reports, d => d.Detail.Contains("'NoSuchFamilyB'", System.StringComparison.Ordinal));
    }
}
