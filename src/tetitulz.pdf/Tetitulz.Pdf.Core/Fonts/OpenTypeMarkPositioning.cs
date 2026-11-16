// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A font's glyph positioning — the OpenType <c>GPOS</c> <c>curs</c>, <c>mark</c> and <c>mkmk</c>
    /// features, and the <c>kern</c> feature when a producer asks for kerning — applied to a run of glyphs in
    /// logical order as a shaper applies them: single adjustment (lookup type 1), pair adjustment (2),
    /// cursive attachment (3), mark-to-base (4), mark-to-ligature (5), mark-to-mark (6), and context (7) and chained context (8) in all three formats
    /// — by glyph, by class and by coverage — whose nested lookups are any of these, through extension
    /// lookups (9), with <c>GDEF</c>'s glyph classes saying which glyphs are marks and which a lookup's
    /// flags skip; and a font's legacy <c>kern</c> table (format 0, horizontal) for kerning where its
    /// <c>GPOS</c> has none.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these.</b> A producer that shapes by rule (<c>Core.Text.OpenTypeShaper</c>)
    /// still has to put a mark where the font says. The Hebrew fonts on a Windows host keep most of
    /// their anchors behind chained-context rules — Arial, Segoe UI, Times New Roman and David have
    /// about 180 such subtables each in their <c>mark</c> feature, whose nested mark-to-base lookups
    /// no feature lists directly, so a dagesh or a shin dot is anchored only through them — and
    /// Gisha nests single adjustments the same way. Every Arabic font there keeps its marks on the
    /// lam-alef ligature in mark-to-ligature lookups; a mark after a ligature attaches to its last
    /// component, which is the alef's — the only ligature producers form is lam-alef, and never across a
    /// mark. Kerning and cursive attachment need no <c>GDEF</c>; without one every glyph is of class 0.</para>
    /// <para><b>Cursive attachment</b> joins a glyph's exit anchor to the entry anchor of the next glyph
    /// the lookup does not skip — the joining of a Nastaliq-style face whose presentation forms carry
    /// entry and exit anchors. (Dubai, on a Windows host, has such lookups, flagged RightToLeft and
    /// IgnoreMarks, but over glyphs only its own <c>GSUB</c> reaches; producers here draw the
    /// presentation forms its <c>cmap</c> maps, which they do not cover.) It records the join; the composer, which knows the advances
    /// and the visual order, turns it into the advance of the glyph drawn on the left, as shapers set
    /// it. The vertical offset chains: without the RightToLeft flag the later glyph hangs from the
    /// earlier, with it the earlier from the later. A glyph already hanging from one keeps that parent
    /// (some shapers re-root the old chain instead, taking lookups of both orientations on one glyph).</para>
    /// <para><b>Flags.</b> A lookup's IgnoreBaseGlyphs, IgnoreLigatures and IgnoreMarks skip those
    /// <c>GDEF</c> classes when matching; of the marks left, a lookup with a mark filtering set (GDEF
    /// 1.2) skips those outside the set, and otherwise one with a mark-attachment type skips those of
    /// another <c>GDEF</c> mark-attachment class — the set deciding where a lookup names both, as
    /// shapers read it.</para>
    /// <para><b>Lenient and bounded.</b> This is a metric reader: an offset outside its table, a
    /// count that does not fit, or a format it does not know drops that one table, subtable or
    /// lookup, and a font with nothing usable has no positioning at all. Nested lookups stop at a
    /// depth of <see cref="MaxNesting"/>, and one run's application at <see cref="MaxSteps"/>
    /// subtable trials and <see cref="MaxExaminationsPerGlyph"/> lookup positions a glyph, so a font
    /// whose rules call each other, or whose lookups skip every glyph, cannot loop or run away.</para>
    /// <para><b>Which positioning features.</b> <c>mark</c> and <c>mkmk</c>, and the Indic scripts'
    /// <c>abvm</c> and <c>blwm</c> (above- and below-base mark positioning, the features a Devanagari
    /// or Bengali font puts its mark anchors under), are applied always; so is <c>dist</c>, the pair
    /// adjustment Indic and Khmer fonts space their glyphs with, which a shaper applies unasked; and
    /// <c>curs</c>. <c>kern</c> is applied when the producer asks (<see cref="Apply"/>'s
    /// <c>kerning</c>), as a user can turn it off.</para>
    /// <para>Every distance is in the font's design units; <see cref="UnitsPerEm"/> scales them.
    /// An anchor's contour-point (format 2) and device-table (format 3) refinements are not read:
    /// both adjust a hinted rasterisation at a pixel size, and a PDF producer draws unhinted outlines
    /// at every size, so the design coordinates are the anchor.</para>
    /// <para><b>The same reader reads <c>GSUB</c></b> (<see cref="TryLoadSubstitution"/>), for its
    /// one-for-one substitutions only: the <c>rclt</c> and <c>calt</c> features' single substitutions (lookup type 1),
    /// reached directly or through context (5) and chained-context (6) rules in all three formats and
    /// extension lookups (7). The two tables share their script, feature and lookup lists, their
    /// lookup flags and their context rules — <see cref="MatchAndApply"/> serves both. A substitution
    /// keeps one glyph per code point, which is why it needs none of the cluster tracking a ligature
    /// would. <c>rclt</c> is the feature a shaper may not turn off, and is always applied; <c>calt</c>
    /// is one a user can turn off (Word leaves it off, a browser on), so it is applied only when the
    /// producer asks, as kerning is.</para>
    /// </remarks>
    internal sealed class OpenTypeMarkPositioning
    {
        private const int BaseGlyphClass = 1;
        private const int LigatureGlyphClass = 2;
        private const int MarkGlyphClass = 3;
        private const int LookupSingle = 1;
        private const int LookupPair = 2;
        private const int LookupCursive = 3;
        private const int LookupMarkToBase = 4;
        private const int LookupMarkToLigature = 5;
        private const int LookupMarkToMark = 6;
        private const int LookupContext = 7;
        private const int LookupChainContext = 8;
        private const int LookupExtension = 9;
        private const int SubstSingle = 1, SubstLigature = 4, SubstContext = 5, SubstChainContext = 6, SubstExtension = 7;
        private const int RightToLeft = 0x1, IgnoreBaseGlyphs = 0x2, IgnoreLigatures = 0x4, IgnoreMarks = 0x8, UseMarkFilteringSet = 0x10;

        /// <summary>The deepest a context rule's nested lookups may call further rules.</summary>
        internal const int MaxNesting = 8;

        /// <summary>The most subtable trials one run's application makes.</summary>
        internal const int MaxSteps = 200_000;

        /// <summary>
        /// The most lookup positions one run's application examines, per glyph of the run — a
        /// position a lookup's flags skip, or a joining feature's mask passes over, included.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxSteps"/> counts subtable trials, and a skipped position makes none: a font
        /// whose lookups all skip every glyph of a run — mark lookups over a run of bases — cost
        /// its registered lookups (up to the 16-bit count) times the run's length, with nothing
        /// counting. Per glyph, so the work is linear in the text: no font this library has met
        /// registers a thousand lookups for one script.
        /// </remarks>
        internal const int MaxExaminationsPerGlyph = 1024;

        /// <summary>Lookup positions examined on the calling thread: the evidence for <see cref="MaxExaminationsPerGlyph"/>.</summary>
        [System.ThreadStatic] internal static long PositionsExamined;

        private readonly byte[] _d;
        private readonly int _classDef;
        private int _markAttachClassDef = -1;         // GDEF's mark-attachment class definition, or -1
        private int _markGlyphSets = -1;              // GDEF 1.2's mark glyph sets table, or -1
        private readonly int _lookupList;
        private readonly int _lookupCount;
        private readonly Dictionary<string, int[]> _byScript = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> _kernByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> _distanceByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GPOS dist
        private readonly Dictionary<string, int[]> _cursiveByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int[]> _contextualByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GSUB calt
        private readonly Dictionary<string, int[]> _requiredLigaturesByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GSUB rlig
        private readonly Dictionary<string, int[]> _ligaturesByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GSUB liga
        private readonly Dictionary<string, int[]> _discretionaryByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GSUB dlig
        private readonly Dictionary<string, int[]> _historicalByScript = new Dictionary<string, int[]>(StringComparer.Ordinal);   // GSUB hlig
        // GSUB isol, fina, init, medi: script → feature → lookups.
        private readonly Dictionary<string, Dictionary<string, int[]>> _joiningByScript =
            new Dictionary<string, Dictionary<string, int[]>>(StringComparer.Ordinal);
        private static readonly string[] JoiningFeatures = { "isol", "fina", "init", "medi" };
        private int _legacyKernPairs = -1;            // the legacy kern table's format 0 pair array, or -1
        private int _legacyKernCount;
        private readonly Dictionary<int, Lookup> _lookups = new Dictionary<int, Lookup>();

        private readonly bool _substitution;          // a GSUB, not a GPOS
        private readonly int _contextType, _chainContextType, _extensionType;

        private OpenTypeMarkPositioning(byte[] data, int classDef, int lookupList, int lookupCount, int unitsPerEm,
                                        bool substitution = false)
        {
            _d = data;
            _classDef = classDef;
            _lookupList = lookupList;
            _lookupCount = lookupCount;
            UnitsPerEm = unitsPerEm;
            _substitution = substitution;
            _contextType = substitution ? SubstContext : LookupContext;
            _chainContextType = substitution ? SubstChainContext : LookupChainContext;
            _extensionType = substitution ? SubstExtension : LookupExtension;
        }

        /// <summary>
        /// Reads <paramref name="font"/>'s <c>GSUB</c> <c>rclt</c>, <c>calt</c>, <c>rlig</c> and
        /// <c>liga</c> substitutions and its joining features, or returns <c>null</c> when it has none
        /// this can use.
        /// </summary>
        internal static OpenTypeMarkPositioning TryLoadSubstitution(byte[] font)
        {
            if (font == null) return null;
            if (!SfntTableDirectory.TryRead(font, out IReadOnlyList<SfntTableDirectory.TableRecord> tables, out _))
                return null;
            int gdef = -1, gdefLen = 0, gsub = -1, gsubLen = 0;
            foreach (var t in tables)
            {
                if (t.Tag == "GDEF") { gdef = t.Offset; gdefLen = t.Length; }
                else if (t.Tag == "GSUB") { gsub = t.Offset; gsubLen = t.Length; }
            }
            if (gsub < 0) return null;

            var r = new Bounded(font);
            int classDef = gdef < 0 ? -1 : r.Sub(gdef, gdefLen, 4);
            int lookupList = r.U16(gsub) != 1 ? -1 : r.Sub(gsub, gsubLen, 8);
            int lookupCount = lookupList < 0 ? 0 : Math.Max(0, r.U16(lookupList));
            if (lookupCount == 0) return null;

            var substitution = new OpenTypeMarkPositioning(font, classDef, lookupList, lookupCount, 1000, substitution: true);
            if (gdef >= 0)
            {
                substitution._markAttachClassDef = r.Sub(gdef, gdefLen, 10);
                if (r.U16(gdef) == 1 && r.U16(gdef + 2) >= 2) substitution._markGlyphSets = r.Sub(gdef, gdefLen, 12);
            }
            substitution.ReadScripts(r, gsub, gsubLen);
            return substitution._byScript.Count > 0 || substitution._contextualByScript.Count > 0
                || substitution._joiningByScript.Count > 0 || substitution._requiredLigaturesByScript.Count > 0
                || substitution._ligaturesByScript.Count > 0 || substitution._discretionaryByScript.Count > 0
                || substitution._historicalByScript.Count > 0 ? substitution : null;
        }

        /// <summary>
        /// True when the font's <c>GSUB</c> has joining feature <paramref name="feature"/>
        /// (<c>isol</c>, <c>fina</c>, <c>init</c>, <c>medi</c>) for <paramref name="script"/>, or for its
        /// default script.
        /// </summary>
        internal bool HasJoiningFeature(string script, string feature)
            => JoiningLookups(script, feature) != null;

        private int[] JoiningLookups(string script, string feature)
        {
            foreach (string tag in Text.UnicodeScripts.Fallbacks(script))
                if (_joiningByScript.TryGetValue(tag, out var byFeature))
                    return byFeature.TryGetValue(feature, out int[] l) ? l : null;
            return null;
        }

        /// <summary>
        /// The <c>rclt</c> and <c>rlig</c> substitutions for <paramref name="script"/> (the font's
        /// default script when it has none for it) — and with <paramref name="contextualAlternates"/>
        /// the <c>calt</c> ones, with <paramref name="ligatures"/> the <c>liga</c> ones, with
        /// <paramref name="discretionary"/> the <c>dlig</c> ones and with <paramref name="historical"/> the
        /// <c>hlig</c> ones, in lookup
        /// order with them — applied to <paramref name="glyphs"/>, which are in logical order, each
        /// lookup over the whole run before the next; the glyphs after, with the input each stands for
        /// where a ligature joined several.
        /// </summary>
        /// <remarks>
        /// <paramref name="joining"/> names, per glyph, the joining feature (<c>isol</c>, <c>fina</c>,
        /// <c>init</c>, <c>medi</c>) it is to take, or <c>null</c>: a joining feature's lookups start
        /// only at the glyphs that name it, as a shaper's feature masks have them, while <c>rclt</c>
        /// and <c>calt</c> start anywhere. All are run in lookup order together.
        /// </remarks>
        internal Substituted Substitute(string script, IReadOnlyList<ushort> glyphs, bool contextualAlternates = false,
                                        IReadOnlyList<string> joining = null, bool ligatures = false,
                                        bool discretionary = false, bool historical = false)
        {
            var run = new Run(glyphs, new Positions(glyphs.Count), this);
            // Each lookup, in lookup order, with the joining features it may start at — null for anywhere.
            var masks = new SortedDictionary<int, HashSet<string>>();
            void Everywhere(int[] set) { if (set != null) foreach (int li in set) masks[li] = null; }
            Everywhere(LookupsFor(_byScript, script));
            Everywhere(LookupsFor(_requiredLigaturesByScript, script));
            if (contextualAlternates) Everywhere(LookupsFor(_contextualByScript, script));
            if (ligatures) Everywhere(LookupsFor(_ligaturesByScript, script));
            if (discretionary) Everywhere(LookupsFor(_discretionaryByScript, script));
            if (historical) Everywhere(LookupsFor(_historicalByScript, script));
            if (joining != null)
            {
                foreach (string feature in JoiningFeatures)
                {
                    int[] set = JoiningLookups(script, feature);
                    if (set == null) continue;
                    foreach (int li in set)
                    {
                        if (!masks.TryGetValue(li, out HashSet<string> mask)) masks[li] = new HashSet<string>(StringComparer.Ordinal) { feature };
                        else mask?.Add(feature);
                    }
                }
            }
            foreach (var entry in masks)
            {
                Lookup lookup = LookupAt(entry.Key);
                if (lookup == null) continue;
                for (int i = 0; i < run.Glyphs.Length && run.MayExamine(); i++)
                {
                    if (entry.Value != null && (i >= joining.Count || joining[i] == null || !entry.Value.Contains(joining[i]))) continue;
                    if (!SkipsAt(lookup, run, i)) ApplyAt(lookup, i, run, 0);
                }
            }
            return run.Compact();
        }

        /// <summary>The font's design units per em, which every distance here is in.</summary>
        internal int UnitsPerEm { get; }

        /// <summary>What applying the features to a run decided, per glyph in the run's logical order.</summary>
        internal sealed class Positions
        {
            internal Positions(int n)
            {
                Parent = new int[n];
                for (int i = 0; i < n; i++) Parent[i] = -1;
                AnchorX = new int[n]; AnchorY = new int[n];
                XPlacement = new int[n]; YPlacement = new int[n]; XAdvance = new int[n];
                JoinedTo = new int[n]; CursiveParent = new int[n];
                for (int i = 0; i < n; i++) JoinedTo[i] = CursiveParent[i] = -1;
                ExitX = new int[n]; EntryX = new int[n]; CursiveY = new int[n];
            }

            /// <summary>The glyph each one's exit anchor is joined to (its entry anchor), or -1.</summary>
            internal int[] JoinedTo { get; }

            /// <summary>A joined glyph's exit anchor's x, for the join <see cref="JoinedTo"/> records.</summary>
            internal int[] ExitX { get; }

            /// <summary>A joined glyph's entry anchor's x, for the join that ends at it.</summary>
            internal int[] EntryX { get; }

            /// <summary>The glyph a cursive join hangs each one from, or -1.</summary>
            internal int[] CursiveParent { get; }

            /// <summary>How far above its <see cref="CursiveParent"/> a glyph is drawn, so their anchors meet.</summary>
            internal int[] CursiveY { get; }

            /// <summary>The glyph each one is attached to, or -1.</summary>
            internal int[] Parent { get; }

            /// <summary>An attached glyph's origin from its parent's, so their anchors meet.</summary>
            internal int[] AnchorX { get; }

            /// <inheritdoc cref="AnchorX"/>
            internal int[] AnchorY { get; }

            /// <summary>Adjustments a single-adjustment lookup made: where the glyph is drawn, and its advance.</summary>
            internal int[] XPlacement { get; }

            /// <inheritdoc cref="XPlacement"/>
            internal int[] YPlacement { get; }

            /// <inheritdoc cref="XPlacement"/>
            internal int[] XAdvance { get; }
        }

        /// <summary>
        /// Reads <paramref name="font"/>'s glyph positioning, or returns <c>null</c> when it has none
        /// this can use — no lookup in a <c>curs</c>, <c>mark</c>, <c>mkmk</c>, <c>abvm</c>, <c>blwm</c>, <c>dist</c> or <c>kern</c> feature (a mark
        /// lookup only counting with <c>GDEF</c> glyph classes) and no legacy <c>kern</c> table.
        /// </summary>
        internal static OpenTypeMarkPositioning TryLoad(byte[] font, int unitsPerEm)
        {
            if (font == null || unitsPerEm <= 0) return null;
            if (!SfntTableDirectory.TryRead(font, out IReadOnlyList<SfntTableDirectory.TableRecord> tables, out _))
                return null;

            int gdef = -1, gdefLen = 0, gpos = -1, gposLen = 0, kern = -1, kernLen = 0;
            foreach (var t in tables)
            {
                if (t.Tag == "GDEF") { gdef = t.Offset; gdefLen = t.Length; }
                else if (t.Tag == "GPOS") { gpos = t.Offset; gposLen = t.Length; }
                else if (t.Tag == "kern") { kern = t.Offset; kernLen = t.Length; }
            }

            var r = new Bounded(font);
            int classDef = gdef < 0 ? -1 : r.Sub(gdef, gdefLen, 4);
            int lookupList = gpos < 0 || r.U16(gpos) != 1 ? -1 : r.Sub(gpos, gposLen, 8);
            int lookupCount = lookupList < 0 ? 0 : Math.Max(0, r.U16(lookupList));

            var positioning = new OpenTypeMarkPositioning(font, classDef, lookupList, lookupCount, unitsPerEm);
            if (gdef >= 0)
            {
                positioning._markAttachClassDef = r.Sub(gdef, gdefLen, 10);
                if (r.U16(gdef) == 1 && r.U16(gdef + 2) >= 2) positioning._markGlyphSets = r.Sub(gdef, gdefLen, 12);
            }
            if (lookupCount > 0) positioning.ReadScripts(r, gpos, gposLen);
            if (classDef < 0) positioning._byScript.Clear();      // marks need GDEF to be told apart
            if (kern >= 0) positioning.ReadLegacyKern(r, kern, kernLen);
            return positioning._byScript.Count > 0 || positioning._kernByScript.Count > 0 || positioning._distanceByScript.Count > 0
                || positioning._cursiveByScript.Count > 0 || positioning._legacyKernPairs >= 0
                ? positioning : null;
        }

        /// <summary>True when the font's <c>GPOS</c> joins <paramref name="script"/> (or its default script) cursively.</summary>
        internal bool HasCursive(string script) => LookupsFor(_cursiveByScript, script) != null;

        /// <summary>True when the font's <c>GPOS</c> has <c>dist</c> adjustments for <paramref name="script"/> (or its default script): applied unasked, marks or none.</summary>
        internal bool HasDistance(string script) => LookupsFor(_distanceByScript, script) != null;

        /// <summary>True when the font has mark lookups for some script.</summary>
        internal bool HasMarkPositioning => _byScript.Count > 0;

        /// <summary>True when the font can kern: a <c>GPOS</c> <c>kern</c> feature or a legacy <c>kern</c> table.</summary>
        internal bool CanKern => _kernByScript.Count > 0 || _legacyKernPairs >= 0;

        /// <summary>True when the font's <c>GPOS</c> kerns <paramref name="script"/> (or its default script).</summary>
        internal bool HasGposKerning(string script) => LookupsFor(_kernByScript, script) != null;

        /// <summary>
        /// The legacy <c>kern</c> table's adjustment for <paramref name="left"/> followed, on the page,
        /// by <paramref name="right"/>, in design units; 0 for no pair.
        /// </summary>
        internal int LegacyKerning(ushort left, ushort right)
        {
            if (_legacyKernPairs < 0) return 0;
            var r = new Bounded(_d);
            uint key = ((uint)left << 16) | right;
            int lo = 0, hi = _legacyKernCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1, rec = _legacyKernPairs + 6 * mid;
                uint k = ((uint)Math.Max(0, r.U16(rec)) << 16) | (uint)Math.Max(0, r.U16(rec + 2));
                if (key < k) hi = mid - 1;
                else if (key > k) lo = mid + 1;
                else return r.I16(rec + 4);
            }
            return 0;
        }

        /// <summary>The first horizontal, non-minimum, non-cross-stream format 0 subtable of a version 0 <c>kern</c> table.</summary>
        private void ReadLegacyKern(Bounded r, int kern, int length)
        {
            if (r.U16(kern) != 0) return;                       // version 0 only (Apple's version 1 is a u32 1.0)
            int count = r.U16(kern + 2);
            int o = kern + 4;
            for (int t = 0; t < count && o + 6 <= kern + length; t++)
            {
                int subLength = r.U16(o + 2), coverage = r.U16(o + 4);
                if (subLength < 6) return;
                bool format0 = (coverage >> 8) == 0, horizontal = (coverage & 0x1) != 0;
                bool minimum = (coverage & 0x2) != 0, crossStream = (coverage & 0x4) != 0;
                if (format0 && horizontal && !minimum && !crossStream)
                {
                    int pairs = r.U16(o + 6);
                    if (pairs > 0 && r.InRange(o + 14, 6 * pairs)) { _legacyKernPairs = o + 14; _legacyKernCount = pairs; }
                    return;
                }
                o += subLength;
            }
        }

        /// <summary>True when <c>GDEF</c> classes <paramref name="glyph"/> as a mark.</summary>
        internal bool IsMark(ushort glyph) => GlyphClass(glyph) == MarkGlyphClass;

        /// <summary>
        /// Applies the <c>curs</c>, <c>mark</c> and <c>mkmk</c> lookups for <paramref name="script"/> (an OpenType
        /// script tag; the font's default script when it has none for it) to <paramref name="glyphs"/>,
        /// which are in logical order, each lookup over the whole run before the next, in lookup order.
        /// </summary>
        /// <remarks>With <paramref name="kerning"/> the <c>kern</c> feature's lookups join them, in lookup order with the rest.</remarks>
        internal Positions Apply(string script, IReadOnlyList<ushort> glyphs, bool kerning = false)
        {
            var result = new Positions(glyphs.Count);
            var all = new SortedSet<int>();
            foreach (int[] set in new[] { LookupsFor(_byScript, script), LookupsFor(_cursiveByScript, script),
                                          LookupsFor(_distanceByScript, script), kerning ? LookupsFor(_kernByScript, script) : null })
                if (set != null) all.UnionWith(set);
            if (all.Count == 0) return result;
            int[] lookups = all.ToArray();

            var run = new Run(glyphs, result, this);
            foreach (int index in lookups)
            {
                Lookup lookup = LookupAt(index);
                if (lookup == null) continue;
                for (int i = 0; i < glyphs.Count && run.MayExamine(); i++)
                    if (!SkipsAt(lookup, run, i)) ApplyAt(lookup, i, run, 0);
            }
            return result;
        }

        /// <summary>
        /// The lookups registered for <paramref name="script"/> — or, where the font has none for it,
        /// for the first of its fallbacks it has (<see cref="Text.UnicodeScripts.Fallbacks"/>: an Indic
        /// script's first-generation tag, the default script, then Latin, as a shaper falls back).
        /// </summary>
        private static int[] LookupsFor(Dictionary<string, int[]> byScript, string script)
        {
            foreach (string tag in Text.UnicodeScripts.Fallbacks(script))
                if (byScript.TryGetValue(tag, out int[] l)) return l;
            return null;
        }

        private sealed class Run
        {
            public Run(IReadOnlyList<ushort> glyphs, Positions positions, OpenTypeMarkPositioning owner)
            {
                Glyphs = glyphs.ToArray();          // a copy: a substitution writes into it
                Positions = positions;
                Classes = new int[glyphs.Count];
                for (int i = 0; i < glyphs.Count; i++) Classes[i] = owner.GlyphClass(glyphs[i]);
                _examinationsLeft = (long)glyphs.Count * MaxExaminationsPerGlyph;
            }

            public ushort[] Glyphs { get; }
            public Positions Positions { get; }
            public int[] Classes { get; }
            public int Steps;

            private long _examinationsLeft;

            /// <summary>
            /// Whether the application may examine one more lookup position, charging it: within
            /// <see cref="MaxSteps"/> subtable trials and <see cref="MaxExaminationsPerGlyph"/>
            /// positions a glyph.
            /// </summary>
            public bool MayExamine()
            {
                if (Steps >= MaxSteps || _examinationsLeft <= 0) return false;
                _examinationsLeft--;
                PositionsExamined++;
                return true;
            }

            /// <summary>Which glyphs a ligature substitution removed, or <c>null</c> while none has.</summary>
            public bool[] Removed { get; private set; }

            /// <summary>
            /// For a glyph a ligature substitution formed, the run indices of its components in run
            /// order (its own first); <c>null</c> for a glyph that is its own source, and <c>null</c>
            /// altogether while no ligature has formed.
            /// </summary>
            public List<int>[] Sources { get; private set; }

            /// <summary>Removes glyph <paramref name="component"/>, a ligature's component, folding its sources into glyph <paramref name="into"/>'s.</summary>
            public void Remove(int component, int into)
            {
                Removed ??= new bool[Glyphs.Length];
                Sources ??= new List<int>[Glyphs.Length];
                Removed[component] = true;
                Sources[into] ??= new List<int> { into };
                if (Sources[component] != null) Sources[into].AddRange(Sources[component]);
                else Sources[into].Add(component);
                Sources[component] = null;
            }

            /// <summary>
            /// The glyphs still in the run, in order, and for each the run indices it stands for —
            /// itself, or a ligature's components; <c>Sources</c> is <c>null</c> when no ligature formed.
            /// </summary>
            public Substituted Compact()
            {
                if (Removed == null) return new Substituted(Glyphs, null);
                var glyphs = new List<ushort>(Glyphs.Length);
                var sources = new List<int[]>(Glyphs.Length);
                for (int i = 0; i < Glyphs.Length; i++)
                {
                    if (Removed[i]) continue;
                    glyphs.Add(Glyphs[i]);
                    List<int> s = Sources[i];
                    if (s == null) sources.Add(new[] { i });
                    else { s.Sort(); sources.Add(s.ToArray()); }
                }
                return new Substituted(glyphs.ToArray(), sources.ToArray());
            }
        }

        /// <summary>
        /// What <see cref="Substitute"/> made of a run: the glyphs after, and — when a ligature formed
        /// — for each the input indices it stands for, ascending; <c>null</c> when every glyph stands
        /// for the input glyph at its own index.
        /// </summary>
        internal readonly struct Substituted
        {
            internal Substituted(ushort[] glyphs, int[][] sources) { Glyphs = glyphs; Sources = sources; }
            internal ushort[] Glyphs { get; }
            internal int[][] Sources { get; }
        }

        // ------------------------------------------------------------------ GDEF

        private int GlyphClass(ushort glyph) => _classDef < 0 ? 0 : ClassOf(new Bounded(_d), _classDef, glyph);

        /// <summary>A class definition's class for <paramref name="glyph"/>: formats 1 and 2, 0 for none.</summary>
        private static int ClassOf(Bounded r, int o, ushort glyph)
        {
            if (o < 0) return 0;
            int format = r.U16(o);
            if (format == 1)
            {
                int start = r.U16(o + 2), count = r.U16(o + 4);
                int i = glyph - start;
                return i >= 0 && i < count ? Math.Max(0, r.U16(o + 6 + 2 * i)) : 0;
            }
            if (format == 2)
            {
                int count = r.U16(o + 2);
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1, rec = o + 4 + 6 * mid;
                    int first = r.U16(rec), last = r.U16(rec + 2);
                    if (first < 0 || last < 0) return 0;
                    if (glyph < first) hi = mid - 1;
                    else if (glyph > last) lo = mid + 1;
                    else return Math.Max(0, r.U16(rec + 4));
                }
            }
            return 0;
        }

        /// <summary>True when a lookup with <paramref name="flag"/> skips a glyph of <paramref name="glyphClass"/>.</summary>
        private static bool SkipsClass(int flag, int glyphClass)
            => (flag & IgnoreBaseGlyphs) != 0 && glyphClass == BaseGlyphClass
            || (flag & IgnoreLigatures) != 0 && glyphClass == LigatureGlyphClass
            || (flag & IgnoreMarks) != 0 && glyphClass == MarkGlyphClass;

        /// <summary>
        /// True when <paramref name="lookup"/> skips glyph <paramref name="j"/> of the run: by its
        /// <c>GDEF</c> class, and a mark outside the lookup's mark filtering set or, without one, of a
        /// mark-attachment class other than the lookup's mark-attachment type.
        /// </summary>
        private bool SkipsAt(Lookup lookup, Run run, int j)
        {
            if (run.Removed != null && run.Removed[j]) return true;   // a ligature's component is no longer in the run
            int glyphClass = run.Classes[j];
            if (SkipsClass(lookup.Flag, glyphClass)) return true;
            if (glyphClass != MarkGlyphClass) return false;
            var r = new Bounded(_d);
            if ((lookup.Flag & UseMarkFilteringSet) != 0)
                return !InMarkGlyphSet(r, lookup.MarkFilteringSet, run.Glyphs[j]);
            int attachType = lookup.Flag >> 8;
            return attachType != 0 && (_markAttachClassDef < 0 || ClassOf(r, _markAttachClassDef, run.Glyphs[j]) != attachType);
        }

        /// <summary>True when <paramref name="glyph"/> is in mark glyph set <paramref name="set"/> (<c>GDEF</c> 1.2; a set the font does not have holds nothing).</summary>
        private bool InMarkGlyphSet(Bounded r, int set, ushort glyph)
        {
            if (_markGlyphSets < 0 || set < 0 || r.U16(_markGlyphSets) != 1 || set >= r.U16(_markGlyphSets + 2)) return false;
            uint offset = r.U32(_markGlyphSets + 4 + 4 * set);
            if (offset == 0 || offset >= (uint)r.Length) return false;
            long coverage = _markGlyphSets + (long)offset;
            return coverage < r.Length && CoverageIndex(r, (int)coverage, glyph) >= 0;
        }

        // ------------------------------------------------------------------ GPOS lists

        private sealed class Lookup
        {
            public int Type;
            public int Flag;
            public int MarkFilteringSet = -1;
            public List<int> Subtables = new List<int>();
        }

        /// <summary>For every script, the <c>mark</c> and <c>mkmk</c>, the <c>curs</c> and the <c>kern</c> lookup indices of its default language system, in lookup order.</summary>
        /// <remarks>
        /// Bounded by <see cref="PdfLimits.MaxOpenTypeScriptSteps"/>: the script, feature-index and
        /// lookup-index counts are three 16-bit fields the font chooses and the loops below are their
        /// product. The scripts read before the budget runs out are kept; the one being read, and the
        /// rest, are not.
        /// </remarks>
        private void ReadScripts(Bounded r, int gpos, int length)
        {
            int scripts = r.Sub(gpos, length, 4);
            int features = r.Sub(gpos, length, 6);
            if (scripts < 0 || features < 0) return;
            int scriptCount = r.U16(scripts);
            int featureCount = r.U16(features);
            if (scriptCount < 0 || featureCount < 0) return;
            int budget = PdfLimits.MaxOpenTypeScriptSteps;

            for (int s = 0; s < scriptCount; s++)
            {
                if (--budget < 0) return;
                int rec = scripts + 2 + 6 * s;
                string tag = r.Tag(rec);
                int script = r.Offset(scripts, rec + 4);
                if (tag == null || script < 0 || _byScript.ContainsKey(tag) || _kernByScript.ContainsKey(tag)
                    || _distanceByScript.ContainsKey(tag) || _cursiveByScript.ContainsKey(tag) || _contextualByScript.ContainsKey(tag)
                    || _joiningByScript.ContainsKey(tag) || _requiredLigaturesByScript.ContainsKey(tag)
                    || _ligaturesByScript.ContainsKey(tag) || _discretionaryByScript.ContainsKey(tag)
                    || _historicalByScript.ContainsKey(tag)) continue;
                int langSys = r.Offset(script, script);
                if (langSys < 0) continue;

                var indices = new SortedSet<int>();
                var kernIndices = new SortedSet<int>();
                var distanceIndices = new SortedSet<int>();
                var cursiveIndices = new SortedSet<int>();
                var contextualIndices = new SortedSet<int>();
                var requiredLigatureIndices = new SortedSet<int>();
                var ligatureIndices = new SortedSet<int>();
                var discretionaryIndices = new SortedSet<int>();
                var historicalIndices = new SortedSet<int>();
                var joiningIndices = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
                int featureIndexCount = r.U16(langSys + 4);
                for (int f = 0; f < featureIndexCount && f < featureCount; f++)
                {
                    if (--budget < 0) return;
                    int fi = r.U16(langSys + 6 + 2 * f);
                    if (fi < 0 || fi >= featureCount) continue;
                    int frec = features + 2 + 6 * fi;
                    string ftag = r.Tag(frec);
                    bool joining = _substitution && Array.IndexOf(JoiningFeatures, ftag) >= 0;
                    // Positioning: mark, mkmk and the Indic abvm/blwm attach marks; dist is the pair
                    // adjustment a shaper applies unasked (Indic and Khmer spacing), kern the one it
                    // applies when asked; curs joins. The rest of the registry positions nothing here.
                    if (!joining && (_substitution ? ftag != "rclt" && ftag != "calt" && ftag != "rlig" && ftag != "liga"
                                                     && ftag != "dlig" && ftag != "hlig"
                                                   : ftag != "mark" && ftag != "mkmk" && ftag != "abvm" && ftag != "blwm"
                                                     && ftag != "dist" && ftag != "kern" && ftag != "curs")) continue;
                    if (joining && !joiningIndices.ContainsKey(ftag)) joiningIndices[ftag] = new SortedSet<int>();
                    var into = joining ? joiningIndices[ftag] : ftag == "kern" ? kernIndices : ftag == "dist" ? distanceIndices : ftag == "curs" ? cursiveIndices
                             : ftag == "calt" ? contextualIndices : ftag == "rlig" ? requiredLigatureIndices
                             : ftag == "liga" ? ligatureIndices : ftag == "dlig" ? discretionaryIndices
                             : ftag == "hlig" ? historicalIndices : indices;
                    int feature = r.Offset(features, frec + 4);
                    if (feature < 0) continue;
                    int n = r.U16(feature + 2);
                    for (int k = 0; k < n; k++)
                    {
                        if (--budget < 0) return;
                        int li = r.U16(feature + 4 + 2 * k);
                        if (li >= 0 && li < _lookupCount && LookupAt(li) != null) into.Add(li);
                    }
                }
                if (indices.Count > 0) _byScript[tag] = new List<int>(indices).ToArray();
                if (kernIndices.Count > 0) _kernByScript[tag] = new List<int>(kernIndices).ToArray();
                if (distanceIndices.Count > 0) _distanceByScript[tag] = new List<int>(distanceIndices).ToArray();
                if (cursiveIndices.Count > 0) _cursiveByScript[tag] = new List<int>(cursiveIndices).ToArray();
                if (contextualIndices.Count > 0) _contextualByScript[tag] = new List<int>(contextualIndices).ToArray();
                if (requiredLigatureIndices.Count > 0) _requiredLigaturesByScript[tag] = new List<int>(requiredLigatureIndices).ToArray();
                if (ligatureIndices.Count > 0) _ligaturesByScript[tag] = new List<int>(ligatureIndices).ToArray();
                if (discretionaryIndices.Count > 0) _discretionaryByScript[tag] = new List<int>(discretionaryIndices).ToArray();
                if (historicalIndices.Count > 0) _historicalByScript[tag] = new List<int>(historicalIndices).ToArray();
                var joiningByFeature = new Dictionary<string, int[]>(StringComparer.Ordinal);
                foreach (var kv in joiningIndices)
                    if (kv.Value.Count > 0) joiningByFeature[kv.Key] = new List<int>(kv.Value).ToArray();
                if (joiningByFeature.Count > 0) _joiningByScript[tag] = joiningByFeature;
            }
        }

        /// <summary>Lookup <paramref name="index"/>, read on first use: its type after extensions, its flag, and the subtables of a format this reads; <c>null</c> for none.</summary>
        private Lookup LookupAt(int index)
        {
            if (index < 0 || index >= _lookupCount) return null;
            if (_lookups.TryGetValue(index, out Lookup cached)) return cached;
            _lookups[index] = null;                       // a lookup reached again while being read is none

            var r = new Bounded(_d);
            Lookup result = null;
            int lookup = r.Offset(_lookupList, _lookupList + 2 + 2 * index);
            int count = lookup < 0 ? -1 : r.U16(lookup + 4);
            if (count > 0)
            {
                int type = r.U16(lookup);
                result = new Lookup { Type = type, Flag = Math.Max(0, r.U16(lookup + 2)) };
                if ((result.Flag & UseMarkFilteringSet) != 0) result.MarkFilteringSet = r.U16(lookup + 6 + 2 * count);
                for (int i = 0; i < count; i++)
                {
                    int sub = r.Offset(lookup, lookup + 6 + 2 * i);
                    if (sub < 0) continue;
                    int subType = type;
                    if (type == _extensionType)
                    {
                        if (r.U16(sub) != 1) continue;
                        subType = r.U16(sub + 2);
                        long target = (long)sub + r.U32(sub + 4);
                        if (subType == _extensionType || target <= sub || target >= r.Length) continue;
                        sub = (int)target;
                    }
                    int format = r.U16(sub);
                    bool contextual = (subType == _contextType || subType == _chainContextType) && format >= 1 && format <= 3;
                    bool readable = _substitution
                        ? subType == SubstSingle && (format == 1 || format == 2) || subType == SubstLigature && format == 1 || contextual
                        : (subType == LookupSingle || subType == LookupPair) && (format == 1 || format == 2)
                          || (subType == LookupCursive || subType == LookupMarkToBase || subType == LookupMarkToLigature || subType == LookupMarkToMark) && format == 1
                          || contextual;
                    if (!readable) continue;
                    result.Type = subType;
                    result.Subtables.Add(sub);
                }
                if (result.Subtables.Count == 0) result = null;
            }
            _lookups[index] = result;
            return result;
        }

        // ------------------------------------------------------------------ application

        /// <summary>Applies <paramref name="lookup"/> at glyph <paramref name="i"/>: its first subtable that applies.</summary>
        private bool ApplyAt(Lookup lookup, int i, Run run, int depth)
        {
            var r = new Bounded(_d);
            foreach (int sub in lookup.Subtables)
            {
                if (++run.Steps > MaxSteps) return false;
                bool applied = _substitution ? lookup.Type switch
                {
                    SubstSingle => ApplySingleSubstitution(r, sub, i, run),
                    SubstLigature => ApplyLigatureSubstitution(r, sub, lookup, i, run),
                    SubstContext => ApplyContext(r, sub, lookup, i, run, depth),
                    SubstChainContext => ApplyChainContext(r, sub, lookup, i, run, depth),
                    _ => false,
                }
                : lookup.Type switch
                {
                    LookupSingle => ApplySingle(r, sub, i, run),
                    LookupPair => ApplyPair(r, sub, lookup, i, run),
                    LookupCursive => ApplyCursive(r, sub, lookup, i, run),
                    LookupMarkToBase => ApplyMarkToBase(r, sub, i, run),
                    LookupMarkToLigature => ApplyMarkToLigature(r, sub, i, run),
                    LookupMarkToMark => ApplyMarkToMark(r, sub, lookup, i, run),
                    LookupContext => ApplyContext(r, sub, lookup, i, run, depth),
                    LookupChainContext => ApplyChainContext(r, sub, lookup, i, run, depth),
                    _ => false,
                };
                if (applied) return true;
            }
            return false;
        }

        /// <summary>
        /// A single substitution (<c>GSUB</c> type 1): glyph <paramref name="i"/>, when covered, becomes
        /// itself plus a delta (format 1, modulo 65536) or the substitute its coverage index names
        /// (format 2). Its <c>GDEF</c> class is the new glyph's from then on.
        /// </summary>
        private bool ApplySingleSubstitution(Bounded r, int sub, int i, Run run)
        {
            int format = r.U16(sub);
            int index = CoverageIndex(r, r.Offset(sub, sub + 2), run.Glyphs[i]);
            if (index < 0) return false;
            int substitute;
            if (format == 1) substitute = (run.Glyphs[i] + r.I16(sub + 4)) & 0xFFFF;
            else
            {
                if (index >= r.U16(sub + 4)) return false;
                substitute = r.U16(sub + 6 + 2 * index);
                if (substitute < 0) return false;
            }
            run.Glyphs[i] = (ushort)substitute;
            run.Classes[i] = GlyphClass(run.Glyphs[i]);
            return true;
        }

        /// <summary>
        /// A ligature substitution (<c>GSUB</c> type 4, format 1): glyph <paramref name="i"/>, when
        /// covered, followed — over the glyphs <paramref name="lookup"/> skips — by a ligature's other
        /// components becomes the ligature glyph, and the components are removed from the run
        /// (<see cref="Run.Removed"/>); the first ligature of its set that matches wins, as a font
        /// orders them longest first. The glyphs skipped between the components stay where they are,
        /// after the ligature in the run's order, and the ligature's sources (<see cref="Run.Sources"/>)
        /// gather the components' in run order.
        /// </summary>
        private bool ApplyLigatureSubstitution(Bounded r, int sub, Lookup lookup, int i, Run run)
        {
            int index = CoverageIndex(r, r.Offset(sub, sub + 2), run.Glyphs[i]);
            if (index < 0) return false;
            int setCount = r.U16(sub + 4);
            if (index >= setCount) return false;
            int set = r.Offset(sub, sub + 6 + 2 * index);
            if (set < 0) return false;
            int ligatureCount = r.U16(set);
            if (ligatureCount < 0) return false;

            var matched = new List<int>();
            for (int l = 0; l < ligatureCount; l++)
            {
                if (++run.Steps > MaxSteps) return false;
                int ligature = r.Offset(set, set + 2 + 2 * l);
                if (ligature < 0) continue;
                int ligatureGlyph = r.U16(ligature), componentCount = r.U16(ligature + 2);
                if (ligatureGlyph < 0 || componentCount < 1) continue;

                // A component count past the table's end reads -1 for the components beyond it, which
                // no glyph is, and the walk ends at the run's end: bounded by both without a check.
                matched.Clear();
                int at = i;
                bool matches = true;
                for (int c = 1; c < componentCount && matches; c++)
                {
                    at = Next(run, lookup, at);
                    matches = at >= 0 && run.Glyphs[at] == r.U16(ligature + 4 + 2 * (c - 1));
                    if (matches) matched.Add(at);
                }
                if (!matches) continue;

                run.Glyphs[i] = (ushort)ligatureGlyph;
                run.Classes[i] = GlyphClass(run.Glyphs[i]);
                foreach (int component in matched) run.Remove(component, into: i);
                return true;
            }
            return false;
        }

        private static bool ApplySingle(Bounded r, int sub, int i, Run run)
        {
            int format = r.U16(sub);
            int index = CoverageIndex(r, r.Offset(sub, sub + 2), run.Glyphs[i]);
            if (index < 0) return false;
            int valueFormat = r.U16(sub + 4);
            if (valueFormat < 0) return false;
            int value;
            if (format == 1) value = sub + 6;
            else
            {
                if (index >= r.U16(sub + 6)) return false;
                value = sub + 8 + index * ValueRecordSize(valueFormat);
            }
            return ApplyValue(r, value, valueFormat, i, run);
        }

        /// <summary>A value record's placement and advance, added to glyph <paramref name="i"/>; false when it lies outside the font.</summary>
        private static bool ApplyValue(Bounded r, int value, int valueFormat, int i, Run run)
        {
            if (!r.InRange(value, ValueRecordSize(valueFormat))) return false;
            int o = value;
            if ((valueFormat & 0x1) != 0) { run.Positions.XPlacement[i] += r.I16(o); o += 2; }
            if ((valueFormat & 0x2) != 0) { run.Positions.YPlacement[i] += r.I16(o); o += 2; }
            if ((valueFormat & 0x4) != 0) { run.Positions.XAdvance[i] += r.I16(o); }
            return true;
        }

        /// <summary>
        /// A pair adjustment: glyph <paramref name="i"/> and the next glyph the lookup does not skip,
        /// by glyph pair (format 1, its pair set searched for the second glyph) or by class pair
        /// (format 2); the first value record goes to the first glyph, the second to the second.
        /// </summary>
        private bool ApplyPair(Bounded r, int sub, Lookup lookup, int i, Run run)
        {
            int j = Next(run, lookup, i);
            if (j < 0) return false;
            int format = r.U16(sub);
            int index = CoverageIndex(r, r.Offset(sub, sub + 2), run.Glyphs[i]);
            if (index < 0) return false;
            int format1 = r.U16(sub + 4), format2 = r.U16(sub + 6);
            if (format1 < 0 || format2 < 0) return false;
            int size1 = ValueRecordSize(format1), size2 = ValueRecordSize(format2);

            if (format == 1)
            {
                if (index >= r.U16(sub + 8)) return false;
                int set = r.Offset(sub, sub + 10 + 2 * index);
                if (set < 0) return false;
                int count = r.U16(set), stride = 2 + size1 + size2;
                int lo = 0, hi = count - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1, rec = set + 2 + stride * mid;
                    int second = r.U16(rec);
                    if (second < 0) return false;
                    if (run.Glyphs[j] < second) hi = mid - 1;
                    else if (run.Glyphs[j] > second) lo = mid + 1;
                    else
                    {
                        ApplyValue(r, rec + 2, format1, i, run);
                        ApplyValue(r, rec + 2 + size1, format2, j, run);
                        return true;
                    }
                }
                return false;
            }
            if (format == 2)
            {
                int class1 = ClassOf(r, r.Offset(sub, sub + 8), run.Glyphs[i]);
                int class2 = ClassOf(r, r.Offset(sub, sub + 10), run.Glyphs[j]);
                int class1Count = r.U16(sub + 12), class2Count = r.U16(sub + 14);
                if (class1 >= class1Count || class2 >= class2Count || class1Count <= 0 || class2Count <= 0) return false;
                int rec = sub + 16 + (class1 * class2Count + class2) * (size1 + size2);
                ApplyValue(r, rec, format1, i, run);
                ApplyValue(r, rec + size1, format2, j, run);
                return true;
            }
            return false;
        }

        /// <summary>
        /// A cursive attachment (format 1): glyph <paramref name="j"/>'s entry anchor joined to the exit
        /// anchor of the glyph before it the lookup does not skip, both from the subtable's one
        /// coverage and its entry-exit records. Records the join, and hangs one glyph from the other
        /// (see the class remarks) by the difference of the anchors' heights.
        /// </summary>
        private bool ApplyCursive(Bounded r, int sub, Lookup lookup, int j, Run run)
        {
            int coverage = r.Offset(sub, sub + 2);
            int count = r.U16(sub + 4);
            int jIndex = CoverageIndex(r, coverage, run.Glyphs[j]);
            if (jIndex < 0 || jIndex >= count) return false;
            int entry = r.Offset(sub, sub + 6 + 4 * jIndex);
            if (entry < 0) return false;
            int i = Previous(run, lookup, j);
            if (i < 0) return false;
            int iIndex = CoverageIndex(r, coverage, run.Glyphs[i]);
            if (iIndex < 0 || iIndex >= count) return false;
            int exit = r.Offset(sub, sub + 6 + 4 * iIndex + 2);
            if (exit < 0) return false;
            if (!TryAnchor(r, entry, out int entryX, out int entryY) || !TryAnchor(r, exit, out int exitX, out int exitY))
                return false;

            Positions p = run.Positions;
            p.JoinedTo[i] = j;
            p.ExitX[i] = exitX;
            p.EntryX[j] = entryX;
            int child = (lookup.Flag & RightToLeft) != 0 ? i : j, parent = child == i ? j : i;
            if (p.CursiveParent[child] < 0)
            {
                p.CursiveParent[child] = parent;
                p.CursiveY[child] = child == i ? entryY - exitY : exitY - entryY;
            }
            return true;
        }

        /// <summary>A value record's size: two bytes per field its format names (device offsets included).</summary>
        private static int ValueRecordSize(int valueFormat)
        {
            int n = 0;
            for (int bit = valueFormat & 0xFF; bit != 0; bit >>= 1) n += bit & 1;
            return 2 * n;
        }

        /// <summary>A mark attaches to the nearest glyph before it that is not a mark, as a shaper's base search skips marks whatever the lookup's flags.</summary>
        private static bool ApplyMarkToBase(Bounded r, int sub, int i, Run run)
        {
            if (run.Classes[i] != MarkGlyphClass) return false;
            int j = i - 1;
            while (j >= 0 && run.Classes[j] == MarkGlyphClass) j--;
            return j >= 0 && Attach(r, sub, j, i, run);
        }

        /// <summary>
        /// A mark attaches to the ligature before it — the nearest glyph that is not a mark, as for a
        /// base — at its <b>last</b> component's anchor for the mark's class: the subtable's mark
        /// coverage and array as a mark-to-base one's, then a ligature coverage and an array of
        /// ligature attach tables, each a component count and, per component, an anchor per class.
        /// </summary>
        /// <remarks>
        /// A shaper attaches a mark to the component it followed in the text; a mark typed after the
        /// whole ligature belongs to its last. Producers form only the lam-alef ligature and never with
        /// a mark between lam and alef (<c>Core.Text.ArabicLigatures</c>), so every mark that reaches a
        /// ligature here followed the alef — the last component.
        /// </remarks>
        private static bool ApplyMarkToLigature(Bounded r, int sub, int i, Run run)
        {
            if (run.Classes[i] != MarkGlyphClass) return false;
            int j = i - 1;
            while (j >= 0 && run.Classes[j] == MarkGlyphClass) j--;
            if (j < 0) return false;

            int markCoverage = r.Offset(sub, sub + 2);
            int ligatureCoverage = r.Offset(sub, sub + 4);
            int classCount = r.U16(sub + 6);
            int markArray = r.Offset(sub, sub + 8);
            int ligatureArray = r.Offset(sub, sub + 10);
            if (markCoverage < 0 || ligatureCoverage < 0 || classCount <= 0 || markArray < 0 || ligatureArray < 0) return false;

            int markIndex = CoverageIndex(r, markCoverage, run.Glyphs[i]);
            int ligatureIndex = CoverageIndex(r, ligatureCoverage, run.Glyphs[j]);
            if (markIndex < 0 || ligatureIndex < 0 || ligatureIndex >= r.U16(ligatureArray)) return false;
            int attach = r.Offset(ligatureArray, ligatureArray + 2 + 2 * ligatureIndex);
            int components = r.U16(attach);
            if (attach < 0 || components <= 0) return false;
            int last = components - 1;
            return AttachByClass(r, markArray, markIndex, classCount,
                                 markClass => r.Offset(attach, attach + 2 + 2 * (last * classCount + markClass)), j, i, run);
        }

        /// <summary>A mark attaches to the glyph before it the lookup does not skip, which must be a mark.</summary>
        private bool ApplyMarkToMark(Bounded r, int sub, Lookup lookup, int i, Run run)
        {
            if (run.Classes[i] != MarkGlyphClass) return false;
            int j = Previous(run, lookup, i);
            return j >= 0 && run.Classes[j] == MarkGlyphClass && Attach(r, sub, j, i, run);
        }

        /// <summary>
        /// A format-1 mark-to-base or mark-to-mark subtable (their layouts are the same): the mark's
        /// class and anchor from the mark array, the other glyph's anchor for that class.
        /// </summary>
        private static bool Attach(Bounded r, int sub, int previous, int mark, Run run)
        {
            int markCoverage = r.Offset(sub, sub + 2);
            int baseCoverage = r.Offset(sub, sub + 4);
            int classCount = r.U16(sub + 6);
            int markArray = r.Offset(sub, sub + 8);
            int baseArray = r.Offset(sub, sub + 10);
            if (markCoverage < 0 || baseCoverage < 0 || classCount <= 0 || markArray < 0 || baseArray < 0) return false;

            int markIndex = CoverageIndex(r, markCoverage, run.Glyphs[mark]);
            if (markIndex < 0) return false;
            int baseIndex = CoverageIndex(r, baseCoverage, run.Glyphs[previous]);
            if (baseIndex < 0 || baseIndex >= r.U16(baseArray)) return false;
            return AttachByClass(r, markArray, markIndex, classCount,
                                 markClass => r.Offset(baseArray, baseArray + 2 + 2 * (baseIndex * classCount + markClass)), previous, mark, run);
        }

        /// <summary>
        /// The attachment every mark lookup ends in: the mark's class and anchor from the mark array,
        /// the other glyph's anchor for that class (<paramref name="anchorFor"/>), and the mark's origin
        /// placed so the two meet.
        /// </summary>
        private static bool AttachByClass(Bounded r, int markArray, int markIndex, int classCount,
                                          Func<int, int> anchorFor, int previous, int mark, Run run)
        {
            if (markIndex >= r.U16(markArray)) return false;
            int markRecord = markArray + 2 + 4 * markIndex;
            int markClass = r.U16(markRecord);
            if (markClass < 0 || markClass >= classCount) return false;
            int markAnchor = r.Offset(markArray, markRecord + 2);
            int baseAnchor = anchorFor(markClass);
            if (markAnchor < 0 || baseAnchor < 0) return false;    // a null base anchor: no attachment for this class
            if (!TryAnchor(r, markAnchor, out int mx, out int my) || !TryAnchor(r, baseAnchor, out int bx, out int by))
                return false;

            run.Positions.Parent[mark] = previous;
            run.Positions.AnchorX[mark] = bx - mx;
            run.Positions.AnchorY[mark] = by - my;
            return true;
        }

        /// <summary>
        /// A context subtable (lookup type 7): its rules matched at glyph <paramref name="i"/> — by
        /// glyph (format 1), by class (format 2) or by coverage (format 3) — over the glyphs the lookup
        /// does not skip; the first that matches applies its nested lookups.
        /// </summary>
        /// <remarks>A context rule is a chained one with no backtrack and no lookahead, and is matched
        /// by the same code (<see cref="MatchAndApply"/>).</remarks>
        private bool ApplyContext(Bounded r, int sub, Lookup lookup, int i, Run run, int depth)
        {
            if (depth >= MaxNesting) return false;
            ushort first = run.Glyphs[i];
            switch (r.U16(sub))
            {
                case 1:
                {
                    // Rule sets by the first glyph's coverage index; a rule: glyphCount, recordCount,
                    // the input glyphs after the first, the records.
                    int rules = RuleSet(r, sub, sub + 4, CoverageIndex(r, r.Offset(sub, sub + 2), first));
                    for (int k = 0, n = rules < 0 ? 0 : r.U16(rules); k < n; k++)
                    {
                        int rule = r.Offset(rules, rules + 2 + 2 * k);
                        int glyphCount = r.U16(rule), recordCount = r.U16(rule + 2);
                        if (rule < 0 || glyphCount <= 0 || recordCount < 0) continue;
                        int input = rule + 4;
                        if (MatchAndApply(r, lookup, i, run, depth, 0, null, glyphCount, (at, g) => r.U16(input + 2 * (at - 1)) == g,
                                          0, null, input + 2 * (glyphCount - 1), recordCount))
                            return true;
                    }
                    return false;
                }
                case 2:
                {
                    // Class sets by the first glyph's class (once it is covered); a rule as format 1's,
                    // with classes for glyphs.
                    if (CoverageIndex(r, r.Offset(sub, sub + 2), first) < 0) return false;
                    int classDef = r.Offset(sub, sub + 4);
                    int rules = RuleSet(r, sub, sub + 6, ClassOf(r, classDef, first));
                    for (int k = 0, n = rules < 0 ? 0 : r.U16(rules); k < n; k++)
                    {
                        int rule = r.Offset(rules, rules + 2 + 2 * k);
                        int glyphCount = r.U16(rule), recordCount = r.U16(rule + 2);
                        if (rule < 0 || glyphCount <= 0 || recordCount < 0) continue;
                        int input = rule + 4;
                        if (MatchAndApply(r, lookup, i, run, depth, 0, null, glyphCount,
                                          (at, g) => r.U16(input + 2 * (at - 1)) == ClassOf(r, classDef, g),
                                          0, null, input + 2 * (glyphCount - 1), recordCount))
                            return true;
                    }
                    return false;
                }
                case 3:
                {
                    // glyphCount, recordCount, one coverage per input glyph, the records.
                    int glyphCount = r.U16(sub + 2), recordCount = r.U16(sub + 4);
                    if (glyphCount <= 0 || recordCount < 0) return false;
                    int coverages = sub + 6;
                    return MatchAndApply(r, lookup, i, run, depth, 0, null, glyphCount,
                                         (at, g) => CoverageIndex(r, r.Offset(sub, coverages + 2 * at), g) >= 0,
                                         0, null, coverages + 2 * glyphCount, recordCount, firstChecked: false);
                }
            }
            return false;
        }

        /// <summary>
        /// A chained-context subtable (lookup type 8): as <see cref="ApplyContext"/>, with glyphs
        /// before the input (backtrack, read nearest first) and after it (lookahead) to match too —
        /// by glyph (format 1), by each sequence's own class definition (format 2) or by coverage
        /// (format 3).
        /// </summary>
        private bool ApplyChainContext(Bounded r, int sub, Lookup lookup, int i, Run run, int depth)
        {
            if (depth >= MaxNesting) return false;
            ushort first = run.Glyphs[i];
            int format = r.U16(sub);
            if (format == 1 || format == 2)
            {
                int coverage = r.Offset(sub, sub + 2);
                int backtrackClasses = -1, inputClasses = -1, lookaheadClasses = -1, rules;
                if (format == 1) rules = RuleSet(r, sub, sub + 4, CoverageIndex(r, coverage, first));
                else
                {
                    if (CoverageIndex(r, coverage, first) < 0) return false;
                    backtrackClasses = r.Offset(sub, sub + 4);
                    inputClasses = r.Offset(sub, sub + 6);
                    lookaheadClasses = r.Offset(sub, sub + 8);
                    rules = RuleSet(r, sub, sub + 10, ClassOf(r, inputClasses, first));
                }
                Func<int, int, ushort, bool> matches = format == 1
                    ? (Func<int, int, ushort, bool>)((at, classDef, g) => r.U16(at) == g)
                    : (at, classDef, g) => r.U16(at) == ClassOf(r, classDef, g);

                // A rule: backtrack count and values, input count and the values after the first,
                // lookahead count and values, record count and records.
                for (int k = 0, n = rules < 0 ? 0 : r.U16(rules); k < n; k++)
                {
                    int rule = r.Offset(rules, rules + 2 + 2 * k);
                    if (rule < 0) continue;
                    int backtrackCount = r.U16(rule);
                    if (backtrackCount < 0) continue;
                    int backtrack = rule + 2;
                    int o = backtrack + 2 * backtrackCount;
                    int inputCount = r.U16(o);
                    if (inputCount <= 0) continue;
                    int input = o + 2;
                    o = input + 2 * (inputCount - 1);
                    int lookaheadCount = r.U16(o);
                    if (lookaheadCount < 0) continue;
                    int lookahead = o + 2;
                    o = lookahead + 2 * lookaheadCount;
                    int recordCount = r.U16(o);
                    if (recordCount < 0) continue;
                    if (MatchAndApply(r, lookup, i, run, depth,
                                      backtrackCount, (at, g) => matches(backtrack + 2 * at, backtrackClasses, g),
                                      inputCount, (at, g) => matches(input + 2 * (at - 1), inputClasses, g),
                                      lookaheadCount, (at, g) => matches(lookahead + 2 * at, lookaheadClasses, g),
                                      o + 2, recordCount))
                        return true;
                }
                return false;
            }
            if (format == 3)
            {
                // One coverage per backtrack, input and lookahead glyph, then the records.
                int o = sub + 2;
                int backtrackCount = r.U16(o);
                if (backtrackCount < 0) return false;
                int backtrack = o + 2;
                o = backtrack + 2 * backtrackCount;
                int inputCount = r.U16(o);
                if (inputCount <= 0) return false;
                int input = o + 2;
                o = input + 2 * inputCount;
                int lookaheadCount = r.U16(o);
                if (lookaheadCount < 0) return false;
                int lookahead = o + 2;
                o = lookahead + 2 * lookaheadCount;
                int recordCount = r.U16(o);
                if (recordCount < 0) return false;
                return MatchAndApply(r, lookup, i, run, depth,
                                     backtrackCount, (at, g) => CoverageIndex(r, r.Offset(sub, backtrack + 2 * at), g) >= 0,
                                     inputCount, (at, g) => CoverageIndex(r, r.Offset(sub, input + 2 * at), g) >= 0,
                                     lookaheadCount, (at, g) => CoverageIndex(r, r.Offset(sub, lookahead + 2 * at), g) >= 0,
                                     o + 2, recordCount, firstChecked: false);
            }
            return false;
        }

        /// <summary>The rule set at index <paramref name="index"/> of the offset array whose count is at <paramref name="countAt"/>; -1 for none.</summary>
        private static int RuleSet(Bounded r, int sub, int countAt, int index)
        {
            if (index < 0 || index >= r.U16(countAt)) return -1;
            return r.Offset(sub, countAt + 2 + 2 * index);
        }

        /// <summary>
        /// Matches one rule at glyph <paramref name="i"/> over the glyphs <paramref name="lookup"/>
        /// does not skip — the input sequence forward from it (<paramref name="input"/> asked of each
        /// position, the first only when <paramref name="firstChecked"/> is false: a format 1 or 2
        /// rule's first glyph was chosen by its coverage), the backtrack backward from it nearest
        /// first, the lookahead forward from the input's last glyph — and where it matches applies
        /// each of its <paramref name="recordCount"/> sequence lookup records (sequence index, lookup
        /// index) at its input glyph.
        /// </summary>
        private bool MatchAndApply(Bounded r, Lookup lookup, int i, Run run, int depth,
                                   int backtrackCount, Func<int, ushort, bool> backtrack,
                                   int inputCount, Func<int, ushort, bool> input,
                                   int lookaheadCount, Func<int, ushort, bool> lookahead,
                                   int records, int recordCount, bool firstChecked = true)
        {
            if (!r.InRange(records, 4 * recordCount)) return false;
            var positions = new int[inputCount];
            int at = i;
            for (int k = 0; k < inputCount; k++)
            {
                if (k > 0) at = Next(run, lookup, at);
                if (at < 0) return false;
                if ((k > 0 || !firstChecked) && !input(k, run.Glyphs[at])) return false;
                positions[k] = at;
            }
            int back = i;
            for (int k = 0; k < backtrackCount; k++)
            {
                back = Previous(run, lookup, back);
                if (back < 0 || !backtrack(k, run.Glyphs[back])) return false;
            }
            int ahead = positions[inputCount - 1];
            for (int k = 0; k < lookaheadCount; k++)
            {
                ahead = Next(run, lookup, ahead);
                if (ahead < 0 || !lookahead(k, run.Glyphs[ahead])) return false;
            }

            for (int k = 0; k < recordCount; k++)
            {
                int sequence = r.U16(records + 4 * k);
                Lookup nested = LookupAt(r.U16(records + 2 + 4 * k));
                if (nested == null || sequence < 0 || sequence >= inputCount) continue;
                int target = positions[sequence];
                if (!SkipsAt(nested, run, target)) ApplyAt(nested, target, run, depth + 1);
            }
            return true;
        }

        private int Next(Run run, Lookup lookup, int from)
        {
            for (int j = from + 1; j < run.Glyphs.Length; j++)
                if (!SkipsAt(lookup, run, j)) return j;
            return -1;
        }

        private int Previous(Run run, Lookup lookup, int from)
        {
            for (int j = from - 1; j >= 0; j--)
                if (!SkipsAt(lookup, run, j)) return j;
            return -1;
        }

        // ------------------------------------------------------------------ tables

        /// <summary>An anchor's design-unit coordinates (formats 1–3 share them; the device and contour-point refinements are not read).</summary>
        private static bool TryAnchor(Bounded r, int anchor, out int x, out int y)
        {
            int format = r.U16(anchor);
            x = r.I16(anchor + 2);
            y = r.I16(anchor + 4);
            return format >= 1 && format <= 3 && r.InRange(anchor, 6);
        }

        /// <summary>The coverage index of <paramref name="glyph"/>, or -1.</summary>
        private static int CoverageIndex(Bounded r, int coverage, ushort glyph)
        {
            if (coverage < 0) return -1;
            int format = r.U16(coverage);
            int count = r.U16(coverage + 2);
            if (count < 0) return -1;
            int lo = 0, hi = count - 1;
            if (format == 1)
            {
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    int g = r.U16(coverage + 4 + 2 * mid);
                    if (g < 0) return -1;
                    if (glyph < g) hi = mid - 1;
                    else if (glyph > g) lo = mid + 1;
                    else return mid;
                }
            }
            else if (format == 2)
            {
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1, rec = coverage + 4 + 6 * mid;
                    int first = r.U16(rec), last = r.U16(rec + 2);
                    if (first < 0 || last < 0) return -1;
                    if (glyph < first) hi = mid - 1;
                    else if (glyph > last) lo = mid + 1;
                    else
                    {
                        int start = r.U16(rec + 4);
                        return start < 0 ? -1 : start + glyph - first;
                    }
                }
            }
            return -1;
        }

        /// <summary>Big-endian reads that answer -1 (or 0 for a signed value) outside the font rather than throwing.</summary>
        private readonly struct Bounded
        {
            private readonly byte[] _d;
            public Bounded(byte[] d) { _d = d; }
            public int Length => _d.Length;
            public bool InRange(int o, int n) => o >= 0 && n >= 0 && o <= _d.Length - n;
            public int U16(int o) => InRange(o, 2) ? (_d[o] << 8) | _d[o + 1] : -1;
            public int I16(int o) => InRange(o, 2) ? (short)((_d[o] << 8) | _d[o + 1]) : 0;
            public uint U32(int o) => InRange(o, 4) ? ((uint)_d[o] << 24) | ((uint)_d[o + 1] << 16) | ((uint)_d[o + 2] << 8) | _d[o + 3] : 0;
            public string Tag(int o) => InRange(o, 4) ? new string(new[] { (char)_d[o], (char)_d[o + 1], (char)_d[o + 2], (char)_d[o + 3] }) : null;

            /// <summary>The 16-bit offset at <paramref name="at"/>, from <paramref name="from"/>; -1 when null or outside the font.</summary>
            public int Offset(int from, int at)
            {
                int v = U16(at);
                if (v <= 0 || from < 0) return -1;
                int o = from + v;
                return o < _d.Length ? o : -1;
            }

            /// <summary>A sub-table's offset in a table of <paramref name="length"/> bytes at <paramref name="table"/>; -1 when null or outside it.</summary>
            public int Sub(int table, int length, int at)
            {
                int v = U16(table + at);
                return v > 0 && v < length ? table + v : -1;
            }
        }
    }
}
