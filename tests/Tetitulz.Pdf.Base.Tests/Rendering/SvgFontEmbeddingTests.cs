// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG exporter re-emits a document's own embedded font program as a CSS
/// <c>@font-face</c> for the runs that vector outlining cannot take.
/// </summary>
/// <remarks>
/// <para><b>Which runs those are is the whole reason this exists.</b> An embedded font is
/// normally drawn as vector <c>&lt;path&gt;</c> outlines — the actual glyph shapes — and needs no
/// font-face. Two populations fall past that with a font program sitting in the file: a
/// <b>vertical writing-mode</b> run (§9.7.4.3), which the outline emitter declines outright, and
/// a program SkiaSharp would not load. Both used to be drawn in a family picked by name
/// similarity, which for vertical CJK is a substitute with the wrong shapes and the wrong
/// metrics.</para>
///
/// <para>Every fixture here therefore draws through <c>/Identity-V</c>, and the acceptance
/// control at the bottom draws the same font horizontally to prove the outline path still wins
/// where it can — a change that made every embedded font take the <c>@font-face</c> route would
/// be a fidelity regression, not a feature.</para>
///
/// <para>Assertions parse the SVG as an <see cref="XDocument"/> and, for the program itself,
/// <b>decode the base64 back to bytes and compare them</b>: "a data URI appears" is satisfied by
/// a truncated one, an unrelated one, and one whose payload is the wrong font.</para>
/// </remarks>
public sealed class SvgFontEmbeddingTests
{
    /// <summary>The exact program bytes reach the SVG, under a family the run then names.</summary>
    [Fact]
    public void AVerticalRunEmbedsItsOwnFontProgramAndNamesIt()
    {
        byte[] program = SfntFixture.Build();
        string svg = RenderVertical(program);

        string rule = FontFaceRule(svg);
        Assert.Equal(program, ProgramBytesOf(rule));

        // The family the rule declares must be the one the run asks for first — a rule nothing
        // references is a payload, not a font.
        string declared = DeclaredFamily(rule);
        string used = TextElement(svg).Attribute("font-family")?.Value;

        Assert.NotNull(used);
        Assert.StartsWith("'" + declared + "'", used, StringComparison.Ordinal);
    }

    /// <summary>The system families stay behind it, so a viewer that rejects the program still has one.</summary>
    /// <remarks>
    /// CSS skips a <c>@font-face</c> whose <c>src</c> it cannot decode and walks on to the next
    /// family. Dropping the rest of the stack would turn "the browser could not read this font"
    /// into "the browser renders in its default", which is the defect this whole area was about.
    /// </remarks>
    [Fact]
    public void TheEmbeddedFamilyLeadsTheStackRatherThanReplacingIt()
    {
        string svg = RenderVertical(SfntFixture.Build());
        string family = TextElement(svg).Attribute("font-family")?.Value;

        Assert.NotNull(family);

        string[] parts = family.Split(new[] { ", " }, StringSplitOptions.None);
        Assert.True(parts.Length >= 2,
            "the embedded family replaced the fallbacks instead of leading them: " + family);
        Assert.Equal("sans-serif", parts[parts.Length - 1]);
    }

    /// <summary>One rule per program, however many runs draw it.</summary>
    /// <remarks>
    /// <b>A font program is measured in megabytes and base64 costs a further third.</b> Emitting
    /// the rule per run is the difference between an SVG and a file nothing will open.
    /// </remarks>
    [Fact]
    public void ManyRunsInOneFontEmitTheRuleOnce()
    {
        string svg = RenderVertical(SfntFixture.Build(), runs: 6);

        Assert.Equal(6, TextElements(svg).Length);
        Assert.Single(FontFaceRules(svg));
    }

    /// <summary>
    /// Two fonts embedding two different programs get a rule each. The registry keys on the program
    /// stream's object number — the reference's target, not the reference object's own number, which
    /// is 0 for every reference read out of a dictionary and would fold every program into the first.
    /// </summary>
    [Fact]
    public void TwoDifferentProgramsOnOnePageEachGetARule()
    {
        byte[] first = SfntFixture.Build(), second = SfntFixture.Build(fsType: 8);
        Assert.NotEqual(first, second);

        byte[] content = Encoding.ASCII.GetBytes(
            "BT /F1 24 Tf 72 700 Td <00010001> Tj ET\nBT /F2 24 Tf 72 500 Td <00010001> Tj ET\n");

        (byte[], byte[])[] Font(int self, byte[] program) => new (byte[], byte[])[]
        {
            (Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type0 /BaseFont /TestFace /Encoding /Identity-V"
                + " /DescendantFonts [" + (self + 1) + " 0 R] >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestFace"
                + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
                + " /DW 800 /CIDToGIDMap /Identity /FontDescriptor " + (self + 2) + " 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /FontDescriptor /FontName /TestFace /Flags 32 /ItalicAngle 0"
                + " /StemV 80 /Ascent 800 /Descent -200 /CapHeight 700 /FontBBox [0 -200 1000 900]"
                + " /FontFile2 " + (self + 3) + " 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Length " + program.Length.ToString(CultureInfo.InvariantCulture)
                + " /Length1 " + program.Length.ToString(CultureInfo.InvariantCulture) + " >>"), program),
        };

