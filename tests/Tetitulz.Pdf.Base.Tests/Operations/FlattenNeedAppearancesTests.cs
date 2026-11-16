// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §12.7.3.3 <c>/NeedAppearances</c> in <see cref="PdfFormFlattener"/> — the irreversible half.
/// </summary>
/// <remarks>
/// <para><b>Why this is worse than the render-time case it followed.</b> A render is repeatable, so
/// drawing the blank template appearance is a wrong picture you can take again. Flattening stamps
/// that appearance into the page content <i>and then removes the <c>/AcroForm</c></i> that said the
/// field had a value — so the document that comes out has neither the value nor any record that one
/// existed. It was found by fixing the render-time half, not by a survey.</para>
///
/// <para><b>These share the render tests' fixture on purpose.</b> The whole claim is that a page
/// flattens to what it renders as, so both suites drive
/// <c>NeedAppearancesRegenerationTests.FormPdf</c>. A second fixture here would let the two drift
/// while both stayed green, which is this repository's recurring defect class.</para>
/// </remarks>
public class FlattenNeedAppearancesTests
{
    /// <summary>
    /// The structural claim: the stamped form XObject shows the field's value. Asserted on the
    /// decoded content stream rather than on pixels, because a stamped-but-blank appearance and a
    /// stamped-and-drawn one both produce a perfectly valid page.
    /// </summary>
    [Fact]
    public void StaleAppearanceIsRegeneratedBeforeItIsStamped()
    {
        string stamped = StampedContent(Flatten(Stale()));

        Assert.Contains(NeedAppearancesRegenerationTests.Value, stamped);
    }

    /// <summary>
    /// The control that makes the test above mean something: without the flag the document's own
    /// appearance is authoritative, and it is empty.
    /// </summary>
    [Fact]
    public void WithoutTheFlagTheDocumentsOwnAppearanceIsStamped()
    {
        string stamped = StampedContent(Flatten(NotStale()));

        Assert.DoesNotContain(NeedAppearancesRegenerationTests.Value, stamped);
    }

    /// <summary>
    /// The whole chain, end to end and in ink: the flattened page — which no longer has a widget,
    /// so the renderer's own §12.7.3.3 fallback cannot run and cannot flatter the result — draws the
    /// value.
    /// </summary>
    [Fact]
    public void TheFlattenedPageDrawsTheValue()
    {
        Assert.True(InteriorInk(Render(Flatten(Stale()))) > 0,
            "the regenerated appearance should have been stamped into the page content");
    }

    /// <summary>The pixel control, mirroring the structural one.</summary>
    [Fact]
    public void TheFlattenedPageWithoutTheFlagDrawsNothing()
    {
        Assert.Equal(0, InteriorInk(Render(Flatten(NotStale()))));
    }

    /// <summary>
    /// The flattened output must agree with what the renderer shows for the same input — the
    /// property the shared predicate exists to guarantee, and the one a per-caller copy would let
    /// drift.
    /// </summary>
    [Fact]
    public void FlattenedAndRenderedOutputAgree()
    {
        Assert.Equal(
            InteriorInk(Render(Stale())) > 0,
            InteriorInk(Render(Flatten(Stale()))) > 0);
    }

    /// <summary>
    /// <c>GenerateAppearances = false</c> restores the pre-2026-09-02 behaviour verbatim, for a
    /// caller who wants the file's own bytes stamped whatever the flag says.
    /// </summary>
    [Fact]
    public void GenerateAppearancesFalseStampsTheDocumentsOwnAppearance()
    {
        string stamped = StampedContent(
            Flatten(Stale(), new FormFillOptions { GenerateAppearances = false }));

        Assert.DoesNotContain(NeedAppearancesRegenerationTests.Value, stamped);
    }

    /// <summary>
    /// A regeneration is <b>reported</b>. Flattening is irreversible, so "the value you can no
    /// longer recover was redrawn by us rather than taken from the file" is not something a caller
    /// may have to infer.
    /// </summary>
    [Fact]
    public void RegenerationIsReported()
    {
        var seen = new List<string>();
        void Handler(string detail) { lock (seen) seen.Add(detail); }

        DocumentEditDiagnostics.OnEvent += Handler;
        try { Flatten(Stale()); }
        finally { DocumentEditDiagnostics.OnEvent -= Handler; }

        Assert.Contains(seen, d => d != null && d.Contains("/NeedAppearances"));
    }

