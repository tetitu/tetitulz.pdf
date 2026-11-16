// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>What the colour samples of a decoded <c>JPXDecode</c> image hold.</summary>
    public enum JpxColourModel
    {
        /// <summary>The image dictionary's <c>/ColorSpace</c> governs: the samples are its components, as many per pixel as it has (ISO 32000-2 §7.4.9).</summary>
        ImageDictionary,
        /// <summary>One grey sample per pixel.</summary>
        Gray,
        /// <summary>Red, green and blue — sRGB, or an RGB space taken as such; sYCC already converted.</summary>
        Rgb,
        /// <summary>Cyan, magenta, yellow and black (EnumCS 12, or four device channels).</summary>
        Cmyk,
        /// <summary>CIE L*, a*, b* encoded per T.801 M.11.7.4.1; the consumer converts.</summary>
        Lab,
        /// <summary><c>/ImageMask true</c>: one byte per pixel holding the mask bit, 0 or 1.</summary>
        Stencil,
    }

    /// <summary>What a <c>JPXDecode</c> image's dictionary says, as ISO 32000-2 §7.4.9 reads it.</summary>
    public readonly struct JpxImageDictionaryFacts
    {
        /// <summary>A dictionary saying nothing: no <c>/ColorSpace</c>, <c>/SMaskInData</c> 0, not an image mask.</summary>
        public static readonly JpxImageDictionaryFacts None = default;

        /// <param name="colourSpaceComponents">The component count of <c>/ColorSpace</c>, or 0 when the entry is absent.</param>
        /// <param name="colourSpaceIsIndexed">True when <c>/ColorSpace</c> is Indexed, whose samples are the indices a JP2 palette would otherwise consume.</param>
        /// <param name="smaskInData"><c>/SMaskInData</c>: 0 (or absent), 1 or 2.</param>
        /// <param name="imageMask"><c>/ImageMask</c>.</param>
        public JpxImageDictionaryFacts(int colourSpaceComponents, bool colourSpaceIsIndexed, int smaskInData, bool imageMask)
        {
            ColourSpaceComponents = colourSpaceComponents;
            ColourSpaceIsIndexed = colourSpaceIsIndexed;
            SMaskInData = smaskInData;
            ImageMask = imageMask;
        }

        /// <summary>The component count of <c>/ColorSpace</c>, or 0 when the entry is absent.</summary>
        public int ColourSpaceComponents { get; }

        /// <summary>True when <c>/ColorSpace</c> is an Indexed space.</summary>
        public bool ColourSpaceIsIndexed { get; }

        /// <summary><c>/SMaskInData</c>: 0, 1 or 2 (ISO 32000-2 Table 87).</summary>
        public int SMaskInData { get; }

        /// <summary><c>/ImageMask</c>.</summary>
        public bool ImageMask { get; }

        internal Jp2PdfImageFacts ToInternal() => new Jp2PdfImageFacts(ColourSpaceComponents, ColourSpaceIsIndexed, SMaskInData, ImageMask);
    }

    /// <summary>The samples of a decoded <c>JPXDecode</c> image and what they mean, from <see cref="JpxImageDecoding.TryDecode"/>.</summary>
    public sealed class JpxImagePixels
    {
        internal JpxImagePixels(JpxPdfImage image)
        {
            Width = image.Width;
            Height = image.Height;
            ColourComponents = image.ColourComponents;
            ColourModel = image.ColourModel;
            Colour = image.Colour;
            Opacity = image.Opacity;
            OpacityIsPremultiplied = image.OpacityIsPremultiplied;
            IccProfile = image.IccProfile;
            DecodeArrayApplies = image.DecodeArrayApplies;
            Deviations = image.Deviations.AsReadOnly();
        }

        /// <summary>The image's width in samples (<c>Xsiz − XOsiz</c>).</summary>
        public int Width { get; }

        /// <summary>The image's height in samples.</summary>
        public int Height { get; }

        /// <summary>Samples per pixel in <see cref="Colour"/>.</summary>
        public int ColourComponents { get; }

        /// <summary>What <see cref="Colour"/> holds.</summary>
        public JpxColourModel ColourModel { get; }

        /// <summary>The colour samples, 8 bits each, interleaved pixel by pixel in colour order, row-major.</summary>
        public byte[] Colour { get; }

        /// <summary>One opacity byte per pixel when <c>/SMaskInData</c> was 1 or 2 and the file carries the channel; null otherwise.</summary>
        public byte[] Opacity { get; }

        /// <summary><c>/SMaskInData 2</c>: the colour samples are premultiplied by the opacity.</summary>
        public bool OpacityIsPremultiplied { get; }

        /// <summary>The <c>colr</c> box's ICC profile when the file's colour is defined by one; <see cref="Colour"/> is then in its data colour space.</summary>
        public byte[] IccProfile { get; }

        /// <summary>Whether the image dictionary's <c>/Decode</c> applies (ISO 32000-2 §7.4.9: only with <c>/ColorSpace</c> or <c>/ImageMask</c>).</summary>
        public bool DecodeArrayApplies { get; }

        /// <summary>Everything tolerated on the way — the decoder's, the JP2 header's and the channel plan's irregularities.</summary>
        public IReadOnlyList<string> Deviations { get; }
    }

    /// <summary>
    /// The in-house JPEG 2000 decoder for a consumer outside <c>Tetitulz.Pdf.Core</c>'s
    /// <c>InternalsVisibleTo</c>: one decode that applies the JP2 colour rules a PDF consumer
    /// must (ISO 32000-2 §7.4.9), and the one rule that narrows a sample to 8 bits.
    /// </summary>
    /// <remarks>
    /// <b>Public because a plugin needs it.</b> Core's <c>InternalsVisibleTo</c> names no plugin:
    /// a decode plugin narrows its samples by <see cref="NarrowTo8Bit(long, int, bool)"/> so the two
    /// decoders render a 12-bit image alike, and <c>Plugins.Maintenance</c> decodes through
    /// <see cref="TryDecode"/>. Inside the core graph, <c>Base</c> reaches the same code as
    /// <c>JpxPdfImage</c>, with its own say on which ICC profiles it can evaluate; this facade
    /// accepts every profile the JP2 header recognises and hands the bytes on.
    /// </remarks>
    public static class JpxImageDecoding
    {
        /// <summary>
        /// A sample narrowed to 8 bits: a deeper one shifted right by <paramref name="precision"/> − 8,
        /// a shallower one scaled by 255 / (2^precision − 1), an 8-bit one taken as is, each clipped to
        /// 0–255; a signed sample is first offset by 2^(precision − 1), so its minimum is black.
        /// </summary>
        /// <param name="value">The sample, within the component's range.</param>
        /// <param name="precision">Bits per sample, 1 to 38, counting a sign bit.</param>
        /// <param name="isSigned">Whether the component is signed.</param>
        public static byte NarrowTo8Bit(int value, int precision, bool isSigned) => NarrowTo8Bit((long)value, precision, isSigned);

        /// <summary>The same rule for a sample deeper than an <c>int</c> holds — T.800 allows 38 bits.</summary>
        public static byte NarrowTo8Bit(long value, int precision, bool isSigned)
        {
            JpxComponentTransforms.RequirePrecision(precision);
            return JpxDecodedImage.NarrowTo8Bit(value, precision, isSigned);
        }

        /// <summary>
        /// Decodes a JP2 file or a raw codestream to 8-bit samples under ISO 32000-2 §7.4.9's colour
        /// rules. False with the reason when the codestream cannot be decoded or the file and the
        /// dictionary cannot be reconciled.
        /// </summary>
        /// <param name="data">The image stream's bytes after every other filter.</param>
        /// <param name="facts">What the image dictionary says; <see cref="JpxImageDictionaryFacts.None"/> for a bare decode.</param>
        /// <param name="pixels">The samples and their meaning.</param>
        /// <param name="reason">Why nothing was decoded, when this returns false.</param>
        public static bool TryDecode(byte[] data, JpxImageDictionaryFacts facts, out JpxImagePixels pixels, out string reason)
        {
            pixels = null;
            if (!JpxPdfImage.TryDecode(data, facts.ToInternal(), null, 0, out JpxPdfImage image, out reason)) return false;
            pixels = new JpxImagePixels(image);
            return true;
        }
    }
}
