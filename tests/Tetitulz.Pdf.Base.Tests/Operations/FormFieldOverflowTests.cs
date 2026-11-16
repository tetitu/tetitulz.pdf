// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A value too long for its widget is <b>reported</b>, not silently truncated.
///
/// <para><b>The bug these pin.</b> Auto-sizing shrinks a multiline block until it fits, but stops at
/// a 4 pt floor. Past that, <c>FormAppearance.AppendLines</c> simply stopped emitting lines — a bare
/// <c>break</c> once the baseline fell below the box — and said nothing. The fill returned success,
/// <c>FieldsWithoutAppearance</c> was empty, <c>Warnings</c> was empty, and the tail of the value
/// was gone. A caller had no way to tell a complete document from a truncated one.</para>
///
/// <para>The horizontal twin of this was already guarded: a single word too wide for the box is
/// broken by character rather than left to overflow, under a comment saying the alternative would
/// "silently hide the rest of the value". The vertical axis had never had that rule applied.</para>
///
/// <para><b>These assert on the warning and on the drawn content</b>, not on the call returning
/// something: a truncated appearance is a perfectly valid stream, which is exactly why the defect
/// survived.</para>
/// </summary>
public class FormFieldOverflowTests
{
    /// <summary>A multiline text field 60 pt wide and 15 pt tall — room for about two 4 pt lines.</summary>
    private static byte[] TinyMultilineField() => FormPdfFixtures.TextField(
        rect: "[100 700 160 715]",
        fieldEntries: "/DA (/Helv 12 Tf 0 g) /Ff 4096");

    private static Dictionary<string, string> Values(string value)
        => new Dictionary<string, string> { ["name"] = value };

    /// <summary>Long enough that no font size down to the 4 pt floor fits it in 60×15 pt.</summary>
    private const string TooLong =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. " +
        "How vexingly quick daft zebras jump. Sphinx of black quartz, judge my vow. " +
        "Jackdaws love my big sphinx of quartz and the five boxing wizards jump quickly.";

    [Fact]
    public void AValueTooLongForItsWidget_IsReportedRatherThanSilentlyTruncated()
    {
        using var input = new MemoryStream(TinyMultilineField());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output, Values(TooLong), new FormFillOptions());

        Assert.Contains("name", result.FilledFields);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("does not fit"));
        Assert.Contains(result.Warnings, w => w.Contains("not drawn"));
    }

    /// <summary>
    /// The control. Without it, an implementation that warned unconditionally would pass the test
    /// above — and the warning would be worthless because it would never distinguish anything.
    /// </summary>
    [Fact]
    public void AValueThatFits_ProducesNoOverflowWarning()
    {
        using var input = new MemoryStream(TinyMultilineField());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output, Values("Hi"), new FormFillOptions());

        Assert.Contains("name", result.FilledFields);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("does not fit"));
    }

    /// <summary>
    /// Truncation is <b>not</b> an appearance failure: everything that fits really is drawn, and the
    /// field is not listed as undrawn. Conflating the two would tell callers to regenerate the
    /// appearance in a viewer, which cannot help — the widget is not going to get any taller.
    /// </summary>
    [Fact]
    public void OverflowIsAWarningNotAnAppearanceFailure()
    {
        using var input = new MemoryStream(TinyMultilineField());
        using var output = new MemoryStream();

        var result = PdfFormFiller.Fill(input, output, Values(TooLong), new FormFillOptions());

        Assert.Empty(result.FieldsWithoutAppearance);
        Assert.False(result.NeedAppearances);
    }

    /// <summary>
    /// The warning has to be <b>true</b>, not merely present: the drawn appearance must actually be
    /// missing text. Asserted against the decoded appearance stream, so an implementation that
    /// warned while in fact drawing everything would fail here.
    /// </summary>
    [Fact]
    public void TheReportedOverflowMatchesAnAppearanceThatReallyIsMissingText()
    {
        string drawn = DrawnAppearance(TooLong);

        // The value's own tail must not appear in what was drawn.
        Assert.DoesNotContain("wizards", drawn);

        // ...while its head does, so this is truncation rather than the appearance being empty.
        Assert.Contains("quick", drawn);
    }

    /// <summary>The control for the assertion above: a short value is drawn in full.</summary>
    [Fact]
    public void AValueThatFits_IsDrawnInFull()
    {
        Assert.Contains("Hi", DrawnAppearance("Hi"));
    }

    /// <summary>The decoded <c>/AP /N</c> content stream of the filled field.</summary>
    private static string DrawnAppearance(string value)
    {
        using var input = new MemoryStream(TinyMultilineField());
        using var output = new MemoryStream();
        PdfFormFiller.Fill(input, output, Values(value), new FormFillOptions());

        output.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(output);
        var field = Assert.IsAssignableFrom<Tetitulz.Pdf.Core.Objects.PdfDictionary>(
            doc.GetObject(FormPdfFixtures.FirstFieldObj));
        var ap = Assert.IsAssignableFrom<Tetitulz.Pdf.Core.Objects.PdfDictionary>(
            doc.ResolveObject(field[Tetitulz.Pdf.Core.Objects.PdfName.AP]));
        var normal = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(
            doc.ResolveObject(ap[Tetitulz.Pdf.Core.Objects.PdfName.N]));

        return System.Text.Encoding.GetEncoding("iso-8859-1").GetString(normal.GetDecodedData());
    }
}
