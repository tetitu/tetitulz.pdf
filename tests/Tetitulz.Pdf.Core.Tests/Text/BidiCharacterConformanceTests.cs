// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text
{
    /// <summary>
    /// The bidi resolver against Unicode's own conformance file for UAX #9, <c>BidiCharacterTest.txt</c> of the
    /// Character Database the class and bracket tables are generated from (<c>Fixtures/Unicode</c>, gzipped and
    /// otherwise as published): every case's paragraph level, its levels after L1, and its visual order after L2.
    /// </summary>
    /// <remarks>
    /// The file reaches what a resolver's own tests miss: an explicit embedding or override beside numbers,
    /// brackets or an isolate. A sequence split at isolates alone, or an embedding's run taking the paragraph's
    /// sos, gets dozens of its cases wrong in levels and in order — and an RTL embedding of <c>1 2 3</c> then
    /// reads as written.
    /// </remarks>
    public class BidiCharacterConformanceTests
    {
        private static string FixturePath(string name)
            => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Unicode", name);

        private static IEnumerable<string> Lines()
        {
            using var file = File.OpenRead(FixturePath("BidiCharacterTest-" + UnicodeBidi.BidiClassUnicodeVersion + ".txt.gz"));
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null) yield return line;
        }

        [Fact]
        public void EveryCaseOfUnicodesConformanceFile_ResolvesAsItSays()
        {
            int cases = 0;
            var failures = new List<string>();
            foreach (string line in Lines())
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] field = line.Split(';');
                int[] codePoints = field[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToInt32(h, 16)).ToArray();
                var direction = field[1] switch { "0" => BaseDirection.LeftToRight, "1" => BaseDirection.RightToLeft, _ => BaseDirection.Auto };
                int paragraphLevel = int.Parse(field[2]);
                string[] levels = field[3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int[] order = field[4].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                cases++;

                // The text in code units, and where each code point starts in it.
                var text = new StringBuilder();
                var unitOf = new int[codePoints.Length];
                for (int i = 0; i < codePoints.Length; i++) { unitOf[i] = text.Length; text.Append(char.ConvertFromUtf32(codePoints[i])); }
                var paragraph = UnicodeBidi.ResolveParagraph(text.ToString(), direction);
                int[] lineLevels = paragraph.LineLevels(0, text.Length);

                bool levelsAgree = paragraph.ParagraphLevel == paragraphLevel;
                for (int i = 0; i < codePoints.Length && levelsAgree; i++)
                    if (levels[i] != "x" && lineLevels[unitOf[i]] != int.Parse(levels[i])) levelsAgree = false;

                var codePointAt = new Dictionary<int, int>();
                for (int i = 0; i < codePoints.Length; i++) codePointAt[unitOf[i]] = i;
                int[] ours = paragraph.VisualOrder(0, text.Length)
                    .Where(codePointAt.ContainsKey).Select(u => codePointAt[u]).Where(i => levels[i] != "x").ToArray();

                if ((!levelsAgree || !ours.SequenceEqual(order)) && failures.Count < 10)
                    failures.Add(line + "  -> levels " + string.Join(" ", unitOf.Select(u => lineLevels[u]))
                                 + "; order " + string.Join(" ", ours));
            }

            Assert.True(cases > 90_000, $"only {cases} cases read: the file is not the whole conformance file");
            Assert.True(failures.Count == 0, "cases that disagree:\n" + string.Join("\n", failures));
        }

        /// <summary>
        /// One character of each Bidi_Class, for <c>BidiTest.txt</c>, whose cases are sequences of classes. None is a
        /// paired bracket, as the file asks; B is U+2029, which the file puts only at a sequence's end.
        /// </summary>
        private static readonly Dictionary<string, char> Representative = new Dictionary<string, char>
        {
            ["L"] = 'A', ["R"] = '\u05D0', ["AL"] = '\u0627', ["EN"] = '0', ["ES"] = '+', ["ET"] = '#', ["AN"] = '\u0660',
            ["CS"] = ',', ["NSM"] = '\u0300', ["BN"] = '\u00AD', ["B"] = '\u2029', ["S"] = '\t', ["WS"] = ' ', ["ON"] = '!',
            ["LRE"] = '\u202A', ["LRO"] = '\u202D', ["RLE"] = '\u202B', ["RLO"] = '\u202E', ["PDF"] = '\u202C',
            ["LRI"] = '\u2066', ["RLI"] = '\u2067', ["FSI"] = '\u2068', ["PDI"] = '\u2069',
        };

        /// <summary>
        /// Every case of Unicode's other conformance file, <c>BidiTest.txt</c> — every sequence of up to four classes,
        /// and longer ones for the algorithm's known pitfalls (overflow, isolates, FSI), under each paragraph direction
        /// its bitset names — through a representative character of each class: the levels after L1 and the order.
        /// </summary>
        [Fact]
        public void EveryCaseOfUnicodesClassSequenceFile_ResolvesAsItSays()
        {
            string[] levels = null;
            int[] order = null;
            int cases = 0;
            var failures = new List<string>();
            using var file = File.OpenRead(FixturePath("BidiTest-" + UnicodeBidi.BidiClassUnicodeVersion + ".txt.gz"));
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line.StartsWith("@Levels:", StringComparison.Ordinal))
                {
                    levels = line.Substring(8).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    continue;
                }
                if (line.StartsWith("@Reorder:", StringComparison.Ordinal))
                {
                    order = line.Substring(9).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    continue;
                }
                string[] field = line.Split(';');
                string text = new string(field[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(c => Representative[c]).ToArray());
                int directions = int.Parse(field[1].Trim());
                foreach (var (bit, direction) in new[] { (1, BaseDirection.Auto), (2, BaseDirection.LeftToRight), (4, BaseDirection.RightToLeft) })
                {
                    if ((directions & bit) == 0) continue;
                    cases++;
                    var paragraph = UnicodeBidi.ResolveParagraph(text, direction);
                    int[] lineLevels = paragraph.LineLevels(0, text.Length);
                    bool agree = true;
                    for (int i = 0; i < text.Length && agree; i++)
                        if (levels[i] != "x" && lineLevels[i] != int.Parse(levels[i])) agree = false;
                    int[] ours = paragraph.VisualOrder(0, text.Length).Where(i => levels[i] != "x").ToArray();
                    if ((!agree || !ours.SequenceEqual(order)) && failures.Count < 10)
                        failures.Add($"{field[0]} ({direction}) -> levels {string.Join(" ", lineLevels)}; order {string.Join(" ", ours)};"
                                     + $" expected {string.Join(" ", levels)}; {string.Join(" ", order)}");
                }
            }

            Assert.True(cases > 400_000, $"only {cases} cases read: the file is not the whole conformance file");
            Assert.True(failures.Count == 0, "cases that disagree:\n" + string.Join("\n", failures));
        }

        /// <summary>The file's Character Database is the one both generated tables were built from.</summary>
        [Fact]
        public void TheConformanceFile_IsOfTheTablesUnicodeVersion()
        {
            string header = Lines().First();
            Assert.Equal("# BidiCharacterTest-" + UnicodeBidi.BidiClassUnicodeVersion + ".txt", header);
            Assert.Equal(UnicodeBidi.BidiClassUnicodeVersion, UnicodeBidi.BidiBracketsUnicodeVersion);
        }
    }
}
