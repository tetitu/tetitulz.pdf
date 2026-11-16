// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The pair kerning of the standard-14 fonts drawn through WinAnsiEncoding: the <c>KPX</c> lines of
    /// Adobe's Core14 AFM files, embedded gzipped and otherwise unmodified beside the
    /// <c>MustRead.html</c> their licence requires (<c>Fonts/Afm</c>, <c>tools/standard14-afm/pack.ps1</c>,
    /// which checks two independent copies agree pair for pair).
    /// </summary>
    /// <remarks>
    /// <para><b>Keyed by the bytes a WinAnsi string draws</b>, each pair's glyph names read through
    /// <see cref="SimpleFontEncodingNames.WinAnsi"/>, so a pair is found for exactly the glyphs the
    /// string selects — a character above U+00FF as the <c>?</c> it is written as
    /// (<c>PdfContentStreamBuilder.ShowText</c>).</para>
    /// <para><b>An oblique kerns as its upright</b>: Helvetica-Oblique's and Helvetica-BoldOblique's
    /// <c>KPX</c> lines equal Helvetica's and Helvetica-Bold's, line for line. Courier, Symbol and
    /// ZapfDingbats have none.</para>
    /// <para>A reader positions a standard-14 font's glyphs by their widths alone, so kerning is the
    /// producer's: written as <c>TJ</c> adjustments.</para>
    /// </remarks>
    internal static class Standard14Kerning
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Dictionary<int, short>> Loaded = new(StringComparer.Ordinal);

        /// <summary>The AFM a face's pairs are read from; <c>null</c> for a face with none.</summary>
        private static string AfmOf(string baseFont) => baseFont switch
        {
            StandardFonts.Helvetica or StandardFonts.HelveticaOblique => "Helvetica",
            StandardFonts.HelveticaBold or StandardFonts.HelveticaBoldOblique => "Helvetica-Bold",
            StandardFonts.TimesRoman => "Times-Roman",
            StandardFonts.TimesBold => "Times-Bold",
            StandardFonts.TimesItalic => "Times-Italic",
            StandardFonts.TimesBoldItalic => "Times-BoldItalic",
            _ => null,
        };

        /// <summary>
        /// The adjustment after each glyph of <paramref name="text"/> drawn in
        /// <paramref name="baseFont"/> through WinAnsiEncoding, in thousandths of an em (an AFM's
        /// <c>KPX</c> value: negative brings the next glyph closer); one per gap, so one fewer than the
        /// characters. <c>null</c> when the face has no pairs or none applies.
        /// </summary>
        internal static double[] Adjustments(string baseFont, string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 2) return null;
            Dictionary<int, short> pairs = PairsOf(baseFont);
            if (pairs == null) return null;

            double[] adjustments = null;
            int left = Code(text[0]);
            for (int i = 1; i < text.Length; i++)
            {
                int right = Code(text[i]);
                if (pairs.TryGetValue(left << 8 | right, out short value))
                    (adjustments ??= new double[text.Length - 1])[i - 1] = value;
                left = right;
            }
            return adjustments;
        }

        // The byte a WinAnsi string writes for c (PdfContentStreamBuilder.ShowText).
        private static int Code(char c) => c <= 0xFF ? c : '?';

        private static Dictionary<int, short> PairsOf(string baseFont)
        {
            string afm = AfmOf(baseFont);
            if (afm == null) return null;
            lock (Gate)
            {
                if (!Loaded.TryGetValue(afm, out var pairs)) Loaded[afm] = pairs = Read(afm);
                return pairs;
            }
        }

        /// <summary>The <c>KPX</c> pairs of one embedded AFM between glyphs WinAnsiEncoding reaches, by their codes.</summary>
        private static Dictionary<int, short> Read(string afm)
        {
            var codes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            string[] names = SimpleFontEncodingNames.WinAnsi;
            for (int code = 0; code < names.Length; code++)
            {
                if (names[code] == null) continue;
                if (!codes.TryGetValue(names[code], out var list)) codes[names[code]] = list = new List<int>(1);
                list.Add(code);
            }

            var pairs = new Dictionary<int, short>();
            using (Stream resource = typeof(Standard14Kerning).Assembly.GetManifestResourceStream("Tetitulz.Pdf.Core.Fonts.Afm." + afm + ".afm.gz"))
            {
                if (resource == null) return pairs;
                using var reader = new StreamReader(new GZipStream(resource, CompressionMode.Decompress), Encoding.ASCII);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("KPX ", StringComparison.Ordinal)) continue;
                    string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length != 4 || !short.TryParse(f[3], System.Globalization.NumberStyles.AllowLeadingSign,
                            System.Globalization.CultureInfo.InvariantCulture, out short value)) continue;
                    if (!codes.TryGetValue(f[1], out var lefts) || !codes.TryGetValue(f[2], out var rights)) continue;
                    foreach (int l in lefts)
                        foreach (int r in rights)
                            pairs[l << 8 | r] = value;
                }
            }
            return pairs;
        }
    }
}
