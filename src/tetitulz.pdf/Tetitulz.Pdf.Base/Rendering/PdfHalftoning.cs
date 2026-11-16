// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Rendering.Halftones;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Halftoned output for a device that can only lay ink or leave paper (ISO 32000-2 §10.6): a grey page
    /// screened to one bit — a fax, a monochrome printer, <c>tiffg4</c> — and a page's separations screened to one
    /// bit a plate, as a press's plate-setter receives them (<c>tiffsep1</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>The halftone.</b> The page's own <c>/HT</c> when it sets one (Types 1, 5, 6, 10 and 16), else the
    /// device default (<see cref="PdfHalftoneOptions"/>): a Round dot, cyan at 15°, magenta at 75°, yellow at 0°,
    /// black, grey and the spot inks at 45° — the screens of ISO 32000-2's own Type 5 example. The predefined spot
    /// functions are Table 126's PostScript programs, run by the library's Type 4 evaluator.</para>
    /// <para><b>What is approximated, and reported</b> (<see cref="RenderDiagnosticKind.HalftoneApproximated"/>).
    /// A halftone belongs to the graphics state, so a page could screen each paint differently; a page that sets
    /// more than one has the first serve all of it. <c>/AccurateScreens</c> is screened ordinarily, and
    /// <c>/HTO</c>, the halftone origin, is not applied: the screen's phase is the page's top-left pixel. A
    /// halftone this cannot use — malformed, larger than <see cref="PdfLimits.MaxHalftoneCellPixels"/>, or naming a
    /// spot function Table 126 does not have — gives way to the device default.</para>
    /// <para>The input to the screen is the continuous-tone render after transfer functions, in the device's own
    /// components, as §10.6.1 orders it: the plate's ink for a separation (<see cref="PdfSeparationRenderer"/>,
    /// exact where it is), the page's BT.601 luminance for grey.</para>
    /// </remarks>
    public static class PdfHalftoning
    {
        /// <summary>
        /// A page screened to one bit as a grey device does: rendered, reduced to luminance, and halftoned through
        /// the page's grey screen.
        /// </summary>
        /// <param name="input">The PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">The device's resolution — a halftone is defined in its pixels.</param>
        /// <param name="options">The device's default screen and whether the page's own halftone is honoured.</param>
        /// <param name="renderOptions">Render options (the password among them).</param>
        public static PdfBilevelImage RenderPage(Stream input, int pageIndex, int dpi = PdfPageRenderer.DefaultDpi,
            PdfHalftoneOptions options = null, PdfRenderOptions renderOptions = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");
            options ??= new PdfHalftoneOptions();

            return PdfPageRenderer.WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                var halftone = PageHalftone(doc, pageIndex, options);
                using var bitmap = PdfPageRenderer.RenderPageToBitmap(page, doc, dpi, renderOptions);
                byte[] tints = GreyTints(bitmap);
                var screen = Screen(halftone, GreyColorant, options, dpi, out byte[] transfer);
                byte[] bits = screen.Apply(tints, bitmap.Width, bitmap.Height, transfer);
                return new PdfBilevelImage(GreyColorant, bitmap.Width, bitmap.Height, dpi, bits);
            });
        }

        /// <summary>
        /// A page's separations — <see cref="PdfSeparationRenderer"/>'s plates, every process and spot ink — each
        /// screened to one bit through its colorant's screen.
        /// </summary>
        /// <param name="input">The PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="dpi">The device's resolution.</param>
        /// <param name="options">The device's default screens and whether the page's own halftone is honoured.</param>
        /// <param name="renderOptions">Render options (the password among them).</param>
        /// <param name="separationOptions">How the separations convert colour, as for <see cref="PdfSeparationRenderer"/>.</param>
        public static PdfHalftonedSeparations RenderSeparations(Stream input, int pageIndex, int dpi = PdfPageRenderer.DefaultDpi,
            PdfHalftoneOptions options = null, PdfRenderOptions renderOptions = null, PdfSeparationOptions separationOptions = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), "DPI must be positive.");
            options ??= new PdfHalftoneOptions();

            SeparationOutput output = null;
            if (separationOptions?.OutputIccProfile != null)
            {
                output = SeparationOutput.TryCreate(separationOptions.OutputIccProfile, separationOptions.BlackPointCompensation, out string reason);
                if (output == null) throw new ArgumentException("The output ICC profile cannot be used: " + reason, nameof(separationOptions));
            }

            return PdfPageRenderer.WithDecryptedPage(input, pageIndex, renderOptions?.Password, (page, doc) =>
            {
                var halftone = PageHalftone(doc, pageIndex, options);
                var separations = PdfSeparationRenderer.RenderPlates(
                    plate => PdfPageRenderer.RenderPageToBitmap(page, doc, dpi, renderOptions, null, default, plate),
                    dpi, out _, output);
                var plates = new List<PdfBilevelImage>(separations.Plates.Count);
                foreach (var plate in separations.Plates)
                {
                    var screen = Screen(halftone, plate.Colorant, options, dpi, out byte[] transfer);
                    plates.Add(new PdfBilevelImage(plate.Colorant, separations.Width, separations.Height, dpi,
                        screen.Apply(plate.Tints, separations.Width, separations.Height, transfer)));
                }
                return new PdfHalftonedSeparations(separations.Width, separations.Height, dpi, plates);
            });
        }

        /// <summary>The colorant a grey device's screen is looked up under in a Type 5 halftone (§10.6.5.6).</summary>
        internal const string GreyColorant = "Gray";

        /// <summary>
        /// The halftone the page's graphics states set — its first, when it sets several — or null for the device
        /// default, each departure from the page reported.
        /// </summary>
        internal static HalftoneSet PageHalftone(PdfDocument doc, int pageIndex, PdfHalftoneOptions options)
        {
            if (!options.UseDocumentHalftones) return null;
            var named = PageHalftones(doc, pageIndex);
            if (named.Count == 0) return null;
            if (named.Count > 1)
                Report($"the page sets {named.Count} halftones; the first is used for all of it");
            var set = HalftoneSet.Read(doc, named[0], out string problem);
            if (problem != null) Report(problem + (set == null ? "; the device's default halftone is used" : ""));
            if (set != null && set.AccurateScreensRequested) Report("/AccurateScreens is screened ordinarily");
            return set;
        }

        /// <summary>
        /// Every halftone the page's graphics states name, each once, in the order the page's resources are walked
        /// (<see cref="DocumentImageIndex.WalkPage"/>: the page's, its forms', patterns', fonts' and appearances');
        /// the name <c>/Default</c> counts as one, any other name as one.
        /// </summary>
        internal static List<PdfObject> PageHalftones(PdfDocument doc, int pageIndex)
        {
            var found = new List<PdfObject>();
            var seenDictionaries = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            var seenNames = new HashSet<string>(StringComparer.Ordinal);
            DocumentImageIndex.WalkPage(doc, pageIndex, (_, resources) =>
            {
                if (!(doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states)) return;
                foreach (var entry in states)
                {
                    if (!(doc.ResolveObject(entry.Value) is PdfDictionary state) || state[PdfName.HT] == null) continue;
                    switch (doc.ResolveObject(state[PdfName.HT]))
                    {
                        case PdfDictionary dictionary when seenDictionaries.Add(dictionary): found.Add(dictionary); break;
                        case PdfName name when seenNames.Add(name.Value): found.Add(name); break;
                    }
                    if (state[PdfName.HTO] != null) Report("/HTO, the halftone origin, is not applied: the screen's phase is the page's top-left pixel");
                }
            });
            return found;
        }

        /// <summary>
        /// The screen <paramref name="colorant"/> is halftoned through at <paramref name="dpi"/>, and its transfer
        /// table: the page's when it gives one that builds, else the device default.
        /// </summary>
        private static HalftoneScreen Screen(HalftoneSet halftone, string colorant, PdfHalftoneOptions options, int dpi, out byte[] transfer)
        {
            transfer = null;
            HalftoneDefinition definition = null;
            if (halftone != null)
            {
                definition = halftone.For(colorant, out string componentProblem);
                if (componentProblem != null)
                    Report(componentProblem + (definition == null ? "; the device's default halftone is used" : "; the halftone's /Default is used"));
            }
            if (definition != null)
            {
                var screen = definition.Build(dpi, out string problem);
                if (screen != null)
                {
                    transfer = definition.TransferTable();
                    return screen;
                }
                Report($"{colorant}: {problem}; the device's default halftone is used");
            }
            return options.DefaultScreen(colorant, dpi);
        }

        /// <summary>The page's ink per pixel as a grey device sees it: <c>255 −</c> its BT.601 luminance, over white where it is not opaque.</summary>
        private static byte[] GreyTints(SKBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            var tints = new byte[checked(w * h)];
            BitmapPixels.Read(bitmap, (row, y) =>
            {
                int o = y * w;
                for (int x = 0; x < row.Length && x < w; x++)
                {
                    // A straight colour c at alpha a, over white: c·a/255 + 255 − a.
                    SKColor c = row[x];
                    int a = c.Alpha, white = 255 - a;
                    int r = (c.Red * a + 127) / 255 + white, g = (c.Green * a + 127) / 255 + white, b = (c.Blue * a + 127) / 255 + white;
                    int luma = (r * PdfColorSpaceNames.LumaRedPermille + g * PdfColorSpaceNames.LumaGreenPermille
                                + b * PdfColorSpaceNames.LumaBluePermille + PdfColorSpaceNames.LumaScale / 2) / PdfColorSpaceNames.LumaScale;
                    tints[o + x] = (byte)(255 - Math.Min(255, luma));
                }
            });
            return tints;
        }

        private static void Report(string detail) => RenderDiagnostics.Report(RenderDiagnosticKind.HalftoneApproximated, detail);
    }

    /// <summary>The device's own screens, used where a page sets no halftone (or <see cref="UseDocumentHalftones"/> is off).</summary>
    public sealed class PdfHalftoneOptions
    {
        /// <summary>
        /// The default screens' frequency, in cells per inch; 0, the default, for a sixth of the resolution — 50 at
        /// 300 dpi, 100 at 600 — a cell of about 36 pixels and as many grey levels. A positive finite number.
        /// </summary>
        public double Frequency
        {
            get => _frequency;
            init
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                    throw new ArgumentOutOfRangeException(nameof(Frequency), value, "Frequency is 0 for the default, or a positive finite number of cells per inch.");
                _frequency = value;
            }
        }

        private double _frequency;

        /// <summary>
        /// The default screens' spot function: a name from ISO 32000-2's Table 126 (<c>Round</c>, the default;
        /// <c>SimpleDot</c>, <c>Ellipse</c>, <c>Line</c> …).
        /// </summary>
        public string SpotFunction
        {
            get => _spotFunction;
            init
            {
                if (!SpotFunctions.IsPredefined(value))
                    throw new ArgumentException($"'{value}' is not a predefined spot function (ISO 32000-2 Table 126).", nameof(SpotFunction));
                _spotFunction = value;
            }
        }

        private string _spotFunction = "Round";

        /// <summary>Whether a halftone the page sets (<c>/HT</c>) is honoured. True by default.</summary>
        public bool UseDocumentHalftones { get; init; } = true;

        /// <summary>
        /// The device default's screen for <paramref name="colorant"/>: Round (or <see cref="SpotFunction"/>) at the
        /// colorant's angle. A <see cref="Frequency"/> whose cell at <paramref name="dpi"/> is larger than
        /// <see cref="PdfLimits.MaxHalftoneCellPixels"/> is reported and gives way to a sixth of the resolution, a
        /// cell of about 36 pixels at any resolution.
        /// </summary>
        internal HalftoneScreen DefaultScreen(string colorant, int dpi)
        {
            double angle = colorant switch
            {
                PdfColorSpaceNames.CyanColorant => 15,
                PdfColorSpaceNames.MagentaColorant => 75,
                PdfColorSpaceNames.YellowColorant => 0,
                _ => 45,
            };
            var spot = SpotFunctions.Named(_spotFunction);
            if (_frequency > 0)
            {
                var screen = HalftoneScreen.FromSpotFunction(_frequency, angle, dpi, spot, PdfLimits.MaxHalftoneCellPixels);
                if (screen != null) return screen;
                RenderDiagnostics.Report(RenderDiagnosticKind.HalftoneApproximated,
                    $"the default screen's frequency of {_frequency.ToString(System.Globalization.CultureInfo.InvariantCulture)} cells per inch makes a cell at {dpi} dpi larger than PdfLimits.MaxHalftoneCellPixels; a sixth of the resolution is used");
            }
            return HalftoneScreen.FromSpotFunction(dpi / 6.0, angle, dpi, spot, long.MaxValue);
        }
    }

    /// <summary>A one-bit raster — a halftoned page or plate: each pixel ink or paper.</summary>
    public sealed class PdfBilevelImage
    {
        internal PdfBilevelImage(string name, int width, int height, int dpi, byte[] bits)
        {
            Name = name;
            Width = width;
            Height = height;
            Dpi = dpi;
            Bits = bits;
        }

        /// <summary>The colorant: <c>Gray</c> for a grey device's page, a plate's ink for a separation.</summary>
        public string Name { get; }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>The resolution the screens were built at.</summary>
        public int Dpi { get; }

        /// <summary>The pixels, packed: <c>(Width + 7) / 8</c> bytes a row, most significant bit first, 1 where ink is laid.</summary>
        public byte[] Bits { get; }

        /// <summary>Whether ink is laid at (<paramref name="x"/>, <paramref name="y"/>).</summary>
        public bool IsInk(int x, int y) => (Bits[y * ((Width + 7) / 8) + (x >> 3)] & (0x80 >> (x & 7))) != 0;

        /// <summary>The image as a bilevel TIFF (CCITT Group 4, ink black).</summary>
        public byte[] ToTiff() => PdfImageEncoder.EncodeBilevelTiff(new[] { this }, Dpi);

        /// <summary>The image as a binary PBM (<c>P4</c>), ink black.</summary>
        public byte[] ToPbm()
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"P4\n{Width} {Height}\n");
            var pbm = new byte[header.Length + Bits.Length];
            Buffer.BlockCopy(header, 0, pbm, 0, header.Length);
            Buffer.BlockCopy(Bits, 0, pbm, header.Length, Bits.Length);
            return pbm;
        }
    }

    /// <summary>A page's separations screened to one bit a plate.</summary>
    public sealed class PdfHalftonedSeparations
    {
        internal PdfHalftonedSeparations(int width, int height, int dpi, IReadOnlyList<PdfBilevelImage> plates)
        {
            Width = width;
            Height = height;
            Dpi = dpi;
            Plates = plates;
        }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>The resolution the screens were built at.</summary>
        public int Dpi { get; }

        /// <summary>Cyan, Magenta, Yellow, Black, then the spot inks, as <see cref="PdfSeparations.Plates"/> orders them.</summary>
        public IReadOnlyList<PdfBilevelImage> Plates { get; }

        /// <summary>The plate for <paramref name="colorant"/>, or null when the page has none.</summary>
        public PdfBilevelImage this[string colorant]
        {
            get
            {
                foreach (var p in Plates)
                    if (string.Equals(p.Name, colorant, StringComparison.Ordinal)) return p;
                return null;
            }
        }

        /// <summary>Every plate as one multi-page bilevel TIFF (CCITT Group 4), a page a plate, each named for its ink.</summary>
        public byte[] ToTiff() => PdfImageEncoder.EncodeBilevelTiff(Plates, Dpi);
    }
}
