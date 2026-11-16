// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal static class ListRenderer
    {
        /// <summary>The longest list marker text (<c>w:lvlText</c>) laid out.</summary>
        internal const int MaxListMarkerChars = 256;

        /// <summary>
        /// Set when a numbered list could not be resolved and a bullet fallback was
        /// used (missing numbering part / instance / abstract definition / level).
        /// Follows the project's diagnostics pattern. Null when the last call
        /// resolved cleanly.
        /// </summary>
        internal static string LastError
        {
            get => _lastError;
            private set
            {
                _lastError = value;
                // Forwarded so one conversion has one place to read; see TableRenderer.LastError.
                // The reset at the top of Resolve passes null and is deliberately not reported.
                if (value != null) DocxDiagnostics.Report("ListRenderer", value);
            }
        }

        [ThreadStatic]
        private static string _lastError;

        // Counter dictionary is passed in from DocxRenderContext (per-document, not
        // static) so concurrent conversions cannot interfere with each other.
        internal static string GetListPrefix(Paragraph paragraph, DocxRenderContext context)
            => GetListPrefix(paragraph, context.WordDocument, context.ListCounters, context.StyleResolver);

        /// <summary>
        /// The numbering a paragraph takes: through <paramref name="resolver"/> when there is one (its own
        /// <c>w:numPr</c>, else its paragraph style's), its own alone otherwise.
        /// </summary>
        private static NumberingProperties NumberingPropertiesOf(Paragraph paragraph, StyleResolver resolver)
            => resolver != null ? resolver.ResolveNumbering(paragraph) : paragraph?.ParagraphProperties?.NumberingProperties;

        /// <summary>
        /// The marker <paramref name="paragraph"/>'s numbering asks for, without a render context.
        /// </summary>
        /// <remarks>
        /// <b>The two output paths must read <c>numbering.xml</c> the same way.</b> The rendering
        /// path had this; the Builder-model path drew a hard-coded bullet for every numbered
        /// paragraph, so an ordered DOCX list converted to a bulleted one through it. The context
        /// was never more than the document and the counters, so the split is a parameter list
        /// rather than a second implementation.
        /// </remarks>
        /// <param name="paragraph">The paragraph whose numbering to resolve.</param>
        /// <param name="wordDocument">The document holding the numbering definitions part.</param>
        /// <param name="listCounters">
        /// The per-conversion counter state, keyed by numbering id and level. A caller that passes
        /// a fresh dictionary restarts every sequence, which is right for one document and wrong
        /// for a paragraph inside one — so it belongs to the caller, not to this method.
        /// </param>
        /// <param name="resolver">The style resolver, for a numbering a paragraph style gives the paragraph; null reads the paragraph's own alone.</param>
        internal static string GetListPrefix(
            Paragraph paragraph,
            WordprocessingDocument wordDocument,
            Dictionary<int, int> listCounters,
            StyleResolver resolver = null)
        {
            LastError = null;
            var numProps = NumberingPropertiesOf(paragraph, resolver);
            if (numProps == null) return null;

            var numIdVal = numProps.NumberingId?.Val?.Value;
            int ilvl = numProps.NumberingLevelReference?.Val?.Value ?? 0;
            if (numIdVal == null || numIdVal == 0) return null;
            int numId = numIdVal.Value;

            var numberingPart = wordDocument.MainDocumentPart?.NumberingDefinitionsPart;
            if (numberingPart?.Numbering == null)
            {
                LastError = "No NumberingDefinitionsPart; using bullet fallback.";
                return "• ";
            }
            var numbering = numberingPart.Numbering;

            var index = IndexOf(numbering);
            index.Instances.TryGetValue(numId, out var numInstance);
            if (numInstance == null)
            {
                LastError = $"NumberingInstance {numId} not found; using bullet fallback.";
                return "• ";
            }

            var abstractNumId = numInstance.AbstractNumId?.Val?.Value;
            AbstractNum abstractNum = null;
            if (abstractNumId != null) index.Abstracts.TryGetValue(abstractNumId.Value, out abstractNum);
            if (abstractNum == null)
            {
                LastError = $"AbstractNum for numId {numId} not found; using bullet fallback.";
                return "• ";
            }

            var levels = index.LevelsOf(abstractNum);

            if (!levels.TryGetValue(ilvl, out var level))
            {
                LastError = $"Level {ilvl} not found in abstractNum; using bullet fallback.";
                return "• ";
            }

            // A level with no format, or a bullet, draws the bullet; a level of format "none" draws no
            // number (§17.18.59) — its level text, when it has any, with nothing substituted.
            var numFmt = level.NumberingFormat?.Val?.Value;
            if (numFmt == null || numFmt == NumberFormatValues.Bullet)
                return "• ";

            // Advance this level's counter (initialising from its start value), then
            // reset all deeper levels so a sub-list restarts the next time it appears.
            int key = numId * 100 + ilvl;
            int start = ResolveStart(numInstance, level, ilvl);
            int counter = listCounters.TryGetValue(key, out int c) ? c + 1 : start;
            listCounters[key] = counter;
            for (int k = ilvl + 1; k <= 8; k++)
                listCounters.Remove(numId * 100 + k);

            var levelText = level.LevelText?.Val?.Value;
            if (string.IsNullOrEmpty(levelText))
                return numFmt == NumberFormatValues.None ? null : FormatCounter(counter, numFmt.Value) + ". ";

            // w:isLgl (§17.9.9): the levels before this one show in decimal whatever their own format.
            bool legal = level.IsLegalNumberingStyle is IsLegalNumberingStyle lgl && (lgl.Val == null || lgl.Val.Value);

            // The marker is laid out for every numbered paragraph, so its length multiplies by the
            // paragraph count — two counts the file chooses. Word's own marker text is a few
            // characters; past MaxListMarkerChars it is cut and the cut said.
            if (levelText.Length > MaxListMarkerChars)
            {
                LastError = $"A list level's marker text is {levelText.Length} characters; the first {MaxListMarkerChars} were used.";
                levelText = levelText.Substring(0, MaxListMarkerChars);
            }

            // Substitute %1..%(ilvl+1) with each ancestor level's current counter,
            // each formatted in that level's own numbering format ("1.1", "a.ii"…).
            string result = levelText;
            for (int k = ilvl; k >= 0; k--) // high→low so "%1" doesn't clobber "%1x"
            {
                if (!levels.TryGetValue(k, out var kLevel)) continue;
                int kKey = numId * 100 + k;
                int kValue = listCounters.TryGetValue(kKey, out int kv) ? kv : ResolveStart(numInstance, kLevel, k);
                var kFmt = legal && k < ilvl ? NumberFormatValues.Decimal : kLevel.NumberingFormat?.Val?.Value ?? NumberFormatValues.Decimal;
                result = result.Replace("%" + (k + 1), FormatCounter(kValue, kFmt));
            }
            return result + " ";
        }

        /// <summary>Whether <paramref name="paragraph"/>'s numbering level is a numbered one.</summary>
        /// <remarks>
        /// <b>The marker string cannot answer this.</b> A level whose text is "1." and one whose
        /// text is a literal "1." bullet character produce the same prefix, and only
        /// <c>w:numFmt</c> says which is a sequence — which is what an exporter needs to write
        /// <c>&lt;ol&gt;</c> rather than <c>&lt;ul&gt;</c>, or a <c>/L</c> a reader can count.
        /// </remarks>
        internal static bool IsOrderedLevel(Paragraph paragraph, WordprocessingDocument wordDocument, StyleResolver resolver = null)
        {
            var numProps = NumberingPropertiesOf(paragraph, resolver);
            var numIdVal = numProps?.NumberingId?.Val?.Value;
            if (numIdVal == null || numIdVal == 0) return false;

            int ilvl = numProps.NumberingLevelReference?.Val?.Value ?? 0;
            var numbering = wordDocument.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
            if (numbering == null) return false;

            var index = IndexOf(numbering);
            index.Instances.TryGetValue(numIdVal.Value, out var instance);
            var abstractNumId = instance?.AbstractNumId?.Val?.Value;
            AbstractNum abstractNum = null;
            if (abstractNumId != null) index.Abstracts.TryGetValue(abstractNumId.Value, out abstractNum);

            Level level = null;
            if (abstractNum != null) index.LevelsOf(abstractNum).TryGetValue(ilvl, out level);

            var fmt = level?.NumberingFormat?.Val?.Value;
            return fmt != null
                   && fmt != NumberFormatValues.Bullet
                   && fmt != NumberFormatValues.None;
        }

        /// <summary>The numbering id and level of a numbered paragraph, or null.</summary>
        /// <remarks>
        /// The id is what separates one list from the next: two adjacent numbered paragraphs with
        /// different ids are two lists, and grouping them into one would restart nothing and
        /// renumber everything.
        /// </remarks>
        internal static (int NumId, int Level)? NumberingOf(Paragraph paragraph, StyleResolver resolver = null)
        {
            var numProps = NumberingPropertiesOf(paragraph, resolver);
            var numIdVal = numProps?.NumberingId?.Val?.Value;
            if (numIdVal == null || numIdVal == 0) return null;

            return (numIdVal.Value, numProps.NumberingLevelReference?.Val?.Value ?? 0);
        }

        /// <summary>
        /// A numbering part's instances, abstract definitions and each definition's levels, by id, read once per
        /// document — the first of each id, as the lookups it replaces took the first.
        /// </summary>
        /// <remarks>
        /// Every list paragraph scanned the part's every instance and abstract definition and rebuilt its
        /// definition's levels, so a document's numbered paragraphs multiplied its numbering definitions — both
        /// the file's to choose, and both large in documents built by copying: 20 000 definitions under 2000
        /// numbered items would take 5 s on the legacy path and 10 s on the Builder path. Keyed by the
        /// part's root, which lives as long as its document; a conversion reads its document on one thread.
        /// </remarks>
        private sealed class NumberingIndex
        {
            internal readonly Dictionary<int, NumberingInstance> Instances = new Dictionary<int, NumberingInstance>();
            internal readonly Dictionary<int, AbstractNum> Abstracts = new Dictionary<int, AbstractNum>();
            private readonly Dictionary<AbstractNum, Dictionary<int, Level>> _levels = new Dictionary<AbstractNum, Dictionary<int, Level>>();

            internal NumberingIndex(Numbering numbering)
            {
                foreach (var instance in numbering.Elements<NumberingInstance>())
                    if (instance.NumberID?.Value is int id && !Instances.ContainsKey(id)) Instances[id] = instance;
                foreach (var definition in numbering.Elements<AbstractNum>())
                    if (definition.AbstractNumberId?.Value is int id && !Abstracts.ContainsKey(id)) Abstracts[id] = definition;
            }

            /// <summary>A definition's levels by index, the first of each.</summary>
            internal Dictionary<int, Level> LevelsOf(AbstractNum definition)
            {
                if (_levels.TryGetValue(definition, out var levels)) return levels;
                levels = new Dictionary<int, Level>();
                foreach (var level in definition.Elements<Level>())
                    if (level.LevelIndex?.Value is int index && !levels.ContainsKey(index)) levels[index] = level;
                _levels[definition] = levels;
                return levels;
            }

            /// <summary>How many indexes have been built — the read-once evidence.</summary>
            [ThreadStatic] internal static int Built;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Numbering, NumberingIndex> Indexes =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Numbering, NumberingIndex>();

        private static NumberingIndex IndexOf(Numbering numbering)
            => Indexes.GetValue(numbering, n => { NumberingIndex.Built++; return new NumberingIndex(n); });

        /// <summary>How many numbering indexes this thread has built — read once per document, the evidence.</summary>
        internal static int NumberingIndexesBuilt => NumberingIndex.Built;

        private static int ResolveStart(NumberingInstance instance, Level level, int ilvl)
        {
            var ovr = instance.Elements<LevelOverride>()
                .FirstOrDefault(o => o.LevelIndex?.Value == ilvl);
            var startOverride = ovr?.StartOverrideNumberingValue?.Val?.Value;
            if (startOverride != null) return startOverride.Value;
            return level.StartNumberingValue?.Val?.Value ?? 1;
        }

        private static string FormatCounter(int counter, NumberFormatValues fmt)
        {
            // §17.18.59: decimalZero pads a value below ten to two digits; none shows no number.
            if (fmt == NumberFormatValues.None) return "";
            if (fmt == NumberFormatValues.DecimalZero)
                return counter >= 0 && counter < 10 ? "0" + counter.ToString(CultureInfo.InvariantCulture) : counter.ToString(CultureInfo.InvariantCulture);
            if (fmt == NumberFormatValues.LowerLetter) return ToLetters(counter, upper: false);
            if (fmt == NumberFormatValues.UpperLetter) return ToLetters(counter, upper: true);
            if (fmt == NumberFormatValues.LowerRoman) return ToRoman(counter).ToLowerInvariant();
            if (fmt == NumberFormatValues.UpperRoman) return ToRoman(counter);
            // Hebrew letters (gematria) and the Hebrew alphabet, ECMA-376 §17.18.59 — one
            // implementation of each, in Core.Util.OrdinalNumbering, shared with the page numbers.
            if (fmt == NumberFormatValues.Hebrew1) return Core.Util.OrdinalNumbering.ToHebrewNumerals(counter);
            if (fmt == NumberFormatValues.Hebrew2) return Core.Util.OrdinalNumbering.ToHebrewAlphabet(counter);
            return counter.ToString(CultureInfo.InvariantCulture);
        }

        // Bijective base-26: 1→a … 26→z, 27→aa, 28→ab, …
        // One implementation, in Core.Util.OrdinalNumbering.
        private static string ToLetters(int n, bool upper) => Core.Util.OrdinalNumbering.ToLetters(n, upper);

        // Roman numerals: one implementation, in Core.Util.OrdinalNumbering, shared with the page numbers.
        private static string ToRoman(int number) => Core.Util.OrdinalNumbering.ToRoman(number, upper: true);
    }
}
