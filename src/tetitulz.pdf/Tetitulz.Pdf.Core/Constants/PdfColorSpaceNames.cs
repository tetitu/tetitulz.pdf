// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// PDF colour-space family names (PDF 32000-1:2008, §8.6) as <c>const string</c>, so they can
    /// be used as <c>switch</c> case labels.
    ///
    /// <para><see cref="ComponentCount"/> is the single authority for how many colour components a
    /// family carries, so a family cannot be understood as 3-component in one code path and 4 in
    /// another.</para>
    /// </summary>
    public static class PdfColorSpaceNames
    {
        // ---- Device spaces ----
        /// <summary><c>/DeviceGray</c> — 1 component.</summary>
        public const string DeviceGray = "DeviceGray";
        /// <summary><c>/DeviceRGB</c> — 3 components.</summary>
        public const string DeviceRGB = "DeviceRGB";
        /// <summary><c>/DeviceCMYK</c> — 4 components.</summary>
        public const string DeviceCMYK = "DeviceCMYK";

        // ---- CIE-based spaces ----
        /// <summary><c>/CalGray</c> — 1 component.</summary>
        public const string CalGray = "CalGray";
        /// <summary><c>/CalRGB</c> — 3 components.</summary>
        public const string CalRGB = "CalRGB";
        /// <summary><c>/Lab</c> — 3 components (L*, a*, b*).</summary>
        public const string Lab = "Lab";
        /// <summary><c>/ICCBased</c> — component count comes from the stream's <c>/N</c> entry.</summary>
        public const string ICCBased = "ICCBased";

        // ---- Special spaces ----
        /// <summary><c>/Indexed</c> — always a single index component.</summary>
        public const string Indexed = "Indexed";
        /// <summary><c>/Separation</c> — always a single tint component.</summary>
        public const string Separation = "Separation";
        /// <summary><c>/DeviceN</c> — component count comes from the name array's length.</summary>
        public const string DeviceN = "DeviceN";
        /// <summary><c>/Pattern</c> — carries no colour components of its own.</summary>
        public const string Pattern = "Pattern";

        // ---- Special colorant names and DeviceN subtypes (§8.6.6.4, Table 71) ----

        /// <summary>
        /// <c>/All</c> — the reserved <see cref="Separation"/> colorant name meaning "every
        /// colorant the device has, at this tint" (registration marks). Not a colorant that can
        /// appear in a <see cref="DeviceN"/> name array.
        /// </summary>
        public const string AllColorant = "All";

        /// <summary>
        /// <c>/None</c> — the reserved colorant name that never marks the page (§8.6.6.4): a
        /// <see cref="Separation"/> named <c>None</c> paints nothing, and a <see cref="DeviceN"/>
        /// component named <c>None</c> is ignored.
        /// </summary>
        public const string NoneColorant = "None";

        /// <summary>
        /// The four process colorant names (§8.6.6.4): a <see cref="Separation"/> or
        /// <see cref="DeviceN"/> naming one of these refers to the corresponding DeviceCMYK
        /// colorant of the output device rather than to a spot ink. Overprint (§11.7.4.5) marks
        /// exactly the colorants a colour space names, so these are what let a colorant set be
        /// composited on a CMYK backdrop at all.
        /// </summary>
        public const string CyanColorant    = "Cyan";
        /// <inheritdoc cref="CyanColorant"/>
        public const string MagentaColorant = "Magenta";
        /// <inheritdoc cref="CyanColorant"/>
        public const string YellowColorant  = "Yellow";
        /// <inheritdoc cref="CyanColorant"/>
        public const string BlackColorant   = "Black";

        /// <summary>
        /// <c>/NChannel</c> — the <see cref="DeviceN"/> attributes <c>/Subtype</c> that adds
        /// per-colorant process/spot definitions and mixing hints on top of the tint transform.
        /// </summary>
        public const string NChannel = "NChannel";

        // ---- Inline-image abbreviations (PDF 32000-1:2008, §8.9.7 "Inline Images") ----
        /// <summary><c>/G</c> — inline-image abbreviation for <see cref="DeviceGray"/>.</summary>
        public const string DeviceGrayAbbrev = "G";
        /// <summary><c>/RGB</c> — inline-image abbreviation for <see cref="DeviceRGB"/>.</summary>
        public const string DeviceRGBAbbrev = "RGB";
        /// <summary><c>/CMYK</c> — inline-image abbreviation for <see cref="DeviceCMYK"/>.</summary>
        public const string DeviceCMYKAbbrev = "CMYK";
        /// <summary><c>/I</c> — inline-image abbreviation for <see cref="Indexed"/>.</summary>
        public const string IndexedAbbrev = "I";

        // These three name the arity, not the space: they are also the right answer for
        // single-component spaces that are not grey (/Indexed, /Separation) and for
        // three-component spaces that are not RGB (/Lab). See ComponentCount below.
        /// <summary>One colour component — the arity of <see cref="DeviceGray"/> and
        /// <see cref="CalGray"/>, and also of <see cref="Indexed"/> and <see cref="Separation"/>.</summary>
        public const int GrayComponents = 1;
        /// <summary>Three colour components — the arity of <see cref="DeviceRGB"/>,
        /// <see cref="CalRGB"/> and <see cref="Lab"/>.</summary>
        public const int RgbComponents = 3;
        /// <summary>
        /// The largest legal <c>/HiVal</c> of an <c>/Indexed</c> colour space (ISO 32000-1
        /// §8.6.6.3): the palette has at most 256 entries, so an index fits one byte.
        /// <para>Clamp to this before sizing anything from a file-supplied <c>/HiVal</c> — it is
        /// attacker-controlled, and the palette builder allocates and loops from it (and, for a
        /// Separation/DeviceN base, evaluates the tint transform once per entry).</para>
        /// </summary>
        public const int MaxIndexedHiVal = 255;

        /// <summary>Four colour components — the arity of <see cref="DeviceCMYK"/>.</summary>
        public const int CmykComponents = 4;

        // ---- ITU-R BT.601 luminance weights ----
        //
        // The weights that collapse RGB to a single grey level. Used for /SMask /Luminosity
        // (§11.6.5.3), for RGB→DeviceGray image conversion, and by the PostScript plugin's
        // `currentgray`. They had five independent copies before being named here — see GAPS.md
        // §2b — in three different numeric representations, which is why all three are provided.
        //
        // THE THREE SETS ARE NOT INTERCHANGEABLE. `(double)0.299f` is 0.29899999499320984, not
        // 0.299, so handing a float constant to a double expression is a real (if tiny) change of
        // value that can move a byte across a quantisation boundary. Use the set matching the
        // arithmetic already at the call site; do not "simplify" to one set without re-baselining.

        /// <summary>ITU-R BT.601 red luminance weight, as <c>double</c>.</summary>
        public const double LumaRed = 0.299;
        /// <summary>ITU-R BT.601 green luminance weight, as <c>double</c>.</summary>
        public const double LumaGreen = 0.587;
        /// <summary>ITU-R BT.601 blue luminance weight, as <c>double</c>.</summary>
        public const double LumaBlue = 0.114;

        /// <summary>ITU-R BT.601 red luminance weight, as <c>float</c> — exactly <c>0.299f</c>.</summary>
        public const float LumaRedF = (float)LumaRed;
        /// <summary>ITU-R BT.601 green luminance weight, as <c>float</c> — exactly <c>0.587f</c>.</summary>
        public const float LumaGreenF = (float)LumaGreen;
        /// <summary>ITU-R BT.601 blue luminance weight, as <c>float</c> — exactly <c>0.114f</c>.</summary>
        public const float LumaBlueF = (float)LumaBlue;

        /// <summary>
        /// ITU-R BT.601 red weight in ×1000 fixed point, for integer luminance
        /// (<c>(r*299 + g*587 + b*114) / LumaScale</c>).
        /// <para>Written out rather than derived from <see cref="LumaRed"/>. The derivation
        /// happens to be exact — <c>0.299 * 1000</c> rounds to exactly 299.0 in IEEE 754 double —
        /// but §12.23 of the C# specification permits constant expressions on <c>double</c> to be
        /// evaluated at higher precision than <c>double</c>, which would make a derived value
        /// implementation-defined. The literal is pinned; a test asserts the two still agree.</para>
        /// </summary>
        public const int LumaRedPermille = 299;
        /// <summary>ITU-R BT.601 green weight in ×1000 fixed point. See <see cref="LumaRedPermille"/>.</summary>
        public const int LumaGreenPermille = 587;
        /// <summary>ITU-R BT.601 blue weight in ×1000 fixed point. See <see cref="LumaRedPermille"/>.</summary>
        public const int LumaBluePermille = 114;
        /// <summary>Divisor pairing with the <c>*Permille</c> weights. Integer division truncates —
        /// that is the existing behaviour of the call site and is deliberately preserved.</summary>
        public const int LumaScale = 1000;

        /// <summary>
        /// Number of colour components for a colour-space family name, or
        /// <paramref name="fallback"/> when the family is unknown or its arity is not determined
        /// by the name alone.
        ///
        /// <para><see cref="ICCBased"/> and <see cref="DeviceN"/> deliberately return
        /// <paramref name="fallback"/>: their arity lives in the stream's <c>/N</c> entry and the
        /// name array respectively, so the caller must resolve it from the object, not the name.
        /// <see cref="Pattern"/> likewise carries no components of its own.</para>
        /// </summary>
        /// <param name="family">The colour-space family name, with no leading solidus.</param>
        /// <param name="fallback">Value returned when the arity cannot be decided from the name.</param>
        public static int ComponentCount(string family, int fallback = RgbComponents)
        {
            switch (family)
            {
                case DeviceGray:
                case CalGray:
                case DeviceGrayAbbrev:
                // Not grey, but still one component each: /Indexed carries a palette index and
                // /Separation a tint. The base space's arity is irrelevant to the sample count.
                case Indexed:
                case IndexedAbbrev:
                case Separation:
                    return GrayComponents;

                case DeviceRGB:
                case CalRGB:
                case DeviceRGBAbbrev:
                // Not RGB, but three components: L*, a*, b*.
                case Lab:
                    return RgbComponents;

                case DeviceCMYK:
                case DeviceCMYKAbbrev:
                    return CmykComponents;

                default:
                    return fallback;
            }
        }

        /// <summary>
        /// Expands an inline-image colour-space abbreviation to its canonical family name.
        /// Names that are already canonical (or unrecognised) are returned unchanged.
        /// </summary>
        public static string Expand(string family)
        {
            switch (family)
            {
                case DeviceGrayAbbrev: return DeviceGray;
                case DeviceRGBAbbrev:  return DeviceRGB;
                case DeviceCMYKAbbrev: return DeviceCMYK;
                case IndexedAbbrev:    return Indexed;
                default:               return family;
            }
        }
    }
}
