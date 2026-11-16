// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tetitulz.Pdf.Plugins.Docx
{
    /// <summary>
    /// Reads the loosely-typed <c>Extra</c> bag of
    /// <see cref="Tetitulz.Pdf.Core.Plugins.ImportOptions"/> /
    /// <see cref="Tetitulz.Pdf.Core.Plugins.ExportOptions"/> into this plugin's real options.
    ///
    /// <para><b>Every method refuses rather than defaults:</b> an unrecognised key, a value of the
    /// wrong shape and a value out of range each raise <see cref="ArgumentException"/> naming the
    /// key, so a mistyped key cannot produce the same output as an honoured one.</para>
    ///
    /// <para><b>Plugin-local on purpose.</b> <c>Plugins.Html</c> and <c>Plugins.Svg</c> carry their
    /// own readers: a plugin → plugin reference is banned (CLAUDE.md), and both routes to sharing
    /// cost something structural —</para>
    /// <list type="bullet">
    ///   <item>promoting the reader to <b>public</b> <c>Core.Plugins</c> adds seven permanent members
    ///     to the surface of the assembly every consumer depends on, for a helper no consumer
    ///     calls;</item>
    ///   <item>keeping it <c>internal</c> and adding the plugins to Core's <c>InternalsVisibleTo</c>
    ///     turns CLAUDE.md's rule that Core's list names no plugin into a case-by-case judgement.</item>
    /// </list>
    /// <para>Three internal copies of a stable reader over a spec-frozen options bag are the cheaper
    /// trade. They look keys up the same way, case-insensitively, and a test measures it
    /// (<c>ImportHonoursAPageWidthKeyWhoseCasingDiffers</c>). <b>Re-open the question only if a
    /// fourth plugin needs the reader.</b></para>
    /// </summary>
    internal static class DocxPluginOptionReader
    {
        /// <summary>
        /// Fails when <paramref name="extra"/> holds a key outside <paramref name="recognised"/>.
        /// Case-insensitive, because the bag is hand-populated by callers.
        /// </summary>
        internal static void RejectUnknownKeys(
            string plugin, IReadOnlyDictionary<string, object>? extra, params string[] recognised)
        {
            if (extra == null || extra.Count == 0) return;

            var known = new HashSet<string>(recognised, StringComparer.OrdinalIgnoreCase);
            var unknown = extra.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (unknown.Count == 0) return;

            throw new ArgumentException(
                $"{plugin}: unrecognised option key(s) {string.Join(", ", unknown.Select(k => "'" + k + "'"))}. "
                + (known.Count == 0
                    ? "This adapter has no configurable options; the request would have been silently ignored."
                    : $"Recognised keys: {string.Join(", ", recognised.OrderBy(k => k, StringComparer.Ordinal))}."));
        }

        /// <summary>Reads <paramref name="key"/> as a boolean, or leaves <paramref name="value"/> alone.</summary>
        internal static void ReadBool(
            string plugin, IReadOnlyDictionary<string, object> extra, string key, ref bool value)
        {
            if (!TryGet(extra, key, out object? raw)) return;
            switch (raw)
            {
                case bool b: value = b; return;
                case string s when bool.TryParse(s, out bool p): value = p; return;
                default:
                    throw new ArgumentException(
                        $"{plugin}: option '{key}' must be a boolean, got '{raw ?? "null"}'.");
            }
        }

        /// <summary>
        /// Reads <paramref name="key"/> as a <b>positive</b> length in points. Zero and negative
        /// are refused rather than clamped: a page 0 points wide is not a smaller page, it is a
        /// document with nothing on it, and silently substituting the default would be the very
        /// behaviour this class exists to end.
        /// </summary>
        internal static void ReadPositiveDouble(
            string plugin, IReadOnlyDictionary<string, object> extra, string key, ref double value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            double parsed;
            switch (raw)
            {
                case double d: parsed = d; break;
                case float f: parsed = f; break;
                case int i: parsed = i; break;
                case long l: parsed = l; break;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p):
                    parsed = p; break;
                default:
                    throw new ArgumentException(
                        $"{plugin}: option '{key}' must be a number, got '{raw ?? "null"}'.");
            }

            if (!(parsed > 0) || double.IsInfinity(parsed))
                throw new ArgumentException(
                    $"{plugin}: option '{key}' must be greater than zero, got {parsed.ToString(CultureInfo.InvariantCulture)}.");

            value = parsed;
        }

        /// <summary>
        /// Reads <paramref name="key"/> as a <b>non-negative</b> length in points — margins may
        /// legitimately be zero, page dimensions may not, which is why this is a separate method
        /// rather than a flag on the one above.
        /// </summary>
        internal static void ReadNonNegativeDouble(
            string plugin, IReadOnlyDictionary<string, object> extra, string key, ref double value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            double parsed;
            switch (raw)
            {
                case double d: parsed = d; break;
                case float f: parsed = f; break;
                case int i: parsed = i; break;
                case long l: parsed = l; break;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p):
                    parsed = p; break;
                default:
                    throw new ArgumentException(
                        $"{plugin}: option '{key}' must be a number, got '{raw ?? "null"}'.");
            }

            if (parsed < 0 || double.IsInfinity(parsed) || double.IsNaN(parsed))
                throw new ArgumentException(
                    $"{plugin}: option '{key}' must be zero or greater, got {parsed.ToString(CultureInfo.InvariantCulture)}.");

            value = parsed;
        }

        /// <summary>Reads <paramref name="key"/> as a non-empty string.</summary>
        internal static void ReadString(
            string plugin, IReadOnlyDictionary<string, object> extra, string key, ref string value)
        {
            if (!TryGet(extra, key, out object? raw)) return;
            if (raw is string s && s.Length > 0) { value = s; return; }
            throw new ArgumentException(
                $"{plugin}: option '{key}' must be a non-empty string, got '{raw ?? "null"}'.");
        }

        /// <summary>
        /// Looks <paramref name="key"/> up <b>case-insensitively</b>, because
        /// <see cref="RejectUnknownKeys"/> already accepts it that way.
        /// </summary>
        /// <remarks>
        /// The unknown-key gate matches with <see cref="StringComparer.OrdinalIgnoreCase"/>, so
        /// <c>Extra["pagewidth"]</c> is accepted — and the caller's bag is an ordinary
        /// <c>Dictionary</c>, so an exact-case <c>TryGetValue</c> would then miss it and discard the
        /// option silently. Pinned by <c>ImportHonoursAPageWidthKeyWhoseCasingDiffers</c>, which
        /// measures the emitted page width.
        /// </remarks>
        private static bool TryGet(
            IReadOnlyDictionary<string, object>? extra, string key, out object? value)
        {
            value = null;
            if (extra == null) return false;

            foreach (var kvp in extra)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = kvp.Value;
                    return true;
                }
            }
            return false;
        }
    }
}
