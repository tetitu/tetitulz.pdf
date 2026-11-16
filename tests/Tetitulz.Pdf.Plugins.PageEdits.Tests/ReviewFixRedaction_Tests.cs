// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using System.Reflection;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// Review finding #1: the redactor only DRAWS AN OPAQUE COVER over a region; it does NOT
/// remove the underlying text/image operators, so the hidden content is still extractable.
///
/// <para>The honest-documentation path was taken (true operator-removal is an open gap):
/// the public method is now <see cref="PdfRedactor.CoverRegions"/> with cover-only semantics,
/// the misleadingly named <c>Redact</c> is kept as an <see cref="ObsoleteAttribute"/> alias,
/// and the XML docs state the limitation prominently. These tests LOCK IN that honest
/// behaviour so the dangerous "permanently obscuring" guarantee cannot silently come back.</para>
/// </summary>
public class ReviewFixRedaction_Tests
{
    private const string SecretText = "TopSecretSSN123456789";

    [Fact]
    public void CoverRegions_ProducesValidPdf()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(SecretText);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.CoverRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 90, Y = 690, Width = 200, Height = 30 },
        });

        // CoverRegions paints an opaque box over the region; it does NOT remove the underlying
        // text (that is RedactRegions' job, and the distinction matters for anyone choosing
        // between them). Assert both halves: the document still opens, and the cover really was
        // added — the output differs from the input and carries a fill operator it did not have.
        byte[] produced = output.ToArray();
        Assert.NotEqual(pdf, produced);

        using var doc = PdfDocument.Open(new MemoryStream(produced));
        Assert.Single(doc.Pages);

        string content = Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains(" re", content);
        Assert.Contains(SecretText, Encoding.ASCII.GetString(produced));   // covered, not removed
    }

    [Fact]
    public void CoverRegions_DoesNotRemoveUnderlyingText_KnownLimitation()
    {
        // Honest-behaviour lock-in: the covered text is STILL extractable. If true
        // content-removal redaction is ever implemented (the open gap), this assertion
        // must flip to Assert.DoesNotContain — at which point the cover-only docs/obsolete
        // alias should be revisited too.
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf(SecretText);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        PdfRedactor.CoverRegions(input, output, new[]
        {
            new RedactionRegion { PageIndex = 0, X = 90, Y = 690, Width = 200, Height = 30 },
        });

        output.Position = 0;
        string extracted = PdfOperations.ExtractText(output);

        Assert.Contains(SecretText, extracted);
    }

    [Fact]
    public void Redact_IsMarkedObsolete_AndForwardsToCoverRegions()
    {
        // The dangerous-sounding name is retained for source compatibility but flagged so
        // callers are warned it is a cover, not a true redaction.
        var method = typeof(PdfRedactor).GetMethod(
            nameof(PdfRedactor.Redact),
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<ObsoleteAttribute>());

        // It still works (forwards to CoverRegions): validation contract is preserved.
        var regions = new List<RedactionRegion>
        {
            new RedactionRegion { PageIndex = -1, X = 0, Y = 0, Width = 10, Height = 10 },
        };
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("x"));
        using var output = new MemoryStream();
#pragma warning disable CS0618 // deliberately exercising the obsolete alias
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfRedactor.Redact(input, output, regions));
#pragma warning restore CS0618
        Assert.Equal("redactions", ex.ParamName);
    }
}
