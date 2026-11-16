// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The decisions in <c>Documents</c>' small files that the folder's first sweep found nothing
/// asserting — the sanitizer's defaults, the font lookup, the repairing opener, the header/footer
/// writer, the visual diff and the linearization hints.
/// </summary>
public class DocumentsBatch1DecisionTests
{
    /// <summary>A document of pages each <paramref name="w"/> × <paramref name="h"/>, each drawing its content.</summary>
    private static byte[] Pages(float w, float h, params string[] contents)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, contents.Length).Select(i => $"{3 + 2 * i} 0 R"))}] /Count {contents.Length} >>",
        };
        foreach (string content in contents)
        {
            bodies.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Contents {bodies.Count + 2} 0 R /Resources << >> >>"));
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        return Objects(bodies, "");
    }

    private static byte[] Objects(IReadOnlyList<string> bodies, string trailerExtras)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R {trailerExtras} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---------------------------------------------------------------- the sanitizer's defaults

    /// <summary>
    /// What a default sanitize removes: every active and embedding construct — scripts, attachments,
    /// form actions, launch actions, the open action, XFA and rich media — and keeps a plain link.
    /// </summary>
    [Fact]
    public void SanitizeOptions_DefaultsRemoveEveryActiveConstruct_ButKeepLinks()
    {
        var o = SanitizeOptions.Default;
        Assert.Equal((true, true, true, true, true, true, true, false),
            (o.RemoveJavaScript, o.RemoveEmbeddedFiles, o.RemoveFormActions, o.RemoveLaunchActions,
             o.RemoveOpenAction, o.RemoveXfa, o.RemoveRichMediaAnnotations, o.RemoveURIActions));
        Assert.False(o.AllowSignatureInvalidation);
    }

    // ---------------------------------------------------------------- the font lookup

    /// <summary>Resources with no fonts, or without the named one, resolve to no font rather than throwing.</summary>
    [Fact]
    public void ResourceFontLookup_AMissingFontIsNone()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pages(100, 100, "q Q")));
        Assert.Null(ResourceFontLookup.Resolve("F1", new PdfDictionary(), doc));

        var resources = new PdfDictionary();
        var fonts = new PdfDictionary();
        fonts.Set(new PdfName("F2"), new PdfDictionary());
        resources.Set(PdfName.Font, fonts);
        Assert.Null(ResourceFontLookup.Resolve("F1", resources, doc));
    }

    // ---------------------------------------------------------------- the repairing opener

    /// <summary>
    /// Bytes neither the parser nor the repair can make a document of are refused with the parser's
    /// own exception — rethrown with the stack it was thrown with, so the trace reaches the parser.
    /// </summary>
    [Fact]
    public void OpenWithRepair_AnUnrecoverableFile_ThrowsTheParsersException_WithItsTrace()
    {
        var ex = Assert.ThrowsAny<Exception>(() => PdfDocumentOpener.OpenWithRepair(new MemoryStream(Encoding.ASCII.GetBytes("this is not a PDF at all"))));
        Assert.Contains("Tetitulz.Pdf.Core", ex.StackTrace);
    }

    // ---------------------------------------------------------------- the header/footer writer

    private static List<ContentStreamOperation> AppendedOps(HeaderFooterOptions options)
    {
        using var output = new MemoryStream();
        PdfHeaderFooterWriter.AddHeaderFooter(new MemoryStream(Pages(612, 792, "q Q")), output, options);
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    private static List<(double X, double Y, string Text)> Placed(List<ContentStreamOperation> ops)
    {
        var placed = new List<(double, double, string)>();
        for (int i = 0; i + 1 < ops.Count; i++)
            if (ops[i].Operator == "Td" && ops[i + 1].Operator == "Tj")
                placed.Add((((PdfNumber)ops[i].Operands[0]).DoubleValue, ((PdfNumber)ops[i].Operands[1]).DoubleValue,
                            Encoding.Latin1.GetString(((PdfString)ops[i + 1].Operands[0]).RawBytes)));
        return placed;
    }

    /// <summary>
    /// Right-aligned lines end at the margin by the font's own widths — "WWW" in 10-point Helvetica
    /// is 28.32 points (944 units a W) — the header a font size below the top margin and the footer
    /// on the bottom one; <c>{Page}</c> becomes the page number; the colour is the one asked for.
    /// </summary>
    [Fact]
    public void HeaderFooter_RightAlignedByTheFontsWidths_OnTheirMargins()
    {
        var ops = AppendedOps(new HeaderFooterOptions
        {
            HeaderText = "WWW", FooterText = "{Page}", HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 10, MarginPt = 36, ColorR = 1,
        });
        var placed = Placed(ops);

        Assert.Equal(2, placed.Count);
        Assert.Equal("WWW", placed[0].Text);
        Assert.Equal(612 - 36 - 28.32, placed[0].X, 3);
        Assert.Equal(792 - 36 - 10, placed[0].Y, 3);
        Assert.Equal("1", placed[1].Text);
        Assert.Equal(612 - 36 - 5.56, placed[1].X, 3);   // a Helvetica "1" is 556 units
        Assert.Equal(36, placed[1].Y, 3);

        var rg = ops.First(o => o.Operator == "rg").Operands.Select(o => ((PdfNumber)o).DoubleValue);
        Assert.Equal(new[] { 1.0, 0, 0 }, rg);
    }

    /// <summary>A centred line sits half its width either side of the page's middle.</summary>
    [Fact]
    public void HeaderFooter_CentredByTheFontsWidths()
    {
        var placed = Placed(AppendedOps(new HeaderFooterOptions { HeaderText = "WWW", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 10 }));
        Assert.Equal((612 - 28.32) / 2, Assert.Single(placed).X, 3);
    }

    /// <summary>A font with no width table — Symbol — is measured at half an em a character, the old estimate.</summary>
    [Fact]
    public void HeaderFooter_AFontWithNoWidthTable_FallsBackToHalfAnEm()
        => Assert.Equal(612 - 36 - 10, PdfHeaderFooterWriter_RightX("Symbol", "ab"), 3);

    private static double PdfHeaderFooterWriter_RightX(string font, string text)
        => Assert.Single(Placed(AppendedOps(new HeaderFooterOptions
        {
            HeaderText = text, FontName = font, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 10, MarginPt = 36,
        }))).X;

    // ---------------------------------------------------------------- the visual diff

    private static PdfVisualDiff.Result Diff(byte[] left, byte[] right, float threshold = 2f, int tolerance = 8)
        => PdfVisualDiff.Compare(new MemoryStream(left), new MemoryStream(right), dpi: 72, thresholdPct: threshold, pixelTolerance: tolerance);

    /// <summary>
    /// A channel difference of exactly the tolerance is no difference, one more is — on each channel
    /// alone: a page of black against one with only red, then only green, raised.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void VisualDiff_TheToleranceIsExclusive_OnEachChannel(int channel)
    {
        string Fill(int level)
        {
            var rgb = new double[3];
            rgb[channel] = level / 255.0;
            return FormattableString.Invariant($"{rgb[0]:0.000000} {rgb[1]:0.000000} {rgb[2]:0.000000} rg 0 0 50 50 re f");
        }
        byte[] dark = Pages(50, 50, Fill(0)), raised = Pages(50, 50, Fill(20));

        // The rendered difference, measured rather than assumed — the colour pipeline decides it —
        // and only on this channel.
        var a = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(dark), 0, 72);
        var b = Tetitulz.Pdf.Base.Rendering.PdfPageRenderer.RenderPageToPixels(new MemoryStream(raised), 0, 72);
        int at = 25 * a.Stride + 25 * 4;
        int d = Math.Abs(a.Pixels[at + channel] - b.Pixels[at + channel]);
        Assert.True(d > 1, "the two fills render alike");
        for (int c = 0; c < 3; c++)
            if (c != channel) Assert.Equal(a.Pixels[at + c], b.Pixels[at + c]);

        Assert.Equal(0, Diff(dark, raised, tolerance: d).Pages[0].DifferingPixels);
        Assert.Equal(2500, Diff(dark, raised, tolerance: d - 1).Pages[0].DifferingPixels);
    }

    /// <summary>Pages of different sizes differ by at least the area one has and the other does not.</summary>
    [Fact]
    public void VisualDiff_ASizeDifference_IsCountedAsDifferingArea()
    {
        var page = Diff(Pages(50, 50, "q Q"), Pages(50, 100, "q Q")).Pages[0];
        Assert.Equal(2500, page.DifferingPixels);
        Assert.Equal(5000, page.TotalPixels);
    }

    /// <summary>A difference of exactly the threshold is within it: 2% of a page is equal at a 2% threshold, not at 1.9%.</summary>
    [Fact]
    public void VisualDiff_TheThresholdIsInclusive()
    {
        byte[] plain = Pages(100, 100, "q Q");
        byte[] marked = Pages(100, 100, "0 0 0 rg 10 10 10 20 re f");   // 200 of 10 000 pixels
        Assert.True(Diff(plain, marked, threshold: 2f).AreEqual);
        Assert.False(Diff(plain, marked, threshold: 1.9f).AreEqual);
    }

    /// <summary>Either side encrypted is refused — the left as surely as the right.</summary>
    [Fact]
    public void VisualDiff_AnEncryptedLeftSide_IsRefused()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
        };
        byte[] encrypted = Objects(bodies, "/Encrypt << /Filter /Standard /V 1 /R 2 /O <00> /U <00> /P -4 >> /ID [<00><00>]");
        var ex = Assert.Throws<PdfException>(() => Diff(encrypted, Pages(10, 10, "q Q")));
        Assert.Contains("left", ex.Message);
    }

    // ---------------------------------------------------------------- the linearization hints

    private static PdfLinearizationHints Hints(byte[] pdf)
    {
        using var linearized = new MemoryStream();
        Tetitulz.Pdf.Plugins.Maintenance.PdfLinearizer.Linearize(new MemoryStream(pdf), linearized);
        linearized.Position = 0;
        Assert.True(PdfLinearizationHintReader.TryRead(linearized, out PdfLinearizationHints hints), PdfLinearizationHintReader.LastError);
        return hints;
    }

    /// <summary>A page's content stream lies inside the page's own range, where the hint says it starts.</summary>
    [Fact]
    public void LinearizationHints_APagesContentLiesInItsOwnRange()
    {
        var hints = Hints(MinimalPdfBuilder.CreateMultiPagePdf(3));
        foreach (PdfPageHint page in hints.Pages.Skip(1))
        {
            Assert.True(page.ContentOffset > 0, "a later page's content offset is not recorded");
            Assert.InRange(page.ContentOffset, page.Offset, page.Offset + page.Length);
        }
    }

    /// <summary>A document with an outline gets the outline's hint — its first object and how many there are.</summary>
    [Fact]
    public void LinearizationHints_AnOutlineHasItsHint()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << >> >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (One) /Parent 5 0 R /Dest [3 0 R /Fit] >>",
        };
        PdfGenericHint outline = Hints(Objects(bodies, "")).Outline;
        Assert.NotNull(outline);
        Assert.Equal(2, outline.ObjectCount);
        Assert.True(outline.Length > 0);
    }
}
