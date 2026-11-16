// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// The glyph outlines of the face a standard font's text is drawn with — what <c>charpath</c>
    /// appends for a font the document did not embed. The face is the host's (Core's
    /// <see cref="SystemFontResolver"/>, which takes the standard fourteen to Arial, Times New Roman
    /// and Courier New or to their metric-compatible Liberation substitutes), or the program
    /// <see cref="PostScriptConversionOptions.OutlineFontProvider"/> supplies; read once per
    /// conversion per face.
    ///
    /// <para><b>Symbol and ZapfDingbats are asked by the character a code's glyph stands for.</b>
    /// Their codes select glyphs through their own encoding (§9.6.6.4), so the face is asked for the
    /// character the glyph's name means — Symbol's through the Adobe Glyph List, ZapfDingbats' through
    /// its own list (<c>a1</c> is U+2701) — and a symbol font's (3,0) <c>cmap</c> for the code in its
    /// private-use area (<c>PsTextOps.TryStandardGlyph</c>).</para>
    /// </summary>
    internal static class PsStandardOutlines
    {
        /// <summary>A face's program and its outlines; both null when there is none to read.</summary>
        internal sealed class Face
        {
            public TrueTypeFontProgram Program;
            public TrueTypeOutlines Outlines;
        }

        [ThreadStatic] private static Dictionary<string, Face> _faces;
        [ThreadStatic] private static Func<string, byte[]> _provider;

        /// <summary>Forgets the faces read so far and takes this conversion's provider.</summary>
        internal static void Reset(Func<string, byte[]> provider)
        {
            _faces = null;
            _provider = provider;
        }

        /// <summary>
        /// The face <paramref name="baseFont"/> — a standard-14 name — is drawn with, or null when no
        /// face with <c>glyf</c> outlines can be had.
        /// </summary>
        public static Face For(string baseFont)
        {
            if (!TryStyle(baseFont, out string family, out bool bold, out bool italic)) return null;

            _faces ??= new Dictionary<string, Face>(StringComparer.Ordinal);
            if (_faces.TryGetValue(baseFont, out Face known)) return known.Outlines != null ? known : null;

            var face = new Face();
            face.Program = _provider != null ? FromProvider(baseFont) : FromHost(family, bold, italic);
            if (face.Program != null) face.Outlines = TrueTypeOutlines.TryRead(face.Program, out _);
            _faces[baseFont] = face;
            return face.Outlines != null ? face : null;
        }

        /// <summary>
        /// The face a composite font named <paramref name="faceName"/> is outlined in, or null when no
        /// face with <c>glyf</c> outlines can be had: the provider's program for the name, else the
        /// host face of that family or, the name being a CJK face the host rarely has, one covering
        /// <paramref name="codePoints"/> — the characters of the first string asked about. Read once
        /// per conversion per name.
        /// </summary>
        public static Face ForComposite(string faceName, IReadOnlyCollection<int> codePoints)
        {
            if (string.IsNullOrEmpty(faceName)) return null;
            string key = "\u0000cid:" + faceName;   // never a standard-14 name
            _faces ??= new Dictionary<string, Face>(StringComparer.Ordinal);
            if (_faces.TryGetValue(key, out Face known)) return known.Outlines != null ? known : null;

            var face = new Face();
            face.Program = _provider != null ? FromProvider(faceName) : FromHost(faceName, codePoints);
            if (face.Program != null) face.Outlines = TrueTypeOutlines.TryRead(face.Program, out _);
            _faces[key] = face;
            return face.Outlines != null ? face : null;
        }

        private static TrueTypeFontProgram FromHost(string family, IReadOnlyCollection<int> codePoints)
        {
            FontResolution resolution = SystemFontResolver.Resolve(new FontResolutionRequest
            {
                FamilyHint = family,
                RequiredCodePoints = codePoints,
                AllowSystemFontSubstitution = true,
                AllowCffOutlines = false,
            }, null);
            return resolution.Success ? resolution.Program : null;
        }

        private static TrueTypeFontProgram FromProvider(string baseFont)
        {
            byte[] bytes;
            try { bytes = _provider(baseFont); }
            catch (Exception ex)
            {
                // The host's callback, not the document: said, and the text keeps its metric box.
                PostScriptDiagnostics.Report("PsStandardOutlines",
                    $"the outline font provider threw {ex.GetType().Name} for '{baseFont}': {ex.Message}");
                return null;
            }
            return bytes == null ? null : TrueTypeFontProgram.Parse(bytes);
        }

        private static TrueTypeFontProgram FromHost(string family, bool bold, bool italic)
        {
            FontResolution resolution = SystemFontResolver.Resolve(new FontResolutionRequest
            {
                FamilyHint = family,
                Bold = bold,
                Italic = italic,
                AllowSystemFontSubstitution = true,
                AllowCffOutlines = false,
            }, null);
            return resolution.Success ? resolution.Program : null;
        }

        /// <summary>A standard-14 name's family and style; false for anything else.</summary>
        private static bool TryStyle(string baseFont, out string family, out bool bold, out bool italic)
        {
            family = Standard14Names.SystemFamily(baseFont);
            bold = baseFont is StandardFonts.HelveticaBold or StandardFonts.HelveticaBoldOblique
                            or StandardFonts.TimesBold or StandardFonts.TimesBoldItalic
                            or StandardFonts.CourierBold or StandardFonts.CourierBoldOblique;
            italic = baseFont is StandardFonts.HelveticaOblique or StandardFonts.HelveticaBoldOblique
                              or StandardFonts.TimesItalic or StandardFonts.TimesBoldItalic
                              or StandardFonts.CourierOblique or StandardFonts.CourierBoldOblique;
            return family != null;
        }
    }
}
