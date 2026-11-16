// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Text
{
    public sealed class PdfTextExtractor
    {
        /// <summary>
        /// Reason the most recent Form-XObject extraction returned null.
        /// <c>null</c> when extraction succeeded.  Useful when a downstream
        /// caller observes missing text from a particular page and needs to
        /// distinguish "no text in form" from "form stream couldn't be
        /// decoded".
        /// <para>Also carries the aggregate-work refusal
        /// (<see cref="PdfLimits.MaxTextExtractionWorkUnits"/>): a non-null value there means the
        /// returned text is <b>truncated</b>, not that the page had no more text. Reset at the
        /// start of every <see cref="ExtractText(PdfPage)"/>; the whole-document overload keeps the
        /// FIRST page's reason rather than the last page's.</para>
        /// </summary>
        [System.ThreadStatic]
        private static string _lastFormXObjectError;
        public static string LastFormXObjectError => _lastFormXObjectError;

        public static string ExtractText(PdfPage page) => ExtractText(page, new TextExtractionCache());

        private static string ExtractText(PdfPage page, TextExtractionCache cache)
        {
            // Reset the diagnostic at the START of the operation that writes it, on every path —
            // otherwise a page with no Form XObject at all reports whatever the previous page (or
            // the previous document) left behind. See the Diagnostics Pattern section of CLAUDE.md.
            _lastFormXObjectError = null;
            cache.BeginPage();

            byte[] contentData = page.GetContentStreamData();
            if (contentData == null || contentData.Length == 0)
                return string.Empty;

            var document = GetDocument(page);

            // ONE cache per top-level extraction, for the same reason there is one budget: a form
            // invoked N times does not re-tokenise its content stream and rebuild its fonts N
            // times. Bounded and scoped to the call — a page's, or the whole document's, whose
            // pages then share fonts and the overlap index's storage. See TextExtractionCache.
            // The page's own resources go through the cache too: a form with no /Resources of its
            // own inherits these, so this is the entry every such form will hit.
            var fonts = ResolveFonts(page.Resources, document, cache);

            // Not cached: the page's own content is parsed exactly once per extraction, and
            // PdfPage.GetContentStreamData concatenates a fresh array for an array-valued
            // /Contents, so there would be nothing stable to key on and nothing to gain.
            var operations = ContentStreamParser.Parse(contentData);

            // ONE budget per top-level extraction, spanning every Form XObject the page reaches.
            // MaxFormXObjectDepth bounds depth and nothing else — see PdfLimits.
            var budget = new WorkBudget(PdfLimits.MaxTextExtractionWorkUnits,
                "Form XObject text extraction");

            return InterpretTextOperations(operations, fonts, document, page.Resources,
                depth: 0, visited: null, budget: budget, cache: cache);
        }

        public static string ExtractText(PdfDocument document) => ExtractText(document, null);

        /// <summary>
        /// Extracts every page's text, notifying <paramref name="onPage"/> with each page's own
        /// text as it is produced.
        /// </summary>
        /// <param name="document">The document to read.</param>
        /// <param name="onPage">
        /// Called with the zero-based page index and that page's text, or <c>null</c> to observe
        /// nothing. Exceptions it throws propagate; it is called after the page has been added.
        /// </param>
        /// <remarks>
        /// <para><b>This exists so that observing the pages cannot change the answer.</b>
        /// <c>PdfOperations.ExtractText</c> needs each page's text separately in order to notify
        /// text-extraction hooks, and reaches it through this loop: a second loop with <b>no
        /// separator between pages</b> and without the first-error aggregation below would make a
        /// document's extracted text depend on whether any plugin, of any kind, happened to be
        /// registered — the same two-page file coming back as <c>"one\ntwo"</c> or
        /// <c>"onetwo"</c>.</para>
        ///
        /// <para>The error slot is read <i>before</i> the callback runs, because a hook is free to
        /// run its own extraction and reset it.</para>
        /// </remarks>
        public static string ExtractText(PdfDocument document, Action<int, string> onPage)
            => ExtractText(document, onPage, new TextExtractionCache());

        /// <summary><see cref="ExtractText(PdfDocument, Action{int, string})"/> over a caller's cache, which its pages share.</summary>
        internal static string ExtractText(PdfDocument document, Action<int, string> onPage, TextExtractionCache cache)
        {
            var sb = new StringBuilder();
            string firstError = null;
            for (int i = 0; i < document.Pages.Count; i++)
            {
                string pageText = ExtractText(document.Pages[i], cache);
                // Each page resets the slot, so without this the whole-document call would report
                // only the LAST page's status — most often "fine" — and hide an earlier refusal.
                firstError ??= _lastFormXObjectError;

                if (i > 0) sb.AppendLine();
                sb.Append(pageText);
                onPage?.Invoke(i, pageText);
            }
            _lastFormXObjectError = firstError;
            return sb.ToString();
        }

        private static PdfDocument GetDocument(PdfPage page)
        {
            return page.Document;
        }

        private static Dictionary<string, PdfFont> LoadFonts(PdfDictionary resources, PdfDocument document,
            TextExtractionCache cache = null)
        {
            var fonts = new Dictionary<string, PdfFont>();
            if (resources == null || document == null) return fonts;

            var fontDict = resources[PdfName.Font];
            if (fontDict == null) return fonts;

            var resolvedFontDict = document.ResolveObject(fontDict) as PdfDictionary;
            if (resolvedFontDict == null) return fonts;

            foreach (var kvp in resolvedFontDict)
            {
                var fontObj = document.ResolveObject(kvp.Value) as PdfDictionary;
                if (fontObj != null)
                {
                    if (cache == null || !cache.TryGetFont(fontObj, out PdfFont font))
                    {
                        font = PdfFont.Create(fontObj, document);
                        cache?.AddFont(fontObj, font);
                    }
                    fonts[kvp.Key.Value] = font;
                }
            }

            return fonts;
        }

        /// <summary>
        /// The font map for <paramref name="resources"/>, from <paramref name="cache"/> when this
        /// exact dictionary instance has already been loaded during this extraction.
        /// </summary>
        /// <remarks>
        /// <b>Keyed on the resource dictionary, never on the form.</b> A Form XObject with no
        /// <c>/Resources</c> of its own inherits the invoker's (§8.10.1), so the same form invoked
        /// from a page and from inside another form legitimately sees two different font sets;
        /// keying on the form would hand the second invocation the first one's fonts. A miss is
        /// always safe — <see cref="LoadFonts"/> runs and produces the same answer as before.
        /// </remarks>
        private static Dictionary<string, PdfFont> ResolveFonts(PdfDictionary resources,
            PdfDocument document, TextExtractionCache cache)
        {
            if (cache != null && cache.TryGetFonts(resources, out var hit)) return hit;

            var fonts = LoadFonts(resources, document, cache);
            cache?.AddFonts(resources, fonts);
            return fonts;
        }

        /// <summary>
        /// The parsed operations of <paramref name="data"/>, from <paramref name="cache"/> when
        /// this exact decoded array has already been parsed during this extraction.
        /// </summary>
        private static List<ContentStreamOperation> ResolveOperations(byte[] data,
            TextExtractionCache cache)
            => cache != null ? cache.GetOrParse(data) : ContentStreamParser.Parse(data);

        // Defence-in-depth against pathological Form XObject cycles and deep
        // nesting.  32 levels covers legitimate deeply-nested templates (seen
        // ~8 levels max in real PDFs) while bounding worst-case recursion.
        private static int MaxFormXObjectDepth => PdfLimits.MaxFormXObjectDepth;

        /// <summary>
        /// Vertical text-space displacement, in unscaled text units, above which a
        /// <c>Td</c>/<c>TD</c> move is read as a line break rather than intra-line positioning.
        /// </summary>
        private const double LineBreakTextSpaceY = 0.5;

        /// <summary>
        /// Horizontal <c>Td</c> displacement, as a fraction of the current font size, above
        /// which the move is read as an inter-word gap and a space is emitted.
        /// </summary>
        private const double WordGapFontSizeFraction = 0.3;

        /// <summary>
        /// A gap, in ems of the text that follows it, wider than which is read as a space between
        /// words: a <c>TJ</c> number below −200 thousandths, and a <c>Tm</c> that starts the next
        /// text further than this to the right of where the last ended on the same line.
        /// </summary>
        private const double WordGapEmFraction = 0.2;

        /// <summary>
        /// How far, in ems, a <c>Td</c>'s landing point may sit above or below where the last glyph
        /// ended and still be on its line, for <see cref="ContinuesWhereTheLastGlyphEnded"/>.
        /// </summary>
        private const double SameLineEmFraction = 0.5;

        /// <summary>
        /// Device-space Y displacement, in points, above which a <c>Tm</c> that repositions the
        /// text object is read as starting a new line. Larger than
        /// <see cref="LineBreakTextSpaceY"/> because it is measured after the CTM is applied.
        /// </summary>
        private const double LineBreakDeviceY = 1;

        /// <summary>
        /// Glyph width, in glyph-space units, assumed when the font supplies no usable width —
        /// half an em, the conventional "average Latin glyph" approximation.
        /// </summary>
        private const double FallbackGlyphWidth = 500;

        private static string InterpretTextOperations(List<ContentStreamOperation> operations,
            Dictionary<string, PdfFont> fonts)
        {
            return InterpretTextOperations(operations, fonts, null, null,
                depth: 0, visited: null, budget: null, cache: null);
        }

        private static string InterpretTextOperations(List<ContentStreamOperation> operations,
            Dictionary<string, PdfFont> fonts, PdfDocument document, PdfDictionary resources)
        {
            return InterpretTextOperations(operations, fonts, document, resources,
                depth: 0, visited: null, budget: null, cache: null);
        }

        /// <summary>
        /// Charges <paramref name="cost"/> against the extraction budget.
        /// Returns false — recording the reason on <see cref="LastFormXObjectError"/> — when the
        /// budget is spent, so the caller returns the text it already has.
        /// </summary>
        /// <remarks>
        /// The partial-result policy is what <c>Base.Operations.WalkBudget</c> exists for, but that
        /// type lives in <c>Base</c> and <c>Core</c> may not reference it; converting the throw here
        /// is the available equivalent. The <see cref="WorkBudget.IsExhausted"/> pre-check keeps it
        /// to one exception no matter how many invocations are still queued on the open stack.
        /// </remarks>
        private static bool ChargeExtraction(WorkBudget budget, long cost)
        {
            if (budget == null) return true;
            if (budget.IsExhausted)
            {
                _lastFormXObjectError ??= BudgetRefusal;
                return false;
            }
            try { budget.Tick(cost); return true; }
            catch (WorkBudgetExceededException ex)
            {
                _lastFormXObjectError = ex.Message;
                return false;
            }
        }

        private const string BudgetRefusal =
            "Work budget for Form XObject text extraction exceeded; extracted text is partial.";

        private static string InterpretTextOperations(List<ContentStreamOperation> operations,
            Dictionary<string, PdfFont> fonts, PdfDocument document, PdfDictionary resources,
            int depth, HashSet<string> visited, WorkBudget budget, TextExtractionCache cache)
        {
            var sb = new StringBuilder();
            var shown = new List<ShownString>();   // where each shown string landed (InPositionOrder)
            var state = new TextState();
            PdfFont currentFont = null;
            var gsStack = new Stack<TextState>();
            double lastY = double.NaN;
            double lastX = double.NaN;
            // CTM tracking for accurate position calculation.
            var ctm = Matrix3x3.Identity;
            var ctmStack = new Stack<Matrix3x3>();

            // Open marked-content sequences: the §14.9.4 /ActualText each declares, and where in
            // the builder its text began. The second is what makes the replacement possible at all
            // — "the sequence's content" is not known until EMC.
            var markedContent = new Stack<(string ActualText, int Start, bool Reversed)>();

            // What each glyph drew and where, so that text drawn again at the same place is
            // extracted once. See ShowGlyph.
            var drawn = cache != null ? cache.RentIndex() : new OverlappingTextIndex<string>();

            foreach (var op in operations)
            {
                switch (op.Operator)
                {
                    // ---- Marked content (§14.9) --------------------------------------
                    //
                    // This extractor ignored marked content entirely, which was right while
                    // nothing here depended on it. §14.9.4's /ActualText changes that: it is "an
                    // exact replacement for the marked-content sequence's content", so a sequence
                    // that declares one must contribute the replacement and NOT the glyphs. What is
                    // tracked is where in the builder the sequence's text began, because that is
                    // the only way to know at EMC what the content turned out to be.
                    case PdfOperators.BeginMarkedContentProps:
                    {
                        MarkedContentInfo info = op.Operands.Length > 1
                            ? MarkedContentInfo.Read(op.Operands[1], resources, document)
                            : default;

                        // NESTING NEEDS NO GUARD, AND A MUTATION PROVED IT. An outer sequence's
                        // range starts before an inner one's, so the outer splice subsumes whatever
                        // the inner splice produced — truncating to the outer start discards it.
                        // Testing the stack for an open replacement and skipping the inner one
                        // would change no outcome anywhere — an inert guard against a real
                        // hazard.
                        markedContent.Push((info.ActualText, sb.Length,
                                            MarkedContentInfo.IsReversedChars(op.Operands.Length > 0 ? op.Operands[0] : null)));
                        break;
                    }

                    case PdfOperators.BeginMarkedContent:
                        markedContent.Push((null, sb.Length,
                                            MarkedContentInfo.IsReversedChars(op.Operands.Length > 0 ? op.Operands[0] : null)));
                        break;

                    case PdfOperators.EndMarkedContent:
                        if (markedContent.Count > 0)
                        {
                            var (actual, start, reversed) = markedContent.Pop();
                            if (actual != null && start <= sb.Length)
                            {
                                // Replace, not append. A sequence that drew nothing has nothing to
                                // replace and contributes the string anyway here — unlike the
                                // positioned extractor, which has nowhere to put it. A string has
                                // no geometry to be wrong about, so the /ActualText on a figure is
                                // carried rather than dropped.
                                sb.Length = start;
                                sb.Append(actual);
                            }
                            else if (reversed && start < sb.Length)
                            {
                                // A /ReversedChars run's brackets decode to their logical selves;
                                // ApplyBidi reads the line as drawn, so it is handed their drawn
                                // shapes (UnicodeBidi.AsDrawnInReversedRun).
                                string asDrawn = UnicodeBidi.AsDrawnInReversedRun(sb.ToString(start, sb.Length - start));
                                sb.Length = start;
                                sb.Append(asDrawn);
                            }
                        }
                        break;

                    case PdfOperators.Save: // Save graphics state
                        gsStack.Push(state.Clone());
                        ctmStack.Push(ctm);
                        break;

                    case PdfOperators.Restore: // Restore graphics state
                        if (gsStack.Count > 0)
                        {
                            // §9.3.1: the text matrices are not graphics state, so Q leaves them where
                            // the text has reached. Put back, the next string starts over the last one,
                            // and the duplicate rule takes a letter that falls on its like.
                            TextState reached = state;
                            state = gsStack.Pop();
                            state.TextMatrix = reached.TextMatrix;
                            state.TextLineMatrix = reached.TextLineMatrix;
                            state.LastShownEnd = reached.LastShownEnd;
                        }
                        if (ctmStack.Count > 0)
                            ctm = ctmStack.Pop();
                        break;

                    case PdfOperators.ConcatMatrix: // Concatenate matrix
                        if (op.Operands.Length >= 6)
                        {
                            var m = new Matrix3x3(
                                GetDouble(op.Operands[0]), GetDouble(op.Operands[1]),
                                GetDouble(op.Operands[2]), GetDouble(op.Operands[3]),
                                GetDouble(op.Operands[4]), GetDouble(op.Operands[5]));
                            ctm = Matrix3x3.Multiply(m, ctm);
                        }
                        break;

                    case PdfOperators.BeginText: // Begin text
                        state.TextMatrix = Matrix3x3.Identity;
                        state.TextLineMatrix = Matrix3x3.Identity;
                        break;

                    case PdfOperators.EndText: // End text
                        break;

                    case PdfOperators.SetFont: // Set font and size
                        if (op.Operands.Length >= 2)
                        {
                            if (op.Operands[0] is PdfName fontName)
                            {
                                state.FontName = fontName.Value;
                                fonts.TryGetValue(fontName.Value, out currentFont);
                            }
                            if (op.Operands[1] is PdfNumber fontSize)
                                state.FontSize = fontSize.DoubleValue;
                        }
                        break;

                    case PdfOperators.SetCharSpacing: // Character spacing
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfNumber tc)
                            state.CharacterSpacing = tc.DoubleValue;
                        break;

                    case PdfOperators.SetWordSpacing: // Word spacing
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfNumber tw)
                            state.WordSpacing = tw.DoubleValue;
                        break;

                    case PdfOperators.SetHorizontalScaling: // Horizontal scaling
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfNumber tz)
                            state.HorizontalScaling = tz.DoubleValue;
                        break;

                    case PdfOperators.SetLeading: // Leading
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfNumber tl)
                            state.Leading = tl.DoubleValue;
                        break;

                    case PdfOperators.SetTextRise: // Text rise
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfNumber ts)
                            state.TextRise = ts.DoubleValue;
                        break;

                    case PdfOperators.TextMove: // Move text position
                        if (op.Operands.Length >= 2)
                        {
                            double tx = GetDouble(op.Operands[0]);
                            double ty = GetDouble(op.Operands[1]);
                            var translate = new Matrix3x3(1, 0, 0, 1, tx, ty);
                            state.TextLineMatrix = Matrix3x3.Multiply(translate, state.TextLineMatrix);
                            state.TextMatrix = state.TextLineMatrix;

                            // Detect line breaks
                            if (!double.IsNaN(lastY) && System.Math.Abs(ty) > LineBreakTextSpaceY)
                            {
                                sb.AppendLine();
                            }
                            else if (!double.IsNaN(lastX) && tx > state.FontSize * WordGapFontSizeFraction
                                     && !ContinuesWhereTheLastGlyphEnded(state, Matrix3x3.Multiply(state.TextMatrix, ctm)))
                            {
                                sb.Append(' ');
                            }
                        }
                        break;

                    case PdfOperators.TextMoveSetLeading: // Move text position and set leading
                        if (op.Operands.Length >= 2)
                        {
                            double tx = GetDouble(op.Operands[0]);
                            double ty = GetDouble(op.Operands[1]);
                            state.Leading = -ty;
                            var translate = new Matrix3x3(1, 0, 0, 1, tx, ty);
                            state.TextLineMatrix = Matrix3x3.Multiply(translate, state.TextLineMatrix);
                            state.TextMatrix = state.TextLineMatrix;

                            if (System.Math.Abs(ty) > LineBreakTextSpaceY)
                                sb.AppendLine();
                        }
                        break;

                    case PdfOperators.SetTextMatrix: // Set text matrix
                        if (op.Operands.Length >= 6)
                        {
                            // Use CTM-transformed Y for accurate line detection.
                            var rawTm = new Matrix3x3(
                                GetDouble(op.Operands[0]), GetDouble(op.Operands[1]),
                                GetDouble(op.Operands[2]), GetDouble(op.Operands[3]),
                                GetDouble(op.Operands[4]), GetDouble(op.Operands[5]));
                            var effectiveTm = Matrix3x3.Multiply(rawTm, ctm);
                            double newY = effectiveTm.TranslateY;

                            if (!double.IsNaN(lastY) && System.Math.Abs(newY - lastY) > LineBreakDeviceY)
                            {
                                sb.AppendLine();
                            }
                            else if (!double.IsNaN(lastX) && StartsAWordGapAway(state, effectiveTm)
                                     && sb.Length > 0 && !char.IsWhiteSpace(sb[sb.Length - 1]))
                            {
                                // A producer that places each word by its own text matrix — an OCR
                                // layer, a layout tool — leaves the gap between words as a jump, not
                                // a space character or a TJ kern. Read as a TJ gap is read.
                                sb.Append(' ');
                            }

                            state.TextMatrix = rawTm;
                            state.TextLineMatrix = state.TextMatrix;
                            lastY = newY;
                        }
                        break;

                    case PdfOperators.NextLine: // Move to next line
                        var t = new Matrix3x3(1, 0, 0, 1, 0, -state.Leading);
                        state.TextLineMatrix = Matrix3x3.Multiply(t, state.TextLineMatrix);
                        state.TextMatrix = state.TextLineMatrix;
                        sb.AppendLine();
                        break;

                    case PdfOperators.ShowText: // Show text
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfString tjStr)
                        {
                            var effTm = Matrix3x3.Multiply(state.TextMatrix, ctm);
                            lastY = effTm.TranslateY;
                            lastX = effTm.TranslateX;
                            ShowGlyphs(sb, drawn, state, ctm, tjStr, currentFont, shown);
                        }
                        break;

                    case PdfOperators.ShowTextArray: // Show text with individual glyph positioning
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfArray tjArray)
                        {
                            // Treat a TJ kern wider than this as a word break.
                            //
                            // The threshold is deliberately NOT scaled by font size. A TJ number is
                            // expressed in thousandths of a unit of TEXT space, so `num / 1000` is
                            // already a fraction of an em; font size only enters when text space is
                            // mapped to user space (see the `kern` calculation below). "Gap wider
                            // than 0.2 em" is therefore exactly `num <= -200` at every font size.
                            //
                            // No font-size condition: the threshold is the same at every size.
                            const double spaceThreshold = -(WordGapEmFraction * PdfUnits.GlyphSpaceUnitsPerEm);

                            // The word space a kern stands for belongs to the glyph before it:
                            // kept with a kept glyph, dropped with a copy. A kern before any glyph
                            // has nothing to follow and is kept.
                            //
                            // Not before a combining mark: a producer placing a mark by its font's
                            // anchors moves right onto the letter, draws the zero-width mark there and
                            // moves back — a gap that is no word break, and a space before the mark
                            // split the word it belongs to. So the space waits for the glyph after it.
                            bool lastGlyphKept = true;
                            int pendingSpace = -1;            // where a kern's space was put, if one is waiting
                            foreach (var item in tjArray)
                            {
                                if (item is PdfString str)
                                {
                                    lastGlyphKept = ShowGlyphs(sb, drawn, state, ctm, str, currentFont, shown);
                                    if (pendingSpace >= 0 && pendingSpace + 1 < sb.Length
                                        && CharUnicodeInfo.GetUnicodeCategory(sb[pendingSpace + 1]) == UnicodeCategory.NonSpacingMark)
                                        sb.Remove(pendingSpace, 1);
                                    pendingSpace = -1;
                                }
                                else if (item is PdfNumber num)
                                {
                                    // Kerning: advance by -num/1000 * fontSize — along the line, under
                                    // Th; down the column of a vertical font, without it (§9.4.4).
                                    bool vertical = IsVertical(currentFont);
                                    double kern = -(num.DoubleValue / PdfUnits.GlyphSpaceUnitsPerEm) * state.FontSize
                                                  * (vertical ? 1.0 : state.HorizontalScaling / 100.0);
                                    AdvanceTextMatrixBy(state, kern, vertical);

                                    if (num.DoubleValue < spaceThreshold && lastGlyphKept && pendingSpace < 0)
                                    {
                                        pendingSpace = sb.Length;
                                        sb.Append(' ');
                                    }
                                }
                            }
                            var effTm2 = Matrix3x3.Multiply(state.TextMatrix, ctm);
                            lastY = effTm2.TranslateY;
                            lastX = effTm2.TranslateX;
                        }
                        break;

                    case PdfOperators.NextLineShowText: // Move to next line and show text
                        {
                            var tstar = new Matrix3x3(1, 0, 0, 1, 0, -state.Leading);
                            state.TextLineMatrix = Matrix3x3.Multiply(tstar, state.TextLineMatrix);
                            state.TextMatrix = state.TextLineMatrix;
                            sb.AppendLine();
                            if (op.Operands.Length >= 1 && op.Operands[0] is PdfString aposStr)
                            {
                                var effTm3 = Matrix3x3.Multiply(state.TextMatrix, ctm);
                                lastY = effTm3.TranslateY;
                                lastX = effTm3.TranslateX;
                                ShowGlyphs(sb, drawn, state, ctm, aposStr, currentFont, shown);
                            }
                        }
                        break;

                    case PdfOperators.NextLineSetSpacingShowText: // Set word/char spacing, move to next line, show text
                        if (op.Operands.Length >= 3)
                        {
                            state.WordSpacing = GetDouble(op.Operands[0]);
                            state.CharacterSpacing = GetDouble(op.Operands[1]);
                            var tstar2 = new Matrix3x3(1, 0, 0, 1, 0, -state.Leading);
                            state.TextLineMatrix = Matrix3x3.Multiply(tstar2, state.TextLineMatrix);
                            state.TextMatrix = state.TextLineMatrix;
                            sb.AppendLine();
                            if (op.Operands[2] is PdfString dquoteStr)
                            {
                                var effTm4 = Matrix3x3.Multiply(state.TextMatrix, ctm);
                                lastY = effTm4.TranslateY;
                                lastX = effTm4.TranslateX;
                                ShowGlyphs(sb, drawn, state, ctm, dquoteStr, currentFont, shown);
                            }
                        }
                        break;

                    case PdfOperators.InvokeXObject: // Invoke XObject — recurse into Form XObjects for text extraction
                        if (document != null && resources != null && op.Operands.Length >= 1
                            && op.Operands[0] is PdfName xobjName
                            && depth < MaxFormXObjectDepth)
                        {
                            // Track visited XObject names to break cycles. A PDF that references the
                            // same Form XObject twice legitimately (e.g. a header repeated on two
                            // pages) is fine — the visited set is scoped to the current recursion
                            // BRANCH, not to the whole walk.
                            //
                            // Not a fresh copy of the set per `Do`, taken before anything checks
                            // whether the name refers to a form, which would make every image `Do`
                            // on the page allocate and populate a HashSet it cannot use. One set with add/remove around the recursion is the
                            // same branch scope by the standard depth-first argument: a sibling
                            // invoked after this one sees the name removed, exactly as it saw a copy
                            // taken before it was added; a descendant sees it present, exactly as it
                            // saw it in the copy. The `finally` matters because extraction is
                            // failure-tolerant — an exception out of the recursion must not leave
                            // the name behind and make a later sibling look like a cycle.
                            visited ??= new HashSet<string>(System.StringComparer.Ordinal);
                            if (visited.Add(xobjName.Value))
                            {
                                string formText;
                                try
                                {
                                    formText = ExtractFormXObjectText(
                                        xobjName.Value, document, resources, depth + 1, visited,
                                        budget, cache);
                                }
                                finally { visited.Remove(xobjName.Value); }

                                if (!string.IsNullOrEmpty(formText))
                                {
                                    if (sb.Length > 0) sb.AppendLine();
                                    sb.Append(formText);
                                }
                            }
                        }
                        break;
                }
            }

            if (drawn.CeilingReached) _lastFormXObjectError ??= OverlapRefusal;
            cache?.ReturnIndex(drawn);
            return ApplyBidi(InPositionOrder(sb, shown).TrimEnd());
        }

        private const string OverlapRefusal =
            "The page's duplicate-text comparisons exceeded PdfLimits.MaxOverlappingTextComparisons; " +
            "text drawn again over itself after that point is extracted once for each drawing.";

        /// <summary>
        /// Extracts text from a Form XObject referenced by name in the page's /XObject resources.
        /// Recurses into Form XObjects.
        /// </summary>
        private static string ExtractFormXObjectText(string name, PdfDocument document, PdfDictionary resources,
            int depth = 1, HashSet<string> visited = null, WorkBudget budget = null,
            TextExtractionCache cache = null)
        {
            if (resources == null) return null;
            if (depth > MaxFormXObjectDepth) return null;
            // Charge the fixed per-invocation cost first: the resolve, the visited-set copy and the
            // font load below happen for every invocation, and a fan-out of near-empty forms is
            // exactly the cheap shape to author.
            if (!ChargeExtraction(budget, PdfLimits.FormXObjectInvocationCost)) return null;
            var xobjRes = resources[PdfName.XObject];
            if (xobjRes == null) return null;
            var xobjDict = document.ResolveObject(xobjRes) as PdfDictionary;
            if (xobjDict == null) return null;
            var entry = xobjDict[new PdfName(name)];
            if (entry == null) return null;
            var formDict = document.ResolveObject(entry) as PdfDictionary;
            if (formDict == null) return null;

            // Must be a Form XObject. PdfName's operator!= is value-based and null-safe, so a
            // missing /Subtype falls out here too.
            var subtype = document.ResolveObject(formDict[PdfName.Subtype]) as PdfName;
            if (subtype != PdfName.Form) return null;

            var formStream = formDict as PdfStream ?? document.ResolveObject(formDict) as PdfStream;
            if (formStream == null) return null;

            byte[] data;
            try { data = formStream.GetDecodedData(); _lastFormXObjectError = null; }
            catch (Exception ex)
            {
                _lastFormXObjectError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
            if (data == null || data.Length == 0) return null;

            // Charge the parse, which is proportional to the decoded byte count. A form re-entered
            // on N sibling branches is re-parsed N times, so the budget has to see each one.
            //
            // The charge stays UNCONDITIONAL now that the parse is memoised, and that is
            // deliberate: the budget is a DoS bound, and interpreting the operations still costs
            // one pass per invocation whether or not the tokenising was skipped. Charging only on
            // a cache miss would let a fan-out of one form buy an unbounded number of
            // interpretation passes for the price of one parse. The cache is a speed and
            // allocation win; it must not become a budget hole.
            if (!ChargeExtraction(budget, data.Length)) return null;

            // Form XObjects have their own resource dictionary
            var formResources = formDict[PdfName.Resources] is PdfObject ro
                ? document.ResolveObject(ro) as PdfDictionary ?? resources
                : resources;

            var formFonts = ResolveFonts(formResources, document, cache);
            var formOps = ResolveOperations(data, cache);
            return InterpretTextOperations(formOps, formFonts, document, formResources, depth,
                visited, budget, cache);
        }

        /// <summary>
        /// Shows <paramref name="str"/> glyph by glyph: each glyph advances the text matrix by its
        /// own width and is appended unless the same glyph was drawn earlier in this content stream
        /// at nearly the same place. Returns whether the last glyph shown was appended — true when
        /// nothing was shown — which is what a <c>TJ</c> kern space after it follows.
        /// </summary>
        /// <remarks>
        /// <para><b>The unit is the glyph, not the show operator.</b> Text drawn twice — a slug line
        /// stroked and then filled — reads once, not <c>PEC_CH00_FM_2PP.indd 1PEC_CH00_FM_2PP.indd 1</c>.
        /// Comparing one operator's text at a time catches that case but not an OCR layer drawn word
        /// by word over lines drawn whole: neither operator's text equals the other's. Structured
        /// extraction compares per glyph, and the positions that needs are the ones the per-glyph advance
        /// computes anyway, so the rule (<see cref="OverlappingTextIndex{TKey}"/>) is applied at that
        /// granularity here too, whatever operator boundaries a copy has.</para>
        /// <para><b>A glyph's text comes from decoding its own bytes.</b>
        /// <see cref="PdfFont.SplitCodes"/> cuts the string at the encoding CMap's code boundaries
        /// and <see cref="PdfFont.Decode(byte[])"/> maps each code independently, so decoding one code's
        /// bytes gives the characters the whole string would, one glyph at a time. A font without a
        /// usable width table has no codes to speak of; there each decoded character is a glyph at
        /// the 500-unit fallback advance, as before.</para>
        /// </remarks>
        private static bool ShowGlyphs(StringBuilder sb, OverlappingTextIndex<string> drawn,
            TextState state, Matrix3x3 ctm, PdfString str, PdfFont font, List<ShownString> shown)
        {
            int startLength = sb.Length;
            var startAt = Matrix3x3.Multiply(state.TextMatrix, ctm);
            bool kept = ShowGlyphs(sb, drawn, state, ctm, str, font);
            if (sb.Length > startLength)
                shown.Add(new ShownString(startLength, sb.Length, startAt,
                    Matrix3x3.Multiply(state.TextMatrix, ctm).TranslateX, state.FontSize));
            return kept;
        }

        private static bool ShowGlyphs(StringBuilder sb, OverlappingTextIndex<string> drawn,
            TextState state, Matrix3x3 ctm, PdfString str, PdfFont font)
        {
            bool kept = true;
            byte[] raw = str.RawBytes;
            if (UseWidthBasedAdvance(font))
            {
                // §9.7.4.3: a vertical font's pen moves down the column, by w1 and the spacings.
                bool vertical = IsVertical(font);
                // A code at a time (NextCode), not a list per shown string: on a hundred-thousand-
                // show document the list was a third of what this extractor allocated.
                for (int offset = 0; offset < raw.Length;)
                {
                    offset += Math.Min(Math.Max(font.NextCode(raw, offset, out CMapCodec.Code code), 1), raw.Length - offset);
                    // The code's own text, from the font's per-code cache — not a copy of its bytes
                    // decoded afresh, which was a byte array and a StringBuilder per glyph.
                    kept = ShowGlyph(sb, drawn, state, ctm, UnicodeBidi.InVisualOrder(font.Decode(code)),
                                     GlyphAdvance(state, code, font), vertical);
                }
            }
            else
            {
                string text = DecodeString(str, font);
                double advance = FallbackGlyphAdvance(state);
                for (int i = 0; i < text.Length; i++)
                {
                    string glyph = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                        ? text.Substring(i++, 2)
                        : text[i].ToString();
                    kept = ShowGlyph(sb, drawn, state, ctm, glyph, advance);
                }
            }
            return kept;
        }

        /// <summary>
        /// Appends one glyph unless it is a copy of an earlier one, advancing the text matrix by
        /// <paramref name="advance"/> (text space) either way. The copy test is in device space —
        /// where the glyph starts and how far it moves the matrix, after the text matrix's own
        /// scale and rotation and the CTM — so a quarter turn or a twelvefold <c>Tm</c> changes
        /// nothing about which glyphs are copies.
        /// </summary>
        private static bool ShowGlyph(StringBuilder sb, OverlappingTextIndex<string> drawn,
            TextState state, Matrix3x3 ctm, string glyph, double advance, bool vertical = false)
        {
            var start = Matrix3x3.Multiply(state.TextMatrix, ctm);
            AdvanceTextMatrixBy(state, advance, vertical);
            if (glyph.Length == 0) return true;   // a code that maps to no character: nothing to append

            var end = Matrix3x3.Multiply(state.TextMatrix, ctm);
            double dx = end.TranslateX - start.TranslateX, dy = end.TranslateY - start.TranslateY;
            if (drawn.IsCopyOfEarlier(glyph, start.TranslateX, start.TranslateY, Math.Sqrt(dx * dx + dy * dy), glyph.Length))
                return false;

            sb.Append(glyph);
            state.LastShownEnd = end;
            return true;
        }

        /// <summary>
        /// True when a <c>Td</c> leaves the next text where the last glyph appended ended, on the
        /// same upright line: no further right than <see cref="WordGapEmFraction"/> of an em, and
        /// no further left than a kern pulls a glyph back (an em).
        /// </summary>
        /// <remarks>
        /// A <c>Td</c> moves from the start of the line, not from the end of what the line has
        /// shown, so its operand grows with every glyph before it. A producer that writes each
        /// kerning pair as a move — a browser's PDF: <c>&lt;P&gt; Tj 7.43 0 Td &lt;AYROLL&gt; Tj</c> —
        /// moves by the width of the glyphs shown, which is no gap at all; read by its operand
        /// alone, the word comes back as <c>P AYROLL</c> and no search finds it. Where the gap
        /// cannot be measured — nothing shown yet, text not upright, another line — the answer is
        /// false and the operand decides.
        /// </remarks>
        private static bool ContinuesWhereTheLastGlyphEnded(TextState state, Matrix3x3 next)
        {
            if (!(state.LastShownEnd is Matrix3x3 end)) return false;
            bool runsAlongX = next.A > 0 && System.Math.Abs(next.B) < 1e-6 * next.A
                           && end.A > 0 && System.Math.Abs(end.B) < 1e-6 * end.A;
            if (!runsAlongX) return false;
            double em = state.FontSize * System.Math.Sqrt(next.C * next.C + next.D * next.D);
            if (!(em > 0)) return false;
            if (System.Math.Abs(next.TranslateY - end.TranslateY) > SameLineEmFraction * em) return false;
            double gap = next.TranslateX - end.TranslateX;
            return gap <= WordGapEmFraction * em && gap >= -em;
        }

        /// <summary>
        /// True when the text a new <c>Tm</c> starts, upright and on the line the last text ended on,
        /// begins more than <see cref="WordGapEmFraction"/> of an em to the right of where the last
        /// glyph appended ended (<see cref="TextState.LastShownEnd"/>, which a <c>BT</c> does not reset).
        /// </summary>
        private static bool StartsAWordGapAway(TextState state, Matrix3x3 next)
        {
            if (!(state.LastShownEnd is Matrix3x3 end)) return false;
            bool upright = next.A > 0 && System.Math.Abs(next.B) < 1e-6 * next.A
                        && end.A > 0 && System.Math.Abs(end.B) < 1e-6 * end.A;
            if (!upright) return false;
            double em = state.FontSize * System.Math.Sqrt(next.C * next.C + next.D * next.D);
            return em > 0 && next.TranslateX - end.TranslateX > WordGapEmFraction * em;
        }

        /// <summary>
        /// Advances the text matrix by <paramref name="t"/> units of text space along its x axis
        /// — §9.4.4's <c>Tm ← [1 0 0 1 tx 0] × Tm</c> — or, for a vertical font, along its y axis
        /// (<c>[1 0 0 1 0 ty]</c>), so the advance is carried through the text matrix's own scale
        /// and rotation.
        /// </summary>
        /// <remarks>
        /// Adding the advance to <c>Tm.E</c> directly would be the same thing only while <c>Tm</c>
        /// is a pure translation: under <c>1 Tf</c> with a <c>12 0 0 12 x y Tm</c> — how many
        /// producers size their text — every glyph would advance half a point instead of six, and
        /// under a rotated <c>Tm</c> the line would run along the page's x axis whichever way the
        /// matrix turned it.
        /// </remarks>
        private static void AdvanceTextMatrixBy(TextState state, double t, bool vertical = false)
        {
            var tm = state.TextMatrix;
            if (vertical)
            {
                tm.E += t * tm.C;
                tm.F += t * tm.D;
            }
            else
            {
                tm.E += t * tm.A;
                tm.F += t * tm.B;
            }
            state.TextMatrix = tm;
        }

        /// <summary>Whether <paramref name="font"/> writes vertically (<c>/WMode 1</c>, §9.7.4.3).</summary>
        private static bool IsVertical(PdfFont font) => font is Fonts.PdfType0Font composite && composite.IsVerticalWritingMode;

        /// <summary>One shown string's place in the text and on the page: its characters' range and where it starts and ends.</summary>
        private readonly struct ShownString
        {
            internal ShownString(int start, int end, Matrix3x3 startAt, double endX, double fontSize)
            {
                Start = start;
                End = end;
                StartX = startAt.TranslateX;
                EndX = endX;
                Em = fontSize * Math.Sqrt(startAt.C * startAt.C + startAt.D * startAt.D);
                Upright = startAt.A > 0 && Math.Abs(startAt.B) < 1e-6 * startAt.A;
            }

            internal int Start { get; }
            internal int End { get; }
            internal double StartX { get; }
            internal double EndX { get; }
            internal double Em { get; }
            internal bool Upright { get; }
        }

        /// <summary>
        /// <paramref name="sb"/>'s text, each line holding a right-to-left character put in left-to-right
        /// order of where its strings were drawn when they were drawn out of it — so that
        /// <see cref="ApplyBidi"/>, which reads a line as visual, reads what the page shows.
        /// </summary>
        /// <remarks>
        /// <para>A line's text is its strings in the order the content stream shows them. A producer that
        /// draws a line's pieces out of their places — the Builder drew a list marker after its item, at
        /// the item's right — read back misordered though the page was right: "פריטא." for "א. פריט".
        /// Such a line's strings are taken in order of their starting x, and the space between two is
        /// read from the gap (more than <see cref="WordGapEmFraction"/> of an em) or a space at either
        /// edge, as word gaps are read elsewhere here.</para>
        /// <para>Only where it changes something: a line with no right-to-left character (bidi does not
        /// read it), one whose strings go left to right already, by more than half an em, one with a
        /// string not upright, and one holding text no shown string accounts for (a form's) are left as
        /// they are.</para>
        /// </remarks>
        private static string InPositionOrder(StringBuilder sb, List<ShownString> shown)
        {
            string text = sb.ToString();
            if (shown.Count < 2 || !UnicodeBidi.ContainsRtl(text)) return text;

            StringBuilder result = null;
            int copied = 0, next = 0;
            for (int lineStart = 0; lineStart < text.Length;)
            {
                int newline = text.IndexOf('\n', lineStart);
                int lineEnd = newline < 0 ? text.Length : newline;
                int contentEnd = lineEnd > lineStart && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;

                while (next < shown.Count && shown[next].Start < lineStart) next++;
                int first = next;
                while (next < shown.Count && shown[next].Start < contentEnd) next++;

                string ordered = OrderedLine(text, lineStart, contentEnd, shown, first, next);
                if (ordered != null)
                {
                    result ??= new StringBuilder(text.Length);
                    result.Append(text, copied, lineStart - copied).Append(ordered);
                    copied = contentEnd;
                }
                if (newline < 0) break;
                lineStart = newline + 1;
            }
            if (result == null) return text;
            return result.Append(text, copied, text.Length - copied).ToString();
        }

        /// <summary>The line <c>[start, end)</c> with its strings <c>shown[first..last)</c> in order of x, or null to leave it.</summary>
        private static string OrderedLine(string text, int start, int end, List<ShownString> shown, int first, int last)
        {
            if (last - first < 2) return null;
            bool rtl = false;
            for (int i = start; i < end && !rtl; i++) rtl = UnicodeBidi.IsStrongRtl(text[i]);
            if (!rtl) return null;

            bool outOfOrder = false;
            for (int k = first; k < last; k++)
            {
                if (!shown[k].Upright) return null;
                if (k > first && shown[k].StartX < shown[k - 1].StartX - 0.5 * Math.Max(shown[k].Em, shown[k - 1].Em))
                    outOfOrder = true;
            }
            if (!outOfOrder) return null;

            // Every letter of the line must be a shown string's: a form's text, appended whole, has no place.
            int covered = start;
            for (int k = first; k < last; k++)
            {
                for (int i = covered; i < shown[k].Start; i++) if (!char.IsWhiteSpace(text[i])) return null;
                covered = Math.Max(covered, shown[k].End);
            }
            for (int i = covered; i < end; i++) if (!char.IsWhiteSpace(text[i])) return null;

            var order = new List<(ShownString Item, int MarksFrom)>(last - first);
            for (int k = first; k < last; k++)
            {
                int marksFrom = k;
                while (k + 1 < last && IsMarksOnly(text, shown[k], end) && Carries(shown[k], shown[k + 1])) k++;
                order.Add((shown[k], marksFrom));
            }
            order.Sort((a, b) => a.Item.StartX != b.Item.StartX
                ? a.Item.StartX.CompareTo(b.Item.StartX) : a.Item.Start.CompareTo(b.Item.Start));

            // A space between two strings where they are a word gap apart or either brings one at the
            // edge between them; a string of nothing but spaces only carries its space over.
            var line = new StringBuilder(end - start);
            bool spaceWaiting = false;
            ShownString? previous = null;
            foreach (var (item, marksFrom) in order)
            {
                int from = shown[marksFrom].Start;
                string piece = text.Substring(from, Math.Min(item.End, end) - from);
                string trimmed = piece.Trim();
                // A space drawn on the string before it, inside that string's span, separates nothing.
                if (trimmed.Length == 0 && previous is ShownString under
                    && item.StartX < under.EndX - WordGapEmFraction * Math.Max(under.Em, item.Em))
                    continue;
                if (piece.Length > 0 && char.IsWhiteSpace(piece[0])) spaceWaiting = true;
                if (trimmed.Length > 0)
                {
                    if (previous is ShownString before && line.Length > 0
                        && item.StartX - before.EndX > WordGapEmFraction * Math.Max(before.Em, item.Em))
                        spaceWaiting = true;
                    if (spaceWaiting && line.Length > 0) line.Append(' ');
                    line.Append(trimmed);
                    spaceWaiting = false;
                }
                if (piece.Length > 0 && char.IsWhiteSpace(piece[piece.Length - 1])) spaceWaiting = true;
                previous = item;
            }
            return line.ToString();
        }

        /// <summary>
        /// Whether <paramref name="s"/>'s text is combining marks alone (Unicode's Mn and Me) — a point a
        /// producer draws as a string of its own.
        /// </summary>
        private static bool IsMarksOnly(string text, ShownString s, int lineEnd)
        {
            int stop = Math.Min(s.End, lineEnd);
            if (stop <= s.Start) return false;
            for (int i = s.Start; i < stop; i++)
            {
                var category = char.GetUnicodeCategory(text[i]);
                if (category != System.Globalization.UnicodeCategory.NonSpacingMark
                    && category != System.Globalization.UnicodeCategory.EnclosingMark) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether the points of <paramref name="marks"/> sit on <paramref name="next"/>'s glyphs: a
        /// line drawn in visual order has a right-to-left letter's points before it (UAX #9's L3 — Word's
        /// export, and the Microsoft PS Class Driver's glyph runs), placed inside the letter, so an order
        /// by starting x would put them after it. They go with it, the points within an em of its span.
        /// </summary>
        private static bool Carries(ShownString marks, ShownString next)
        {
            double em = Math.Max(marks.Em, next.Em);
            return marks.StartX >= Math.Min(next.StartX, next.EndX) - em
                && marks.StartX <= Math.Max(next.StartX, next.EndX) + em;
        }

        /// <summary>
        /// Applies Unicode BiDi reordering line by line.
        /// Lines that contain only LTR characters are returned unchanged (fast path).
        /// </summary>
        private static string ApplyBidi(string text)
        {
            // Reorder answers a line with no right-to-left character unchanged, so a page with none
            // is its own answer — without a string per line to find that out.
            if (string.IsNullOrEmpty(text) || !UnicodeBidi.ContainsRtl(text))
                return text;

            var lines = text.Split('\n');
            bool anyReordered = false;
            for (int i = 0; i < lines.Length; i++)
            {
                // Strip trailing \r if present from \r\n splits
                string line = lines[i].TrimEnd('\r');
                string reordered = UnicodeBidi.Reorder(line, UnicodeBidi.VisualLineDirection(line));
                if (!ReferenceEquals(reordered, line) && reordered != line)
                {
                    lines[i] = reordered;
                    anyReordered = true;
                }
            }

            return anyReordered ? string.Join("\n", lines) : text;
        }

        /// <summary>
        /// Decides whether to advance the text cursor using the font's real width tables.
        /// For composite (Type0/CID) fonts we always use the /W + /DW width-based advance:
        /// probing <c>GetWidth(0x20)</c> there returns the width of CID 32 (≈/DW), not the
        /// space glyph, which is wrong for CJK / Identity-H. For simple fonts we keep the
        /// original probe — a non-zero advance for the space code means a usable /Widths
        /// table is present.
        /// </summary>
        private static bool UseWidthBasedAdvance(PdfFont currentFont)
        {
            if (currentFont == null) return false;
            if (currentFont is Fonts.PdfType0Font) return true;
            return currentFont.GetWidth(0x20) > 0;
        }

        /// <summary>
        /// The advance of one glyph, in text space, when the font supplies no usable width table:
        /// <see cref="FallbackGlyphWidth"/> at the current size and horizontal scaling, and none at
        /// all at a font size of zero or less.
        /// </summary>
        private static double FallbackGlyphAdvance(TextState state)
            => state.FontSize <= 0
                ? 0
                : (FallbackGlyphWidth / PdfUnits.GlyphSpaceUnitsPerEm) * state.FontSize
                  * (state.HorizontalScaling / 100.0);

        /// <summary>
        /// Advances the text matrix using actual per-glyph widths from the font's width table.
        /// Reads /Widths (simple fonts) or /W + /DW (CID fonts)
        /// and use them to compute precise text cursor advancement.
        /// </summary>
        /// <remarks>
        /// <para><paramref name="codes"/> comes from <see cref="PdfFont.SplitCodes"/>, so the
        /// composite case is split by the font's <c>/Encoding</c> CMap — the same split both
        /// renderers make, never a fixed stride, which no mixed-width CMap can be expressed in and
        /// which would disagree with the renderers whenever <c>/ToUnicode</c> and <c>/Encoding</c>
        /// do.</para>
        ///
        /// <para><b>Widths are keyed by CID.</b> <c>/W</c> is a CID table (§9.7.4.3), so the lookup
        /// is on <see cref="CMapCodec.Code.Cid"/>; for a simple font that equals the byte, so one
        /// loop serves both. And <c>Tw</c> applies to the <b>single-byte</b> code 32 only (§9.3.3),
        /// which is what <see cref="CMapCodec.Code.IsWordSpaceCode"/> encodes, never a 2-byte code
        /// 0x0020, which the spec excludes.</para>
        ///
        /// <para><c>internal</c> rather than <c>private</c> so the advance can be measured directly:
        /// the resulting text matrix is not observable in the extracted string, so nothing weaker
        /// than reading it back can tell a correct advance from a wrong one. The show operators go
        /// glyph by glyph through the same two pieces, <see cref="GlyphAdvance"/> and
        /// <see cref="AdvanceTextMatrixBy"/>; this is the run's sum of them.</para>
        /// </remarks>
        internal static void AdvanceTextMatrixWithWidths(TextState state,
            IReadOnlyList<CMapCodec.Code> codes, PdfFont currentFont)
        {
            if (codes == null || codes.Count == 0 || state.FontSize <= 0) return;

            double totalAdvance = 0;
            for (int i = 0; i < codes.Count; i++)
                totalAdvance += GlyphAdvance(state, codes[i], currentFont);

            AdvanceTextMatrixBy(state, totalAdvance, IsVertical(currentFont));
        }

        /// <summary>
        /// The advance of one glyph, in text space: §9.4.4's <c>tx = (w₀ · Tfs + Tc + Tw) · Th</c>,
        /// with <c>Tw</c> on the single-byte code 32 only; for a vertical font the downward
        /// <c>ty = w₁ · Tfs + Tc + Tw</c>, negative, where <c>w₁</c> is negative and the spacings
        /// shorten the step (§9.3.2), as the renderers step. None at a font size of zero or less.
        /// </summary>
        private static double GlyphAdvance(TextState state, CMapCodec.Code code, PdfFont font)
        {
            if (state.FontSize <= 0) return 0;

            if (font is Fonts.PdfType0Font composite && composite.IsVerticalWritingMode)
            {
                double down = composite.VerticalAdvanceEm(code.Cid) * state.FontSize - state.CharacterSpacing;
                if (code.IsWordSpaceCode) down -= state.WordSpacing;
                return -down;
            }

            // One rule, in Core.Fonts.GlyphWidth. This site coerced ANY non-positive width to the
            // 500-unit default with no escape, so an explicit /W 0 — the Hebrew-nikud and
            // combining-mark case — advanced 500 units here and 0 in both renderers. Extracted
            // positions drifted from the glyphs they describe by one default advance per mark,
            // accumulating along the run.
            // In text space already: a Type 3 font's width goes through its /FontMatrix, not 1000.
            double charAdv = GlyphWidth.TextSpace(font, code.Cid) * state.FontSize;
            charAdv += state.CharacterSpacing;
            if (code.IsWordSpaceCode) charAdv += state.WordSpacing;
            return charAdv * (state.HorizontalScaling / 100.0);
        }

        private static string DecodeString(PdfString str, PdfFont font)
        {
            if (font != null)
                return font.Decode(str.RawBytes);
            return str.GetText();
        }

        private static double GetDouble(PdfObject obj)
        {
            if (obj is PdfNumber num) return num.DoubleValue;
            return 0;
        }
    }
}
