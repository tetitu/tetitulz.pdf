// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>One component's line in the SIZ marker segment (T.800 Table A.11).</summary>
    internal sealed class JpxComponentSize
    {
        internal JpxComponentSize(int precision, bool isSigned, int xrsiz, int yrsiz)
        {
            Precision = precision;
            IsSigned = isSigned;
            XRsiz = xrsiz;
            YRsiz = yrsiz;
        }

        /// <summary>Bits per sample, 1–38, counting the sign bit (<c>Ssiz</c> + 1).</summary>
        internal int Precision { get; }

        /// <summary>True when <c>Ssiz</c>'s sign bit is set.</summary>
        internal bool IsSigned { get; }

        /// <summary>Horizontal sample separation on the reference grid, 1–255.</summary>
        internal int XRsiz { get; }

        /// <summary>Vertical sample separation on the reference grid, 1–255.</summary>
        internal int YRsiz { get; }
    }

    /// <summary>
    /// The SIZ marker segment (T.800 §A.5.1): the reference grid, the image area on it, the tiling,
    /// and each component's precision and sub-sampling. <b>The one SIZ reader in the library</b> —
    /// <see cref="JpxHeader"/> and the codestream parser both read through it.
    /// </summary>
    /// <remarks>
    /// <para><see cref="TryRead"/> checks the segment's own structure: its length agrees with its
    /// component count, and every per-component value is in range. <see cref="TryValidateGrid"/>
    /// checks what the geometry needs as well (T.800 equations B-3 and B-4), and the codestream
    /// parser calls both. <see cref="JpxHeader"/>'s precision query calls only the first, so a
    /// header whose grid is inconsistent can still say how deep its samples are.</para>
    /// </remarks>
    internal sealed class JpxImageSize
    {
        /// <summary>Bytes of SIZ parameters before the first component's <c>Ssiz</c>: Lsiz, Rsiz, the
        /// eight grid and tile values, and Csiz.</summary>
        internal const int FixedLength = 2 + 2 + (4 * 8) + 2;

        /// <summary>The largest <c>Csiz</c> T.800 Table A.9 allows.</summary>
        internal const int MaxComponents = 16384;

        /// <summary>The largest tile count an <c>Isot</c> can address (T.800 Table A.5: 0 to 65 534).</summary>
        internal const long MaxAddressableTiles = 65535;

        private JpxImageSize() { }

        /// <summary>Capabilities (T.800 Table A.10; T.801 Table A.2 when bit 15 is set).</summary>
        internal int Rsiz { get; private set; }

        internal long Xsiz { get; private set; }
        internal long Ysiz { get; private set; }
        internal long XOsiz { get; private set; }
        internal long YOsiz { get; private set; }
        internal long XTsiz { get; private set; }
        internal long YTsiz { get; private set; }
        internal long XTOsiz { get; private set; }
        internal long YTOsiz { get; private set; }

        internal JpxComponentSize[] Components { get; private set; }

        /// <summary>Tiles across (T.800 equation B-5). Zero until <see cref="TryValidateGrid"/> succeeds.</summary>
        internal long NumXTiles { get; private set; }

        /// <summary>Tiles down (T.800 equation B-5). Zero until <see cref="TryValidateGrid"/> succeeds.</summary>
        internal long NumYTiles { get; private set; }

        /// <summary><see cref="NumXTiles"/> × <see cref="NumYTiles"/>.</summary>
        internal long NumTiles => NumXTiles * NumYTiles;

        /// <summary>
        /// Reads the SIZ marker segment whose <c>Lsiz</c> field is at <c>lsiz</c>, within
        /// <c>[lsiz, end)</c>. False, with the reason, when the segment is truncated, its length
        /// disagrees with its component count, or a per-component value is out of range.
        /// </summary>
        internal static bool TryRead(byte[] data, long lsiz, long end, out JpxImageSize size, out string reason)
        {
            size = null;
            reason = null;
            if (data == null || lsiz < 0 || lsiz + FixedLength > end || end > data.Length)
            {
                reason = "the SIZ marker segment is truncated";
                return false;
            }

            int length = ReadU16(data, lsiz);
            int csiz = ReadU16(data, lsiz + FixedLength - 2);
            if (csiz < 1 || csiz > MaxComponents)
            {
                reason = $"SIZ declares {csiz} components; T.800 allows 1 to {MaxComponents}";
                return false;
            }
            if (length != FixedLength + 3 * csiz)
            {
                reason = $"SIZ is {length} bytes long but declares {csiz} components, which need {FixedLength + 3 * csiz}";
                return false;
            }
            if (lsiz + length > end)
            {
                reason = "the SIZ marker segment is truncated";
                return false;
            }

            var s = new JpxImageSize
            {
                Rsiz = ReadU16(data, lsiz + 2),
                Xsiz = ReadU32(data, lsiz + 4),
                Ysiz = ReadU32(data, lsiz + 8),
                XOsiz = ReadU32(data, lsiz + 12),
                YOsiz = ReadU32(data, lsiz + 16),
                XTsiz = ReadU32(data, lsiz + 20),
                YTsiz = ReadU32(data, lsiz + 24),
                XTOsiz = ReadU32(data, lsiz + 28),
                YTOsiz = ReadU32(data, lsiz + 32),
                Components = new JpxComponentSize[csiz],
            };

            for (int c = 0; c < csiz; c++)
            {
                long at = lsiz + FixedLength + 3L * c;
                int ssiz = data[at];
                int xr = data[at + 1];
                int yr = data[at + 2];
                int precision = (ssiz & 0x7F) + 1;
                if (precision > 38)
                {
                    reason = $"component {c} declares {precision}-bit samples; T.800 allows 1 to 38";
                    return false;
                }
                if (xr < 1 || yr < 1)
                {
                    reason = $"component {c} declares a sample separation of 0; T.800 allows 1 to 255";
                    return false;
                }
                s.Components[c] = new JpxComponentSize(precision, (ssiz & 0x80) != 0, xr, yr);
            }

            size = s;
            return true;
        }

        /// <summary>
        /// Checks the grid the geometry is computed from: a non-empty image area, a non-empty tile, the
        /// tile offset no greater than the image offset and the first tile reaching into the image
        /// (T.800 equations B-3 and B-4), and a tile count <c>Isot</c> can address and
        /// <see cref="Constants.PdfLimits.MaxJpxTiles"/> permits. Sets <see cref="NumXTiles"/> and
        /// <see cref="NumYTiles"/>.
        /// </summary>
        internal bool TryValidateGrid(out string reason)
        {
            reason = null;
            if (Xsiz <= XOsiz || Ysiz <= YOsiz)
            {
                reason = $"the image area is empty: the grid is {Xsiz}x{Ysiz} and the image starts at ({XOsiz}, {YOsiz})";
                return false;
            }
            if (XTsiz < 1 || YTsiz < 1)
            {
                reason = $"the tile size {XTsiz}x{YTsiz} is empty";
                return false;
            }
            if (XTOsiz > XOsiz || YTOsiz > YOsiz)
            {
                reason = $"the tile offset ({XTOsiz}, {YTOsiz}) lies past the image offset ({XOsiz}, {YOsiz}) (T.800 B-3)";
                return false;
            }
            if (XTsiz + XTOsiz <= XOsiz || YTsiz + YTOsiz <= YOsiz)
            {
                reason = "the first tile holds no part of the image area (T.800 B-4)";
                return false;
            }

            for (int c = 0; c < Components.Length; c++)
            {
                // Equation B-2: a sub-sampled component may have no sample inside the image area.
                JpxComponentSize k = Components[c];
                if (JpxGeometry.CeilDiv(XOsiz, k.XRsiz) >= JpxGeometry.CeilDiv(Xsiz, k.XRsiz)
                    || JpxGeometry.CeilDiv(YOsiz, k.YRsiz) >= JpxGeometry.CeilDiv(Ysiz, k.YRsiz))
                {
                    reason = $"component {c} has no samples: at a separation of {k.XRsiz}x{k.YRsiz} none falls inside the image area (T.800 B-2)";
                    return false;
                }
            }

            long across = JpxGeometry.CeilDiv(Xsiz - XTOsiz, XTsiz);
            long down = JpxGeometry.CeilDiv(Ysiz - YTOsiz, YTsiz);
            long tiles = across * down;   // each factor is below 2^32, so the product fits a long
            long ceiling = System.Math.Min(MaxAddressableTiles, Constants.PdfLimits.MaxJpxTiles);
            if (tiles > ceiling)
            {
                reason = $"the codestream declares {tiles} tiles ({across}x{down}); the limit is {ceiling}";
                return false;
            }

            NumXTiles = across;
            NumYTiles = down;
            return true;
        }

        private static long ReadU32(byte[] d, long i) => JpxHeader.ReadU32(d, i);

        private static int ReadU16(byte[] d, long i) => JpxHeader.ReadU16(d, i);
    }
}
