// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// The SVG filter primitives <see cref="SvgToPdfConverter"/> applies (Filter Effects 1 §9), run on
    /// pixels: a PDF has no filter of its own, so the filtered element is rendered, filtered here, and
    /// drawn back as an image.
    /// </summary>
    /// <remarks>
    /// An image is premultiplied RGBA in floats, its first row at the top. Each primitive works in its
    /// own colour space, linearRGB unless its <c>color-interpolation-filters</c> is <c>sRGB</c>
    /// (§9.5): an input is converted to it on the way in, and the result carries it on. Every length
    /// here is in pixels; the converter maps them from user space.
    /// </remarks>
    internal static class SvgFilterEffects
    {
        /// <summary>A premultiplied RGBA image, row-major from the top, in sRGB or linearRGB.</summary>
        internal sealed class Image
        {
            public readonly int Width, Height;
            public readonly float[] Px;
            public readonly bool Linear;
            /// <summary>The pixels a primitive's result was clipped to (x0, y0, x1, y1), or <c>null</c> for the whole image: what <c>feTile</c> tiles.</summary>
            public int[] Subregion;

            public Image(int width, int height, bool linear)
            {
                Width = width; Height = height; Linear = linear;
                Px = new float[width * height * 4];
            }

            /// <summary>This image in <paramref name="linear"/>'s space: itself when it is already there.</summary>
            public Image In(bool linear)
            {
                if (linear == Linear) return this;
                var converted = new Image(Width, Height, linear) { Subregion = Subregion };
                for (int i = 0; i < Px.Length; i += 4)
                {
                    float a = Px[i + 3];
                    converted.Px[i + 3] = a;
                    if (a <= 0) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        float straight = Math.Min(1f, Px[i + c] / a);
                        converted.Px[i + c] = a * (linear ? ToLinear(straight) : ToSrgb(straight));
                    }
                }
                return converted;
            }
        }

        /// <summary>sRGB to linear light (IEC 61966-2-1).</summary>
        internal static float ToLinear(float c) => (float)Core.Imaging.SrgbTransfer.Decode(c);

        /// <summary>Linear light to sRGB (IEC 61966-2-1).</summary>
        internal static float ToSrgb(float c) => (float)Core.Imaging.SrgbTransfer.Encode(c);

        /// <summary>One primitive: its inputs by name (<c>null</c> for the default), its result's name, its space and its subregion.</summary>
        internal abstract class Primitive
        {
            public string In, In2, Result;
            public bool Linear = true;
            /// <summary>The pixels the result is clipped to (x0, y0, x1, y1), or <c>null</c> for the whole image.</summary>
            public int[] Subregion;
            public abstract Image Apply(Func<string, bool, Image> input, int width, int height);
        }

        /// <summary><c>feGaussianBlur</c> (§9.13): three box blurs per axis, as the specification approximates it.</summary>
        internal sealed class Blur : Primitive
        {
            public float StdX, StdY;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear);
                var result = new Image(width, height, Linear);
                Array.Copy(source.Px, result.Px, source.Px.Length);
                BoxBlurs(result, StdX, horizontal: true);
                BoxBlurs(result, StdY, horizontal: false);
                return result;
            }
        }

        /// <summary><c>feOffset</c> (§9.19): the input moved by whole pixels.</summary>
        internal sealed class Offset : Primitive
        {
            public int Dx, Dy;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
                => Shift(input(In, Linear), Dx, Dy);
        }

        /// <summary><paramref name="source"/> moved by (<paramref name="dx"/>, <paramref name="dy"/>) pixels, transparent where nothing moved in.</summary>
        private static Image Shift(Image source, int dx, int dy)
        {
            int width = source.Width, height = source.Height;
            var result = new Image(width, height, source.Linear);
            for (int y = 0; y < height; y++)
            {
                int sy = y - dy;
                if (sy < 0 || sy >= height) continue;
                for (int x = 0; x < width; x++)
                {
                    int sx = x - dx;
                    if (sx < 0 || sx >= width) continue;
                    Array.Copy(source.Px, (sy * width + sx) * 4, result.Px, (y * width + x) * 4, 4);
                }
            }
            return result;
        }

        /// <summary>
        /// <c>feDropShadow</c> (§9.12): the input's alpha blurred, moved and filled with the flood
        /// colour, and the input drawn over it — the blur, offset, flood, composite and merge a drop
        /// shadow is otherwise written as.
        /// </summary>
        internal sealed class DropShadow : Primitive
        {
            public float StdX, StdY;
            public int Dx, Dy;
            public float R, G, B, A;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear);
                var shadow = new Image(width, height, Linear);
                for (int i = 3; i < shadow.Px.Length; i += 4) shadow.Px[i] = source.Px[i];
                BoxBlurs(shadow, StdX, horizontal: true);
                BoxBlurs(shadow, StdY, horizontal: false);
                shadow = Shift(shadow, Dx, Dy);

                float r = Linear ? ToLinear(R) : R, g = Linear ? ToLinear(G) : G, b = Linear ? ToLinear(B) : B;
                var result = new Image(width, height, Linear);
                float[] px = result.Px, top = source.Px;
                for (int i = 0; i < px.Length; i += 4)
                {
                    float sa = shadow.Px[i + 3] * A, keep = 1 - top[i + 3];
                    px[i] = top[i] + r * sa * keep;
                    px[i + 1] = top[i + 1] + g * sa * keep;
                    px[i + 2] = top[i + 2] + b * sa * keep;
                    px[i + 3] = top[i + 3] + sa * keep;
                }
                return result;
            }
        }

        /// <summary>
        /// One channel's function in a <c>feComponentTransfer</c> (§9.8), on a straight value in
        /// [0, 1]: <c>identity</c>, <c>table</c>, <c>discrete</c>, <c>linear</c> or <c>gamma</c>.
        /// </summary>
        internal sealed class TransferFunction
        {
            public string Type = "identity";
            public float[] Table = Array.Empty<float>();
            public float Slope = 1, Intercept, Amplitude = 1, Exponent = 1, Offset;

            public float Apply(float c)
            {
                switch (Type)
                {
                    case "table":
                    {
                        // n = count − 1 intervals; C in [k/n, (k+1)/n] runs from v_k to v_k+1.
                        if (Table.Length == 0) return c;
                        int n = Table.Length - 1;
                        int k = Math.Min(SaturatingCast.ToInt(Math.Floor(c * n)), n);
                        return k == n ? Table[n] : Table[k] + (c - (float)k / n) * n * (Table[k + 1] - Table[k]);
                    }
                    case "discrete":
                    {
                        // n = count steps; C in [k/n, (k+1)/n) is v_k.
                        if (Table.Length == 0) return c;
                        int n = Table.Length;
                        return Table[Math.Min(SaturatingCast.ToInt(Math.Floor(c * n)), n - 1)];
                    }
                    case "linear": return Slope * c + Intercept;
                    case "gamma": return Amplitude * (float)Math.Pow(c, Exponent) + Offset;
                    default: return c;
                }
            }
        }

        /// <summary><c>feComponentTransfer</c> (§9.8): each channel of straight colour and alpha through its own function.</summary>
        internal sealed class ComponentTransfer : Primitive
        {
            /// <summary>R, G, B and A's functions; <c>null</c> is the identity.</summary>
            public readonly TransferFunction[] Functions = new TransferFunction[4];

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                float[] source = input(In, Linear).Px;
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                for (int i = 0; i < px.Length; i += 4)
                {
                    float a = source[i + 3];
                    float na = Clamp01(Functions[3]?.Apply(a) ?? a);
                    for (int c = 0; c < 3; c++)
                    {
                        float straight = a > 0 ? source[i + c] / a : 0;
                        px[i + c] = na * Clamp01(Functions[c]?.Apply(straight) ?? straight);
                    }
                    px[i + 3] = na;
                }
                return result;
            }
        }

        /// <summary><c>feFlood</c> (§9.12): the whole image one colour, straight sRGB and an opacity.</summary>
        internal sealed class Flood : Primitive
        {
            public float R, G, B, A;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                var result = new Image(width, height, Linear);
                float r = A * (Linear ? ToLinear(R) : R), g = A * (Linear ? ToLinear(G) : G), b = A * (Linear ? ToLinear(B) : B);
                for (int i = 0; i < result.Px.Length; i += 4)
                {
                    result.Px[i] = r; result.Px[i + 1] = g; result.Px[i + 2] = b; result.Px[i + 3] = A;
                }
                return result;
            }
        }

        /// <summary><c>feMerge</c> (§9.18): its nodes composited in order, each over those before it.</summary>
        internal sealed class Merge : Primitive
        {
            public readonly List<string> Nodes = new List<string>();

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                var result = new Image(width, height, Linear);
                foreach (string node in Nodes)
                {
                    float[] top = input(node, Linear).Px, px = result.Px;
                    for (int i = 0; i < px.Length; i += 4)
                    {
                        float keep = 1 - top[i + 3];
                        for (int c = 0; c < 4; c++) px[i + c] = top[i + c] + px[i + c] * keep;
                    }
                }
                return result;
            }
        }

        /// <summary><c>feComposite</c> (§9.9): Porter-Duff of <c>in</c> onto <c>in2</c>, or the arithmetic sum.</summary>
        internal sealed class Composite : Primitive
        {
            public string Operator = "over";
            public float K1, K2, K3, K4;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                float[] a = input(In, Linear).Px, b = input(In2, Linear).Px;
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                for (int i = 0; i < px.Length; i += 4)
                {
                    float aa = a[i + 3], ba = b[i + 3];
                    if (Operator == "arithmetic")
                    {
                        float alpha = Clamp01(K1 * aa * ba + K2 * aa + K3 * ba + K4);
                        for (int c = 0; c < 3; c++)
                            px[i + c] = Math.Min(alpha, Clamp01(K1 * a[i + c] * b[i + c] + K2 * a[i + c] + K3 * b[i + c] + K4));
                        px[i + 3] = alpha;
                        continue;
                    }
                    // Fa and Fb, the Porter-Duff weights of A and B.
                    float fa, fb;
                    switch (Operator)
                    {
                        case "in":      fa = ba;     fb = 0;      break;
                        case "out":     fa = 1 - ba; fb = 0;      break;
                        case "atop":    fa = ba;     fb = 1 - aa; break;
                        case "xor":     fa = 1 - ba; fb = 1 - aa; break;
                        case "lighter": fa = 1;      fb = 1;      break;
                        default:        fa = 1;      fb = 1 - aa; break;   // over
                    }
                    for (int c = 0; c < 4; c++) px[i + c] = Math.Min(1f, a[i + c] * fa + b[i + c] * fb);
                }
                return result;
            }
        }

        /// <summary><c>feColorMatrix</c> (§9.10): a 5 × 4 matrix over straight colour and alpha.</summary>
        internal sealed class ColorMatrix : Primitive
        {
            /// <summary>Row-major, 20 values: each output channel from R, G, B, A and 1.</summary>
            public float[] Matrix;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                float[] source = input(In, Linear).Px, m = Matrix;
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                for (int i = 0; i < px.Length; i += 4)
                {
                    float a = source[i + 3];
                    float r = a > 0 ? source[i] / a : 0, g = a > 0 ? source[i + 1] / a : 0, b = a > 0 ? source[i + 2] / a : 0;
                    float na = Clamp01(m[15] * r + m[16] * g + m[17] * b + m[18] * a + m[19]);
                    for (int c = 0; c < 3; c++)
                        px[i + c] = na * Clamp01(m[c * 5] * r + m[c * 5 + 1] * g + m[c * 5 + 2] * b + m[c * 5 + 3] * a + m[c * 5 + 4]);
                    px[i + 3] = na;
                }
                return result;
            }

            /// <summary>The matrix <c>saturate</c> makes (§9.10).</summary>
            public static float[] Saturate(float s) => new[]
            {
                0.213f + 0.787f * s, 0.715f - 0.715f * s, 0.072f - 0.072f * s, 0, 0,
                0.213f - 0.213f * s, 0.715f + 0.285f * s, 0.072f - 0.072f * s, 0, 0,
                0.213f - 0.213f * s, 0.715f - 0.715f * s, 0.072f + 0.928f * s, 0, 0,
                0, 0, 0, 1, 0,
            };

            /// <summary>The matrix <c>hueRotate</c> makes, the angle in degrees (§9.10).</summary>
            public static float[] HueRotate(float degrees)
            {
                double t = degrees * Math.PI / 180;
                float cos = (float)Math.Cos(t), sin = (float)Math.Sin(t);
                return new[]
                {
                    0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
                    0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
                    0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
                    0, 0, 0, 1, 0,
                };
            }

            /// <summary>The matrix <c>luminanceToAlpha</c> makes: black, its alpha the luminance (§9.10).</summary>
            public static float[] LuminanceToAlpha() => new[]
            {
                0f, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                0, 0, 0, 0, 0,
                0.2125f, 0.7154f, 0.0721f, 0, 0,
            };
        }

        /// <summary>
        /// <c>feBlend</c> (§9.7): <c>in</c>, the source, blended onto <c>in2</c>, the backdrop, by a
        /// CSS blend mode (Compositing and Blending 1 §9) — the separable ones a channel at a time,
        /// the non-separable ones (<c>hue</c>, <c>saturation</c>, <c>color</c>, <c>luminosity</c>) on
        /// the whole colour.
        /// </summary>
        /// <remarks>All sixteen modes, not SVG 1.1's five alone.</remarks>
        internal sealed class Blend : Primitive
        {
            public string Mode = "normal";

            /// <summary>The sixteen modes of Compositing and Blending 1, which Filter Effects 1 takes for <c>feBlend</c>.</summary>
            public static bool IsKnown(string mode) => mode is "normal" or "multiply" or "screen" or "overlay" or "darken" or "lighten"
                or "color-dodge" or "color-burn" or "hard-light" or "soft-light" or "difference" or "exclusion"
                or "hue" or "saturation" or "color" or "luminosity";

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                float[] a = input(In, Linear).Px, b = input(In2, Linear).Px;
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                var cs = new float[3]; var cb = new float[3]; var mixed = new float[3]; var scratch = new float[3];
                for (int i = 0; i < px.Length; i += 4)
                {
                    float aa = a[i + 3], ba = b[i + 3];
                    for (int c = 0; c < 3; c++)
                    {
                        cs[c] = aa > 0 ? a[i + c] / aa : 0;
                        cb[c] = ba > 0 ? b[i + c] / ba : 0;
                    }
                    Mix(Mode, cb, cs, mixed, scratch);
                    // Premultiplied: A(1 − αB) + B(1 − αA) + αA·αB·B(Cb, Cs), the colours straight.
                    for (int c = 0; c < 3; c++)
                        px[i + c] = Clamp01(a[i + c] * (1 - ba) + b[i + c] * (1 - aa) + aa * ba * mixed[c]);
                    px[i + 3] = Clamp01(aa + ba - aa * ba);
                }
                return result;
            }

            /// <summary>B(Cb, Cs) of Compositing and Blending 1 §9: the blended straight colour, into <paramref name="into"/>.</summary>
            internal static void Mix(string mode, float[] cb, float[] cs, float[] into, float[] scratch)
            {
                switch (mode)
                {
                    case "hue": SetLum(SetSat(cs, Sat(cb), scratch), Lum(cb), into); return;
                    case "saturation": SetLum(SetSat(cb, Sat(cs), scratch), Lum(cb), into); return;
                    case "color": SetLum(cs, Lum(cb), into); return;
                    case "luminosity": SetLum(cb, Lum(cs), into); return;
                }
                for (int c = 0; c < 3; c++) into[c] = Separable(mode, cb[c], cs[c]);
            }

            /// <summary>A separable mode's B(Cb, Cs) for one channel (§9.1.2 to §9.1.13); an unknown mode is <c>normal</c>.</summary>
            private static float Separable(string mode, float cb, float cs) => mode switch
            {
                "multiply" => cb * cs,
                "screen" => cb + cs - cb * cs,
                "overlay" => HardLight(cs, cb),
                "darken" => Math.Min(cb, cs),
                "lighten" => Math.Max(cb, cs),
                "color-dodge" => cb <= 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
                "color-burn" => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
                "hard-light" => HardLight(cb, cs),
                "soft-light" => cs <= 0.5f ? cb - (1 - 2 * cs) * cb * (1 - cb) : cb + (2 * cs - 1) * (D(cb) - cb),
                "difference" => Math.Abs(cb - cs),
                "exclusion" => cb + cs - 2 * cb * cs,
                _ => cs,
            };

            private static float HardLight(float cb, float cs)
                => cs <= 0.5f ? cb * (2 * cs) : cb + (2 * cs - 1) - cb * (2 * cs - 1);

            /// <summary>Soft-light's D(Cb) (§9.1.10).</summary>
            private static float D(float cb) => cb <= 0.25f ? ((16 * cb - 12) * cb + 4) * cb : (float)Math.Sqrt(cb);

            private static float Lum(float[] c) => 0.3f * c[0] + 0.59f * c[1] + 0.11f * c[2];

            private static float Sat(float[] c) => Math.Max(c[0], Math.Max(c[1], c[2])) - Math.Min(c[0], Math.Min(c[1], c[2]));

            /// <summary>SetLum (§9.2): <paramref name="c"/> at luminosity <paramref name="l"/>, clipped into [0, 1] about its luminosity, into <paramref name="into"/>.</summary>
            private static void SetLum(float[] c, float l, float[] into)
            {
                float d = l - Lum(c);
                for (int i = 0; i < 3; i++) into[i] = c[i] + d;
                float lum = Lum(into);
                float n = Math.Min(into[0], Math.Min(into[1], into[2])), x = Math.Max(into[0], Math.Max(into[1], into[2]));
                if (n < 0) for (int i = 0; i < 3; i++) into[i] = lum + (into[i] - lum) * lum / (lum - n);
                if (x > 1) for (int i = 0; i < 3; i++) into[i] = lum + (into[i] - lum) * (1 - lum) / (x - lum);
            }

            /// <summary>SetSat (§9.2): <paramref name="c"/> at saturation <paramref name="s"/> — its greatest channel s, its least 0, the middle in proportion — into <paramref name="into"/>.</summary>
            private static float[] SetSat(float[] c, float s, float[] into)
            {
                int max = 0, min = 0;
                for (int i = 1; i < 3; i++)
                {
                    if (c[i] > c[max]) max = i;
                    if (c[i] < c[min]) min = i;
                }
                into[0] = into[1] = into[2] = 0;
                if (c[max] > c[min])
                {
                    int mid = 3 - max - min;
                    into[mid] = (c[mid] - c[min]) * s / (c[max] - c[min]);
                    into[max] = s;
                }
                return into;
            }
        }

        /// <summary>
        /// <c>feMorphology</c> (§9.16): each channel's minimum (<c>erode</c>) or maximum
        /// (<c>dilate</c>) over a (2rx + 1) × (2ry + 1) window of pixels, outside the image transparent
        /// black. A radius of zero on an axis leaves that axis alone.
        /// </summary>
        internal sealed class Morphology : Primitive
        {
            public bool Dilate;
            public int RadiusX, RadiusY;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear);
                var result = new Image(width, height, Linear);
                Array.Copy(source.Px, result.Px, source.Px.Length);
                Extremes(result, RadiusX, horizontal: true, Dilate);
                Extremes(result, RadiusY, horizontal: false, Dilate);
                return result;
            }
        }

        /// <summary>
        /// A sliding minimum or maximum of half-width <paramref name="radius"/> along one axis, per
        /// channel, through a monotonic deque: the work is the line's length, whatever the radius. A
        /// window past the image's edge holds transparent black, so a minimum there is 0.
        /// </summary>
        private static void Extremes(Image image, int radius, bool horizontal, bool max)
        {
            if (radius <= 0) return;
            int lines = horizontal ? image.Height : image.Width, length = horizontal ? image.Width : image.Height;
            int step = horizontal ? 4 : image.Width * 4;
            var line = new float[length];
            var outLine = new float[length];
            var deque = new int[length];
            for (int l = 0; l < lines; l++)
            {
                int start = horizontal ? l * image.Width * 4 : l * 4;
                for (int c = 0; c < 4; c++)
                {
                    for (int i = 0; i < length; i++) line[i] = image.Px[start + i * step + c];
                    int head = 0, tail = 0, next = 0;
                    for (int i = 0; i < length; i++)
                    {
                        int hi = Math.Min(length - 1, i + radius);
                        while (next <= hi)
                        {
                            while (tail > head && (max ? line[deque[tail - 1]] <= line[next] : line[deque[tail - 1]] >= line[next])) tail--;
                            deque[tail++] = next++;
                        }
                        while (deque[head] < i - radius) head++;
                        if (!max && (i - radius < 0 || i + radius >= length)) outLine[i] = 0;
                        else outLine[i] = line[deque[head]];
                    }
                    for (int i = 0; i < length; i++) image.Px[start + i * step + c] = outLine[i];
                }
            }
        }

        /// <summary>
        /// <c>feConvolveMatrix</c> (§9.11): each pixel the kernel's weighted sum of the pixels about it
        /// — the kernel turned 180°, <c>targetX</c>/<c>targetY</c> the pixel's own cell — over
        /// <c>divisor</c>, plus <c>bias</c> times the pixel's alpha; on premultiplied colour and alpha,
        /// or on straight colour with the alpha kept when <c>preserveAlpha</c> is set. Past the image
        /// the edge pixel is read (<c>duplicate</c>), the image wraps, or nothing is read (<c>none</c>).
        /// </summary>
        internal sealed class ConvolveMatrix : Primitive
        {
            public int OrderX, OrderY, TargetX, TargetY;
            /// <summary>Row-major, <see cref="OrderY"/> rows of <see cref="OrderX"/>.</summary>
            public float[] Kernel;
            public float Divisor = 1, Bias;
            public string EdgeMode = "duplicate";
            public bool PreserveAlpha;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                float[] src = input(In, Linear).Px;
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int o = (y * width + x) * 4;
                        float a = src[o + 3];
                        float r = 0, g = 0, b = 0, na = 0;
                        for (int i = 0; i < OrderY; i++)
                            for (int j = 0; j < OrderX; j++)
                            {
                                float k = Kernel[(OrderY - i - 1) * OrderX + (OrderX - j - 1)];
                                if (k == 0) continue;
                                int sx = x - TargetX + j, sy = y - TargetY + i;
                                if (!Sample(width, height, ref sx, ref sy)) continue;
                                int s = (sy * width + sx) * 4;
                                float sa = src[s + 3];
                                if (PreserveAlpha)
                                {
                                    if (sa <= 0) continue;
                                    r += k * src[s] / sa; g += k * src[s + 1] / sa; b += k * src[s + 2] / sa;
                                }
                                else
                                {
                                    r += k * src[s]; g += k * src[s + 1]; b += k * src[s + 2]; na += k * sa;
                                }
                            }
                        float alpha = PreserveAlpha ? a : Clamp01(na / Divisor + Bias * a);
                        r = Clamp01(r / Divisor + Bias * a); g = Clamp01(g / Divisor + Bias * a); b = Clamp01(b / Divisor + Bias * a);
                        // Straight colour goes back under the kept alpha; premultiplied colour is no more than the alpha.
                        px[o] = PreserveAlpha ? alpha * r : Math.Min(alpha, r);
                        px[o + 1] = PreserveAlpha ? alpha * g : Math.Min(alpha, g);
                        px[o + 2] = PreserveAlpha ? alpha * b : Math.Min(alpha, b);
                        px[o + 3] = alpha;
                    }
                return result;
            }

            /// <summary>Brings a sample position into the image by the edge mode; false when <c>none</c> reads nothing there.</summary>
            private bool Sample(int width, int height, ref int sx, ref int sy)
            {
                if (sx >= 0 && sx < width && sy >= 0 && sy < height) return true;
                switch (EdgeMode)
                {
                    case "wrap": sx = ((sx % width) + width) % width; sy = ((sy % height) + height) % height; return true;
                    case "none": return false;
                    default: sx = Math.Min(Math.Max(sx, 0), width - 1); sy = Math.Min(Math.Max(sy, 0), height - 1); return true;
                }
            }
        }

        /// <summary>
        /// <c>feTile</c> (§9.20): the input's subregion repeated across the image, from that
        /// subregion's own place — an input clipped to no subregion tiles as itself.
        /// </summary>
        internal sealed class Tile : Primitive
        {
            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear);
                int[] rect = source.Subregion;
                if (rect == null) return source;
                int x0 = Math.Max(0, rect[0]), y0 = Math.Max(0, rect[1]);
                int w = Math.Min(width, rect[2]) - x0, h = Math.Min(height, rect[3]) - y0;
                var result = new Image(width, height, Linear);
                if (w <= 0 || h <= 0) return result;
                for (int y = 0; y < height; y++)
                {
                    int sy = y0 + (((y - y0) % h) + h) % h;
                    for (int x = 0; x < width; x++)
                    {
                        int sx = x0 + (((x - x0) % w) + w) % w;
                        Array.Copy(source.Px, (sy * width + sx) * 4, result.Px, (y * width + x) * 4, 4);
                    }
                }
                return result;
            }
        }

        /// <summary><c>feImage</c> (§9.14): a picture rendered by the converter, in sRGB; none where it could not be, transparent black.</summary>
        internal sealed class Picture : Primitive
        {
            public Image Rendered;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
                => Rendered == null ? new Image(width, height, Linear) : Rendered.In(Linear);
        }

        /// <summary>
        /// <c>feTurbulence</c> (§9.21): Perlin turbulence or fractal noise at each pixel's point in
        /// user space, by the specification's reference algorithm — its lattice, its congruential
        /// generator, its octaves and its stitching — so that a seed gives the picture every other
        /// conforming implementation gives.
        /// </summary>
        /// <remarks>
        /// <para>Each channel is its own noise (four gradient tables). <c>fractalNoise</c> is (sum + 1) / 2,
        /// <c>turbulence</c> the sum of absolute values; the four values are the pixel, straight, in
        /// the primitive's space (§9.5), premultiplied here.</para>
        /// <para>Three departures from the reference C, each where it is not a specification: a
        /// gradient that came out (0, 0) is left so rather than divided by zero; a lattice coordinate
        /// past an <c>int</c> is noise 0 rather than undefined; and the lattice coordinate is masked
        /// <em>after</em> the stitch adjustment, as browsers read it — masked before, as the C prints
        /// it, the adjustment could never fire, since the wrap point carries PerlinN.</para>
        /// </remarks>
        internal sealed class Turbulence : Primitive
        {
            public float BaseFreqX, BaseFreqY;
            public int Octaves = 1;
            public long Seed;
            public bool FractalNoise;
            /// <summary><c>stitchTiles="stitch"</c>: the noise repeats across the tile, so its edges meet.</summary>
            public bool Stitch;
            /// <summary>The tile in user space — the primitive subregion, the filter region by default.</summary>
            public double TileX, TileY, TileWidth, TileHeight;
            /// <summary>A pixel's centre to user space: x' = a·x + c·y + e, y' = b·x + d·y + f.</summary>
            public double[] ToUser = { 1, 0, 0, 1, 0, 0 };

            private const int BSize = 0x100, BM = 0xff, PerlinN = 0x1000;
            private const long RandM = 2147483647, RandA = 16807, RandQ = 127773, RandR = 2836;
            private int[] _lattice;
            /// <summary>Per channel, the gradient of lattice index i at [2i], [2i + 1].</summary>
            private double[][] _gradient;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Init(Seed);
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                var straight = new float[4];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        double cx = x + 0.5, cy = y + 0.5;
                        Sample(ToUser[0] * cx + ToUser[2] * cy + ToUser[4], ToUser[1] * cx + ToUser[3] * cy + ToUser[5], straight);
                        int i = (y * width + x) * 4;
                        float a = straight[3];
                        for (int c = 0; c < 3; c++) px[i + c] = straight[c] * a;
                        px[i + 3] = a;
                    }
                return result;
            }

            private static long SetupSeed(long seed)
            {
                if (seed <= 0) seed = -(seed % (RandM - 1)) + 1;
                if (seed > RandM - 1) seed = RandM - 1;
                return seed;
            }

            private static long Random(long seed)
            {
                long result = RandA * (seed % RandQ) - RandR * (seed / RandQ);
                if (result <= 0) result += RandM;
                return result;
            }

            /// <summary>The lattice and the four gradient tables for <paramref name="seed"/>, as the reference's <c>init</c> builds them.</summary>
            private void Init(long seed)
            {
                _lattice = new int[BSize + BSize + 2];
                _gradient = new double[4][];
                for (int k = 0; k < 4; k++) _gradient[k] = new double[(BSize + BSize + 2) * 2];
                seed = SetupSeed(seed);
                int i, j = 0;
                for (int k = 0; k < 4; k++)
                    for (i = 0; i < BSize; i++)
                    {
                        _lattice[i] = i;
                        for (j = 0; j < 2; j++)
                            _gradient[k][i * 2 + j] = (double)(((seed = Random(seed)) % (BSize + BSize)) - BSize) / BSize;
                        double s = Math.Sqrt(_gradient[k][i * 2] * _gradient[k][i * 2] + _gradient[k][i * 2 + 1] * _gradient[k][i * 2 + 1]);
                        if (s > 0)
                        {
                            _gradient[k][i * 2] /= s;
                            _gradient[k][i * 2 + 1] /= s;
                        }
                    }
                for (i = BSize - 1; i > 0; i--)
                {
                    int k = _lattice[i];
                    _lattice[i] = _lattice[j = (int)((seed = Random(seed)) % BSize)];
                    _lattice[j] = k;
                }
                for (i = 0; i < BSize + 2; i++)
                {
                    _lattice[BSize + i] = _lattice[i];
                    for (int k = 0; k < 4; k++)
                        for (j = 0; j < 2; j++)
                            _gradient[k][(BSize + i) * 2 + j] = _gradient[k][i * 2 + j];
                }
            }

            /// <summary>The four channels of the noise at the user-space point (<paramref name="ux"/>, <paramref name="uy"/>), straight, into <paramref name="into"/>.</summary>
            private void Sample(double ux, double uy, float[] into)
            {
                double fx = BaseFreqX, fy = BaseFreqY;
                bool stitch = Stitch && TileWidth > 0 && TileHeight > 0;
                int tileW = 0, tileH = 0, wrapX = 0, wrapY = 0;
                if (stitch)
                {
                    // The frequencies are moved to the nearer of the two at which the tile holds a
                    // whole number of lattice cells, so that its edges continue.
                    fx = StitchFrequency(fx, TileWidth);
                    fy = StitchFrequency(fy, TileHeight);
                    tileW = SaturatingCast.ToInt(TileWidth * fx + 0.5);
                    wrapX = SaturatingCast.ToInt(TileX * fx + PerlinN + tileW);
                    tileH = SaturatingCast.ToInt(TileHeight * fy + 0.5);
                    wrapY = SaturatingCast.ToInt(TileY * fy + PerlinN + tileH);
                }
                for (int channel = 0; channel < 4; channel++)
                {
                    double sum = 0, vx = ux * fx, vy = uy * fy, ratio = 1;
                    int w = tileW, h = tileH, wx = wrapX, wy = wrapY;
                    for (int octave = 0; octave < Octaves; octave++)
                    {
                        double n = Noise2(channel, vx, vy, stitch, w, h, wx, wy);
                        sum += (FractalNoise ? n : Math.Abs(n)) / ratio;
                        vx *= 2; vy *= 2; ratio *= 2;
                        if (stitch)
                        {
                            w *= 2; wx = 2 * wx - PerlinN;
                            h *= 2; wy = 2 * wy - PerlinN;
                        }
                    }
                    into[channel] = Clamp01((float)(FractalNoise ? (sum + 1) / 2 : sum));
                }
            }

            private static double StitchFrequency(double frequency, double tile)
            {
                if (frequency == 0) return 0;
                double lo = Math.Floor(tile * frequency) / tile, hi = Math.Ceiling(tile * frequency) / tile;
                return frequency / lo < hi / frequency ? lo : hi;
            }

            private static double SCurve(double t) => t * t * (3 - 2 * t);
            private static double Lerp(double t, double a, double b) => a + t * (b - a);

            /// <summary>The reference's <c>noise2</c>: gradient noise for one channel at the frequency-scaled point.</summary>
            private double Noise2(int channel, double vx, double vy, bool stitch, int tileW, int tileH, int wrapX, int wrapY)
            {
                double t = vx + PerlinN;
                if (!(t > int.MinValue && t < int.MaxValue)) return 0;
                int bx0 = SaturatingCast.ToInt(t), bx1 = bx0 + 1;
                double rx0 = t - SaturatingCast.ToInt(t), rx1 = rx0 - 1;
                t = vy + PerlinN;
                if (!(t > int.MinValue && t < int.MaxValue)) return 0;
                int by0 = SaturatingCast.ToInt(t), by1 = by0 + 1;
                double ry0 = t - SaturatingCast.ToInt(t), ry1 = ry0 - 1;
                if (stitch)
                {
                    if (bx0 >= wrapX) bx0 -= tileW;
                    if (bx1 >= wrapX) bx1 -= tileW;
                    if (by0 >= wrapY) by0 -= tileH;
                    if (by1 >= wrapY) by1 -= tileH;
                }
                bx0 &= BM; bx1 &= BM; by0 &= BM; by1 &= BM;
                int i = _lattice[bx0], j = _lattice[bx1];
                int b00 = _lattice[i + by0], b10 = _lattice[j + by0], b01 = _lattice[i + by1], b11 = _lattice[j + by1];
                double sx = SCurve(rx0), sy = SCurve(ry0);
                double[] g = _gradient[channel];
                double u = rx0 * g[b00 * 2] + ry0 * g[b00 * 2 + 1];
                double v = rx1 * g[b10 * 2] + ry0 * g[b10 * 2 + 1];
                double a = Lerp(sx, u, v);
                u = rx0 * g[b01 * 2] + ry1 * g[b01 * 2 + 1];
                v = rx1 * g[b11 * 2] + ry1 * g[b11 * 2 + 1];
                double b = Lerp(sx, u, v);
                return Lerp(sy, a, b);
            }
        }

        /// <summary>
        /// <c>feDisplacementMap</c> (§9.12): each pixel of <c>in</c> read from where <c>in2</c>'s
        /// channels send it — P'(x, y) = P(x + scale·(XC(x, y) − ½), y + scale·(YC(x, y) − ½)) — the
        /// map's channels straight in this primitive's space, the nearest source pixel taken, outside
        /// the image transparent.
        /// </summary>
        /// <remarks>
        /// The scale is a user-space length; the converter hands it on as the pixel vector a unit of
        /// XC − ½ displaces by and the one a unit of YC − ½ does, so a rotated or scaled element is
        /// displaced along its own axes. §9.12 leaves <c>in</c> in its own colour space; it is read
        /// here in the primitive's and the result left there, a round trip through <see cref="Image.In"/>.
        /// </remarks>
        internal sealed class DisplacementMap : Primitive
        {
            /// <summary>Pixels displaced per unit of XC − ½, along x and along y; and per unit of YC − ½.</summary>
            public float XDx, XDy, YDx, YDy;
            /// <summary>The map channel read for each axis: 0 to 3 for R, G, B, A (the default).</summary>
            public int XChannel = 3, YChannel = 3;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear), map = input(In2, Linear);
                var result = new Image(width, height, Linear);
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        float a = map.Px[i + 3];
                        float Channel(int c) => c == 3 ? a : a > 0 ? Math.Min(1f, map.Px[i + c] / a) : 0;
                        float xc = Channel(XChannel) - 0.5f, yc = Channel(YChannel) - 0.5f;
                        double sx = x + 0.5 + XDx * xc + YDx * yc, sy = y + 0.5 + XDy * xc + YDy * yc;
                        if (!(sx >= 0 && sx < width && sy >= 0 && sy < height)) continue;
                        Array.Copy(source.Px, (SaturatingCast.ToInt(sy) * width + SaturatingCast.ToInt(sx)) * 4, result.Px, i, 4);
                    }
                return result;
            }
        }

        /// <summary>
        /// <c>feDiffuseLighting</c> and <c>feSpecularLighting</c> (§9.10, §9.11): the input's alpha as a
        /// height field, lit by one light — distant, point or spot — with the Phong terms the
        /// specification writes. Diffuse is kd·(N·L)·light, opaque; specular is ks·(N·H)^exp·light
        /// with alpha the largest channel, which is a premultiplied pixel. The surface normal is the
        /// specification's Sobel kernels with its edge and corner factors.
        /// </summary>
        /// <remarks>
        /// Every position here is in pixels — the converter maps a light's user-space place, its z at
        /// the mapping's mean scale — and a pixel is its centre. The surface's height is
        /// surfaceScale × alpha, unscaled, as browsers take it. An image narrower or shorter than two
        /// pixels has no slope: its normal is flat. <c>kernelUnitLength</c> is not read, as
        /// <c>feConvolveMatrix</c>'s is not. A shade that is not a number is dark.
        /// </remarks>
        internal sealed class Lighting : Primitive
        {
            internal enum LightKind { Distant, Point, Spot }

            public bool Specular;
            public float SurfaceScale = 1;
            /// <summary>kd or ks; and specularExponent (1 to 128).</summary>
            public float Constant = 1, Exponent = 1;
            /// <summary>The light's colour, straight sRGB (<c>lighting-color</c>).</summary>
            public float R = 1, G = 1, B = 1;
            public LightKind Kind;
            /// <summary>A distant light's direction, in degrees: azimuth clockwise from x in the image plane, elevation out of it.</summary>
            public float Azimuth, Elevation;
            /// <summary>A point or spot light's place in pixels, z out of the image; a spot's target, its focus (its specularExponent) and its cone's half-angle in degrees (NaN for none).</summary>
            public double X, Y, Z, PointsAtX, PointsAtY, PointsAtZ, Focus = 1, ConeAngle = double.NaN;

            public override Image Apply(Func<string, bool, Image> input, int width, int height)
            {
                Image source = input(In, Linear);
                var result = new Image(width, height, Linear);
                float[] px = result.Px;
                float lr = Linear ? ToLinear(R) : R, lg = Linear ? ToLinear(G) : G, lb = Linear ? ToLinear(B) : B;
                double az = Azimuth * Math.PI / 180, el = Elevation * Math.PI / 180;
                double dlx = Math.Cos(az) * Math.Cos(el), dly = Math.Sin(az) * Math.Cos(el), dlz = Math.Sin(el);
                // A spot's axis: from the light to what it points at.
                double ax = PointsAtX - X, ay = PointsAtY - Y, az2 = PointsAtZ - Z;
                double alen = Math.Sqrt(ax * ax + ay * ay + az2 * az2);
                if (alen > 0) { ax /= alen; ay /= alen; az2 /= alen; }
                double cosCone = double.IsNaN(ConeAngle) ? -1 : Math.Cos(ConeAngle * Math.PI / 180);
                bool flat = width < 2 || height < 2;

                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        double nx = 0, ny = 0;
                        if (!flat) Normal(source, x, y, out nx, out ny);
                        double nlen = Math.Sqrt(nx * nx + ny * ny + 1);
                        nx /= nlen; ny /= nlen;
                        double nz = 1 / nlen;

                        double lx = dlx, ly = dly, lz = dlz, intensity = 1;
                        if (Kind != LightKind.Distant)
                        {
                            lx = X - (x + 0.5); ly = Y - (y + 0.5); lz = Z - SurfaceScale * source.Px[i + 3];
                            double llen = Math.Sqrt(lx * lx + ly * ly + lz * lz);
                            if (llen > 0) { lx /= llen; ly /= llen; lz /= llen; } else { lx = 0; ly = 0; lz = 1; }
                            if (Kind == LightKind.Spot)
                            {
                                // −L·S: the cosine between the light's axis and its way to the surface;
                                // nothing outside the cone, the focus's power inside it.
                                double along = -(lx * ax + ly * ay + lz * az2);
                                intensity = along <= 0 || along < cosCone ? 0 : Math.Pow(along, Focus);
                            }
                        }

                        double shade;
                        if (!Specular)
                            shade = Constant * (nx * lx + ny * ly + nz * lz);
                        else
                        {
                            // H: halfway between the light and the eye at (0, 0, 1).
                            double hx = lx, hy = ly, hz = lz + 1;
                            double hlen = Math.Sqrt(hx * hx + hy * hy + hz * hz);
                            double nh = hlen > 0 ? (nx * hx + ny * hy + nz * hz) / hlen : 1;
                            shade = Constant * Math.Pow(Math.Max(0, nh), Exponent);
                        }
                        // A surface facing away is negative here and clamps to black below; a shade
                        // that is not a number — a surface scale or exponent that is not — is dark.
                        shade *= intensity;
                        if (double.IsNaN(shade)) shade = 0;

                        float r = Clamp01((float)(shade * lr)), g = Clamp01((float)(shade * lg)), b = Clamp01((float)(shade * lb));
                        px[i] = r; px[i + 1] = g; px[i + 2] = b;
                        px[i + 3] = Specular ? Math.Max(r, Math.Max(g, b)) : 1;
                    }
                return result;
            }

            /// <summary>
            /// The surface normal's x and y before normalisation (§9.10 "Surface normal"): the Sobel
            /// columns and rows of the alpha, with the specification's factor for the interior (¼), an
            /// edge (⅓ along it, ½ across it) and a corner (⅔), times −surfaceScale.
            /// </summary>
            private void Normal(Image s, int x, int y, out double nx, out double ny)
            {
                int w = s.Width, h = s.Height;
                double I(int px, int py) => s.Px[(py * w + px) * 4 + 3];
                bool left = x == 0, right = x == w - 1, top = y == 0, bottom = y == h - 1;
                double fx, fy, gx, gy;
                if (left && top)
                {
                    fx = fy = 2.0 / 3;
                    gx = (2 * I(x + 1, y) + I(x + 1, y + 1)) - (2 * I(x, y) + I(x, y + 1));
                    gy = (2 * I(x, y + 1) + I(x + 1, y + 1)) - (2 * I(x, y) + I(x + 1, y));
                }
                else if (right && top)
                {
                    fx = fy = 2.0 / 3;
                    gx = (2 * I(x, y) + I(x, y + 1)) - (2 * I(x - 1, y) + I(x - 1, y + 1));
                    gy = (I(x - 1, y + 1) + 2 * I(x, y + 1)) - (I(x - 1, y) + 2 * I(x, y));
                }
                else if (left && bottom)
                {
                    fx = fy = 2.0 / 3;
                    gx = (I(x + 1, y - 1) + 2 * I(x + 1, y)) - (I(x, y - 1) + 2 * I(x, y));
                    gy = (2 * I(x, y) + I(x + 1, y)) - (2 * I(x, y - 1) + I(x + 1, y - 1));
                }
                else if (right && bottom)
                {
                    fx = fy = 2.0 / 3;
                    gx = (I(x, y - 1) + 2 * I(x, y)) - (I(x - 1, y - 1) + 2 * I(x - 1, y));
                    gy = (I(x - 1, y) + 2 * I(x, y)) - (I(x - 1, y - 1) + 2 * I(x, y - 1));
                }
                else if (top)
                {
                    fx = 1.0 / 3; fy = 1.0 / 2;
                    gx = (2 * I(x + 1, y) + I(x + 1, y + 1)) - (2 * I(x - 1, y) + I(x - 1, y + 1));
                    gy = (I(x - 1, y + 1) + 2 * I(x, y + 1) + I(x + 1, y + 1)) - (I(x - 1, y) + 2 * I(x, y) + I(x + 1, y));
                }
                else if (bottom)
                {
                    fx = 1.0 / 3; fy = 1.0 / 2;
                    gx = (I(x + 1, y - 1) + 2 * I(x + 1, y)) - (I(x - 1, y - 1) + 2 * I(x - 1, y));
                    gy = (I(x - 1, y) + 2 * I(x, y) + I(x + 1, y)) - (I(x - 1, y - 1) + 2 * I(x, y - 1) + I(x + 1, y - 1));
                }
                else if (left)
                {
                    fx = 1.0 / 2; fy = 1.0 / 3;
                    gx = (I(x + 1, y - 1) + 2 * I(x + 1, y) + I(x + 1, y + 1)) - (I(x, y - 1) + 2 * I(x, y) + I(x, y + 1));
                    gy = (2 * I(x, y + 1) + I(x + 1, y + 1)) - (2 * I(x, y - 1) + I(x + 1, y - 1));
                }
                else if (right)
                {
                    fx = 1.0 / 2; fy = 1.0 / 3;
                    gx = (I(x, y - 1) + 2 * I(x, y) + I(x, y + 1)) - (I(x - 1, y - 1) + 2 * I(x - 1, y) + I(x - 1, y + 1));
                    gy = (I(x - 1, y + 1) + 2 * I(x, y + 1)) - (I(x - 1, y - 1) + 2 * I(x, y - 1));
                }
                else
                {
                    fx = fy = 1.0 / 4;
                    gx = (I(x + 1, y - 1) + 2 * I(x + 1, y) + I(x + 1, y + 1)) - (I(x - 1, y - 1) + 2 * I(x - 1, y) + I(x - 1, y + 1));
                    gy = (I(x - 1, y + 1) + 2 * I(x, y + 1) + I(x + 1, y + 1)) - (I(x - 1, y - 1) + 2 * I(x, y - 1) + I(x + 1, y - 1));
                }
                nx = -SurfaceScale * fx * gx;
                ny = -SurfaceScale * fy * gy;
            }
        }

        /// <summary>
        /// Runs <paramref name="primitives"/> over <paramref name="source"/> (sRGB): each reads its
        /// inputs — <c>SourceGraphic</c>, <c>SourceAlpha</c>, an earlier result by name, or by default
        /// the previous primitive's, the source for the first — and the last one's result is the
        /// filter's, back in sRGB.
        /// </summary>
        /// <remarks>
        /// A name that no earlier primitive gave is read as the default, as §9.2 says; a keyword this
        /// does not model (<c>BackgroundImage</c>, <c>FillPaint</c>) is transparent black, reported
        /// through <paramref name="unmodelled"/>.
        /// </remarks>
        internal static Image Run(IReadOnlyList<Primitive> primitives, Image source, Action<string> unmodelled)
        {
            int width = source.Width, height = source.Height;
            Image alpha = null;
            var results = new Dictionary<string, Image>(StringComparer.Ordinal);
            Image previous = source;

            foreach (Primitive primitive in primitives)
            {
                Image current = previous;
                Image Input(string name, bool linear)
                {
                    switch (name)
                    {
                        case null: case "": return current.In(linear);
                        case "SourceGraphic": return source.In(linear);
                        case "SourceAlpha": return alpha ??= AlphaOf(source);
                        case "BackgroundImage": case "BackgroundAlpha": case "FillPaint": case "StrokePaint":
                            unmodelled(name);
                            return new Image(width, height, linear);
                    }
                    return results.TryGetValue(name, out Image named) ? named.In(linear) : current.In(linear);
                }

                Image result = primitive.Apply(Input, width, height);
                if (primitive.Subregion != null) ClipTo(result, primitive.Subregion);
                result.Subregion = primitive.Subregion;
                if (!string.IsNullOrEmpty(primitive.Result)) results[primitive.Result] = result;
                previous = result;
            }
            return previous.In(linear: false);
        }

        /// <summary><c>SourceAlpha</c>: black, the source's alpha. Black is black in either space.</summary>
        private static Image AlphaOf(Image source)
        {
            var alpha = new Image(source.Width, source.Height, source.Linear);
            for (int i = 3; i < alpha.Px.Length; i += 4) alpha.Px[i] = source.Px[i];
            return alpha;
        }

        /// <summary>Transparent black outside <paramref name="rect"/> (x0, y0, x1, y1).</summary>
        private static void ClipTo(Image image, int[] rect)
        {
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                    if (x < rect[0] || x >= rect[2] || y < rect[1] || y >= rect[3])
                        Array.Clear(image.Px, (y * image.Width + x) * 4, 4);
        }

        /// <summary>
        /// Three box blurs along one axis, standing in for a Gaussian of <paramref name="std"/> (§9.13):
        /// with d = ⌊s·3·√(2π)/4 + 0.5⌋, three of size d centred when d is odd; when it is even, two of
        /// size d centred on the boundaries either side of the pixel, and one of d + 1 centred.
        /// </summary>
        private static void BoxBlurs(Image image, float std, bool horizontal)
        {
            if (!(std > 0)) return;
            int d = SaturatingCast.ToInt(Math.Floor(std * 3 * Math.Sqrt(2 * Math.PI) / 4 + 0.5));
            if (d < 1) return;
            int lines = horizontal ? image.Height : image.Width, length = horizontal ? image.Width : image.Height;
            int step = horizontal ? 4 : image.Width * 4;
            var line = new float[length * 4];
            var scratch = new float[length * 4];
            for (int l = 0; l < lines; l++)
            {
                int start = horizontal ? l * image.Width * 4 : l * 4;
                for (int i = 0; i < length; i++) Array.Copy(image.Px, start + i * step, line, i * 4, 4);
                if (d % 2 == 1)
                {
                    int r = d / 2;
                    Box(line, scratch, length, r, r);
                    Box(scratch, line, length, r, r);
                    Box(line, scratch, length, r, r);
                }
                else
                {
                    int h = d / 2;
                    Box(line, scratch, length, h, h - 1);
                    Box(scratch, line, length, h - 1, h);
                    Box(line, scratch, length, h, h);
                }
                for (int i = 0; i < length; i++) Array.Copy(scratch, i * 4, image.Px, start + i * step, 4);
            }
        }

        /// <summary>
        /// One box blur of a line of RGBA pixels: each output the mean of the <paramref name="before"/>
        /// pixels before it, itself and the <paramref name="after"/> after it, outside the line
        /// transparent. A running sum: the work is the line's length, whatever the box's.
        /// </summary>
        private static void Box(float[] from, float[] to, int length, int before, int after)
        {
            float size = before + after + 1;
            for (int c = 0; c < 4; c++)
            {
                double sum = 0;
                for (int i = -before; i <= after; i++)
                    if (i >= 0 && i < length) sum += from[i * 4 + c];
                for (int i = 0; i < length; i++)
                {
                    to[i * 4 + c] = (float)(sum / size);
                    int enter = i + after + 1, leave = i - before;
                    if (enter < length) sum += from[enter * 4 + c];
                    if (leave >= 0) sum -= from[leave * 4 + c];
                }
            }
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
