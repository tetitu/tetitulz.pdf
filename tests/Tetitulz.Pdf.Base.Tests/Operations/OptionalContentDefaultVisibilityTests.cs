// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// "Is this optional-content group hidden in the default configuration?" (§8.11.4.3) had two
/// answers, and they disagreed on <c>/BaseState</c>.
/// </summary>
/// <remarks>
/// <para>The renderers asked <c>PdfPageRenderer.BuildHiddenOcgSet</c>, which reads
/// <c>/BaseState</c>, <c>/ON</c> and <c>/OFF</c>. <c>PdfOperations.ListLayers</c> asked
/// <c>PdfOcgManager.CollectOcgs</c>, which read <c>/OFF</c> and <c>/ON</c> and <b>never
/// <c>/BaseState</c></b>. For a document whose <c>/D</c> says <c>/BaseState /OFF</c> the listing
/// therefore reported <b>every</b> layer <c>DefaultVisible = true</c> over a page on which both
/// renderers correctly painted nothing — so a host building a layer UI drew every checkbox ticked
/// above a blank page, and a caller auditing hidden content was told there was none.</para>
///
/// <para><b>Why the listing is compared against painted pixels rather than against the hidden
/// set.</b> Both sides now call one rule, so asserting that the two computations agree would be a
/// tautology. Pixels are not: the render path additionally has to look the group up through the
/// page's <c>/Properties</c>, honour <c>BDC</c>/<c>EMC</c> nesting and suppress the drawing
/// operators inside. And each test states the <i>absolute</i> visibility it expects, so it fails
/// against an implementation that hides everything as loudly as against one that hides nothing.
/// The no-<c>/BaseState</c> control is what makes "Silent is hidden" a claim about
/// <c>/BaseState</c>: the same fixture, the same bands, the same <c>/ON</c> and <c>/OFF</c>
/// arrays, and that layer comes back.</para>
/// </remarks>
public sealed class OptionalContentDefaultVisibilityTests
{
    // The four groups, their object numbers, and the user-space band each one's content paints.
    // Under /BaseState /OFF: Silent and Suppressed are hidden, Revived and Contested are not.
    private const string Silent = "Silent";          // obj 5, in /OCGs only        — base state decides
    private const string Revived = "Revived";        // obj 6, in /ON               — /ON re-enables
    private const string Contested = "Contested";    // obj 7, in /ON *and* /OFF    — /ON wins
    private const string Suppressed = "Suppressed";  // obj 8, in /OFF              — hidden either way

    private static readonly (string Layer, float Y0, float Y1)[] Bands =
    {
        (Silent,     600f, 700f),
        (Revived,    450f, 550f),
        (Contested,  300f, 400f),
        (Suppressed, 150f, 250f),
    };

    // ------------------------------------------------------------------
    //  /BaseState /OFF — the case the two answers disagreed on
    // ------------------------------------------------------------------

    [Fact]
    public void WithBaseStateOff_ListLayersReportsHidden_EveryGroupNotNamedInOn()
    {
        IReadOnlyList<OcgInfo> layers = ListLayers(baseStateOff: true);

        Assert.False(Visibility(layers, Silent),
            "/BaseState /OFF hides every group in /OCGs; this one is in no override list.");
        Assert.False(Visibility(layers, Suppressed), "the group named in /OFF was reported visible.");
        Assert.True(Visibility(layers, Revived), "/ON must re-enable the group it names.");
        Assert.True(Visibility(layers, Contested), "/ON must win for a group named in both lists.");
    }

    [Fact]
    public void WithBaseStateOff_OnlyTheGroupsNamedInOnArePainted()
    {
        using var bmp = Render(baseStateOff: true);

        Assert.Equal(0, InkInBand(bmp, Silent));
        Assert.Equal(0, InkInBand(bmp, Suppressed));
        Assert.True(InkInBand(bmp, Revived) > 1000, "the /ON group's content was suppressed.");
        Assert.True(InkInBand(bmp, Contested) > 1000,
            "a group in both /ON and /OFF was not painted — /OFF won.");
    }

    // ------------------------------------------------------------------
    //  The control: identical /ON and /OFF, no /BaseState
    // ------------------------------------------------------------------

    /// <summary>
    /// Drop <c>/BaseState</c> and the layer that was hidden comes back — in the listing and in the
    /// pixels. Without this the two assertions above are met by an implementation that hides every
    /// group it is not told to show.
    /// </summary>
    [Fact]
    public void WithoutBaseState_TheGroupInNoOverrideListIsVisible()
    {
        IReadOnlyList<OcgInfo> layers = ListLayers(baseStateOff: false);
        using var bmp = Render(baseStateOff: false);

        Assert.True(Visibility(layers, Silent),
            "the default base state is ON, so a group in neither /ON nor /OFF is visible.");
        Assert.True(InkInBand(bmp, Silent) > 1000, "that group's content was not painted.");

        // …and /OFF still hides its group, so "visible" is not a constant either.
        Assert.False(Visibility(layers, Suppressed));
        Assert.Equal(0, InkInBand(bmp, Suppressed));
    }

