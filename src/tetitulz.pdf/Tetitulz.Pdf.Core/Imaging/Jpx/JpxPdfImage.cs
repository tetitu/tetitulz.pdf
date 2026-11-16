// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A <c>JPXDecode</c> image as a PDF consumer sees it: the in-house decoder's samples with the
    /// JP2 colour rules of ISO 32000-2 §7.4.9 applied — the colour channels in colour order, a
    /// palette expanded, sYCC converted to RGB, the opacity channel split off when
    /// <c>/SMaskInData</c> asks for it — every sample narrowed to 8 bits by the one rule
    /// (<see cref="JpxDecodedImage.NarrowTo8Bit(long, int, bool)"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>The pixel half of <see cref="Jp2ColourDescription.TryPlanForPdf"/>.</b> The plan
    /// says which decoded channel is which; this reads the samples it names. Nothing here touches
    /// SkiaSharp or a PDF dictionary: the facts the dictionary supplies arrive as
    /// <see cref="Jp2PdfImageFacts"/>, and the result is a byte buffer with a
    /// <see cref="JpxColourModel"/> saying what it holds. The one conversion that needs no colour
    /// management — sYCC to RGB (T.800 Annex I's sYCC is IEC 61966-2-1's YCC, the ICT's own
    /// constants) — happens here; CIELab and an ICC profile are handed on with their parameters,
    /// because converting them is a colour-managed consumer's job.</para>
    /// <para><b>A JP2 container whose header cannot be read is not refused.</b> The codestream
    /// decoded; only the colour boxes are unusable, so the image is read as a bare codestream
    /// (device colour by channel count) and the header's refusal is recorded as a deviation. A
    /// consumer that must not guess reads <see cref="Deviations"/>.</para>
    /// </remarks>
    internal sealed class JpxPdfImage
    {
        private JpxPdfImage(int width, int height, int nativeWidth, int nativeHeight, int reduction,
            int colourComponents, JpxColourModel colourModel, byte[] colour,
            byte[] opacity, bool opacityIsPremultiplied, byte[] iccProfile, double[] labRanges, double[] labOffsets,
            bool decodeArrayApplies, Jp2PdfChannelPlan plan, List<string> deviations, WorkBudget budget)
        {
            Width = width;
            Height = height;
            NativeWidth = nativeWidth;
            NativeHeight = nativeHeight;
            Reduction = reduction;
            ColourComponents = colourComponents;
            ColourModel = colourModel;
            Colour = colour;
            Opacity = opacity;
            OpacityIsPremultiplied = opacityIsPremultiplied;
            IccProfile = iccProfile;
            LabRanges = labRanges;
            LabOffsets = labOffsets;
            DecodeArrayApplies = decodeArrayApplies;
            Plan = plan;
            Deviations = deviations;
            Budget = budget;
        }

        /// <summary>The width of <see cref="Colour"/>: the image area's (T.800 §B.2, what <c>/Width</c> is) at <see cref="Reduction"/> 0, ⌈Xsiz / 2^r⌉ − ⌈XOsiz / 2^r⌉ at reduction r.</summary>
        internal int Width { get; }

        /// <summary>The height of <see cref="Colour"/>, likewise.</summary>
        internal int Height { get; }

        /// <summary>The image area's width, Xsiz − XOsiz — what the codestream is, whatever it was decoded at.</summary>
        internal int NativeWidth { get; }

        /// <summary>The image area's height, Ysiz − YOsiz.</summary>
        internal int NativeHeight { get; }

        /// <summary>How many decomposition levels short of the codestream's resolution the samples are: the reduction asked for, clamped to the fewest levels any tile-component has.</summary>
        internal int Reduction { get; }

        /// <summary>Samples per pixel in <see cref="Colour"/>: the plan's colour-channel count.</summary>
        internal int ColourComponents { get; }

        /// <summary>What <see cref="Colour"/> holds.</summary>
        internal JpxColourModel ColourModel { get; }

        /// <summary>
        /// The colour samples, 8 bits each, interleaved pixel by pixel in colour order (T.800 Table
        /// I.18), row-major. For <see cref="JpxColourModel.Stencil"/> one byte per pixel holding 0
        /// or 1, the mask bit.
        /// </summary>
        internal byte[] Colour { get; }

        /// <summary>One opacity byte per pixel when <c>/SMaskInData</c> is 1 or 2 and the file has the channel; null otherwise.</summary>
        internal byte[] Opacity { get; }

        /// <summary><c>/SMaskInData 2</c>: <see cref="Colour"/> is premultiplied by <see cref="Opacity"/> (ISO 32000-2 Table 87).</summary>
        internal bool OpacityIsPremultiplied { get; }

        /// <summary>The <c>colr</c> box's ICC profile when the plan chose one; <see cref="Colour"/> is then in its data colour space, with <see cref="ColourModel"/> naming its arity.</summary>
        internal byte[] IccProfile { get; }

        /// <summary>
        /// For <see cref="JpxColourModel.Lab"/>: the R<sub>L</sub>, R<sub>a</sub>, R<sub>b</sub> of
        /// T.801 equation M-18, so that L* = (N − O) · R / 255 over the 8-bit sample N and
        /// <see cref="LabOffsets"/>' O.
        /// </summary>
        internal double[] LabRanges { get; }

        /// <summary>For <see cref="JpxColourModel.Lab"/>: the offsets of M-18, rescaled to the 8-bit samples (a signed channel's level shift folded in).</summary>
        internal double[] LabOffsets { get; }

        /// <summary>Whether the image dictionary's <c>/Decode</c> applies (ISO 32000-2 §7.4.9: only with <c>/ColorSpace</c> or <c>/ImageMask</c>).</summary>
        internal bool DecodeArrayApplies { get; }

        /// <summary>The channel plan the samples follow.</summary>
        internal Jp2PdfChannelPlan Plan { get; }

        /// <summary>Everything tolerated on the way: the decoder's, the JP2 header's and the plan's.</summary>
        internal List<string> Deviations { get; }

        /// <summary>The decode's work budget; its <see cref="WorkBudget.Spent"/> is what the decode cost.</summary>
        internal WorkBudget Budget { get; }

        /// <summary>The degree the decode ran at (<see cref="JpxDecodeResult.Threads"/>): the caller's <c>maxThreads</c>, or the automatic degree for 0.</summary>
        internal int Threads { get; private set; }

        /// <summary>
        /// Decodes <paramref name="data"/> — a JP2 file or a raw codestream — and applies the colour
        /// rules under <paramref name="facts"/>. False with the reason when the codestream cannot be
        /// decoded or the file and the dictionary cannot be reconciled (the plan's refusals).
        /// </summary>
        /// <param name="data">The image stream's bytes after every other filter.</param>
        /// <param name="facts">What the image dictionary says (see <see cref="Jp2PdfImageFacts"/>).</param>
        /// <param name="iccProfileUsable">The consumer's say on an ICC profile from a <c>colr</c> box; null accepts every recognised one.</param>
        /// <param name="reduction">
        /// Decomposition levels to stop short of the full resolution — 0 for the codestream's own
        /// size; more is clamped to what the codestream has (<see cref="Reduction"/> says what was
        /// done, <see cref="Width"/> and <see cref="Height"/> what it made). Every stage after the
        /// wavelet — the component transformation, the JP2 colour rules, the narrowing — runs on
        /// the reduced samples, so a reduced decode costs about 4^r less than a full one.
        /// </param>
        /// <param name="image">The samples and their meaning.</param>
        /// <param name="reason">Why the image could not be produced, when this returns false.</param>
        /// <param name="maxThreads">The decode's thread budget (<see cref="JpxDecoder.TryDecode"/>): 1 for the calling thread, N for at most N, 0 for the automatic degree.</param>
        internal static bool TryDecode(byte[] data, Jp2PdfImageFacts facts, Func<byte[], bool> iccProfileUsable, int reduction,
            out JpxPdfImage image, out string reason, int maxThreads = 0)
        {
            image = null;
            reason = null;
            if (data == null || data.Length < 4)
            {
                reason = "the image stream carries no JPEG 2000 data";
                return false;
            }
            if (!JpxDecoder.TryDecode(data, reduction, out JpxDecodeResult result, out reason, maxThreads)) return false;
            JpxDecodedImage decoded = result.Image;
            if (decoded == null)
            {
                reason = "the decoder assembled no image";
                return false;
            }

            var deviations = new List<string>(result.Deviations);
            Jp2ColourDescription header = ReadHeader(data, deviations);
            if (!header.TryPlanForPdf(decoded.Components, facts, iccProfileUsable, out Jp2PdfChannelPlan plan, out reason))
                return false;
            deviations.AddRange(plan.Deviations);

            int width = decoded.Width, height = decoded.Height;
            long area = (long)width * height;
            int n = plan.ColourChannels.Length;
            long bytes = area * (n + (plan.OpacityApplies ? 1 : 0));
            if (bytes > PdfLimits.MaxDecompressedBytes)
            {
                reason = $"the image is {width}x{height} with {n} colour channel(s), {bytes} bytes of samples; PdfLimits.MaxDecompressedBytes is {PdfLimits.MaxDecompressedBytes}";
                return false;
            }

            var sources = new ChannelSource[n];
            for (int k = 0; k < n; k++) sources[k] = ChannelSource.For(decoded, header, plan.Channels[plan.ColourChannels[k]]);

            JpxColourModel model = ModelOf(plan);
            var colour = new byte[area * n];
            double[] labRanges = null, labOffsets = null;
            switch (plan.ColourSpace)
            {
                case Jp2ColourSpaceKind.StencilMask:
                    // §7.4.9: one 1-bit channel; the bit is what the narrowing rule makes of it.
                    for (long i = 0; i < area; i++) colour[i] = sources[0].Narrow(i) >= 128 ? (byte)1 : (byte)0;
                    break;
                case Jp2ColourSpaceKind.SYcc:
                    ConvertSYccToRgb(sources[0], sources[1], sources[2], colour, width, height);
                    break;
                case Jp2ColourSpaceKind.ESYcc:
                    ConvertESYccToRgb(sources[0], sources[1], sources[2], colour, area);
                    break;
                case Jp2ColourSpaceKind.ESRgb:
                    ConvertESRgbToRgb(sources, colour, area);
                    break;
                case Jp2ColourSpaceKind.RommRgb:
                    ConvertRommRgbToRgb(sources[0], sources[1], sources[2], colour, area);
                    break;
                case Jp2ColourSpaceKind.CieLab:
                    LabParameters(plan, sources, out labRanges, out labOffsets);
                    Interleave(sources, colour, area);
                    break;
                default:
                    if (plan.IndicesVerbatim) InterleaveIndices(sources, colour, area);
                    else Interleave(sources, colour, area);
                    break;
            }

            byte[] opacity = null;
            if (plan.OpacityApplies)
            {
                ChannelSource o = ChannelSource.For(decoded, header, plan.Channels[plan.OpacityChannel]);
                opacity = new byte[area];
                for (long i = 0; i < area; i++) opacity[i] = o.Narrow(i);
            }

            byte[] icc = plan.ColourSpace == Jp2ColourSpaceKind.IccProfile ? plan.ColourSpecification.IccProfile : null;
            JpxImageSize grid = result.Codestream.Size;
            image = new JpxPdfImage(width, height, (int)(grid.Xsiz - grid.XOsiz), (int)(grid.Ysiz - grid.YOsiz), decoded.Reduction,
                n, model, colour, opacity, plan.OpacityIsPremultiplied, icc,
                labRanges, labOffsets, plan.DecodeArrayApplies, plan, deviations, result.Budget)
            { Threads = result.Threads };
            return true;
        }

        /// <summary>The JP2 header's description, or a bare codestream's when there is none or it cannot be read (a deviation, not a refusal — see the class remarks).</summary>
        private static Jp2ColourDescription ReadHeader(byte[] data, List<string> deviations)
        {
            if (!JpxHeader.IsJp2Container(data)) return Jp2ColourDescription.ForRawCodestream();
            if (Jp2ColourDescription.TryRead(data, out Jp2ColourDescription header, out string why))
            {
                foreach (string d in header.Deviations) deviations.Add("JP2 header: " + d);
                return header;
            }
            deviations.Add($"JP2 header: {why}; the file's colour boxes are ignored and the codestream's components are read directly");
            return Jp2ColourDescription.ForRawCodestream();
        }

        /// <summary>What the colour samples are once the conversions here have run.</summary>
        private static JpxColourModel ModelOf(Jp2PdfChannelPlan plan)
        {
            switch (plan.ColourSpace)
            {
                case Jp2ColourSpaceKind.ImageDictionary: return JpxColourModel.ImageDictionary;
                case Jp2ColourSpaceKind.StencilMask: return JpxColourModel.Stencil;
                case Jp2ColourSpaceKind.Greyscale:
                case Jp2ColourSpaceKind.DeviceGray: return JpxColourModel.Gray;
                case Jp2ColourSpaceKind.Cmyk:
                case Jp2ColourSpaceKind.DeviceCmyk: return JpxColourModel.Cmyk;
                case Jp2ColourSpaceKind.CieLab: return JpxColourModel.Lab;
                case Jp2ColourSpaceKind.IccProfile:
                    // The profile's data colour space fixed the channel count (IccDataColourSpaces).
                    return plan.ColourComponentCount == 1 ? JpxColourModel.Gray
                        : plan.ColourComponentCount == 4 ? JpxColourModel.Cmyk
                        : JpxColourModel.Rgb;
                default:
                    // sRGB as it is; sYCC, e-sYCC, e-sRGB and ROMM-RGB were converted to sRGB above.
                    return JpxColourModel.Rgb;
            }
        }

        private static void Interleave(ChannelSource[] sources, byte[] colour, long area)
        {
            int n = sources.Length;
            for (int k = 0; k < n; k++)
            {
                ChannelSource s = sources[k];
                long at = k;
                for (long i = 0; i < area; i++, at += n) colour[at] = s.Narrow(i);
            }
        }

        /// <summary>
        /// sYCC to RGB (T.800 Annex I: IEC 61966-2-1's YCC), through the ICT's own arithmetic —
        /// <see cref="JpxComponentTransforms.InverseIct"/> on the centred values, then the
        /// rounding, level shift and clip of G.1.2 at the luma's depth — a row at a time, so the
        /// constants have one home and a large image needs no second set of planes. The chroma was
        /// up-sampled by replication when the decoder placed it on the grid.
        /// </summary>
        private static void ConvertSYccToRgb(ChannelSource y, ChannelSource cb, ChannelSource cr, byte[] colour, int width, int height)
        {
            int depth = y.Depth;
            double half = (double)(1L << (depth - 1));
            long max = JpxComponentTransforms.MaxSample(depth, false);
            double cbScale = Math.Pow(2, depth - cb.Depth), crScale = Math.Pow(2, depth - cr.Depth);
            double cbHalf = (double)(1L << (cb.Depth - 1)), crHalf = (double)(1L << (cr.Depth - 1));
            var yf = new double[width];
            var cbf = new double[width];
            var crf = new double[width];
            for (int row = 0; row < height; row++)
            {
                long o = (long)row * width;
                for (int x = 0; x < width; x++)
                {
                    yf[x] = y.Unsigned(o + x) - half;
                    cbf[x] = (cb.Unsigned(o + x) - cbHalf) * cbScale;
                    crf[x] = (cr.Unsigned(o + x) - crHalf) * crScale;
                }
                JpxComponentTransforms.InverseIct(yf, cbf, crf);
                for (int x = 0; x < width; x++)
                {
                    long at = (o + x) * 3;
                    colour[at] = JpxDecodedImage.NarrowTo8Bit(SaturatingCast.ToLong(JpxComponentTransforms.RoundAndClamp(yf[x] + half, 0, max)), depth, false);
                    colour[at + 1] = JpxDecodedImage.NarrowTo8Bit(SaturatingCast.ToLong(JpxComponentTransforms.RoundAndClamp(cbf[x] + half, 0, max)), depth, false);
                    colour[at + 2] = JpxDecodedImage.NarrowTo8Bit(SaturatingCast.ToLong(JpxComponentTransforms.RoundAndClamp(crf[x] + half, 0, max)), depth, false);
                }
            }
        }

        /// <summary>e-sRGB's code values to sRGB samples, channel by channel (<see cref="Jp2ExtendedColour.ESRgbToSRgb8"/>).</summary>
        private static void ConvertESRgbToRgb(ChannelSource[] sources, byte[] colour, long area)
        {
            int n = sources.Length;
            for (int k = 0; k < n; k++)
            {
                ChannelSource s = sources[k];
                long at = k;
                for (long i = 0; i < area; i++, at += n) colour[at] = Jp2ExtendedColour.ESRgbToSRgb8(s.Unsigned(i), s.Depth);
            }
        }

        /// <summary>e-sYCC's luma and centred chroma to sRGB samples (<see cref="Jp2ExtendedColour.ESYccToSRgb8"/>); the chroma was up-sampled by replication when the decoder placed it on the grid.</summary>
        private static void ConvertESYccToRgb(ChannelSource y, ChannelSource cb, ChannelSource cr, byte[] colour, long area)
        {
            for (long i = 0; i < area; i++)
            {
                Jp2ExtendedColour.ESYccToSRgb8(y.Unsigned(i), cb.Unsigned(i), cr.Unsigned(i), y.Depth, cb.Depth, cr.Depth, out byte r, out byte g, out byte b);
                long at = i * 3;
                colour[at] = r;
                colour[at + 1] = g;
                colour[at + 2] = b;
            }
        }

        /// <summary>ROMM-RGB's code values to sRGB samples (<see cref="Jp2ExtendedColour.RommRgbToSRgb8"/>), at the red channel's depth — the three share one in a ROMM file, and a channel of another depth is rescaled to it first.</summary>
        private static void ConvertRommRgbToRgb(ChannelSource r, ChannelSource g, ChannelSource b, byte[] colour, long area)
        {
            int depth = r.Depth;
            double gScale = Math.Pow(2, depth - g.Depth), bScale = Math.Pow(2, depth - b.Depth);
            for (long i = 0; i < area; i++)
            {
                Jp2ExtendedColour.RommRgbToSRgb8(r.Unsigned(i), g.Unsigned(i) * gScale, b.Unsigned(i) * bScale, depth, out byte sr, out byte sg, out byte sb);
                long at = i * 3;
                colour[at] = sr;
                colour[at + 1] = sg;
                colour[at + 2] = sb;
            }
        }

        /// <summary>
        /// T.801 M.11.7.4.1: the EP field's ranges and offsets, or Table M.29bis's defaults, with
        /// each offset rescaled to the 8-bit sample the consumer holds — N<sub>8</sub> stands for
        /// N · 255 / (2^n − 1) with a signed channel's level shift added, so
        /// (N − O) · R / (2^n − 1) becomes (N<sub>8</sub> − O<sub>8</sub>) · R / 255.
        /// </summary>
        private static void LabParameters(Jp2PdfChannelPlan plan, ChannelSource[] sources, out double[] ranges, out double[] offsets)
        {
            Jp2CieLabParameters p;
            if (!Jp2CieLabParameters.TryParse(plan.ColourSpecification.EnumeratedParameters, out p))
                p = Jp2CieLabParameters.Default(sources[1].Depth, sources[1].Signed, sources[2].Depth, sources[2].Signed);
            ranges = new[] { (double)p.RangeL, p.RangeA, p.RangeB };
            offsets = new[] { Offset8(p.OffsetL, sources[0]), Offset8(p.OffsetA, sources[1]), Offset8(p.OffsetB, sources[2]) };
        }

        private static double Offset8(long offset, ChannelSource s)
        {
            double shift = s.Signed ? (double)(1L << (s.Depth - 1)) : 0;
            double max = Math.Pow(2, s.Depth) - 1;
            return (offset + shift) * 255.0 / max;
        }

        /// <summary>An Indexed dictionary space's samples are the indices themselves, whatever the component's depth: each value as it is, clamped to a byte (a palette has at most 256 entries, ISO 32000-2 §8.6.6.3), never narrowed.</summary>
        private static void InterleaveIndices(ChannelSource[] sources, byte[] colour, long area)
        {
            int n = sources.Length;
            for (long i = 0; i < area; i++)
                for (int k = 0; k < n; k++)
                {
                    long v = sources[k].ValueAt(i);
                    colour[i * n + k] = v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;
                }
        }

        /// <summary>Where one plan channel's samples come from: a component plane, read directly or through a palette column.</summary>
        private readonly struct ChannelSource
        {
            private readonly int[] _plane;
            private readonly long[] _wide;
            private readonly Jp2Palette _palette;
            private readonly int _column;

            private ChannelSource(int[] plane, long[] wide, Jp2Palette palette, int column, int depth, bool signed)
            {
                _plane = plane;
                _wide = wide;
                _palette = palette;
                _column = column;
                Depth = depth;
                Signed = signed;
            }

            /// <summary>Bits per sample of what <see cref="ValueAt"/> returns: the palette column's, else the component's.</summary>
            internal int Depth { get; }

            internal bool Signed { get; }

            internal static ChannelSource For(JpxDecodedImage decoded, Jp2ColourDescription header, Jp2Channel channel) =>
                new ChannelSource(decoded.IsWide(channel.Component) ? null : decoded.Plane(channel.Component),
                    decoded.IsWide(channel.Component) ? decoded.WidePlane(channel.Component) : null,
                    channel.PaletteColumn >= 0 ? header.Palette : null, channel.PaletteColumn, channel.Depth, channel.IsSigned);

            /// <summary>The sample at <paramref name="i"/>: the component's value, or the palette entry it indexes (T.800 I.5.3.4; an index past the table takes its last entry).</summary>
            internal long ValueAt(long i)
            {
                long s = _wide != null ? _wide[i] : _plane[i];
                if (_column < 0) return s;
                int entry = s < 0 ? 0 : s >= _palette.EntryCount ? _palette.EntryCount - 1 : (int)s;
                return _palette.Value(entry, _column);
            }

            /// <summary>The sample with a signed channel's level shift added, so 0 is black whatever the sign.</summary>
            internal double Unsigned(long i) => Signed ? ValueAt(i) + (double)(1L << (Depth - 1)) : ValueAt(i);

            /// <summary>The sample narrowed by the one rule, at the channel's depth (a palette column's may exceed the component's).</summary>
            internal byte Narrow(long i) => JpxDecodedImage.NarrowTo8Bit(ValueAt(i), Depth, Signed);
        }
    }
}
