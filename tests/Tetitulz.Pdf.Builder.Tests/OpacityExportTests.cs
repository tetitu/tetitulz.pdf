// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <c>Paragraph.Opacity</c> / <c>TextRun.Opacity</c> — emitted as PDF constant alpha
/// (<c>/ExtGState /ca</c> plus a <c>gs</c> operator).
///
/// <para><b>Asserted on the operators and the resource dictionary, not on ink.</b> A page drawn at
/// half opacity still rasterises to a perfectly good PNG, and this repository's own bytes are
/// lossless either way — so "it produced a PDF" cannot see any of this. What can see it is which
/// <c>gs</c> operators reached the content stream and what the <c>/ExtGState</c> they name
/// contains.</para>
///
/// <para><b>Every case here has a discriminating negative.</b> The value of the feature is as much
/// in what it does <i>not</i> emit — a document that never asks for opacity must be byte-identical
/// to one built before the feature existed, or every existing caller's output moved.</para>
/// </summary>
public class OpacityExportTests
{
    // `/GS3 gs` — the operator that installs a named graphics state.
    private static readonly Regex GsOp = new Regex(@"/(GS\d+)\s+gs", RegexOptions.Compiled);

    private static List<string> GsNames(string content)
    {
        var names = new List<string>();
        foreach (Match m in GsOp.Matches(content)) names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>The <c>/ca</c> (or <c>/CA</c>) value of a named ExtGState, read from the file.</summary>
    /// <remarks>
    /// The ExtGState objects are written uncompressed, so this reads the raw file rather than the
    /// inflated content — the resource is the half of the claim the content stream cannot show.
    /// </remarks>
    private static string AlphaOf(byte[] pdf, string gsName, string key)
    {
        string raw = PdfContentProbe.Raw(pdf);

        // Find `/GSn <ref> ` inside the page's /ExtGState dictionary, then the object it names.
        var refMatch = Regex.Match(raw, @"/" + Regex.Escape(gsName) + @"\s+(\d+)\s+0\s+R");
        Assert.True(refMatch.Success, $"no /{gsName} entry in any /ExtGState resource dictionary");

        string objNum = refMatch.Groups[1].Value;
        var objMatch = Regex.Match(raw,
            @"(?<![\d])" + objNum + @"\s+0\s+obj\s*<<(?<body>[^>]*)>>");
        Assert.True(objMatch.Success, $"object {objNum} 0 (the /{gsName} ExtGState) not found");

        var alpha = Regex.Match(objMatch.Groups["body"].Value, @"/" + key + @"\s+([\d.]+)");
        Assert.True(alpha.Success,
            $"/{gsName} -> object {objNum} has no /{key}: <<{objMatch.Groups["body"].Value}>>");
        return alpha.Groups[1].Value;
    }

    private static Document Doc(Action<SectionBuilder> section) =>
        new DocumentBuilder().AddSection(section).Build();

    // ================================================================ the operator is emitted

    /// <summary>
    /// A shaded paragraph below full opacity is a transparency group (batch 90): the page sets one
    /// state — fill and stroke alpha both 0.5 — and paints the group's form once; inside the form
    /// the shading fill and the glyphs carry no state at all, so they composite as one layer and
    /// the text does not show the background through it. The form's resources carry the font the
    /// text is set in. Before batch 90 each paint carried its own <c>/ca</c>.
    /// </summary>
    [Fact]
    public void ShadedParagraphOpacity_IsATransparencyGroup_PaintedOnceAtTheAlpha()
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Faded").Shading("#FF0000").Opacity(0.5)));

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);
        var names = GsNames(content);

        string gs = Assert.Single(names);
        Assert.Equal("0.5", AlphaOf(pdf, gs, "ca"));
        Assert.Equal("0.5", AlphaOf(pdf, gs, "CA"));
        Assert.True(content.IndexOf(" gs", StringComparison.Ordinal) < content.IndexOf(" Do", StringComparison.Ordinal), "the state is not set before the group is painted");
        // Inside the group: the fill in its own q…Q with no gs, and the glyphs with none between BT and Tj.
        Assert.Matches(@"q\s+1 0 0 rg\s+[\d. -]+re\s+f\s+Q", content);
        Assert.Matches(@"BT(?:(?!gs)[\s\S])*?\(Faded\) Tj", content);
        Assert.DoesNotMatch(@"BT[\s\S]*?/GS\d+ gs[\s\S]*?\(Faded\) Tj", content);