    /// <summary>
    /// The failure path, and it must fail <i>loudly and harmlessly</i>: a Hebrew value needs glyphs
    /// no supplied font has, so the document's own blank appearance is stamped — never a hole where
    /// the field was — and the reason names the setting that would have fixed it.
    /// </summary>
    [Fact]
    public void AValueNeedingAFontNobodySuppliedKeepsTheDocumentsAppearanceAndSaysWhy()
    {
        var seen = new List<string>();
        void Handler(string detail) { lock (seen) seen.Add(detail); }

        byte[] pdf = NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", value: "שלום");

        byte[] flattened;
        DocumentEditDiagnostics.OnEvent += Handler;
        try { flattened = Flatten(pdf); }
        finally { DocumentEditDiagnostics.OnEvent -= Handler; }

        // The widget is still stamped — the page keeps a form XObject for it.
        Assert.NotNull(StampedContent(flattened));
        Assert.Contains(seen, d => d != null && d.Contains("FontProvider"));
    }

    /// <summary>
    /// The §12.5.5 placement must be computed from the appearance that is actually stamped.
    /// </summary>
    /// <remarks>
    /// <b>Written for a defect found by reviewing the diff, not by a failing test.</b> The first
    /// version handed the placement a <c>doc.ResolveObject(reference)</c> for a stream that exists
    /// only in the revision being staged — so it resolved to null, fell back to the <i>old</i>
    /// appearance, and computed the fit matrix from the template's <c>/BBox</c> while drawing the
    /// new stream. Every other test here missed it because the fixture's template <c>/BBox</c>
    /// happens to equal the widget rect, making the two answers identical. This one gives the
    /// template a quarter-size box, so the stale reading scales the value 4× and the correct one
    /// maps 1:1.
    /// </remarks>
    [Fact]
    public void PlacementUsesTheRegeneratedAppearanceNotTheTemplateBBox()
    {
        byte[] pdf = NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", appearanceBBox: "[0 0 40 10]");

        // 4x oversized text overflows the rect and is clipped to the /BBox, so the ink differs
        // sharply from the 1:1 case. Compared against the same document flattened with the
        // template box, which is the shape the defect produced.
        int correct = InteriorInk(Render(Flatten(pdf)));
        int reference = InteriorInk(Render(Flatten(Stale())));

        Assert.True(correct > 0, "the regenerated value should still be drawn");
        Assert.Equal(reference, correct);
    }

    /// <summary>
    /// A form-wide <c>/AcroForm /Q</c> aligns the regenerated value, as it does when the same field
    /// is filled.
    /// </summary>
    /// <remarks>
    /// The flattener climbed the widget's <c>/Parent</c> chain for <c>/Q</c> and stopped there, so a
    /// form that right-aligns every field once, at the form level, was filled right-aligned and
    /// flattened left-aligned. "Hello" at 12 pt in the 160-wide widget, 2 pt margin each side.
    /// </remarks>
    [Fact]
    public void AFormWideQuaddingAlignsTheRegeneratedValue()
    {
        string stamped = StampedContent(Flatten(
            NeedAppearancesRegenerationTests.FormPdf("/NeedAppearances true /Q 2")));

        double textWidth = NeedAppearancesRegenerationTests.Value
            .Sum(c => Standard14Metrics.GetWidthByUnicode("Helvetica", c)) / 1000.0 * 12;
        Assert.Equal(2 + (156 - textWidth), FirstTextX(stamped), precision: 1);
    }

    /// <summary>
    /// <b>Comb is a text-field flag.</b> A choice widget whose <c>/Ff</c> carries bit 25 is still
    /// drawn as one run of text — the filler's reading, which the flattener's own copy of the rule
    /// had dropped: it tested the bit alone and split this value into five comb cells.
    /// </summary>
    [Fact]
    public void AChoiceWidgetCarryingTheCombBitIsNotLaidOutAsAComb()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Ch /Ff " + CombBit + " /MaxLen 5")));

