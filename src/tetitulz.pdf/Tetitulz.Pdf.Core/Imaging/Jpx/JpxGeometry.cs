// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A rectangle on the reference grid or in a component, sub-band or resolution domain: the upper
    /// left sample at (<see cref="X0"/>, <see cref="Y0"/>) and the lower right at
    /// (<see cref="X1"/> − 1, <see cref="Y1"/> − 1), the convention T.800 Annex B uses throughout.
    /// </summary>
    internal readonly struct JpxRect
    {
        internal JpxRect(long x0, long y0, long x1, long y1)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }

        internal long X0 { get; }
        internal long Y0 { get; }
        internal long X1 { get; }
        internal long Y1 { get; }

        internal long Width => X1 - X0;
        internal long Height => Y1 - Y0;

        public override string ToString() => $"({X0}, {Y0})-({X1}, {Y1})";
    }

    /// <summary>
    /// The geometry T.800 Annex B derives from the SIZ and COD/COC parameters — components, tiles,
    /// tile-components, resolution levels, sub-bands, precincts and code-blocks — as the equations
    /// B-1 to B-18 give it. All arithmetic is in <c>long</c>: every grid value is below 2^32.
    /// </summary>
    internal static class JpxGeometry
    {
        /// <summary>⌈a / b⌉ for b &gt; 0 and any sign of a.</summary>
        internal static long CeilDiv(long a, long b) => -FloorDiv(-a, b);

        /// <summary>⌊a / b⌋ for b &gt; 0 and any sign of a.</summary>
        internal static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        /// <summary>The image area on the reference grid (T.800 equation B-1): (XOsiz, YOsiz) to (Xsiz, Ysiz).</summary>
        internal static JpxRect ImageArea(JpxImageSize siz) => new JpxRect(siz.XOsiz, siz.YOsiz, siz.Xsiz, siz.Ysiz);

        /// <summary>Component <c>c</c>'s samples on its own grid (T.800 equation B-2; B-1 is the image area).</summary>
        internal static JpxRect Component(JpxImageSize siz, int c) => Component(siz, siz.Components[c]);

        /// <summary>Equation B-2 for a component of the given separation on <paramref name="siz"/>'s grid.</summary>
        internal static JpxRect Component(JpxImageSize siz, JpxComponentSize comp) => Component(ImageArea(siz), comp);

        /// <summary>
        /// Equation B-2 for a component of the given separation over <paramref name="area"/> — the
        /// image area, or the image area at a reduced resolution (<see cref="Resolution"/> of it),
        /// where the same ceilings give the component's extent at that resolution: ⌈⌈a / 2^r⌉ / s⌉
        /// and ⌈⌈a / s⌉ / 2^r⌉ are both ⌈a / (s · 2^r)⌉.
        /// </summary>
        internal static JpxRect Component(JpxRect area, JpxComponentSize comp) =>
            new JpxRect(
                CeilDiv(area.X0, comp.XRsiz), CeilDiv(area.Y0, comp.YRsiz),
                CeilDiv(area.X1, comp.XRsiz), CeilDiv(area.Y1, comp.YRsiz));

        /// <summary>
        /// Tile <c>t</c> on the reference grid (T.800 equations B-6 to B-10). The tile index is
        /// raster order, the <c>Isot</c> of a tile-part's SOT.
        /// </summary>
        internal static JpxRect Tile(JpxImageSize siz, int t)
        {
            long p = t % siz.NumXTiles;
            long q = t / siz.NumXTiles;
            return new JpxRect(
                System.Math.Max(siz.XTOsiz + p * siz.XTsiz, siz.XOsiz),
                System.Math.Max(siz.YTOsiz + q * siz.YTsiz, siz.YOsiz),
                System.Math.Min(siz.XTOsiz + (p + 1) * siz.XTsiz, siz.Xsiz),
                System.Math.Min(siz.YTOsiz + (q + 1) * siz.YTsiz, siz.Ysiz));
        }

        /// <summary>A tile's part of component <c>c</c>, in the component's domain (T.800 equation B-12).</summary>
        internal static JpxRect TileComponent(JpxImageSize siz, JpxRect tile, int c)
        {
            var comp = siz.Components[c];
            return new JpxRect(
                CeilDiv(tile.X0, comp.XRsiz), CeilDiv(tile.Y0, comp.YRsiz),
                CeilDiv(tile.X1, comp.XRsiz), CeilDiv(tile.Y1, comp.YRsiz));
        }

        /// <summary>
        /// Resolution level <c>r</c> (0 to <c>levels</c>) of a tile-component with <c>levels</c>
        /// decomposition levels (T.800 equation B-14).
        /// </summary>
        internal static JpxRect Resolution(JpxRect tileComponent, int levels, int r)
        {
            long scale = 1L << (levels - r);
            return new JpxRect(
                CeilDiv(tileComponent.X0, scale), CeilDiv(tileComponent.Y0, scale),
                CeilDiv(tileComponent.X1, scale), CeilDiv(tileComponent.Y1, scale));
        }

        /// <summary>
        /// A sub-band at decomposition level <c>nb</c> (1 to NL, or NL for the NLLL band), with
        /// <c>(xob, yob)</c> from T.800 Table B.1 — (0,0) LL, (1,0) HL, (0,1) LH, (1,1) HH — per
        /// T.800 equation B-15.
        /// </summary>
        internal static JpxRect Subband(JpxRect tileComponent, int nb, int xob, int yob)
        {
            long scale = 1L << nb;
            long half = nb > 0 ? 1L << (nb - 1) : 0;
            return new JpxRect(
                CeilDiv(tileComponent.X0 - half * xob, scale), CeilDiv(tileComponent.Y0 - half * yob, scale),
                CeilDiv(tileComponent.X1 - half * xob, scale), CeilDiv(tileComponent.Y1 - half * yob, scale));
        }

        /// <summary>
        /// The number of precincts across one dimension of a resolution level, with precinct exponent
        /// <c>pp</c> (T.800 equation B-16): 0 when the level is empty in that dimension.
        /// </summary>
        internal static long PrecinctCount(long start, long end, int pp)
        {
            if (end <= start) return 0;
            long size = 1L << pp;
            return CeilDiv(end, size) - FloorDiv(start, size);
        }

        /// <summary>
        /// The code-block exponent at resolution level <c>r</c>: the signalled exponent, bounded by the
        /// precinct exponent — less one above level 0, where a precinct spans a sub-band at half its
        /// resolution-level size (T.800 equations B-17 and B-18).
        /// </summary>
        internal static int CodeBlockExponent(int signalled, int precinctExponent, int r) =>
            System.Math.Min(signalled, r > 0 ? precinctExponent - 1 : precinctExponent);
    }
}
