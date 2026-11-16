// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The PDF exporter asks <c>Core.Writing.LinkTargets</c> before it writes a <c>/URI</c> action (2026-10-20);
/// the rule itself is tested in Core.
/// </summary>
public class LinkTargetPdfExportTests
{
    /// <summary>
    /// The PDF exporter writes no <c>/URI</c> action for a <c>javascript:</c> target — the text stays, and it is
    /// reported once — where it wrote any target it was given; an https one, the control, is written.
    /// </summary>
    [Fact]
    public void ThePdfExporter_WritesNoActionForAnUnsafeTarget()
    {
        string Raw(string target, List<BuilderDiagnostic> seen)
        {
            var doc = new Document();
            var section = new Section();
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = "click", LinkUri = target });
            section.Children.Add(p);
            doc.Sections.Add(section);
            void Collect(BuilderDiagnostic d) => seen.Add(d);
            BuilderDiagnostics.OnEvent += Collect;
            try { return PdfContentProbe.Raw(doc.ToPdf()); }
            finally { BuilderDiagnostics.OnEvent -= Collect; }
        }

        var unsafeSeen = new List<BuilderDiagnostic>();
        string unsafeRaw = Raw("javascript:alert(1)", unsafeSeen);
        Assert.DoesNotContain("/URI", unsafeRaw, System.StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", unsafeRaw, System.StringComparison.Ordinal);
        Assert.Contains(unsafeSeen, d => d.Detail.Contains("was not written", System.StringComparison.Ordinal));

        string safeRaw = Raw("https://x.example/a", new List<BuilderDiagnostic>());
        Assert.Contains("/URI (https://x.example/a)", safeRaw, System.StringComparison.Ordinal);
    }
}
