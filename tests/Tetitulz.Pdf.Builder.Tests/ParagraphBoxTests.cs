// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A paragraph's own background shading and border box — the pair drawn by
/// <c>PdfExporter.RenderParagraphAt</c> around the rendered text.
///
/// <para><b>Two defects, both in the same eight lines.</b> The background fill sat inside an
/// <c>if (hasBox &amp;&amp; paginate)</c> branch while the border below it was drawn under a bare
/// <c>if (hasBox)</c>, so every caller that renders with <c>paginate:false</c> — headers, footers
/// and block content inside a table cell — drew the border and <b>no background at all</b>. And
/// neither the fill nor the four border strokes were wrapped as an artifact, so on a tagged page
/// they were unmarked ink, which is a PDF/UA violation in its own right; <c>TableRenderer</c> has
/// always wrapped its own cell shading and cell borders exactly that way.</para>
/// </summary>
public sealed class ParagraphBoxTests
{
    // A filled rectangle. Text never emits `re`, so in these fixtures it is the paragraph box —
    // provided the fixture carries no underline or strike-through, which draw their rules the
    // same way (RendererTests.Underline_EmitsRule).
    private static readonly Regex FilledRectangle = new(@"re\s+f", RegexOptions.Compiled);

    /// <summary>
    /// The defect: a header renders with <c>paginate:false</c>, so its shading was skipped.
    /// </summary>
    [Fact]
    public void ShadedParagraph_InAHeader_StillPaintsItsBackground()
    {
        string content = InflateContent(HeaderDocument(shading: "#3366CC").ToPdf());

        Assert.Matches(FilledRectangle, content);
    }

    /// <summary>
    /// The control. Without it the assertion above would be satisfied by any stray rectangle the
    /// exporter happens to draw for a header, and the test would pass with shading deleted.
    /// </summary>
    [Fact]
    public void UnshadedParagraph_InAHeader_PaintsNoBackground()
    {
        string content = InflateContent(HeaderDocument(shading: "").ToPdf());

        Assert.DoesNotMatch(FilledRectangle, content);
    }

    /// <summary>
    /// The body path was already correct and must stay so — this is what pins the fix as "the
    /// fill moved out of the pagination branch", not "the fill moved".
    /// </summary>
    [Fact]
    public void ShadedParagraph_InTheBody_StillPaintsItsBackground()
    {
        var doc = new Document();
        var sec = new Section();
        sec.Children.Add(ShadedParagraph("#3366CC"));
        doc.Sections.Add(sec);

        Assert.Matches(FilledRectangle, InflateContent(doc.ToPdf()));
    }

    /// <summary>
    /// On a tagged page the background fill is decoration, so it belongs inside an
    /// <c>/Artifact</c> marked-content sequence rather than loose in the stream.
    /// </summary>
    [Fact]
    public void ShadedParagraph_OnATaggedPage_DrawsItsBackgroundAsAnArtifact()
    {
        var doc = new Document { Tagged = true };
        var sec = new Section();
        sec.Children.Add(ShadedParagraph("#3366CC"));
        doc.Sections.Add(sec);

        Assert.All(RectangleFills(InflateContent(doc.ToPdf())),
                   at => Assert.True(at.insideArtifact,
                       "A paragraph background was painted outside any /Artifact sequence on a " +
                       "tagged page, so it is untagged content rather than decoration."));
    }

    // ================================================================
    //  Background image
    // ================================================================