        var objects = new List<(byte[], byte[])>
        {
            (Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R /F2 9 0 R >> >> >>"), null),
            (Encoding.ASCII.GetBytes("<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>"), content),
        };
        objects.AddRange(Font(5, first));
        objects.AddRange(Font(9, second));

        using var ms = new MemoryStream(Build(objects.ToArray()));
        string[] rules = FontFaceRules(PdfSvgRenderer.RenderPageToSvg(ms, 0));

        Assert.Equal(2, rules.Length);
        Assert.NotEqual(DeclaredFamily(rules[0]), DeclaredFamily(rules[1]));
        Assert.Equal(second, ProgramBytesOf(rules[1]));
    }

    /// <summary>
    /// A <c>TJ</c> number moves a vertical run down, along its writing direction (§9.4.4:
    /// <c>ty = (w1 − Tj/1000)·Tfs</c>), not sideways. The first glyph's advance is 24 down and the
    /// 500 another 12, so the second glyph stands at (72, 664); the horizontal formula put it at x 60.
    /// </summary>
    [Fact]
    public void ATjNumberMovesAVerticalRunDown()
    {
        string svg = RenderVertical(SfntFixture.Build(),
            contentOverride: "BT /F1 24 Tf 72 700 Td [<0001> 500 <0001>] TJ ET\n");
        var texts = TextElements(svg);

        Assert.Equal(2, texts.Length);
        Assert.Equal("matrix(24,0,0,-24,72,700)", texts[0].Attribute("transform")?.Value);
        Assert.Equal("matrix(24,0,0,-24,72,664)", texts[1].Attribute("transform")?.Value);
    }

    /// <summary>A form XObject drawing the same font does not re-emit the program.</summary>
    /// <remarks>
    /// <b>This is the case the registry exists for, and the page-only test could not see it.</b>
    /// Resolving a face is memoised per writer, so six runs on one page ask once no matter what
    /// the shared registry does — deleting the registry left that test green. Every form XObject,
    /// pattern cell, soft mask, Type 3 glyph and annotation appearance is a <i>separate</i> writer
    /// with its own memo and the page's one <c>SvgSharedState</c>, which is the only thing
    /// standing between a document built out of forms and one copy of the font per form.
    /// </remarks>
    [Fact]
    public void AFormXObjectDrawingTheSameFontDoesNotReEmitTheProgram()
    {
        string svg = RenderVertical(SfntFixture.Build(), withForm: true);

        Assert.Equal(2, TextElements(svg).Length);   // the page's run and the form's
        Assert.Single(FontFaceRules(svg));
    }

    /// <summary>
    /// A form invoked forty times is forty child writers, and each built its own font cache and
    /// read the font program again before the page's <c>@font-face</c> registry answered — the
    /// raster renderer hands one cache to every nested interpreter. The page now reads the font
    /// and its program once, and every invocation still draws its run.
    /// </summary>
    [Fact]
    public void AdversarialFormInvokedManyTimes_ReadsItsFontOnce()
    {
        string invocations = string.Concat(Enumerable.Repeat("q /Fm1 Do Q\n", 40));
        FontCache.EntriesCreated = 0;
        SvgFontEmbedder.ProgramReads = 0;

        string svg = RenderVertical(SfntFixture.Build(), withForm: true, contentOverride: invocations);

        Assert.Equal(41, TextElements(svg).Length);   // forty invocations and the fixture's own
        Assert.Single(FontFaceRules(svg));
        Assert.Equal(1L, SvgFontEmbedder.ProgramReads);
        Assert.Equal(1L, FontCache.EntriesCreated);
    }

    /// <summary>A font whose <c>OS/2</c> forbids outline embedding is not re-emitted.</summary>
    /// <remarks>
    /// The source document embedded the program under whatever right its producer held; an SVG is
    /// a different container with different distribution. <c>fsType</c> bits 0–3 are one
    /// mutually-exclusive value, so 0x0002 is Restricted — the one value that forbids it outright.
    /// The run still renders: it falls back to naming a family, which is what the export did for
    /// every font before embedding existed.
    /// </remarks>
    [Fact]
    public void ARestrictedLicenceFontIsNotReEmitted()
    {
        string svg = RenderVertical(SfntFixture.Build(fsType: 0x0002));

        Assert.Empty(FontFaceRules(svg));
        Assert.False(string.IsNullOrEmpty(TextElement(svg).Attribute("font-family")?.Value));
    }

