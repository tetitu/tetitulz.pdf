// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    // The reflowing half of find-and-replace — GAPS.md §19d — sharing this engine's glyph walk,
    // erasure and font resolution, and differing in what it draws back.
    // Why this is in the same engine and not a second one. Locating text, deciding
    // whether a span can be edited, erasing it and embedding a font for what replaces it are the
    // same problems whether or not the paragraph is re-broken. A second copy of that walk is the
    // divergence this repository keeps finding defects in. What genuinely differs is one step —
    // the replacement is laid out over several lines instead of one — and that is what this file
    // holds.
    //
    // The contract is not this engine's, which is why the entry point is not either.
    // PdfTextReflow is the public face, with its own options and its own result,
    // because reflow gives up the guarantee that makes ReplaceText safe: text the
    // caller did not name does move. A caller has to choose that knowingly, and an overload
    // on the same class would let them arrive at it by autocomplete.
    public static partial class PdfTemplateEngine
    {
        /// <summary>
        /// The reflowing entry, reached through <see cref="PdfTextReflow"/> rather than offered
        /// here — the contract differs even though the machinery does not.
        /// </summary>
        internal static TemplateReplaceResult ReplaceReflowing(
            Stream input, Stream output,
            IReadOnlyDictionary<string, string> replacements,
            TemplateReplaceOptions options)
            => Run(input, output, replacements, options, MatchMode.Reflow);

        /// <summary>Baselines this far apart, relative to the leading, count as the same step.</summary>
        private const float LeadingTolerance = 0.15f;

        /// <summary>Left edges within this many points count as the same margin.</summary>
        private const float MarginTolerance = 0.75f;

        /// <summary>
        /// One line of a paragraph: the glyphs sharing a baseline inside one text object.
        /// </summary>
        private sealed class ReflowLine
        {
            internal int First;
            internal int Last;
            internal float BaselineY;
            internal float Left;
            internal float Width;
            internal string Text;
        }

        /// <summary>
        /// Turns each located span into a reflowed <b>paragraph</b>, or refuses it by name.
        /// </summary>
        /// <remarks>
        /// <para><b>What counts as a paragraph here is deliberately narrow</b>, and every part of
        /// the definition is a refusal a caller can read: one content stream, one text object, one
        /// font size, consecutive baselines a uniform distance apart, a common left margin, and no
        /// rotation or skew. Those together are "left-aligned prose set as a block", which is what
        /// the feature is for. Anything else — a table cell, a centred heading, a rotated stamp —
        /// is left alone and named, because re-breaking it would move text in a direction nobody
        /// asked about.</para>
        ///
        /// <para><b>Uniform leading is the load-bearing one.</b> It is what says the lines belong to
        /// one block rather than being two paragraphs that happen to be adjacent, and it is the
        /// value the new lines are laid out on. Without it the reflowed text would be evenly spaced
        /// where the original was not, which is a visible change to a paragraph the caller only
        /// asked to edit the words of.</para>
        /// </remarks>
        private static List<TokenMatch> MatchReflow(
            List<Glyph> glyphs, List<StreamWork> streams, IReadOnlyDictionary<string, string> values,
            TemplateReplaceOptions options, int pageIndex, TemplateReplaceResult result)
        {
            var matches = new List<TokenMatch>();
            List<TokenMatch> spans = MatchLiterals(glyphs, streams, values, pageIndex, result);
            if (spans.Count == 0) return matches;

            var claimed = new List<ReflowLine>();

            foreach (TokenMatch span in spans)
            {
                List<ReflowLine> paragraph = ParagraphAround(glyphs, span, pageIndex, result);
                if (paragraph == null) continue;

                // Two keys inside one paragraph would each rewrite it, and the second rewrite would
                // be computed from the text the first one replaced — so the paragraph is claimed
                // once and the second key is reported rather than silently dropped.
                if (Overlaps(claimed, paragraph))
                {
                    // Refuse, not merely warn: a key that was FOUND and declined must reach
                    // NotReflowedKeys, or it falls through to "not found" and sends the caller
                    // looking for a typo in a key that is spelled correctly.
                    Refuse(result, span, pageIndex,
                        "another key in the same paragraph was reflowed first, and reflowing twice " +
                        "would lay out the second replacement over text the first had already moved");
                    continue;
                }

                TokenMatch built = BuildReflowMatch(glyphs, paragraph, span, pageIndex, result);
                if (built == null) continue;

                claimed.AddRange(paragraph);
                matches.Add(built);
            }

            return matches;
        }

        private static bool Overlaps(List<ReflowLine> claimed, List<ReflowLine> paragraph)
        {
            foreach (ReflowLine a in claimed)
                foreach (ReflowLine b in paragraph)
                    if (a.First <= b.Last && b.First <= a.Last) return true;
            return false;
        }

        /// <summary>
        /// The paragraph containing <paramref name="span"/>, or null with a named refusal.
        /// </summary>
        private static List<ReflowLine> ParagraphAround(
            List<Glyph> glyphs, TokenMatch span, int pageIndex, TemplateReplaceResult result)
        {
            Glyph anchor = glyphs[span.First];

            if (Math.Abs(anchor.Trm[1]) > 1e-4f || Math.Abs(anchor.Trm[2]) > 1e-4f)
            {
                Refuse(result, span, pageIndex,
                    "the text is rotated or skewed, and a re-broken line would step in a direction " +
                    "this does not compute");
                return null;
            }

            List<ReflowLine> lines = LinesOf(glyphs, anchor.StreamId, anchor.TextObjectId, anchor.FontSize);
            int index = lines.FindIndex(l => l.First <= span.First && span.Last <= l.Last);
            if (index < 0)
            {
                Refuse(result, span, pageIndex,
                    "the match spans more than one line of its text object, so the paragraph it " +
                    "belongs to cannot be identified");
                return null;
            }

            if (lines.Count == 1)
            {
                Refuse(result, span, pageIndex,
                    "its text object holds a single line, so there is no block to re-break and no " +
                    "second line to read a leading or a margin from. PdfTemplateEngine.ReplaceText " +
                    "replaces text in one line correctly, whatever its alignment");
                return null;
            }

            // The leading is taken from the anchor's own neighbour and then required of every step,
            // rather than averaged: an average absorbs exactly the irregularity that says these are
            // two blocks rather than one.
            float leading = index > 0
                ? lines[index - 1].BaselineY - lines[index].BaselineY
                : lines[index].BaselineY - lines[index + 1].BaselineY;

            if (leading <= 0)
            {
                Refuse(result, span, pageIndex,
                    "the lines of this text object do not descend the page in order, so their " +
                    "leading cannot be read");
                return null;
            }

            int first = index, last = index;
            while (first > 0 && Continues(lines[first - 1], lines[first], leading)) first--;
            while (last + 1 < lines.Count && Continues(lines[last], lines[last + 1], leading)) last++;

            if (first == last)
            {
                // Neighbours exist and none of them continues this line, so it is not part of a
                // block: a centred or right-aligned paragraph looks exactly like this from the
                // file, its lines starting at different x. Re-breaking it as if it were
                // left-aligned would silently move every line to this one's margin.
                Refuse(result, span, pageIndex,
                    "the lines around it do not share its left margin or its leading, so it is not " +
                    "part of a block this can re-break — centred, right-aligned and justified text " +
                    "look like this, and re-breaking one would move every line to a new margin");
                return null;
            }

            return lines.GetRange(first, last - first + 1);
        }

        /// <summary>Whether <paramref name="below"/> continues the block <paramref name="above"/> starts.</summary>
        private static bool Continues(ReflowLine above, ReflowLine below, float leading)
        {
            float step = above.BaselineY - below.BaselineY;
            if (Math.Abs(step - leading) > leading * LeadingTolerance) return false;
            return Math.Abs(above.Left - below.Left) <= MarginTolerance;
        }

        /// <summary>
        /// The lines of one text object at one font size, top to bottom.
        /// </summary>
        private static List<ReflowLine> LinesOf(
            List<Glyph> glyphs, int streamId, int textObjectId, float fontSize)
        {
            var lines = new List<ReflowLine>();
            ReflowLine current = null;

            for (int g = 0; g < glyphs.Count; g++)
            {
                Glyph glyph = glyphs[g];
                if (glyph.StreamId != streamId || glyph.TextObjectId != textObjectId) continue;
                if (Math.Abs(glyph.FontSize - fontSize) > 1e-4f) continue;

                if (current != null && Math.Abs(glyph.BaselineY - current.BaselineY) <= SameBaselineTolerance)
                {
                    current.Last = g;
                    current.Width += glyph.AdvanceX;
                    continue;
                }

                current = new ReflowLine
                {
                    First = g,
                    Last = g,
                    BaselineY = glyph.BaselineY,
                    Left = glyph.Trm[4],
                    Width = glyph.AdvanceX,
                };
                lines.Add(current);
            }

            foreach (ReflowLine line in lines)
            {
                var sb = new StringBuilder();
                for (int g = line.First; g <= line.Last; g++) sb.Append(glyphs[g].Text);
                line.Text = sb.ToString();
            }

            return lines;
        }

        /// <summary>
        /// Applies the replacement to the paragraph's text and builds the match that erases the old
        /// lines and draws the new ones.
        /// </summary>
        /// <remarks>
        /// <b>The line breaking is not done here, and cannot be.</b> Breaking needs the width of a
        /// candidate line, which needs the font — and the font this engine embeds is resolved from
        /// the set of replacements, so it does not exist until every match is known.
        /// <see cref="BreakReflowedParagraphs"/> runs afterwards, once there is something to
        /// measure with.
        /// </remarks>
        private static TokenMatch BuildReflowMatch(
            List<Glyph> glyphs, List<ReflowLine> paragraph, TokenMatch span,
            int pageIndex, TemplateReplaceResult result)
        {
            // The lines are joined with a space: a PDF line carries no terminator, so two lines that
            // read as one sentence have nothing between them, and joining without one would weld
            // the last word of each line to the first of the next.
            var joined = new StringBuilder();
            foreach (ReflowLine line in paragraph)
            {
                if (joined.Length > 0) joined.Append(' ');
                joined.Append(line.Text);
            }

            string original = joined.ToString();
            string replaced = original.Replace(span.Key, span.Value ?? string.Empty);
            if (string.Equals(original, replaced, StringComparison.Ordinal))
            {
                Refuse(result, span, pageIndex,
                    "the key was located but does not appear in the paragraph's joined text, which " +
                    "happens when it straddles a line break");
                return null;
            }

            // The measure is the widest line the paragraph already had, which is the only statement
            // about available width a PDF actually makes: there is no margin in the file, only text
            // that reached a certain x. Taking the widest rather than the last is what keeps a short
            // final line from narrowing the whole paragraph.
            float measureWidth = 0;
            foreach (ReflowLine line in paragraph) measureWidth = Math.Max(measureWidth, line.Width);

            float leading = paragraph.Count > 1
                ? paragraph[0].BaselineY - paragraph[1].BaselineY
                : span.FontSize;

            // Justified: every line but the last ends at one right edge, as every line starts at one
            // left margin. Two such lines at least — a ragged paragraph whose first line happens to
            // be full would otherwise read as justified and have every new line stretched.
            bool justified = paragraph.Count >= 3;
            float right = paragraph[0].Left + paragraph[0].Width;
            for (int i = 1; justified && i < paragraph.Count - 1; i++)
                justified = Math.Abs(paragraph[i].Left + paragraph[i].Width - right) <= MarginTolerance;

            Glyph first = glyphs[paragraph[0].First];
            return new TokenMatch
            {
                Key = span.Key,
                Value = replaced,
                First = paragraph[0].First,
                Last = paragraph[paragraph.Count - 1].Last,
                Trm = first.Trm,
                Ctm = first.Ctm,
                SlotWidth = measureWidth,
                FontSize = first.FontSize,
                TextObjectId = first.TextObjectId,
                FillColorOps = first.FillColorOps,
                StreamId = first.StreamId,
                ReflowLeading = leading,
                ReflowLineCount = paragraph.Count,
                ReflowJustified = justified,
            };
        }

        /// <summary>
        /// Breaks every reflowed paragraph's text into lines, now that there is a font to measure
        /// with, and drops the ones that would overflow.
        /// </summary>
        /// <remarks>
        /// <para><b>Dropped rather than clipped.</b> A paragraph that needs more lines than it had
        /// would be drawn over whatever sits below it — which this operation does not move and
        /// cannot see. Refusing keeps the one guarantee reflow still makes: nothing outside the
        /// paragraph changes.</para>
        ///
        /// <para><b>No font is not the same as no room.</b> When the font could not be resolved the
        /// paragraph is left with its lines unset, and the emitter reports it through the ordinary
        /// not-drawn path rather than this one inventing a layout for a run it cannot draw.</para>
        /// </remarks>
        private static void BreakReflowedParagraphs(
            List<PageWork> pages, EmbeddedFont font, TemplateReplaceOptions options,
            TemplateReplaceResult result)
        {
            if (font?.Program == null) return;

            foreach (PageWork work in pages)
            {
                for (int i = work.Matches.Count - 1; i >= 0; i--)
                {
                    TokenMatch m = work.Matches[i];
                    if (m.ReflowLineCount <= 0) continue;          // not a reflowed paragraph

                    List<string> lines = ReflowLineBreaker.Break(
                        m.Value, candidate => MeasureRun(candidate, font, options, m.FontSize), m.SlotWidth,
                        options.Hyphenate);

                    if (lines.Count == 0)
                    {
                        Refuse(result, m, work.Index, "the replacement leaves the paragraph empty");
                        work.Matches.RemoveAt(i);
                        continue;
                    }

                    if (lines.Count > m.ReflowLineCount && !options.AllowReflowPastTheParagraph)
                    {
                        Refuse(result, m, work.Index,
                            $"the replacement needs {lines.Count} lines where the paragraph had " +
                            $"{m.ReflowLineCount}, so it would be drawn over whatever follows it. " +
                            "Nothing outside the paragraph is moved by this operation; set " +
                            "AllowReflowPastTheParagraph to accept the overflow");
                        work.Matches.RemoveAt(i);
                        continue;
                    }

                    m.ReflowLines = lines;
                }
            }
        }

        /// <summary>The width a candidate line would be drawn at, in page units.</summary>
        /// <remarks>
        /// Shaped through the same composer the emitter draws with, so the width measured is the
        /// width produced. Measuring with anything else — a sum of advances, an unshaped string —
        /// would break lines against a number the drawing step does not agree with.
        /// </remarks>
        private static double MeasureRun(
            string candidate, EmbeddedFont font, TemplateReplaceOptions options, float fontSize)
        {
            var run = TextRunComposer.Compose(candidate, font.Program, new TextRunOptions
            {
                BaseDirection = options.BaseDirection,
                MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
            });

            // An uncoverable character has no width here; the emitter refuses the whole run by name,
            // and breaking on a short measurement first would hide that behind a layout decision.
            return run.IsComplete ? run.WidthAt(fontSize) : double.MaxValue;
        }

        /// <summary>
        /// One reflowed line's show-text operators at <paramref name="fontSize"/> (<see cref="GlyphRunShowText"/>), or null when the font cannot draw it.
        /// </summary>
        /// <remarks>
        /// The same composition the first line goes through, so a paragraph cannot be drawn with
        /// its first line shaped and the rest not. It refuses by name for the same reason: dropping
        /// one unmapped character silently is how a word loses a letter in the middle of a
        /// paragraph, where it is least likely to be noticed.
        /// </remarks>
        private static string RunShowText(
            string text, EmbeddedFont font, TemplateReplaceOptions options,
            TokenMatch m, TemplateReplaceResult result, float fontSize, double justifyTo = 0)
        {
            var run = TextRunComposer.Compose(text, font.Program, new TextRunOptions
            {
                BaseDirection = options.BaseDirection,
                MissingGlyphPolicy = MissingGlyphPolicy.Refuse,
            });

            if (!run.IsComplete)
            {
                result.NotDrawnList.Add(m.Key);
                result.WarningList.Add(
                    $"{m.Key}: the replacement font has no glyph for U+{run.MissingCodePoint:X4}; " +
                    "the paragraph was erased but nothing was drawn in its place");
                return null;
            }

            run.RecordUsedGlyphs(font.GidToUnicode, font.GidToText);
            return justifyTo > 0 ? JustifiedShowText(run, fontSize, justifyTo) : GlyphRunShowText.Operators(run, fontSize);
        }

        /// <summary>
        /// A justified line: the space glyphs share the room between the run's natural width and
        /// <paramref name="measure"/>, as <c>TJ</c> adjustments after each — the embedded font's codes
        /// are two bytes, which <c>Tw</c> never reaches (§9.3.3). A line with no space, or already as
        /// wide as the measure, is drawn as it is.
        /// </summary>
        private static string JustifiedShowText(ShapedGlyphRun run, float fontSize, double measure)
        {
            int spaces = 0;
            foreach (GlyphMapping glyph in run.Glyphs) if (glyph.CodePoint == ' ') spaces++;
            double natural = run.WidthAt(fontSize);
            if (spaces == 0 || natural >= measure || fontSize <= 0) return GlyphRunShowText.Operators(run, fontSize);

            // A TJ number moves the next glyph by −n/1000 of the font size (Tz is 100 here).
            double perSpace = -(measure - natural) / spaces / fontSize * Core.Constants.PdfUnits.GlyphSpaceUnitsPerEm;
            var after = new double[run.GlyphIds.Length];
            for (int i = 0; i < after.Length; i++) if (run.Glyphs[i].CodePoint == ' ') after[i] = perSpace;
            return GlyphRunShowText.Operators(run.GlyphIds, run.Placements, fontSize, 0, after);
        }

        private static void Refuse(
            TemplateReplaceResult result, TokenMatch span, int pageIndex, string reason)
        {
            result.NotReflowedList.Add(span.Key);
            result.WarningList.Add($"{span.Key} (page {pageIndex + 1}): {reason}; left untouched");
        }
    }
}
