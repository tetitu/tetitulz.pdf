// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Tetitulz.Pdf.Builder.Import
{
    /// <summary>
    /// XFA's measurement and colour syntax, in points and hex — the two value forms every template
    /// attribute that places or paints something uses.
    /// </summary>
    /// <remarks>
    /// <para><b>The grammar.</b> A measurement is a number and a unit: <c>in</c>, <c>pt</c>, <c>mm</c>,
    /// <c>cm</c>, <c>pc</c> (pica, 12 pt), <c>mp</c> (millipoint) or <c>px</c> (1/96 in); a bare
    /// number is points. That is the form the XFA form designer writes (<c>w="8in"</c>,
    /// <c>y="22.225mm"</c>, <c>size="16pt"</c>, <c>thickness="0.175mm"</c> in the public fixtures)
    /// and the form other readers of the same specification
    /// accepts. A colour is <c>r,g,b</c> decimal components (<c>value="255,0,0"</c>).</para>
    /// <para><b>Hostile input.</b> A template comes from the file. A measurement that is not a
    /// number, or not finite, is refused (the caller's default applies); a component outside 0–255
    /// is clamped. Nothing here throws.</para>
    /// </remarks>
    internal static class XfaMeasure
    {
        /// <summary>
        /// The number's digits and dot are matched one way only (<c>\d+(?:\.\d*)?</c>, never the
        /// ambiguous <c>\d+\.?\d*</c>, whose two digit runs backtrack against each other on a long
        /// digit string that then fails), the text is trimmed before the match so no two
        /// whitespace runs are adjacent, and the engine has a timeout: a measurement attribute is
        /// hostile input and a regular expression that can be made quadratic is a denial of service.
        /// </summary>
        private static readonly Regex Pattern = new Regex(
            @"^([-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?)\s*(in|pt|mm|cm|pc|mp|px)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

        /// <summary>Longer than any measurement: a number, an exponent, a unit and the spaces between.</summary>
        internal const int MaxTextLength = 64;

        /// <summary>Parses <paramref name="text"/> as a measurement in points.</summary>
        public static bool TryParse(string? text, out double points)
        {
            points = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string trimmed = text!.Trim();
            if (trimmed.Length > MaxTextLength) return false;
            Match m;
            try { m = Pattern.Match(trimmed); }
            catch (RegexMatchTimeoutException) { return false; }
            if (!m.Success) return false;
            if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n)) return false;
            // The pattern admits no NaN, and an overflowed number ("1e400") is caught once, below,
            // where the unit's factor can overflow too.

            double factor = m.Groups[2].Value.ToLowerInvariant() switch
            {
                "in" => 72.0,
                "mm" => 72.0 / 25.4,
                "cm" => 72.0 / 2.54,
                "pc" => 12.0,
                "mp" => 0.001,
                "px" => 0.75,
                _    => 1.0,     // pt, or none
            };
            points = n * factor;
            return !double.IsInfinity(points);
        }

        /// <summary>The measurement, or <paramref name="fallback"/> when the text is not one.</summary>
        public static double Parse(string? text, double fallback) => TryParse(text, out double p) ? p : fallback;

        /// <summary>The measurement, or <c>null</c> when the attribute is absent or not one.</summary>
        public static double? ParseOrNull(string? text) => TryParse(text, out double p) ? p : (double?)null;

        /// <summary>An XFA <c>r,g,b</c> colour as <c>#RRGGBB</c>, or <c>null</c> when it is not one.</summary>
        public static string? Color(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string[] parts = text!.Split(',');
            if (parts.Length != 3) return null;
            var bytes = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return null;
                bytes[i] = Math.Max(0, Math.Min(255, v));
            }
            return "#" + bytes[0].ToString("X2", CultureInfo.InvariantCulture)
                       + bytes[1].ToString("X2", CultureInfo.InvariantCulture)
                       + bytes[2].ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
