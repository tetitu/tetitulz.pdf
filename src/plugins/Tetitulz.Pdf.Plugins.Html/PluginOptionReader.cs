// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tetitulz.Pdf.Plugins.Html
{
    /// <summary>
    /// Reads the loosely-typed <c>Extra</c> bag of <see cref="Tetitulz.Pdf.Core.Plugins.ExportOptions"/>
    /// / <see cref="Tetitulz.Pdf.Core.Plugins.ImportOptions"/> into a plugin's real options object.
    ///
    /// <para><b>The point is the reporting, not the parsing.</b> Every method here refuses rather
    /// than defaults: an unrecognised key, a value of the wrong shape, and a value out of range each
    /// raise <see cref="ArgumentException"/> naming the key — a dropped setting would otherwise
    /// produce output identical to an honoured one.</para>
    ///
    /// <para><b>Why it is not shared with the other adapter plugins.</b> Two plugins are peers and
    /// a plugin → plugin reference is banned outright (CLAUDE.md), and the alternative — promoting
    /// this into <c>Core</c> — would put a helper for four adapters into the public surface of the
    /// assembly every consumer depends on. <c>Plugins.Svg</c> keeps its own two-method copy for the
    /// same reason. If a fifth adapter needs it, that is the point to reconsider a Core home, not
    /// before.</para>
    /// </summary>
    internal static class PluginOptionReader
    {
        /// <summary>
        /// Fails when <paramref name="extra"/> holds a key outside <paramref name="recognised"/>.
        /// Case-insensitive, because the bag is hand-populated by callers.
        /// </summary>
        internal static void RejectUnknownKeys(string plugin, IReadOnlyDictionary<string, object> extra,
                                               params string[] recognised)
        {
            if (extra == null || extra.Count == 0) return;

            var known = new HashSet<string>(recognised, StringComparer.OrdinalIgnoreCase);
            var unknown = extra.Keys.Where(k => !known.Contains(k))
                                    .OrderBy(k => k, StringComparer.Ordinal)
                                    .ToList();
            if (unknown.Count == 0) return;

            throw new ArgumentException(
                $"{plugin}: unrecognised option key(s) {string.Join(", ", unknown.Select(k => "'" + k + "'"))}. " +
                $"Recognised keys are {string.Join(", ", recognised.OrderBy(k => k, StringComparer.Ordinal).Select(k => "'" + k + "'"))}. " +
                "A dropped setting is indistinguishable from an honoured one, so the request is refused rather than ignored.",
                nameof(extra));
        }

        /// <summary>Reads <paramref name="key"/> as a boolean, or leaves <paramref name="value"/> alone.</summary>
        internal static void ReadBool(string plugin, IReadOnlyDictionary<string, object> extra,
                                      string key, ref bool value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            switch (raw)
            {
                case bool b: value = b; return;
                case string s when bool.TryParse(s, out bool parsed): value = parsed; return;
                default: throw Unusable(plugin, key, raw, "a boolean");
            }
        }

        /// <summary>Reads <paramref name="key"/> as a positive <see cref="int"/>.</summary>
        internal static void ReadPositiveInt(string plugin, IReadOnlyDictionary<string, object> extra,
                                             string key, ref int value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            int parsed;
            switch (raw)
            {
                case int i: parsed = i; break;
                case long l when l >= int.MinValue && l <= int.MaxValue: parsed = (int)l; break;
                // Invariant parsing: a bag populated from a config file on a comma-decimal host
                // must not read "300" differently from an en-US one.
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p):
                    parsed = p; break;
                default: throw Unusable(plugin, key, raw, "an integer");
            }

            if (parsed <= 0) throw Unusable(plugin, key, raw, "a positive integer");
            value = parsed;
        }

        /// <summary>Reads <paramref name="key"/> as a positive <see cref="long"/>.</summary>
        internal static void ReadPositiveLong(string plugin, IReadOnlyDictionary<string, object> extra,
                                              string key, ref long value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            long parsed;
            switch (raw)
            {
                case long l: parsed = l; break;
                case int i: parsed = i; break;
                case string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long p):
                    parsed = p; break;
                default: throw Unusable(plugin, key, raw, "an integer");
            }

            if (parsed <= 0) throw Unusable(plugin, key, raw, "a positive integer");
            value = parsed;
        }

        /// <summary>Reads <paramref name="key"/> as a finite, positive <see cref="double"/>.</summary>
        internal static void ReadPositiveDouble(string plugin, IReadOnlyDictionary<string, object> extra,
                                                string key, ref double value)
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
                default: throw Unusable(plugin, key, raw, "a number");
            }

            if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0d)
                throw Unusable(plugin, key, raw, "a finite positive number");
            value = parsed;
        }

        /// <summary>Reads <paramref name="key"/> as a non-empty string.</summary>
        internal static void ReadString(string plugin, IReadOnlyDictionary<string, object>? extra,
                                        string key, ref string? value)
        {
            if (!TryGet(extra, key, out object? raw)) return;

            if (raw is string s && s.Length > 0) { value = s; return; }
            throw Unusable(plugin, key, raw, "a non-empty string");
        }

        /// <summary>Reads <paramref name="key"/> as a named member of <typeparamref name="TEnum"/>.</summary>
        internal static void ReadEnum<TEnum>(string plugin, IReadOnlyDictionary<string, object> extra,
                                             string key, ref TEnum value) where TEnum : struct, Enum
        {
            if (!TryGet(extra, key, out object? raw)) return;

            if (raw is TEnum already) { value = already; return; }
            // Names only. An integer would let a caller select a member that does not exist —
            // Enum.IsDefined is the difference between "Managed" and a silent out-of-range cast.
            if (raw is string s && Enum.TryParse(s, ignoreCase: true, out TEnum parsed) &&
                Enum.IsDefined(typeof(TEnum), parsed))
            {
                value = parsed;
                return;
            }

            throw Unusable(plugin, key, raw,
                "one of " + string.Join(", ", Enum.GetNames(typeof(TEnum)).Select(n => "'" + n + "'")));
        }

        private static bool TryGet(IReadOnlyDictionary<string, object>? extra, string key,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? value)
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

        private static ArgumentException Unusable(string plugin, string key, object raw, string expected) =>
            new ArgumentException(
                $"{plugin}: option '{key}' must be {expected}, but was " +
                (raw == null ? "null" : $"'{Convert.ToString(raw, CultureInfo.InvariantCulture)}' ({raw.GetType().Name})") +
                ". The request is refused rather than silently defaulted.",
                "options");
    }
}
