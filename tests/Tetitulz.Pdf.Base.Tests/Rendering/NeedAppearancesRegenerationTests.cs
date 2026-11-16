// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §12.7.3.3 <c>/NeedAppearances</c> — when the interactive form sets it, a widget's existing
/// appearance stream is the blank template's and the value must be <i>constructed</i>, not drawn.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>grep NeedAppearances</c> over <c>Base/Rendering/</c> returned nothing
/// while <c>PdfAcroForm</c>, the Templating plugin and <c>FontGlyphUsageScanner</c> all read it, so a
/// form filled by another producer — which writes <c>/V</c>, sets the flag and leaves the template's
/// empty <c>/AP</c> alone — rendered with <b>blank fields</b> in both renderers. That is the same
/// picture a form nobody filled in produces, which is why it survived every visual check.</para>
///
/// <para><b>Why the predicate is asserted directly as well as through pixels.</b> The three
/// narrowings that keep this from being a regression — variable-text fields only, a value must be
/// present, and an appearance that already draws something is kept — are decisions, and a decision
/// asserted only through a rendered page is asserted through whichever of its branches the fixture
/// happens to take. The rendering tests below then prove the decision is actually <i>reached</i> by
/// both renderers, which asking the predicate cannot.</para>
/// </remarks>
public class NeedAppearancesRegenerationTests
{
    /// <summary>Internal so <c>FlattenNeedAppearancesTests</c> asserts against the same fixture.</summary>
    internal const string Value = "Hello";

    // ------------------------------------------------------------------
    //  The document-scoped flag
    // ------------------------------------------------------------------

    /// <summary>The flag is read off <c>/AcroForm</c>, not off the widget.</summary>
    [Theory]
    [InlineData("/NeedAppearances true", true)]
    [InlineData("", false)]
    [InlineData("/NeedAppearances false", false)]
    public void FormNeedsAppearances_ReadsTheAcroFormEntry(string entry, bool expected)
    {
        using var doc = Open(FormPdf(acroFormExtra: entry));
        Assert.Equal(expected, AnnotationFallbackModel.FormNeedsAppearances(doc));
    }

    /// <summary>
    /// Written as an indirect reference — legal under §7.3.10, and the spelling that made
    /// <c>PdfFormFiller.ExistingNeedAppearances</c> read a set flag as absent before it resolved.
    /// </summary>
    [Fact]
    public void FormNeedsAppearances_FollowsAnIndirectReference()
    {
        using var doc = Open(FormPdf(acroFormExtra: "/NeedAppearances 9 0 R", trueObject: true));
        Assert.True(AnnotationFallbackModel.FormNeedsAppearances(doc));
    }

    // ------------------------------------------------------------------
    //  The staleness decision
    // ------------------------------------------------------------------

    /// <summary>The case the gap describes: flag set, value written, template appearance blank.</summary>
    [Fact]
    public void Stale_WhenTheFlagIsSetAndTheAppearanceShowsNothing()
    {
        Assert.True(IsStale(FormPdf("/NeedAppearances true")));
    }

    /// <summary>
    /// The floor. Without the flag the file's appearance is authoritative however empty it is — a
    /// blank field is then simply a blank field.
    /// </summary>
    [Fact]
    public void NotStale_WhenTheFormDoesNotSetTheFlag()
    {
        Assert.False(IsStale(FormPdf(acroFormExtra: "")));
    }

    /// <summary>
    /// A <c>/Btn</c> is excluded on purpose: its appearance is chosen by <c>/AS</c> naming a
    /// sub-state the file supplies, so regenerating would put a synthetic tick where a designed
    /// checkbox, radio or push-button face is.
    /// </summary>
    [Fact]
    public void NotStale_ForAButtonWidget()
    {
        Assert.False(IsStale(FormPdf("/NeedAppearances true", fieldType: "/Btn")));
    }

    /// <summary>
    /// With no value the template appearance carries the field's <c>/MK</c> background and border
    /// and the fallback would draw a bare rectangle — strictly the worse of the two.
    /// </summary>
    [Fact]
    public void NotStale_WhenTheFieldHasNoValue()
    {
        Assert.False(IsStale(FormPdf("/NeedAppearances true", value: null)));
    }

    /// <summary>
    /// A producer that sets the flag <b>and</b> generates appearances anyway is common. Its streams
    /// are not stale, and §12.7.3.3 asking a viewer to regenerate is not a reason to draw worse.
    /// </summary>
    [Fact]
    public void NotStale_WhenTheAppearanceAlreadyShowsText()
    {
        Assert.False(IsStale(FormPdf("/NeedAppearances true",
            appearanceContent: "BT /Helv 12 Tf 2 14 Td (" + Value + ") Tj ET")));
    }