    // ------------------------------------------------------------------
    //  The rule both copies already agreed on, pinned before and after the merge
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AGroupNamedInBothOnAndOff_IsVisible(bool baseStateOff)
    {
        IReadOnlyList<OcgInfo> layers = ListLayers(baseStateOff);
        using var bmp = Render(baseStateOff);

        Assert.True(Visibility(layers, Contested), "/ON must win over /OFF for a group in both.");
        Assert.True(InkInBand(bmp, Contested) > 1000, "that group's content was not painted.");
    }

    // ------------------------------------------------------------------
    //  The two answers, over the same bytes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ListLayersReportsWhatTheRendererPaints_ForEveryGroup(bool baseStateOff)
    {
        IReadOnlyList<OcgInfo> layers = ListLayers(baseStateOff);
        using var bmp = Render(baseStateOff);

        foreach (var (layer, _, _) in Bands)
        {
            bool listed = Visibility(layers, layer);
            bool painted = InkInBand(bmp, layer) > 1000;
            Assert.True(listed == painted,
                $"/{layer}: ListLayers said DefaultVisible={listed}, the renderer painted={painted}.");
        }
    }

    /// <summary>All four groups are listed, so no assertion above can pass vacuously.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryGroupInTheOcgsArrayIsListed(bool baseStateOff)
        => Assert.Equal(
            Bands.Select(b => b.Layer).OrderBy(n => n),
            ListLayers(baseStateOff).Select(l => l.Name).OrderBy(n => n));

    // ==================================================================
    //  Fixture
    // ==================================================================

    private static bool Visibility(IReadOnlyList<OcgInfo> layers, string name)
        => Assert.Single(layers, l => l.Name == name).DefaultVisible;

    private static IReadOnlyList<OcgInfo> ListLayers(bool baseStateOff)
    {
        using var ms = new MemoryStream(BuildPdf(baseStateOff));
        return PdfOperations.ListLayers(ms);
    }

    private const int Dpi = 72;

    private static SKBitmap Render(bool baseStateOff)
    {
        using var ms = new MemoryStream(BuildPdf(baseStateOff));
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// Inked pixels inside the named layer's band, inset by 5 px so an off-by-one in the
    /// user-space → device-row mapping cannot leak a neighbouring band's ink in.
    /// </summary>
    private static int InkInBand(SKBitmap bmp, string layer)
    {
        var band = Bands.Single(b => b.Layer == layer);
        int top = bmp.Height - 1 - (int)(band.Y1 * Dpi / 72f) + 5;
        int bottom = bmp.Height - 1 - (int)(band.Y0 * Dpi / 72f) - 5;

        int n = 0;
        for (int y = top; y <= bottom; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240)) n++;
            }
        return n;
    }

    /// <summary>
    /// One page painting four black bands, each wrapped in its own <c>/OC … BDC … EMC</c>, over a
    /// four-group <c>/OCProperties</c>. The default configuration is <b>indirect</b> (object 10) —
    /// the shape a common viewer writes, and the one that once disabled layer hiding outright.
    /// </summary>
    private static byte[] BuildPdf(bool baseStateOff)
    {
        string baseState = baseStateOff ? "/BaseState /OFF " : "";

        // /OC1…/OC4 name objects 5…8 through the page's /Properties, in Bands order.
        var content = new StringBuilder();
        for (int i = 0; i < Bands.Length; i++)
            content.Append($"/OC /OC{i + 1} BDC 0 g 50 {Bands[i].Y0} 200 " +
                           $"{Bands[i].Y1 - Bands[i].Y0} re f EMC\n");

        return Assemble(new[]
        {
            /* 1 */ "<< /Type /Catalog /Pages 2 0 R /OCProperties 9 0 R >>",
            /* 2 */ "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            /* 3 */ "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                    "/Resources << /Properties << /OC1 5 0 R /OC2 6 0 R /OC3 7 0 R /OC4 8 0 R >> >> >>",
            /* 4 */ Stream(content.ToString()),
            /* 5 */ $"<< /Type /OCG /Name ({Silent}) >>",
            /* 6 */ $"<< /Type /OCG /Name ({Revived}) >>",
            /* 7 */ $"<< /Type /OCG /Name ({Contested}) >>",
            /* 8 */ $"<< /Type /OCG /Name ({Suppressed}) >>",
            /* 9 */ "<< /OCGs [5 0 R 6 0 R 7 0 R 8 0 R] /D 10 0 R >>",
            /* 10 */ $"<< {baseState}/ON [6 0 R 7 0 R] /OFF [7 0 R 8 0 R] >>",
        });
    }

    private static string Stream(string body)
        => $"<< /Length {body.Length} >>\nstream\n{body}\nendstream";

    /// <summary>
    /// Assembles a PDF from pre-formatted object bodies (1-based) with a correct xref table,
    /// so the fixture stays self-contained.
    /// </summary>
    private static byte[] Assemble(IReadOnlyList<string> objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