        Assert.Equal(1, ShowTextCount(stamped));
        Assert.Contains("(" + NeedAppearancesRegenerationTests.Value + ") Tj", stamped);
    }

    /// <summary>The control: the same bit on a text field does lay the value out in cells.</summary>
    [Fact]
    public void ATextWidgetCarryingTheCombBitIsLaidOutAsAComb()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + CombBit + " /MaxLen 5")));

        Assert.Equal(NeedAppearancesRegenerationTests.Value.Length, ShowTextCount(stamped));
    }

    /// <summary>
    /// <b>Multiline is a text-field flag too.</b> A value wider than its widget wraps in a
    /// multiline text field and stays one line in a choice field carrying the same bit.
    /// </summary>
    [Fact]
    public void AChoiceWidgetCarryingTheMultilineBitIsNotWrapped()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Ch /Ff " + MultilineBit, value: WiderThanTheWidget)));

        Assert.Equal(1, ShowTextCount(stamped));
    }

    /// <summary>The control: the same value in a multiline text field wraps onto several lines.</summary>
    [Fact]
    public void ATextWidgetCarryingTheMultilineBitWraps()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + MultilineBit, value: WiderThanTheWidget)));

        Assert.True(ShowTextCount(stamped) > 1, "a multiline text field should wrap this value: " + stamped);
    }

    /// <summary>
    /// <b>A password is never baked into the page.</b> Flattening makes an appearance permanent, so a
    /// password field regenerated with its value drawn would put the secret into page content that no
    /// later edit to the form can reach. Table 229 bit 14 asks for asterisks, and the stamp has them.
    /// </summary>
    [Fact]
    public void AFlattenedPasswordFieldIsStampedAsAsterisks()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + PasswordBit)));

        Assert.Contains("(*****) Tj", stamped);
        Assert.DoesNotContain(NeedAppearancesRegenerationTests.Value, stamped);
    }

    /// <summary>
    /// Comb is ignored on a password field (§12.7.4.3), so the asterisks are one run of text, not
    /// one per cell.
    /// </summary>
    [Fact]
    public void APasswordFieldCarryingTheCombBitIsNotLaidOutAsAComb()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + (CombBit | PasswordBit) + " /MaxLen 5")));

        Assert.Equal(1, ShowTextCount(stamped));
    }

    /// <summary>Comb is ignored on a file-select field too.</summary>
    [Fact]
    public void AFileSelectFieldCarryingTheCombBitIsNotLaidOutAsAComb()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + (CombBit | FileSelectBit) + " /MaxLen 5")));

        Assert.Equal(1, ShowTextCount(stamped));
    }

    /// <summary>
    /// And on a multiline field, which wraps instead: the value fits one line, so it is one run.
    /// </summary>
    [Fact]
    public void AMultilineFieldCarryingTheCombBitIsNotLaidOutAsAComb()
    {
        string stamped = StampedContent(Flatten(NeedAppearancesRegenerationTests.FormPdf(
            "/NeedAppearances true", fieldType: "/Tx /Ff " + (CombBit | MultilineBit) + " /MaxLen 5")));

        Assert.Equal(1, ShowTextCount(stamped));
    }

    /// <summary>Table 229 bit 14, Password.</summary>
    private const int PasswordBit = 1 << 13;

    /// <summary>Table 229 bit 21, FileSelect.</summary>
    private const int FileSelectBit = 1 << 20;

    /// <summary>Table 226 bit 25, Comb.</summary>
    private const int CombBit = 1 << 24;

    /// <summary>Table 226 bit 13, Multiline.</summary>
    private const int MultilineBit = 1 << 12;

    /// <summary>
    /// Eight words of Helvetica 12, about 245 pt — well past the fixture widget's 160 pt. (Five
    /// words come to about 150 pt, which fits, and made the wrapping control fail for that reason.)
    /// </summary>
    private const string WiderThanTheWidget = "Hello Hello Hello Hello Hello Hello Hello Hello";

    private static int ShowTextCount(string content) => Regex.Matches(content, @"\) Tj").Count;

    // ------------------------------------------------------------------

    /// <summary>The x of the first <c>Td</c>, which is where the first line of text starts.</summary>
    private static double FirstTextX(string content)
    {
        var match = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) Td");
        Assert.True(match.Success, "no Td operator in the stamped content: " + content);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static byte[] Stale() =>
        NeedAppearancesRegenerationTests.FormPdf("/NeedAppearances true");

    private static byte[] NotStale() =>
        NeedAppearancesRegenerationTests.FormPdf(acroFormExtra: "");

    private static byte[] Flatten(byte[] pdf, FormFillOptions options = null)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfFormFlattener.Flatten(input, output, options);
        return output.ToArray();
    }

    /// <summary>
    /// The decoded content of every form XObject the flattener stamped onto page 0, concatenated.
    /// Named <c>FlatXO*</c> by <c>StampWidgetsOnPage</c>, which is how the widget's appearance is
    /// told apart from anything the page already carried.
    /// </summary>
    private static string StampedContent(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var xobjects = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        var sb = new StringBuilder();
        foreach (var kv in xobjects!)
        {
            if (!kv.Key.Value.StartsWith("FlatXO", System.StringComparison.Ordinal)) continue;
            if (doc.ResolveObject(kv.Value) is not PdfStream s) continue;
            sb.Append(Encoding.Latin1.GetString(s.GetDecodedData()));
        }

        Assert.True(sb.Length >= 0);
        return sb.ToString();
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// Dark pixels strictly inside the widget rect, on the render tests' terms — the widget sits at
    /// <c>[20 20 180 60]</c> on a 200-point page, so at 72&#160;dpi one point is one pixel.
    /// </summary>
    private static int InteriorInk(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 145; y < 176; y++)
            for (int x = 24; x < 171; x++)
                if (bmp.GetPixel(x, y).Red < 128) ink++;
        return ink;
    }
}
