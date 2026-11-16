// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// The same placeholder used more than once, which is the normal case in a real template — a name
/// in a header and again in a signature block, a date in three places.
///
/// <para>Two things this pins, both found by reviewing the implementation rather than by a failing
/// test. <b>Every occurrence must be replaced</b>, not just the first: replacing one and leaving the
/// rest produces a document that looks finished at a glance. And <b>the report is keyed by
/// placeholder name</b>, so a key used three times appears once — otherwise
/// <c>FilledTokens.Count</c> means "occurrences" in some documents and "keys" in others, and the
/// documented invariant that every supplied key lands in exactly one bucket quietly stops holding.
/// </para>
/// </summary>
public class RepeatedTokenTests
{
    private static byte[] TokenThreeTimes() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
        TemplateFixtures.StreamBody(string.Empty,
            "BT /F1 12 Tf 72 700 Td (Dear {{name}},) Tj ET\n" +
            "BT /F1 12 Tf 72 660 Td (We wrote to {{name}} before.) Tj ET\n" +
            "BT /F1 12 Tf 72 620 Td (Yours, {{name}}) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    private static (TemplateFillReport report, byte[] output) Fill(string value)
    {
        using var input = new MemoryStream(TokenThreeTimes());
        using var output = new MemoryStream();
        var report = PdfTemplateFiller.Fill(input, output,
            new Dictionary<string, string> { ["name"] = value });
        return (report, output.ToArray());
    }

    private static string ExtractText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc);
    }

    [Fact]
    public void EveryOccurrenceIsErased_NotJustTheFirst()
    {
        var (_, output) = Fill("Dana");
        string text = ExtractText(output);

        Assert.DoesNotContain("{{name}}", text);
        Assert.DoesNotContain("{{", text);

        // ...and the surrounding text of all three lines survived.
        Assert.Contains("Dear", text);
        Assert.Contains("before", text);
        Assert.Contains("Yours", text);
    }

    /// <summary>
    /// The report is keyed by name. Three occurrences of one key are one entry.
    /// </summary>
    [Fact]
    public void ARepeatedKeyIsReportedOnce()
    {
        var (report, _) = Fill("Dana");

        Assert.Single(report.FilledTokens);
        Assert.Equal("name", report.FilledTokens[0]);
        Assert.Empty(report.UnmatchedKeys);
        Assert.Empty(report.NotEditableKeys);
    }

    /// <summary>
    /// The invariant stated on <see cref="TemplateFillReport"/>, checked directly: every supplied
    /// key is in exactly one bucket, once.
    /// </summary>
    [Fact]
    public void EverySuppliedKeyIsInExactlyOneBucketExactlyOnce()
    {
        var (report, _) = Fill("Dana");

        var buckets = report.FilledFields
            .Concat(report.FilledTokens)
            .Concat(report.UnmatchedKeys)
            .Concat(report.NotEditableKeys)
            .ToList();

        Assert.Equal(1, buckets.Count(k => k == "name"));
    }
}
