// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The CSS font selectors an SVG <c>&lt;text&gt;</c> element needs: a family stack, a weight and
    /// a slant, derived from a PDF font's <c>/BaseFont</c> and its font descriptor.
    /// </summary>
    /// <remarks>
    /// <para><b>Every <c>&lt;text&gt;</c> names its font.</b> One carrying a matrix, a fill and a
    /// size and no <c>font-family</c> renders in whatever the viewer's default is — nothing for its
    /// "own matching font" to match on — so a Times document comes out in the viewer's sans-serif,
    /// with the wrong shapes and the wrong widths inside each run.</para>
    ///
    /// <para><b>This is genuinely SVG-shaped, so it lives on the SVG side.</b> The raster renderer
    /// answers the same question by resolving an <c>SKTypeface</c> and never needs a name; there is
    /// no shared decision here to extract, only a serialisation of one. It does reuse the two
    /// pieces that <i>are</i> shared — <see cref="Standard14Names.Canonical"/> for the standard-14
    /// spellings and <see cref="SubsetTag"/> for the <c>ABCDEF+</c> prefix — rather than restating
    /// either.</para>
    ///
    /// <para><b>Only the non-embedded path reaches this.</b> An embedded font is drawn as vector
    /// outlines, which are the actual glyph shapes and need no family name; the
    /// <c>&lt;text&gt;</c> element is what a base-14 or system-substituted font falls through to,
    /// and it is exactly the case where the viewer has to be told what to look for.</para>
    ///
    /// <para><b>A generic family always ends the stack.</b> ISO 32000-1 §9.8.2's descriptor flags
    /// say whether the font is fixed-pitch or serif, and CSS's <c>monospace</c>/<c>serif</c>/
    /// <c>sans-serif</c> is the one thing every viewer can honour — so a font nobody has installed
    /// still renders in the right shape class instead of in the default.</para>
    /// </remarks>
    internal readonly struct SvgFontFace
    {
        /// <summary>The CSS <c>font-family</c> list, already quoted and escaped.</summary>
        public readonly string Family;

        /// <summary>The CSS <c>font-weight</c>, 100–900.</summary>
        public readonly int Weight;

        /// <summary>True when the font is italic or oblique.</summary>
        public readonly bool Italic;

        private SvgFontFace(string family, int weight, bool italic)
        {
            Family = family;
            Weight = weight;
            Italic = italic;
        }

        /// <summary>Weight of an unremarkable font, and the value CSS omits.</summary>
        private const int NormalWeight = 400;

        /// <summary>Weight this maps a bold font to when the descriptor gives no number.</summary>
        private const int BoldWeight = 700;

        // ISO 32000-1 §9.8.2, Table 123. The numbers are bit POSITIONS counting from 1, which is
        // how the table states them; the shifts below convert.
        private const int FixedPitchFlag = 1 << 0;
        private const int SerifFlag      = 1 << 1;
        private const int ItalicFlag     = 1 << 6;
        private const int ForceBoldFlag  = 1 << 18;

        /// <summary>
        /// Describes the face for <paramref name="baseFont"/>, refining it with
        /// <paramref name="descriptor"/> when the font has one.
        /// </summary>
        /// <param name="baseFont">The font's <c>/BaseFont</c>, subset tag included.</param>
        /// <param name="descriptor">The resolved <c>/FontDescriptor</c>, or null.</param>
        /// <param name="document">Used to follow indirect descriptor entries; may be null.</param>
        public static SvgFontFace Describe(string baseFont, PdfDictionary descriptor,
            Core.Document.PdfDocument document)
        {
            string name = SubsetTag.Strip(baseFont ?? string.Empty);

            int flags = Integer(descriptor, PdfName.Flags, document) ?? 0;
            bool italic = (flags & ItalicFlag) != 0
                          || (Number(descriptor, PdfName.ItalicAngle, document) ?? 0) != 0
                          || FontNameStyle.IsItalic(name);

            int weight = Integer(descriptor, PdfName.FontWeight, document)
                         ?? ((flags & ForceBoldFlag) != 0 || FontNameStyle.IsBold(name)
                                 ? BoldWeight : NormalWeight);

            // CSS accepts 1–1000, and a producer that writes something else has said nothing
            // usable. Clamping rather than dropping keeps a plausible /FontWeight 950 working.
            if (weight < 100) weight = NormalWeight;
            if (weight > 900) weight = 900;

            string generic = (flags & FixedPitchFlag) != 0 ? "monospace"
                           : (flags & SerifFlag) != 0 ? "serif"
                           : GenericFromName(name);

            return new SvgFontFace(BuildStack(name, generic), weight, italic);
        }

        /// <summary>
        /// The family stack: the standard-14's system family when the name is one, then the name's
        /// own family token, then a generic.
        /// </summary>
        /// <remarks>
        /// <b>The standard-14 family comes first because it is the one a viewer can actually
        /// have.</b> A PDF that says <c>Helvetica</c> means the metrics of Arial on nearly every
        /// desktop, which is the mapping <see cref="Standard14Names.SystemFamily"/> already owns for
        /// the renderer's benefit; asking for <c>Helvetica</c> alone resolves to a substitute of the
        /// browser's choosing, and on Windows that is usually not a metric match.
        /// </remarks>
        private static string BuildStack(string name, string generic)
        {
            var stack = new StringBuilder();

            string canonical = Standard14Names.Canonical(name);
            string system = canonical == null ? null : Standard14Names.SystemFamily(canonical);
            if (system != null) Append(stack, system);

            string family = FamilyToken(name);
            if (family.Length > 0) Append(stack, family);

            Append(stack, generic);
            return stack.ToString();
        }

        /// <summary>
        /// Everything before the first <c>-</c> or <c>,</c>, which is where a PostScript name puts
        /// the style.
        /// </summary>
        /// <remarks>
        /// <c>Times-BoldItalic</c> and <c>Arial,BoldItalic</c> are both real spellings and both mean
        /// the family is the part in front. The style half is not thrown away — it has already been
        /// read into <see cref="Weight"/> and <see cref="Italic"/>, which is where CSS wants it.
        /// </remarks>
        private static string FamilyToken(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int cut = name.IndexOfAny(new[] { '-', ',' });
            return (cut > 0 ? name.Substring(0, cut) : name).Trim();
        }

        /// <summary>
        /// A generic family for a font with no usable descriptor flags, read out of its name.
        /// </summary>
        /// <remarks>
        /// A font with no <c>/FontDescriptor</c> at all is the standard-14 case, where the flags a
        /// descriptor would carry are implied by the name and nothing else is available.
        /// </remarks>
        private static string GenericFromName(string name)
        {
            if (NameSays(name, "Courier") || NameSays(name, "Mono")) return "monospace";
            if (NameSays(name, "Times") || NameSays(name, "Serif") || NameSays(name, "Georgia")
                || NameSays(name, "Garamond") || NameSays(name, "Roman"))
                return "serif";
            return "sans-serif";
        }

        private static void Append(StringBuilder stack, string family)
        {
            if (stack.Length > 0) stack.Append(", ");

            // A generic keyword must NOT be quoted — quoting it makes CSS look for a family with
            // that literal name, which nobody has — and a family containing a space or a digit must
            // be, so the two cases are separated rather than one rule applied to both.
            if (IsGeneric(family)) { stack.Append(family); return; }

            stack.Append('\'');
            // Characters that would end or break the quoted CSS string are dropped, not escaped: a
            // quote ends it, a backslash escapes the closing quote, and a control character (a
            // newline among them) is a parse error inside a string — each takes the WHOLE
            // declaration with it, generic fallback included. A PDF name can carry any of them (#5C).
            foreach (char ch in family)
                if (ch != '\'' && ch != '\\' && ch >= ' ') stack.Append(ch);
            stack.Append('\'');
        }

        private static bool IsGeneric(string family)
            => family == "serif" || family == "sans-serif" || family == "monospace";

        private static bool NameSays(string name, string token)
            => name != null && name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static int? Integer(PdfDictionary dict, PdfName key, Core.Document.PdfDocument doc)
        {
            double? value = Number(dict, key, doc);
            return value.HasValue ? SaturatingCast.ToInt(value.Value) : (int?)null;
        }

        private static double? Number(PdfDictionary dict, PdfName key, Core.Document.PdfDocument doc)
        {
            if (dict == null) return null;

            PdfObject value = dict[key];
            if (value is PdfReference && doc != null)
            {
                try { value = doc.ResolveObject(value); }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        "a font descriptor entry could not be resolved for the SVG font stack", ex);
                    return null;
                }
            }

            return value is PdfNumber number ? number.DoubleValue : (double?)null;
        }

        /// <summary>
        /// The same face with <paramref name="cssFamily"/> — an <c>@font-face</c> carrying the
        /// document's own embedded program — at the head of the stack. Returns this unchanged when
        /// there was nothing to embed.
        /// </summary>
        /// <remarks>
        /// <b>The system names stay behind it, and that is the fallback.</b> A <c>@font-face</c>
        /// whose <c>src</c> a viewer cannot decode is simply skipped by CSS, which then walks on to
        /// the next family — so a program this library re-emitted and the browser rejected renders
        /// exactly as it did before embedding existed, rather than in the viewer's default.
        /// </remarks>
        public SvgFontFace WithEmbeddedFamily(string cssFamily)
        {
            if (string.IsNullOrEmpty(cssFamily)) return this;

            var stack = new StringBuilder();
            Append(stack, cssFamily);
            if (!string.IsNullOrEmpty(Family)) stack.Append(", ").Append(Family);

            return new SvgFontFace(stack.ToString(), Weight, Italic);
        }

        /// <summary>The attributes to write on a <c>&lt;text&gt;</c> element, or the empty string.</summary>
        /// <remarks>
        /// <c>font-weight</c> and <c>font-style</c> are omitted at their defaults, so an ordinary
        /// run adds one attribute rather than three — the output is one element per text-showing
        /// operator, and three attributes on every one of them is a measurable share of the file.
        /// </remarks>
        public string ToAttributes()
        {
            var sb = new StringBuilder();
            sb.Append(" font-family=\"").Append(SvgSyntax.XmlEsc(Family)).Append('"');

            if (Weight != NormalWeight)
                sb.Append(" font-weight=\"")
                  .Append(Weight.ToString(CultureInfo.InvariantCulture)).Append('"');

            if (Italic) sb.Append(" font-style=\"italic\"");
            return sb.ToString();
        }
    }
}
