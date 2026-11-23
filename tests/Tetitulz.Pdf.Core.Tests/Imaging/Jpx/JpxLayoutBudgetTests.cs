// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// A tile's layout — its precincts, precinct-bands and code-blocks — costs work and memory by the
/// structures the header declares, not by the file's bytes: 1 × 1 precincts and code-blocks are legal
/// (PPx = PPy = 0 at r = 0), a structure per sample, and every tile's layout is held until tier 1. So
/// the layout is charged to the decode's work budget as it is made
/// (<see cref="JpxTileLayout.StructuresPerUnit"/>) and its memory joins the working-set check
/// (<see cref="JpxTileLayout.BytesPerStructure"/>). Asserted on the budget's evidence and the refusal,
/// never a clock.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class JpxLayoutBudgetTests
{
    [Fact]
    public void Adversarial_structure_per_sample_layout_is_charged_to_the_work_budget()
    {
        byte[] cs = Codestream(side: 1400, tilesAcross: 1, onePixelPrecincts: true, codeBlockExponent: 0);
        Assert.True(JpxCodestreamParser.TryParse(cs, out JpxCodestream parsed, out string reason), reason);

        // The decode's own allowance for these 83 bytes and 1.96 million samples: about 73 000 units.
        var budget = new WorkBudget(JpxDecoder.WorkAllowed(cs.Length, 1400L * 1400), "test decode");

        Assert.Throws<WorkBudgetExceededException>(() => JpxTier2Decoder.TryDecodeTile(parsed, 0, budget, out _, out _));
        Assert.True(budget.IsExhausted);
    }

    [Fact]
    public void Adversarial_structure_per_sample_codestream_is_refused_by_the_decoder()
    {
        byte[] cs = Codestream(side: 1400, tilesAcross: 3, onePixelPrecincts: true, codeBlockExponent: 0);

        Assert.False(JpxDecoder.TryDecode(cs, 0, out _, out string reason, 1));
        Assert.Contains("MaxJpxDecodeWorkPerByte", reason);
    }

    /// <summary>
    /// The layout's memory joins the working set: 512² samples of 4 × 4 code-blocks hold 16 384
    /// code-blocks (5.6 MB at 344 bytes) beside 4.5 MB of planes, so under a 6 MB ceiling the planes
    /// alone would pass and the two together do not.
    /// </summary>
    [Fact]
    public void Layout_memory_is_part_of_the_working_set()
    {
        byte[] cs = Codestream(side: 512, tilesAcross: 1, onePixelPrecincts: false, codeBlockExponent: 0);
        try
        {
            PdfLimits.MaxJpxWorkingSetBytes = 6L * 1024 * 1024;

            Assert.False(JpxDecoder.TryDecode(cs, 0, out _, out string reason, 1));
            Assert.Contains("code-blocks and precinct-bands", reason);
            Assert.Contains("MaxJpxWorkingSetBytes", reason);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: the same 4 × 4 code-block image decodes under the default ceilings — the blank tile's mid grey.</summary>
    [Fact]
    public void Small_code_blocks_decode_under_the_default_ceilings()
    {
        byte[] cs = Codestream(side: 512, tilesAcross: 1, onePixelPrecincts: false, codeBlockExponent: 0);

        Assert.True(JpxDecoder.TryDecode(cs, 0, out JpxDecodeResult result, out string reason, 1), reason);
        Assert.Equal(512, result.Image.Width);
        Assert.Equal(128, result.Image.Samples[0][512 * 256 + 256]);
    }

    /// <summary>
    /// <paramref name="tilesAcross"/>² tiles of <paramref name="side"/>² samples, one 8-bit component, no
    /// decomposition levels, code-blocks of 2^(<paramref name="codeBlockExponent"/> + 2) samples a side,
    /// 1 × 1 precincts or the default, each tile-part one empty packet.
    /// </summary>
    internal static byte[] Codestream(int side, int tilesAcross, bool onePixelPrecincts, int codeBlockExponent)
    {
        int full = side * tilesAcross;
        var ms = new MemoryStream();
        void U16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        void U32(long v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        U16(0xFF4F);
        U16(0xFF51); U16(41); U16(0);
        U32(full); U32(full); U32(0); U32(0);
        U32(side); U32(side); U32(0); U32(0);
        U16(1); ms.WriteByte(7); ms.WriteByte(1); ms.WriteByte(1);
        U16(0xFF52); U16(onePixelPrecincts ? 13 : 12);
        ms.WriteByte((byte)(onePixelPrecincts ? 0x01 : 0x00));
        ms.WriteByte(0); U16(1); ms.WriteByte(0);
        ms.WriteByte(0);
        ms.WriteByte((byte)codeBlockExponent); ms.WriteByte((byte)codeBlockExponent);
        ms.WriteByte(0); ms.WriteByte(1);
        if (onePixelPrecincts) ms.WriteByte(0x00);
        U16(0xFF5C); U16(4); ms.WriteByte(0x40); ms.WriteByte(8 << 3);
        for (int t = 0; t < tilesAcross * tilesAcross; t++)
        {
            U16(0xFF90); U16(10); U16(t); U32(12 + 2 + 1); ms.WriteByte(0); ms.WriteByte(1);
            U16(0xFF93);
            ms.WriteByte(0x00);
        }
        U16(0xFFD9);
        return ms.ToArray();
    }
}
