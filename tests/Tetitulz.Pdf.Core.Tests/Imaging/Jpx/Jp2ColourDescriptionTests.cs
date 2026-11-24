// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Tetitulz.Pdf.TestHelpers;
using H = Tetitulz.Pdf.TestHelpers.Jp2Headers;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="Jp2ColourDescription"/>: the JP2 Header box's colour and channel boxes (T.800 I.5.3),
/// the first compositing layer's Opacity box (T.801 M.11.7.6), and what a PDF consumer makes of them
/// (ISO 32000-2 §7.4.9).
/// </summary>
/// <remarks>
/// Every refusal is asserted <b>for the reason in its name</b> — the reason's wording, not merely a
/// false return — and sits beside the acceptance that differs from it in one box. Each PDF rule has
/// a test citing the clause. The corpus test at the end states what each ISO conformance JP2 file's
/// header holds, checked against an independent tool 12.2 (the reference decoder) reading the same files.
/// </remarks>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class Jp2ColourDescriptionTests
{
    // ── fixtures ────────────────────────────────────────────────────────────

    private static readonly Jp2PdfImageFacts NoDictionary = new(0, false, 0, false);
    private static readonly Jp2PdfImageFacts AsImageMask = new(0, false, 0, true);
    private static Jp2PdfImageFacts WithColourSpace(int n, bool indexed = false, int smask = 0) => new(n, indexed, smask, false);
    private static Jp2PdfImageFacts WithSMask(int v) => new(0, false, v, false);

    private static JpxComponentSize[] Comps(params int[] bits) =>
        Array.ConvertAll(bits, b => new JpxComponentSize(b, false, 1, 1));

    private static byte[] Header(params byte[][] boxes) => H.Container(H.Jp2h(boxes));

    /// <summary>A three-component 8-bit sRGB header plus any further header boxes.</summary>
    private static byte[] Rgb(params byte[][] more) =>
        Header(H.Ihdr(64, 64, 3, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Concat(more));

    /// <summary>Four components, sRGB, with a cdef typing the fourth as whole-image opacity.</summary>
    private static byte[] Rgba(int opacityType = Jp2ChannelDefinition.OpacityType, int association = 0) =>
        Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb),
            H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, opacityType, association)));

    private static readonly byte[] Palette3 = H.Pclr(new byte[] { 7, 7, 7 },
        new long[] { 0, 0, 0 }, new long[] { 255, 255, 255 }, new long[] { 23, 12, 21 });
    private static readonly byte[] Mapping3 = H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2));

    /// <summary>One index component expanded to RGB through the palette, like conformance file9.</summary>
    private static byte[] Palettised(params byte[][] more) =>
        Header(H.Ihdr(64, 64, 1, 7), Palette3, Mapping3, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb), H.Concat(more));

    private static Jp2ColourDescription Read(byte[] jp2)
    {
        Assert.True(Jp2ColourDescription.TryRead(jp2, out Jp2ColourDescription d, out string reason), reason);
        return d;
    }

    private static void Refuses(byte[] jp2, string reasonFragment)
    {
        Assert.False(Jp2ColourDescription.TryRead(jp2, out _, out string reason));
        Assert.Contains(reasonFragment, reason, StringComparison.Ordinal);
    }

    private static Jp2PdfChannelPlan Plan(Jp2ColourDescription d, JpxComponentSize[] comps, Jp2PdfImageFacts facts,
        Func<byte[], bool>? icc = null)
    {
        Assert.True(d.TryPlanForPdf(comps, facts, icc, out Jp2PdfChannelPlan plan, out string reason), reason);
        return plan;
    }

    private static string PlanRefusal(Jp2ColourDescription d, JpxComponentSize[] comps, Jp2PdfImageFacts facts)
    {
        Assert.False(d.TryPlanForPdf(comps, facts, null, out _, out string reason));
        return reason;
    }

    private static void AssertDeviation(List<string> deviations, string fragment) =>
        Assert.Contains(deviations, s => s.Contains(fragment, StringComparison.Ordinal));

    private static byte[] U32s(params uint[] values)
    {
        var b = new byte[4 * values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            b[4 * i] = (byte)(values[i] >> 24); b[4 * i + 1] = (byte)(values[i] >> 16);
            b[4 * i + 2] = (byte)(values[i] >> 8); b[4 * i + 3] = (byte)values[i];
        }
        return b;
    }

    // ── reading: every box ──────────────────────────────────────────────────

    [Fact]
    public void TryRead_ReadsEveryHeaderBox()
    {
        byte[] jp2 = H.Container(
            H.Jp2h(
                H.Ihdr(64, 48, 2, 255, unknownColourSpace: 1),
                H.Bpcc(7, 0x83),                                     // 8-bit unsigned, 4-bit signed
                H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, precedence: 2, approximation: 1),
                H.Pclr(new byte[] { 7, 9 }, new long[] { 1, 2 }, new long[] { 3, 1000 }),
                H.Cmap((0, 1, 0), (0, 1, 1), (1, 0, 0)),
                H.Cdef((0, 0, 1), (1, 0, 2), (2, 1, 0))),
            H.Jplh(H.Opct(1)));

        Jp2ColourDescription d = Read(jp2);

        Assert.True(d.HasImageHeader);
        Assert.Equal((48L, 64L, 2), (d.Width, d.Height, d.ComponentCount));
        Assert.Equal(new[] { 8, 4 }, d.ComponentDepths);
        Assert.Equal(new[] { false, true }, d.ComponentSigned);
        Assert.True(d.ColourSpaceUnknown);

        Jp2ColourSpecification spec = Assert.Single(d.ColourSpecifications);
        Assert.Equal((Jp2ColourSpecification.EnumeratedMethod, 2, 1, 16L, 3), (spec.Method, spec.Precedence, spec.Approximation, spec.EnumeratedSpace, spec.ColourChannels));
        Assert.Empty(spec.EnumeratedParameters);
        Assert.Null(spec.IccProfile);

        Assert.Equal((2, 2), (d.Palette.EntryCount, d.Palette.ColumnCount));
        Assert.Equal(new[] { 8, 10 }, d.Palette.ColumnDepths);
        Assert.Equal(1000, d.Palette.Value(1, 1));

        Assert.Equal(3, d.ComponentMapping.Length);
        Assert.Equal((0, true, 1), (d.ComponentMapping[1].Component, d.ComponentMapping[1].ThroughPalette, d.ComponentMapping[1].PaletteColumn));
        Assert.Equal((1, false, 0), (d.ComponentMapping[2].Component, d.ComponentMapping[2].ThroughPalette, d.ComponentMapping[2].PaletteColumn));

        Assert.Equal(3, d.ChannelDefinitions.Length);
        Assert.Equal((2, 1, 0), (d.ChannelDefinitions[2].Channel, d.ChannelDefinitions[2].Type, d.ChannelDefinitions[2].Association));

        Assert.Equal(1, d.OpacityBoxType);
        Assert.Empty(d.Deviations);
    }

    [Fact]
    public void TryRead_ToleratesAbsentOptionalBoxes()
    {
        Jp2ColourDescription d = Read(Rgb());

        Assert.Null(d.Palette);
        Assert.Null(d.ComponentMapping);
        Assert.Null(d.ChannelDefinitions);
        Assert.Equal(-1, d.OpacityBoxType);
        Assert.Equal(new[] { 8, 8, 8 }, d.ComponentDepths);   // from ihdr's BPC
        Assert.Empty(d.Deviations);
    }

    /// <summary>T.800 I.5.3.1 requires an ihdr; a header without one is read for what it does hold, and the codestream supplies the rest.</summary>
    [Fact]
    public void TryRead_MissingImageHeader_IsToleratedWithADeviation()
    {
        Jp2ColourDescription d = Read(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb)));

        Assert.False(d.HasImageHeader);
        Assert.Null(d.ComponentDepths);
        Assert.Equal(3, Assert.Single(d.ColourSpecifications).ColourChannels);
        AssertDeviation(d.Deviations, "no Image Header box (ihdr)");
    }

    [Fact]
    public void TryRead_RawCodestreamOrHeaderlessContainer_IsRefused()
    {
        Refuses(new byte[] { 0xFF, 0x4F, 0xFF, 0x51, 0, 0 }, "raw codestream");
        Refuses(H.Container(H.Box("jp2c", new byte[8])), "no JP2 Header box");
        // Control: the same container with a header reads.
        Assert.True(Jp2ColourDescription.TryRead(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Box("jp2c", new byte[8])), out _, out _));
    }

    // ── reading: ihdr and bpcc ──────────────────────────────────────────────

    [Fact]
    public void TryRead_ImageHeaderOfWrongLength_IsRefused()
    {
        Refuses(Header(H.Box("ihdr", new byte[13])), "fixes it at 14");
        Refuses(Header(H.Box("ihdr", new byte[15])), "fixes it at 14");
        Assert.True(Read(Header(H.Ihdr(1, 1, 1, 0))).HasImageHeader);
    }

    [Fact]
    public void TryRead_ImageHeaderWithZeroComponentsOrExtent_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 0, 7)), "declares 0 components");
        Refuses(Header(H.Ihdr(64, 0, 3, 7)), "0x64 image");
        Refuses(Header(H.Ihdr(0, 64, 3, 7)), "64x0 image");
        Assert.Equal(16384, Read(Header(H.Ihdr(64, 64, 16384, 7))).ComponentCount);
    }

    [Fact]
    public void TryRead_ImageHeaderDepthPast38Bits_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 0x26)), "39-bit components");
        Assert.Equal(new[] { 38 }, Read(Header(H.Ihdr(64, 64, 1, 0x25))).ComponentDepths);
    }

    [Fact]
    public void TryRead_UnexpectedCompressionOrUnkC_IsADeviation()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 1, 7, compression: 8, unknownColourSpace: 2)));
        AssertDeviation(d.Deviations, "compression type 8");
        AssertDeviation(d.Deviations, "UnkC is 2");
        Assert.False(d.ColourSpaceUnknown);
    }

    [Fact]
    public void TryRead_DuplicateBox_IsRefused()
    {
        byte[] ihdr = H.Ihdr(64, 64, 3, 255);
        var cases = new (byte[] jp2, string box)[]
        {
            (Header(ihdr, ihdr), "two Image Header boxes"),
            (Header(ihdr, H.Bpcc(7, 7, 7), H.Bpcc(7, 7, 7)), "two Bits Per Component boxes"),
            (Header(H.Ihdr(64, 64, 1, 7), Palette3, Palette3, Mapping3), "two Palette boxes"),
            (Header(H.Ihdr(64, 64, 1, 7), Palette3, Mapping3, Mapping3), "two Component Mapping boxes"),
            (Header(ihdr, H.Cdef((0, 0, 1)), H.Cdef((0, 0, 1))), "two Channel Definition boxes"),
        };
        foreach ((byte[] jp2, string box) in cases) Refuses(jp2, box);
        // Control: one of each reads. Several colr boxes are allowed (T.800 I.5.3.3).
        Assert.Equal(2, Read(Header(H.Ihdr(64, 64, 1, 7), Palette3, Mapping3, H.Cdef((0, 0, 1)), H.ColrEnumerated(16), H.ColrEnumerated(17))).ColourSpecifications.Length);
    }

    /// <summary>T.800 I.5.3.2: when the components vary in depth, ihdr says 255 and bpcc lists each.</summary>
    [Fact]
    public void TryRead_BitsPerComponent_SuppliesEachDepthAndSign()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 255), H.Bpcc(7, 0x8B, 0x0F), H.ColrEnumerated(16)));

        Assert.Equal(new[] { 8, 12, 16 }, d.ComponentDepths);
        Assert.Equal(new[] { false, true, false }, d.ComponentSigned);
        Assert.Empty(d.Deviations);
    }

    [Fact]
    public void TryRead_BitsPerComponentCountDisagreeingWithNC_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 3, 255), H.Bpcc(7, 7)), "lists 2 depths for 3 components");
        Refuses(Header(H.Ihdr(64, 64, 3, 255), H.Bpcc(7, 7, 7, 7)), "lists 4 depths for 3 components");
        Assert.Equal(3, Read(Header(H.Ihdr(64, 64, 3, 255), H.Bpcc(7, 7, 7))).ComponentDepths.Length);
    }

    [Fact]
    public void TryRead_BitsPerComponentDepthPast38Bits_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 2, 255), H.Bpcc(7, 0x26)), "component 1 39 bits");
    }

    [Fact]
    public void TryRead_VaryingDepthWithoutBpcc_IsADeviation()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 255)));
        Assert.Null(d.ComponentDepths);
        AssertDeviation(d.Deviations, "BPC 255");
    }

    [Fact]
    public void TryRead_BpccBesideAUniformIhdr_IsADeviationAndWins()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 7), H.Bpcc(0x0B, 0x0B, 0x0B)));
        Assert.Equal(new[] { 12, 12, 12 }, d.ComponentDepths);
        AssertDeviation(d.Deviations, "per-component depths are used");
    }

    // ── reading: colr ───────────────────────────────────────────────────────

    [Fact]
    public void TryRead_TruncatedColourSpecification_IsRefused()
    {
        Refuses(Header(H.Box("colr", new byte[] { 1 })), "need 3");
        Refuses(Header(H.Box("colr", new byte[] { 1, 0, 0, 0, 0, 16 })), "no EnumCS follows APPROX");
        Refuses(Header(H.ColrIcc(new byte[19])), "too short to name its data colour space");
        // Controls: the shortest of each form reads.
        Assert.Equal(16, Read(Header(H.Box("colr", new byte[] { 1, 0, 0, 0, 0, 0, 16 }))).ColourSpecifications[0].EnumeratedSpace);
        Assert.Equal(20, Read(Header(H.ColrIcc(new byte[20]))).ColourSpecifications[0].IccProfileLength);
    }

    /// <summary>T.800 Table I.9 / T.801 Table M.22: an unknown METH is ignored, not refused — the box stays listed, never chosen.</summary>
    [Fact]
    public void TryRead_UnknownColourMethod_IsKeptButImpliesNoChannels()
    {
        byte[] jp2 = Header(H.Ihdr(64, 64, 3, 7), H.Box("colr", new byte[] { 4, 0, 1, 9, 9, 9 }), H.ColrEnumerated(16));
        Jp2ColourDescription d = Read(jp2);

        Assert.Equal(2, d.ColourSpecifications.Length);
        Assert.Equal((4, 0), (d.ColourSpecifications[0].Method, d.ColourSpecifications[0].ColourChannels));
        Assert.True(JpxHeader.TryReadColourComponents(jp2, out int colour));
        Assert.Equal(3, colour);
    }

    [Fact]
    public void TryRead_IccProfile_KeepsTheBytesAndTheSignature()
    {
        byte[] profile = H.IccProfile("CMYK", 200);
        Jp2ColourSpecification spec = Assert.Single(Read(Header(H.ColrIcc(profile, method: 3, precedence: -1))).ColourSpecifications);

        Assert.Equal((Jp2ColourSpecification.AnyIccMethod, -1, "CMYK", 4, 200L), (spec.Method, spec.Precedence, spec.IccDataColourSpace, spec.ColourChannels, spec.IccProfileLength));
        Assert.Equal(profile, spec.IccProfile);
        Assert.True(spec.IsIcc);
        Assert.Equal(-1, spec.EnumeratedSpace);
    }

    [Fact]
    public void TryRead_IccProfileOverTheCap_IsNotKeptAndThePlanPassesItOver()
    {
        byte[] jp2 = Header(H.Ihdr(64, 64, 3, 7), H.ColrIcc(H.IccProfile("RGB ", 132)));
        try
        {
            PdfLimits.MaxIccProfileBytes = 100;
            Jp2ColourDescription d = Read(jp2);
            Jp2ColourSpecification spec = Assert.Single(d.ColourSpecifications);
            Assert.Null(spec.IccProfile);
            Assert.Equal((132L, 3), (spec.IccProfileLength, spec.ColourChannels));   // the signature is still read
            AssertDeviation(d.Deviations, "exceeds PdfLimits.MaxIccProfileBytes");

            Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8), NoDictionary);
            Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, plan.ColourSpace);
            AssertDeviation(plan.Deviations, "exceeds PdfLimits.MaxIccProfileBytes");

            // Control: under the cap the profile is kept and chosen.
            PdfLimits.MaxIccProfileBytes = 132;
            Assert.Equal(132, Read(jp2).ColourSpecifications[0].IccProfile.Length);
            Assert.Equal(Jp2ColourSpaceKind.IccProfile, Plan(Read(jp2), Comps(8, 8, 8), NoDictionary).ColourSpace);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>T.801 M.11.7.4.1, Table M.30: the seven fields of a CIELab EP.</summary>
    [Fact]
    public void TryRead_CieLabParameters_AreRead()
    {
        byte[] ep = U32s(100, 0, 255, 128, 255, 96, Jp2CieLabParameters.IlluminantD50);
        Jp2ColourSpecification spec = Assert.Single(Read(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab, ep: ep))).ColourSpecifications);

        Assert.Equal(28, spec.EnumeratedParameters.Length);
        Assert.True(Jp2CieLabParameters.TryParse(spec.EnumeratedParameters, out Jp2CieLabParameters p));
        Assert.Equal((100L, 0L, 255L, 128L, 255L, 96L), (p.RangeL, p.OffsetL, p.RangeA, p.OffsetA, p.RangeB, p.OffsetB));
        Assert.Equal(Jp2CieLabParameters.IlluminantD50, p.Illuminant);
        Assert.False(p.IsDefault);
    }

    [Fact]
    public void TryRead_CieLabParametersOfWrongLength_IsRefused()
    {
        Refuses(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab, ep: new byte[27])), "T.801 Table M.30 defines 28");
        // Controls: none (defaults) and exactly 28 read; another space's EP is not checked.
        Assert.Empty(Read(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab))).ColourSpecifications[0].EnumeratedParameters);
        Assert.Equal(28, Read(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab, ep: new byte[28]))).ColourSpecifications[0].EnumeratedParameters.Length);
        Assert.Equal(27, Read(Header(H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, ep: new byte[27]))).ColourSpecifications[0].EnumeratedParameters.Length);
        Assert.False(Jp2CieLabParameters.TryParse(new byte[27], out _));
    }

    /// <summary>T.801 Table M.29bis: the offsets follow the channel depth for unsigned channels and are 0 for signed ones.</summary>
    [Fact]
    public void CieLabDefaults_FollowTableM29bis()
    {
        Jp2CieLabParameters eight = Jp2CieLabParameters.Default(8, false, 8, false);
        Assert.Equal((100L, 0L, 170L, 128L, 200L, 96L), (eight.RangeL, eight.OffsetL, eight.RangeA, eight.OffsetA, eight.RangeB, eight.OffsetB));
        Assert.Equal(Jp2CieLabParameters.IlluminantD50, eight.Illuminant);
        Assert.True(eight.IsDefault);

        Jp2CieLabParameters sixteen = Jp2CieLabParameters.Default(16, false, 16, false);
        Assert.Equal((32768L, 24576L), (sixteen.OffsetA, sixteen.OffsetB));

        Jp2CieLabParameters signed = Jp2CieLabParameters.Default(8, true, 8, true);
        Assert.Equal((0L, 0L), (signed.OffsetA, signed.OffsetB));
    }

    // ── reading: pclr and cmap ──────────────────────────────────────────────

    [Fact]
    public void TryRead_PaletteWithNoEntries_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Box("pclr", new byte[] { 0, 0, 3, 7, 7, 7 }), Mapping3), "declares 0 entries");
    }

    [Fact]
    public void TryRead_PaletteWithNoColumns_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Box("pclr", new byte[] { 0, 1, 0 }), Mapping3), "declares 0 columns");
    }

    [Fact]
    public void TryRead_PaletteWithMoreThan1024Entries_IsRefused()
    {
        var entries = new long[1025][];
        for (int i = 0; i < entries.Length; i++) entries[i] = new long[] { i & 0xFF };
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Pclr(new byte[] { 7 }, entries), H.Cmap((0, 1, 0))), "declares 1025 entries");
        // Control: 1024 entries read.
        Assert.Equal(1024, Read(Header(H.Ihdr(64, 64, 1, 7), H.Pclr(new byte[] { 7 }, entries[..1024]), H.Cmap((0, 1, 0)))).Palette.EntryCount);
    }

    [Fact]
    public void TryRead_TruncatedPalette_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Box("pclr", new byte[] { 0, 1 }), Mapping3), "NE and NPC need 3 bytes");
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Box("pclr", new byte[] { 0, 1, 3, 7, 7 }), Mapping3), "declares 3 columns but holds 2 depth bytes");
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Box("pclr", Palette3[8..^1]), Mapping3), "3 entries of 3 columns need 15 bytes, it holds 14");
        Assert.Equal(3, Read(Header(H.Ihdr(64, 64, 1, 7), Palette3, Mapping3)).Palette.EntryCount);
    }

    [Fact]
    public void TryRead_PaletteColumnDeeperThan38Bits_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), H.Pclr(new byte[] { 0x26 }, new long[] { 1 }), H.Cmap((0, 1, 0))), "column 0 declares 39-bit values");
        Assert.Equal(new[] { 38 }, Read(Header(H.Ihdr(64, 64, 1, 7), H.Pclr(new byte[] { 0x25 }, new long[] { 1 }), H.Cmap((0, 1, 0)))).Palette.ColumnDepths);
    }

    /// <summary>T.800 Table I.12: a value sits in the low bits of a field padded to whole bytes; a signed column's sign bit is the field's top bit.</summary>
    [Fact]
    public void TryRead_PaletteValues_AreTheLowBitsOfEachField_SignExtendedWhenSigned()
    {
        byte[] pclr = H.Pclr(new byte[] { 7, 9, 0x83 }, new long[] { 255, 1023, 0xF }, new long[] { 0, 0x3FF & 0x1234, 7 });
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 1, 7), pclr, H.Cmap((0, 1, 0), (0, 1, 1), (0, 1, 2))));

        Assert.Equal(new[] { 8, 10, 4 }, d.Palette.ColumnDepths);
        Assert.Equal(new[] { false, false, true }, d.Palette.ColumnSigned);
        Assert.Equal((255L, 1023L, -1L), (d.Palette.Value(0, 0), d.Palette.Value(0, 1), d.Palette.Value(0, 2)));
        Assert.Equal((0x234L, 7L), (d.Palette.Value(1, 1), d.Palette.Value(1, 2)));

        // Padding bits above the field are not part of the value: 0x0FFF in a 10-bit field reads 0x3FF.
        byte[] padded = H.Box("pclr", new byte[] { 0, 1, 1, 9, 0x0F, 0xFF });
        Assert.Equal(0x3FF, Read(Header(H.Ihdr(64, 64, 1, 7), padded, H.Cmap((0, 1, 0)))).Palette.Value(0, 0));
    }

    [Fact]
    public void TryRead_BytesAfterThePalette_AreADeviation()
    {
        byte[] longer = H.Box("pclr", H.Concat(Palette3[8..], new byte[] { 9, 9 }));
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 1, 7), longer, Mapping3));
        AssertDeviation(d.Deviations, "2 bytes follow the palette's last entry");
        Assert.Equal(21, d.Palette.Value(2, 2));
    }

    /// <summary>T.800 I.5.3.4 / I.5.3.5: a palette needs a mapping to apply it; a mapping without a palette has nothing to map through.</summary>
    [Fact]
    public void TryRead_PaletteWithoutMappingOrMappingWithoutPalette_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3), "Palette box without a Component Mapping box");
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Mapping3), "Component Mapping box without a Palette box");
        Assert.Equal(3, Read(Header(H.Ihdr(64, 64, 1, 7), Mapping3, Palette3)).Palette.EntryCount);   // either order
    }

    [Fact]
    public void TryRead_MappingNamingAMissingComponent_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Cmap((0, 1, 0), (1, 0, 0))), "cmap channel 1 takes component 1; the Image Header box declares 1");
        Assert.Equal(2, Read(Header(H.Ihdr(64, 64, 2, 7), Palette3, H.Cmap((0, 1, 0), (1, 0, 0)))).ComponentMapping.Length);
    }

    [Fact]
    public void TryRead_MappingNamingAMissingPaletteColumn_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Cmap((0, 1, 0), (0, 1, 3))), "cmap channel 1 takes palette column 3; the palette has 3");
        // A direct mapping's PCOL is not a column reference (Table I.15 has it 0; a stray value is ignored).
        Assert.Equal(2, Read(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Cmap((0, 1, 0), (0, 0, 3)))).ComponentMapping.Length);
    }

    [Fact]
    public void TryRead_MappingWithAReservedTypeOrNoChannels_IsRefused()
    {
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Cmap((0, 2, 0))), "mapping type 2; T.800 Table I.14");
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Box("cmap", new byte[0])), "4 bytes per channel, at least one");
        Refuses(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Box("cmap", new byte[6])), "4 bytes per channel");
        Assert.Single(Read(Header(H.Ihdr(64, 64, 1, 7), Palette3, H.Cmap((0, 1, 0)))).ComponentMapping);
    }

    // ── reading: cdef ───────────────────────────────────────────────────────

    [Fact]
    public void TryRead_DefinitionNamingAChannelPastTheCount_IsRefused()
    {
        // With a cmap the channels are its three outputs; without one, the ihdr's components.
        Refuses(Palettised(H.Cdef((3, 1, 0))), "names channel 3; the image has 3 channels");
        Refuses(Rgb(H.Cdef((3, 1, 0))), "names channel 3; the image has 3 channels");
        Assert.Single(Read(Rgb(H.Cdef((2, 1, 0)))).ChannelDefinitions);
        // Without an ihdr or cmap the count is unknown here and checked by the plan instead.
        Assert.Single(Read(Header(H.ColrEnumerated(16), H.Cdef((9, 1, 0)))).ChannelDefinitions);
    }

    [Fact]
    public void TryRead_TruncatedOrEmptyDefinitions_IsRefused()
    {
        Refuses(Rgb(H.Box("cdef", new byte[] { 0 })), "N needs 2 bytes");
        Refuses(Rgb(H.Box("cdef", new byte[] { 0, 0 })), "declares 0 descriptions");
        Refuses(Rgb(H.Box("cdef", new byte[] { 0, 2, 0, 0, 0, 0, 0, 1 })), "2 descriptions need 14 bytes, it holds 8");
        Refuses(Rgb(H.Box("cdef", new byte[] { 0, 1, 0, 0, 0, 0, 0 })), "1 descriptions need 8 bytes, it holds 7");   // one byte short
        Assert.Single(Read(Rgb(H.Box("cdef", new byte[] { 0, 1, 0, 0, 0, 0, 0, 1 }))).ChannelDefinitions);
    }

    [Fact]
    public void TryRead_DefinitionWithAReservedType_IsRefused()
    {
        Refuses(Rgb(H.Cdef((0, 3, 1))), "type 3; T.800 Table I.16 defines");
        Assert.Equal(0xFFFF, Read(Rgb(H.Cdef((0, 0xFFFF, 0xFFFF)))).ChannelDefinitions[0].Type);
    }

    // ── reading: the walk ───────────────────────────────────────────────────

    [Fact]
    public void TryRead_ChildBoxRunningPastTheHeader_IsRefused()
    {
        byte[] overrunning = { 0, 0, 0, 100, (byte)'c', (byte)'o', (byte)'l', (byte)'r', 1, 0, 0, 0, 0, 0, 16 };
        byte[] tail = H.Box("jp2c", new byte[20]);
        Refuses(H.Container(H.Jp2h(H.Ihdr(64, 64, 3, 7), overrunning), tail), "runs past the header's end");
        Refuses(H.Container(H.Jp2h(H.Ihdr(64, 64, 3, 7), new byte[] { 0, 0, 0, 3, (byte)'c', (byte)'o', (byte)'l', (byte)'r' }), tail), "truncated");
        // Control: the same box with its true length reads.
        overrunning[3] = 15;
        Assert.Equal(16, Read(H.Container(H.Jp2h(H.Ihdr(64, 64, 3, 7), overrunning), tail)).ColourSpecifications[0].EnumeratedSpace);
    }

    [Fact]
    public void TryRead_MoreThan64HeaderBoxes_IsRefused_Bounded()
    {
        var boxes = new List<byte[]> { H.Ihdr(64, 64, 3, 7) };
        for (int i = 0; i < 63; i++) boxes.Add(H.Box("free", new byte[0]));
        Assert.True(Read(Header(boxes.ToArray())).HasImageHeader);   // exactly 64

        boxes.Add(H.Box("free", new byte[0]));
        Refuses(Header(boxes.ToArray()), "more than 64 boxes");
    }

    [Fact]
    public void TryRead_OpacityBox_IsReadFromTheFirstCompositingLayer()
    {
        Assert.Equal(0, Read(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(H.Box("lbl ", new byte[3]), H.Opct(0)))).OpacityBoxType);
        Assert.Equal(2, Read(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(H.Box("opct", new byte[] { 2, 3, 0, 0, 0 })))).OpacityBoxType);
        Assert.Equal(-1, Read(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(H.Box("lbl ", new byte[3])))).OpacityBoxType);
    }

    [Fact]
    public void TryRead_OpacityBoxWithAReservedTypeOrNoType_IsRefused()
    {
        Refuses(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(H.Opct(3))), "declares type 3; T.801 Table M.33");
        Refuses(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(H.Box("opct", new byte[0]))), "Opacity box is empty");
        Refuses(H.Container(H.Jp2h(H.ColrEnumerated(16)), H.Jplh(new byte[] { 0, 0, 0, 3, 1, 1, 1, 1 })), "inside the Compositing Layer Header box");
    }

    // ── ranking, and the public colour-channel count ────────────────────────

    /// <summary>ISO 32000-2 §7.4.9: "the one with the highest precedence and best approximation value"; T.800 I.5.3.3: a JP2 reader takes the first.</summary>
    [Fact]
    public void RankedColourSpecifications_HighestPrecedenceThenBestApproximationThenFileOrder()
    {
        Jp2ColourDescription d = Read(Header(
            H.ColrEnumerated(16, precedence: 0, approximation: 2),   // 0
            H.ColrEnumerated(17, precedence: 1, approximation: 3),   // 1
            H.ColrEnumerated(18, precedence: 1, approximation: 1),   // 2
            H.ColrEnumerated(12, precedence: 1, approximation: 1),   // 3: ties with 2, later in the file
            H.ColrEnumerated(20, precedence: -1, approximation: 1),  // 4: PREC is signed
            H.ColrEnumerated(21, precedence: 1, approximation: 0))); // 5: APPROX 0 (JP2) ranks with 1, after 2 and 3

        Jp2ColourSpecification[] ranked = d.RankedColourSpecifications();
        Assert.Equal(new[] { 2, 3, 5, 1, 0, 4 }, Array.ConvertAll(ranked, s => s.Order));
        // The file's own list is untouched.
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Array.ConvertAll(d.ColourSpecifications, s => s.Order));
    }

    [Fact]
    public void ColourChannelCount_PrefersTheRankedSpecification_ThenTheDefinitions()
    {
        byte[] unknownMethod = H.Box("colr", new byte[] { 4, 0, 1, 9 });
        byte[] cdef = H.Cdef((0, 0, 1), (1, 0, 2), (2, 1, 0));

        Assert.Equal(2, Read(Header(unknownMethod, cdef)).ColourChannelCount());
        Assert.Equal(3, Read(Header(unknownMethod, H.ColrEnumerated(16, precedence: -5), cdef)).ColourChannelCount());
        Assert.Equal(0, Read(Header(unknownMethod)).ColourChannelCount());
        // A channel described twice counts once.
        Assert.Equal(1, Read(Header(H.Cdef((0, 0, 1), (0, 0, 2)))).ColourChannelCount());
    }

    [Fact]
    public void JpxHeader_TryReadColourComponents_AnswersFromTheRankedSpecification()
    {
        Assert.True(JpxHeader.TryReadColourComponents(Header(H.ColrEnumerated(17, precedence: 0), H.ColrEnumerated(16, precedence: 1)), out int preferRgb));
        Assert.Equal(3, preferRgb);
        Assert.True(JpxHeader.TryReadColourComponents(Header(H.ColrEnumerated(17, precedence: 1), H.ColrEnumerated(16, precedence: 0)), out int preferGrey));
        Assert.Equal(1, preferGrey);
        // A refused header does not say.
        Assert.False(JpxHeader.TryReadColourComponents(Header(H.ColrEnumerated(16), H.Ihdr(64, 64, 1, 7), Palette3), out int refused));
        Assert.Equal(0, refused);
    }

    // ── the plan: the colour space (ISO 32000-2 §7.4.9) ─────────────────────

    /// <summary>§7.4.9: a /ColorSpace "shall determine how the image samples are interpreted, and the colour space specifications in the JPEG 2000 data shall be ignored".</summary>
    [Fact]
    public void Plan_ColorSpaceInTheDictionary_OverridesTheFilesSpecification()
    {
        Jp2ColourDescription cmyk = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.Cmyk)));

        Jp2PdfChannelPlan dict = Plan(cmyk, Comps(8, 8, 8, 8), WithColourSpace(4));
        Assert.Equal(Jp2ColourSpaceKind.ImageDictionary, dict.ColourSpace);
        Assert.Null(dict.ColourSpecification);
        Assert.Equal(new[] { 0, 1, 2, 3 }, dict.ColourChannels);
        Assert.True(dict.DecodeArrayApplies);

        // Control: without the entry the file's CMYK is used and /Decode is ignored.
        Jp2PdfChannelPlan file = Plan(cmyk, Comps(8, 8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.Cmyk, file.ColourSpace);
        Assert.Equal(0, file.ColourSpecification.Order);
        Assert.False(file.DecodeArrayApplies);
    }

    /// <summary>§7.4.9: "the number of ordinary colour channels in the JPEG 2000 data shall match the number of components in the colour space".</summary>
    [Fact]
    public void Plan_ColorSpaceComponentCount_MustBeSuppliedByTheOrdinaryChannels()
    {
        Jp2ColourDescription rgb = Read(Rgb());
        Assert.Contains("has 3 ordinary colour channel(s); the image dictionary's 4-component colour space needs 4",
            PlanRefusal(rgb, Comps(8, 8, 8), WithColourSpace(4)), StringComparison.Ordinal);

        // Fewer components than channels: the leading ones are used, the rest reported unused —
        // what an RGB /ColorSpace on a four-channel file without a cdef means here.
        Jp2PdfChannelPlan fewer = Plan(Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16))), Comps(8, 8, 8, 8), WithColourSpace(3));
        Assert.Equal(new[] { 0, 1, 2 }, fewer.ColourChannels);
        Assert.Equal(-1, fewer.OpacityChannel);
        AssertDeviation(fewer.Deviations, "channel(s) 3 lie beyond");
    }

    /// <summary>§7.4.9: "attempt to use the one with the highest precedence and best approximation value".</summary>
    [Fact]
    public void Plan_NoColorSpace_UsesTheHighestPrecedenceSpecification()
    {
        byte[] icc = H.ColrIcc(H.IccProfile("RGB "), precedence: 0, approximation: 1);
        byte[] romm = H.ColrEnumerated(Jp2EnumeratedColourSpaces.RommRgb, precedence: 1, approximation: 1);

        Jp2PdfChannelPlan plan = Plan(Read(Header(H.Ihdr(64, 64, 3, 7), icc, romm)), Comps(8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.RommRgb, plan.ColourSpace);
        Assert.Equal(1, plan.ColourSpecification.Order);
        Assert.Empty(plan.Deviations);

        byte[] iccFirst = H.ColrIcc(H.IccProfile("RGB "), precedence: 2, approximation: 1);
        Jp2PdfChannelPlan other = Plan(Read(Header(H.Ihdr(64, 64, 3, 7), iccFirst, romm)), Comps(8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.IccProfile, other.ColourSpace);
        Assert.Equal("RGB ", other.ColourSpecification.IccDataColourSpace);
    }

    /// <summary>§7.4.9: "If the colour space is given by an unsupported ICC profile, the next lower colour space ... shall be used."</summary>
    [Fact]
    public void Plan_UnsupportedIccProfile_FallsToTheNextSpecification()
    {
        byte[] profile = H.IccProfile("RGB ");
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 7),
            H.ColrIcc(profile, precedence: 1), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, precedence: 0)));

        byte[]? asked = null;
        Jp2PdfChannelPlan rejected = Plan(d, Comps(8, 8, 8), NoDictionary, p => { asked = p; return false; });
        Assert.Equal(profile, asked);
        Assert.Equal(Jp2ColourSpaceKind.SRgb, rejected.ColourSpace);
        AssertDeviation(rejected.Deviations, "does not support its ICC profile");

        Assert.Equal(Jp2ColourSpaceKind.IccProfile, Plan(d, Comps(8, 8, 8), NoDictionary, _ => true).ColourSpace);
        Assert.Equal(Jp2ColourSpaceKind.IccProfile, Plan(d, Comps(8, 8, 8), NoDictionary).ColourSpace);

        // Nothing left after the profile: the device fallback.
        Jp2ColourDescription only = Read(Header(H.Ihdr(64, 64, 3, 7), H.ColrIcc(profile)));
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, Plan(only, Comps(8, 8, 8), NoDictionary, _ => false).ColourSpace);
    }

    /// <summary>§7.4.9: "If no supported colour space is found, the colour space used shall be DeviceGray, DeviceRGB, or DeviceCMYK, depending on whether the number of ordinary channels ... is 1, 3, or 4."</summary>
    [Fact]
    public void Plan_NoSupportedSpecification_FallsToADeviceSpaceByChannelCount()
    {
        Jp2ColourDescription d = Read(Header(H.ColrEnumerated(9999)));

        Jp2PdfChannelPlan grey = Plan(d, Comps(8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.DeviceGray, grey.ColourSpace);
        Assert.Null(grey.ColourSpecification);
        AssertDeviation(grey.Deviations, "EnumCS 9999 is not a colour space a PDF may use");
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, Plan(d, Comps(8, 8, 8), NoDictionary).ColourSpace);
        Assert.Equal(Jp2ColourSpaceKind.DeviceCmyk, Plan(d, Comps(8, 8, 8, 8), NoDictionary).ColourSpace);

        Assert.Contains("2 ordinary channel(s) fit neither DeviceGray, DeviceRGB nor DeviceCMYK",
            PlanRefusal(d, Comps(8, 8), NoDictionary), StringComparison.Ordinal);
        Assert.Contains("5 ordinary channel(s)", PlanRefusal(d, Comps(8, 8, 8, 8, 8), NoDictionary), StringComparison.Ordinal);
    }

    /// <summary>§7.4.9: "the baseline JPX colour spaces excluding enumerated colour space 19 (CIEJab)".</summary>
    [Fact]
    public void Plan_CieJab_IsPassedOver()
    {
        Jp2PdfChannelPlan plan = Plan(Read(Header(H.Ihdr(64, 64, 3, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieJab))), Comps(8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, plan.ColourSpace);
        AssertDeviation(plan.Deviations, "CIEJab (EnumCS 19) is excluded by ISO 32000-2 §7.4.9");
        // Control: CIELab (14), its neighbour in the baseline, is used.
        Assert.Equal(Jp2ColourSpaceKind.CieLab, Plan(Read(Header(H.Ihdr(64, 64, 3, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.CieLab))), Comps(8, 8, 8), NoDictionary).ColourSpace);
    }

    [Fact]
    public void Plan_SpecificationNeedingMoreChannelsThanTheDataHas_IsPassedOver()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 7),
            H.ColrEnumerated(Jp2EnumeratedColourSpaces.Cmyk, precedence: 1), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb, precedence: 0)));
        Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.SRgb, plan.ColourSpace);
        AssertDeviation(plan.Deviations, "needs 4 colour channels and the JPEG 2000 data has 3");
    }

    // ── the plan: opacity (ISO 32000-2 §7.4.9, Table 87) ────────────────────

    /// <summary>Table 87: /SMaskInData 0 — "encoded soft-mask image information shall be ignored".</summary>
    [Fact]
    public void Plan_SMaskInDataZero_IgnoresTheDeclaredOpacityChannel()
    {
        Jp2PdfChannelPlan plan = Plan(Read(Rgba()), Comps(8, 8, 8, 8), NoDictionary);

        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(3, plan.OpacityChannel);   // named, so a consumer leaves it out on purpose
        Assert.False(plan.OpacityApplies);
        Assert.False(plan.OpacityIsPremultiplied);
        Assert.Equal(Jp2ChannelDefinition.OpacityType, plan.Channels[3].Type);
        Assert.Empty(plan.Deviations);
    }

    /// <summary>Table 87: 1 is a soft mask over straight colour, 2 over premultiplied colour.</summary>
    [Fact]
    public void Plan_SMaskInDataOneOrTwo_UsesTheWholeImageOpacityChannel_TwoMeansPremultiplied()
    {
        Jp2ColourDescription straight = Read(Rgba());
        Jp2PdfChannelPlan one = Plan(straight, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal((3, true, false), (one.OpacityChannel, one.OpacityApplies, one.OpacityIsPremultiplied));
        Assert.Empty(one.Deviations);

        Jp2PdfChannelPlan two = Plan(straight, Comps(8, 8, 8, 8), WithSMask(2));
        Assert.Equal((3, true, true), (two.OpacityChannel, two.OpacityApplies, two.OpacityIsPremultiplied));
        AssertDeviation(two.Deviations, "the image dictionary's statement is followed");

        // A file that says premultiplied agrees with 2 and disagrees with 1.
        Jp2ColourDescription premultiplied = Read(Rgba(Jp2ChannelDefinition.PremultipliedOpacityType));
        Assert.Empty(Plan(premultiplied, Comps(8, 8, 8, 8), WithSMask(2)).Deviations);
        AssertDeviation(Plan(premultiplied, Comps(8, 8, 8, 8), WithSMask(1)).Deviations, "as premultiplied opacity and /SMaskInData 1 says straight");
    }

    /// <summary>§7.4.9: "there shall be only one opacity channel in the JPEG 2000 data and it shall apply to all colour channels".</summary>
    [Fact]
    public void Plan_SMaskInData_WithPerColourOrSeveralOpacityChannels_IsRefused()
    {
        Jp2ColourDescription perColour = Read(Rgba(association: 1));
        Assert.Contains("requires exactly one opacity channel applying to all colour channels", PlanRefusal(perColour, Comps(8, 8, 8, 8), WithSMask(1)), StringComparison.Ordinal);

        Jp2ColourDescription two = Read(Header(H.Ihdr(64, 64, 5, 7), H.ColrEnumerated(16),
            H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 0), (4, 1, 0))));
        Assert.Contains("the JPEG 2000 data has 2, associated with channel 3: 0, channel 4: 0", PlanRefusal(two, Comps(8, 8, 8, 8, 8), WithSMask(2)), StringComparison.Ordinal);

        // Controls: with /SMaskInData 0 both files plan, and neither names an opacity channel.
        Assert.Equal(-1, Plan(perColour, Comps(8, 8, 8, 8), NoDictionary).OpacityChannel);
        Assert.Equal(-1, Plan(two, Comps(8, 8, 8, 8, 8), NoDictionary).OpacityChannel);
    }

    /// <summary>
    /// A four-channel sRGB file with no cdef declares no opacity channel at all (non-conforming: T.800
    /// I.5.3.6 requires one for auxiliary channels). /SMaskInData says there is one, and the only
    /// channel it can be is the one past the colour space's three — the reading GAPS.md §2 recorded.
    /// </summary>
    [Fact]
    public void Plan_SMaskInData_WithoutADeclaredOpacityChannel_TakesTheChannelPastTheColourSpace()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16)));

        Jp2PdfChannelPlan announced = Plan(d, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal((3, true), (announced.OpacityChannel, announced.OpacityApplies));
        Assert.Equal(new[] { 0, 1, 2 }, announced.ColourChannels);
        AssertDeviation(announced.Deviations, "the JP2 header declares no opacity channel; channel 3");

        Jp2PdfChannelPlan silent = Plan(d, Comps(8, 8, 8, 8), NoDictionary);
        Assert.Equal(-1, silent.OpacityChannel);
        AssertDeviation(silent.Deviations, "channel(s) 3 lie beyond");
    }

    /// <summary>
    /// Announced with nothing to apply to: opaque, with a deviation — the rule the plugin path's
    /// <c>ImageSampleLayout</c> applies to the same file, so the two decoders agree.
    /// </summary>
    [Fact]
    public void Plan_SMaskInData_WithNoSpareChannel_IsIgnoredWithADeviation()
    {
        Jp2PdfChannelPlan plan = Plan(Read(Rgb()), Comps(8, 8, 8), WithSMask(1));
        Assert.Equal((false, -1, 3), (plan.OpacityApplies, plan.OpacityChannel, plan.ColourChannels.Length));
        AssertDeviation(plan.Deviations, "declares none and has no channel beyond");
        AssertDeviation(plan.Deviations, "the image is opaque");
        // The control: the same file with the key at 0 plans without the deviation.
        Assert.Empty(Plan(Read(Rgb()), Comps(8, 8, 8), NoDictionary).Deviations);
    }

    /// <summary>The device fallback counts the announced opacity channel out before choosing 1, 3 or 4.</summary>
    [Fact]
    public void Plan_DeviceFallback_CountsOutTheAnnouncedOpacityChannel()
    {
        Jp2ColourDescription bare = Jp2ColourDescription.ForRawCodestream();

        Jp2PdfChannelPlan rgba = Plan(bare, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal((Jp2ColourSpaceKind.DeviceRgb, 3, true), (rgba.ColourSpace, rgba.OpacityChannel, rgba.OpacityApplies));

        Jp2PdfChannelPlan greyAlpha = Plan(bare, Comps(8, 8), WithSMask(2));
        Assert.Equal((Jp2ColourSpaceKind.DeviceGray, 1, true), (greyAlpha.ColourSpace, greyAlpha.OpacityChannel, greyAlpha.OpacityIsPremultiplied));

        Assert.Contains("fit neither", PlanRefusal(bare, Comps(8, 8), NoDictionary), StringComparison.Ordinal);
        // Four channels with nothing announced stay CMYK.
        Assert.Equal(Jp2ColourSpaceKind.DeviceCmyk, Plan(bare, Comps(8, 8, 8, 8), NoDictionary).ColourSpace);
    }

    [Fact]
    public void Plan_SMaskInDataOutOfRange_IsTreatedAsZero()
    {
        Jp2PdfChannelPlan plan = Plan(Read(Rgba()), Comps(8, 8, 8, 8), WithSMask(5));
        Assert.False(plan.OpacityApplies);
        AssertDeviation(plan.Deviations, "/SMaskInData 5 is not 0, 1 or 2");
    }

    // ── the plan: channel order and types (T.800 I.5.3.6) ───────────────────

    /// <summary>T.800 Table I.18: Asoc numbers the colour, so a cdef can put Cr first — conformance file2 does.</summary>
    [Fact]
    public void Plan_DefinitionsReorderTheColourChannels()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 3, 7), H.ColrEnumerated(Jp2EnumeratedColourSpaces.SYcc),
            H.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1))));
        Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8), NoDictionary);

        Assert.Equal(new[] { 2, 1, 0 }, plan.ColourChannels);
        Assert.Equal(Jp2ColourSpaceKind.SYcc, plan.ColourSpace);
        Assert.Equal(3, plan.Channels[0].Association);
    }

    [Fact]
    public void Plan_ColourChannelsNoneNumbered_KeepChannelOrder()
    {
        // Every colour channel associated with "the whole image": no order stated, so channel order.
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16),
            H.Cdef((0, 0, 0), (1, 0, 0), (2, 0, 0), (3, 1, 0))));
        Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(3, plan.OpacityChannel);
    }

    [Fact]
    public void Plan_ColourChannelsPartlyNumbered_IsRefused()
    {
        Jp2ColourDescription d = Read(Rgb(H.Cdef((0, 0, 1), (1, 0, 0), (2, 0, 3))));
        Assert.Contains("some colour channels carry a colour association and others none", PlanRefusal(d, Comps(8, 8, 8), NoDictionary), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_NonContiguousOrDuplicateColourAssociations_IsRefused()
    {
        Assert.Contains("numbers an n-colour space's colours 1 to n",
            PlanRefusal(Read(Rgb(H.Cdef((0, 0, 2), (1, 0, 3), (2, 0, 4)))), Comps(8, 8, 8), NoDictionary), StringComparison.Ordinal);
        Assert.Contains("channel 0: 1, channel 1: 1, channel 2: 3",
            PlanRefusal(Read(Rgb(H.Cdef((0, 0, 1), (1, 0, 1), (2, 0, 3)))), Comps(8, 8, 8), NoDictionary), StringComparison.Ordinal);
        // Control: 1, 2, 3 in any file order.
        Assert.Equal(new[] { 1, 2, 0 }, Plan(Read(Rgb(H.Cdef((0, 0, 3), (1, 0, 1), (2, 0, 2)))), Comps(8, 8, 8), NoDictionary).ColourChannels);
    }

    /// <summary>A channel the cdef does not describe keeps I.5.3.6's default: colour, in channel order.</summary>
    [Fact]
    public void Plan_UndescribedChannelsKeepTheDefault()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16), H.Cdef((3, 1, 0))));
        Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(3, plan.OpacityChannel);
        Assert.Equal(2, plan.Channels[1].Association);
    }

    [Fact]
    public void Plan_ChannelDescribedTwiceAsColour_IsRefused_OpacityForSeveralColoursIsPerColour()
    {
        Assert.Contains("describes channel 0 twice with different meanings",
            PlanRefusal(Read(Rgb(H.Cdef((0, 0, 1), (0, 0, 2), (1, 0, 2), (2, 0, 3)))), Comps(8, 8, 8), NoDictionary), StringComparison.Ordinal);

        // I.5.3.6's own example: one opacity channel for the red and green colours.
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16), H.Cdef((3, 1, 1), (3, 1, 2))));
        Assert.Contains("applying to all colour channels", PlanRefusal(d, Comps(8, 8, 8, 8), WithSMask(1)), StringComparison.Ordinal);
        Assert.Equal(-1, Plan(d, Comps(8, 8, 8, 8), NoDictionary).OpacityChannel);
    }

    [Fact]
    public void Plan_UnspecifiedChannel_IsDropped()
    {
        Jp2ColourDescription d = Read(Header(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16), H.Cdef((3, 0xFFFF, 0xFFFF))));
        Jp2PdfChannelPlan plan = Plan(d, Comps(8, 8, 8, 8), NoDictionary);
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(-1, plan.OpacityChannel);
        AssertDeviation(plan.Deviations, "channel 3 has no specified type");
        // With /SMaskInData 1 there is no spare channel: the unspecified one is not colour, so
        // nothing is opacity and the image is opaque.
        Jp2PdfChannelPlan announced = Plan(d, Comps(8, 8, 8, 8), WithSMask(1));
        Assert.Equal((false, -1), (announced.OpacityApplies, announced.OpacityChannel));
        AssertDeviation(announced.Deviations, "declares none and has no channel beyond");
    }

    [Fact]
    public void Plan_DefinitionNamingAChannelPastTheCodestream_IsRefused()
    {
        // No ihdr, so the count is first known here.
        Jp2ColourDescription d = Read(Header(H.ColrEnumerated(16), H.Cdef((3, 1, 0))));
        Assert.Contains("names channel 3; the image has 3 channels", PlanRefusal(d, Comps(8, 8, 8), NoDictionary), StringComparison.Ordinal);
        Assert.Equal(3, Plan(d, Comps(8, 8, 8, 8), WithSMask(1)).OpacityChannel);
    }

    // ── the plan: the palette (T.800 I.5.3.4, I.5.3.5) and Indexed ──────────

    /// <summary>T.800 I.5.3.4: the palette creates channels from one component; each takes its column's depth (I.5.3.3).</summary>
    [Fact]
    public void Plan_PaletteExpandsOneComponentToThree()
    {
        // A 4-bit index component (ihdr BPC 3) under 8-bit palette columns.
        byte[] jp2 = Header(H.Ihdr(64, 64, 1, 3), Palette3, Mapping3, H.ColrEnumerated(Jp2EnumeratedColourSpaces.SRgb));
        Jp2PdfChannelPlan plan = Plan(Read(jp2), Comps(4), NoDictionary);

        Assert.True(plan.PaletteApplied);
        Assert.Equal(3, plan.ColourComponentCount);
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(new[] { 8, 8, 8 }, plan.ColourChannelDepths);   // the columns', not the 4-bit index's
        Assert.Equal((0, 1), (plan.Channels[1].Component, plan.Channels[1].PaletteColumn));
        Assert.Equal(Jp2ColourSpaceKind.SRgb, plan.ColourSpace);
        Assert.Empty(plan.Deviations);
    }

    /// <summary>§7.4.9: an Indexed /ColorSpace is "the equivalent of an Indexed colour space in PDF syntax" — it reads the index samples, and the file's palette is not applied first.</summary>
    [Fact]
    public void Plan_IndexedColorSpace_BypassesThePalette()
    {
        Jp2ColourDescription d = Read(Palettised(H.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3))));

        Jp2PdfChannelPlan indexed = Plan(d, Comps(8), WithColourSpace(1, indexed: true));
        Assert.False(indexed.PaletteApplied);
        Assert.Equal(1, indexed.ColourComponentCount);
        Assert.Equal((0, -1, 8), (indexed.Channels[0].Component, indexed.Channels[0].PaletteColumn, indexed.Channels[0].Depth));
        Assert.Equal(Jp2ColourSpaceKind.ImageDictionary, indexed.ColourSpace);
        AssertDeviation(indexed.Deviations, "Indexed colour space bypasses");

        // Controls: a non-Indexed three-component space sees the palette's output; a raw file has nothing to bypass.
        Jp2PdfChannelPlan rgb = Plan(d, Comps(8), WithColourSpace(3));
        Assert.True(rgb.PaletteApplied);
        Assert.Equal(3, rgb.ColourComponentCount);
        Assert.Equal(1, Plan(Jp2ColourDescription.ForRawCodestream(), Comps(8), WithColourSpace(1, indexed: true)).ColourComponentCount);
    }

    [Fact]
    public void Plan_IndexedColorSpaceWithSeveralComponents_IsRefused()
    {
        Assert.Contains("needs one component; the codestream has 2",
            PlanRefusal(Read(Palettised()), Comps(8, 8), WithColourSpace(1, indexed: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_MappingNamingAComponentPastTheCodestream_IsRefused()
    {
        // No ihdr: the read cannot check the component, the plan can.
        Jp2ColourDescription d = Read(Header(Palette3, H.Cmap((0, 1, 0), (1, 1, 1), (0, 1, 2)), H.ColrEnumerated(16)));
        Assert.Contains("cmap channel 1 takes component 1; the codestream has 1", PlanRefusal(d, Comps(8), NoDictionary), StringComparison.Ordinal);
        Assert.Equal(3, Plan(d, Comps(8, 8), NoDictionary).ColourComponentCount);
    }

    // ── the plan: image masks, /Decode, the codestream's authority ──────────

    /// <summary>§7.4.9: "If ImageMask is true, the JPEG 2000 data shall provide a single colour channel with 1-bit samples."</summary>
    [Fact]
    public void Plan_ImageMask_RequiresOneOneBitChannel()
    {
        Jp2ColourDescription grey = Read(Header(H.Ihdr(64, 64, 1, 0), H.ColrEnumerated(Jp2EnumeratedColourSpaces.Greyscale)));

        Jp2PdfChannelPlan mask = Plan(grey, Comps(1), AsImageMask);
        Assert.Equal(Jp2ColourSpaceKind.StencilMask, mask.ColourSpace);
        Assert.Equal(new[] { 0 }, mask.ColourChannels);
        Assert.True(mask.DecodeArrayApplies);

        Assert.Contains("needs 1-bit samples; channel 0 has 8 bits", PlanRefusal(grey, Comps(8), AsImageMask), StringComparison.Ordinal);
        Assert.Contains("needs a single colour channel; the JPEG 2000 data has 2", PlanRefusal(grey, Comps(1, 1), AsImageMask), StringComparison.Ordinal);
    }

    /// <summary>§7.4.9: "If ColorSpace is absent, then the Decode array shall be ignored unless ImageMask is true."</summary>
    [Fact]
    public void Plan_DecodeArray_AppliesOnlyWithAColorSpaceOrAnImageMask()
    {
        Jp2ColourDescription rgb = Read(Rgb());
        Assert.False(Plan(rgb, Comps(8, 8, 8), NoDictionary).DecodeArrayApplies);
        Assert.False(Plan(Read(Rgba()), Comps(8, 8, 8, 8), WithSMask(1)).DecodeArrayApplies);   // an opacity channel changes nothing
        Assert.True(Plan(rgb, Comps(8, 8, 8), WithColourSpace(3)).DecodeArrayApplies);
        Assert.True(Plan(Read(Header(H.Ihdr(64, 64, 1, 0), H.ColrEnumerated(17))), Comps(1), AsImageMask).DecodeArrayApplies);
    }

    /// <summary>T.800 I.5.3.1: readers "may choose to attempt to read these files by using the values found within the codestream".</summary>
    [Fact]
    public void Plan_CodestreamValuesWinOverTheHeader()
    {
        Jp2PdfChannelPlan plan = Plan(Read(Rgb()), Comps(12, 12, 12, 12), WithSMask(1));

        Assert.Equal(4, plan.Channels.Length);
        Assert.Equal(new[] { 12, 12, 12 }, plan.ColourChannelDepths);
        Assert.Equal(3, plan.OpacityChannel);
        AssertDeviation(plan.Deviations, "declares 3 components and the codestream 4");
        AssertDeviation(plan.Deviations, "component 0 8 bits and the codestream 12");
    }

    [Fact]
    public void Plan_EmptyComponentList_IsRefused()
    {
        Assert.False(Read(Rgb()).TryPlanForPdf(Array.Empty<JpxComponentSize>(), NoDictionary, null, out _, out string reason));
        Assert.Contains("declares no components", reason, StringComparison.Ordinal);
    }

    // ── the plan: the Opacity box (T.801 M.11.7.6) and a raw codestream ─────

    /// <summary>T.801 Table M.33: Otyp 0 or 1 makes the last channel (premultiplied) opacity and the rest colour; M.11.7 has it displace the cdef.</summary>
    [Fact]
    public void Plan_OpacityBox_MakesTheLastChannelOpacity_AndDisplacesTheDefinitions()
    {
        byte[] jp2 = H.Container(
            H.Jp2h(H.Ihdr(64, 64, 4, 7), H.ColrEnumerated(16), H.Cdef((0, 0, 3), (1, 0, 2), (2, 0, 1), (3, 1, 0))),
            H.Jplh(H.Opct(1)));
        Jp2PdfChannelPlan plan = Plan(Read(jp2), Comps(8, 8, 8, 8), WithSMask(2));

        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);   // not the cdef's reversal
        Assert.Equal((3, true, true), (plan.OpacityChannel, plan.OpacityApplies, plan.OpacityIsPremultiplied));
        Assert.Equal(Jp2ChannelDefinition.PremultipliedOpacityType, plan.Channels[3].Type);
        AssertDeviation(plan.Deviations, "displaces the Channel Definition box");

        Jp2PdfChannelPlan straight = Plan(Read(H.Container(H.Jp2h(H.Ihdr(64, 64, 2, 7), H.ColrEnumerated(17)), H.Jplh(H.Opct(0)))), Comps(8, 8), WithSMask(1));
        Assert.Equal((1, Jp2ChannelDefinition.OpacityType), (straight.OpacityChannel, straight.Channels[1].Type));
        Assert.Empty(straight.Deviations);
    }

    /// <summary>§7.4.9: "For JPX files used in PDF files, the chroma-key transparency shall be ignored."</summary>
    [Fact]
    public void Plan_ChromaKeyOpacityBox_IsIgnored()
    {
        byte[] jp2 = H.Container(H.Jp2h(H.Ihdr(64, 64, 3, 7), H.ColrEnumerated(16)), H.Jplh(H.Box("opct", new byte[] { 2, 3, 0, 0, 0 })));
        Jp2PdfChannelPlan plan = Plan(Read(jp2), Comps(8, 8, 8), NoDictionary);
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Equal(-1, plan.OpacityChannel);
        AssertDeviation(plan.Deviations, "chroma key");
    }

    [Fact]
    public void Plan_RawCodestream_PlansByChannelCountAlone()
    {
        Jp2ColourDescription bare = Jp2ColourDescription.ForRawCodestream();
        Assert.Empty(bare.Deviations);
        Assert.Empty(bare.ColourSpecifications);

        Jp2PdfChannelPlan plan = Plan(bare, Comps(8, 8, 8), NoDictionary);
        Assert.Equal(Jp2ColourSpaceKind.DeviceRgb, plan.ColourSpace);
        Assert.Equal(new[] { 0, 1, 2 }, plan.ColourChannels);
        Assert.Empty(plan.Deviations);
        Assert.Equal(Jp2ColourSpaceKind.ImageDictionary, Plan(bare, Comps(8, 8, 8, 8), WithColourSpace(4)).ColourSpace);
    }

    // ── the corpus ──────────────────────────────────────────────────────────

    private static JpxComponentSize[] SizComponents(byte[] data)
    {
        Assert.True(JpxHeader.TryLocateCodestream(data, out long start, out long end));
        Assert.True(JpxImageSize.TryRead(data, start + 4, end, out JpxImageSize siz, out string reason), reason);
        return siz.Components;
    }

    /// <summary>
    /// The nine ISO/IEC 15444-4 conformance JP2 files, as the reference decoder's test data holds them. What each
    /// header carries was read box by box and checked against an independent tool 12.2 (the reference decoder) opening the
    /// same files: file2's cdef reverses the sYCC channels (an independent tool still opens it as RGB, having
    /// applied the reorder), file9 is one index component under a 256-entry RGB palette (an independent tool opens
    /// it as mode P and its palette's third entry is (23, 12, 21)), file5 and file7 carry an ICC
    /// profile at precedence 0 and an enumerated space at precedence 1, file8 a greyscale ICC profile
    /// alone, file6 twelve-bit grey. No conformance file has an opacity channel.
    /// </summary>
    [JpxCorpusFact]
    public void Conformance_Jp2Files_HeadersAndPlansAreAsDocumented()
    {
        string dir = Path.Combine(TestCorpus.JpxRoot!, "input", "conformance");
        var expected = new Dictionary<string, (Jp2ColourSpaceKind kind, int[] colour, int[] depths, int colrBoxes, bool cdef, bool palette, int publicCount)>
        {
            ["file1.jp2"] = (Jp2ColourSpaceKind.SRgb, new[] { 0, 1, 2 }, new[] { 8, 8, 8 }, 1, false, false, 3),
            ["file2.jp2"] = (Jp2ColourSpaceKind.SYcc, new[] { 2, 1, 0 }, new[] { 8, 8, 8 }, 1, true, false, 3),
            ["file3.jp2"] = (Jp2ColourSpaceKind.SYcc, new[] { 0, 1, 2 }, new[] { 8, 8, 8 }, 1, false, false, 3),
            ["file4.jp2"] = (Jp2ColourSpaceKind.Greyscale, new[] { 0 }, new[] { 8 }, 1, false, false, 1),
            ["file5.jp2"] = (Jp2ColourSpaceKind.RommRgb, new[] { 0, 1, 2 }, new[] { 8, 8, 8 }, 2, false, false, 3),
            ["file6.jp2"] = (Jp2ColourSpaceKind.Greyscale, new[] { 0 }, new[] { 12 }, 1, false, false, 1),
            ["file7.jp2"] = (Jp2ColourSpaceKind.ESRgb, new[] { 0, 1, 2 }, new[] { 16, 16, 16 }, 2, false, false, 3),
            ["file8.jp2"] = (Jp2ColourSpaceKind.IccProfile, new[] { 0 }, new[] { 8 }, 1, false, false, 1),
            ["file9.jp2"] = (Jp2ColourSpaceKind.SRgb, new[] { 0, 1, 2 }, new[] { 8, 8, 8 }, 1, false, true, 3),
        };

        var failures = new List<string>();
        foreach (KeyValuePair<string, (Jp2ColourSpaceKind kind, int[] colour, int[] depths, int colrBoxes, bool cdef, bool palette, int publicCount)> e in expected)
        {
            string path = Path.Combine(dir, e.Key);
            Assert.True(File.Exists(path), $"{e.Key} is missing from {dir}");
            byte[] data = File.ReadAllBytes(path);

            if (!Jp2ColourDescription.TryRead(data, out Jp2ColourDescription d, out string reason))
            {
                failures.Add($"{e.Key}: refused: {reason}");
                continue;
            }
            if (!d.TryPlanForPdf(SizComponents(data), NoDictionary, null, out Jp2PdfChannelPlan plan, out reason))
            {
                failures.Add($"{e.Key}: no plan: {reason}");
                continue;
            }

            if (plan.ColourSpace != e.Value.kind) failures.Add($"{e.Key}: colour space {plan.ColourSpace}, expected {e.Value.kind}");
            if (!Equal(plan.ColourChannels, e.Value.colour)) failures.Add($"{e.Key}: colour channels {string.Join(",", plan.ColourChannels)}");
            if (!Equal(plan.ColourChannelDepths, e.Value.depths)) failures.Add($"{e.Key}: depths {string.Join(",", plan.ColourChannelDepths)}");
            if (d.ColourSpecifications.Length != e.Value.colrBoxes) failures.Add($"{e.Key}: {d.ColourSpecifications.Length} colr boxes");
            if ((d.ChannelDefinitions != null) != e.Value.cdef) failures.Add($"{e.Key}: cdef {(d.ChannelDefinitions != null ? "present" : "absent")}");
            if (plan.PaletteApplied != e.Value.palette) failures.Add($"{e.Key}: palette applied {plan.PaletteApplied}");
            if (plan.OpacityChannel != -1) failures.Add($"{e.Key}: opacity channel {plan.OpacityChannel}");
            if (!d.HasImageHeader || d.ComponentCount != SizComponents(data).Length) failures.Add($"{e.Key}: ihdr NC {d.ComponentCount}");
            if (d.Deviations.Count != 0) failures.Add($"{e.Key}: header deviations: {string.Join("; ", d.Deviations)}");
            if (!JpxHeader.TryReadColourComponents(data, out int publicCount) || publicCount != e.Value.publicCount)
                failures.Add($"{e.Key}: TryReadColourComponents {publicCount}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // file5 and file7: the enumerated space at precedence 1 outranks the restricted ICC profile at 0,
        // which is still readable — 'RGB ' with an XYZ connection, 546 and 13332 bytes.
        Jp2ColourDescription file5 = Read(File.ReadAllBytes(Path.Combine(dir, "file5.jp2")));
        Assert.Equal((2, 0, "RGB ", 546L), (file5.ColourSpecifications[0].Method, file5.ColourSpecifications[0].Precedence, file5.ColourSpecifications[0].IccDataColourSpace, file5.ColourSpecifications[0].IccProfileLength));
        Assert.Equal((1, 1, 21L), (file5.ColourSpecifications[1].Method, file5.ColourSpecifications[1].Precedence, file5.ColourSpecifications[1].EnumeratedSpace));
        Assert.Equal(1, file5.RankedColourSpecifications()[0].Order);
        Jp2ColourDescription file7 = Read(File.ReadAllBytes(Path.Combine(dir, "file7.jp2")));
        Assert.Equal((13332L, 20L), (file7.ColourSpecifications[0].IccProfileLength, file7.ColourSpecifications[1].EnumeratedSpace));

        // file8: the ICC profile is the only specification; a consumer that cannot use it gets DeviceGray.
        byte[] data8 = File.ReadAllBytes(Path.Combine(dir, "file8.jp2"));
        Jp2ColourDescription file8 = Read(data8);
        Jp2PdfChannelPlan icc = Plan(file8, SizComponents(data8), NoDictionary);
        Assert.Equal(("GRAY", 414), (icc.ColourSpecification.IccDataColourSpace, icc.ColourSpecification.IccProfile.Length));
        Jp2PdfChannelPlan fallback = Plan(file8, SizComponents(data8), NoDictionary, _ => false);
        Assert.Equal(Jp2ColourSpaceKind.DeviceGray, fallback.ColourSpace);
        AssertDeviation(fallback.Deviations, "does not support its ICC profile");

        // file9: 256 entries of three 8-bit columns, all three channels from component 0.
        byte[] data9 = File.ReadAllBytes(Path.Combine(dir, "file9.jp2"));
        Jp2ColourDescription file9 = Read(data9);
        Assert.Equal((256, 3), (file9.Palette.EntryCount, file9.Palette.ColumnCount));
        Assert.Equal(new[] { 8, 8, 8 }, file9.Palette.ColumnDepths);
        Assert.Equal((0L, 0L, 0L), (file9.Palette.Value(0, 0), file9.Palette.Value(0, 1), file9.Palette.Value(0, 2)));
        Assert.Equal((255L, 255L, 255L), (file9.Palette.Value(1, 0), file9.Palette.Value(1, 1), file9.Palette.Value(1, 2)));
        Assert.Equal((23L, 12L, 21L), (file9.Palette.Value(2, 0), file9.Palette.Value(2, 1), file9.Palette.Value(2, 2)));
        Assert.All(file9.ComponentMapping, m => Assert.True(m.ThroughPalette && m.Component == 0));
        Assert.Equal(1, Plan(file9, SizComponents(data9), WithColourSpace(1, indexed: true)).ColourComponentCount);

        // file2: the cdef's associations, as stored.
        Jp2ColourDescription file2 = Read(File.ReadAllBytes(Path.Combine(dir, "file2.jp2")));
        Assert.Equal(new[] { 3, 2, 1 }, Array.ConvertAll(file2.ChannelDefinitions, c => c.Association));
    }

    private static bool Equal(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