    /// <summary>
    /// <c>Do</c> counts as drawing: an appearance can show the value through a form XObject, and
    /// this scan does not follow one.
    /// </summary>
    [Fact]
    public void NotStale_WhenTheAppearanceInvokesAnXObject()
    {
        Assert.False(IsStale(FormPdf("/NeedAppearances true", appearanceContent: "/Inner Do")));
    }

    /// <summary>
    /// Past <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxWidgetAppearanceScanBytes"/> the
    /// question is answered "it draws" without parsing, so a hostile stream cannot buy a second
    /// full parse. The fixture draws nothing at all and is still kept.
    /// </summary>
    [Fact]
    public void NotStale_WhenTheAppearanceIsOversizedForTheScan()
    {
        Assert.False(IsStale(FormPdf("/NeedAppearances true",
            appearanceContent: new string('\n', 300_000))));
    }

    // ------------------------------------------------------------------
    //  Both renderers reach it
    // ------------------------------------------------------------------

    /// <summary>
    /// The visible claim: the value becomes ink. Counted <b>inside</b> the synthesised border so the
    /// border stroke cannot satisfy it — text is the only thing that reaches the interior.
    /// </summary>
    [Fact]
    public void Raster_DrawsTheValueWhenTheAppearanceIsStale()
    {
        Assert.True(InteriorInk(RenderRaster(FormPdf("/NeedAppearances true"))) > 0,
            "the field's value should have been drawn inside the widget rect");
    }

    /// <summary>
    /// The control that makes the test above mean something: the identical document without the
    /// flag draws the blank appearance, so the interior stays empty.
    /// </summary>
    [Fact]
    public void Raster_DrawsNothingWithoutTheFlag()
    {
        Assert.Equal(0, InteriorInk(RenderRaster(FormPdf(acroFormExtra: ""))));
    }

    /// <summary>
    /// The SVG exporter takes the same decision. Asserted on the parsed document — the fallback's
    /// <c>&lt;text&gt;</c> carries the field's value, and the <c>/AP</c> path cannot produce it
    /// because the appearance stream is empty.
    /// </summary>
    [Fact]
    public void Svg_EmitsTheValueWhenTheAppearanceIsStale()
    {
        Assert.Contains(Value, TextElements(RenderSvg(FormPdf("/NeedAppearances true"))));
    }

    /// <summary>The SVG control, mirroring the raster one.</summary>
    [Fact]
    public void Svg_EmitsNothingWithoutTheFlag()
    {
        Assert.DoesNotContain(Value, TextElements(RenderSvg(FormPdf(acroFormExtra: ""))));
    }