        using var read = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(pdf));
        var xobjects = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(read.ResolveObject(read.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.XObject]));
        var form = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(read.ResolveObject(xobjects[new Tetitulz.Pdf.Core.Objects.PdfName("Fm1")]));
        var group = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(read.ResolveObject(form[Tetitulz.Pdf.Core.Objects.PdfName.Group]));
        Assert.Equal("Transparency", ((Tetitulz.Pdf.Core.Objects.PdfName)group[Tetitulz.Pdf.Core.Objects.PdfName.S]).Value);
        var resources = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(read.ResolveObject(form[Tetitulz.Pdf.Core.Objects.PdfName.Resources]));
        Assert.NotNull(read.ResolveObject(resources[Tetitulz.Pdf.Core.Objects.PdfName.Font]));
    }

    /// <summary>
    /// A shaded paragraph with nothing over its box is no group: a fill alone composites the same
    /// either way, so it keeps the constant alpha and mints no form.
    /// </summary>
    [Fact]
    public void ShadedParagraphWithNoContent_KeepsConstantAlpha()
    {
        var doc = Doc(s => s.AddParagraph(p => p.Shading("#FF0000").Opacity(0.5)));

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);

        Assert.DoesNotContain(" Do", content, StringComparison.Ordinal);
        Assert.Matches(@"q\s+1 0 0 rg\s+/GS\d+ gs", content);
    }

    /// <summary>
    /// A tagged document marks the group's <c>Do</c> as the paragraph — one sequence, since a form
    /// invoked inside a marked sequence belongs to it (§14.7.4.4) — and nothing inside the form is
    /// marked: an MCID in a form would need the form's own parent-tree entry.
    /// </summary>
    [Fact]
    public void TaggedShadedParagraphOpacity_MarksTheGroupsDoAsTheParagraph()
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Faded").Shading("#FF0000").Opacity(0.5)));
        doc.Tagged = true;

        byte[] pdf = doc.ToPdf();
        using var read = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(pdf));
        string page = System.Text.Encoding.Latin1.GetString(read.Pages[0].GetContentStreamData());
        var xobjects = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfDictionary>(read.ResolveObject(read.Pages[0].Resources[Tetitulz.Pdf.Core.Objects.PdfName.XObject]));
        var form = Assert.IsType<Tetitulz.Pdf.Core.Objects.PdfStream>(read.ResolveObject(xobjects[new Tetitulz.Pdf.Core.Objects.PdfName("Fm1")]));
        string inner = System.Text.Encoding.Latin1.GetString(form.GetDecodedData());

        Assert.Matches(@"/P <</MCID \d+>> BDC\s+q\s+/GS\d+ gs\s+/Fm1 Do\s+Q\s+EMC", page);
        Assert.DoesNotContain("BDC", inner, StringComparison.Ordinal);
        Assert.Contains("(Faded) Tj", inner, StringComparison.Ordinal);
    }

    /// <summary>
    /// A translucent box taller than the page's remaining height breaks across pages and cannot be
    /// one form: it keeps the constant alpha — a <c>gs</c> inside its shading's <c>q…Q</c>, no
    /// form — and reports the approximation once, however many such paragraphs there are.
    /// </summary>
    [Fact]
    public void ATranslucentBoxTallerThanAPage_KeepsConstantAlphaAndReportsOnce()
    {
        string tall = string.Concat(System.Linq.Enumerable.Repeat("word ", 4000));
        var doc = Doc(s => s
            .AddParagraph(p => p.AddText(tall).Shading("#FF0000").Opacity(0.5))
            .AddParagraph(p => p.AddText(tall).Shading("#00FF00").Opacity(0.5)));

        byte[] pdf = null!;
        var seen = CaptureOnThisThread(() => pdf = doc.ToPdf());
        string content = PdfContentProbe.DecodeStreams(pdf);

        Assert.True(PdfContentProbe.CountPages(pdf) >= 2, "the paragraphs did not fill a page");
        Assert.DoesNotContain(" Do", content, StringComparison.Ordinal);
        Assert.Matches(@"q\s+1 0 0 rg\s+/GS\d+ gs", content);
        var approximations = seen.FindAll(d => d.Kind == BuilderDiagnosticKind.ContentApproximated);
        Assert.Single(approximations);
        Assert.Contains("breaks across pages", approximations[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FadedParagraph_ResetsAlphaToOne_SoTheNextParagraphIsOpaque()
    {
        var doc = Doc(s => s
            .AddParagraph(p => p.AddText("Faded").Opacity(0.5))
            .AddParagraph(p => p.AddText("Solid")));

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);
        var names = GsNames(content);

        // Exactly two: set 0.5 for the faded text, put it back to 1 before ET. /ca is graphics
        // state and survives ET, so without the reset "Solid" would draw at half opacity too.
        Assert.Equal(2, names.Count);
        Assert.Equal("0.5", AlphaOf(pdf, names[0], "ca"));
        Assert.Equal("1", AlphaOf(pdf, names[1], "ca"));

        // The second paragraph must draw with no graphics-state change of its own.
        int solidAt = content.IndexOf("(Solid) Tj", StringComparison.Ordinal);
        Assert.True(solidAt > 0, "the second paragraph was not drawn");
        int lastGsAt = content.LastIndexOf(" gs", StringComparison.Ordinal);
        Assert.True(lastGsAt < solidAt,
            "a `gs` was emitted after the faded paragraph reset it:\n" + content);
    }

    [Fact]
    public void TextRunOpacity_MultipliesWithTheParagraphs()
    {
        // 0.5 x 0.4 = 0.2. Neither factor alone produces that, so this fails if either the
        // paragraph's or the run's contribution is dropped — and it fails differently if they
        // are added, maxed or last-wins'd instead of multiplied.
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Dim").Opacity(0.5).TextOpacity(0.4)));

        byte[] pdf = doc.ToPdf();
        var names = GsNames(PdfContentProbe.DecodeStreams(pdf));

        Assert.NotEmpty(names);
        Assert.Equal("0.2", AlphaOf(pdf, names[0], "ca"));
    }

    /// <summary>
    /// A bordered paragraph below full opacity is a group too (batch 90): the border is stroked
    /// opaque inside the form — no <c>gs</c> in its <c>q…Q</c> — and the page's one state carries
    /// the alpha as <c>/CA</c> for the stroke as much as <c>/ca</c> for the fill; a stroke faded
    /// through <c>/ca</c> alone would be opaque.
    /// </summary>
    [Fact]
    public void BorderedParagraphOpacity_IsAGroup_TheBorderStrokedOpaqueInsideIt()
    {
        var doc = Doc(s => s.AddParagraph(p => p
            .AddText("Boxed").Borders(new Border(1, "#000000")).Opacity(0.5)));

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);

        string gs = Assert.Single(GsNames(content));
        Assert.Equal("0.5", AlphaOf(pdf, gs, "CA"));
        Assert.Equal("0.5", AlphaOf(pdf, gs, "ca"));
        Assert.DoesNotMatch(@"q[^Q]*?/GS\d+ gs[^Q]*?\bS\b", content);
        Assert.Matches(@"\bS\b", content);   // the potency control: the border is stroked
    }

    /// <summary>
    /// A translucent paragraph taller than a page keeps constant alpha (it cannot be one group), and
    /// a border of one edge is stroked edge by edge: that edge's stroke carries the alpha as
    /// <c>/CA</c> in its own <c>q…Q</c>, or the border would be drawn opaque over faded text.
    /// </summary>
    [Fact]
    public void ATallTranslucentParagraphsEdge_IsStrokedAtItsAlpha()
    {
        string tall = string.Concat(System.Linq.Enumerable.Repeat("word ", 4000));
        var doc = Doc(s => s.AddParagraph(p => p.AddText(tall).Opacity(0.5)));
        ((Paragraph)doc.Sections[0].Children[0]).BorderTop = new Border(1, "#000000");

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);

        Assert.True(PdfContentProbe.CountPages(pdf) >= 2, "the paragraph did not fill a page");
        var stroke = Regex.Match(content, @"q[^Q]*?/(GS\d+) gs[^Q]*?\bS\b");
        Assert.True(stroke.Success, "no edge stroked under a graphics state of its own:\n" + content);
        Assert.Equal("0.5", AlphaOf(pdf, stroke.Groups[1].Value, "CA"));
    }

    // ================================================================ the discriminating negatives

    [Fact]
    public void ExplicitFullOpacity_EmitsNoGraphicsStateOperator_AndIsByteIdenticalToNotSayingIt()
    {
        var stated = Doc(s => s.AddParagraph(p => p
            .AddText("Plain").Shading("#EEEEEE").Opacity(1.0).TextOpacity(1.0)));
        var silent = Doc(s => s.AddParagraph(p => p.AddText("Plain").Shading("#EEEEEE")));

        byte[] a = stated.ToPdf(), b = silent.ToPdf();

        // Not merely "no gs": no /ExtGState resource is minted either, so the object numbering —
        // and therefore every byte of the file — is untouched by the feature existing.
        Assert.DoesNotContain(" gs", PdfContentProbe.DecodeStreams(a), StringComparison.Ordinal);
        Assert.DoesNotContain("/ExtGState", PdfContentProbe.Raw(a), StringComparison.Ordinal);
        Assert.Equal(PdfContentProbe.NormalizedRaw(b), PdfContentProbe.NormalizedRaw(a));
    }

    [Fact]
    public void TwoRunsAtTheSameOpacity_EmitOneGsBetweenThem_TwoDifferentOnesEmitTwo()
    {
        // The emit-only-on-change rule, which is what keeps a faded paragraph from carrying one
        // operator per word.
        var same = Doc(s => s.AddParagraph(p => p
            .AddText("aa").TextOpacity(0.5).AddText("bb").TextOpacity(0.5)));
        var differ = Doc(s => s.AddParagraph(p => p
            .AddText("aa").TextOpacity(0.5).AddText("bb").TextOpacity(0.25)));

        // One to set 0.5, one to reset to 1.
        Assert.Equal(2, GsNames(PdfContentProbe.DecodeStreams(same.ToPdf())).Count);
        // 0.5, then 0.25, then the reset.
        Assert.Equal(3, GsNames(PdfContentProbe.DecodeStreams(differ.ToPdf())).Count);
    }

    // ================================================================ the approximation report

    /// <summary>Collects diagnostics raised on THIS thread only.</summary>
    /// <remarks>
    /// <c>BuilderDiagnostics.OnEvent</c> is process-wide, so an unfiltered handler makes
    /// <c>Assert.Single</c> a claim about every test running in parallel beside this one — the
    /// exact flake CLAUDE.md's "Diagnosing a red whole-solution run" rule 2 describes.
    /// </remarks>
    private static List<BuilderDiagnostic> CaptureOnThisThread(Action body)
    {
        var seen = new List<BuilderDiagnostic>();
        int mine = Environment.CurrentManagedThreadId;
        void Capture(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != mine) return;
            lock (seen) seen.Add(d);
        }

        BuilderDiagnostics.OnEvent += Capture;
        try { body(); }
        finally { BuilderDiagnostics.OnEvent -= Capture; }
        return seen;
    }

    /// <summary>
    /// A shaded paragraph below full opacity paints a background BEHIND ITS OWN TEXT, which is where
    /// PDF's per-object <c>/ca</c> and CSS's group opacity disagree — and since batch 90 it is a
    /// transparency group, so nothing is approximated and nothing reported. Before batch 90 the
    /// constant alpha stood in and was reported once per export.
    /// </summary>
    [Fact]
    public void ShadedParagraphBelowFullOpacity_IsAGroupAndReportsNothing()
    {
        var doc = Doc(s => s
            .AddParagraph(p => p.AddText("One").Shading("#FF0000").Opacity(0.5))
            .AddParagraph(p => p.AddText("Two").Shading("#00FF00").Opacity(0.5)));

        byte[] pdf = null!;
        var seen = CaptureOnThisThread(() => pdf = doc.ToPdf());

        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated);
        Assert.Equal(2, Regex.Matches(PdfContentProbe.DecodeStreams(pdf), @"/Fm\d+ Do").Count);
    }

    [Fact]
    public void FadedParagraphWithNothingBehindItsText_ReportsNoApproximation()
    {
        // Presence is not deviation. With no background the two models agree exactly, and a
        // channel that fires on conformant input is one a caller learns to ignore — so this is
        // the assertion that gives the one above its meaning.
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Faded").Opacity(0.5)));

        var seen = CaptureOnThisThread(() => doc.ToPdf());

        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated);
    }

    [Fact]
    public void ShadedParagraphAtFullOpacity_ReportsNoApproximation()
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Solid").Shading("#FF0000")));

        var seen = CaptureOnThisThread(() => doc.ToPdf());

        Assert.DoesNotContain(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated);
    }

    // ================================================================ four-format parity

    [Fact]
    public void HtmlExportCarriesOpacity_WhileDocxAndOdtReportItDropped()
    {
        // "The same model exported to four formats" is exactly when a caller needs to be told
        // which one lost something — the rule BuilderDiagnosticKind.UnsupportedByExportFormat
        // exists for.
        Document Faded() => Doc(s => s.AddParagraph(p => p.AddText("Faded").Opacity(0.5)));

        string html = System.Text.Encoding.UTF8.GetString(Faded().ToHtml());
        Assert.Contains("opacity:0.5", html, StringComparison.Ordinal);

        foreach (var (name, export) in new (string, Func<Document, byte[]>)[]
                 {
                     ("DOCX", d => d.ToDocx()),
                     ("ODT",  d => d.ToOdt()),
                 })
        {
            var doc = Faded();
            var seen = CaptureOnThisThread(() => export(doc));
            var dropped = seen.FindAll(d => d.Kind == BuilderDiagnosticKind.UnsupportedByExportFormat);

            Assert.True(dropped.Count > 0, $"{name} dropped the opacity without saying so");
            Assert.Contains(dropped, d => d.Detail.Contains("opacity", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FullyOpaqueDocument_DrawsNoUnsupportedByExportFormatReportFromDocxOrOdt()
    {
        // The other half of the presence-is-not-deviation rule, on the export side.
        Document Plain() => Doc(s => s.AddParagraph(p => p.AddText("Plain")));

        foreach (Func<Document, byte[]> export in new Func<Document, byte[]>[]
                 { d => d.ToDocx(), d => d.ToOdt() })
        {
            var doc = Plain();
            var seen = CaptureOnThisThread(() => export(doc));
            Assert.DoesNotContain(seen, d =>
                d.Kind == BuilderDiagnosticKind.UnsupportedByExportFormat &&
                d.Detail.Contains("opacity", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ================================================================ coercion

    [Theory]
    [InlineData(-1.0, "0")]
    [InlineData(2.0, "1")]
    public void OutOfRangeOpacity_IsClampedIntoTheCaRange(double asked, string expected)
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("x").Opacity(asked)));

        byte[] pdf = doc.ToPdf();
        var names = GsNames(PdfContentProbe.DecodeStreams(pdf));

        if (expected == "1")
        {
            // Clamped UP to fully opaque, which the emit-on-change rule then suppresses entirely.
            Assert.Empty(names);
            Assert.DoesNotContain("/ExtGState", PdfContentProbe.Raw(pdf), StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEmpty(names);
            Assert.Equal(expected, AlphaOf(pdf, names[0], "ca"));
        }
    }

    /// <summary>
    /// A NaN opacity is refused at export, naming the paragraph (Wave 17 R15.2's one walk over the
    /// model's measurements). It used to be coerced to 1 at the point of use — NaN fails every
    /// comparison, so a naive clamp returned NaN and <c>(float)NaN</c> reached <c>/ca</c>, erasing the
    /// paragraph; the coercion kept it visible. The walk now refuses every non-finite measurement
    /// before layout, so the coercion is unreachable from a caller; the clamp of a <i>finite</i>
    /// out-of-range opacity (the theory above) stays, because 7 and −1 are finite and the model
    /// documents them as clamped where they are used.
    /// </summary>
    [Fact]
    public void NotANumberOpacity_IsRefusedAtExport_NamingTheParagraph()
    {
        var doc = Doc(s => s.AddParagraph(p => p.AddText("Visible").Opacity(double.NaN)));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => doc.ToPdf());

        Assert.Equal("document", ex.ParamName);
        Assert.Contains("Sections[0].Children[0].Opacity", ex.Message, StringComparison.Ordinal);
    }
}
