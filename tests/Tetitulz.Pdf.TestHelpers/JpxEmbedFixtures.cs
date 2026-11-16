// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// JP2 files whose headers say what the in-house encoder cannot yet write: an opacity channel
/// declared by a <c>cdef</c> box, and a colour space a PDF cannot name (sYCC). Built from the
/// the reference decoder-written lossless fixtures and <see cref="Jp2Headers"/>, for the embedders' tests —
/// <c>PdfImageToPdf</c>, the image replacer, the Builder — and the dictionary rule they share.
/// </summary>
public static class JpxEmbedFixtures
{
    /// <summary>EnumCS 17 (T.800 Table I.10): greyscale.</summary>
    public const int GreyscaleEnumCs = 17;

    /// <summary>EnumCS 18 (T.800 Table I.10): sYCC — a space a PDF image dictionary cannot name, so the file's own applies.</summary>
    public const int SYccEnumCs = 18;

    /// <summary>
    /// A 40 x 24 greyscale image with an opacity channel: two components, the <c>colr</c> box naming
    /// greyscale and a <c>cdef</c> typing channel 0 as colour and channel 1 as whole-image opacity
    /// (T.800 I.5.3.6). An embedder writes it with <c>/ColorSpace /DeviceGray</c> and <c>/SMaskInData 1</c>.
    /// </summary>
    public static byte[] GreyWithOpacityJp2()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.GrayAlpha40x24_OneLevel;
        return Jp2Headers.Jp2File(f.Codestream,
            Jp2Headers.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7),
            Jp2Headers.ColrEnumerated(GreyscaleEnumCs),
            Jp2Headers.Cdef((0, 0, 1), (1, 1, 0)));
    }

    /// <summary>EnumCS 16 (T.800 Table I.10): sRGB.</summary>
    public const int SRgbEnumCs = 16;

    /// <summary>
    /// The greyscale image with its opacity channel typed <b>premultiplied</b> (cdef Typ 2, whole
    /// image): an embedder writes <c>/SMaskInData 2</c> (ISO 32000-2 Table 89).
    /// </summary>
    public static byte[] GreyWithPremultipliedOpacityJp2()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.GrayAlpha40x24_OneLevel;
        return Jp2Headers.Jp2File(f.Codestream,
            Jp2Headers.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7),
            Jp2Headers.ColrEnumerated(GreyscaleEnumCs),
            Jp2Headers.Cdef((0, 0, 1), (1, 2, 0)));
    }

    /// <summary>
    /// A 40 x 24 sRGB image whose fourth channel is opacity <b>for colour 1 alone</b> (cdef Typ 1,
    /// Asoc 1): a form <c>/SMaskInData</c> cannot express — §7.4.9 wants one channel applying to
    /// all colours — so an embedder writes no key and the file's opacity is left aside.
    /// </summary>
    public static byte[] RgbWithPerColourOpacityJp2()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Rgba40x24_Mct_OneLevel;
        return Jp2Headers.Jp2File(f.Codestream,
            Jp2Headers.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7),
            Jp2Headers.ColrEnumerated(SRgbEnumCs),
            Jp2Headers.Cdef((0, 0, 1), (1, 0, 2), (2, 0, 3), (3, 1, 1)));
    }

    /// <summary>
    /// A 40 x 24 three-component image whose <c>colr</c> box names sYCC: an embedder writes no
    /// <c>/ColorSpace</c>, since naming one would override the file's and repaint it (ISO 32000-2 §7.4.9).
    /// </summary>
    public static byte[] SYccJp2()
    {
        JpxLosslessFixtures.Fixture f = JpxLosslessFixtures.Rgb40x24_NoMct_Blocks8;
        return Jp2Headers.Jp2File(f.Codestream,
            Jp2Headers.Ihdr((uint)f.Height, (uint)f.Width, f.Components, 7),
            Jp2Headers.ColrEnumerated(SYccEnumCs));
    }
}
