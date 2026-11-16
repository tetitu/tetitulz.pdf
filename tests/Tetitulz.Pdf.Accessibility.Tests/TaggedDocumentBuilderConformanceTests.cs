// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>Base.Operations.Structure.PdfTaggedDocumentBuilder</c> declares PDF/UA-1 in its XMP, so its
/// output has to pass this library's own audit — both checkers, the plugin's and the deep one.
/// </summary>
/// <remarks>
/// <b>It did not, and nothing asserted it.</b> The builder's own tests read back the structure
/// roles, the title and the <c>pdfuaid:part</c> declaration; none handed the file to a checker. It
/// drew its text in a non-embedded Helvetica (UA21 / UA25), which the audit reports on every page.
/// The font now comes from the caller and is embedded.
/// </remarks>
public class TaggedDocumentBuilderConformanceTests
{
    private static readonly byte[] Font = MinimalTrueType.Build(
        new string(Enumerable.Range(0x20, 0x5F).Select(c => (char)c).ToArray()));

    [Fact]
    public void TheTaggedDocumentBuildersOutputPassesTheAudit()
    {
        var output = new MemoryStream();
        new PdfTaggedDocumentBuilder()
            .SetFontProvider(_ => Font)
            .SetTitle("Annual report")
            .SetLanguage("en-US")
            .AddHeading(1, "Annual report")
            .AddParagraph("Revenue grew in every quarter.")
            .AddHeading(2, "Outlook")
            .AddParagraph("The plan for next year is unchanged.")
            .Build(output);

        var report = PdfAccessibility.Audit(new MemoryStream(output.ToArray()));

        Assert.True(report.IsConforming,
            "the builder declares PDF/UA-1, and the audit found: " +
            string.Join("; ", report.Issues.Where(i => i.Severity == Models.IssueSeverity.Error)
                                           .Select(i => i.Code + " " + i.Message)));
    }
}
