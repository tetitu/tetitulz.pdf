// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The blending colour space of a transparency group (§11.3.4, §11.6.6 Table 147 <c>/CS</c>):
    /// the space the group's contents are converted into when painted, composited in, and handed
    /// on in. Only the three families the standard admits, by their arithmetic — an additive RGB
    /// space, a one-component grey space, a four-component subtractive space. The gamma, matrix or
    /// profile of a <c>/CalRGB</c>, <c>/CalGray</c> or <c>/ICCBased</c> space does not change the
    /// arithmetic, so each is its device family here; a <c>/Lab</c> space is refused (§11.3.4).
    /// </summary>
    internal enum GroupBlendSpace
    {
        /// <summary>DeviceRGB, CalRGB, ICCBased <c>/N 3</c> — the space the canvas holds, nothing to convert.</summary>
        Rgb,
        /// <summary>DeviceGray, CalGray, ICCBased <c>/N 1</c>.</summary>
        Gray,
        /// <summary>DeviceCMYK, ICCBased <c>/N 4</c>.</summary>
        Cmyk,
    }

    /// <summary>
    /// A Skia blender per (group colour space, blend mode): the one place §11.3's compositing
    /// formula is written for a space other than the canvas's own.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a runtime blender and not a second buffer.</b> The canvas stores sRGB, and the
    /// naive device conversions this renderer uses everywhere else (<see cref="PdfColor.CmykToRgb"/>,
    /// <c>OverprintCompositor.RgbToCmykExactInverse</c>, the BT.601 grey) are exact round trips,
    /// so a group's pixels can be <i>stored</i> as sRGB and <i>composited</i> in the group's space:
    /// each paint's blend step converts source and backdrop into the space, applies §11.3.6's
    /// formula per component there — blend function on the complements for a subtractive space
    /// (§11.3.4), alpha weights on the components, the non-separable modes by §11.3.5.3's CMYK and
    /// grey rules — and converts the result back. That is what an SkSL blender is: a program Skia
    /// runs in place of the fixed blend mode for every pixel a paint touches, on the CPU raster
    /// backend too. Nothing after it knows.</para>
    /// <para><b>What differs from blending in RGB, and so what the tests pin.</b> Normal at
    /// alpha 1 is the same pixel in every space (the conversions are inverses). The visible
    /// differences are a partly transparent paint — the alpha weights interpolate CMYK inks, not
    /// RGB light, so half cyan over black is <c>(0.25, 0.5, 0.5)</c> and not <c>(0, 0.5, 0.5)</c>
    /// — and every blend mode but Multiply, whose complement form is the same product; and a grey
    /// group, whose contents are converted to grey when painted into it, which is how a page group
    /// of <c>/CS /DeviceGray</c> renders a page grey.</para>
    /// <para>Compiled once per (space, mode) and kept for the process: a blender is immutable and
    /// shared across threads, and a paint only references it.</para>
    /// </remarks>
    internal static class GroupBlenders
    {
        private static readonly ConcurrentDictionary<int, SKBlender> Cache = new ConcurrentDictionary<int, SKBlender>();

        /// <summary>
        /// How many times this thread asked for a group blender — the evidence that a paint went
        /// through the group's space rather than Skia's fixed mode, which no pixel of an opaque
        /// Normal paint can show (see <c>PdfRenderContext.ApplyBlend</c>'s shortcut).
        /// </summary>
        [ThreadStatic] internal static int RequestsOnThisThread;

        /// <summary>
        /// The blender compositing <paramref name="mode"/> in <paramref name="space"/>, or null
        /// when <paramref name="mode"/> is not one of the PDF blend modes (Table 134/135, with
        /// <see cref="SKBlendMode.SrcOver"/> as Normal) or <paramref name="space"/> is
        /// <see cref="GroupBlendSpace.Rgb"/>, where Skia's own mode is the answer.
        /// </summary>
        /// <exception cref="InvalidOperationException">The SkSL did not compile — a defect here,
        /// never a property of the file; the message carries the compiler's.</exception>
        internal static SKBlender For(GroupBlendSpace space, SKBlendMode mode)
        {
            if (space == GroupBlendSpace.Rgb || !IsPdfMode(mode)) return null;
            RequestsOnThisThread++;
            return Cache.GetOrAdd(((int)space << 8) | (int)mode, _ => Compile(space, mode));
        }

        /// <summary>Whether <paramref name="mode"/> is a PDF blend mode this class writes a program for.</summary>
        internal static bool IsPdfMode(SKBlendMode mode) => mode switch
        {
            SKBlendMode.SrcOver or SKBlendMode.Multiply or SKBlendMode.Screen or SKBlendMode.Overlay
                or SKBlendMode.Darken or SKBlendMode.Lighten or SKBlendMode.ColorDodge or SKBlendMode.ColorBurn
                or SKBlendMode.HardLight or SKBlendMode.SoftLight or SKBlendMode.Difference or SKBlendMode.Exclusion
                or SKBlendMode.Hue or SKBlendMode.Saturation or SKBlendMode.Color or SKBlendMode.Luminosity => true,
            _ => false,
        };

        private static SKBlender Compile(GroupBlendSpace space, SKBlendMode mode)
        {
            string sksl = Program(space, mode);
            using var effect = SKRuntimeEffect.CreateBlender(sksl, out string errors);
            if (effect == null)
                throw new InvalidOperationException($"group blender ({space}, {mode}) did not compile: {errors}");
            return effect.ToBlender();
        }

        /// <summary>The SkSL for one (space, mode). Internal for the test that compiles every pair.</summary>
        internal static string Program(GroupBlendSpace space, SKBlendMode mode)
        {
            string lumaWeights = Weights(PdfColorSpaceNames.LumaRedF, PdfColorSpaceNames.LumaGreenF, PdfColorSpaceNames.LumaBlueF);
            // §11.3.5.3's Lum(C) weights, which the standard states as 0.3, 0.59, 0.11 for the
            // non-separable modes (not BT.601's 0.299/0.587/0.114, which convert a colour to grey).
            const string blendLumWeights = "float3(0.3, 0.59, 0.11)";

            bool separable = IsSeparable(mode);
            string blendFn = separable ? SeparableFunction(mode) : "";

            var sb = new System.Text.StringBuilder(2048);
            sb.Append(
                // ---- the non-separable toolkit (§11.3.5.3), on RGB-like triples ----
                "float lum(float3 c) { return dot(c, " + blendLumWeights + "); }\n" +
                "float3 clipColor(float3 c) {\n" +
                "  float l = lum(c);\n" +
                "  float n = min(c.r, min(c.g, c.b));\n" +
                "  float x = max(c.r, max(c.g, c.b));\n" +
                "  if (n < 0.0) c = l + (c - l) * l / (l - n);\n" +
                "  if (x > 1.0) c = l + (c - l) * (1.0 - l) / (x - l);\n" +
                "  return c;\n" +
                "}\n" +
                "float3 setLum(float3 c, float l) { return clipColor(c + (l - lum(c))); }\n" +
                "float sat(float3 c) { return max(c.r, max(c.g, c.b)) - min(c.r, min(c.g, c.b)); }\n" +
                "float3 setSat(float3 c, float s) {\n" +
                "  float mx = max(c.r, max(c.g, c.b));\n" +
                "  float mn = min(c.r, min(c.g, c.b));\n" +
                "  return mx > mn ? (c - mn) * s / (mx - mn) : float3(0.0);\n" +
                "}\n");
            if (separable)
                sb.Append("float blend1(float cb, float cs) { " + blendFn + " }\n");

            switch (space)
            {
                case GroupBlendSpace.Cmyk:
                    // Additive form of the four inks: (1-c, 1-m, 1-y, 1-k). From sRGB through the
                    // exact inverse of CmykToRgb: k = 1 - max(r,g,b), 1-c = r / max — black is
                    // (1, 1, 1, 0). Back: rgb = (1-c, 1-m, 1-y) * (1-k).
                    sb.Append(
                        "float4 toSpace(float3 c) {\n" +
                        "  float m = max(c.r, max(c.g, c.b));\n" +
                        "  return m > 0.0 ? float4(c / m, m) : float4(1.0, 1.0, 1.0, 0.0);\n" +
                        "}\n" +
                        "float3 fromSpace(float4 a) { return a.rgb * a.a; }\n");
                    sb.Append(separable
                        ? "float4 blend(float4 d, float4 s) { return float4(blend1(d.r, s.r), blend1(d.g, s.g), blend1(d.b, s.b), blend1(d.a, s.a)); }\n"
                        // §11.3.5.3: C, M, Y complemented to RGB (which the additive form already
                        // is), the RGB formula applied, complemented back; K from the backdrop for
                        // Hue, Saturation and Color, from the source for Luminosity.
                        : "float4 blend(float4 d, float4 s) { return float4(" + NonSeparable(mode) + ", " +
                          (mode == SKBlendMode.Luminosity ? "s.a" : "d.a") + "); }\n");
                    break;

                case GroupBlendSpace.Gray:
                    sb.Append(
                        "float toSpace(float3 c) { return dot(c, " + lumaWeights + "); }\n" +
                        "float3 fromSpace(float g) { return float3(g); }\n");
                    sb.Append(separable
                        ? "float blend(float d, float s) { return blend1(d, s); }\n"
                        // §11.3.5.3: a grey space blends through RGB; two neutral colours have no
                        // saturation and no hue, so Hue, Saturation and Color keep the backdrop's
                        // luminosity — the backdrop — and Luminosity takes the source's.
                        : "float blend(float d, float s) { return " + (mode == SKBlendMode.Luminosity ? "s" : "d") + "; }\n");
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(space), space, "the RGB space blends through Skia's own mode");
            }

            string vec = space == GroupBlendSpace.Cmyk ? "float4" : "float";
            sb.Append(
                "half4 main(half4 src, half4 dst) {\n" +
                "  float sa = float(src.a);\n" +
                "  float da = float(dst.a);\n" +
                "  float3 sc = sa > 0.0 ? float3(src.rgb) / sa : float3(0.0);\n" +
                "  float3 dc = da > 0.0 ? float3(dst.rgb) / da : float3(0.0);\n" +
                $"  {vec} S = toSpace(sc);\n" +
                $"  {vec} D = toSpace(dc);\n" +
                $"  {vec} B = blend(D, S);\n" +
                // §11.3.6: Cr = (1 - as/ar) Cb + (as/ar) ((1 - ab) Cs + ab B(Cb, Cs)), ar = union(ab, as).
                "  float ar = sa + da - sa * da;\n" +
                $"  {vec} Cs2 = (1.0 - da) * S + da * B;\n" +
                $"  {vec} Cr = ar > 0.0 ? (1.0 - sa / ar) * D + (sa / ar) * Cs2 : S;\n" +
                "  float3 rgb = clamp(fromSpace(Cr), 0.0, 1.0);\n" +
                "  return half4(half3(rgb * ar), half(ar));\n" +
                "}\n");
            return sb.ToString();
        }

        private static bool IsSeparable(SKBlendMode mode) => mode switch
        {
            SKBlendMode.Hue or SKBlendMode.Saturation or SKBlendMode.Color or SKBlendMode.Luminosity => false,
            _ => true,
        };

        /// <summary>Table 134's B(cb, cs) as a function body over two floats; the W3C forms Skia's own modes use.</summary>
        private static string SeparableFunction(SKBlendMode mode) => mode switch
        {
            SKBlendMode.SrcOver    => "return cs;",
            SKBlendMode.Multiply   => "return cb * cs;",
            SKBlendMode.Screen     => "return cb + cs - cb * cs;",
            SKBlendMode.Overlay    => "return cb <= 0.5 ? cs * 2.0 * cb : cs + (2.0 * cb - 1.0) - cs * (2.0 * cb - 1.0);",   // HardLight(cs, cb)
            SKBlendMode.Darken     => "return min(cb, cs);",
            SKBlendMode.Lighten    => "return max(cb, cs);",
            SKBlendMode.ColorDodge => "return cb <= 0.0 ? 0.0 : (cs >= 1.0 ? 1.0 : min(1.0, cb / (1.0 - cs)));",
            SKBlendMode.ColorBurn  => "return cb >= 1.0 ? 1.0 : (cs <= 0.0 ? 0.0 : 1.0 - min(1.0, (1.0 - cb) / cs));",
            SKBlendMode.HardLight  => "return cs <= 0.5 ? cb * 2.0 * cs : cb + (2.0 * cs - 1.0) - cb * (2.0 * cs - 1.0);",
            SKBlendMode.SoftLight  => "if (cs <= 0.5) return cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb);\n" +
                                      "  float d = cb <= 0.25 ? ((16.0 * cb - 12.0) * cb + 4.0) * cb : sqrt(cb);\n" +
                                      "  return cb + (2.0 * cs - 1.0) * (d - cb);",
            SKBlendMode.Difference => "return abs(cb - cs);",
            SKBlendMode.Exclusion  => "return cb + cs - 2.0 * cb * cs;",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a separable PDF blend mode"),
        };

        /// <summary>Table 135's B(Cb, Cs) over the RGB-like triples <c>d.rgb</c> and <c>s.rgb</c>.</summary>
        private static string NonSeparable(SKBlendMode mode) => mode switch
        {
            SKBlendMode.Hue        => "setLum(setSat(s.rgb, sat(d.rgb)), lum(d.rgb))",
            SKBlendMode.Saturation => "setLum(setSat(d.rgb, sat(s.rgb)), lum(d.rgb))",
            SKBlendMode.Color      => "setLum(s.rgb, lum(d.rgb))",
            SKBlendMode.Luminosity => "setLum(d.rgb, lum(s.rgb))",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "not a non-separable PDF blend mode"),
        };

        private static string Weights(float r, float g, float b) =>
            "float3(" + F(r) + ", " + F(g) + ", " + F(b) + ")";

        private static string F(float v) => v.ToString("0.0#####", System.Globalization.CultureInfo.InvariantCulture);
    }
}