    /// <summary>
    /// The feature: a paragraph background image is painted behind the text. Counting the tiles
    /// rather than looking for the word "Image" in the file is what makes this about the paint —
    /// an XObject can be registered in the page resources and never invoked.
    /// </summary>
    [Fact]
    public void ParagraphWithABackgroundImage_PaintsItBehindTheText()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 40, HeightPt = 20, Repeat = BackgroundRepeat.NoRepeat,
        });

        Assert.Single(ImageDraws(InflateContent(doc.ToPdf())));
    }

    /// <summary>
    /// The background image is placed in the box the insets widen, as the shading and borders
    /// are: a non-repeating image sits at the box's top-left corner, which moves out with them.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_SitsInTheInsetBox()
    {
        BackgroundImage Background() => new()
        {
            Data = MakeRgbPng(8, 8), WidthPt = 40, HeightPt = 20, Repeat = BackgroundRepeat.NoRepeat,
        };
        var plain = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(Background()).ToPdf())));

        var doc = BackgroundImageDocument(Background());
        var p = (Paragraph)doc.Sections[0].Children[0];
        p.BoxInsetLeft = 12;
        p.BoxInsetTop = 7;
        var inset = Assert.Single(ImageDraws(InflateContent(doc.ToPdf())));

        // 12pt and 7pt against the default 2pt: 10pt further left, 5pt further up.
        Assert.Equal(plain.x - 10, inset.x, 2);
        Assert.Equal(plain.y + 5, inset.y, 2);
    }

    /// <summary>
    /// <see cref="BackgroundImage.PositionX"/> / <c>PositionY</c> place a non-repeating tile at that
    /// fraction of the box's spare room: 0.5 exactly halfway between 0 and 1, on each axis.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_ThePositionPlacesTheTile()
    {
        (double x, double y) At(double px, double py)
        {
            var draw = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
            {
                Data = MakeRgbPng(8, 8), WidthPt = 40, HeightPt = 4, Repeat = BackgroundRepeat.NoRepeat,
                PositionX = px, PositionY = py,
            }).ToPdf())));
            return (draw.x, draw.y);
        }

        var topLeft = At(0, 0);
        var centre = At(0.5, 0.5);
        var bottomRight = At(1, 1);

        // Potency: the box really has room to spare on both axes.
        Assert.True(bottomRight.x - topLeft.x > 100, "the box is not wider than the tile");
        Assert.True(topLeft.y - bottomRight.y > 2, "the box is not taller than the tile");
        Assert.Equal((topLeft.x + bottomRight.x) / 2, centre.x, 2);
        Assert.Equal((topLeft.y + bottomRight.y) / 2, centre.y, 2);
    }

    /// <summary>
    /// A repeating image tiles outward from its position: a tile sits exactly where the position
    /// puts it, and the row still covers the box from its left edge.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_ARepeatTilesOutwardFromThePosition()
    {
        List<(double w, double h, double x, double y)> Draws(double px) =>
            ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
            {
                Data = MakeRgbPng(8, 8), WidthPt = 30, HeightPt = 6, Repeat = BackgroundRepeat.RepeatX,
                PositionX = px,
            }).ToPdf()));

        double boxLeft = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 30, HeightPt = 6, Repeat = BackgroundRepeat.NoRepeat,
        }).ToPdf()))).x;
        double centred = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 30, HeightPt = 6, Repeat = BackgroundRepeat.NoRepeat,
            PositionX = 0.5,
        }).ToPdf()))).x;

        double boxRight = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 30, HeightPt = 6, Repeat = BackgroundRepeat.NoRepeat,
            PositionX = 1,
        }).ToPdf()))).x + 30;

        var tiles = Draws(0.5);

        Assert.Contains(tiles, t => Math.Abs(t.x - centred) < 0.01);
        Assert.True(tiles.Min(t => t.x) <= boxLeft + 0.01, "the row does not reach the box's left edge");
        Assert.True(tiles.Min(t => t.x) > boxLeft - 30, "a tile starts wholly outside the box");
        Assert.True(tiles.Max(t => t.x + t.w) >= boxRight - 0.01, "the row does not reach the box's right edge");
    }

    /// <summary>The same outward tiling downward: a centred column reaches the box's top and bottom.</summary>
    /// <remarks>
    /// The tile is sized from the box — 3.05 tiles to its height — because only then does a centred
    /// column need a row more than the box's height alone suggests: one partial tile above the
    /// anchor's run and one below. A tile that divides the box more evenly let a row count taken
    /// from the height alone pass (the sweep's X10).
    /// </remarks>
    [Fact]
    public void ParagraphBackgroundImage_AVerticalRepeatTilesOutwardFromThePosition()
    {
        double TileBottom(double py, double height)
        {
            var draw = Assert.Single(ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
            {
                Data = MakeRgbPng(8, 8), WidthPt = 20, HeightPt = height, Repeat = BackgroundRepeat.NoRepeat,
                PositionY = py,
            }).ToPdf())));
            return draw.y;
        }

        double boxTop = TileBottom(0, 4) + 4, boxBottom = TileBottom(1, 4);
        double tile = (boxTop - boxBottom) / 3.05;
        double centred = TileBottom(0.5, tile);
        var tiles = ImageDraws(InflateContent(BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 20, HeightPt = tile, Repeat = BackgroundRepeat.RepeatY, PositionY = 0.5,
        }).ToPdf()));

        Assert.True(boxTop - boxBottom > 8, "the box is not two tiles tall");
        Assert.Contains(tiles, t => Math.Abs(t.y - centred) < 0.01);
        Assert.True(tiles.Max(t => t.y + t.h) >= boxTop - 0.01, "the column does not reach the box's top");
        Assert.True(tiles.Min(t => t.y) <= boxBottom + 0.01, "the column does not reach the box's bottom");
    }

    /// <summary>
    /// The control. Without it the assertion above is satisfied by any image the exporter happens
    /// to draw, and would pass with the background-image paint deleted.
    /// </summary>
    [Fact]
    public void ParagraphWithNoBackgroundImage_PaintsNoImage()
    {
        var doc = BackgroundImageDocument(null);

        Assert.Empty(ImageDraws(InflateContent(doc.ToPdf())));
    }

    /// <summary>
    /// <c>background-repeat: repeat</c> tiles a small image across the box. The box here is far
    /// wider than one 20&#160;pt tile, so a single draw would mean the repeat arm never ran.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_Repeat_TilesTheBoxInBothDirections()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 20, HeightPt = 6, Repeat = BackgroundRepeat.Repeat,
        });

        var draws = ImageDraws(InflateContent(doc.ToPdf()));

        Assert.True(draws.Count > 1, $"repeat drew {draws.Count} tile(s), so it did not tile");
        Assert.Contains(draws, d => d.x > draws[0].x);   // a second column
        Assert.Contains(draws, d => d.y < draws[0].y);   // a second row, lower down the page
    }

    /// <summary>
    /// <c>repeat-x</c> must tile horizontally only. Asserting that every tile shares one baseline
    /// is the half that fails if the two axes were swapped, which reading a tile count cannot see.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_RepeatX_TilesHorizontallyOnly()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 20, HeightPt = 6, Repeat = BackgroundRepeat.RepeatX,
        });

        var draws = ImageDraws(InflateContent(doc.ToPdf()));

        Assert.True(draws.Count > 1, $"repeat-x drew {draws.Count} tile(s), so it did not tile");
        Assert.All(draws, d => Assert.Equal(draws[0].y, d.y, 3));
    }

    /// <summary>
    /// <c>background-size: cover</c> scales one tile to fill the box, so it draws exactly one and
    /// that one is at least as wide as the box — which is the observable that distinguishes cover
    /// from "no-repeat at the intrinsic size", the two being indistinguishable by tile count.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_Cover_DrawsOneTileNoSmallerThanTheBox()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 10, HeightPt = 10, Size = BackgroundSize.Cover,
        });

        var draws = ImageDraws(InflateContent(doc.ToPdf()));

        var only = Assert.Single(draws);
        Assert.True(only.w > 10 && only.h > 10,
            $"cover drew the tile at its intrinsic {only.w}×{only.h}, so it was not scaled to the box");
    }

    /// <summary>
    /// On a tagged page the background image is decoration, exactly like the shading fill above
    /// it — unmarked ink on a tagged page is a PDF/UA violation in its own right.
    /// </summary>
    [Fact]
    public void ParagraphBackgroundImage_OnATaggedPage_IsDrawnAsAnArtifact()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), WidthPt = 40, HeightPt = 20, Repeat = BackgroundRepeat.NoRepeat,
        });
        doc.Tagged = true;

        string content = InflateContent(doc.ToPdf());
        var invocations = OperatorPositions(content, XObjectInvocation);

        Assert.NotEmpty(invocations);
        Assert.All(invocations, at => Assert.True(at.insideArtifact,
            "A paragraph background image was painted outside any /Artifact sequence on a tagged page."));
    }

    /// <summary>
    /// The DoS surface: the tile count is the destination box divided by the tile size, and a 1×1
    /// pixel spacer is 0.75&#160;pt, so a full-width paragraph asks for tens of thousands of
    /// <c>Do</c> operators. The assertion is the guard's own evidence — the bounded draw count and
    /// the reported reason — never elapsed time.
    /// </summary>
    [Fact]
    public void OversizedBackgroundImageTiling_IsCappedAndReported()
    {
        BuilderDiagnostic reported = null;
        int mine = Environment.CurrentManagedThreadId;
        void Capture(BuilderDiagnostic d)
        {
            // Filtered to the calling thread. BuilderDiagnostics.OnEvent is process-wide, so an
            // unfiltered handler collects whatever a concurrent test reports — and five other
            // tests in this assembly raise ContentApproximated, so `reported` could end up being
            // a table or opacity diagnostic and the "were painted" assertion fail. The remedy is
            // the one the four already-filtered captures in this assembly use.
            if (Environment.CurrentManagedThreadId != mine) return;
            if (d.Kind == BuilderDiagnosticKind.ContentApproximated) reported = d;
        }

        BuilderDiagnostics.OnEvent += Capture;
        try
        {
            // A 1×1 image with no declared size: the tile is 0.75 pt each way, so an uncapped
            // tiling of the paragraph box would be in the tens of thousands of draws.
            var doc = BackgroundImageDocument(new BackgroundImage
            {
                Data = MakeRgbPng(1, 1), Repeat = BackgroundRepeat.Repeat,
            });

            var draws = ImageDraws(InflateContent(doc.ToPdf()));

            Assert.Equal(PdfExporter.MaxBackgroundTiles, draws.Count);
            Assert.NotNull(reported);
            Assert.Contains("were painted", reported.Detail, StringComparison.Ordinal);
        }
        finally { BuilderDiagnostics.OnEvent -= Capture; }
    }

    // ================================================================
    //  The other three exporters
    // ================================================================

    /// <summary>
    /// The HTML exporter re-emits the background as the CSS that produced it, bytes inlined —
    /// so a PDF→HTML→PDF trip keeps it, which is the acceptance bar for this model field.
    /// </summary>
    [Fact]
    public void HtmlExport_ReEmitsTheBackgroundImageAsADataUri()
    {
        var doc = BackgroundImageDocument(new BackgroundImage
        {
            Data = MakeRgbPng(8, 8), Repeat = BackgroundRepeat.NoRepeat, Size = BackgroundSize.Cover,
        });

        string html = Encoding.UTF8.GetString(doc.ToHtml());

        Assert.Contains("background-image:url(data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Contains("background-repeat:no-repeat", html, StringComparison.Ordinal);
        Assert.Contains("background-size:cover", html, StringComparison.Ordinal);
    }

    /// <summary>
    /// DOCX has no paragraph background image, so the content is dropped — and a caller exporting
    /// one model to four formats needs to be told which one lost something. Silence here is the
    /// failure mode, not the absent <c>&lt;w:shd&gt;</c>.
    /// </summary>
    [Fact]
    public void DocxExport_DropsTheBackgroundImage_AndSaysSo()
    {
        var doc = BackgroundImageDocument(new BackgroundImage { Data = MakeRgbPng(8, 8) });

        doc.ToDocx();

        Assert.Equal(BuilderDiagnosticKind.UnsupportedByExportFormat, BuilderDiagnostics.LastError?.Kind);
        Assert.Contains("background image", BuilderDiagnostics.LastError.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The ODT exporter writes no image parts, so the same rule applies to it.</summary>
    [Fact]
    public void OdtExport_DropsTheBackgroundImage_AndSaysSo()
    {
        var doc = BackgroundImageDocument(new BackgroundImage { Data = MakeRgbPng(8, 8) });

        doc.ToOdt();

        Assert.Equal(BuilderDiagnosticKind.UnsupportedByExportFormat, BuilderDiagnostics.LastError?.Kind);
    }

    /// <summary>
    /// A slot a caller reads <i>after</i> an operation must be reset at the <i>start</i> of that
    /// operation. Only <c>PdfExporter.Export</c> cleared it, so the moment DOCX started reporting,
    /// a clean HTML export would hand back the DOCX export's complaint — the stale-slot failure
    /// CLAUDE.md records against <c>LastPermsValidation</c>, which was per-thread and still wrong.
    /// </summary>
    [Fact]
    public void HtmlExport_ClearsTheDiagnosticLeftByAPreviousExport()
    {
        BackgroundImageDocument(new BackgroundImage { Data = MakeRgbPng(8, 8) }).ToDocx();
        Assert.NotNull(BuilderDiagnostics.LastError);   // the fixture must really leave one behind

        BackgroundImageDocument(null).ToHtml();

        Assert.Null(BuilderDiagnostics.LastError);
    }

    /// <summary>
    /// The border strokes are decoration too, and they are drawn on their own path
    /// (<c>m</c>/<c>l</c>/<c>S</c>) rather than as a rectangle, so they need their own assertion.
    /// </summary>
    [Fact]
    public void BorderedParagraph_OnATaggedPage_DrawsItsBorderAsAnArtifact()
    {
        var doc = new Document { Tagged = true };
        var sec = new Section();
        var para = new Paragraph();
        para.Runs.Add(new TextRun { Text = "Bordered" });
        para.BorderTop = new Border { Color = "#000000", Width = 1 };
        sec.Children.Add(para);
        doc.Sections.Add(sec);

        string content = InflateContent(doc.ToPdf());
        Assert.All(StrokeOperators(content),
                   at => Assert.True(at.insideArtifact,
                       "A paragraph border was stroked outside any /Artifact sequence on a " +
                       "tagged page."));
        Assert.NotEmpty(StrokeOperators(content));   // the fixture must actually stroke something
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    private static Paragraph ShadedParagraph(string shading)
    {
        var p = new Paragraph { Shading = shading };
        p.Runs.Add(new TextRun { Text = "Shaded paragraph" });
        return p;
    }

    /// <summary>
    /// A one-paragraph document whose only paragraph carries <paramref name="background"/>
    /// (<c>null</c> = none, which is the control every background assertion needs).
    /// </summary>
    private static Document BackgroundImageDocument(BackgroundImage background)
    {
        var doc = new Document();
        var sec = new Section();
        var p = new Paragraph { BackgroundImage = background };
        p.Runs.Add(new TextRun { Text = "Text over a background" });
        sec.Children.Add(p);
        doc.Sections.Add(sec);
        return doc;
    }

    /// <summary>A w×h solid-red 8-bit RGB PNG — the smallest real image the decoder accepts.</summary>
    internal static byte[] MakeRgbPng(int w, int h)
    {
        byte[] raw = new byte[h * (1 + w * 3)];
        int p = 0;
        for (int y = 0; y < h; y++)
        {
            raw[p++] = 0;   // filter: None
            for (int x = 0; x < w; x++) { raw[p++] = 200; raw[p++] = 40; raw[p++] = 40; }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var outMs = new MemoryStream();
        outMs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // colour type: truecolour
        WriteChunk(outMs, "IHDR", ihdr);
        WriteChunk(outMs, "IDAT", idat);
        WriteChunk(outMs, "IEND", Array.Empty<byte>());
        return outMs.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len, 0, 4);
        byte[] t = Encoding.ASCII.GetBytes(type);
        var crcIn = new byte[4 + data.Length];
        Array.Copy(t, 0, crcIn, 0, 4); Array.Copy(data, 0, crcIn, 4, data.Length);
        s.Write(t, 0, 4); s.Write(data, 0, data.Length);
        var crc = new byte[4]; WriteBE(crc, 0, (int)Crc32(crcIn)); s.Write(crc, 0, 4);
    }

    private static void WriteBE(byte[] b, int o, int v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (0xEDB88320 ^ (c >> 1)) : (c >> 1);
        }
        return c ^ 0xFFFFFFFF;
    }

    /// <summary>
    /// A document whose only shaded paragraph is in the running header, which
    /// <c>PdfExporter.DrawHeader</c> renders with <c>paginate:false</c>.
    /// </summary>
    private static Document HeaderDocument(string shading)
    {
        var doc = new Document();
        var sec = new Section { Header = new Header() };
        sec.Header.Paragraphs.Add(ShadedParagraph(shading));

        // Body text, so the page is not empty for a reason unrelated to the header.
        var body = new Paragraph();
        body.Runs.Add(new TextRun { Text = "Body" });
        sec.Children.Add(body);

        doc.Sections.Add(sec);
        return doc;
    }

    // ================================================================
    //  Content-stream inspection
    // ================================================================

    /// <summary>
    /// Every <c>re … f</c> in the stream, each tagged with whether it falls inside an open
    /// <c>/Artifact … BDC … EMC</c> range. Scanning for the enclosing state rather than
    /// substring-matching is what makes the assertion about <i>this</i> fill rather than about the
    /// stream containing an artifact somewhere.
    /// </summary>
    private static (int at, bool insideArtifact)[] RectangleFills(string content)
        => OperatorPositions(content, FilledRectangle);

    private static (int at, bool insideArtifact)[] StrokeOperators(string content)
        => OperatorPositions(content, new Regex(@"\bS\b", RegexOptions.Compiled));

    /// <summary>An image XObject invocation, <c>/Name Do</c>.</summary>
    private static readonly Regex XObjectInvocation = new(@"/\w+\s+Do\b", RegexOptions.Compiled);

    /// <summary>
    /// Every image placement in the stream, as the box it was drawn into.
    /// <c>PdfContentStreamBuilder.DrawImage</c> emits <c>w 0 0 h x y cm</c> immediately before the
    /// <c>Do</c>, so reading that matrix is reading the destination rectangle — which is what lets
    /// a test tell <c>cover</c> from <c>no-repeat</c>, and one row from two, rather than only
    /// counting draws.
    /// </summary>
    internal static List<(double w, double h, double x, double y)> ImageDraws(string content)
    {
        var result = new List<(double, double, double, double)>();
        var placement = new Regex(
            @"([-\d.]+)\s+0\s+0\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+cm\s*/\w+\s+Do\b",
            RegexOptions.Compiled);
        foreach (Match m in placement.Matches(content))
            result.Add((D(m, 1), D(m, 2), D(m, 3), D(m, 4)));
        return result;

        static double D(Match m, int group) =>
            double.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (int at, bool insideArtifact)[] OperatorPositions(string content, Regex op)
    {
        var result = new System.Collections.Generic.List<(int, bool)>();
        foreach (Match m in op.Matches(content))
            result.Add((m.Index, ArtifactDepthAt(content, m.Index) > 0));
        return result.ToArray();
    }

    /// <summary>
    /// How many <c>/Artifact … BDC</c> sequences are open at <paramref name="offset"/>. Every
    /// <c>BDC</c>/<c>BMC</c> increments the marked-content depth and every <c>EMC</c> decrements
    /// it, so an artifact opened around a fill is only "still open" if no <c>EMC</c> has closed it
    /// — which is why this counts rather than searching backwards for the nearest token.
    /// </summary>
    private static int ArtifactDepthAt(string content, int offset)
    {
        string before = content.Substring(0, offset);
        int depth = 0, artifactDepth = 0;
        foreach (Match m in Regex.Matches(before, @"/Artifact[^\n]*?BDC|\bBDC\b|\bBMC\b|\bEMC\b"))
        {
            if (m.Value.EndsWith("EMC", StringComparison.Ordinal))
            {
                if (depth == artifactDepth && artifactDepth > 0) artifactDepth--;
                if (depth > 0) depth--;
            }
            else
            {
                depth++;
                if (m.Value.StartsWith("/Artifact", StringComparison.Ordinal)) artifactDepth = depth;
            }
        }
        return artifactDepth > 0 && depth >= artifactDepth ? artifactDepth : 0;
    }

    // Inflates every FlateDecode content stream and concatenates the operators. Ported from
    // RendererTests, which cannot be referenced across test classes without making one of them a
    // helper library; Latin1 is a 1:1 byte mapping, so substring→bytes round-trips.
    private static string InflateContent(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        int idx = 0;
        while (true)
        {
            int s = raw.IndexOf("stream", idx, StringComparison.Ordinal);
            if (s < 0) break;
            if (s >= 3 && raw.Substring(s - 3, 3) == "end") { idx = s + 6; continue; }
            int dataStart = s + 6;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;
            int e = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (e < 0) break;
            int len = e - dataStart;
            while (len > 0 && (raw[dataStart + len - 1] == '\n' || raw[dataStart + len - 1] == '\r')) len--;
            byte[] data = Encoding.Latin1.GetBytes(raw.Substring(dataStart, len));
            try
            {
                using var inMs = new MemoryStream(data);
                using var z = new ZLibStream(inMs, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                sb.Append(Encoding.Latin1.GetString(outMs.ToArray()));
                sb.Append('\n');
            }
            catch { /* not a flate stream (e.g. a font program) */ }
            idx = e + 9;
        }
        return sb.ToString();
    }
}