    /// <summary>A licence that permits print-and-preview embedding is re-emitted.</summary>
    /// <remarks>
    /// The acceptance control the refusal above needs. 0x0004 is Preview &amp; Print, which is a
    /// permission — reading bits 0–3 as four independent flags would make it look like a
    /// restriction, and that misreading is exactly what <c>FontEmbeddingLicense</c> exists to
    /// prevent.
    /// </remarks>
    [Fact]
    public void APreviewAndPrintLicenceFontIsReEmitted()
    {
        string svg = RenderVertical(SfntFixture.Build(fsType: 0x0004));

        Assert.Single(FontFaceRules(svg));
    }

    /// <summary>A bare CFF is refused, because no viewer can load one.</summary>
    /// <remarks>
    /// <c>/FontFile3 /Subtype /Type1C</c> holds a CFF with no sfnt around it — no <c>head</c>, no
    /// <c>cmap</c>, no <c>hmtx</c> — so a browser has nothing to load. Wrapping one is the Type 1
    /// → CFF conversion GAPS §11d tracks, not a line of code here, and emitting the bytes anyway
    /// would produce a rule that silently never applies.
    /// </remarks>
    /// <summary>
    /// A bare CFF program — which a browser cannot load — is wrapped into an OpenType container
    /// whose <c>cmap</c> addresses glyphs by index, and the run is written as those code points.
    /// </summary>
    /// <remarks>
    /// <para><b>The decoder of record is SkiaSharp's, not this library's.</b> The payload is handed
    /// to <c>SKTypeface.FromData</c> and asked which glyph each Plane 15 code point selects; a
    /// container only our own reader accepts would pass a round trip through it and fail in every
    /// browser. GID 1 is CID 1 and GID 2 is CID 2 (<c>BuildCidKeyed(new[] { 1, 2 })</c>), and the
    /// run shows CID 1 twice, so the visible text is U+F0001 U+F0001 — and a code point past the
    /// three glyphs resolves to <c>.notdef</c>, which is the cmap's one group ending where the
    /// glyphs do.</para>
    /// <para>The document's own text rides a zero-opacity overlay beside the visible run, as the
    /// outline path writes one, so the SVG stays searchable for what the PDF says rather than for
    /// Private Use characters.</para>
    /// </remarks>
    [Fact]
    public void ABareCffProgramIsWrappedIntoOpenTypeAndItsRunWrittenByGlyphIndex()
    {
        byte[] cff = Tetitulz.Pdf.TestHelpers.MinimalOpenTypeCff.BuildCidKeyed(new[] { 1, 2 });
        string svg = RenderVertical(cff, fontFileKey: "/FontFile3", subtype: "/CIDFontType0C",
                                    descendantSubtype: "CIDFontType0", toUnicode: true);

        string rule = FontFaceRule(svg);
        Assert.Contains("format('opentype')", rule, StringComparison.Ordinal);
        byte[] otf = ProgramBytesOf(rule);
        Assert.Equal(new byte[] { (byte)'O', (byte)'T', (byte)'T', (byte)'O' }, otf.Take(4).ToArray());

        // The visible run names the embedded family and shows glyph 1 twice, by index.
        XElement[] texts = TextElements(svg);
        XElement visible = Assert.Single(texts, t => t.Attribute("fill-opacity")?.Value != "0");
        Assert.StartsWith("'" + DeclaredFamily(rule) + "'", visible.Attribute("font-family")?.Value ?? "",
                          StringComparison.Ordinal);
        string glyph1 = char.ConvertFromUtf32(CffOtfWrapper.GlyphIndexedCmapBase + 1);
        Assert.Equal(glyph1 + glyph1, visible.Value);

        // The vertical placement counted characters, not UTF-16 units: two x and two y coordinates.
        Assert.Equal(2, visible.Attribute("x")?.Value.Split(' ').Length);
        Assert.Equal(2, visible.Attribute("y")?.Value.Split(' ').Length);

        // The document's text — CID 1 is 'A' through /ToUnicode — is in the overlay, not the
        // Private Use characters.
        XElement overlay = Assert.Single(texts, t => t.Attribute("fill-opacity")?.Value == "0");
        Assert.Equal("AA", overlay.Value);

        // SkiaSharp reads the container and the cmap this library wrote.
        using var data = SkiaSharp.SKData.CreateCopy(otf);
        using var typeface = SkiaSharp.SKTypeface.FromData(data);
        Assert.NotNull(typeface);
        Assert.Equal(3, typeface.GlyphCount);
        Assert.Equal(new ushort[] { 1 }, typeface.GetGlyphs(glyph1));
        Assert.Equal(new ushort[] { 2 }, typeface.GetGlyphs(char.ConvertFromUtf32(CffOtfWrapper.GlyphIndexedCmapBase + 2)));
        Assert.Equal(new ushort[] { 0 }, typeface.GetGlyphs(char.ConvertFromUtf32(CffOtfWrapper.GlyphIndexedCmapBase + 3)));
    }

