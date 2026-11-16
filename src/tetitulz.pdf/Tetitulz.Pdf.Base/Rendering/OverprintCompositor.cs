// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The overprint rules both renderers read: which process colorants a colorant set names
    /// (§11.7.4.5), which overprinting images leave colorants alone (§8.6.7), and the exact inverse of
    /// the naive CMYK conversion a separation plate reaches sRGB colour through.
    /// </summary>
    /// <remarks>
    /// <para><b>No compositing lives here.</b> The raster renderer's simulation is the page's
    /// separation plates recombined (<see cref="OverprintPreview"/>), which composites every case per
    /// colorant. The SVG writer keeps its own per-paint approximation and reads the
    /// colorant-set mapping below.</para>
    /// </remarks>
    internal static class OverprintCompositor
    {
        /// <summary>Channel bits for <c>channels</c> arguments: which colorants a paint writes.</summary>
        internal const int CyanChannel    = 1 << 0;
        /// <inheritdoc cref="CyanChannel"/>
        internal const int MagentaChannel = 1 << 1;
        /// <inheritdoc cref="CyanChannel"/>
        internal const int YellowChannel  = 1 << 2;
        /// <inheritdoc cref="CyanChannel"/>
        internal const int BlackChannel   = 1 << 3;
        /// <summary>Every process colorant — a paint that preserves nothing.</summary>
        internal const int AllChannels    = CyanChannel | MagentaChannel | YellowChannel | BlackChannel;

        /// <summary>
        /// <c>channels</c> value meaning "not a colorant set": the source was set in DeviceCMYK,
        /// and §11.7.4.2's nonzero overprint mode decides per component instead.
        /// </summary>
        internal const int DeviceCmykRule = 0;

        /// <summary>
        /// Reads a <c>/Separation</c> or <c>/DeviceN</c> colorant set as DeviceCMYK components
        /// plus the channels it writes, when every named colorant is a process colorant
        /// (§8.6.6.4: Cyan, Magenta, Yellow, Black) or the reserved <c>/All</c>.
        /// </summary>
        /// <param name="tints">The named colorants and their tints, as the colour operator set them.</param>
        /// <param name="cmyk">The four components, with every unnamed colorant at 0.</param>
        /// <param name="channels">The named colorants as channel bits — what the paint writes.</param>
        /// <returns>
        /// False when any colorant is a spot ink. A spot ink is a fifth plate, and laying it down
        /// over a CMYK backdrop while leaving all four process plates unchanged is a thing a
        /// four-channel compositing model cannot express; that needs a separation device
        /// (GAPS §11a), and the caller reports it rather than approximating.
        /// </returns>
        /// <remarks>
        /// <b>The one mapping, for both renderers.</b> The raster path uses the components and the
        /// mask to composite; the SVG writer uses the mask to decide whether its multiply
        /// approximation applies. A second copy of "which names are process colorants" would be a
        /// second place for the two to disagree about one file's ink.
        /// </remarks>
        internal static bool TryProcessColorants(
            System.Collections.Generic.IReadOnlyDictionary<string, float> tints,
            out float[] cmyk, out int channels)
        {
            cmyk = null;
            channels = DeviceCmykRule;
            if (tints == null || tints.Count == 0) return false;

            var components = new float[4];
            int written = 0;
            foreach (var kv in tints)
            {
                float t = Clamp01(kv.Value);
                switch (kv.Key)
                {
                    case Core.Constants.PdfColorSpaceNames.CyanColorant:
                        components[0] = t; written |= CyanChannel; break;
                    case Core.Constants.PdfColorSpaceNames.MagentaColorant:
                        components[1] = t; written |= MagentaChannel; break;
                    case Core.Constants.PdfColorSpaceNames.YellowColorant:
                        components[2] = t; written |= YellowChannel; break;
                    case Core.Constants.PdfColorSpaceNames.BlackColorant:
                        components[3] = t; written |= BlackChannel; break;
                    case Core.Constants.PdfColorSpaceNames.AllColorant:
                        // §8.6.6.4: the tint applies to every colorant the device has.
                        components[0] = components[1] = components[2] = components[3] = t;
                        written |= AllChannels; break;
                    default:
                        return false;
                }
            }

            cmyk = components;
            channels = written;
            return true;
        }

        /// <summary>
        /// Does an overprinting image leave some colorant of the backdrop alone? Only when its colour
        /// space is a colorant set — <c>/Separation</c> or <c>/DeviceN</c>, or an <c>/Indexed</c>
        /// space over one — naming some colorants and not all: §11.7.4.5 marks exactly the named
        /// ones. Every other image writes all four process colorants, because §8.6.7's non-zero
        /// overprint mode "shall not, however, apply to the painting of images" — so on a composite
        /// its overprint is already the ordinary draw. Both renderers report the first case and
        /// only it.
        /// </summary>
        /// <remarks>
        /// <c>/All</c> names every colorant, and a DeviceN of all four process colorants writes all
        /// four, so neither leaves anything alone. An independent separation renderer agrees on both
        /// halves: a DeviceCMYK image under <c>/OPM 1</c> knocks black out under a zero K sample,
        /// and a Separation-Cyan image keeps the magenta beneath it.
        /// </remarks>
        internal static bool ImageLeavesColorantsAlone(PdfDictionary image, PdfDocument doc)
        {
            if (image == null) return false;
            var space = PdfColor.ResolveSpace(image[PdfName.ColorSpace] ?? image[PdfName.CS], doc);
            if (space.Family == Core.Constants.PdfColorSpaceNames.Indexed && space.Array != null && space.Array.Count > 1)
                space = PdfColor.ResolveSpace(space.Array[1], doc);
            if (space.Family != Core.Constants.PdfColorSpaceNames.Separation
                && space.Family != Core.Constants.PdfColorSpaceNames.DeviceN) return false;
            if (space.Array == null || space.Array.Count < 2) return false;

            PdfObject names = doc?.ResolveObject(space.Array[1]) ?? space.Array[1];
            var named = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            if (names is PdfName single) named.Add(single.Value);
            else if (names is PdfArray arr)
                for (int i = 0; i < arr.Count; i++)
                    if ((doc?.ResolveObject(arr[i]) ?? arr[i]) is PdfName n) named.Add(n.Value);

            if (named.Contains(Core.Constants.PdfColorSpaceNames.AllColorant)) return false;
            return !(named.Contains(Core.Constants.PdfColorSpaceNames.CyanColorant)
                     && named.Contains(Core.Constants.PdfColorSpaceNames.MagentaColorant)
                     && named.Contains(Core.Constants.PdfColorSpaceNames.YellowColorant)
                     && named.Contains(Core.Constants.PdfColorSpaceNames.BlackColorant));
        }
        /// <summary>
        /// sRGB → DeviceCMYK, the exact inverse of <see cref="PdfColor.CmykToRgb"/>
        /// (<c>r = (1−c)(1−k)</c>, and likewise for g and b), so a colour that came from CMYK and
        /// was never blended round-trips unchanged.
        ///
        /// <para><b>Not interchangeable with
        /// <c>Operations.Metadata.PdfColorSpaceConverter.RgbToCmykWithGcr</c></b>, which works in linear
        /// light and pulls part of <c>min(c,m,y)</c> into K for printable rich blacks. Both are
        /// correct for their own job — round-trip fidelity here, print quality there — and the two
        /// were once both called <c>RgbToCmyk</c>, which is the only reason they read as a
        /// duplicate. See GAPS.md §2b.</para>
        /// </summary>
        internal static void RgbToCmykExactInverse(SKColor rgb, out float c, out float m, out float y, out float k)
        {
            float r = rgb.Red / 255f, g = rgb.Green / 255f, b = rgb.Blue / 255f;
            float maxc = Math.Max(r, Math.Max(g, b));
            k = 1f - maxc;
            float inv = 1f - k;
            if (inv <= 0f) { c = m = y = 0f; return; }   // black: no chromatic ink needed
            c = 1f - r / inv;
            m = 1f - g / inv;
            y = 1f - b / inv;
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }
}
