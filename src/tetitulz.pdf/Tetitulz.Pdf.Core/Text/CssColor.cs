// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// A CSS colour value as red, green, blue and alpha in [0, 1]: <c>#rgb</c>, <c>#rgba</c>,
    /// <c>#rrggbb</c>, <c>#rrggbbaa</c>, <c>rgb()</c>/<c>rgba()</c> with numbers or percentages,
    /// <c>hsl()</c>/<c>hsla()</c>, and the 148 named colours of CSS Color Module Level 4.
    /// </summary>
    /// <remarks>
    /// <para><b>One reading, for HTML and SVG.</b> This was HtmlToPdf's, private to its mapper.
    /// <c>SvgToPdfConverter</c> had its own: sixteen names, no percentages, no <c>hsl()</c>, and
    /// black for everything else without a report. SVG 1.1 §4.4 takes its colour keywords from CSS,
    /// so <c>fill="steelblue"</c> is the same colour in both.</para>
    /// <para><b>Digits are ASCII.</b> The functional forms matched <c>\d</c>, which in .NET is any
    /// Unicode decimal digit. The number parse behind it then failed and read 0, so a value that is
    /// not a colour came back as one.</para>
    /// <para>Returning <c>false</c> is what makes an unsupported colour <i>absent</i> rather than
    /// <i>wrong</i>. Each caller decides what absent means: HTML drops the declaration and the
    /// inherited value stands; SVG reports it and draws black.</para>
    /// <para>Internal. HtmlToPdf reaches it through Core's <c>InternalsVisibleTo</c>, as DocxToPdf
    /// reaches <c>FontFileLocator</c>.</para>
    /// </remarks>
    internal static class CssColor
    {
        /// <summary>
        /// The CSS Color Module Level 4 named colours, as <c>#RRGGBB</c>. Case-insensitive, as CSS
        /// keywords are.
        /// </summary>
        private static readonly Dictionary<string, string> NamedColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["aliceblue"] = "#F0F8FF", ["antiquewhite"] = "#FAEBD7", ["aqua"] = "#00FFFF",
            ["aquamarine"] = "#7FFFD4", ["azure"] = "#F0FFFF", ["beige"] = "#F5F5DC",
            ["bisque"] = "#FFE4C4", ["black"] = "#000000", ["blanchedalmond"] = "#FFEBCD",
            ["blue"] = "#0000FF", ["blueviolet"] = "#8A2BE2", ["brown"] = "#A52A2A",
            ["burlywood"] = "#DEB887", ["cadetblue"] = "#5F9EA0", ["chartreuse"] = "#7FFF00",
            ["chocolate"] = "#D2691E", ["coral"] = "#FF7F50", ["cornflowerblue"] = "#6495ED",
            ["cornsilk"] = "#FFF8DC", ["crimson"] = "#DC143C", ["cyan"] = "#00FFFF",
            ["darkblue"] = "#00008B", ["darkcyan"] = "#008B8B", ["darkgoldenrod"] = "#B8860B",
            ["darkgray"] = "#A9A9A9", ["darkgrey"] = "#A9A9A9", ["darkgreen"] = "#006400",
            ["darkkhaki"] = "#BDB76B", ["darkmagenta"] = "#8B008B", ["darkolivegreen"] = "#556B2F",
            ["darkorange"] = "#FF8C00", ["darkorchid"] = "#9932CC", ["darkred"] = "#8B0000",
            ["darksalmon"] = "#E9967A", ["darkseagreen"] = "#8FBC8F", ["darkslateblue"] = "#483D8B",
            ["darkslategray"] = "#2F4F4F", ["darkslategrey"] = "#2F4F4F", ["darkturquoise"] = "#00CED1",
            ["darkviolet"] = "#9400D3", ["deeppink"] = "#FF1493", ["deepskyblue"] = "#00BFFF",
            ["dimgray"] = "#696969", ["dimgrey"] = "#696969", ["dodgerblue"] = "#1E90FF",
            ["firebrick"] = "#B22222", ["floralwhite"] = "#FFFAF0", ["forestgreen"] = "#228B22",
            ["fuchsia"] = "#FF00FF", ["gainsboro"] = "#DCDCDC", ["ghostwhite"] = "#F8F8FF",
            ["gold"] = "#FFD700", ["goldenrod"] = "#DAA520", ["gray"] = "#808080",
            ["grey"] = "#808080", ["green"] = "#008000", ["greenyellow"] = "#ADFF2F",
            ["honeydew"] = "#F0FFF0", ["hotpink"] = "#FF69B4", ["indianred"] = "#CD5C5C",
            ["indigo"] = "#4B0082", ["ivory"] = "#FFFFF0", ["khaki"] = "#F0E68C",
            ["lavender"] = "#E6E6FA", ["lavenderblush"] = "#FFF0F5", ["lawngreen"] = "#7CFC00",
            ["lemonchiffon"] = "#FFFACD", ["lightblue"] = "#ADD8E6", ["lightcoral"] = "#F08080",
            ["lightcyan"] = "#E0FFFF", ["lightgoldenrodyellow"] = "#FAFAD2", ["lightgray"] = "#D3D3D3",
            ["lightgrey"] = "#D3D3D3", ["lightgreen"] = "#90EE90", ["lightpink"] = "#FFB6C1",
            ["lightsalmon"] = "#FFA07A", ["lightseagreen"] = "#20B2AA", ["lightskyblue"] = "#87CEFA",
            ["lightslategray"] = "#778899", ["lightslategrey"] = "#778899", ["lightsteelblue"] = "#B0C4DE",
            ["lightyellow"] = "#FFFFE0", ["lime"] = "#00FF00", ["limegreen"] = "#32CD32",
            ["linen"] = "#FAF0E6", ["magenta"] = "#FF00FF", ["maroon"] = "#800000",
            ["mediumaquamarine"] = "#66CDAA", ["mediumblue"] = "#0000CD", ["mediumorchid"] = "#BA55D3",
            ["mediumpurple"] = "#9370DB", ["mediumseagreen"] = "#3CB371", ["mediumslateblue"] = "#7B68EE",
            ["mediumspringgreen"] = "#00FA9A", ["mediumturquoise"] = "#48D1CC", ["mediumvioletred"] = "#C71585",
            ["midnightblue"] = "#191970", ["mintcream"] = "#F5FFFA", ["mistyrose"] = "#FFE4E1",
            ["moccasin"] = "#FFE4B5", ["navajowhite"] = "#FFDEAD", ["navy"] = "#000080",
            ["oldlace"] = "#FDF5E6", ["olive"] = "#808000", ["olivedrab"] = "#6B8E23",
            ["orange"] = "#FFA500", ["orangered"] = "#FF4500", ["orchid"] = "#DA70D6",
            ["palegoldenrod"] = "#EEE8AA", ["palegreen"] = "#98FB98", ["paleturquoise"] = "#AFEEEE",
            ["palevioletred"] = "#DB7093", ["papayawhip"] = "#FFEFD5", ["peachpuff"] = "#FFDAB9",
            ["peru"] = "#CD853F", ["pink"] = "#FFC0CB", ["plum"] = "#DDA0DD",
            ["powderblue"] = "#B0E0E6", ["purple"] = "#800080", ["rebeccapurple"] = "#663399",
            ["red"] = "#FF0000", ["rosybrown"] = "#BC8F8F", ["royalblue"] = "#4169E1",
            ["saddlebrown"] = "#8B4513", ["salmon"] = "#FA8072", ["sandybrown"] = "#F4A460",
            ["seagreen"] = "#2E8B57", ["seashell"] = "#FFF5EE", ["sienna"] = "#A0522D",
            ["silver"] = "#C0C0C0", ["skyblue"] = "#87CEEB", ["slateblue"] = "#6A5ACD",
            ["slategray"] = "#708090", ["slategrey"] = "#708090", ["snow"] = "#FFFAFA",
            ["springgreen"] = "#00FF7F", ["steelblue"] = "#4682B4", ["tan"] = "#D2B48C",
            ["teal"] = "#008080", ["thistle"] = "#D8BFD8", ["tomato"] = "#FF6347",
            ["turquoise"] = "#40E0D0", ["violet"] = "#EE82EE", ["wheat"] = "#F5DEB3",
            ["white"] = "#FFFFFF", ["whitesmoke"] = "#F5F5F5", ["yellow"] = "#FFFF00",
            ["yellowgreen"] = "#9ACD32",
        };

        /// <summary>
        /// Longer than any colour this reads — the longest name is 20 characters, an <c>rgba()</c>
        /// with four decimal components and the spaces CSS allows well under 64. A value past it is
        /// not a colour, and is refused before either pattern sees it: the separator idiom
        /// <c>\s*[,\s]\s*</c> splits a run of n spaces n ways, each split backtracked, so a value of
        /// a million spaces would cost a million-squared steps. The cap is the guard; the timeout
        /// below is the belt.
        /// </summary>
        internal const int MaxLength = 64;

        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        /// <summary><c>rgb()</c>/<c>rgba()</c>, comma- or space-separated, alpha after <c>,</c> or <c>/</c>.</summary>
        private static readonly Regex RgbPattern =
            new Regex(@"^rgba?\(\s*([0-9.]+%?)\s*[,\s]\s*([0-9.]+%?)\s*[,\s]\s*([0-9.]+%?)\s*(?:[,/]\s*([0-9.]+%?)\s*)?\)$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

        /// <summary><c>hsl()</c>/<c>hsla()</c>: a hue in degrees, then saturation and lightness as percentages.</summary>
        private static readonly Regex HslPattern =
            new Regex(@"^hsla?\(\s*([0-9.]+)(?:deg)?\s*[,\s]\s*([0-9.]+)%\s*[,\s]\s*([0-9.]+)%\s*(?:[,/]\s*([0-9.]+%?)\s*)?\)$",
                      RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);

        /// <summary>
        /// Parses <paramref name="value"/>; <c>false</c> when it is not a colour this reads. The
        /// components are in [0, 1] except an <c>hsl()</c> lightness above 100%, which a caller
        /// clamps.
        /// </summary>
        internal static bool TryParse(string value, out double r, out double g, out double b, out double a)
        {
            r = g = b = 0; a = 1;
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim();
            if (v.Length > MaxLength) return false;

            if (v.StartsWith("#", StringComparison.Ordinal))
            {
                string h = v.Substring(1);
                // The length and digit checks are exact on purpose. Accepting any string of 6+
                // characters after the '#' and reading alpha from positions 6-7 whatever they are,
                // the background shorthand "#eeeeee url(bg.png)" would parse as #eeeeee with alpha
                // taken from " u" — zero — and composite to WHITE. A trailing component must
                // make the value not-a-colour, not change its alpha.
                if (!IsHexDigits(h)) return false;
                switch (h.Length)
                {
                    case 3: r = Hex1(h[0]); g = Hex1(h[1]); b = Hex1(h[2]); return true;
                    case 4: r = Hex1(h[0]); g = Hex1(h[1]); b = Hex1(h[2]); a = Hex1(h[3]); return true;
                    case 6: r = Hex2(h, 0); g = Hex2(h, 2); b = Hex2(h, 4); return true;
                    case 8: r = Hex2(h, 0); g = Hex2(h, 2); b = Hex2(h, 4); a = Hex2(h, 6); return true;
                    default: return false;
                }
            }

            var rgb = RgbPattern.Match(v);
            if (rgb.Success)
            {
                r = Comp(rgb.Groups[1].Value); g = Comp(rgb.Groups[2].Value); b = Comp(rgb.Groups[3].Value);
                if (rgb.Groups[4].Success) a = Alpha(rgb.Groups[4].Value);
                return true;
            }

            var hsl = HslPattern.Match(v);
            if (hsl.Success)
            {
                (r, g, b) = HslToRgb(D(hsl.Groups[1].Value), D(hsl.Groups[2].Value) / 100, D(hsl.Groups[3].Value) / 100);
                if (hsl.Groups[4].Success) a = Alpha(hsl.Groups[4].Value);
                return true;
            }

            if (NamedColors.TryGetValue(v, out var hex))
            {
                string hh = hex.Substring(1);
                r = Hex2(hh, 0); g = Hex2(hh, 2); b = Hex2(hh, 4);
                return true;
            }

            // CSS Color 3 §4.2.3: the keyword is rgba(0, 0, 0, 0). HtmlToPdf recognised it by name at
            // two sites and the SVG converter not at all, so fill="transparent" filled black. Each
            // caller now decides what an alpha of 0 means; both paint nothing.
            if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            {
                a = 0;
                return true;
            }
            return false;
        }

        /// <summary>
        /// <c>true</c> when <paramref name="value"/> is a colour with an alpha of 0 — the
        /// <c>transparent</c> keyword, <c>rgba(…, 0)</c>, <c>#rrggbb00</c> — which paints nothing.
        /// </summary>
        internal static bool IsFullyTransparent(string value)
            => TryParse(value, out _, out _, out _, out double a) && a <= 0;

        /// <summary>
        /// A CSS colour as the <c>"#RRGGBB"</c> a document model holds, or <c>null</c> when it is not a
        /// colour this reads or paints nothing (alpha 0) — so the declaration is dropped and what was
        /// inherited stands, rather than a wrong colour painted. A translucent colour is composited over
        /// white, approximating how it looks on a page. The HTML converter and XFA's rich text alike.
        /// </summary>
        internal static string ToOpaqueHex(string value)
        {
            if (value == null || !TryParse(value, out double r, out double g, out double b, out double a) || a <= 0) return null;
            if (a < 1) { r = r * a + (1 - a); g = g * a + (1 - a); b = b * a + (1 - a); }
            int R = SaturatingCast.ToInt(Math.Round(Clamp01(r) * 255));
            int G = SaturatingCast.ToInt(Math.Round(Clamp01(g) * 255));
            int B = SaturatingCast.ToInt(Math.Round(Clamp01(b) * 255));
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", R, G, B);
        }

        /// <summary>
        /// Clamps to the unit interval. NaN passes through, as <c>Math.Clamp</c> returns it
        /// (<c>Math.Clamp</c> itself is not on netstandard2.0).
        /// </summary>
        internal static double Clamp01(double value)
        {
            if (value < 0) return 0;
            return value > 1 ? 1 : value;
        }

        private static bool IsHexDigits(string s)
        {
            if (s.Length == 0) return false;
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            return true;
        }

        private static int HexVal(char c) =>
            c >= '0' && c <= '9' ? c - '0' :
            c >= 'a' && c <= 'f' ? c - 'a' + 10 :
            c >= 'A' && c <= 'F' ? c - 'A' + 10 : 0;

        private static double Hex1(char c) { int v = HexVal(c); return (v * 16 + v) / 255.0; }

        private static double Hex2(string s, int i) => (HexVal(s[i]) * 16 + HexVal(s[i + 1])) / 255.0;

        private static double D(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        private static double Comp(string s)
        {
            s = s.Trim();
            return s.EndsWith("%", StringComparison.Ordinal)
                ? Clamp01(D(s.Substring(0, s.Length - 1)) / 100)
                : Clamp01(D(s) / 255);
        }

        private static double Alpha(string s)
        {
            s = s.Trim();
            return s.EndsWith("%", StringComparison.Ordinal)
                ? Clamp01(D(s.Substring(0, s.Length - 1)) / 100)
                : Clamp01(D(s));
        }

        /// <summary>CSS's <c>hsl()</c>, its hue in degrees, through <see cref="HslColor.ToRgb"/>.</summary>
        private static (double, double, double) HslToRgb(double h, double s, double l)
            => HslColor.ToRgb(((h % 360) + 360) % 360 / 360.0, s, l);
    }
}