    /// <summary>
    /// The horizontal run in the same bare CFF font still takes the outline path: the wrapper is for
    /// the runs outlining declines, not a new route for every CFF program.
    /// </summary>
    [Fact]
    public void AHorizontalRunInABareCffFontIsStillDrawnAsOutlines()
    {
        byte[] cff = Tetitulz.Pdf.TestHelpers.MinimalOpenTypeCff.BuildCidKeyed(new[] { 1, 2 });
        string svg = RenderVertical(cff, vertical: false, fontFileKey: "/FontFile3", subtype: "/CIDFontType0C",
                                    descendantSubtype: "CIDFontType0");

        Assert.Empty(FontFaceRules(svg));
        XElement text = TextElement(svg);
        Assert.Equal("0", text.Attribute("fill-opacity")?.Value);
        Assert.Null(text.Attribute("font-family"));
    }

    /// <summary>
    /// A CFF program that does not parse is refused with the reason, as before — the wrapper does not
    /// turn garbage into a container a browser then rejects silently.
    /// </summary>
    [Fact]
    public void AMalformedBareCffProgramIsRefusedRatherThanWrapped()
    {
        // A CFF header: major 1, minor 0, hdrSize 4, offSize 2 — and nothing a parser can read after it.
        byte[] cff = new byte[] { 0x01, 0x00, 0x04, 0x02, 0x00, 0x01, 0x01, 0x01 };
        string svg = RenderVertical(cff, fontFileKey: "/FontFile3", subtype: "/Type1C");

        Assert.Empty(FontFaceRules(svg));
        Assert.False(string.IsNullOrEmpty(TextElement(svg).Attribute("font-family")?.Value));
    }

    /// <summary>
    /// A Type 1 program (<c>/FontFile</c>) is converted to CFF and wrapped, glyph-indexed, with the
    /// converted program's glyph names available for the writer's code-to-glyph step — read through
    /// the embedder directly, since outlining takes every horizontal Type 1 run before it.
    /// </summary>
    [Fact]
    public void AType1ProgramIsConvertedWrappedAndGlyphIndexed()
    {
        (byte[] program, int length1, int length2) = Tetitulz.Pdf.TestHelpers.MinimalType1.Build(
            new[] { ("A", Tetitulz.Pdf.TestHelpers.MinimalType1.AdvanceOnly(600)),
                    ("B", Tetitulz.Pdf.TestHelpers.MinimalType1.AdvanceOnly(700)) },
            encoding: new Dictionary<int, string> { [65] = "A", [66] = "B" });

        byte[] pdf = Build(
            (Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /ProbeFont /FontDescriptor 5 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /FontDescriptor /FontName /ProbeFont /Flags 32 /ItalicAngle 0 /StemV 80"
                + " /Ascent 800 /Descent -200 /CapHeight 700 /FontBBox [0 0 1000 1000] /FontFile 6 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Length " + program.Length.ToString(CultureInfo.InvariantCulture)
                + " /Length1 " + length1.ToString(CultureInfo.InvariantCulture)
                + " /Length2 " + length2.ToString(CultureInfo.InvariantCulture) + " /Length3 0 >>"), program));

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var descriptor = (Tetitulz.Pdf.Core.Objects.PdfDictionary)doc.GetObject(5);

        SvgFontEmbedder.Program embedded = SvgFontEmbedder.TryRead(descriptor, doc, "F1");

        Assert.True(embedded.IsUsable, "the Type 1 program was refused");
        Assert.Equal("opentype", embedded.Format);
        Assert.True(embedded.GlyphIndexed);
        Assert.NotNull(embedded.GlyphNameToGid);
        Assert.True(embedded.GlyphNameToGid.TryGetValue("B", out int gidB) && gidB > 0, "glyph B has no index");

