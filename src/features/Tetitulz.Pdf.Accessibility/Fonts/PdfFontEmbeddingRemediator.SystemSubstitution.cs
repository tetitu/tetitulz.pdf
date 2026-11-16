// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  System-font substitution
        // ================================================================

        /// <summary>
        /// The system family this feature is willing to substitute for a Standard-14 font, or
        /// <c>null</c> — for a name that is not one of the fourteen, and for the two symbolic ones,
        /// which it refuses. See <see cref="FontEmbeddingOptions.AllowSystemFontSubstitution"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>The pairings live in <see cref="Standard14Names.SystemFamily"/>, which
        /// <c>Base.Rendering.FontCache</c> also calls.</b> They must agree — an embedded substitute
        /// has to be the font this library would have drawn with, or the glyphs in the file and the
        /// glyphs on the page diverge. A sentence saying two maps are "deliberately the same" is a
        /// correctness constraint enforced by nobody; there is one map.</para>
        ///
        /// <para><b>What stays here is the policy</b>, because it belongs to this feature rather than
        /// to the Standard 14: the shared helper answers for Symbol and ZapfDingbats too, and
        /// substitution is refused for them. Swapping a symbolic font changes which glyphs the same
        /// character codes draw, so an "equivalent" is not equivalent — whereas for the Latin twelve
        /// the pairing is metric-compatible and the document's <c>/Widths</c> stay meaningful.</para>
        /// </remarks>
        private static string SystemFamilyFor(string standard14Name)
        {
            string family = Standard14Names.SystemFamily(standard14Name);

            return family == null
                || standard14Name == StandardFonts.Symbol
                || standard14Name == StandardFonts.ZapfDingbats
                || standard14Name == StandardFonts.ZapfDingbatsAbbrev
                ? null
                : family;
        }

        /// <summary>
        /// Resolves the font program of an installed system font, or null with the reason in
        /// <paramref name="detail"/>.
        ///
        /// <para>
        /// Resolution goes through the same mechanism the renderer uses
        /// (<c>SKTypeface.FromFamilyName</c> plus the shared Standard-14 family map), so an embedded
        /// substitute is the font this library would have drawn with. A second, divergent font
        /// resolver would guarantee that the embedded glyphs and the rendered glyphs eventually
        /// disagree.
        /// </para>
        /// </summary>
        private static byte[] ResolveSystemFont(FontEmbeddingRequest request, out string detail)
        {
            detail = null;

            string family = SystemFamilyFor(request.Standard14Name)
                         ?? SubsetTag.Strip(request.BaseFont);

            if (string.IsNullOrEmpty(family))
            {
                detail = "the font dictionary carries no /BaseFont, so no system font can be matched to it";
                return null;
            }

            // The one reading of a name (Core.Fonts.FontNameStyle) — the renderer's, so the face
            // embedded is the face drawn.
            bool bold   = FontNameStyle.IsBold(request.BaseFont)   || FontNameStyle.IsBold(request.Standard14Name);
            bool italic = FontNameStyle.IsItalic(request.BaseFont) || FontNameStyle.IsItalic(request.Standard14Name);

            SKFontStyle style = bold && italic ? SKFontStyle.BoldItalic
                              : bold           ? SKFontStyle.Bold
                              : italic         ? SKFontStyle.Italic
                              :                  SKFontStyle.Normal;

            SKTypeface typeface = null;
            try
            {
                typeface = SKTypeface.FromFamilyName(family, style);
                if (typeface == null)
                {
                    detail = "no font matching \"" + family + "\" is installed on this machine";
                    return null;
                }

                // FromFamilyName never fails loudly: an unknown family silently yields the platform
                // default. Embedding that would put an arbitrary font into the document under the
                // original name, so an unrequested family is treated as "not found".
                //
                // THE STANDARD-14 MAP IS NOT EXEMPT FROM THIS CHECK; IT IS THE ONE CASE THAT MOST NEEDS
                // IT. The substitution is "a deliberate, metrically safe choice" — true of Arial, and true
                // only if "Arial" resolves to Arial. On a stock Linux container, the platform CI
                // verifies, Arial is absent and fontconfig answers DejaVu Sans; unchecked, DejaVu Sans
                // would be embedded as Helvetica, and since AllowSystemFontSubstitution preserves the
                // document's own advance widths, every glyph would draw at an advance belonging to a
                // different typeface.
                //
                // The metric-compatible SET is the right test, not the single mapped name: Liberation
                // Sans is as safe a Helvetica substitute as Arial, which is exactly why
                // FontFileLocator ranks it first. What is NOT safe is the rest of that locator's
                // list — DejaVu, Noto, Free — which it carries as broad-coverage fallbacks for
                // RENDERING, where some glyph beats none. Under preserved /Widths they are wrong
                // answers, so this set is the metric-compatible prefix and stops there.
                if (!IsAcceptableSubstitute(request.Standard14Name, family, typeface.FamilyName))
                {
                    detail = "no font matching \"" + family + "\" is installed on this machine (the " +
                             "system offered \"" + (typeface.FamilyName ?? "?") + "\" instead, which " +
                             "is a different typeface and was not embedded)";
                    return null;
                }

                byte[] program = ReadTypefaceProgram(typeface, out string readProblem);
                if (program == null)
                {
                    detail = "the installed font \"" + (typeface.FamilyName ?? family) +
                             "\" could not be read as an embeddable program: " + readProblem;
                    return null;
                }

                return program;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "system font resolution for \"" + family + "\" failed; the font was left non-embedded", ex);
                detail = "system font resolution failed (" + ex.Message + ")";
                return null;
            }
            finally
            {
                typeface?.Dispose();
            }
        }

        private static byte[] ReadTypefaceProgram(SKTypeface typeface, out string problem)
        {
            problem = null;
            SKStreamAsset asset = null;
            try
            {
                asset = typeface.OpenStream(out int ttcIndex);
                if (asset == null)
                {
                    problem = "the system exposes no font file for it";
                    return null;
                }

                if (ttcIndex != 0)
                {
                    problem = "it is face " + ttcIndex.ToString(CultureInfo.InvariantCulture) +
                              " of a TrueType Collection, and a collection cannot be embedded as one font";
                    return null;
                }

                if (!asset.HasLength || asset.Length <= 0)
                {
                    problem = "its font file reports no length";
                    return null;
                }

                int length = asset.Length;
                byte[] buffer = new byte[length];
                int read = asset.Read(buffer, length);
                if (read != length)
                {
                    problem = "only " + read.ToString(CultureInfo.InvariantCulture) + " of " +
                              length.ToString(CultureInfo.InvariantCulture) + " bytes could be read";
                    return null;
                }

                return buffer;
            }
            finally
            {
                asset?.Dispose();
            }
        }

        /// <summary>
        /// The families that may stand in for a Standard-14 face <b>without moving a glyph</b> —
        /// metric-compatible with it, so the document's own <c>/Widths</c> stay true.
        /// </summary>
        /// <remarks>
        /// <para><b>Deliberately shorter than the renderer's candidate list.</b>
        /// <c>Core.Fonts.FontFileLocator</c> ranks DejaVu, Noto and Free after these, and is right
        /// to: for RENDERING a broad-coverage face beats a missing glyph. For EMBEDDING under
        /// preserved widths they are wrong answers, because the advance the file declares belongs
        /// to a different typeface. This is the metric-compatible prefix of that list and nothing
        /// after it.</para>
        ///
        /// <para>The URW names (Nimbus) are here because common print-toolchain installs provide them
        /// and they are the original metric clones of the Adobe faces.</para>
        /// </remarks>
        private static readonly Dictionary<string, string[]> MetricCompatibleFamilies =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "Arial",           new[] { "Arial", "Liberation Sans",  "Helvetica",       "Nimbus Sans"     } },
                { "Times New Roman", new[] { "Times New Roman", "Liberation Serif", "Times", "Nimbus Roman"    } },
                { "Courier New",     new[] { "Courier New", "Liberation Mono", "Courier",    "Nimbus Mono PS"  } },
            };

        /// <summary>
        /// <b>The whole substitution decision</b>: may a font the system resolved to
        /// <paramref name="resolvedFamily"/> be embedded for a request that asked for
        /// <paramref name="requestedFamily"/>?
        /// </summary>
        /// <remarks>
        /// <para><b>One function, and that is deliberate.</b> This was two conditions spelled inline
        /// at the call site, and the consequence was that a test could pin them and still not pin
        /// the DECISION. Mutating the call site back to its pre-fix form — exempting the
        /// Standard-14 map from the check — left every test green, because the tests exercised the
        /// predicates rather than the branch. With the branch here, that mutation has somewhere to
        /// land.</para>
        ///
        /// <para><b><c>internal</c> so it can be tested at all.</b> The defect only reproduces where
        /// Arial, Times New Roman and Courier New are ABSENT — a stock Linux container. Any test
        /// driving the public remediation path passes on Windows whatever this returns, which is
        /// how the exemption survived in the first place. The rule is platform-independent even
        /// though its consequence is not, so the rule is what gets pinned.</para>
        /// </remarks>
        internal static bool IsAcceptableSubstitute(
            string standard14Name, string requestedFamily, string resolvedFamily)
            => FontFileLocator.IsFamilyMatch(resolvedFamily, requestedFamily)
               || IsMetricCompatible(standard14Name, resolvedFamily);

        /// <summary>
        /// Whether <paramref name="resolved"/> is a metric-safe stand-in for the Standard-14 face
        /// <paramref name="standard14Name"/> maps onto. False for a non-Standard-14 request, which
        /// has no substitution licence at all.
        /// </summary>
        internal static bool IsMetricCompatible(string standard14Name, string resolved)
        {
            if (standard14Name == null || string.IsNullOrEmpty(resolved)) return false;
            string mapped = SystemFamilyFor(standard14Name);
            if (mapped == null) return false;
            if (!MetricCompatibleFamilies.TryGetValue(mapped, out string[] safe)) return false;

            foreach (string candidate in safe)
                if (FontFileLocator.IsFamilyMatch(resolved, candidate)) return true;

            return false;
        }
    }
}
