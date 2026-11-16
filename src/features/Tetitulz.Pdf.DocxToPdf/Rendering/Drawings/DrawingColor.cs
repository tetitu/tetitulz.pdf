// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>An RGB colour with an alpha, all components 0..1.</summary>
    internal readonly struct DrawingRgba
    {
        internal DrawingRgba(double r, double g, double b, double a)
        {
            R = r; G = g; B = b; A = a;
        }

        internal double R { get; }
        internal double G { get; }
        internal double B { get; }
        internal double A { get; }

        internal Rgb ToRgb() => new Rgb(R, G, B);
        internal bool IsOpaque => A >= 0.999;
    }

    /// <summary>
    /// Resolves a DrawingML colour — the element under an <c>a:solidFill</c>, <c>a:fillRef</c>,
    /// <c>a:lnRef</c>, <c>a:gs</c> or <c>a:fontRef</c> — to RGBA.
    ///
    /// <para>
    /// <b>This is what makes Word's own shapes appear.</b> A default autoshape carries no
    /// <c>spPr/a:solidFill</c> at all: its colour lives in <c>wps:style/a:fillRef idx="1"</c>
    /// pointing at the theme's format scheme, whose entry is
    /// <c>a:solidFill/a:schemeClr val="phClr"</c> — a placeholder the reference itself fills in.
    /// Without that chain the shape resolves to no colour and is not painted.
    /// </para>
    ///
    /// <para>
    /// Everything is matched by <see cref="OpenXmlElement.LocalName"/>: <c>wps:style</c> and
    /// <c>dsp:style</c> have identical children in different namespaces, so a typed lookup
    /// silently returns null for one of them.
    /// </para>
    /// </summary>
    internal sealed class DrawingColorResolver
    {
        // Elements that are a colour, in the order a container may hold them.
        private static readonly HashSet<string> ColorChoices = new HashSet<string>(StringComparer.Ordinal)
        {
            "srgbClr", "schemeClr", "sysClr", "prstClr", "scrgbClr", "hslClr",
        };

        private static readonly Dictionary<string, string> PresetColors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["black"] = "000000", ["white"] = "FFFFFF", ["red"] = "FF0000",
                ["green"] = "008000", ["lime"] = "00FF00", ["blue"] = "0000FF",
                ["yellow"] = "FFFF00", ["cyan"] = "00FFFF", ["aqua"] = "00FFFF",
                ["magenta"] = "FF00FF", ["fuchsia"] = "FF00FF", ["gray"] = "808080",
                ["grey"] = "808080", ["darkGray"] = "A9A9A9", ["lightGray"] = "D3D3D3",
                ["orange"] = "FFA500", ["purple"] = "800080", ["navy"] = "000080",
            };

        private readonly StyleResolver _styles;
        private bool _reportedThemeFallback;

        internal DrawingColorResolver(StyleResolver styles) => _styles = styles;

        /// <summary>
        /// Resolves the colour held by <paramref name="container"/> (its first colour-choice
        /// child), applying every transform the colour carries.
        /// <paramref name="phClrHex"/> supplies the <c>a:schemeClr val="phClr"</c> placeholder used
        /// inside a style reference's template; pass <c>null</c> outside one.
        /// </summary>
        internal bool TryResolve(OpenXmlElement container, string phClrHex, out DrawingRgba rgba)
        {
            rgba = default;
            var clr = container?.ChildElements.FirstOrDefault(e => ColorChoices.Contains(e.LocalName));
            if (clr == null) return false;
            return TryResolveColorElement(clr, phClrHex, out rgba);
        }

        /// <summary>
        /// The fill a shape inherits from its <c>wps:style</c> / <c>dsp:style</c>
        /// <c>a:fillRef</c>.
        /// </summary>
        internal bool TryResolveFillRef(OpenXmlElement style, out DrawingRgba rgba)
            => TryResolveStyleRef(style, "fillRef", "fillStyleLst", "bgFillStyleLst", out rgba, out _);

        /// <summary>
        /// The outline a shape inherits from its <c>a:lnRef</c> — colour and, when the theme's
        /// line style states one, width in points (0 when it does not).
        /// </summary>
        internal bool TryResolveLineRef(OpenXmlElement style, out DrawingRgba rgba, out double widthPt)
            => TryResolveStyleRef(style, "lnRef", "lnStyleLst", null, out rgba, out widthPt);

        // ------------------------------------------------------------------ style references

        private bool TryResolveStyleRef(OpenXmlElement style, string refName,
            string listName, string bgListName, out DrawingRgba rgba, out double widthPt)
        {
            rgba = default;
            widthPt = 0;

            var reference = style?.ChildElements.FirstOrDefault(e => e.LocalName == refName);
            if (reference == null) return false;

            // The reference's own colour becomes the phClr placeholder inside the template.
            string phClr = null;
            if (TryResolve(reference, null, out var refColor))
                phClr = ToHex(refColor);

            var template = FindFormatSchemeEntry(reference, listName, bgListName);
            if (template == null)
            {
                // No theme (or no fmtScheme): the reference's own colour *is* the answer. That is
                // what fillStyleLst[1] amounts to in every stock theme — a plain solid phClr.
                if (phClr == null) return false;
                rgba = refColor;
                return true;
            }

            // a:lnStyleLst entries are <a:ln w="…">, whose fill is a child; a:fillStyleLst entries
            // are the fill element itself.
            var fillHolder = template.LocalName == "ln" ? template : template;
            if (template.LocalName == "ln")
            {
                string w = Attr(template, "w");
                if (long.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu) && emu > 0)
                    widthPt = emu / PdfUnits.EmuPerPoint;
            }

            var solid = fillHolder.LocalName == "solidFill"
                ? fillHolder
                : fillHolder.ChildElements.FirstOrDefault(e => e.LocalName == "solidFill");

            if (solid != null && TryResolve(solid, phClr, out rgba)) return true;

            // A gradient or pattern entry: not paintable yet, but the reference's own colour is a
            // faithful stand-in for its dominant tone, and better than not painting the shape.
            if (phClr != null && TryParseHex(phClr, out rgba))
            {
                DocxDiagnostics.Report("DrawingColorResolver.StyleRef",
                    "the theme style for this shape is a gradient or pattern fill; its solid "
                        + "reference colour was used instead");
                return true;
            }
            return false;
        }

        // theme → themeElements → fmtScheme → <listName> → the idx-th child (1-based).
        private OpenXmlElement FindFormatSchemeEntry(OpenXmlElement reference,
            string listName, string bgListName)
        {
            string idxAttr = Attr(reference, "idx");
            if (!int.TryParse(idxAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) || idx <= 0)
                return null;

            // idx ≥ 1001 indexes the background fill list at idx − 1000 (ECMA-376 §20.1.4.1.15).
            string list = listName;
            if (idx >= 1001 && bgListName != null) { list = bgListName; idx -= 1000; }

            var fmtScheme = _styles?.Theme?.ChildElements
                .FirstOrDefault(e => e.LocalName == "themeElements")?.ChildElements
                .FirstOrDefault(e => e.LocalName == "fmtScheme");

            var entries = fmtScheme?.ChildElements.FirstOrDefault(e => e.LocalName == list);
            if (entries == null) return null;

            var children = entries.ChildElements.ToList();
            return idx <= children.Count ? children[idx - 1] : null;
        }

        // ------------------------------------------------------------------ colour elements

        private bool TryResolveColorElement(OpenXmlElement clr, string phClrHex, out DrawingRgba rgba)
        {
            rgba = default;

            double r, g, b;
            switch (clr.LocalName)
            {
                case "srgbClr":
                    if (!TryParseHex(Attr(clr, "val"), out rgba)) return false;
                    r = rgba.R; g = rgba.G; b = rgba.B;
                    break;

                case "schemeClr":
                {
                    string token = Attr(clr, "val");
                    string hex;
                    if (string.Equals(token, "phClr", StringComparison.Ordinal))
                    {
                        // Not re-entrant on purpose: a placeholder inside a template resolves to
                        // the caller's colour and never starts another reference lookup.
                        if (phClrHex == null) return false;
                        hex = phClrHex;
                    }
                    else
                    {
                        hex = _styles?.ThemeHexByDrawingMlToken(token);
                        if (hex == null)
                        {
                            hex = OfficeThemeDefaults.Hex(token);
                            if (hex == null) return false;
                            if (!_reportedThemeFallback)
                            {
                                _reportedThemeFallback = true;
                                DocxDiagnostics.Report("DrawingColorResolver.Theme",
                                    "the document has no theme part; Word's built-in Office colour "
                                        + "scheme was used for its scheme colours");
                            }
                        }
                    }
                    if (!TryParseHex(hex, out rgba)) return false;
                    r = rgba.R; g = rgba.G; b = rgba.B;
                    break;
                }

                case "sysClr":
                {
                    string last = Attr(clr, "lastClr");
                    if (!TryParseHex(last, out rgba))
                    {
                        string name = Attr(clr, "val");
                        string hex = string.Equals(name, "window", StringComparison.OrdinalIgnoreCase)
                            ? "FFFFFF" : "000000";
                        if (!TryParseHex(hex, out rgba)) return false;
                    }
                    r = rgba.R; g = rgba.G; b = rgba.B;
                    break;
                }

                case "prstClr":
                {
                    string name = Attr(clr, "val");
                    if (name == null || !PresetColors.TryGetValue(name, out string hex))
                    {
                        DocxDiagnostics.Report("DrawingColorResolver.PresetColor",
                            "preset colour '" + (name ?? "(none)") + "' is not in the supported set; black was used");
                        hex = "000000";
                    }
                    if (!TryParseHex(hex, out rgba)) return false;
                    r = rgba.R; g = rgba.G; b = rgba.B;
                    break;
                }

                case "scrgbClr":
                    r = Percent(Attr(clr, "r"), 0);
                    g = Percent(Attr(clr, "g"), 0);
                    b = Percent(Attr(clr, "b"), 0);
                    break;

                case "hslClr":
                {
                    double hue = ParseNumber(Attr(clr, "hue"), 0) / 60000.0 / 360.0;
                    double sat = Percent(Attr(clr, "sat"), 0);
                    double lum = Percent(Attr(clr, "lum"), 0);
                    HslToRgb(hue, sat, lum, out r, out g, out b);
                    break;
                }

                default:
                    return false;
            }

            double alpha = 1.0;
            ApplyTransforms(clr, ref r, ref g, ref b, ref alpha);
            rgba = new DrawingRgba(Clamp01(r), Clamp01(g), Clamp01(b), Clamp01(alpha));
            return true;
        }

        /// <summary>
        /// Applies the colour's transform children, in document order — which is what the spec
        /// says and what Word does; reordering them changes the result.
        /// </summary>
        private static void ApplyTransforms(OpenXmlElement clr,
            ref double r, ref double g, ref double b, ref double alpha)
        {
            foreach (var t in clr.ChildElements)
            {
                string v = Attr(t, "val");
                switch (t.LocalName)
                {
                    // HSL-space transforms. lumMod 60000 + lumOff 40000 is "Accent1, Lighter 40%",
                    // by a distance the most common colour in a SmartArt diagram, so these are not
                    // optional.
                    case "lumMod": ScaleLum(ref r, ref g, ref b, Percent(v, 1), 0); break;
                    case "lumOff": ScaleLum(ref r, ref g, ref b, 1.0, Percent(v, 0)); break;
                    case "satMod": ScaleSat(ref r, ref g, ref b, Percent(v, 1), 0); break;
                    case "satOff": ScaleSat(ref r, ref g, ref b, 1.0, Percent(v, 0)); break;
                    case "hueMod": ShiftHue(ref r, ref g, ref b, Percent(v, 1), 0); break;
                    case "hueOff": ShiftHue(ref r, ref g, ref b, 1.0, ParseNumber(v, 0) / 60000.0 / 360.0); break;

                    // sRGB-space transforms. ECMA-376 defines these in a linear space; the naive
                    // sRGB form below is what Word actually paints, and matching Word is the point
                    // of a DOCX converter. Deliberate, and stated so it is not "corrected".
                    case "tint":
                    {
                        double t2 = Percent(v, 1);
                        r = r * t2 + (1 - t2); g = g * t2 + (1 - t2); b = b * t2 + (1 - t2);
                        break;
                    }
                    case "shade":
                    {
                        double s = Percent(v, 1);
                        r *= s; g *= s; b *= s;
                        break;
                    }

                    case "alpha": alpha = Percent(v, 1); break;
                    case "alphaMod": alpha *= Percent(v, 1); break;
                    case "alphaOff": alpha += Percent(v, 0); break;
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static void ScaleLum(ref double r, ref double g, ref double b, double mod, double off)
        {
            RgbToHsl(r, g, b, out double h, out double s, out double l);
            l = Clamp01(l * mod + off);
            HslToRgb(h, s, l, out r, out g, out b);
        }

        private static void ScaleSat(ref double r, ref double g, ref double b, double mod, double off)
        {
            RgbToHsl(r, g, b, out double h, out double s, out double l);
            s = Clamp01(s * mod + off);
            HslToRgb(h, s, l, out r, out g, out b);
        }

        private static void ShiftHue(ref double r, ref double g, ref double b, double mod, double off)
        {
            RgbToHsl(r, g, b, out double h, out double s, out double l);
            h = (h * mod + off) % 1.0;
            if (h < 0) h += 1.0;
            HslToRgb(h, s, l, out r, out g, out b);
        }

        // HSL with the hue in turns, CSS Color 4's model (Core.Text.HslColor, shared with CssColor).
        private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
            => (h, s, l) = Tetitulz.Pdf.Core.Text.HslColor.FromRgb(r, g, b);

        private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
            => (r, g, b) = Tetitulz.Pdf.Core.Text.HslColor.ToRgb(h, s, l);

        /// <summary>
        /// ECMA-376 percentages come in two spellings — <c>val="60000"</c> and the strict
        /// <c>val="60%"</c> — and a file may use either.
        /// </summary>
        private static double Percent(string v, double fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            if (v.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double pct))
                return pct / 100.0;
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)
                ? raw / 100000.0
                : fallback;
        }

        private static double ParseNumber(string v, double fallback)
            => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : fallback;

        internal static bool TryParseHex(string hex, out DrawingRgba rgba)
        {
            rgba = default;
            if (!RunRenderer.TryParseHexColor(hex, out int r, out int g, out int b)) return false;
            rgba = new DrawingRgba(r / 255.0, g / 255.0, b / 255.0, 1.0);
            return true;
        }

        private static string ToHex(DrawingRgba c)
            => (SaturatingCast.ToInt(Math.Round(Clamp01(c.R) * 255))).ToString("X2", CultureInfo.InvariantCulture)
             + (SaturatingCast.ToInt(Math.Round(Clamp01(c.G) * 255))).ToString("X2", CultureInfo.InvariantCulture)
             + (SaturatingCast.ToInt(Math.Round(Clamp01(c.B) * 255))).ToString("X2", CultureInfo.InvariantCulture);

        private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
