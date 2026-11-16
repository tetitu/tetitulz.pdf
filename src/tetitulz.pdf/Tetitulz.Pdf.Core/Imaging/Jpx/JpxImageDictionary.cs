// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// What an image dictionary says about a JPEG 2000 file embedded as it is under
    /// <c>/JPXDecode</c> (ISO 32000-2 §7.4.9): the size from the SIZ; <c>/BitsPerComponent</c>
    /// when every component has the one depth a PDF can state (1 to 16), else none; the
    /// <c>/ColorSpace</c> when the file names a device space — sRGB, greyscale or CMYK by its
    /// first <c>colr</c> box, or by component count for a raw codestream, which has no box — and
    /// none when the file's own colour space (sYCC, CIELab, an ICC profile) should apply; and
    /// whether the file declares an opacity channel, which <c>/SMaskInData 1</c> lets a viewer use.
    /// </summary>
    /// <remarks>
    /// One rule for every embedder — <c>PdfImageToPdf</c>, <c>ReplacementImage.Jpeg2000</c> and the
    /// Builder's image decoder — as <c>JpegImageDictionary</c> is for a JPEG, so that the three
    /// cannot disagree about a file. A dictionary that names <c>/ColorSpace</c> overrides the file's
    /// colour specification, so naming one for an sYCC or ICC file would repaint it; leaving it
    /// out is what the standard provides for.
    /// </remarks>
    internal static class JpxImageDictionary
    {
        /// <summary>The largest depth a PDF's <c>/BitsPerComponent</c> states.</summary>
        internal const int MaxStatedBitsPerComponent = 16;

        /// <summary>
        /// Whether <c>/BitsPerComponent</c> may say this depth: ISO 32000-2 Table 87 allows 1, 2, 4,
        /// 8 and 16, and for a <c>JPXDecode</c> image the key is optional — a 12-bit codestream
        /// leaves it out, and the codestream's own precision governs.
        /// </summary>
        internal static bool IsStatableDepth(int bits) => bits == 1 || bits == 2 || bits == 4 || bits == 8 || bits == 16;

        /// <param name="data">The JP2 file or raw codestream.</param>
        /// <param name="width">Xsiz − XOsiz.</param>
        /// <param name="height">Ysiz − YOsiz.</param>
        /// <param name="bitsPerComponent">The one depth every component has, when Table 87 lets the key say it (1, 2, 4, 8, 16), or 0 to leave the key out.</param>
        /// <param name="colorSpace">The device colour space's name, or null to leave the key out.</param>
        /// <param name="smaskInData">The <c>/SMaskInData</c> value the file's declaration wants — <see cref="Jp2ColourDescription.OpacityMode"/>: 1 for one straight whole-image opacity channel, 2 for a premultiplied one, 0 for none or for a form the key cannot express (leave the key out).</param>
        /// <param name="why">Why the file cannot be embedded, when this returns false.</param>
        internal static bool TryDescribe(byte[] data, out int width, out int height, out int bitsPerComponent,
            out string colorSpace, out int smaskInData, out string why)
        {
            width = height = bitsPerComponent = 0;
            colorSpace = null;
            smaskInData = 0;
            why = null;
            if (data == null || !JpxHeader.IsJpx(data))
            {
                why = "not a JPEG 2000 file: neither the JP2 signature nor a codestream's SOC marker opens it";
                return false;
            }
            if (!JpxHeader.TryReadImageSize(data, out long w, out long h, out int components))
            {
                why = "the JPEG 2000 file has no readable SIZ marker segment";
                return false;
            }
            if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue)
            {
                why = $"the JPEG 2000 image is {w}x{h}";
                return false;
            }
            width = (int)w;
            height = (int)h;

            if (JpxHeader.TryReadComponentPrecision(data, out int[] precision, out _) && precision.Length > 0)
            {
                bool uniform = true;
                for (int i = 1; i < precision.Length; i++) uniform &= precision[i] == precision[0];
                if (uniform && IsStatableDepth(precision[0])) bitsPerComponent = precision[0];
            }

            int colourChannels = components;
            if (JpxHeader.IsJp2Container(data))
            {
                if (!Jp2ColourDescription.TryRead(data, out Jp2ColourDescription header, out why)) return false;
                int declared = header.ColourChannelCount();
                if (declared > 0) colourChannels = declared;
                smaskInData = header.OpacityMode();
                if (header.ColourSpecifications.Length > 0)
                {
                    // The colour specification a reader prefers — highest PREC, best APPROX, then file
                    // order (T.801 M.11.7.2; T.800 I.5.3.3 for a JP2, where all are equal and the
                    // first wins) — the same ranking every consumer applies; naming another one in
                    // /ColorSpace would override the file in every viewer.
                    Jp2ColourSpecification spec = header.RankedColourSpecifications()[0];
                    if (spec.Method != Jp2ColourSpecification.EnumeratedMethod) return true;   // an ICC profile, or a method a reader ignores: the file's
                    switch (Jp2EnumeratedColourSpaces.Kind(spec.EnumeratedSpace))
                    {
                        case Jp2ColourSpaceKind.SRgb: colorSpace = PdfColorSpaceNames.DeviceRGB; break;
                        case Jp2ColourSpaceKind.Greyscale: colorSpace = PdfColorSpaceNames.DeviceGray; break;
                        case Jp2ColourSpaceKind.Cmyk: colorSpace = PdfColorSpaceNames.DeviceCMYK; break;
                    }
                    return true;   // any other enumerated space is the file's to describe
                }
            }

            switch (colourChannels)
            {
                case 1: colorSpace = PdfColorSpaceNames.DeviceGray; return true;
                case 3: colorSpace = PdfColorSpaceNames.DeviceRGB; return true;
                case 4: colorSpace = PdfColorSpaceNames.DeviceCMYK; return true;
                default:
                    why = $"the JPEG 2000 file has {colourChannels} colour channels and no colour specification; a PDF names a colour space for 1, 3 or 4";
                    return false;
            }
        }
    }
}
