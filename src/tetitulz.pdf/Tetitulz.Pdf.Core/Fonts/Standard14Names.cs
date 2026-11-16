// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Maps an arbitrary producer-written font name onto one of the fourteen standard base fonts
    /// (§9.6.2.2), or reports that it is none of them.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this belongs in Core, beside the metrics it selects.</b> The answer chooses an
    /// AFM width table through <see cref="Standard14Metrics"/>, so a wrong answer lays text out at
    /// the wrong advances — and <c>Standard14Metrics.IsStandard14</c>, which lives here already, is
    /// the degenerate case of this same question.</para>
    ///
    /// <para><b>It had two independent implementations that disagreed on real input.</b> The
    /// Accessibility font remediator and the PostScript plugin's <c>PsFontMapper</c> each did their
    /// own split-family-from-style, strip-suffix, match-family walk. On
    /// <c>TimesNewRomanPSMT</c> — an entirely ordinary name — one stripped the <c>psmt</c> suffix
    /// and answered Times-Roman, while the other missed its family table, fell into a style-word
    /// stripper that cut at "roman" leaving <c>timesnew</c>, missed again, and answered
    /// <b>Helvetica</b>. Times text was then drawn in Helvetica at Helvetica advances, and the
    /// reported reason said "no metric-compatible substitute is known" while the other body knew
    /// one.</para>
    ///
    /// <para><b>Public because a plugin needs it.</b> Core's <c>InternalsVisibleTo</c> names no
    /// plugin, and <c>Plugins.PostScript</c> already calls <see cref="Standard14Metrics"/> from the
    /// very method that needs this — so the edge exists and points downward. No static fields, so
    /// no type initializer.</para>
    /// </remarks>
    public static class Standard14Names
    {
        /// <summary>
        /// The canonical Standard-14 name for <paramref name="baseFont"/>, or <c>null</c> when it
        /// names none of the fourteen.
        /// </summary>
        /// <remarks>
        /// Handles the aliases real producers write — <c>ArialMT</c>, <c>Arial,Bold</c>,
        /// <c>TimesNewRomanPSMT</c>, <c>CourierNewPS-BoldMT</c> — and the AcroForm short names
        /// <c>Helv</c>, <c>TiRo</c>, <c>Cour</c>, <c>ZaDb</c>. Returning <c>null</c> rather than a
        /// default is deliberate: "this is not a standard-14 font" and "this is Helvetica" are
        /// different answers, and a caller that wants the second can spell it.
        /// </remarks>
        public static string Canonical(string baseFont)
        {
            if (string.IsNullOrEmpty(baseFont)) return null;

            string name = SubsetTag.Strip(baseFont);

            bool bold   = FontNameStyle.IsBold(name);
            bool italic = FontNameStyle.IsItalic(name);

            // Everything up to the first '-' or ',' is the family; the rest is style, already read.
            int cut = name.IndexOfAny(new[] { '-', ',' });
            string family = Squash(cut > 0 ? name.Substring(0, cut) : name);

            // Strip the PostScript suffixes producers append to the family token.
            foreach (string suffix in new[] { "psmt", "psstd", "ps", "mt", "std" })
                if (family.Length > suffix.Length && family.EndsWith(suffix, StringComparison.Ordinal))
                {
                    family = family.Substring(0, family.Length - suffix.Length);
                    break;
                }

            switch (family)
            {
                case "helvetica":
                case "helv":
                case "arial":
                    return bold && italic ? StandardFonts.HelveticaBoldOblique
                         : bold           ? StandardFonts.HelveticaBold
                         : italic         ? StandardFonts.HelveticaOblique
                         :                  StandardFonts.Helvetica;

                case "times":
                case "timesroman":
                case "timesnewroman":
                case "tiro":
                    return bold && italic ? StandardFonts.TimesBoldItalic
                         : bold           ? StandardFonts.TimesBold
                         : italic         ? StandardFonts.TimesItalic
                         :                  StandardFonts.TimesRoman;

                case "courier":
                case "couriernew":
                case "cour":
                    return bold && italic ? StandardFonts.CourierBoldOblique
                         : bold           ? StandardFonts.CourierBold
                         : italic         ? StandardFonts.CourierOblique
                         :                  StandardFonts.Courier;

                case "symbol":
                    return StandardFonts.Symbol;

                case "zapfdingbats":
                case "zadb":
                case "dingbats":
                    return StandardFonts.ZapfDingbats;

                default:
                    return null;
            }
        }

        /// <summary>
        /// The metric-compatible system family a Standard-14 font substitutes to — <c>Arial</c>,
        /// <c>Times New Roman</c>, <c>Courier New</c>, <c>Symbol</c> or <c>ZapfDingbats</c> — or
        /// <c>null</c> when <paramref name="standard14Name"/> is not one of the fourteen.
        /// </summary>
        /// <remarks>
        /// <para><b>It does not canonicalise.</b> <c>FontCache.ResolveByName</c> uses the answer as a
        /// <i>gate</i>: a name this resolves takes the Standard-14 substitute branch, and everything
        /// else falls through to the CJK fast path and the name-derivation heuristics. Feeding it
        /// <see cref="Canonical"/> first would send <c>ArialMT</c> — and every other producer alias —
        /// down the substitute branch instead of the derivation one. Callers that want the alias
        /// handled spell it: <c>SystemFamily(Canonical(x))</c>.</para>
        ///
        /// <para><b>That routing is currently pinned only by this type's own tests, which is worth
        /// knowing before changing it.</b> Making this method canonicalise was tried as a mutation:
        /// six <c>Standard14SystemFamilyTests</c> cases redden and <b>the whole 48-case
        /// <c>PdfRendererTests</c> suite stays green</b>, host fonts installed. The reason is that
        /// the four alias cases it does cover — <c>ArialMT</c>, <c>ABCDEF+ArialMT</c>,
        /// <c>TimesNewRomanPSMT</c>, <c>CourierNewPSMT</c> — resolve to the same family down either
        /// route, so they assert an outcome both paths produce rather than which path ran. So the
        /// justification for the current shape is that it preserves <c>FontCache</c>'s behaviour
        /// exactly, <i>not</i> that the renderer suite would catch the change: it would not, in
        /// either direction.</para>
        ///
        /// <para><b>Why it is here.</b> The map existed twice — <c>Base.Rendering.FontCache</c> and
        /// the Accessibility font remediator — and the second one's comment said the pairings "are
        /// deliberately the same ones <c>FontCache</c> already resolves the Standard 14 through, so
        /// an embedded substitute matches what this library renders". That is a correctness
        /// requirement stated in prose and enforced by nobody, which is this repository's signature
        /// defect shape. Now the requirement is that they call the same method.</para>
        ///
        /// <para><b>The symbolic pair is returned, not filtered.</b> Whether Symbol and ZapfDingbats
        /// may be substituted is a <i>policy</i> — the remediator refuses because swapping a symbolic
        /// font changes which glyphs the same codes draw, while the renderer wants the closest face
        /// it can get — so each caller applies its own rule to a complete answer rather than sharing
        /// a pre-filtered one that reads as the whole truth.</para>
        ///
        /// <para>A <c>switch</c> rather than a dictionary so this type keeps having no static fields
        /// and therefore no type initializer, which the class remarks above rely on.</para>
        /// </remarks>
        public static string SystemFamily(string standard14Name) => standard14Name switch
        {
            StandardFonts.Helvetica            or
            StandardFonts.HelveticaBold        or
            StandardFonts.HelveticaOblique     or
            StandardFonts.HelveticaBoldOblique => "Arial",

            StandardFonts.TimesRoman           or
            StandardFonts.TimesBold            or
            StandardFonts.TimesItalic          or
            StandardFonts.TimesBoldItalic      => "Times New Roman",

            StandardFonts.Courier              or
            StandardFonts.CourierBold          or
            StandardFonts.CourierOblique       or
            StandardFonts.CourierBoldOblique   => "Courier New",

            StandardFonts.Symbol               => "Symbol",

            StandardFonts.ZapfDingbats         or
            StandardFonts.ZapfDingbatsAbbrev   => "ZapfDingbats",

            _ => null,
        };

        /// <summary>Lower-cases and removes white space, so "Times New Roman" matches "timesnewroman".</summary>
        private static string Squash(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }
    }
}
