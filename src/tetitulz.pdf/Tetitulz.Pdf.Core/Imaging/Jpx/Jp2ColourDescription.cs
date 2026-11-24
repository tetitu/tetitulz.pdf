// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// Box types of the JP2 file format (T.800 Annex I) and of the JPX additions a PDF may carry
    /// (T.801 Annex M), as big-endian four-character codes.
    /// </summary>
    internal static class Jp2BoxTypes
    {
        internal const uint Jp2h = 0x6A703268; // 'jp2h' — the JP2 Header superbox (T.800 I.5.3)
        internal const uint Jp2c = 0x6A703263; // 'jp2c' — the Contiguous Codestream box (T.800 I.5.4)
        internal const uint Ihdr = 0x69686472; // 'ihdr' — Image Header (T.800 I.5.3.1)
        internal const uint Bpcc = 0x62706363; // 'bpcc' — Bits Per Component (T.800 I.5.3.2)
        internal const uint Colr = 0x636F6C72; // 'colr' — Colour Specification (T.800 I.5.3.3)
        internal const uint Pclr = 0x70636C72; // 'pclr' — Palette (T.800 I.5.3.4)
        internal const uint Cmap = 0x636D6170; // 'cmap' — Component Mapping (T.800 I.5.3.5)
        internal const uint Cdef = 0x63646566; // 'cdef' — Channel Definition (T.800 I.5.3.6)
        internal const uint Jplh = 0x6A706C68; // 'jplh' — Compositing Layer Header (T.801 M.11.7)
        internal const uint Opct = 0x6F706374; // 'opct' — Opacity (T.801 M.11.7.6)
        internal const uint Ftbl = 0x6674626C; // 'ftbl' — Fragment Table (T.801 M.11.3): a codestream in pieces
        internal const uint Flst = 0x666C7374; // 'flst' — Fragment List (T.801 M.11.3.1): where the pieces are
    }

    /// <summary>
    /// The <c>EnumCS</c> codes of the enumerated colour-specification method (T.800 Table I.10;
    /// T.801 Table M.25) and which of them a PDF may carry.
    /// </summary>
    internal static class Jp2EnumeratedColourSpaces
    {
        internal const long Cmyk = 12;       // T.801 Table M.25; ISO 32000-2 §7.4.9 adds it although it is outside the JPX baseline
        internal const long CieLab = 14;     // T.801 Table M.25; baseline with default or enumerated parameters (M.9.2.4)
        internal const long SRgb = 16;       // T.800 Table I.10
        internal const long Greyscale = 17;  // T.800 Table I.10 (sRGB-grey)
        internal const long SYcc = 18;       // T.800 Table I.10
        internal const long CieJab = 19;     // T.801 Table M.25; baseline, but ISO 32000-2 §7.4.9 excludes it
        internal const long ESRgb = 20;      // T.801 Table M.25
        internal const long RommRgb = 21;    // T.801 Table M.25
        internal const long ESYcc = 24;      // T.801 Table M.25

        /// <summary>
        /// The colour-channel count of an enumerated space a PDF may use, or 0 for every other code.
        /// ISO 32000-2 §7.4.9: the JPX baseline's spaces (T.801 M.9.2.4 — sRGB, sRGB-grey, sYCC,
        /// e-sRGB, ROMM-RGB, e-sYCC and CIELab) less CIEJab (19), plus CMYK (12).
        /// </summary>
        /// <param name="enumCs">The <c>EnumCS</c> field.</param>
        internal static int ColourChannels(long enumCs)
        {
            switch (enumCs)
            {
                case Cmyk: return 4;
                case CieLab:
                case SRgb:
                case SYcc:
                case ESRgb:
                case RommRgb:
                case ESYcc: return 3;
                case Greyscale: return 1;
                default: return 0;
            }
        }

        /// <summary>The <see cref="Jp2ColourSpaceKind"/> of a code <see cref="ColourChannels"/> accepts.</summary>
        /// <param name="enumCs">The <c>EnumCS</c> field.</param>
        internal static Jp2ColourSpaceKind Kind(long enumCs)
        {
            switch (enumCs)
            {
                case Cmyk: return Jp2ColourSpaceKind.Cmyk;
                case CieLab: return Jp2ColourSpaceKind.CieLab;
                case SRgb: return Jp2ColourSpaceKind.SRgb;
                case Greyscale: return Jp2ColourSpaceKind.Greyscale;
                case SYcc: return Jp2ColourSpaceKind.SYcc;
                case ESRgb: return Jp2ColourSpaceKind.ESRgb;
                case RommRgb: return Jp2ColourSpaceKind.RommRgb;
                case ESYcc: return Jp2ColourSpaceKind.ESYcc;
                default: return Jp2ColourSpaceKind.DeviceRgb;
            }
        }
    }

    /// <summary>The colour space a PDF consumer interprets a JPX image's colour channels in.</summary>
    internal enum Jp2ColourSpaceKind
    {
        /// <summary>The image dictionary's <c>/ColorSpace</c> governs and the file's colour
        /// specifications are ignored (ISO 32000-2 §7.4.9).</summary>
        ImageDictionary,
        /// <summary><c>/ImageMask true</c>: the single 1-bit channel is a stencil; no colour space
        /// applies (ISO 32000-2 §7.4.9, last bullet).</summary>
        StencilMask,
        /// <summary>EnumCS 16.</summary>
        SRgb,
        /// <summary>EnumCS 17, sRGB-grey.</summary>
        Greyscale,
        /// <summary>EnumCS 18.</summary>
        SYcc,
        /// <summary>EnumCS 20, e-sRGB.</summary>
        ESRgb,
        /// <summary>EnumCS 21, ROMM-RGB.</summary>
        RommRgb,
        /// <summary>EnumCS 24, e-sYCC.</summary>
        ESYcc,
        /// <summary>EnumCS 14, with default or enumerated parameters (<see cref="Jp2CieLabParameters"/>).</summary>
        CieLab,
        /// <summary>EnumCS 12.</summary>
        Cmyk,
        /// <summary>An ICC profile from a <c>colr</c> box (METH 2 or 3); the bytes are on
        /// <see cref="Jp2PdfChannelPlan.ColourSpecification"/>.</summary>
        IccProfile,
        /// <summary>No supported specification and one ordinary channel (ISO 32000-2 §7.4.9).</summary>
        DeviceGray,
        /// <summary>No supported specification and three ordinary channels (ISO 32000-2 §7.4.9).</summary>
        DeviceRgb,
        /// <summary>No supported specification and four ordinary channels (ISO 32000-2 §7.4.9).</summary>
        DeviceCmyk,
    }

    /// <summary>One <c>colr</c> box (T.800 I.5.3.3; T.801 M.11.7.2).</summary>
    internal sealed class Jp2ColourSpecification
    {
        // METH (T.800 Table I.9; T.801 Table M.22).
        internal const int EnumeratedMethod = 1;
        internal const int RestrictedIccMethod = 2;
        internal const int AnyIccMethod = 3;
        internal const int VendorMethod = 4;
        internal const int ParameterizedMethod = 5;

        internal Jp2ColourSpecification(int order, int method, int precedence, int approximation,
            long enumeratedSpace, byte[] enumeratedParameters, byte[] iccProfile, long iccProfileLength,
            string iccDataColourSpace)
        {
            Order = order;
            Method = method;
            Precedence = precedence;
            Approximation = approximation;
            EnumeratedSpace = enumeratedSpace;
            EnumeratedParameters = enumeratedParameters;
            IccProfile = iccProfile;
            IccProfileLength = iccProfileLength;
            IccDataColourSpace = iccDataColourSpace;
            ColourChannels = method == EnumeratedMethod ? Jp2EnumeratedColourSpaces.ColourChannels(enumeratedSpace)
                : IsIcc ? IccDataColourSpaces.ComponentCount(iccDataColourSpace)
                : 0;
        }

        /// <summary>Position among the file's <c>colr</c> boxes, from 0 — the tie-breaker T.800 I.5.3.3 implies (a JP2 reader uses the first).</summary>
        internal int Order { get; }

        /// <summary><c>METH</c>. Only 1, 2 and 3 are read; T.800 Table I.9 and T.801 Table M.22 have a reader ignore any other.</summary>
        internal int Method { get; }

        /// <summary><c>PREC</c>, a signed byte: higher is preferred (T.801 M.11.7.2). Always 0 in a JP2, where readers ignore it.</summary>
        internal int Precedence { get; }

        /// <summary><c>APPROX</c>: 1 accurate to 4 poor (T.801 Table M.23); 0 in a JP2, where readers ignore it.</summary>
        internal int Approximation { get; }

        /// <summary><c>EnumCS</c> when <see cref="Method"/> is 1, else −1.</summary>
        internal long EnumeratedSpace { get; }

        /// <summary>The <c>EP</c> field — every byte after <c>EnumCS</c> (T.801 M.11.7.3.1); empty when there is none.</summary>
        internal byte[] EnumeratedParameters { get; }

        /// <summary>The <c>PROFILE</c> field for METH 2 or 3, or null when it is not kept (over
        /// <see cref="PdfLimits.MaxIccProfileBytes"/>) or the method is not ICC.</summary>
        internal byte[] IccProfile { get; }

        /// <summary>The <c>PROFILE</c> field's length, kept or not.</summary>
        internal long IccProfileLength { get; }

        /// <summary>The profile's data colour space signature (ICC.1 header offset 16), or null for a non-ICC method.</summary>
        internal string IccDataColourSpace { get; }

        /// <summary>The colour-channel count this specification implies, or 0 when it says nothing a
        /// PDF can use: an enumerated space outside ISO 32000-2 §7.4.9's list, an unrecognised ICC
        /// signature, or a method a reader ignores.</summary>
        internal int ColourChannels { get; }

        internal bool IsIcc => Method == RestrictedIccMethod || Method == AnyIccMethod;

        /// <summary>A short description for a reason string.</summary>
        internal string Describe()
        {
            if (Method == EnumeratedMethod) return $"EnumCS {EnumeratedSpace}";
            if (IsIcc) return $"METH {Method} ICC profile '{IccDataColourSpace}'";
            return $"METH {Method}";
        }
    }

    /// <summary>
    /// The <c>EP</c> field of a CIELab colour specification (T.801 M.11.7.4.1, Table M.30): the
    /// range and offset of each of L*, a* and b* in equation M-18, and the illuminant.
    /// </summary>
    internal readonly struct Jp2CieLabParameters
    {
        /// <summary>Seven 32-bit fields (Table M.30).</summary>
        internal const int EncodedLength = 28;

        /// <summary>CIE illuminant D50 (Table M.29), T.42's illuminant and the default here.</summary>
        internal const uint IlluminantD50 = 0x00443530;

        internal Jp2CieLabParameters(long rangeL, long offsetL, long rangeA, long offsetA, long rangeB, long offsetB,
            uint illuminant, bool isDefault)
        {
            RangeL = rangeL;
            OffsetL = offsetL;
            RangeA = rangeA;
            OffsetA = offsetA;
            RangeB = rangeB;
            OffsetB = offsetB;
            Illuminant = illuminant;
            IsDefault = isDefault;
        }

        internal long RangeL { get; }
        internal long OffsetL { get; }
        internal long RangeA { get; }
        internal long OffsetA { get; }
        internal long RangeB { get; }
        internal long OffsetB { get; }
        internal uint Illuminant { get; }

        /// <summary>True when the values are T.801's defaults rather than the file's EP field.</summary>
        internal bool IsDefault { get; }

        /// <summary>
        /// Reads a 28-byte <c>EP</c> field. False for any other length: an EP for EnumCS 14 has
        /// exactly Table M.30's seven fields, and an absent one (0 bytes) means <see cref="Default"/>.
        /// </summary>
        /// <param name="ep">The bytes after <c>EnumCS</c>.</param>
        /// <param name="parameters">The seven fields.</param>
        internal static bool TryParse(byte[] ep, out Jp2CieLabParameters parameters)
        {
            parameters = default;
            if (ep == null || ep.Length != EncodedLength) return false;
            parameters = new Jp2CieLabParameters(
                JpxHeader.ReadU32(ep, 0), JpxHeader.ReadU32(ep, 4),
                JpxHeader.ReadU32(ep, 8), JpxHeader.ReadU32(ep, 12),
                JpxHeader.ReadU32(ep, 16), JpxHeader.ReadU32(ep, 20),
                (uint)JpxHeader.ReadU32(ep, 24), false);
            return true;
        }

        /// <summary>
        /// The values when the EP field is absent (T.801 M.11.7.4.1, Table M.29bis): ranges 100,
        /// 170 and 200; offset 0 for L*; for an unsigned a* channel of <c>BPC + 1</c> bits
        /// 2^<c>BPC</c> (half the range) and for an unsigned b* channel 2^(<c>BPC</c>−1) +
        /// 2^(<c>BPC</c>−2) (three eighths of it); 0 for a signed channel. The illuminant is D50.
        /// </summary>
        /// <param name="depthA">The a* channel's bits, including a sign bit.</param>
        /// <param name="signedA">Whether the a* channel is signed.</param>
        /// <param name="depthB">The b* channel's bits, including a sign bit.</param>
        /// <param name="signedB">Whether the b* channel is signed.</param>
        internal static Jp2CieLabParameters Default(int depthA, bool signedA, int depthB, bool signedB)
        {
            int bpcA = depthA - 1, bpcB = depthB - 1;
            long offsetA = signedA || bpcA < 0 ? 0 : 1L << bpcA;
            long offsetB = signedB || bpcB < 2 ? 0 : (1L << (bpcB - 1)) + (1L << (bpcB - 2));
            return new Jp2CieLabParameters(100, 0, 170, offsetA, 200, offsetB, IlluminantD50, true);
        }
    }

    /// <summary>The <c>pclr</c> box (T.800 I.5.3.4): <c>NE</c> entries of <c>NPC</c> columns.</summary>
    internal sealed class Jp2Palette
    {
        /// <summary>T.800 Table I.12: <c>NE</c> is 1 to 1024.</summary>
        internal const int MaxEntries = 1024;

        private readonly long[] _values;

        internal Jp2Palette(int entryCount, int columnCount, int[] columnDepths, bool[] columnSigned, long[] values)
        {
            EntryCount = entryCount;
            ColumnCount = columnCount;
            ColumnDepths = columnDepths;
            ColumnSigned = columnSigned;
            _values = values;
        }

        /// <summary><c>NE</c>.</summary>
        internal int EntryCount { get; }

        /// <summary><c>NPC</c>.</summary>
        internal int ColumnCount { get; }

        /// <summary>Bits per column, 1–38, from <c>B_i</c> (T.800 Table I.13) — the depth of a channel the column feeds (I.5.3.3's <c>BPC_i</c>).</summary>
        internal int[] ColumnDepths { get; }

        /// <summary>Whether each column's values are signed (<c>B_i</c>'s high bit).</summary>
        internal bool[] ColumnSigned { get; }

        /// <summary><c>C_ji</c>: the value of entry <paramref name="entry"/> in column <paramref name="column"/>, sign-extended when the column is signed.</summary>
        /// <param name="entry">0 to <see cref="EntryCount"/> − 1.</param>
        /// <param name="column">0 to <see cref="ColumnCount"/> − 1.</param>
        internal long Value(int entry, int column) => _values[entry * ColumnCount + column];
    }

    /// <summary>One channel of the <c>cmap</c> box (T.800 I.5.3.5): where its samples come from.</summary>
    internal sealed class Jp2ChannelMapping
    {
        // MTYP (T.800 Table I.14).
        internal const int DirectMapping = 0;
        internal const int PaletteMapping = 1;

        internal Jp2ChannelMapping(int component, bool throughPalette, int paletteColumn)
        {
            Component = component;
            ThroughPalette = throughPalette;
            PaletteColumn = paletteColumn;
        }

        /// <summary><c>CMP</c>: the codestream component, used directly or as the palette's index.</summary>
        internal int Component { get; }

        /// <summary><c>MTYP</c> 1: the palette column <see cref="PaletteColumn"/> is applied to the component.</summary>
        internal bool ThroughPalette { get; }

        /// <summary><c>PCOL</c>; meaningful only when <see cref="ThroughPalette"/>.</summary>
        internal int PaletteColumn { get; }
    }

    /// <summary>One description of the <c>cdef</c> box (T.800 I.5.3.6): a channel's type and the colour it carries.</summary>
    internal sealed class Jp2ChannelDefinition
    {
        // Typ (T.800 Table I.16).
        internal const int ColourType = 0;
        internal const int OpacityType = 1;
        internal const int PremultipliedOpacityType = 2;
        internal const int UnspecifiedType = 0xFFFF;

        // Asoc (T.800 Table I.17): 0 the whole image, 1..65534 a colour of Table I.18, 65535 none.
        internal const int WholeImage = 0;
        internal const int NoAssociation = 0xFFFF;

        internal Jp2ChannelDefinition(int channel, int type, int association)
        {
            Channel = channel;
            Type = type;
            Association = association;
        }

        /// <summary><c>Cn</c>: a <c>cmap</c> channel, or a component when there is no <c>cmap</c>.</summary>
        internal int Channel { get; }

        /// <summary><c>Typ</c>.</summary>
        internal int Type { get; }

        /// <summary><c>Asoc</c>.</summary>
        internal int Association { get; }
    }

    /// <summary>What the image dictionary says about a <c>JPXDecode</c> image, as ISO 32000-2 §7.4.9 reads it.</summary>
    internal readonly struct Jp2PdfImageFacts
    {
        internal Jp2PdfImageFacts(int colourSpaceComponents, bool colourSpaceIsIndexed, int smaskInData, bool imageMask)
        {
            ColourSpaceComponents = colourSpaceComponents;
            ColourSpaceIsIndexed = colourSpaceIsIndexed;
            SMaskInData = smaskInData;
            ImageMask = imageMask;
        }

        /// <summary>The component count of <c>/ColorSpace</c>, or 0 when the entry is absent.</summary>
        internal int ColourSpaceComponents { get; }

        /// <summary>True when <c>/ColorSpace</c> is an Indexed space, whose samples are the indices a JP2 palette would otherwise consume.</summary>
        internal bool ColourSpaceIsIndexed { get; }

        /// <summary><c>/SMaskInData</c>: 0 (or absent), 1 or 2 (ISO 32000-2 Table 87).</summary>
        internal int SMaskInData { get; }

        /// <summary><c>/ImageMask</c>.</summary>
        internal bool ImageMask { get; }
    }

    /// <summary>One channel of the image as a PDF consumer reads it: its source and its meaning.</summary>
    internal sealed class Jp2Channel
    {
        internal Jp2Channel(int component, int paletteColumn, int depth, bool isSigned, int type, int association)
        {
            Component = component;
            PaletteColumn = paletteColumn;
            Depth = depth;
            IsSigned = isSigned;
            Type = type;
            Association = association;
        }

        /// <summary>The codestream component the samples come from — directly, or as the index into <see cref="PaletteColumn"/>.</summary>
        internal int Component { get; }

        /// <summary>The palette column applied to <see cref="Component"/>, or −1 for a direct channel.</summary>
        internal int PaletteColumn { get; }

        /// <summary>Bits per sample: the palette column's, else the component's (T.800 I.5.3.3, <c>BPC_i</c>).</summary>
        internal int Depth { get; }

        /// <summary>Whether the samples are signed.</summary>
        internal bool IsSigned { get; }

        /// <summary>A <see cref="Jp2ChannelDefinition"/> type.</summary>
        internal int Type { get; }

        /// <summary>A <see cref="Jp2ChannelDefinition"/> association.</summary>
        internal int Association { get; }
    }

    /// <summary>
    /// What a PDF consumer makes of a JPX image's channels: which are colour and in what order,
    /// which is the opacity channel, whether the palette is applied, and the colour space.
    /// Produced by <see cref="Jp2ColourDescription.TryPlanForPdf"/>.
    /// </summary>
    internal sealed class Jp2PdfChannelPlan
    {
        /// <summary>Every channel, in channel order (the <c>cmap</c>'s, else the codestream's).</summary>
        internal Jp2Channel[] Channels { get; set; }

        /// <summary>The colour channels in colour order — red, green, blue; or Y, Cb, Cr; or C, M, Y, K (T.800 Table I.18).</summary>
        internal int[] ColourChannels { get; set; }

        /// <summary>The component count a PDF consumer sees: <see cref="ColourChannels"/>' length.</summary>
        internal int ColourComponentCount => ColourChannels.Length;

        /// <summary>Bits per sample of each colour channel, in <see cref="ColourChannels"/>' order.</summary>
        internal int[] ColourChannelDepths
        {
            get
            {
                var depths = new int[ColourChannels.Length];
                for (int i = 0; i < depths.Length; i++) depths[i] = Channels[ColourChannels[i]].Depth;
                return depths;
            }
        }

        /// <summary>The one opacity channel that applies to the whole image, or −1. Named even when
        /// <see cref="OpacityApplies"/> is false, so a consumer can leave it out of the colour samples.</summary>
        internal int OpacityChannel { get; set; } = -1;

        /// <summary>True when <c>/SMaskInData</c> is 1 or 2 and <see cref="OpacityChannel"/> is set: the consumer builds a soft mask from it (ISO 32000-2 Table 87).</summary>
        internal bool OpacityApplies { get; set; }

        /// <summary><c>/SMaskInData 2</c>: the colour samples are premultiplied by the opacity (ISO 32000-2 Table 87).</summary>
        internal bool OpacityIsPremultiplied { get; set; }

        /// <summary>True when the image dictionary's Indexed colour space governs: the colour samples are palette indices, handed on as they are (clamped to a byte), not narrowed to 8 bits.</summary>
        internal bool IndicesVerbatim { get; set; }

        /// <summary>True when a channel is produced by applying the palette to a component (T.800 I.5.3.4).</summary>
        internal bool PaletteApplied { get; set; }

        /// <summary>The colour space the colour channels are interpreted in.</summary>
        internal Jp2ColourSpaceKind ColourSpace { get; set; }

        /// <summary>The <c>colr</c> box <see cref="ColourSpace"/> came from, or null for the image dictionary's space, a stencil mask, or a device fallback.</summary>
        internal Jp2ColourSpecification ColourSpecification { get; set; }

        /// <summary>
        /// Whether the image dictionary's <c>/Decode</c> array applies: ISO 32000-2 §7.4.9 has it
        /// ignored when <c>/ColorSpace</c> is absent, unless <c>/ImageMask</c> is true.
        /// </summary>
        internal bool DecodeArrayApplies { get; set; }

        /// <summary>Tolerated irregularities, each with the clause that lets it pass.</summary>
        internal List<string> Deviations { get; } = new List<string>();
    }

    /// <summary>
    /// What the JP2 file format says about an image's colour and channels — the contents of the
    /// JP2 Header box (T.800 I.5.3: <c>ihdr</c>, <c>bpcc</c>, <c>colr</c>, <c>pclr</c>, <c>cmap</c>,
    /// <c>cdef</c>) and the first compositing layer's Opacity box (T.801 M.11.7.6) — and, through
    /// <see cref="TryPlanForPdf"/>, what a PDF consumer makes of it under ISO 32000-2 §7.4.9.
    /// </summary>
    /// <remarks>
    /// <para><b>Metadata only.</b> Nothing here decodes a sample; the plan says which decoded
    /// channel is which.</para>
    /// <para><b>One reader.</b> <see cref="JpxHeader.TryReadColourComponents"/> answers from this
    /// reader's <see cref="ColourChannelCount"/>; the box walk is <see cref="JpxHeader"/>'s.</para>
    /// <para><b>A malformed box refuses the header with a reason</b> — a truncated box, a palette
    /// with no entries or columns, a mapping naming a component or palette column that does not
    /// exist, a definition naming a channel past the count. An <i>absent</i> optional box is
    /// tolerated, as is a missing <c>ihdr</c> (T.800 I.5.3.1 lets a reader fall back on the
    /// codestream's own values, and the codestream is the authority anyway); such irregularities are
    /// listed in <see cref="Deviations"/>.</para>
    /// <para><b>Every walk is bounded</b>: 64 boxes in the header, and each variable-length box is
    /// sized by its own length before anything is allocated for it.</para>
    /// </remarks>
    internal sealed class Jp2ColourDescription
    {
        private const int MaxHeaderBoxes = JpxHeader.MaxBoxesPerWalk;

        /// <summary>T.800 I.5.3.1: the Image Header box is 22 bytes, 14 of them content.</summary>
        private const int ImageHeaderLength = 14;

        /// <summary>T.800 Table I.6: the <c>BPC</c> value meaning "the components vary; see <c>bpcc</c>".</summary>
        private const int VaryingDepth = 255;

        /// <summary>T.800 Table I.5: the compression type of a JPEG 2000 codestream.</summary>
        private const int Jpeg2000Compression = 7;

        /// <summary>The bytes of an ICC profile up to and including its data colour space signature (ICC.1 header offset 16, four bytes).</summary>
        private const int IccSignatureEnd = 20;

        private const int MaxDepth = 38;

        /// <summary>A channel count no colour space can use; bounds the <c>cmap</c> allocation.</summary>
        private const int MaxChannels = JpxImageSize.MaxComponents;

        private byte[] _bpcc;
        private int _bpc = -1;

        private Jp2ColourDescription()
        {
            ColourSpecifications = Array.Empty<Jp2ColourSpecification>();
        }

        /// <summary>True when the header carried an <c>ihdr</c>; the grid and component fields are 0 otherwise.</summary>
        internal bool HasImageHeader { get; private set; }

        /// <summary><c>WIDTH</c>, the image area's width (<c>Xsiz − XOsiz</c>).</summary>
        internal long Width { get; private set; }

        /// <summary><c>HEIGHT</c>, the image area's height (<c>Ysiz − YOsiz</c>).</summary>
        internal long Height { get; private set; }

        /// <summary><c>NC</c>, the codestream's component count as the header states it.</summary>
        internal int ComponentCount { get; private set; }

        /// <summary>Bits per component from <c>bpcc</c>, else from <c>ihdr</c>'s <c>BPC</c>; null when neither says (no header, or <c>BPC</c> 255 without a <c>bpcc</c>).</summary>
        internal int[] ComponentDepths { get; private set; }

        /// <summary>Whether the header holds a Bits Per Component box (T.800 I.5.3.2) — the components' depths vary, which ISO 19005-2 §6.2.8.3 forbids.</summary>
        internal bool HasBitsPerComponentBox => _bpcc != null;

        /// <summary>The sign bit of each component's depth field; null when <see cref="ComponentDepths"/> is.</summary>
        internal bool[] ComponentSigned { get; private set; }

        /// <summary><c>UnkC</c> 1: the writer did not know the true colour space; T.800 I.5.3.1 has the specifications used regardless.</summary>
        internal bool ColourSpaceUnknown { get; private set; }

        /// <summary>Every <c>colr</c> box, in file order; possibly empty.</summary>
        internal Jp2ColourSpecification[] ColourSpecifications { get; private set; }

        /// <summary>The <c>pclr</c> box, or null.</summary>
        internal Jp2Palette Palette { get; private set; }

        /// <summary>The <c>cmap</c> box's channels, or null when components map to channels directly.</summary>
        internal Jp2ChannelMapping[] ComponentMapping { get; private set; }

        /// <summary>The <c>cdef</c> box's descriptions, or null.</summary>
        internal Jp2ChannelDefinition[] ChannelDefinitions { get; private set; }

        /// <summary>
        /// <c>Otyp</c> of the first compositing layer's Opacity box (T.801 Table M.33): 0 the last
        /// channel is opacity, 1 premultiplied opacity, 2 a chroma key; −1 when there is no such box.
        /// </summary>
        internal int OpacityBoxType { get; private set; } = -1;

        /// <summary>Tolerated irregularities of the header, each with the clause that lets it pass.</summary>
        internal List<string> Deviations { get; } = new List<string>();

        /// <summary>
        /// The description of a raw codestream, which has no boxes: every channel a colour channel in
        /// codestream order, no palette, no opacity, no colour specification. Lets a consumer plan a
        /// bare codestream by the same rules (ISO 32000-2 §7.4.9 expects a JPX file, but bare
        /// codestreams occur in PDFs).
        /// </summary>
        internal static Jp2ColourDescription ForRawCodestream() => new Jp2ColourDescription();

        /// <summary>
        /// Reads the JP2 Header box and the first compositing layer's Opacity box of a JP2 or JPX
        /// container. False, with the reason, for a raw codestream, a container without a header,
        /// or a malformed box.
        /// </summary>
        /// <param name="data">The image's bytes.</param>
        /// <param name="description">The header's contents.</param>
        /// <param name="reason">Why the header was refused, when this returns false.</param>
        internal static bool TryRead(byte[] data, out Jp2ColourDescription description, out string reason)
        {
            description = null;
            reason = null;
            if (!JpxHeader.IsJp2Container(data))
            {
                reason = "not a JP2 container: a raw codestream carries no header boxes";
                return false;
            }
            if (!JpxHeader.TryFindTopLevelBox(data, Jp2BoxTypes.Jp2h, out long start, out long end))
            {
                reason = "no JP2 Header box (jp2h) among the top-level boxes";
                return false;
            }

            var d = new Jp2ColourDescription();
            if (!d.ReadHeaderBoxes(data, start, end, out reason)) return false;
            if (!d.CrossCheck(out reason)) return false;

            // T.801 M.9.2.8: a baseline reader takes opacity from a cdef, from an Opacity box, or from
            // a palette; the Opacity box lives in the layer's header (M.11.7.6), never in jp2h.
            if (JpxHeader.TryFindTopLevelBox(data, Jp2BoxTypes.Jplh, out long layerStart, out long layerEnd)
                && !d.ReadOpacityBox(data, layerStart, layerEnd, out reason))
                return false;

            description = d;
            return true;
        }

        /// <summary>
        /// The <c>colr</c> boxes in the order a reader should try them: highest <c>PREC</c> first
        /// (T.801 M.11.7.2), then best <c>APPROX</c> — ISO 32000-2 §7.4.9's "highest precedence and
        /// best approximation value" — then file order, which is all a JP2 reader has (T.800 I.5.3.3:
        /// use the first). <c>APPROX</c> 0, the JP2 value readers ignore, ranks with 1.
        /// </summary>
        internal Jp2ColourSpecification[] RankedColourSpecifications()
        {
            var ranked = (Jp2ColourSpecification[])ColourSpecifications.Clone();
            Array.Sort(ranked, (a, b) =>
            {
                int c = b.Precedence.CompareTo(a.Precedence);
                if (c != 0) return c;
                c = ApproximationRank(a).CompareTo(ApproximationRank(b));
                if (c != 0) return c;
                return a.Order.CompareTo(b.Order);
            });
            return ranked;
        }

        private static int ApproximationRank(Jp2ColourSpecification spec) =>
            spec.Approximation == 0 ? 1 : spec.Approximation;

        /// <summary>Whether a <c>cdef</c> box declares an opacity channel (plain or premultiplied, whatever it is associated with), or a compositing layer's Opacity box does: the inspector's "has alpha".</summary>
        internal bool DeclaresOpacity()
        {
            if (OpacityBoxType == 0 || OpacityBoxType == 1) return true;
            if (ChannelDefinitions == null) return false;
            foreach (Jp2ChannelDefinition def in ChannelDefinitions)
                if (def.Type == Jp2ChannelDefinition.OpacityType || def.Type == Jp2ChannelDefinition.PremultipliedOpacityType) return true;
            return false;
        }

        /// <summary>
        /// The <c>/SMaskInData</c> value that says what the file declares (ISO 32000-2 Table 89):
        /// 0 for no opacity channel, 1 for one straight opacity channel applying to the whole
        /// image (cdef Typ 1 with Asoc 0, or an Opacity box of Otyp 0), 2 for a premultiplied one
        /// (Typ 2, or Otyp 1) — and 0 for what the key cannot express, an opacity channel
        /// associated with one colour or several opacity channels, which §7.4.9 forbids the key
        /// for: an embedder then writes no key, and the file's opacity is left aside.
        /// </summary>
        internal int OpacityMode()
        {
            if (OpacityBoxType == 0) return 1;
            if (OpacityBoxType == 1) return 2;
            if (ChannelDefinitions == null) return 0;
            int mode = 0, count = 0;
            foreach (Jp2ChannelDefinition def in ChannelDefinitions)
            {
                if (def.Type != Jp2ChannelDefinition.OpacityType && def.Type != Jp2ChannelDefinition.PremultipliedOpacityType) continue;
                count++;
                if (def.Association != Jp2ChannelDefinition.WholeImage) return 0;
                mode = def.Type == Jp2ChannelDefinition.PremultipliedOpacityType ? 2 : 1;
            }
            return count == 1 ? mode : 0;
        }

        /// <summary>
        /// How many channels are colour: the count the preferred usable colour specification implies,
        /// else the count of channels the <c>cdef</c> types as colour; 0 when the header says neither.
        /// What <see cref="JpxHeader.TryReadColourComponents"/> answers.
        /// </summary>
        internal int ColourChannelCount()
        {
            foreach (Jp2ColourSpecification spec in RankedColourSpecifications())
                if (spec.ColourChannels > 0) return spec.ColourChannels;

            if (ChannelDefinitions == null) return 0;
            var colour = new HashSet<int>();
            foreach (Jp2ChannelDefinition def in ChannelDefinitions)
                if (def.Type == Jp2ChannelDefinition.ColourType) colour.Add(def.Channel);
            return colour.Count;
        }

        // ── reading ────────────────────────────────────────────────────────────

        private bool ReadHeaderBoxes(byte[] data, long start, long end, out string reason)
        {
            reason = null;
            var specs = new List<Jp2ColourSpecification>();
            long pos = start;
            for (int i = 0; pos < end; i++)
            {
                if (i == MaxHeaderBoxes)
                {
                    reason = $"the JP2 Header box holds more than {MaxHeaderBoxes} boxes";
                    return false;
                }
                if (!JpxHeader.TryReadBoxHeader(data, pos, out uint type, out long cs, out long ce) || ce > end)
                {
                    reason = $"the box at offset {pos} inside the JP2 Header box is truncated or runs past the header's end";
                    return false;
                }

                switch (type)
                {
                    case Jp2BoxTypes.Ihdr:
                        if (!ReadImageHeader(data, cs, ce, out reason)) return false;
                        break;
                    case Jp2BoxTypes.Bpcc:
                        if (_bpcc != null)
                        {
                            reason = DuplicateBox("Bits Per Component");
                            return false;
                        }
                        _bpcc = Slice(data, cs, ce);
                        break;
                    case Jp2BoxTypes.Colr:
                        if (!ReadColourSpecification(data, cs, ce, specs, out reason)) return false;
                        break;
                    case Jp2BoxTypes.Pclr:
                        if (!ReadPalette(data, cs, ce, out reason)) return false;
                        break;
                    case Jp2BoxTypes.Cmap:
                        if (!ReadComponentMapping(data, cs, ce, out reason)) return false;
                        break;
                    case Jp2BoxTypes.Cdef:
                        if (!ReadChannelDefinitions(data, cs, ce, out reason)) return false;
                        break;
                    // 'res ' and boxes defined by other standards are ignored (T.800 I.5.3).
                }
                pos = ce;
            }
            ColourSpecifications = specs.ToArray();
            return true;
        }

        private static string DuplicateBox(string name) =>
            $"the JP2 Header box holds two {name} boxes; T.800 I.5.3 allows at most one";

        /// <summary>T.800 I.5.3.1, Table I.5.</summary>
        private bool ReadImageHeader(byte[] data, long cs, long ce, out string reason)
        {
            reason = null;
            if (HasImageHeader)
            {
                reason = DuplicateBox("Image Header");
                return false;
            }
            if (ce - cs != ImageHeaderLength)
            {
                reason = $"the Image Header box holds {ce - cs} bytes; T.800 I.5.3.1 fixes it at {ImageHeaderLength}";
                return false;
            }

            long height = JpxHeader.ReadU32(data, cs);
            long width = JpxHeader.ReadU32(data, cs + 4);
            int nc = JpxHeader.ReadU16(data, cs + 8);
            int bpc = data[cs + 10];
            int compression = data[cs + 11];
            int unknown = data[cs + 12];

            if (width < 1 || height < 1)
            {
                reason = $"the Image Header box declares a {width}x{height} image; T.800 Table I.5 requires at least 1x1";
                return false;
            }
            if (nc < 1 || nc > JpxImageSize.MaxComponents)
            {
                reason = $"the Image Header box declares {nc} components; T.800 Table I.5 allows 1 to {JpxImageSize.MaxComponents}";
                return false;
            }
            if (bpc != VaryingDepth && (bpc & 0x7F) + 1 > MaxDepth)
            {
                reason = $"the Image Header box declares {(bpc & 0x7F) + 1}-bit components; T.800 Table I.6 allows 1 to {MaxDepth}";
                return false;
            }
            if (compression != Jpeg2000Compression)
                Deviations.Add($"the Image Header box declares compression type {compression}; T.800 Table I.5 defines 7 (JPEG 2000)");
            if (unknown > 1)
                Deviations.Add($"the Image Header box's UnkC is {unknown}; T.800 Table I.5 defines 0 and 1");

            HasImageHeader = true;
            Width = width;
            Height = height;
            ComponentCount = nc;
            ColourSpaceUnknown = unknown == 1;
            _bpc = bpc;
            return true;
        }

        /// <summary>T.800 I.5.3.3, Table I.11; T.801 M.11.7.2, Table M.24.</summary>
        private bool ReadColourSpecification(byte[] data, long cs, long ce, List<Jp2ColourSpecification> specs, out string reason)
        {
            reason = null;
            int order = specs.Count;
            long length = ce - cs;
            if (length < 3)
            {
                reason = $"colr box {order} holds {length} bytes; METH, PREC and APPROX need 3";
                return false;
            }

            int meth = data[cs];
            int prec = (sbyte)data[cs + 1];
            int approx = data[cs + 2];
            switch (meth)
            {
                case Jp2ColourSpecification.EnumeratedMethod:
                {
                    if (length < 7)
                    {
                        reason = $"colr box {order} is truncated: no EnumCS follows APPROX";
                        return false;
                    }
                    long enumCs = JpxHeader.ReadU32(data, cs + 3);
                    byte[] ep = Slice(data, cs + 7, ce);
                    if (enumCs == Jp2EnumeratedColourSpaces.CieLab && ep.Length != 0
                        && ep.Length != Jp2CieLabParameters.EncodedLength)
                    {
                        reason = $"colr box {order}: the CIELab EP field is {ep.Length} bytes; T.801 Table M.30 defines {Jp2CieLabParameters.EncodedLength}";
                        return false;
                    }
                    specs.Add(new Jp2ColourSpecification(order, meth, prec, approx, enumCs, ep, null, 0, null));
                    return true;
                }
                case Jp2ColourSpecification.RestrictedIccMethod:
                case Jp2ColourSpecification.AnyIccMethod:
                {
                    long profileLength = length - 3;
                    if (profileLength < IccSignatureEnd)
                    {
                        reason = $"colr box {order} holds a {profileLength}-byte ICC profile, too short to name its data colour space (ICC.1 header offset 16)";
                        return false;
                    }
                    long sig = cs + 3 + 16;
                    string signature = new string(new[] { (char)data[sig], (char)data[sig + 1], (char)data[sig + 2], (char)data[sig + 3] });
                    byte[] profile = null;
                    if (profileLength <= PdfLimits.MaxIccProfileBytes) profile = Slice(data, cs + 3, ce);
                    else Deviations.Add($"colr box {order}: its {profileLength}-byte ICC profile exceeds PdfLimits.MaxIccProfileBytes ({PdfLimits.MaxIccProfileBytes}) and is not kept");
                    specs.Add(new Jp2ColourSpecification(order, meth, prec, approx, -1, Array.Empty<byte>(), profile, profileLength, signature));
                    return true;
                }
                default:
                    // T.800 Table I.9 / T.801 Table M.22: a reader ignores the whole box. Kept so the
                    // file's list is complete; ColourChannels 0 keeps it from ever being chosen.
                    specs.Add(new Jp2ColourSpecification(order, meth, prec, approx, -1, Array.Empty<byte>(), null, 0, null));
                    return true;
            }
        }

        /// <summary>T.800 I.5.3.4, Tables I.12 and I.13.</summary>
        private bool ReadPalette(byte[] data, long cs, long ce, out string reason)
        {
            reason = null;
            if (Palette != null)
            {
                reason = DuplicateBox("Palette");
                return false;
            }
            long length = ce - cs;
            if (length < 3)
            {
                reason = "the Palette box is truncated: NE and NPC need 3 bytes";
                return false;
            }

            int ne = JpxHeader.ReadU16(data, cs);
            int npc = data[cs + 2];
            if (ne < 1 || ne > Jp2Palette.MaxEntries)
            {
                reason = $"the Palette box declares {ne} entries; T.800 Table I.12 allows 1 to {Jp2Palette.MaxEntries}";
                return false;
            }
            if (npc < 1)
            {
                reason = "the Palette box declares 0 columns; T.800 Table I.12 allows 1 to 255";
                return false;
            }
            if (length < 3 + npc)
            {
                reason = $"the Palette box is truncated: it declares {npc} columns but holds {length - 3} depth bytes";
                return false;
            }

            var depths = new int[npc];
            var signed = new bool[npc];
            long bytesPerEntry = 0;
            for (int j = 0; j < npc; j++)
            {
                int b = data[cs + 3 + j];
                depths[j] = (b & 0x7F) + 1;
                signed[j] = (b & 0x80) != 0;
                if (depths[j] > MaxDepth)
                {
                    reason = $"palette column {j} declares {depths[j]}-bit values; T.800 Table I.13 allows 1 to {MaxDepth}";
                    return false;
                }
                bytesPerEntry += (depths[j] + 7) / 8;
            }

            long needed = 3 + npc + ne * bytesPerEntry;
            if (length < needed)
            {
                reason = $"the Palette box is truncated: {ne} entries of {npc} columns need {needed} bytes, it holds {length}";
                return false;
            }
            if (length > needed)
                Deviations.Add($"{length - needed} bytes follow the palette's last entry");

            var values = new long[ne * npc];
            long p = cs + 3 + npc;
            for (int e = 0; e < ne; e++)
            {
                for (int j = 0; j < npc; j++)
                {
                    int bytes = (depths[j] + 7) / 8;
                    long v = 0;
                    for (int k = 0; k < bytes; k++) v = (v << 8) | data[p++];
                    // Table I.12: the value sits in the low-order bits of the padded field.
                    v &= (1L << depths[j]) - 1;
                    if (signed[j] && (v & (1L << (depths[j] - 1))) != 0) v -= 1L << depths[j];
                    values[e * npc + j] = v;
                }
            }
            Palette = new Jp2Palette(ne, npc, depths, signed, values);
            return true;
        }

        /// <summary>T.800 I.5.3.5, Tables I.14 and I.15.</summary>
        private bool ReadComponentMapping(byte[] data, long cs, long ce, out string reason)
        {
            reason = null;
            if (ComponentMapping != null)
            {
                reason = DuplicateBox("Component Mapping");
                return false;
            }
            long length = ce - cs;
            if (length < 4 || length % 4 != 0)
            {
                reason = $"the Component Mapping box holds {length} bytes; T.800 I.5.3.5 lays it out as 4 bytes per channel, at least one";
                return false;
            }
            long count = length / 4;
            if (count > MaxChannels)
            {
                reason = $"the Component Mapping box defines {count} channels, more than the {MaxChannels} components a codestream can have";
                return false;
            }

            var mapping = new Jp2ChannelMapping[count];
            for (int i = 0; i < count; i++)
            {
                long at = cs + 4L * i;
                int cmp = JpxHeader.ReadU16(data, at);
                int mtyp = data[at + 2];
                int pcol = data[at + 3];
                if (mtyp > Jp2ChannelMapping.PaletteMapping)
                {
                    reason = $"cmap channel {i} uses mapping type {mtyp}; T.800 Table I.14 defines 0 (direct) and 1 (palette)";
                    return false;
                }
                mapping[i] = new Jp2ChannelMapping(cmp, mtyp == Jp2ChannelMapping.PaletteMapping, pcol);
            }
            ComponentMapping = mapping;
            return true;
        }

        /// <summary>T.800 I.5.3.6, Tables I.16 to I.19.</summary>
        private bool ReadChannelDefinitions(byte[] data, long cs, long ce, out string reason)
        {
            reason = null;
            if (ChannelDefinitions != null)
            {
                reason = DuplicateBox("Channel Definition");
                return false;
            }
            long length = ce - cs;
            if (length < 2)
            {
                reason = "the Channel Definition box is truncated: N needs 2 bytes";
                return false;
            }
            int n = JpxHeader.ReadU16(data, cs);
            if (n < 1)
            {
                reason = "the Channel Definition box declares 0 descriptions; T.800 Table I.19 allows 1 to 65535";
                return false;
            }
            if (length < 2 + 6L * n)
            {
                reason = $"the Channel Definition box is truncated: {n} descriptions need {2 + 6L * n} bytes, it holds {length}";
                return false;
            }

            var defs = new Jp2ChannelDefinition[n];
            for (int i = 0; i < n; i++)
            {
                long at = cs + 2 + 6L * i;
                int cn = JpxHeader.ReadU16(data, at);
                int typ = JpxHeader.ReadU16(data, at + 2);
                int asoc = JpxHeader.ReadU16(data, at + 4);
                if (typ > Jp2ChannelDefinition.PremultipliedOpacityType && typ != Jp2ChannelDefinition.UnspecifiedType)
                {
                    reason = $"cdef description {i} gives channel {cn} type {typ}; T.800 Table I.16 defines 0 (colour), 1 (opacity), 2 (premultiplied opacity) and 65535 (unspecified)";
                    return false;
                }
                defs[i] = new Jp2ChannelDefinition(cn, typ, asoc);
            }
            ChannelDefinitions = defs;
            return true;
        }

        /// <summary>The checks that need more than one box: <c>bpcc</c> against <c>NC</c>, <c>pclr</c> with <c>cmap</c>, and what <c>cmap</c> and <c>cdef</c> name.</summary>
        private bool CrossCheck(out string reason)
        {
            reason = null;
            if (!HasImageHeader)
                Deviations.Add("no Image Header box (ihdr); T.800 I.5.3.1 requires it first in the JP2 Header box — the codestream's own grid, component count and precision are used");

            // T.800 I.5.3.2: one depth per component, in codestream order, when the depths vary.
            if (_bpcc != null)
            {
                if (HasImageHeader && _bpcc.Length != ComponentCount)
                {
                    reason = $"the Bits Per Component box lists {_bpcc.Length} depths for {ComponentCount} components";
                    return false;
                }
                var depths = new int[_bpcc.Length];
                var signed = new bool[_bpcc.Length];
                for (int c = 0; c < _bpcc.Length; c++)
                {
                    depths[c] = (_bpcc[c] & 0x7F) + 1;
                    signed[c] = (_bpcc[c] & 0x80) != 0;
                    if (depths[c] > MaxDepth)
                    {
                        reason = $"the Bits Per Component box gives component {c} {depths[c]} bits; T.800 Table I.8 allows 1 to {MaxDepth}";
                        return false;
                    }
                }
                ComponentDepths = depths;
                ComponentSigned = signed;
                if (HasImageHeader && _bpc != VaryingDepth)
                    Deviations.Add("a Bits Per Component box is present although the Image Header box gives one depth for every component; the per-component depths are used (T.800 I.5.3.2)");
            }
            else if (HasImageHeader)
            {
                if (_bpc == VaryingDepth)
                {
                    Deviations.Add("the Image Header box says the components vary in depth (BPC 255) but there is no Bits Per Component box; the codestream's depths are used");
                }
                else
                {
                    ComponentDepths = new int[ComponentCount];
                    ComponentSigned = new bool[ComponentCount];
                    for (int c = 0; c < ComponentCount; c++)
                    {
                        ComponentDepths[c] = (_bpc & 0x7F) + 1;
                        ComponentSigned[c] = (_bpc & 0x80) != 0;
                    }
                }
            }

            if (ColourSpecifications.Length == 0)
                Deviations.Add("no Colour Specification box; T.800 I.5.3 requires at least one");

            // T.800 I.5.3.4 / I.5.3.5: a palette needs a mapping to apply it, and a mapping without a
            // palette has nothing to map through.
            if (Palette != null && ComponentMapping == null)
            {
                reason = "a Palette box without a Component Mapping box; T.800 I.5.3.4 requires both or neither";
                return false;
            }
            if (Palette == null && ComponentMapping != null)
            {
                reason = "a Component Mapping box without a Palette box; T.800 I.5.3.5 requires both or neither";
                return false;
            }
            if (ComponentMapping != null)
            {
                for (int i = 0; i < ComponentMapping.Length; i++)
                {
                    Jp2ChannelMapping m = ComponentMapping[i];
                    if (m.ThroughPalette && m.PaletteColumn >= Palette.ColumnCount)
                    {
                        reason = $"cmap channel {i} takes palette column {m.PaletteColumn}; the palette has {Palette.ColumnCount}";
                        return false;
                    }
                    if (HasImageHeader && m.Component >= ComponentCount)
                    {
                        reason = $"cmap channel {i} takes component {m.Component}; the Image Header box declares {ComponentCount}";
                        return false;
                    }
                }
            }

            if (ChannelDefinitions != null)
            {
                // T.800 I.5.3.6: Cn indexes the cmap's channels, or the components when there is no
                // cmap. Whether the associations make sense is the plan's question — it depends on
                // which channels turn out to be colour.
                int channels = ComponentMapping != null ? ComponentMapping.Length : HasImageHeader ? ComponentCount : -1;
                for (int i = 0; i < ChannelDefinitions.Length; i++)
                {
                    Jp2ChannelDefinition def = ChannelDefinitions[i];
                    if (channels >= 0 && def.Channel >= channels)
                    {
                        reason = $"cdef description {i} names channel {def.Channel}; the image has {channels} channels";
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>T.801 M.11.7.6, Table M.33: the first compositing layer's Opacity box, if any.</summary>
        private bool ReadOpacityBox(byte[] data, long layerStart, long layerEnd, out string reason)
        {
            reason = null;
            long pos = layerStart;
            for (int i = 0; i < MaxHeaderBoxes && pos < layerEnd; i++)
            {
                if (!JpxHeader.TryReadBoxHeader(data, pos, out uint type, out long cs, out long ce) || ce > layerEnd)
                {
                    reason = $"the box at offset {pos} inside the Compositing Layer Header box is truncated or runs past the header's end";
                    return false;
                }
                if (type == Jp2BoxTypes.Opct)
                {
                    if (ce - cs < 1)
                    {
                        reason = "the Opacity box is empty; T.801 Table M.34 requires Otyp";
                        return false;
                    }
                    int otyp = data[cs];
                    if (otyp > 2)
                    {
                        reason = $"the Opacity box declares type {otyp}; T.801 Table M.33 defines 0, 1 and 2";
                        return false;
                    }
                    OpacityBoxType = otyp;
                    return true;
                }
                pos = ce;
            }
            return true;
        }

        private static byte[] Slice(byte[] data, long start, long end)
        {
            var slice = new byte[end - start];
            Array.Copy(data, start, slice, 0, slice.Length);
            return slice;
        }

        // ── the PDF consumer's plan ────────────────────────────────────────────

        /// <summary>
        /// Decides what a PDF consumer makes of the image's channels, by ISO 32000-2 §7.4.9 and the
        /// JP2 rules it leans on. False, with the reason, when the file and the dictionary cannot
        /// be reconciled.
        /// </summary>
        /// <remarks>
        /// The rules, each at the site that applies it:
        /// <list type="bullet">
        ///   <item>T.800 I.5.3.1: on a contradiction the codestream's component count and precision
        ///   win over the header's copies (a deviation, not a refusal).</item>
        ///   <item>T.800 I.5.3.5: the channels are the <c>cmap</c>'s outputs, else component
        ///   <i>i</i> is channel <i>i</i>; a palette channel's depth is its column's (I.5.3.3).</item>
        ///   <item>T.800 I.5.3.6: the <c>cdef</c> types each channel and orders the colour channels
        ///   by association (Table I.18: colours 1..<i>n</i>); an undescribed channel keeps the
        ///   default {<i>i</i>, colour, <i>i</i>+1}, and colour channels none of which is numbered
        ///   stay in channel order. T.801 M.11.7.6: an Opacity box makes the last channel opacity
        ///   and displaces the <c>cdef</c> (M.11.7).</item>
        ///   <item>ISO 32000-2 §7.4.9: a <c>/ColorSpace</c> overrides the file's specifications and
        ///   the ordinary-channel count must match its components; an Indexed one reads the index
        ///   samples, so the file's palette is not applied first. Without one, the highest-precedence
        ///   best-approximation specification the consumer supports is used, an unsupported ICC
        ///   profile (or a space the clause excludes: CIEJab, anything outside the baseline) falls
        ///   through to the next, and with none left DeviceGray, DeviceRGB or DeviceCMYK by the
        ///   ordinary-channel count 1, 3 or 4.</item>
        ///   <item>ISO 32000-2 §7.4.9 and Table 87: <c>/SMaskInData</c> 0 ignores every opacity
        ///   channel; 1 or 2 requires exactly one, applying to all colour channels, and 2 means the
        ///   colour samples are premultiplied. A file that declares no opacity channel but has one
        ///   channel more than its colour space needs has that channel taken as the announced one;
        ///   one with no spare channel at all is opaque, with a deviation — the rule the plugin
        ///   path's <c>ImageSampleLayout</c> applies, so the two decoders agree on the file.</item>
        ///   <item>ISO 32000-2 §7.4.9, last bullets: <c>/Decode</c> is ignored without a
        ///   <c>/ColorSpace</c> unless <c>/ImageMask</c> is true, and an image mask's data is one
        ///   1-bit colour channel.</item>
        /// </list>
        /// </remarks>
        /// <param name="codestreamComponents">The codestream's components (SIZ), in codestream order.</param>
        /// <param name="facts">What the image dictionary says.</param>
        /// <param name="iccProfileUsable">
        /// The consumer's say on an ICC profile — whether it can evaluate these bytes — consulted
        /// only for a profile the header itself accepts. Null accepts every profile.
        /// </param>
        /// <param name="plan">The decision.</param>
        /// <param name="reason">Why no plan is possible, when this returns false.</param>
        internal bool TryPlanForPdf(JpxComponentSize[] codestreamComponents, Jp2PdfImageFacts facts,
            Func<byte[], bool> iccProfileUsable, out Jp2PdfChannelPlan plan, out string reason)
        {
            plan = null;
            reason = null;
            if (codestreamComponents == null || codestreamComponents.Length < 1)
            {
                reason = "the codestream declares no components";
                return false;
            }
            int csiz = codestreamComponents.Length;
            var p = new Jp2PdfChannelPlan();

            // T.800 I.5.3.1: the header's copies are redundant, and on a contradiction the codestream's values are used.
            if (HasImageHeader && ComponentCount != csiz)
                p.Deviations.Add($"the Image Header box declares {ComponentCount} components and the codestream {csiz}; the codestream's value is used (T.800 I.5.3.1)");
            if (ComponentDepths != null)
            {
                for (int c = 0; c < ComponentDepths.Length && c < csiz; c++)
                {
                    if (ComponentDepths[c] == codestreamComponents[c].Precision) continue;
                    p.Deviations.Add($"the JP2 header gives component {c} {ComponentDepths[c]} bits and the codestream {codestreamComponents[c].Precision}; the codestream's value is used (T.800 I.5.3.1)");
                    break;
                }
            }

            int smask = facts.SMaskInData;
            if (smask < 0 || smask > 2)
            {
                p.Deviations.Add($"/SMaskInData {smask} is not 0, 1 or 2 (ISO 32000-2 Table 87); treated as 0");
                smask = 0;
            }

            // ISO 32000-2 §7.4.9: an Indexed /ColorSpace's samples are the indices themselves — "the
            // equivalent of an Indexed colour space in PDF syntax" is what the JP2 palette is, and the
            // dictionary's space takes its place. Anything else sees the palette's output.
            bool bypassPalette = Palette != null && facts.ColourSpaceComponents > 0 && facts.ColourSpaceIsIndexed;
            // The same clause for the samples: an Indexed /ColorSpace reads the codestream's values
            // as indices, whatever their depth — never narrowed to 8 bits.
            p.IndicesVerbatim = facts.ColourSpaceComponents > 0 && facts.ColourSpaceIsIndexed;

            // T.800 I.5.3.5: the channels are the cmap's outputs; without a cmap, component i is channel i.
            int n;
            int[] component, column, depth;
            bool[] signed;
            if (ComponentMapping != null && !bypassPalette)
            {
                n = ComponentMapping.Length;
                component = new int[n]; column = new int[n]; depth = new int[n]; signed = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    Jp2ChannelMapping m = ComponentMapping[i];
                    if (m.Component >= csiz)
                    {
                        reason = $"cmap channel {i} takes component {m.Component}; the codestream has {csiz}";
                        return false;
                    }
                    component[i] = m.Component;
                    if (m.ThroughPalette)
                    {
                        // T.800 I.5.3.3: BPC_i of a palette channel is the column's B_j.
                        column[i] = m.PaletteColumn;
                        depth[i] = Palette.ColumnDepths[m.PaletteColumn];
                        signed[i] = Palette.ColumnSigned[m.PaletteColumn];
                        p.PaletteApplied = true;
                    }
                    else
                    {
                        column[i] = -1;
                        depth[i] = codestreamComponents[m.Component].Precision;
                        signed[i] = codestreamComponents[m.Component].IsSigned;
                    }
                }
            }
            else
            {
                if (bypassPalette && csiz != 1)
                {
                    reason = $"the image dictionary's Indexed colour space reads the codestream's index samples directly, which needs one component; the codestream has {csiz}";
                    return false;
                }
                n = csiz;
                component = new int[n]; column = new int[n]; depth = new int[n]; signed = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    component[i] = i;
                    column[i] = -1;
                    depth[i] = codestreamComponents[i].Precision;
                    signed[i] = codestreamComponents[i].IsSigned;
                }
                if (bypassPalette && ChannelDefinitions != null)
                    p.Deviations.Add("the Channel Definition box describes the palette's output channels, which the image dictionary's Indexed colour space bypasses; it is ignored");
            }

            // T.800 I.5.3.6: the default when no box describes a channel — colour, in colour order.
            var type = new int[n];
            var assoc = new int[n];
            for (int i = 0; i < n; i++)
            {
                type[i] = Jp2ChannelDefinition.ColourType;
                assoc[i] = i + 1;
            }
            if (OpacityBoxType == 0 || OpacityBoxType == 1)
            {
                // T.801 Table M.33: the last channel is (premultiplied) opacity for the whole image,
                // every other channel colour with association channel + 1; M.11.7 has the box
                // displace any cdef in the JP2 Header box.
                type[n - 1] = OpacityBoxType == 0 ? Jp2ChannelDefinition.OpacityType : Jp2ChannelDefinition.PremultipliedOpacityType;
                assoc[n - 1] = Jp2ChannelDefinition.WholeImage;
                if (ChannelDefinitions != null)
                    p.Deviations.Add("the Opacity box displaces the Channel Definition box (T.801 M.11.7)");
            }
            else if (OpacityBoxType == 2)
            {
                p.Deviations.Add("the Opacity box specifies a chroma key, which ISO 32000-2 §7.4.9 has a PDF processor ignore");
                if (ChannelDefinitions != null)
                    p.Deviations.Add("the Opacity box displaces the Channel Definition box (T.801 M.11.7)");
            }
            else if (ChannelDefinitions != null && !bypassPalette)
            {
                var described = new bool[n];
                for (int i = 0; i < ChannelDefinitions.Length; i++)
                {
                    Jp2ChannelDefinition def = ChannelDefinitions[i];
                    int c = def.Channel;
                    if (c >= n)
                    {
                        reason = $"cdef description {i} names channel {c}; the image has {n} channels";
                        return false;
                    }
                    if (!described[c])
                    {
                        described[c] = true;
                        type[c] = def.Type;
                        assoc[c] = def.Association;
                    }
                    else if (def.Type != type[c] || def.Type == Jp2ChannelDefinition.ColourType)
                    {
                        // I.5.3.6 allows several descriptions of one channel only to associate an
                        // opacity channel with several colours.
                        reason = $"cdef describes channel {c} twice with different meanings; T.800 I.5.3.6 allows that only for an opacity channel's colours";
                        return false;
                    }
                    else if (assoc[c] == Jp2ChannelDefinition.WholeImage)
                    {
                        assoc[c] = def.Association;
                    }
                }
            }

            // T.800 Table I.18: a colour channel's association numbers its colour, 1..n, and the
            // colour channels are taken in that order. A colour channel whose association is 0 (the
            // whole image) or 65535 (none) has no number; when no colour channel has one the channels
            // are already in colour order (I.5.3.6's default), and when only some have one the order
            // is undefined.
            var colourOrder = new List<int>();
            var opacityChannels = new List<int>();
            int numbered = 0;
            for (int i = 0; i < n; i++)
            {
                switch (type[i])
                {
                    case Jp2ChannelDefinition.ColourType:
                        colourOrder.Add(i);
                        if (assoc[i] != Jp2ChannelDefinition.WholeImage && assoc[i] != Jp2ChannelDefinition.NoAssociation) numbered++;
                        break;
                    case Jp2ChannelDefinition.OpacityType:
                    case Jp2ChannelDefinition.PremultipliedOpacityType:
                        opacityChannels.Add(i);
                        break;
                    default:
                        p.Deviations.Add($"channel {i} has no specified type (cdef Typ 65535) and is not used");
                        break;
                }
            }
            if (numbered > 0)
            {
                if (numbered != colourOrder.Count)
                {
                    reason = $"some colour channels carry a colour association and others none ({JoinAssociations(colourOrder, assoc)}); T.800 Table I.18 numbers every colour 1 to n";
                    return false;
                }
                colourOrder.Sort((a, b) => assoc[a] != assoc[b] ? assoc[a].CompareTo(assoc[b]) : a.CompareTo(b));
                for (int k = 0; k < colourOrder.Count; k++)
                {
                    if (assoc[colourOrder[k]] == k + 1) continue;
                    reason = $"the colour channels are associated with colours {JoinAssociations(colourOrder, assoc)}; T.800 Table I.18 numbers an n-colour space's colours 1 to n";
                    return false;
                }
            }
            int ordinary = colourOrder.Count;

            // The colour space, and how many colour channels it needs (ISO 32000-2 §7.4.9).
            Jp2ColourSpaceKind kind;
            Jp2ColourSpecification chosen = null;
            int target;
            string needs;
            if (facts.ImageMask)
            {
                // §7.4.9: "the JPEG 2000 data shall provide a single colour channel with 1-bit samples".
                if (ordinary != 1)
                {
                    reason = $"/ImageMask true needs a single colour channel; the JPEG 2000 data has {ordinary} (ISO 32000-2 §7.4.9)";
                    return false;
                }
                if (depth[colourOrder[0]] != 1)
                {
                    reason = $"/ImageMask true needs 1-bit samples; channel {colourOrder[0]} has {depth[colourOrder[0]]} bits (ISO 32000-2 §7.4.9)";
                    return false;
                }
                kind = Jp2ColourSpaceKind.StencilMask;
                target = 1;
                needs = "a stencil mask";
            }
            else if (facts.ColourSpaceComponents > 0)
            {
                // §7.4.9: /ColorSpace, when present, determines how the samples are interpreted and
                // the colour space specifications in the JPEG 2000 data are ignored; the number of
                // ordinary colour channels shall match its components.
                kind = Jp2ColourSpaceKind.ImageDictionary;
                target = facts.ColourSpaceComponents;
                needs = $"the image dictionary's {target}-component colour space";
            }
            else
            {
                foreach (Jp2ColourSpecification spec in RankedColourSpecifications())
                {
                    if (IsUsable(spec, ordinary, iccProfileUsable, out string why))
                    {
                        chosen = spec;
                        break;
                    }
                    p.Deviations.Add($"colr box {spec.Order} ({spec.Describe()}) is passed over: {why}");
                }
                if (chosen != null)
                {
                    kind = chosen.IsIcc ? Jp2ColourSpaceKind.IccProfile : Jp2EnumeratedColourSpaces.Kind(chosen.EnumeratedSpace);
                    target = chosen.ColourChannels;
                    needs = $"the file's colour space ({chosen.Describe()})";
                }
                else
                {
                    // §7.4.9: with no supported colour space, DeviceGray, DeviceRGB or DeviceCMYK by
                    // whether the ordinary-channel count is 1, 3 or 4. A file that declares no opacity
                    // channel while /SMaskInData announces one is counted without its last channel,
                    // which is the announced one: four channels are RGB and opacity, not CMYK.
                    int count = ordinary;
                    if (smask != 0 && opacityChannels.Count == 0 && IsDeviceArity(count - 1)) count--;
                    if (!IsDeviceArity(count))
                    {
                        reason = $"no supported colour space in the JPEG 2000 data, and its {ordinary} ordinary channel(s) fit neither DeviceGray, DeviceRGB nor DeviceCMYK (ISO 32000-2 §7.4.9)";
                        return false;
                    }
                    kind = count == 1 ? Jp2ColourSpaceKind.DeviceGray : count == 3 ? Jp2ColourSpaceKind.DeviceRgb : Jp2ColourSpaceKind.DeviceCmyk;
                    target = count;
                    needs = kind.ToString();
                }
            }

            if (ordinary < target)
            {
                reason = $"the JPEG 2000 data has {ordinary} ordinary colour channel(s); {needs} needs {target} (ISO 32000-2 §7.4.9)";
                return false;
            }
            List<int> colour = colourOrder.GetRange(0, target);
            List<int> extras = colourOrder.GetRange(target, ordinary - target);

            // Opacity (ISO 32000-2 §7.4.9; Table 87).
            // §7.4.9: "there shall be only one opacity channel in the JPEG 2000 data and it shall
            // apply to all colour channels" — the one channel a non-zero /SMaskInData can use.
            int wholeImageOpacity = -1, wholeImageCount = 0;
            foreach (int o in opacityChannels)
            {
                if (assoc[o] != Jp2ChannelDefinition.WholeImage) continue;
                wholeImageCount++;
                wholeImageOpacity = o;
            }
            int soleWholeImageOpacity = opacityChannels.Count == 1 && wholeImageCount == 1 ? wholeImageOpacity : -1;

            int opacityChannel = -1;
            if (smask != 0 && opacityChannels.Count == 0 && extras.Count == 0)
            {
                // Announced, but there is no channel it could be. Not a refusal: the image is
                // opaque, which is what the plugin path's ImageSampleLayout makes of the same file —
                // an /SMaskInData with nothing to apply to is ignored, not fatal.
                p.Deviations.Add($"/SMaskInData {smask} announces an opacity channel, but the JPEG 2000 data declares none and has no channel beyond {needs}'s {target}; the image is opaque (ISO 32000-2 §7.4.9 asks for exactly one)");
                smask = 0;
            }
            if (smask != 0)
            {
                if (opacityChannels.Count == 0)
                {
                    opacityChannel = extras[0];
                    extras.RemoveAt(0);
                    p.Deviations.Add($"the JP2 header declares no opacity channel; channel {opacityChannel}, the first past {needs}'s {target}, is taken as the one /SMaskInData {smask} announces");
                }
                else if (soleWholeImageOpacity >= 0)
                {
                    opacityChannel = soleWholeImageOpacity;
                }
                else
                {
                    reason = $"/SMaskInData {smask} requires exactly one opacity channel applying to all colour channels (ISO 32000-2 §7.4.9); the JPEG 2000 data has {opacityChannels.Count}, associated with {JoinAssociations(opacityChannels, assoc)}";
                    return false;
                }
                p.OpacityApplies = true;
                p.OpacityIsPremultiplied = smask == 2;
                bool fileTyped = type[opacityChannel] != Jp2ChannelDefinition.ColourType;
                bool filePremultiplied = type[opacityChannel] == Jp2ChannelDefinition.PremultipliedOpacityType;
                if (fileTyped && filePremultiplied != p.OpacityIsPremultiplied)
                    p.Deviations.Add($"the file types channel {opacityChannel} as {(filePremultiplied ? "premultiplied" : "straight")} opacity and /SMaskInData {smask} says {(p.OpacityIsPremultiplied ? "premultiplied" : "straight")}; the image dictionary's statement is followed (ISO 32000-2 Table 87)");
            }
            else
            {
                // Table 87: 0 means the encoded soft-mask information is ignored. The channel is
                // still named so a consumer leaves it out of the colour samples on purpose.
                opacityChannel = soleWholeImageOpacity;
            }
            if (extras.Count > 0)
                p.Deviations.Add($"channel(s) {JoinChannels(extras)} lie beyond {needs}'s {target} colour channels and are not used");

            var channels = new Jp2Channel[n];
            for (int i = 0; i < n; i++)
                channels[i] = new Jp2Channel(component[i], column[i], depth[i], signed[i], type[i], assoc[i]);
            p.Channels = channels;
            p.ColourChannels = colour.ToArray();
            p.OpacityChannel = opacityChannel;
            p.ColourSpace = kind;
            p.ColourSpecification = chosen;
            // §7.4.9: "If ColorSpace is absent, then the Decode array shall be ignored unless ImageMask is true."
            p.DecodeArrayApplies = facts.ColourSpaceComponents > 0 || facts.ImageMask;
            plan = p;
            return true;
        }

        /// <summary>ISO 32000-2 §7.4.9's device fallback covers 1, 3 and 4 ordinary channels.</summary>
        private static bool IsDeviceArity(int count) => count == 1 || count == 3 || count == 4;

        /// <summary>
        /// Whether a specification can be the image's colour space: a method the reader understands
        /// (T.800 Table I.9, T.801 Table M.22), an enumerated space ISO 32000-2 §7.4.9 admits, an ICC
        /// profile that is kept, recognised and — when the consumer has a say — supported, and an
        /// arity the ordinary channels can supply.
        /// </summary>
        private static bool IsUsable(Jp2ColourSpecification spec, int ordinary, Func<byte[], bool> iccProfileUsable, out string why)
        {
            why = null;
            if (spec.Method == Jp2ColourSpecification.EnumeratedMethod)
            {
                if (spec.ColourChannels == 0)
                {
                    why = spec.EnumeratedSpace == Jp2EnumeratedColourSpaces.CieJab
                        ? "CIEJab (EnumCS 19) is excluded by ISO 32000-2 §7.4.9"
                        : $"EnumCS {spec.EnumeratedSpace} is not a colour space a PDF may use (ISO 32000-2 §7.4.9)";
                    return false;
                }
            }
            else if (spec.IsIcc)
            {
                if (spec.IccProfile == null)
                {
                    why = $"its {spec.IccProfileLength}-byte ICC profile exceeds PdfLimits.MaxIccProfileBytes";
                    return false;
                }
                if (spec.ColourChannels == 0)
                {
                    why = $"its ICC profile's data colour space '{spec.IccDataColourSpace}' is not recognised";
                    return false;
                }
                if (iccProfileUsable != null && !iccProfileUsable(spec.IccProfile))
                {
                    why = "the consumer does not support its ICC profile (ISO 32000-2 §7.4.9: the next lower colour space is used)";
                    return false;
                }
            }
            else
            {
                why = $"METH {spec.Method} is not a method a reader must understand (T.801 Table M.22)";
                return false;
            }

            if (spec.ColourChannels > ordinary)
            {
                why = $"it needs {spec.ColourChannels} colour channels and the JPEG 2000 data has {ordinary}";
                return false;
            }
            return true;
        }

        private static string JoinAssociations(List<int> channels, int[] assoc)
        {
            var parts = new string[channels.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = $"channel {channels[i]}: {assoc[channels[i]]}";
            return string.Join(", ", parts);
        }

        private static string JoinChannels(List<int> channels)
        {
            var parts = new string[channels.Count];
            for (int i = 0; i < parts.Length; i++) parts[i] = channels[i].ToString();
            return string.Join(", ", parts);
        }
    }
}