        using var data = SkiaSharp.SKData.CreateCopy(embedded.Bytes);
        using var typeface = SkiaSharp.SKTypeface.FromData(data);
        Assert.NotNull(typeface);
        Assert.Equal(new ushort[] { (ushort)gidB },
                     typeface.GetGlyphs(char.ConvertFromUtf32(CffOtfWrapper.GlyphIndexedCmapBase + gidB)));
    }

    /// <summary>An <c>OTTO</c> program is emitted, and declares the OpenType format.</summary>
    /// <remarks>
    /// The <c>format()</c> hint is what a browser dispatches on, so an OpenType/CFF container
    /// announced as <c>truetype</c> is a rule it may skip. Paired with the bare-CFF refusal above:
    /// the same outlines, and the sfnt wrapper is the whole difference.
    /// </remarks>
    [Fact]
    public void AnOpenTypeCffProgramDeclaresTheOpenTypeFormat()
    {
        string svg = RenderVertical(SfntFixture.Build(sfntVersion: 0x4F54544Fu));
        string rule = FontFaceRule(svg);

        Assert.Contains("format('opentype')", rule, StringComparison.Ordinal);
        Assert.Contains("data:font/otf;base64,", rule, StringComparison.Ordinal);
    }

    /// <summary>An oversized program is refused instead of inflated into the document.</summary>
    /// <remarks>
    /// <b>The cap is on the decoded bytes, which is where the amplification lives.</b> The stream
    /// below is a few hundred bytes of Flate and over eight megabytes decoded; base64 would make
    /// it eleven, held as UTF-16 in the builder assembling the page. A document may declare many.
    /// The run still renders — it names a family, as it did before embedding existed.
    /// </remarks>
    [Fact]
    public void AnOversizedFontProgramIsRefusedRatherThanInflatedIntoTheDocument()
    {
        byte[] huge = new byte[9 * 1024 * 1024];
        huge[0] = 0x00; huge[1] = 0x01; huge[2] = 0x00; huge[3] = 0x00;   // a valid sfnt signature

        string svg = RenderVertical(huge, compress: true);

        Assert.Empty(FontFaceRules(svg));
        Assert.False(string.IsNullOrEmpty(TextElement(svg).Attribute("font-family")?.Value));
    }

    /// <summary>A horizontal run in the same font still takes the outline path.</summary>
    /// <remarks>
    /// <para><b>The acceptance control for the whole file.</b> Outlines are the actual glyph shapes
    /// and are pixel-identical to the raster renderer; a change that routed every embedded font
    /// through <c>@font-face</c> would trade that for whatever the viewer's font engine does, and
    /// would still pass every assertion above.</para>
    ///
    /// <para><b>The two paths sign their <c>&lt;text&gt;</c> differently, and that is the
    /// evidence.</b> The outline path draws the glyphs as <c>&lt;path&gt;</c>/<c>&lt;use&gt;</c>
    /// and adds a <b>zero-opacity</b> <c>&lt;text&gt;</c> purely so the run stays selectable — it
    /// names no font, because nothing renders from it. The font-face path draws a <b>visible</b>
    /// <c>&lt;text&gt;</c> that names one, because the viewer's rendering of it is the output.
    /// Asserting the overlay is opaque-free and family-free pins the branch itself, which counting
    /// <c>&lt;use&gt;</c> elements would not: a fixture whose glyph rasterises to nothing emits no
    /// <c>&lt;use&gt;</c> while still taking the outline path.</para>
    /// </remarks>
    [Fact]
    public void AHorizontalRunIsStillDrawnAsOutlinesWithNoFontFace()
    {
        string svg = RenderVertical(SfntFixture.Build(), vertical: false);
        XElement text = TextElement(svg);

        Assert.Empty(FontFaceRules(svg));
        Assert.Equal("0", text.Attribute("fill-opacity")?.Value);
        Assert.Null(text.Attribute("font-family"));
    }

    /// <summary>And the vertical run in the same font is visible and named.</summary>
    /// <remarks>
    /// The other half of the control above: the same document, the same font, one encoding apart.
    /// Without this, "no font-family on the horizontal run" would also pass if the feature had
    /// never fired at all.
    /// </remarks>
    [Fact]
    public void TheVerticalRunInTheSameFontIsVisibleAndNamed()
    {
        XElement text = TextElement(RenderVertical(SfntFixture.Build()));

        Assert.NotEqual("0", text.Attribute("fill-opacity")?.Value);
        Assert.NotNull(text.Attribute("font-family"));
    }

    // ==================================================================== helpers

    private static XElement[] TextElements(string svg)
        => XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "text")
            .ToArray();

    private static XElement TextElement(string svg)
    {
        XElement[] texts = TextElements(svg);
        Assert.True(texts.Length > 0, "the export drew no <text> element:" + Environment.NewLine + svg);
        return texts[0];
    }

    /// <summary>Every <c>@font-face</c> rule in the document's <c>&lt;style&gt;</c> elements.</summary>
    private static string[] FontFaceRules(string svg)
        => XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "style")
            .SelectMany(e => e.Value.Split(new[] { "@font-face" }, StringSplitOptions.None).Skip(1))
            .Select(part => "@font-face" + part)
            .ToArray();

    private static string FontFaceRule(string svg)
    {
        string[] rules = FontFaceRules(svg);
        Assert.True(rules.Length == 1,
            $"expected exactly one @font-face, found {rules.Length}:" + Environment.NewLine + svg);
        return rules[0];
    }

    /// <summary>The <c>font-family</c> a rule declares, unquoted.</summary>
    private static string DeclaredFamily(string rule)
    {
        int open = rule.IndexOf("font-family:'", StringComparison.Ordinal);
        Assert.True(open >= 0, "the rule declares no font-family: " + rule);

        open += "font-family:'".Length;
        int close = rule.IndexOf('\'', open);
        Assert.True(close > open, "the rule's font-family is unterminated: " + rule);
        return rule.Substring(open, close - open);
    }

    /// <summary>The rule's payload, decoded back to the bytes a viewer would load.</summary>
    private static byte[] ProgramBytesOf(string rule)
    {
        int start = rule.IndexOf("base64,", StringComparison.Ordinal);
        Assert.True(start >= 0, "the rule carries no base64 payload: " + rule);

        start += "base64,".Length;
        int end = rule.IndexOf(')', start);
        Assert.True(end > start, "the rule's url() is unterminated: " + rule);

        return Convert.FromBase64String(rule.Substring(start, end - start));
    }

    /// <summary>
    /// One page showing a run through a Type0 font whose descendant CIDFont embeds
    /// <paramref name="program"/>.
    /// </summary>
    /// <param name="vertical">
    /// <c>/Identity-V</c> when true — the writing mode the outline emitter declines, which is what
    /// puts the run on the <c>&lt;text&gt;</c> path this feature serves.
    /// </param>
    /// <param name="compress">Flate the program stream, so an oversized fixture stays a small PDF.</param>
    /// <param name="withForm">
    /// Add a form XObject that draws the same font — a second <c>SvgContentWriter</c> sharing the
    /// page's one registry, which is the only shape in which the registry does any work.
    /// </param>
    private static string RenderVertical(
        byte[] program,
        int runs = 1,
        bool vertical = true,
        bool compress = false,
        string fontFileKey = "/FontFile2",
        string subtype = null,
        bool withForm = false,
        string contentOverride = null,
        string descendantSubtype = "CIDFontType2",
        bool toUnicode = false)
    {
        // CID 1 → 'A', CID 2 → 'B', so the run has text a reader can select; without it the
        // decoded text is U+0001, which XML cannot carry and the overlay would be empty.
        byte[] toUnicodeBytes = Encoding.ASCII.GetBytes(
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /TU def\n"
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "2 beginbfchar <0001> <0041> <0002> <0042> endbfchar\n"
            + "endcmap CMapName currentdict /CMap defineresource pop end end\n");
        int toUnicodeObj = withForm ? 10 : 9;
        var content = new StringBuilder(contentOverride ?? "");
        for (int i = 0; contentOverride == null && i < runs; i++)
            content.Append("BT /F1 24 Tf 72 ").Append(700 - i * 30)
                   .Append(" Td <00010001> Tj ET\n");
        if (withForm) content.Append("q 1 0 0 1 200 0 cm /Fm1 Do Q\n");

        byte[] contentBytes = Encoding.ASCII.GetBytes(content.ToString());

        byte[] formBytes = Encoding.ASCII.GetBytes("BT /F1 24 Tf 0 400 Td <00010001> Tj ET\n");

        byte[] streamBytes = compress ? FlateEncodeFilter.Encode(program) : program;
        string streamDict = "<< /Length " + streamBytes.Length.ToString(CultureInfo.InvariantCulture)
                            + (compress ? " /Filter /FlateDecode" : "")
                            + " /Length1 " + program.Length.ToString(CultureInfo.InvariantCulture)
                            + (subtype != null ? " /Subtype " + subtype : "") + " >>";

        byte[] pdf = Build(
            (Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"), null),
            (Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), null),
            (Encoding.ASCII.GetBytes(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
                + " /Resources << /Font << /F1 5 0 R >>"
                + (withForm ? " /XObject << /Fm1 9 0 R >>" : "") + " >> >>"), null),
            (Encoding.ASCII.GetBytes(
                "<< /Length " + contentBytes.Length.ToString(CultureInfo.InvariantCulture) + " >>"),
             contentBytes),
            (Encoding.ASCII.GetBytes(
                "<< /Type /Font /Subtype /Type0 /BaseFont /TestFace"
                + " /Encoding " + (vertical ? "/Identity-V" : "/Identity-H")
                + (toUnicode ? " /ToUnicode " + toUnicodeObj.ToString(CultureInfo.InvariantCulture) + " 0 R" : "")
                + " /DescendantFonts [6 0 R] >>"), null),
            (Encoding.ASCII.GetBytes(
                "<< /Type /Font /Subtype /" + descendantSubtype + " /BaseFont /TestFace"
                + " /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>"
                + " /DW 800" + (descendantSubtype == "CIDFontType2" ? " /CIDToGIDMap /Identity" : "")
                + " /FontDescriptor 7 0 R >>"), null),
            (Encoding.ASCII.GetBytes(
                "<< /Type /FontDescriptor /FontName /TestFace /Flags 32 /ItalicAngle 0"
                + " /StemV 80 /Ascent 800 /Descent -200 /CapHeight 700"
                + " /FontBBox [0 -200 1000 900] " + fontFileKey + " 8 0 R >>"), null),
            (Encoding.ASCII.GetBytes(streamDict), streamBytes),
            withForm
                ? (Encoding.ASCII.GetBytes(
                    "<< /Type /XObject /Subtype /Form /BBox [0 0 300 792]"
                    + " /Resources << /Font << /F1 5 0 R >> >>"
                    + " /Length " + formBytes.Length.ToString(CultureInfo.InvariantCulture)
                    + " >>"), formBytes)
                : (null, null),
            toUnicode
                ? (Encoding.ASCII.GetBytes("<< /Length " + toUnicodeBytes.Length.ToString(CultureInfo.InvariantCulture) + " >>"),
                   toUnicodeBytes)
                : (null, null));

        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <remarks>
    /// A trailing <c>(null, null)</c> entry is dropped, so the caller can pass an optional object
    /// without building two different argument lists — and, more to the point, without the object
    /// numbers shifting between the two cases.
    /// </remarks>
    private static byte[] Build(params (byte[] dict, byte[] streamData)[] objects)
    {
        objects = objects.Where(o => o.dict != null).ToArray();

        var output = new MemoryStream();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); output.Write(b, 0, b.Length); }

        Ascii("%PDF-1.7\n");
        var offsets = new List<long>();

        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(output.Position);
            Ascii((i + 1).ToString(CultureInfo.InvariantCulture) + " 0 obj\n");
            output.Write(objects[i].dict, 0, objects[i].dict.Length);

            if (objects[i].streamData != null)
            {
                Ascii("\nstream\n");
                output.Write(objects[i].streamData, 0, objects[i].streamData.Length);
                Ascii("\nendstream");
            }
            Ascii("\nendobj\n");
        }

        long xref = output.Position;
        Ascii("xref\n0 " + (objects.Length + 1).ToString(CultureInfo.InvariantCulture)
              + "\n0000000000 65535 f \n");
        foreach (long offset in offsets)
            Ascii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

        Ascii("trailer\n<< /Size " + (objects.Length + 1).ToString(CultureInfo.InvariantCulture)
              + " /Root 1 0 R >>\nstartxref\n"
              + xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");

        return output.ToArray();
    }

    /// <summary>
    /// A minimal but real sfnt: two square glyphs, the tables a font engine needs, and an
    /// <c>OS/2</c> whose <c>fsType</c> the caller chooses.
    /// </summary>
    /// <remarks>
    /// It is built rather than loaded because the assertions are about <b>which bytes reached the
    /// output</b>, and a system font would make that claim unrepeatable across hosts — as well as
    /// putting a licence question into a test fixture.
    /// </remarks>
    private static class SfntFixture
    {
        internal static byte[] Build(int fsType = 0, uint sfntVersion = 0x00010000u)
        {
            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                { "head", Head() },
                { "hhea", Hhea() },
                { "maxp", Maxp() },
                { "hmtx", Hmtx() },
                { "loca", Loca() },
                { "glyf", Glyf() },
                { "cmap", Cmap() },
                { "OS/2", Os2(fsType) },
            };
            return Assemble(tables, sfntVersion);
        }

        private const int NumGlyphs = 2;

        private static byte[] Head()
        {
            var t = new byte[54];
            t[0] = 0; t[1] = 1;                       // version 1.0
            t[12] = 0x5F; t[13] = 0x0F; t[14] = 0x3C; t[15] = 0xF5;   // magic
            t[18] = 0x03; t[19] = 0xE8;               // unitsPerEm 1000
            t[50] = 0; t[51] = 0;                     // indexToLocFormat: short
            return t;
        }

        private static byte[] Hhea()
        {
            var t = new byte[36];
            t[0] = 0; t[1] = 1;
            t[4] = 0x03; t[5] = 0x20;                 // ascender 800
            t[34] = 0; t[35] = NumGlyphs;             // numberOfHMetrics
            return t;
        }

        private static byte[] Maxp()
        {
            var t = new byte[32];
            t[0] = 0; t[1] = 0x01; t[2] = 0; t[3] = 0;
            t[4] = 0; t[5] = NumGlyphs;
            return t;
        }

        private static byte[] Hmtx()
        {
            var t = new byte[NumGlyphs * 4];
            for (int i = 0; i < NumGlyphs; i++) { t[i * 4] = 0x03; t[i * 4 + 1] = 0x20; }
            return t;
        }

        private static byte[] Glyf()
        {
            // Glyph 0 (.notdef) is empty; glyph 1 is one closed square contour.
            var g = new MemoryStream();
            I16(g, 1);                                   // numberOfContours
            I16(g, 0); I16(g, 0); I16(g, 500); I16(g, 700);
            U16(g, 3);                                   // endPtsOfContours[0]
            U16(g, 0);                                   // instructionLength
            for (int i = 0; i < 4; i++) g.WriteByte(0x01);   // flags: on-curve, 16-bit deltas
            I16(g, 0); I16(g, 500); I16(g, 0); I16(g, -500);  // x deltas
            I16(g, 0); I16(g, 0); I16(g, 700); I16(g, -700);  // y deltas

            byte[] outline = g.ToArray();
            var all = new byte[outline.Length];
            outline.CopyTo(all, 0);
            return all;
        }

        private static byte[] Loca()
        {
            // Short format: offsets are halved. Glyph 0 is empty, glyph 1 runs to the end.
            int glyfLength = Glyf().Length;
            var t = new MemoryStream();
            U16(t, 0);
            U16(t, 0);
            U16(t, (glyfLength + 1) / 2);
            return t.ToArray();
        }

        private static byte[] Cmap()
        {
            // One (3,1) format-4 subtable with the single terminating segment the format requires.
            // A CIDFontType2 with an /Identity CIDToGIDMap selects by glyph index and never
            // consults this, but a font engine rejects a program that has no cmap at all — and a
            // subtable whose declared length disagrees with its contents is the same rejection
            // wearing a table's clothes.
            var sub = new MemoryStream();
            U16(sub, 4);        // format
            U16(sub, 24);       // length — header (14) + one segment's five arrays (10)
            U16(sub, 0);        // language
            U16(sub, 2);        // segCountX2 (one segment)
            U16(sub, 2);        // searchRange
            U16(sub, 0);        // entrySelector
            U16(sub, 0);        // rangeShift
            U16(sub, 0xFFFF);   // endCode[0]
            U16(sub, 0);        // reservedPad
            U16(sub, 0xFFFF);   // startCode[0]
            U16(sub, 1);        // idDelta[0]
            U16(sub, 0);        // idRangeOffset[0]

            byte[] subtable = sub.ToArray();
            var t = new MemoryStream();
            U16(t, 0); U16(t, 1);                          // version, numTables
            U16(t, 3); U16(t, 1); U32(t, 4 + 8);           // (3,1) at the end of the records
            t.Write(subtable, 0, subtable.Length);
            return t.ToArray();
        }

        private static byte[] Os2(int fsType)
        {
            var t = new byte[86];
            t[0] = 0; t[1] = 1;                        // version 1
            t[4] = 0x01; t[5] = 0x90;                  // usWeightClass 400
            t[6] = 0; t[7] = 5;                        // usWidthClass 5
            t[8] = (byte)(fsType >> 8); t[9] = (byte)fsType;
            t[62] = 0; t[63] = 0x40;                   // fsSelection REGULAR
            return t;
        }

        private static byte[] Assemble(Dictionary<string, byte[]> tables, uint sfntVersion)
        {
            List<string> tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
            var output = new MemoryStream();

            U32(output, sfntVersion);
            U16(output, tags.Count);
            U16(output, 16); U16(output, 0); U16(output, tags.Count * 16 - 16);

            int dataOffset = 12 + tags.Count * 16;
            var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string tag in tags)
            {
                offsets[tag] = dataOffset;
                dataOffset += (tables[tag].Length + 3) & ~3;
            }

            foreach (string tag in tags)
            {
                byte[] name = Encoding.ASCII.GetBytes(tag);
                output.Write(name, 0, 4);
                U32(output, 0);
                U32(output, (uint)offsets[tag]);
                U32(output, (uint)tables[tag].Length);
            }

            foreach (string tag in tags)
            {
                byte[] data = tables[tag];
                output.Write(data, 0, data.Length);
                for (int pad = data.Length; (pad & 3) != 0; pad++) output.WriteByte(0);
            }

            return output.ToArray();
        }

        private static void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        private static void I16(Stream s, int v) => U16(s, v & 0xFFFF);

        private static void U32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));  s.WriteByte((byte)v);
        }
    }
}
