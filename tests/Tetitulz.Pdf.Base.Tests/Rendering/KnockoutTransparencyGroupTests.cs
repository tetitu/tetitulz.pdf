// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Knockout transparency groups (ISO 32000-1 §11.4.5.6): inside a knockout group each element
/// composites with the group's <b>initial backdrop</b>, not with the elements painted before it,
/// so a later element replaces an earlier one where they overlap.
///
/// <para><b>How these tests tell the two apart.</b> The fixture paints an opaque red square, then a
/// half-transparent blue square overlapping it. The overlap is the whole test:</para>
/// <list type="bullet">
///   <item><b>Not knocked out</b> — blue composites over red: <c>0.5·blue + 0.5·red</c> = purple,
///   green channel ≈ 0.</item>
///   <item><b>Knocked out</b> — blue composites with the group's transparent initial backdrop, so
///   the group holds blue at α 0.5, which lands on the white page as <c>0.5·blue + 0.5·white</c> =
///   light blue, green channel ≈ 127.</item>
/// </list>
/// <para>The green channel therefore separates the two cases by ~127 levels, which no amount of
/// anti-aliasing or rounding can blur. A test that only checked "something blue-ish is there" would
/// pass either way — which is exactly how this feature could have been declared done while doing
/// nothing.</para>
/// </summary>
public class KnockoutTransparencyGroupTests
{
    private const float OverlapX = 175, OverlapY = 175;   // inside both squares
    private const float RedOnlyX = 120, RedOnlyY = 120;   // red square only
    private const float BlueOnlyX = 225, BlueOnlyY = 225; // blue square only

    /// <summary>
    /// A page whose only content is one transparency-group form XObject holding two overlapping
    /// squares, the second at <c>ca 0.5</c>.
    /// </summary>
    /// <param name="doBlendMode">
    /// A <c>/BM</c> to set on the page immediately before the <c>Do</c>, or null for none. A
    /// non-Normal blend at the <c>Do</c> is what makes a <c>/I false</c> group refuse to seed its
    /// backdrop — see <see cref="NonIsolatedKnockoutGroup_ThatCouldNotSeedItsBackdrop_KnocksOut"/>.
    /// </param>
    private static byte[] TwoOverlappingSquares(bool knockout, bool isolated = true,
                                                string doBlendMode = null)
    {
        const string groupContent =
            "1 0 0 rg 100 100 100 100 re f\n" +
            "/GS0 gs\n" +
            "0 0 1 rg 150 150 100 100 re f";

        string group = $"/Group << /S /Transparency /CS /DeviceRGB " +
                       $"/I {(isolated ? "true" : "false")} /K {(knockout ? "true" : "false")} >>";

        string pageResources = doBlendMode == null
            ? "<< /XObject << /Fm0 5 0 R >> >>"
            : "<< /XObject << /Fm0 5 0 R >> /ExtGState << /GS1 7 0 R >> >>";
        string pageContent = doBlendMode == null ? "/Fm0 Do" : "/GS1 gs\n/Fm0 Do";

        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R " +
            $"/Resources {pageResources} >>",
            FormPdfFixtures.StreamBody(string.Empty, pageContent),
            FormPdfFixtures.StreamBody(
                "/Type /XObject /Subtype /Form /BBox [0 0 300 300] " +
                "/Resources << /ExtGState << /GS0 6 0 R >> >> " + group,
                groupContent),
            "<< /Type /ExtGState /ca 0.5 >>",
        };
        if (doBlendMode != null) bodies.Add($"<< /Type /ExtGState /BM /{doBlendMode} >>");

