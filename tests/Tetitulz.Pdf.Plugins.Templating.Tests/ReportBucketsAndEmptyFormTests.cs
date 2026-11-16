// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Templating.Tests;

/// <summary>
/// Two claims this plugin makes in prose, pinned so the prose and the behaviour cannot drift
/// apart: what <c>HasAcroForm</c> means, and how many buckets a supplied key can land in.
/// </summary>
public class ReportBucketsAndEmptyFormTests
{
    private static Dictionary<string, string> Values(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    private static (TemplateFillReport report, byte[] output) Fill(
        byte[] template, Dictionary<string, string> values, TemplateOptions options = null)
    {
        using var input = new MemoryStream(template);
        using var output = new MemoryStream();
        var report = PdfTemplateFiller.Fill(input, output, values, options);
        return (report, output.ToArray());
    }

    /// <summary>A form-capable template with an empty <c>/Fields</c> array — what XFA-stripped and
    /// form-less-but-form-capable generators emit.</summary>
    private static byte[] AcroFormWithNoFields() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>",
        "<< /Type /Pages /Kids [4 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
        "<< /Fields [] /DR << /Font << /Helv 5 0 R >> >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

    /// <summary>
    /// <c>HasAcroForm</c>'s summary says "with at least one field"; its body used to say
    /// <c>doc.AcroForm != null</c>. A field-less template therefore took the form-fill branch,
    /// matched nothing, and still had an incremental update appended to it — so the caller's
    /// document grew a revision that changed no content at all, on a path a maintainer reading the
    /// summary would have believed unreachable.
    ///
    /// <para>Asserting on the <b>bytes</b> rather than on the report is what makes this able to
    /// fail: the report says "unmatched" either way.</para>
    /// </summary>
    [Fact]
    public void A_field_less_AcroForm_does_not_have_a_no_op_revision_appended_to_it()
    {
        byte[] template = AcroFormWithNoFields();

        var (report, output) = Fill(template, Values(("name", "Dana")),
                                    new TemplateOptions { Sources = TemplateSources.FormFields });

        Assert.Contains("name", report.UnmatchedKeys);
        Assert.False(report.ChangedAnything);
        Assert.Equal(template, output);
    }

    /// <summary>
    /// The counterpart that keeps the change narrow: a template that really does declare a field
    /// must still take the form-fill branch. A <c>HasAcroForm</c> that answered false for
    /// everything would satisfy the test above and disable form filling outright.
    /// </summary>
    [Fact]
    public void A_template_with_a_field_still_fills()
    {
        var (report, output) = Fill(TemplateFixtures.TextField(), Values(("name", "Dana")),
                                    new TemplateOptions { Sources = TemplateSources.FormFields });

        Assert.Contains("name", report.FilledFields);
        Assert.True(output.Length > 0);
    }

    /// <summary>
    /// <see cref="TemplateFillReport"/>'s class-level invariant named three terminal buckets and
    /// there are four: a token inside a <b>shared</b> Form XObject is filed under
    /// <see cref="TemplateFillReport.NotEditableKeys"/> and under nothing else, so the
    /// reconciliation the old wording invited —
    /// <c>values.Count == FilledFields + FilledTokens + UnmatchedKeys</c> — came up short for
    /// exactly the case where the placeholder really is still printed on the page.
    ///
    /// <para>This asserts the corrected invariant directly, which is the only way the documented
    /// contract can be falsified by a test rather than by a reader.</para>
    /// </summary>
    [Fact]
    public void Every_supplied_key_lands_in_exactly_one_of_the_four_terminal_buckets()
    {
        var values = Values(
            ("name", "Dana"),          // the token in the shared form → NotEditableKeys
            ("nosuchkey", "x"));       // matches nothing → UnmatchedKeys

        var (report, _) = Fill(SharedFormOnTwoPages(), values);

        // Fixture check: without a NotEditable key this test would pass under the OLD three-bucket
        // wording too, and prove nothing.
        Assert.Contains("name", report.NotEditableKeys);

        var buckets = new[]
        {
            report.FilledFields, report.FilledTokens, report.NotEditableKeys, report.UnmatchedKeys,
        };

        foreach (string key in values.Keys)
        {
            int hits = buckets.Count(b => b.Contains(key));
            Assert.True(hits == 1,
                $"'{key}' appears in {hits} of the four terminal buckets; the documented invariant " +
                "is exactly one.");
        }

        Assert.Equal(values.Count, buckets.Sum(b => b.Count));
    }

    /// <summary>The same form drawn from two pages, so it is shared and cannot be edited.</summary>
    private static byte[] SharedFormOnTwoPages() => TemplateFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /XObject << /Body 6 0 R >> >> /Contents 5 0 R >>",
        TemplateFixtures.StreamBody(string.Empty, "/Body Do"),
        TemplateFixtures.StreamBody(
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
            "/Resources << /Font << /F1 7 0 R >> >>",
            "BT /F1 12 Tf 72 700 Td (Dear {{name}}, welcome.) Tj ET"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
}
