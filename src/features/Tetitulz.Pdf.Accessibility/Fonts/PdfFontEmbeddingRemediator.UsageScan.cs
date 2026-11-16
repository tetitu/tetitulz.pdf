// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Fonts
{
    public static partial class PdfFontEmbeddingRemediator
    {
        // ================================================================
        //  Content-stream usage scan
        // ================================================================

        /// <summary>
        /// Which character codes each font actually draws, and whether that answer is complete.
        /// </summary>
        internal sealed class UsageScan
        {
            internal readonly Dictionary<PdfObject, HashSet<int>> CodesByFont =
                new Dictionary<PdfObject, HashSet<int>>(PdfObjectIndex.ReferenceIdentityComparer);

            /// <summary>
            /// Fonts reachable from a stream that could not be decoded or tokenised. Their code set
            /// is a lower bound, not the answer, so they are never subsetted.
            /// </summary>
            internal readonly HashSet<PdfObject> Incomplete =
                new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            /// <summary>
            /// Streams that execute without an operator naming them — tiling patterns, soft-mask
            /// groups and Type 3 glyph procedures — are reached from a resource dictionary rather
            /// than from a `Do`, so they are scanned once per owning dictionary instead of once per
            /// invocation of it. This records which dictionaries that has already been done for.
            /// </summary>
            internal readonly HashSet<PdfObject> ScannedAuxiliary =
                new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            /// <summary>
            /// Which (stream, resources) pairs have already been scanned. The codes a content
            /// stream records are a function of exactly those two things, so scanning the same
            /// pair again cannot record anything new — it only repeats the tokenising pass and its
            /// allocation. The resource dictionary is part of the key because the same Form
            /// XObject invoked from two places may inherit different resources, and then the two
            /// scans genuinely differ.
            /// </summary>
            private readonly Dictionary<PdfObject, HashSet<PdfObject>> _scannedStreams =
                new Dictionary<PdfObject, HashSet<PdfObject>>(PdfObjectIndex.ReferenceIdentityComparer);

            /// <summary>
            /// Records that <paramref name="stream"/> is about to be scanned under
            /// <paramref name="resources"/>; false when that pair was already done, in which case
            /// the caller must not scan it again.
            /// </summary>
            internal bool MarkStreamScanned(PdfObject stream, PdfObject resources)
            {
                if (!_scannedStreams.TryGetValue(stream, out HashSet<PdfObject> under))
                {
                    under = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);
                    _scannedStreams[stream] = under;
                }
                return under.Add(resources);
            }

            /// <summary>
            /// True when the scan ran out of its traversal budget and stopped early. Every font's
            /// code set is then a lower bound, so nothing is subsetted — the whole program is
            /// embedded instead, which is larger but never drops a glyph.
            /// </summary>
            internal bool Truncated;

            internal HashSet<int> For(PdfObject fontDict)
            {
                if (!CodesByFont.TryGetValue(fontDict, out HashSet<int> codes))
                {
                    codes = new HashSet<int>();
                    CodesByFont[fontDict] = codes;
                }
                return codes;
            }
        }

        /// <summary>
        /// Walks every content stream that can reach a font — each page's content, the Form
        /// XObjects it invokes, the appearance streams of its annotations, the tiling patterns and
        /// soft-mask groups its resource dictionaries name, and any Type 3 glyph procedure —
        /// recording the codes each font draws. Reuses <see cref="ContentStreamScanner"/> — the same
        /// byte-accurate tokeniser the tagger splices with — so an inline image's payload can never
        /// be mistaken for text operators.
        ///
        /// <para>
        /// Completeness is the whole point: <see cref="FontEmbeddingRequest.UsedTextIsComplete"/> is
        /// what <see cref="TrySubset"/> takes as proof that a subset cannot drop a glyph. A stream
        /// this walk fails to reach is a glyph the subset silently loses, which is why every path a
        /// font can be drawn through has to be listed here and why running out of budget marks the
        /// whole scan <see cref="UsageScan.Truncated"/> rather than quietly returning less.
        /// </para>
        /// </summary>
        internal static UsageScan CollectUsedCharacterCodes(PdfDocument doc)
        {
            var scan = new UsageScan();

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report(Op,
                    "the page tree could not be read for the glyph-usage scan; no font will be subsetted", ex);
                scan.Truncated = true;
                return scan;
            }

            if (pages == null) return scan;

            var budget = new WalkBudget(MaxContentScanSteps);

            for (int i = 0; i < pages.Count; i++)
            {
                PdfDictionary pageDict;
                PdfDictionary resources;
                try
                {
                    pageDict  = pages[i].Dictionary;
                    resources = pages[i].Resources;
                }
                catch (Exception ex)
                {
                    // Truncated, not just skipped. The message below promises that these fonts
                    // "will not be subsetted", and a bare `continue` did not deliver it: the flag
                    // stayed false, UsedTextIsComplete stayed true, and TrySubset went ahead using
                    // a code set that is missing everything this page draws. A font shared with a
                    // page that DID scan would then be subsetted without that page's glyphs and
                    // render .notdef — the exact loss this scan's summary says it exists to
                    // prevent ("a stream this walk fails to reach is a glyph the subset silently
                    // loses"). The page-tree catch above already does this; only the per-page one
                    // did not.
                    scan.Truncated = true;
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " could not be read for the glyph-usage scan; no font will be subsetted", ex);
                    continue;
                }

                if (pageDict == null)
                {
                    // Same reasoning: a page that cannot be examined makes the whole scan partial.
                    scan.Truncated = true;
                    AccessibilityDiagnostics.Report(Op,
                        "page " + (i + 1).ToString(CultureInfo.InvariantCulture) +
                        " has no dictionary to scan; no font will be subsetted");
                    continue;
                }

                // A page may carry no /Resources of its own and still show text, because an
                // appearance stream brings its own — so the annotation walk is not conditional on it.
                if (resources != null)
                {
                    byte[] content = ContentStreamTagger.ConcatenatePageContent(doc, pageDict);
                    if (content == null)
                        MarkAllFontsIncomplete(doc, resources, scan, NewIdentitySet(), 0, budget);
                    else
                        ScanStream(doc, resources, content, scan, NewIdentitySet(), 0, budget);
                }

                ScanAnnotationAppearances(doc, pageDict, resources, scan, budget);

                if (budget.Exhausted) break;
            }

            if (budget.Exhausted)
            {
                scan.Truncated = true;
                AccessibilityDiagnostics.Report(Op,
                    "the glyph-usage scan exceeded its budget of " +
                    MaxContentScanSteps.ToString(CultureInfo.InvariantCulture) +
                    " tokens; no font is subsetted, so every embedded program is written whole");
            }

            return scan;
        }

        /// <summary>
        /// Scans the appearance streams of the page's annotations. A widget's <c>/AP /N</c> is a
        /// Form XObject nothing in the page content invokes, yet it draws text with a font the page
        /// resources usually also name — the AcroForm <c>Helv</c> pattern. Missing it subsets that
        /// font against the page's characters alone and blanks the field.
        /// </summary>
        private static void ScanAnnotationAppearances(
            PdfDocument doc,
            PdfDictionary pageDict,
            PdfDictionary pageResources,
            UsageScan scan,
            WalkBudget budget)
        {
            List<PdfStream> appearances =
                CollectAppearanceStreams(doc, pageDict, budget, out bool complete);

            // The annotation list itself was unreadable: whatever it draws with is unaccounted for,
            // so nothing reachable from this page may be subsetted.
            if (!complete && pageResources != null)
                MarkAllFontsIncomplete(doc, pageResources, scan, NewIdentitySet(), 0, budget);

            for (int i = 0; i < appearances.Count; i++)
            {
                if (budget.Exhausted) return;

                // A fresh path guard per appearance: each is an independent root, and
                // ScanNestedStream adds the stream to it, so an /AP whose own /Resources name it
                // back under /XObject is caught rather than recursing until the stack dies.
                ScanNestedStream(doc, appearances[i], pageResources, scan, NewIdentitySet(), 0,
                    budget, "an annotation appearance stream");
            }
        }

        private static void ScanStream(
            PdfDocument doc,
            PdfDictionary resources,
            byte[] content,
            UsageScan scan,
            HashSet<PdfObject> visitedStreams,
            int depth,
            WalkBudget budget)
        {
            if (content == null || content.Length == 0 || resources == null) return;
            if (budget.Exhausted) return;

            if (depth > MaxContentDepth)
            {
                MarkAllFontsIncomplete(doc, resources, scan, NewIdentitySet(), 0, budget);
                return;
            }

            // Charge for the tokenising pass BEFORE performing it. ScanDetailed is O(content.Length)
            // and pre-allocates a token array proportional to it, while the per-token loop below is
            // the only other place this budget is spent — so charged afterwards, a stream that
            // produces no tokens at all (pure whitespace, or nothing but `%` comments) would cost a
            // full pass, plus a large-object-heap array, for zero budget, and a page of `/X Do`
            // repeated N times would pay N units for N full passes over a stream bounded only by
            // MaxDecompressedBytes.
            if (!budget.Take(1 + content.Length / ContentScanBytesPerStep)) return;

            ContentScanResult scanned = ContentStreamScanner.ScanDetailed(content);
            if (!scanned.IsSafe)
            {
                // Either the tokeniser could not bound some construct (so operators after it are
                // guesses) or the stream was refused for exceeding a resource ceiling (so there are
                // no tokens at all — ContentScanResult.LimitExceeded). Both mean this stream's font
                // usage was not established, and both take the same conservative answer: every font
                // it can reach is marked incomplete rather than silently reported as unused.
                MarkAllFontsIncomplete(doc, resources, scan, NewIdentitySet(), 0, budget);
                return;
            }

            PdfDictionary fonts = ResolveDictionary(doc, resources[PdfName.Font]);
            PdfDictionary xobjects = ResolveDictionary(doc, resources[PdfName.XObject]);
            PdfDictionary extStates = ResolveDictionary(doc, resources[PdfName.ExtGState]);

            // Patterns, soft-mask groups and Type 3 glyph procedures are not invoked by an operator
            // this loop can watch for, so they are scanned from the dictionary that owns them —
            // once per dictionary, because the answer cannot depend on how it was reached. For the
            // same reason they get a path guard and a depth of their own: reusing this stream's
            // would let the one path that happened to reach the dictionary first decide, for the
            // whole document, that a pattern already on it is not worth scanning.
            if (scan.ScannedAuxiliary.Add(resources))
                ScanAuxiliaryStreams(doc, resources, scan, NewIdentitySet(), 0, budget);

            // The current font is part of the graphics state, so q/Q save and restore it. Tracking
            // the stack matters: without it, text drawn after a Q would be attributed to the font
            // the restored state replaced, and a subset built on that attribution drops glyphs.
            PdfDictionary current = null;
            var stack = new Stack<PdfDictionary>();

            for (int i = 0; i < scanned.Tokens.Count; i++)
            {
                if (!budget.Take()) return;

                ContentToken token = scanned.Tokens[i];
                switch (token.Operator)
                {
                    case PdfOperators.Save:
                        stack.Push(current);
                        break;

                    case PdfOperators.Restore:
                        if (stack.Count > 0) current = stack.Pop();
                        break;

                    case PdfOperators.SetFont:
                        current = null;
                        if (token.Operands.Count >= 2 && token.Operands[0] is PdfName fontName && fonts != null)
                            current = ResolveDictionary(doc, fonts[fontName]);
                        break;

                    case PdfOperators.ShowText:
                    case PdfOperators.NextLineShowText:
                        if (current != null && token.Operands.Count >= 1)
                            RecordCodes(scan, current, token.Operands[token.Operands.Count - 1]);
                        break;

                    case PdfOperators.NextLineSetSpacingShowText:
                        if (current != null && token.Operands.Count >= 3)
                            RecordCodes(scan, current, token.Operands[2]);
                        break;

                    case PdfOperators.ShowTextArray:
                        if (current != null && token.Operands.Count >= 1
                            && token.Operands[token.Operands.Count - 1] is PdfArray array)
                        {
                            for (int j = 0; j < array.Count; j++)
                                RecordCodes(scan, current, array[j]);
                        }
                        break;

                    case PdfOperators.InvokeXObject:
                        if (xobjects != null && token.Operands.Count >= 1
                            && token.Operands[0] is PdfName xobjectName)
                            ScanFormXObject(doc, resources, xobjects[xobjectName], scan,
                                visitedStreams, depth, budget);
                        break;

                    case PdfOperators.SetExtGState:
                    {
                        // ISO 32000-1 §8.4.5: an ExtGState's /Font entry is [font size] and sets the
                        // text font just as `Tf` does. Ignoring it attributes the text that follows
                        // to the previous font — or to none — and the real one loses its glyphs.
                        if (extStates == null || token.Operands.Count < 1
                            || !(token.Operands[0] is PdfName stateName)) break;

                        PdfDictionary gs = ResolveDictionary(doc, extStates[stateName]);
                        PdfArray gsFont = gs == null ? null : ResolveArray(doc, gs[PdfName.Font]);
                        if (gsFont == null || gsFont.Count == 0) break;

                        PdfDictionary gsFontDict = ResolveDictionary(doc, gsFont[0]);
                        if (gsFontDict != null) current = gsFontDict;
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Scans the streams a resource dictionary owns that no operator names: tiling patterns
        /// (selected by <c>scn</c> with a colour this walk does not model), soft-mask groups
        /// (executed by <c>gs</c>) and Type 3 glyph procedures (executed by a show-text operator on
        /// the font that owns them). Each is scanned unconditionally, which over-collects for a
        /// resource that is declared but never used — the safe direction, since an over-large subset
        /// wastes bytes while an under-large one loses text.
        /// </summary>
        private static void ScanAuxiliaryStreams(
            PdfDocument doc,
            PdfDictionary resources,
            UsageScan scan,
            HashSet<PdfObject> visitedStreams,
            int depth,
            WalkBudget budget)
        {
            PdfDictionary patterns = ResolveDictionary(doc, resources[PdfName.Pattern]);
            if (patterns != null)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in patterns)
                {
                    if (!budget.Take()) return;
                    if (!(doc.ResolveObject(kvp.Value) is PdfStream tiling)) continue;
                    if (GetInteger(doc, tiling[PdfName.PatternType]) != 1) continue;

                    ScanNestedStream(doc, tiling, resources, scan, visitedStreams, depth, budget,
                        "a tiling pattern");
                }
            }

            PdfDictionary extStates = ResolveDictionary(doc, resources[PdfName.ExtGState]);
            if (extStates != null)
            {
                foreach (KeyValuePair<PdfName, PdfObject> kvp in extStates)
                {
                    if (!budget.Take()) return;

                    PdfDictionary gs = ResolveDictionary(doc, kvp.Value);
                    // /SMask is /None on most states, which is a name, not a dictionary.
                    PdfDictionary smask = gs == null ? null : ResolveDictionary(doc, gs[PdfName.SMask]);
                    if (smask == null) continue;
                    if (!(doc.ResolveObject(smask[PdfName.G]) is PdfStream group)) continue;

                    ScanNestedStream(doc, group, resources, scan, visitedStreams, depth, budget,
                        "a soft-mask group");
                }
            }

            PdfDictionary fonts = ResolveDictionary(doc, resources[PdfName.Font]);
            if (fonts == null) return;

            foreach (KeyValuePair<PdfName, PdfObject> kvp in fonts)
            {
                if (!budget.Take()) return;

                PdfDictionary fontDict = ResolveDictionary(doc, kvp.Value);
                if (fontDict == null) continue;
                if (ResolveName(doc, fontDict[PdfName.Subtype])?.Value != "Type3") continue;
                if (!scan.ScannedAuxiliary.Add(fontDict)) continue;

                PdfDictionary charProcs = ResolveDictionary(doc, fontDict[PdfName.CharProcs]);
                if (charProcs == null) continue;

                PdfDictionary type3Resources =
                    ResolveDictionary(doc, fontDict[PdfName.Resources]) ?? resources;

                foreach (KeyValuePair<PdfName, PdfObject> proc in charProcs)
                {
                    if (!budget.Take()) return;
                    if (!(doc.ResolveObject(proc.Value) is PdfStream glyph)) continue;

                    ScanNestedStream(doc, glyph, type3Resources, scan, visitedStreams, depth, budget,
                        "a Type 3 glyph procedure");
                }
            }
        }

        private static void ScanFormXObject(
            PdfDocument doc,
            PdfDictionary parentResources,
            PdfObject xobjectRef,
            UsageScan scan,
            HashSet<PdfObject> visitedStreams,
            int depth,
            WalkBudget budget)
        {
            if (!(doc.ResolveObject(xobjectRef) is PdfStream form)) return;
            if (ResolveName(doc, form[PdfName.Subtype])?.Value != "Form") return;

            ScanNestedStream(doc, form, parentResources, scan, visitedStreams, depth, budget,
                "a Form XObject");
        }

        /// <summary>
        /// Scans one nested content stream — a Form XObject, an appearance stream, a tiling pattern,
        /// a soft-mask group or a Type 3 glyph procedure — under its own <c>/Resources</c>, falling
        /// back to the enclosing ones when it declares none.
        /// </summary>
        /// <param name="what">How the stream is named in a diagnostic, e.g. "a tiling pattern".</param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="stream">The nested content stream to scan.</param>
        /// <param name="fallbackResources">
        /// The enclosing <c>/Resources</c>, used when <paramref name="stream"/> declares none —
        /// which §7.8.3 makes an inheritance, not an empty set.
        /// </param>
        /// <param name="scan">Accumulates the fonts and code points found.</param>
        /// <param name="visitedStreams">
        /// Streams already scanned on this path, so a form that draws itself terminates.
        /// </param>
        /// <param name="depth">Current nesting depth, against the recursion cap.</param>
        /// <param name="budget">Aggregate work budget spanning the whole scan, not just this branch.</param>
        private static void ScanNestedStream(
            PdfDocument doc,
            PdfStream stream,
            PdfDictionary fallbackResources,
            UsageScan scan,
            HashSet<PdfObject> visitedStreams,
            int depth,
            WalkBudget budget,
            string what)
        {
            // A stream may invoke itself, directly or through a cycle of siblings.
            if (!visitedStreams.Add(stream)) return;

            try
            {
                PdfDictionary nested =
                    ResolveDictionary(doc, stream[PdfName.Resources]) ?? fallbackResources;

                // The codes this stream records are a function of its bytes and its resources, so
                // the second invocation of the same pair can only repeat work. The path guard above
                // stops recursion; it does NOT stop fan-out, because it is removed again on the way
                // out — a form invoked N times in sequence is entered N times, and each entry costs
                // a full decode-and-tokenise pass over a stream whose size the format does not
                // bound. Memoising here is what makes that linear in distinct streams rather than
                // in invocations.
                if (!scan.MarkStreamScanned(stream, nested)) return;

                // Decoded under what the scan can still pay to tokenise, stopped there: a whole decode
                // first would cost a stream's 512 MB ceiling, each, for bytes the budget then refuses.
                if (!stream.TryGetDecodedData((long)budget.Remaining * ContentScanBytesPerStep, out byte[] content, out string refused))
                {
                    // What it cost is the decoder's to know, and up to the ceiling: half of what is left
                    // is spent, so the refused decodes together cost no more than twice the budget.
                    budget.Take(budget.Remaining / 2);
                    AccessibilityDiagnostics.Report(Op,
                        what + "'s content could not be decoded for the glyph-usage scan (" + refused + "); the " +
                        "fonts it uses will not be subsetted");
                    content = null;
                }

                if (content == null)
                {
                    MarkAllFontsIncomplete(doc, nested, scan, NewIdentitySet(), 0, budget);
                    return;
                }

                ScanStream(doc, nested, content, scan, visitedStreams, depth + 1, budget);
            }
            finally
            {
                visitedStreams.Remove(stream);
            }
        }

        private static void RecordCodes(UsageScan scan, PdfDictionary fontDict, PdfObject operand)
        {
            if (!(operand is PdfString text)) return;
            byte[] raw = text.RawBytes;
            if (raw == null) return;

            HashSet<int> codes = scan.For(fontDict);
            for (int i = 0; i < raw.Length; i++) codes.Add(raw[i]);
        }

        /// <summary>
        /// Marks every font reachable from <paramref name="resources"/> (including through Form
        /// XObjects) as one whose glyph set is not provably complete, so it is embedded whole rather
        /// than subsetted.
        /// </summary>
        /// <remarks>
        /// <b>It shares the scan's <see cref="WalkBudget"/>, and must.</b> The depth cap and the
        /// per-call identity set bound one walk; neither bounds how many walks there are, and this
        /// is invoked with a <i>fresh</i> identity set from five sites — once per page whose content
        /// stream will not decode or will not tokenise safely. Such a page costs only one or two
        /// units of the content-scan budget (<c>ScanStream</c> charges before the scan, then falls
        /// into the <c>!IsSafe</c> branch), so the page loop never breaks, while each page triggers
        /// a complete re-walk of a resource graph the whole document may share. Twenty thousand
        /// pages over a thousand shared resource dictionaries is ~2×10^10 probes from a ~2 MB file.
        /// Its sibling <c>CollectFontsFrom</c> has carried a budget for exactly this shape from the
        /// start; this one simply did not. Running out sets <see cref="UsageScan.Truncated"/>, which
        /// is already the conservative answer — a truncated scan subsets nothing.
        /// </remarks>
        private static void MarkAllFontsIncomplete(
            PdfDocument doc,
            PdfDictionary resources,
            UsageScan scan,
            HashSet<PdfObject> visitedResources,
            int depth,
            WalkBudget budget)
        {
            // The traversal is WalkFontResources, shared with CollectFontsFrom.
            WalkFontResources(doc, resources, visitedResources, depth, budget,
                (fontDict, _) => scan.Incomplete.Add(fontDict));
        }
    }
}