        return FormPdfFixtures.Build(bodies.ToArray());
    }

    private static SKColor At(byte[] pdf, float x, float y)
    {
        using var bmp = PixelAssert.Render(pdf);
        return PixelAssert.At(bmp, x, y);
    }

    // ================================================================

    [Fact]
    public void KnockoutGroup_LaterElement_ReplacesTheEarlierOneInTheOverlap()
    {
        SKColor overlap = At(TwoOverlappingSquares(knockout: true), OverlapX, OverlapY);

        // 0.5·blue + 0.5·white — the red underneath was knocked out, so green comes from the page.
        Assert.InRange(overlap.Green, 100, 160);
        Assert.InRange(overlap.Red, 100, 160);
        Assert.InRange(overlap.Blue, 220, 255);
    }

    [Fact]
    public void NonKnockoutGroup_LaterElement_CompositesOverTheEarlierOne()
    {
        SKColor overlap = At(TwoOverlappingSquares(knockout: false), OverlapX, OverlapY);

        // 0.5·blue + 0.5·red — no green anywhere, because neither element contributes any.
        Assert.InRange(overlap.Green, 0, 40);
        Assert.InRange(overlap.Red, 100, 160);
        Assert.InRange(overlap.Blue, 100, 160);
    }

    /// <summary>
    /// The direct comparison, stated as one claim: the same file with only <c>/K</c> flipped must
    /// render the overlap differently. This is what makes the pair falsifiable as a unit — if
    /// <c>/K</c> were ignored, both renders would be identical and this fails whatever the
    /// absolute values happen to be.
    /// </summary>
    [Fact]
    public void FlippingKnockout_ChangesTheOverlap()
    {
        SKColor knocked = At(TwoOverlappingSquares(knockout: true), OverlapX, OverlapY);
        SKColor plain = At(TwoOverlappingSquares(knockout: false), OverlapX, OverlapY);

        Assert.True(knocked.Green - plain.Green > 80,
            $"knockout should leave the page's green showing through; got {knocked} vs {plain}");
    }

    /// <summary>
    /// Knockout applies between <i>overlapping</i> elements. Where they do not overlap each element
    /// must render exactly as it would outside a knockout group, or the emulation has broken
    /// ordinary painting.
    /// </summary>
    [Fact]
    public void KnockoutGroup_NonOverlappingAreas_AreUnaffected()
    {
        byte[] pdf = TwoOverlappingSquares(knockout: true);

        SKColor red = At(pdf, RedOnlyX, RedOnlyY);
        Assert.InRange(red.Red, 220, 255);
        Assert.InRange(red.Green, 0, 40);

        SKColor blue = At(pdf, BlueOnlyX, BlueOnlyY);
        Assert.InRange(blue.Blue, 220, 255);
        Assert.InRange(blue.Red, 100, 160);   // 0.5 alpha over the white page
    }

    /// <summary>
    /// A non-isolated knockout group that <b>did</b> seed its backdrop is not emulated — its
    /// elements must composite with the page they were seeded from, which replacement would
    /// overwrite — so it still renders non-knockout. Pinned so the limit is a decision rather than
    /// an accident, and so the narrowing in
    /// <see cref="NonIsolatedKnockoutGroup_ThatCouldNotSeedItsBackdrop_KnocksOut"/> cannot quietly
    /// widen into this case.
    /// </summary>
    [Fact]
    public void NonIsolatedKnockoutGroup_ThatSeededItsBackdrop_StillRendersNonKnockout()
    {
        SKColor overlap = At(TwoOverlappingSquares(knockout: true, isolated: false),
                             OverlapX, OverlapY);

        Assert.InRange(overlap.Green, 0, 40);
    }

    /// <summary>
    /// A <c>/I false /K true</c> group whose backdrop seeding was <b>refused</b> — here by a
    /// non-Normal blend mode at the <c>Do</c>, which would otherwise blend the group against a
    /// backdrop it already contains — has degraded to isolated, so its initial backdrop is
    /// transparent and §11.3.6's α<sub>b</sub> = 0 argument applies exactly as it does to a group
    /// declared isolated. It therefore knocks out. Declining the emulation there withheld an answer
    /// that costs nothing and is right.
    ///
    /// <para><b>What the numbers are.</b> The group result is composited onto the white page with
    /// Multiply. Knocked out, the overlap holds blue at α 0.5, and Multiply against white leaves
    /// <c>0.5·1 + 0.5·(0·1)</c> = ~127 in green. Not knocked out, the overlap holds opaque purple,
    /// whose green is 0 and stays 0 through Multiply. The <c>/K false</c> control renders the same
    /// blend mode over the same geometry, so neither assertion can pass on Multiply alone.</para>
    /// </summary>
    [Fact]
    public void NonIsolatedKnockoutGroup_ThatCouldNotSeedItsBackdrop_KnocksOut()
    {
        SKColor knocked = At(
            TwoOverlappingSquares(knockout: true, isolated: false, doBlendMode: "Multiply"),
            OverlapX, OverlapY);
        SKColor plain = At(
            TwoOverlappingSquares(knockout: false, isolated: false, doBlendMode: "Multiply"),
            OverlapX, OverlapY);

        Assert.InRange(knocked.Green, 100, 160);
        Assert.InRange(plain.Green, 0, 40);
        Assert.True(knocked.Green - plain.Green > 80,
            $"the degraded-to-isolated group should knock out; got {knocked} vs {plain}");
    }
}
