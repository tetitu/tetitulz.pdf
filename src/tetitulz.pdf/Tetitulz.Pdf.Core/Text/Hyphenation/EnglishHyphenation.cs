// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Tetitulz.Pdf.Core.Text.Hyphenation
{
    /// <summary>
    /// Where an English word may be hyphenated: Frank Liang's algorithm (TeX's, 1983) over Gerard
    /// Kuiken's American English patterns from the hyph-utf8 package (<c>hyph-en-us.tex</c>, embedded
    /// unmodified with its notice: "Copying and distribution of this file, with or without
    /// modification, are permitted in any medium without royalty provided the copyright notice and
    /// this notice are preserved").
    /// </summary>
    /// <remarks>
    /// <para><b>The algorithm.</b> Each pattern is letters with digits between them (<c>.ach4</c>,
    /// <c>ad4der</c>); for the word written <c>.word.</c>, every pattern matching anywhere contributes its
    /// digits at its positions, the highest digit at a position wins, and an odd one there is a break
    /// opportunity. The file's own exceptions (<c>\hyphenation{…}</c>, a word with its hyphens written)
    /// replace the patterns' answer for that word.</para>
    /// <para><b>The margins</b> are the file's <c>hyphenmins</c> for typesetting: at least 2 letters
    /// before a break and 3 after.</para>
    /// <para><b>What is asked.</b> A word of ASCII letters alone, at most <see cref="MaxWordLength"/>
    /// long; anything else has no break opportunity here (a word with a digit, an apostrophe or a
    /// letter outside the patterns' alphabet is left whole, as TeX leaves it).</para>
    /// </remarks>
    internal static class EnglishHyphenation
    {
        /// <summary>The fewest letters before a break (the file's <c>hyphenmins</c>, left).</summary>
        internal const int LeftMin = 2;

        /// <summary>The fewest letters after a break (the file's <c>hyphenmins</c>, right).</summary>
        internal const int RightMin = 3;

        /// <summary>The longest word asked about; a longer one is left whole.</summary>
        internal const int MaxWordLength = 64;

        private static readonly object Gate = new object();

        /// <summary>
        /// The parsed file, published whole. Three static fields set one after another and read after
        /// a check of the last one alone would let a racing first call on a weakly ordered processor
        /// (ARM64) read the exceptions as null, or the longest pattern as 0, after seeing the patterns.
        /// A reader of one reference to an immutable object sees all of it.
        /// </summary>
        private sealed class Tables
        {
            internal readonly Dictionary<string, byte[]> Patterns;
            internal readonly Dictionary<string, int[]> Exceptions;
            internal readonly int LongestPattern;

            internal Tables(Dictionary<string, byte[]> patterns, Dictionary<string, int[]> exceptions, int longestPattern)
            {
                Patterns = patterns;
                Exceptions = exceptions;
                LongestPattern = longestPattern;
            }
        }

        private static Tables _tables;

        /// <summary>
        /// The positions inside <paramref name="word"/> where it may break — each the number of letters
        /// before the break — in ascending order; empty when it has none.
        /// </summary>
        internal static IReadOnlyList<int> BreakPoints(string word)
        {
            if (word == null) throw new ArgumentNullException(nameof(word));
            if (word.Length < LeftMin + RightMin || word.Length > MaxWordLength) return Array.Empty<int>();
            foreach (char c in word)
                if (!IsAsciiLetter(c)) return Array.Empty<int>();

            Tables tables = Load();
            string lower = word.ToLowerInvariant();
            if (tables.Exceptions.TryGetValue(lower, out int[] given)) return given;

            string dotted = "." + lower + ".";
            var values = new byte[dotted.Length + 1];
            for (int start = 0; start < dotted.Length; start++)
            {
                for (int length = 1; length <= tables.LongestPattern && start + length <= dotted.Length; length++)
                {
                    if (!tables.Patterns.TryGetValue(dotted.Substring(start, length), out byte[] digits)) continue;
                    for (int i = 0; i < digits.Length; i++)
                        if (digits[i] > values[start + i]) values[start + i] = digits[i];
                }
            }

            // values[k] sits before dotted[k]; a break before the word's letter j is values[j + 1].
            var points = new List<int>();
            for (int j = LeftMin; j <= lower.Length - RightMin; j++)
                if ((values[j + 1] & 1) == 1) points.Add(j);
            return points;
        }

        /// <summary>
        /// The break opportunities inside a token as text lays it out — a word with the punctuation
        /// before and after it (<c>(hyphenation),</c>): what lies between the first and last letter or
        /// digit is asked (<see cref="BreakPoints"/>), the positions counted in the token. Punctuation
        /// inside (<c>co-operate</c>, <c>don't</c>), a digit (<c>hyphenation2</c>, a part number) or a
        /// letter outside ASCII (<c>café</c>) leaves the token whole.
        /// </summary>
        internal static IReadOnlyList<int> BreakPointsWithin(string token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            int start = 0, end = token.Length;
            while (start < end && !char.IsLetterOrDigit(token[start])) start++;
            while (end > start && !char.IsLetterOrDigit(token[end - 1])) end--;
            if (start == 0 && end == token.Length) return BreakPoints(token);
            var points = BreakPoints(token.Substring(start, end - start));
            if (points.Count == 0 || start == 0) return points;
            var shifted = new int[points.Count];
            for (int i = 0; i < shifted.Length; i++) shifted[i] = points[i] + start;
            return shifted;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

        /// <summary><paramref name="word"/> with a hyphen at every break opportunity, for reading.</summary>
        internal static string Hyphenated(string word)
        {
            var points = BreakPoints(word);
            if (points.Count == 0) return word;
            var sb = new StringBuilder(word.Length + points.Count);
            int from = 0;
            foreach (int p in points)
            {
                sb.Append(word, from, p - from).Append('-');
                from = p;
            }
            return sb.Append(word, from, word.Length - from).ToString();
        }

        private static Tables Load()
        {
            Tables loaded = Volatile.Read(ref _tables);
            if (loaded != null) return loaded;
            lock (Gate)
            {
                if (_tables != null) return _tables;
                string text;
                using (Stream s = typeof(EnglishHyphenation).Assembly.GetManifestResourceStream(
                           "Tetitulz.Pdf.Core.Text.Hyphenation.hyph-en-us.tex"))
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    text = reader.ReadToEnd();

                var patterns = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var exceptions = new Dictionary<string, int[]>(StringComparer.Ordinal);
                int longest = 0;
                foreach (string entry in Group(text, "\\patterns{"))
                {
                    var letters = new StringBuilder(entry.Length);
                    var digits = new List<byte> { 0 };
                    foreach (char c in entry)
                    {
                        if (c >= '0' && c <= '9') digits[digits.Count - 1] = (byte)(c - '0');
                        else { letters.Append(c); digits.Add(0); }
                    }
                    patterns[letters.ToString()] = digits.ToArray();
                    if (letters.Length > longest) longest = letters.Length;
                }
                foreach (string entry in Group(text, "\\hyphenation{"))
                {
                    var points = new List<int>();
                    var letters = new StringBuilder(entry.Length);
                    foreach (char c in entry)
                    {
                        if (c == '-') points.Add(letters.Length);
                        else letters.Append(c);
                    }
                    exceptions[letters.ToString()] = points.ToArray();
                }
                var tables = new Tables(patterns, exceptions, longest);
                Volatile.Write(ref _tables, tables);
                return tables;
            }
        }

        /// <summary>The whitespace-separated entries of a <c>\name{…}</c> group, <c>%</c> comments removed.</summary>
        private static IEnumerable<string> Group(string text, string opening)
        {
            int start = text.IndexOf(opening, StringComparison.Ordinal);
            if (start < 0) yield break;
            int end = text.IndexOf('}', start + opening.Length);
            if (end < 0) yield break;
            foreach (string rawLine in text.Substring(start + opening.Length, end - start - opening.Length).Split('\n'))
            {
                string line = rawLine;
                int comment = line.IndexOf('%');
                if (comment >= 0) line = line.Substring(0, comment);
                foreach (string entry in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    yield return entry;
            }
        }
    }
}