    /// <summary>
    /// A real appearance survives the flag in the exporter too — the widget is drawn by its own
    /// stream, so the fallback's border rectangle is absent from the annotation layer.
    /// </summary>
    [Fact]
    public void Svg_KeepsAnAppearanceThatAlreadyDraws()
    {
        string svg = RenderSvg(FormPdf("/NeedAppearances true",
            appearanceContent: "BT /Helv 12 Tf 2 14 Td (" + Value + ") Tj ET"));

        Assert.DoesNotContain(
            XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "rect"),
            r => r.Attribute("x")?.Value == "20" && r.Attribute("width")?.Value == "160");
    }

    /// <summary>
    /// The regeneration is <i>reported</i>, because the two outcomes are otherwise
    /// indistinguishable to a caller comparing this library's output with another viewer's.
    /// </summary>
    [Fact]
    public void Raster_RegenerationIsReported()
    {
        Assert.Contains(RenderDiagnosticKind.FormFieldAppearanceRegenerated,
            KindsFrom(() => RenderRaster(FormPdf("/NeedAppearances true"))));
    }

    /// <summary>
    /// And the exporter reports it too. <b>This test exists because its absence survived a
    /// mutation:</b> pointing the SVG path's report at a different kind left all 28 tests green,
    /// so the exporter could have stopped saying it had substituted its own drawing and nothing
    /// would have noticed. The reason and the decision are shared; the two <i>reports</i> are one
    /// line each, and one line each is exactly the size of thing that goes uncovered.
    /// </summary>
    [Fact]
    public void Svg_RegenerationIsReported()
    {
        Assert.Contains(RenderDiagnosticKind.FormFieldAppearanceRegenerated,
            KindsFrom(() => RenderSvg(FormPdf("/NeedAppearances true"))));
    }

    /// <summary>
    /// The diagnostic kinds <paramref name="render"/> reports, <b>filtered to this thread</b>:
    /// <see cref="RenderDiagnostics.OnEvent"/> is process-wide, so an unfiltered handler would be
    /// asserting something about the whole test run.
    /// </summary>
    private static System.Collections.Generic.List<RenderDiagnosticKind> KindsFrom(System.Action render)
    {
        var kinds = new System.Collections.Generic.List<RenderDiagnosticKind>();
        int thread = System.Environment.CurrentManagedThreadId;
        void Handler(RenderDiagnosticKind k, string d, System.Exception e)
        {
            if (System.Environment.CurrentManagedThreadId == thread) lock (kinds) kinds.Add(k);
        }

        RenderDiagnostics.OnEvent += Handler;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Handler; }
        return kinds;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The predicate returns the <i>reason</i> rather than reporting it — three callers, three
    /// sinks — so non-null is the "regenerate" signal.
    /// </summary>
    private static bool IsStale(byte[] pdf)
    {
        using var doc = Open(pdf);
        var annot = doc.ResolveObject(new PdfReference(6, 0)) as PdfDictionary;
        var ap = doc.ResolveObject(new PdfReference(7, 0));
        return AnnotationFallbackModel.WidgetAppearanceIsStale(
            annot, doc, ap, AnnotationFallbackModel.FormNeedsAppearances(doc)) != null;
    }

    // ------------------------------------------------------------------
    //  Password fields — ISO 32000-2 Table 229, bit 14
    // ------------------------------------------------------------------

    /// <summary>Table 229 bit 14, Password.</summary>
    private const string PasswordFlag = "/Ff 8192";

    /// <summary>
    /// <b>A password field's value is shown as asterisks</b>, one per character. Table 229: its
    /// characters "shall instead be echoed in some unreadable form, such as asterisks or bullet
    /// characters". Both fallbacks drew the value itself.
    /// </summary>
    [Fact]
    public void DisplayedWidgetValue_MasksAPasswordField()
    {
        using var doc = Open(FormPdf("/NeedAppearances true", fieldType: "/Tx " + PasswordFlag, value: "hunter2"));
        var widget = (PdfDictionary)doc.GetObject(6)!;

        Assert.Equal("*******", AnnotationFallbackModel.DisplayedWidgetValue(widget, doc));
        Assert.Equal("hunter2", AnnotationFallbackModel.ReadWidgetValue(widget, doc));
    }

    /// <summary>One asterisk per character typed: a supplementary-plane character is one, not two.</summary>
    [Fact]
    public void DisplayedWidgetValue_CountsCharactersNotUtf16Units()
    {
        using var doc = Open(FormPdf("/NeedAppearances true", fieldType: "/Tx " + PasswordFlag, value: "a\U0001F600"));

        Assert.Equal("**", AnnotationFallbackModel.DisplayedWidgetValue((PdfDictionary)doc.GetObject(6)!, doc));
    }

    /// <summary>
    /// The flag is a <b>text</b>-field flag (Table 229); on a choice field bit 14 means nothing, and
    /// the value is shown as it is.
    /// </summary>
    [Fact]
    public void DisplayedWidgetValue_LeavesAChoiceFieldCarryingThePasswordBitAlone()
    {
        using var doc = Open(FormPdf("/NeedAppearances true", fieldType: "/Ch " + PasswordFlag, value: "hunter2"));

        Assert.Equal("hunter2", AnnotationFallbackModel.DisplayedWidgetValue((PdfDictionary)doc.GetObject(6)!, doc));
    }

    /// <summary>The control: an ordinary text field shows what it holds.</summary>
    [Fact]
    public void DisplayedWidgetValue_LeavesAnOrdinaryFieldAlone()
    {
        using var doc = Open(FormPdf("/NeedAppearances true"));

        Assert.Equal(Value, AnnotationFallbackModel.DisplayedWidgetValue((PdfDictionary)doc.GetObject(6)!, doc));
    }

    [Fact]
    public void SvgFallback_DrawsAPasswordAsAsterisks()
    {
        var texts = TextElements(RenderSvg(
            FormPdf("/NeedAppearances true", fieldType: "/Tx " + PasswordFlag, value: "hunter2")));

        Assert.Contains("*******", texts);
        Assert.DoesNotContain(texts, t => t.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void RasterFallback_DrawsAPasswordExactlyAsItDrawsAsterisks()
    {
        // The raster path cannot be read back as text, so it is compared with a field whose value
        // really is five asterisks: identical pixels, or the password was drawn as itself.
        using var password = RenderRaster(FormPdf("/NeedAppearances true", fieldType: "/Tx " + PasswordFlag, value: "Hello"));
        using var asterisks = RenderRaster(FormPdf("/NeedAppearances true", value: "*****"));
        using var plain = RenderRaster(FormPdf("/NeedAppearances true", value: "Hello"));

        Assert.Equal(Pixels(asterisks), Pixels(password));
        Assert.NotEqual(Pixels(plain), Pixels(password));   // the fixture can tell the two apart
    }

    private static uint[] Pixels(SKBitmap bmp) => bmp.Pixels.Select(c => (uint)c).ToArray();

    /// <summary>
    /// Dark pixels strictly inside the widget rect — the fallback's 1&#160;pt border sits on
    /// x = 20/180 and y = 140/180 in device space at 72&#160;dpi, so this region can only be reached
    /// by the drawn value.
    /// </summary>
    private static int InteriorInk(SKBitmap bmp)
    {
        int ink = 0;
        for (int y = 145; y < 176; y++)
            for (int x = 24; x < 171; x++)
                if (bmp.GetPixel(x, y).Red < 128) ink++;
        return ink;
    }

    private static SKBitmap RenderRaster(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string[] TextElements(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .Select(e => e.Value).ToArray();

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf));

    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page form: a text widget at <c>[20 20 180 60]</c> whose <c>/AP</c> <c>/N</c> is the
    /// blank template appearance, plus whatever the case under test varies.
    ///
    /// <para>Internal because <c>FlattenNeedAppearancesTests</c> asserts on the <b>same</b> document
    /// through the flattener. The renderers and the flattener must agree about a file, so testing
    /// them against one fixture is the point rather than a convenience.</para>
    /// </summary>
    /// <param name="acroFormExtra">Extra entries for the <c>/AcroForm</c> dictionary.</param>
    /// <param name="fieldType">The widget's <c>/FT</c>.</param>
    /// <param name="value">The field's <c>/V</c>, or null for none.</param>
    /// <param name="appearanceContent">The template appearance's content stream.</param>
    /// <param name="trueObject">Makes object 9 the boolean <c>true</c>, for the indirect-flag case.</param>
    /// <param name="appearanceBBox">
    /// The template appearance's <c>/BBox</c>. Defaults to the widget rect's size; a case that wants
    /// to know <i>which</i> appearance drove the §12.5.5 placement passes a different one.
    /// </param>
    internal static byte[] FormPdf(
        string acroFormExtra,
        string fieldType = "/Tx",
        string value = Value,
        string appearanceContent = "",
        bool trueObject = false,
        string appearanceBBox = "[0 0 160 40]")
    {
        string valueEntry = value == null ? "" : " /V " + ValueLiteral(value);

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] "
                + acroFormExtra + " /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << >> /Annots [6 0 R] >>",
            StreamObj(""),
            "null",
            "<< /Type /Annot /Subtype /Widget /FT " + fieldType + " /T (f1)" + valueEntry
                + " /Rect [20 20 180 60] /AP << /N 7 0 R >> >>",
            StreamObj(appearanceContent,
                "/Type /XObject /Subtype /Form /BBox " + appearanceBBox
                + " /Resources << /Font << /Helv 8 0 R >> /XObject << /Inner 10 0 R >> >>"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            trueObject ? "true" : "null",
            StreamObj("", "/Type /XObject /Subtype /Form /BBox [0 0 1 1]"));
    }

    /// <summary>
    /// A <c>/V</c> the file can actually carry: a literal string while every character fits a byte,
    /// and a UTF-16BE hex string (§7.9.2.2) otherwise.
    /// </summary>
    /// <remarks>
    /// <b>This was a literal unconditionally, and the Hebrew test it exists for was vacuous.</b> The
    /// fixture is assembled as text and encoded with <c>Encoding.Latin1</c>, which maps every Hebrew
    /// character to <c>?</c> — so the document really did hold <c>????</c>, the appearance builder
    /// found it single-byte encodable, and the case written to prove a *composite-font refusal*
    /// exercised the simple path and passed for the wrong reason. It failed only because the
    /// assertion named the refusal's own wording.
    /// </remarks>
    private static string ValueLiteral(string value)
    {
        bool latin1 = true;
        foreach (char c in value) if (c > 0xFF) { latin1 = false; break; }
        if (latin1) return "(" + value + ")";

        var hex = new StringBuilder("<FEFF");
        foreach (char c in value) hex.AppendFormat(CultureInfo.InvariantCulture, "{0:X4}", (int)c);
        return hex.Append('>').ToString();
    }

    private static string StreamObj(string data, string extraKeys = null) =>
        "<< " + (extraKeys != null ? extraKeys + " " : "") +
        "/Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
