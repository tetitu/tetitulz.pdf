// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Renders a page as colour separations: one plate per colorant — Cyan, Magenta, Yellow and
    /// Black, then every spot ink the page paints with — each a raster of ink amounts, as a
    /// separation device produces them.
    /// </summary>
    /// <remarks>
    /// <para><b>What is exact.</b> Paths, text, stencil masks, tiling patterns, shadings and shading
    /// patterns (sampled from their own components), and images — XObject or inline, JPEG included
    /// (read from their samples) — in DeviceCMYK, DeviceGray, or a <c>/Separation</c> or
    /// <c>/DeviceN</c> space: each colorant lands on its own plate at its own tint, a spot ink is a
    /// plate of its own rather than its alternate's CMYK, <c>/All</c> marks every plate and
    /// <c>/None</c> none. Overprint is the specification's (§11.7.4): a paint leaves the plates its
    /// colour space does not name alone when it overprints, and knocks them out when it does not.
    /// Transparency groups, soft masks, clipping and the separable blend modes work as in an
    /// ordinary render, blending on <c>1 − tint</c> as §11.3.5 has a subtractive space blend.
    /// Checked against an independent separation renderer to within one level.</para>
    ///
    /// <para><b>What is approximated, and reported.</b> Codec-decoded JPEG 2000 images, and images in
    /// RGB, a CIE-based space or <c>/Indexed</c> without an output profile, reach the plates through
    /// their sRGB pixels — the inverse of the naive CMYK conversion, no ink on a spot plate — and a
    /// transfer function and the non-separable blend modes are not applied per plate. Each raises
    /// <see cref="RenderDiagnosticKind.SeparationApproximated"/> once per plate and is counted on
    /// <see cref="PdfSeparationPlate.ApproximatedPaints"/>. RGB and the CIE-based spaces convert by
    /// that naive inverse in vector paints too, unless <see cref="PdfSeparationOptions.OutputIccProfile"/>
    /// names a CMYK output profile.</para>
    ///
    /// <para>The plate passes are also what overprint simulation recombines
    /// (<see cref="OverprintPreview"/>).</para>
    /// </remarks>
    public static class PdfSeparationRenderer
    {
        /// <summary>Renders one page's separations.</summary>
        /// <param name="input">The PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">Output resolution.</param>
        /// <param name="renderOptions">Render options; the background is ignored (a plate starts with no ink).</param>
        public static PdfSeparations RenderPage(Stream input, int pageIndex, int dpi = PdfPageRenderer.DefaultDpi,
            PdfRenderOptions renderOptions = null)
            => RenderPage(input, pageIndex, dpi, renderOptions, System.Threading.CancellationToken.None);

        /// <summary>Renders one page's separations, with cancellation.</summary>
        public static PdfSeparations RenderPage(Stream input, int pageIndex, int dpi,
            PdfRenderOptions renderOptions, System.Threading.CancellationToken cancellationToken)
            => RenderPage(input, pageIndex, dpi, renderOptions, null, cancellationToken);

        /// <summary>
        /// Renders one page's separations under <paramref name="separationOptions"/> — through a CMYK
        /// output profile when it names one.
        /// </summary>
        /// <exception cref="ArgumentException">The output profile is not a CMYK profile this can convert into; the message says why.</exception>
        public static PdfSeparations RenderPage(Stream input, int pageIndex, int dpi,
            PdfRenderOptions renderOptions, PdfSeparationOptions separationOptions,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");

            SeparationOutput output = null;
            if (separationOptions?.OutputIccProfile != null)
            {
                output = SeparationOutput.TryCreate(separationOptions.OutputIccProfile, separationOptions.BlackPointCompensation, out string reason);
                if (output == null) throw new ArgumentException("The output ICC profile cannot be used: " + reason, nameof(separationOptions));
            }

            return RenderPage(input, pageIndex, dpi, renderOptions, output, out _, cancellationToken);
        }

        /// <summary>
        /// One page's separations, and the spot inks the render found with the colour space each was
        /// painted in — what the transparency flattener needs to describe a spot plate's ink in CMYK.
        /// </summary>
        internal static PdfSeparations RenderPage(Stream input, int pageIndex, int dpi,
            PdfRenderOptions renderOptions, SeparationOutput output, out SpotColorantList spots,
            System.Threading.CancellationToken cancellationToken = default)
        {
            SpotColorantList found = null;
            var separations = PdfPageRenderer.WithDecryptedPage(input, pageIndex, renderOptions?.Password,
                (page, doc) =>
                {
                    var plates = RenderPlates(
                        plate => PdfPageRenderer.RenderPageToBitmap(page, doc, dpi, renderOptions, null, cancellationToken, plate),
                        dpi, out found, output);
                    foreach (string name in found.Names)
                    {
                        var plate = plates[name];
                        if (plate != null) plate.Display = Display(doc, found.SpaceOf(name), name);
                    }
                    return plates;
                });
            spots = found;
            return separations;
        }

        /// <summary>
        /// A spot ink's colour at full tint, as a viewer of the separations shows the ink (<see cref="PdfSeparations.ToPsd"/>):
        /// its space's tint transform through the alternate, or null — reported — when nothing says.
        /// </summary>
        private static SKColor? Display(Core.Document.PdfDocument doc, Core.Objects.PdfArray space, string name)
        {
            var ink = OverprintPreview.Ink(doc, space, name);
            if (ink != null)
            {
                try { return ink(255); }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.SeparationApproximated,
                        $"Spot ink {name}: its tint transform did not evaluate, so its plate carries no colour to be shown in (a PSD shows it black).", ex);
                    return null;
                }
            }
            RenderDiagnostics.Report(RenderDiagnosticKind.SeparationApproximated,
                $"Spot ink {name}: no colour space names it with a tint transform, so its plate carries no colour to be shown in (a PSD shows it black).", null);
            return null;
        }

        /// <summary>
        /// The plate passes — the separation render's and the overprint preview's
        /// (<see cref="OverprintPreview"/>) alike, a whole page's or a tile's: <paramref name="pass"/>
        /// renders one plate. The process passes discover the spot inks into
        /// <paramref name="spots"/>; each is then a pass of its own. A spot seen only in a later pass
        /// is impossible: every pass walks the same content.
        /// </summary>
        internal static PdfSeparations RenderPlates(Func<SeparationPlate, SKBitmap> pass, float dpi,
            out SpotColorantList spots, SeparationOutput output = null)
        {
            var found = new SpotColorantList();
            var plates = new List<PdfSeparationPlate>();
            int width = 0, height = 0;

            void Render(string colorant)
            {
                var plate = new SeparationPlate(colorant, found, output);
                using var bitmap = pass(plate);
                width = bitmap.Width;
                height = bitmap.Height;
                plates.Add(new PdfSeparationPlate(colorant, Tints(bitmap), plate.ApproximatedPaints));
            }

            foreach (var colorant in SeparationPlate.Process) Render(colorant);
            // Each spot plate is a whole render of the page and a page-sized plate kept, and the page chooses how
            // many inks it names: past the cap the rest have no plate, and that is said.
            int kept = Math.Min(found.Names.Count, Math.Max(0, PdfLimits.MaxSpotPlatesPerPage));
            for (int i = 0; i < kept; i++) Render(found.Names[i]);
            if (found.Names.Count > kept)
                RenderDiagnostics.Report(RenderDiagnosticKind.SpotPlatesCapped,
                    $"The page names {found.Names.Count} spot inks, more than PdfLimits.MaxSpotPlatesPerPage ({kept}); " +
                    "the inks past it have no plate of their own.", null);
            spots = found;
            return new PdfSeparations(width, height, SaturatingCast.ToInt(Math.Round(dpi)), plates);
        }

        /// <summary>Renders one page's separations from a file.</summary>
        public static PdfSeparations RenderPage(string inputPath, int pageIndex, int dpi = PdfPageRenderer.DefaultDpi,
            PdfRenderOptions renderOptions = null)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return RenderPage(fs, pageIndex, dpi, renderOptions);
        }

        /// <summary>
        /// A plate surface's ink: its grey is <c>1 − tint</c> and it is opaque throughout (cleared
        /// white, composited over), so the red channel alone carries the tint and premultiplied is
        /// straight.
        /// </summary>
        private static byte[] Tints(SKBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height, stride = bitmap.RowBytes;
            var tints = new byte[checked(w * h)];
            ReadOnlySpan<byte> px = bitmap.GetPixelSpan();
            for (int y = 0; y < h; y++)
            {
                int row = y * stride, outRow = y * w;
                for (int x = 0; x < w; x++)
                    tints[outRow + x] = (byte)(255 - px[row + 4 * x]);
            }
            return tints;
        }
    }

    /// <summary>How a separation render converts colour.</summary>
    public sealed class PdfSeparationOptions
    {
        /// <summary>
        /// A CMYK output profile (an ICC <c>prtr</c> profile with <c>B2A</c> tables, such as a press
        /// characterisation). Colours without CMYK components of their own — RGB, CIE-based,
        /// <c>/ICCBased</c> RGB, and images in those spaces — reach the process plates through its
        /// table for each paint's rendering intent instead of the naive inverse of the CMYK
        /// conversion. DeviceGray stays on the black plate alone, and DeviceCMYK, spot and DeviceN
        /// colours keep their own components. Null: no profile.
        /// </summary>
        public byte[] OutputIccProfile { get; init; }

        /// <summary>
        /// Black-point compensation into the output profile (Adobe's
        /// method): source black lands on the profile's black rather than being clipped to it. On by
        /// default, as is usual; it changes nothing under the perceptual intent of
        /// a profile whose perceptual black is already L* 0, and is never applied to absolute
        /// colorimetric.
        /// </summary>
        public bool BlackPointCompensation { get; init; } = true;
    }

    /// <summary>A page's separations: every plate at one size.</summary>
    public sealed class PdfSeparations
    {
        internal PdfSeparations(int width, int height, int dpi, IReadOnlyList<PdfSeparationPlate> plates)
        {
            Width = width;
            Height = height;
            Dpi = dpi;
            Plates = plates;
        }

        /// <summary>
        /// Every plate as one separated TIFF (TIFF 6.0 §16: PhotometricInterpretation 5, 8 bits per
        /// ink, PackBits): InkSet CMYK when the plates are exactly the four process colorants — what
        /// any CMYK reader opens — and otherwise the N-ink form with each plate's name in InkNames.
        /// </summary>
        public byte[] ToTiff()
        {
            var inks = new List<(string, byte[])>(Plates.Count);
            foreach (var p in Plates) inks.Add((p.Colorant, p.Tints));
            return PdfImageEncoder.EncodeSeparatedTiff(Width, Height, inks, Dpi);
        }

        /// <summary>
        /// Every plate as one Photoshop document in CMYK mode: the
        /// process plates as its CMYK channels and each spot ink as a spot channel named for the ink and shown in
        /// its colour at full tint. PackBits-compressed, 8 bits a channel.
        /// </summary>
        /// <exception cref="Core.PdfException">The plates are larger than a PSD's 30 000 pixels a side, or there
        /// are more than 52 spot inks (a PSD holds 56 channels), or the resolution is past the 32 767 dpi a
        /// PSD records.</exception>
        public byte[] ToPsd()
        {
            var inks = new List<(string, byte[], SKColor?)>(Plates.Count);
            foreach (var p in Plates) inks.Add((p.Colorant, p.Tints, p.Display));
            return PdfImageEncoder.EncodePsd(Width, Height, inks, Dpi);
        }

        /// <summary>The resolution the plates were rendered at.</summary>
        public int Dpi { get; }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Cyan, Magenta, Yellow, Black, then the spot inks in the order the page first paints with them.</summary>
        public IReadOnlyList<PdfSeparationPlate> Plates { get; }

        /// <summary>The plate for <paramref name="colorant"/>, or null when the page has none.</summary>
        public PdfSeparationPlate this[string colorant]
        {
            get
            {
                foreach (var p in Plates)
                    if (string.Equals(p.Colorant, colorant, StringComparison.Ordinal)) return p;
                return null;
            }
        }
    }

    /// <summary>One colorant's plate.</summary>
    public sealed class PdfSeparationPlate
    {
        internal PdfSeparationPlate(string colorant, byte[] tints, int approximatedPaints)
        {
            Colorant = colorant;
            Tints = tints;
            ApproximatedPaints = approximatedPaints;
        }

        /// <summary>The colorant: <c>Cyan</c>, <c>Magenta</c>, <c>Yellow</c>, <c>Black</c>, or a spot ink's name.</summary>
        public string Colorant { get; }

        /// <summary>
        /// Ink per pixel, row-major with no padding: 0 is none, 255 full. (A separation TIFF
        /// stores <c>255 −</c> this, as BlackIsZero grey, so a plate prints dark.)
        /// </summary>
        public byte[] Tints { get; }

        /// <summary>
        /// How many paints reached this plate through their sRGB colour rather than their own
        /// components (<see cref="RenderDiagnosticKind.SeparationApproximated"/>); 0 means every
        /// paint on the plate was exact.
        /// </summary>
        public int ApproximatedPaints { get; }

        /// <summary>A spot ink's sRGB at full tint, what <see cref="PdfSeparations.ToPsd"/> shows its channel in; null for a process plate or an ink nothing gives a colour.</summary>
        internal SKColor? Display { get; set; }
    }
}
