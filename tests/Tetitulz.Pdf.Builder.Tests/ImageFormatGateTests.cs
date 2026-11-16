// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BuilderImageDecoder.CanDecode"/> — the public gate that answers "can this be
/// embedded, and if not, why" — and the rule it now shares with <c>Decode</c>.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep found <c>CanDecode</c> had <b>no test at
/// all</b>: deleting the three-component acceptance, or flipping the Adobe-marker condition,
/// changed nothing in a suite of 473. It is public API whose reason strings are written "for a
/// caller to put in front of a user", and its own summary claims its answer is "identical to the
/// one <c>Decode</c> will give" — a claim nothing checked.</para>
///
/// <para><b>The component policy is now one method.</b> Format sniffing was already shared through
/// <c>RasterImageInspector</c>, but the component-count rules were written out in both
/// <c>CanDecode</c> and <c>DecodeJpeg</c>, so "identical" rested on the two copies happening to
/// match. <see cref="TheGateAgreesWithTheDecoder"/> pins the survivor of that merge, which is the
/// step CLAUDE.md's rule 5 requires: a merge whose survivor no test reaches is a deletion.</para>
///
/// <para><b>The fixtures are JPEG headers, not JPEGs.</b> Nothing here decodes a pixel — the gate
/// reads markers — so a SOI, an optional APP14 and a SOF0 are the whole input. That keeps the file
/// self-contained and lets it construct frames no encoder would willingly produce, such as the
/// two-component case.</para>
/// </remarks>
public class ImageFormatGateTests
{
    /// <summary>
    /// A JPEG header: SOI, an optional Adobe APP14, a baseline SOF0 declaring
    /// <paramref name="components"/> channels, then EOI.
    /// </summary>
    private static byte[] Jpeg(int components, bool adobeMarker = false)
    {
        using var ms = new MemoryStream();
        void Be16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        ms.WriteByte(0xFF); ms.WriteByte(0xD8);                     // SOI

        if (adobeMarker)
        {
            byte[] id = Encoding.ASCII.GetBytes("Adobe");
            ms.WriteByte(0xFF); ms.WriteByte(0xEE);                 // APP14
            Be16(2 + id.Length + 7);
            ms.Write(id, 0, id.Length);
            // version, three flag words, then the transform byte the inspector reads last.
            ms.Write(new byte[] { 0x00, 0x64, 0, 0, 0, 0, 0x02 }, 0, 7);
        }

        ms.WriteByte(0xFF); ms.WriteByte(0xC0);                     // SOF0, baseline
        Be16(8 + 3 * components);
        ms.WriteByte(8);                                            // sample precision
        Be16(16); Be16(16);                                         // height, width
        ms.WriteByte((byte)components);
        for (int i = 0; i < components; i++)
        {
            ms.WriteByte((byte)(i + 1));   // component id
            ms.WriteByte(0x11);            // sampling factors
            ms.WriteByte(0);               // quantisation table
        }

        ms.WriteByte(0xFF); ms.WriteByte(0xD9);                     // EOI
        return ms.ToArray();
    }

    // ── accepted ──────────────────────────────────────────────────────────────────────

    /// <summary>Three components is the ordinary colour JPEG, and the commonest input there is.</summary>
    [Fact]
    public void AThreeComponentJpeg_IsAccepted()
    {
        Assert.True(BuilderImageDecoder.CanDecode(Jpeg(3), out string? reason), reason);
        Assert.Null(reason);
    }

    /// <summary>One component is greyscale.</summary>
    [Fact]
    public void ASingleComponentJpeg_IsAccepted()
    {
        Assert.True(BuilderImageDecoder.CanDecode(Jpeg(1), out string? reason), reason);
        Assert.Null(reason);
    }

    /// <summary>Four components <i>with</i> the Adobe APP14 marker is CMYK, and embeddable.</summary>
    [Fact]
    public void AFourComponentJpeg_WithTheAdobeMarker_IsAccepted()
    {
        Assert.True(BuilderImageDecoder.CanDecode(Jpeg(4, adobeMarker: true), out string? reason), reason);
        Assert.Null(reason);
    }

    // ── refused, each for the reason in its name ──────────────────────────────────────

    /// <summary>
    /// Four components <i>without</i> the marker has no identifiable colour space. Refusing is the
    /// point: guessing CMYK inverts the image.
    /// </summary>
    [Fact]
    public void AFourComponentJpeg_WithoutTheAdobeMarker_IsRefused_NamingTheMarker()
    {
        Assert.False(BuilderImageDecoder.CanDecode(Jpeg(4), out string? reason));
        Assert.NotNull(reason);
        Assert.Contains("APP14", reason!);
    }

    /// <summary>A component count no colour space explains is refused, and the count is named.</summary>
    [Fact]
    public void ATwoComponentJpeg_IsRefused_NamingTheCount()
    {
        Assert.False(BuilderImageDecoder.CanDecode(Jpeg(2), out string? reason));
        Assert.NotNull(reason);
        Assert.Contains("2", reason!);
    }

    [Fact]
    public void EmptyBytes_AreRefused()
    {
        Assert.False(BuilderImageDecoder.CanDecode(new byte[0], out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void BytesThatAreNotAnImage_AreRefused()
    {
        Assert.False(BuilderImageDecoder.CanDecode(
            Encoding.ASCII.GetBytes("this is not an image at all"), out string? reason));
        Assert.NotNull(reason);
    }

    // ── the claim the merge made structural ──────────────────────────────────────────

    /// <summary>
    /// <b>The gate and the decoder give the same answer</b>, which is what <c>CanDecode</c> exists
    /// to promise — a caller that is told "yes" and then gets nothing back has been told the wrong
    /// thing where the source URL was still known, and can no longer name the file.
    /// </summary>
    /// <remarks>
    /// <para>The four-component pair is the case that matters: both arms turn on the Adobe marker,
    /// and before the rules were shared each side decided it separately.</para>
    ///
    /// <para><b>This test cannot replace the absolute ones above, and the merge is why.</b> Now
    /// that both callers read one rule, a mutation to that rule moves <i>both</i> sides together
    /// and this assertion still passes — CLAUDE.md's third blind spot, that a relative assertion
    /// cannot see a change applied to both of the things it relates. Verified rather than assumed:
    /// mutating the three-component arm and flipping the Adobe-marker condition were each killed
    /// only by the absolute tests. What this pins is the property the merge was performed to
    /// guarantee, so a future caller that reintroduces its own copy is caught.</para>
    /// </remarks>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    [InlineData(2, false)]
    public void TheGateAgreesWithTheDecoder(int components, bool adobeMarker)
    {
        byte[] bytes = Jpeg(components, adobeMarker);

        bool gate = BuilderImageDecoder.CanDecode(bytes, out _);
        bool decoded = BuilderImageDecoder.Decode(bytes) != null;

        Assert.Equal(gate, decoded);
    }
}
