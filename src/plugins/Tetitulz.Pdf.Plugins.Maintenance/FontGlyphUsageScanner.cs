// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// Works out which glyphs of each embedded TrueType font program the document actually draws.
    ///
    /// <para><b>This is the dangerous half of font subsetting, so it fails closed.</b> Pruning a
    /// glyph the document does draw produces a blank where a character used to be — a corruption
    /// that survives every structural check, because the file stays valid PDF. Every uncertainty is
    /// therefore resolved against pruning:</para>
    /// <list type="bullet">
    ///   <item>A font whose character codes cannot be mapped to glyph IDs <i>blocks</i> its font
    ///     program, even if some other font maps onto the same program and was understood.</item>
    ///   <item>A <c>Tf</c> naming a font that will not resolve marks the whole scan incomplete,
    ///     which disables subsetting for the entire document rather than for that font — the
    ///     unresolved name could have been any of them.</item>
    ///   <item>A font that is never seen drawing anything is absent from the result and is left
    ///     alone. Absence of evidence is not evidence of absence.</item>
    /// </list>
    ///
    /// <para><b>Which fonts are understood.</b> Only Type0 fonts with an <c>Identity-H</c> or
    /// <c>Identity-V</c> encoding over a <c>CIDFontType2</c> descendant, whose <c>/CIDToGIDMap</c>
    /// is <c>/Identity</c> or a readable stream. That is the shape large embedded fonts overwhelmingly
    /// take, and it is the one where a code maps to a glyph by arithmetic rather than by the
    /// multi-step name-and-cmap search §9.6.6.4 prescribes for simple fonts. A simple TrueType font
    /// is deliberately blocked rather than guessed at: it can address at most 256 glyphs, so it is
    /// rarely the size problem that motivates subsetting, and implementing §9.6.6.4 incorrectly is
    /// how the wrong glyphs get deleted.</para>
    ///
    /// <para><b>Where content streams live.</b> Text is not only on the page: it is also inside form
    /// XObjects, tiling patterns, soft-mask groups, Type 3 glyph procedures and annotation
    /// appearance streams. All of those are walked, because a glyph drawn only from an appearance
    /// stream is still a glyph the document draws.</para>
    /// </summary>
    internal static class FontGlyphUsageScanner
    {
        private const int MaxDepth = 16;
        private const int MaxOperations = 2_000_000;

        /// <summary>
        /// Decoded bytes one content stream may hold before the scan refuses it and fails closed.
        /// </summary>
        /// <remarks>
        /// <see cref="MaxOperations"/> bounds work per operation, which is only spent once
        /// <c>ContentStreamParser.Parse</c> has materialised the entire list; the operator-to-byte
        /// ratio is the file's to choose. This bounds the allocation itself, well below the 64 MiB
        /// <c>OptimizationOptions.MaxStreamDecodeBytes</c>.
        /// </remarks>
        private const int MaxContentBytesPerStream = 8 * 1024 * 1024;

        /// <summary>
        /// The fewest bytes a content stream can spend on one operation: a one-character operator
        /// and the separator that ends it. Used to refuse a stream the operator budget could not
        /// walk to its end anyway.
        /// </summary>
        private const int MinBytesPerOperation = 2;

        /// <summary>
        /// Bytes of content this scan may tokenize in all, across every walk. The per-operation budget
        /// is spent only on parsed operations, so a stream of white space, comments or bare operands
        /// parses to nothing and costs nothing — and a form invoked once per <c>Do</c> is re-tokenized
        /// each time for free: a page of 600 000 <c>/F Do</c> over a two-megabyte whitespace form
        /// tokenized about 1.2 TB while spending a fraction of the operation budget. A separate byte
        /// budget bounds the tokenizing without touching the per-operation accounting that decides how
        /// far a single stream is walked.
        /// </summary>
        private const long MaxTokenizedBytes = 512L * 1024 * 1024;

        internal sealed class Result
        {
            /// <summary>Font-program (<c>/FontFile2</c>) object number → glyph IDs drawn from it.</summary>
            internal readonly Dictionary<int, HashSet<int>> UsedGlyphs = new Dictionary<int, HashSet<int>>();

            /// <summary>Font programs reached by at least one font this scanner cannot map.</summary>
            internal readonly HashSet<int> Blocked = new HashSet<int>();

            /// <summary>
            /// Programs whose <see cref="UsedGlyphs"/> entries are <b>CIDs, not glyph indices</b> —
            /// the <c>CIDFontType0</c> (CFF) case.
            /// </summary>
            /// <remarks>
            /// <b>The two numbering systems must never be confused, and nothing else in this result
            /// distinguishes them.</b> A <c>CIDFontType2</c> reaches its glyphs through
            /// <c>/CIDToGIDMap</c>, which this scanner reads, so its recorded values are already
            /// glyph indices. A <c>CIDFontType0</c>'s CIDs go through the <b>CFF charset</b>, which
            /// lives inside the font program and which a plugin cannot read — so the CIDs are
            /// recorded raw and the caller resolves them with
            /// <c>PdfFontSubsetter.TryMapCidsToCffGlyphs</c>. Treating one set as the other blanks
            /// the wrong glyphs on any font whose charset is not the identity.
            /// </remarks>
            internal readonly HashSet<int> CidKeyedPrograms = new HashSet<int>();

            /// <summary>
            /// Programs whose entries are resolved glyph indices — the <c>CIDFontType2</c> case.
            /// Kept only so the disagreement check below can see both orderings.
            /// </summary>
            internal readonly HashSet<int> GlyphIndexPrograms = new HashSet<int>();

            /// <summary>Non-null when the scan could not be completed; subsetting must then be skipped.</summary>
            internal string Reason;

            /// <summary>Content bytes this scan may still tokenize, across every walk (<see cref="MaxTokenizedBytes"/>).</summary>
            internal long TokenizeBytesLeft = MaxTokenizedBytes;

            /// <summary>
            /// Annotation appearances this scan may still visit, across every page
            /// (<see cref="PdfLimits.MaxAnnotationsPerDocument"/>). Pages, a page's <c>/Annots</c> and an
            /// annotation's <c>/AP</c> states can every one be a shared object, so their product is what
            /// bounds the walk, not any one factor.
            /// </summary>
            internal int AnnotationAppearancesLeft = PdfLimits.MaxAnnotationsPerDocument;

            /// <summary>Appearance stream object numbers already walked — a shared stream draws the same glyphs whoever points at it.</summary>
            internal readonly HashSet<int> SeenAppearanceStreams = new HashSet<int>();

            /// <summary>
            /// Fonts written directly in a <c>/Font</c> dictionary, by identity: they have no object number
            /// for the font cache, so each <c>Tf</c> naming one analysed it — and walked a Type 3 font's
            /// procedures — again.
            /// </summary>
            internal readonly Dictionary<object, FontInfo> DirectFonts =
                new Dictionary<object, FontInfo>(PdfReferenceEquality.Instance);

            /// <summary>
            /// Type 3 <c>/CharProcs</c> dictionaries walked, each with the <c>/Resources</c> dictionaries
            /// it was walked under, by identity. What a procedure draws depends on those two alone, so
            /// fonts that share them walk them once.
            /// </summary>
            internal readonly Dictionary<object, HashSet<object>> WalkedCharProcs =
                new Dictionary<object, HashSet<object>>(PdfReferenceEquality.Instance);

            /// <summary>How many Type 3 procedure walks the scan began — the evidence that a shared set is walked once.</summary>
            internal int Type3ProcedureWalks;

            /// <summary>How many font dictionaries the scan analysed — the evidence that <see cref="DirectFonts"/> caches.</summary>
            internal int FontsAnalysed;
        }

        /// <summary>Stands for an absent <c>/Resources</c> in <see cref="Result.WalkedCharProcs"/>.</summary>
        private static readonly object NoResources = new object();

        internal static Result Scan(Dictionary<int, PdfObject> objects, PdfDictionary trailer,
            OptimizationOptions options)
        {
            var result = new Result();
            int budget = MaxOperations;
            var fontCache = new Dictionary<int, FontInfo>();

            if (!(Deref(trailer?[PdfName.Root], objects) is PdfDictionary catalog))
            {
                result.Reason = "the document has no resolvable catalog";
                return result;
            }

            string formBlocker = InteractiveFormBlocker(catalog, objects, ref budget);
            if (formBlocker != null)
            {
                result.Reason = formBlocker;
                return result;
            }

            foreach (var entry in PageTreeWalk.Enumerate(objects, catalog))
            {
                PdfDictionary page = entry.Page;
                PdfDictionary resources =
                    Deref(page[PdfName.Resources], objects) as PdfDictionary ?? entry.InheritedResources;

                byte[] content = ConcatenatePageContent(page, objects, options, result, budget, out string refusal);
                if (refusal != null)
                {
                    result.Reason = refusal;
                    break;
                }
                if (content != null && content.Length > 0)
                {
                    WalkGuarded(content, resources, objects, options, result, fontCache,
                        new HashSet<int>(), 0, inherited: null, ref budget);
                }

                ScanAnnotations(page, resources, objects, options, result, fontCache, ref budget);

                if (budget <= 0)
                {
                    result.Reason = "the operator budget was exhausted";
                    break;
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        //  Interactive form (§12.7) — glyphs a viewer draws that no stream holds
        // ------------------------------------------------------------------

        /// <summary>
        /// Why an interactive form makes this document unsafe to subset, or <c>null</c> when it
        /// does not.
        /// </summary>
        /// <remarks>
        /// <para>The walk below reads content streams: page content, form XObjects, patterns, soft
        /// masks, Type 3 procedures and annotation <c>/AP</c> streams. A field's <b>value</b> is in
        /// none of them. §12.7.3.3: when <c>/NeedAppearances</c> is true the viewer <i>regenerates</i>
        /// each widget's appearance from its <c>/DA</c> and <c>/V</c>, so the <c>/AP</c> this scanner
        /// reads is stale and is not what gets drawn — and the same is true of a field that has a
        /// <c>/V</c> and no <c>/AP</c> at all. The glyphs of "Müller" would then be pruned out of a
        /// font program the page body happens to use for ASCII only, and the field would paint
        /// blanks.</para>
        /// <para>Refusing is the answer consistent with the rest of this type: every uncertainty
        /// here is resolved against pruning, and the caller is <i>told</i> (<see cref="Result.Reason"/>
        /// reaches <c>PdfOptimizeResult.Warnings</c>) rather than handed a quietly damaged font.
        /// Reading the value's code points and the <c>/DA</c> font through <c>/DR /Font</c> would let
        /// such a document be subset safely; that is a larger change and is not attempted here.</para>
        /// <para>Bounded like every other walk in this file: a visited set over object numbers (a
        /// field tree may be a diamond as well as a cycle), the shared <see cref="MaxDepth"/>, and
        /// the caller's operation budget spent per node examined.</para>
        /// </remarks>
        private static string InteractiveFormBlocker(PdfDictionary catalog,
            Dictionary<int, PdfObject> objects, ref int budget)
        {
            if (!(Deref(catalog[PdfName.AcroForm], objects) is PdfDictionary form)) return null;

            if (Deref(form[PdfName.NeedAppearances], objects) is PdfBoolean needs && needs.Value)
                return "the interactive form sets /NeedAppearances, so a viewer regenerates every " +
                       "field appearance from /DA and /V — glyphs no content stream in this document " +
                       "contains, and which subsetting would therefore prune away";

            if (!(Deref(form[PdfName.Fields], objects) is PdfArray fields)) return null;

            var pending = new Stack<(PdfObject Node, bool ValueInherited, int Depth)>();
            for (int i = fields.Count - 1; i >= 0; i--) pending.Push((fields[i], false, 0));
            var visited = new HashSet<int>();

            while (pending.Count > 0)
            {
                if (--budget <= 0) return "the operator budget was exhausted";

                var (node, valueInherited, depth) = pending.Pop();
                // Each of these is skipped with a reason, as everywhere in this file: a field below the
                // depth or behind a reference the map lacks may be one with a value and no appearance.
                if (depth > MaxDepth)
                    return "the form's field tree nests more than " + MaxDepth + " levels deep, so the " +
                           "fields below were not examined for values a viewer draws";
                if (node is PdfReference reference)
                {
                    if (!visited.Add(reference.ReferenceObjectNumber)) continue;
                    if (!objects.ContainsKey(reference.ReferenceObjectNumber))
                        return "a form field (object " + reference.ReferenceObjectNumber + ") could not be " +
                               "read, so whether a viewer draws its value is unknown";
                }
                if (!(Deref(node, objects) is PdfDictionary field)) continue;

                bool hasValue = valueInherited ||
                                !(Deref(field[PdfName.V], objects) is null or PdfNull);

                if (Deref(field[PdfName.Kids], objects) is PdfArray kids && kids.Count > 0)
                {
                    for (int i = kids.Count - 1; i >= 0; i--)
                        pending.Push((kids[i], hasValue, depth + 1));
                    continue;
                }

                // A terminal field, which is also its own widget when it has no /Kids.
                if (hasValue && !(Deref(field[PdfName.AP], objects) is PdfDictionary))
                    return ValueWithoutAppearance;
            }

            // A widget a page lists but the field tree does not reach is drawn all the same, and its
            // value is inherited up /Parent (§12.7.3.1), which the descent above never climbs.
            foreach (var entry in PageTreeWalk.Enumerate(objects, catalog))
            {
                if (!(Deref(entry.Page[PdfName.Annots], objects) is PdfArray annots)) continue;
                for (int i = 0; i < annots.Count; i++)
                {
                    if (--budget <= 0) return "the operator budget was exhausted";
                    if (!(Deref(annots[i], objects) is PdfDictionary widget)) continue;
                    if (!(Deref(widget[PdfName.Subtype], objects) is PdfName subtype) || subtype.Value != "Widget")
                        continue;
                    if (Deref(widget[PdfName.AP], objects) is PdfDictionary) continue;

                    PdfObject value = AcroFieldInheritance.Find(widget, PdfName.V,
                        d => Deref(d[PdfName.Parent], objects) as PdfDictionary, o => Deref(o, objects),
                        null, MaxFieldAncestors, ref budget, out bool capExhausted);
                    if (capExhausted)
                        return "a widget's /Parent chain is longer than " + MaxFieldAncestors + " fields, so " +
                               "whether a viewer draws a value for it is unknown";
                    if (value != null) return ValueWithoutAppearance;
                }
            }

            return null;
        }

        private const string ValueWithoutAppearance =
            "a form field has a value (/V) but no appearance stream (/AP), so a " +
            "viewer draws it from /DA and /V — glyphs no content stream in this " +
            "document contains, and which subsetting would therefore prune away";

        /// <summary>Ceiling on a widget's <c>/Parent</c> climb for an inherited <c>/V</c> (a ring's only guard).</summary>
        private const int MaxFieldAncestors = 64;

        // ------------------------------------------------------------------
        //  Content walking
        // ------------------------------------------------------------------

        private static void WalkGuarded(byte[] content, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth,
            FontInfo inherited, ref int budget)
        {
            try
            {
                Walk(content, resources, objects, options, result, fontCache, inProgress, depth,
                    inherited, ref budget);
            }
            catch (Exception ex)
            {
                result.Reason = "a content stream could not be walked: " + ex.GetType().Name;
            }
        }

        /// <summary>
        /// <paramref name="inherited"/> is the font in force at the invocation. §8.10.1 executes a
        /// form XObject in the graphics state of its caller, and the text font is part of that
        /// state — so a form that draws text without a <c>Tf</c> of its own is drawing with the
        /// caller's font. Starting each nested stream at "no font" instead would leave those glyphs
        /// unrecorded, and unrecorded glyphs are exactly the ones that get pruned away.
        /// </summary>
        private static void Walk(byte[] content, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth,
            FontInfo inherited, ref int budget)
        {
            if (content.Length > MaxContentBytesPerStream)
            {
                // Refused BEFORE parsing, because the budget below is spent per operation and
                // therefore cannot fire until the whole operation list exists: 64 MiB of `q Q q Q`
                // is ~16 million operation objects, on the order of a gigabyte of managed
                // allocation, per page. Fails closed — an unscanned stream means unaccounted-for
                // glyphs, which is exactly what must not be pruned.
                result.Reason =
                    "a content stream of " + content.Length + " bytes exceeds the " +
                    MaxContentBytesPerStream + "-byte scan ceiling, so the glyphs it draws are " +
                    "unaccounted for";
                return;
            }

            // Second, narrower pre-parse refusal, and it is NOT redundant with the ceiling above.
            // The fixed ceiling bounds the allocation; this bounds it against the budget actually
            // left, which is what makes "refused before parsing" true for a stream well under
            // 8 MiB. Two bytes is the least one operation can occupy (a one-character operator and
            // its separator), so a stream longer than twice the remaining budget cannot be walked
            // to its end whatever it holds — refusing it here costs the scan no evidence it could
            // have used, and like every other uncertainty in this type it blocks pruning.
            if (content.LongLength > (long)budget * MinBytesPerOperation)
            {
                result.Reason = "a content stream of " + content.LongLength +
                    " bytes is larger than the remaining operator budget could walk";
                return;
            }

            // Charge the tokenizing to a budget of its own, so the same stream re-walked many times
            // cannot be re-parsed for free (a sparse stream costs no operations): the sum of all walks'
            // bytes is bounded, and the per-operation budget that decides how far one stream is walked is
            // untouched.
            if (content.LongLength > result.TokenizeBytesLeft)
            {
                result.Reason = "the content streams reached together are larger than the scan can tokenize";
                return;
            }
            result.TokenizeBytesLeft -= content.LongLength;

            var operations = ContentStreamParser.Parse(content);

            FontInfo current = inherited;
            var fontStack = new Stack<FontInfo>();

            foreach (var op in operations)
            {
                if (--budget <= 0) return;

                switch (op.Operator)
                {
                    case "q":
                        fontStack.Push(current);
                        break;

                    case "Q":
                        if (fontStack.Count > 0) current = fontStack.Pop();
                        break;

                    case "Tf":
                        current = ResolveFontOperand(op, resources, objects, options, result,
                            fontCache, inProgress, depth, ref budget);
                        break;

                    case "Tj":
                        if (op.Operands.Length >= 1)
                            RecordString(op.Operands[0], current, result);
                        break;

                    case "'":
                        if (op.Operands.Length >= 1)
                            RecordString(op.Operands[0], current, result);
                        break;

                    case "\"":
                        // aw ac string
                        if (op.Operands.Length >= 3)
                            RecordString(op.Operands[2], current, result);
                        break;

                    case "TJ":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfArray array)
                        {
                            for (int i = 0; i < array.Count; i++)
                                RecordString(array[i], current, result);
                        }
                        break;

                    case "Do":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName xobjectName)
                        {
                            WalkNestedStream(
                                Lookup(resources, PdfName.XObject, xobjectName, objects) as PdfStream,
                                LookupReference(resources, PdfName.XObject, xobjectName, objects),
                                resources, objects, options, result, fontCache, inProgress, depth,
                                current, ref budget, role: NestedStreamRole.XObjectInvocation);
                        }
                        break;

                    case "gs":
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName gsName)
                        {
                            WalkSoftMask(gsName, resources, objects, options, result, fontCache,
                                inProgress, depth, current, ref budget);
                            current = ResolveGraphicsStateFont(gsName, current, resources, objects, options,
                                result, fontCache, inProgress, depth, ref budget);
                        }
                        break;

                    case "scn":
                    case "SCN":
                        if (op.Operands.Length >= 1 &&
                            op.Operands[op.Operands.Length - 1] is PdfName patternName)
                        {
                            WalkNestedStream(
                                Lookup(resources, PdfName.Pattern, patternName, objects) as PdfStream,
                                LookupReference(resources, PdfName.Pattern, patternName, objects),
                                resources, objects, options, result, fontCache, inProgress, depth,
                                current, ref budget, role: NestedStreamRole.PatternOrDirect);
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Walks a form XObject or a tiling pattern: both are content streams carrying their own
        /// <c>/Resources</c>, and both can legally reference themselves, directly or in a chain.
        ///
        /// <para><b>Every refusal to walk sets <see cref="Result.Reason"/>.</b> A stream this method
        /// declines — too deep, re-entrant, or undecodable — may be showing text, and text it did
        /// not see is text whose glyphs the pruner will delete. Silent refusals are also what made
        /// the <c>/CharProcs</c> walk in <see cref="ResolveFontOperand"/> unrepeatable: those procs
        /// are walked once, on the cache miss that first analyses the Type 3 font, so a walk refused
        /// there never happens again for that font at any depth. It is safe to walk them exactly
        /// once — a glyph procedure's content and resources do not depend on where it was invoked
        /// from, and the font in force inside it is always the Type 3 font itself — but only if a
        /// refusal is visible, which is what this does.</para>
        /// </summary>
        private static void WalkNestedStream(PdfStream stream, int objectNumber,
            PdfDictionary parentResources, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, Result result, Dictionary<int, FontInfo> fontCache,
            HashSet<int> inProgress, int depth, FontInfo inherited, ref int budget, NestedStreamRole role)
        {
            if (stream == null) return;

            if (depth >= MaxDepth)
            {
                result.Reason = "a content stream nested more than " + MaxDepth +
                    " levels deep was not walked";
                return;
            }

            // TWO QUESTIONS, TWO ANSWERS. `Do` may legitimately name an image, which has no content
            // stream and is skipped in silence; a soft mask's /G must be a form (§11.6.5.2 Table 144).
            // Applying the `Do` rule to soft masks would skip a group with no /Subtype without a
            // reason — its glyphs absent from UsedGlyphs while Reason stays null, so PdfOptimizer
            // would treat the scan as complete and TrueTypeGlyphPruner would blank glyphs the file draws.
            if (role == NestedStreamRole.XObjectInvocation)
            {
                if (!ContentScanGraph.ShouldWalkXObject(stream, objects, out string refusal))
                {
                    if (refusal != null) result.Reason = refusal;
                    return;
                }
            }
            else if (role == NestedStreamRole.SoftMaskGroup)
            {
                if (!ContentScanGraph.ShouldWalkSoftMaskGroup(stream, objects, out string refusal))
                {
                    if (refusal != null) result.Reason = refusal;
                    return;
                }
            }
            else
            {
                // A shading pattern (PatternType 2) has no content stream to walk.
                var patternType = Deref(stream[PdfName.PatternType], objects) as PdfNumber;
                if (patternType != null && patternType.IntValue != 1) return;
            }

            if (objectNumber >= 0 && !inProgress.Add(objectNumber))
            {
                result.Reason = "object " + objectNumber + " draws itself, directly or through a " +
                    "chain, so its content could not be walked in full";
                return;
            }

            try
            {
                if (!stream.TryGetDecodedData(options.MaxStreamDecodeBytes, out byte[] inner))
                {
                    result.Reason = "a nested content stream could not be decoded";
                    return;
                }
                if (inner == null || inner.Length == 0) return;

                var innerResources = Deref(stream[PdfName.Resources], objects) as PdfDictionary
                                     ?? parentResources;

                WalkGuarded(inner, innerResources, objects, options, result, fontCache, inProgress,
                    depth + 1, inherited, ref budget);
            }
            finally
            {
                if (objectNumber >= 0) inProgress.Remove(objectNumber);
            }
        }

        private static void WalkSoftMask(PdfName gsName, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth,
            FontInfo inherited, ref int budget)
        {
            if (!(Lookup(resources, PdfName.ExtGState, gsName, objects) is PdfDictionary gs)) return;
            if (!(Deref(gs[PdfName.SMask], objects) is PdfDictionary smask)) return;

            var group = smask[PdfName.G];
            int number = group is PdfReference reference ? reference.ReferenceObjectNumber : -1;
            WalkNestedStream(Deref(group, objects) as PdfStream, number, resources, objects, options,
                result, fontCache, inProgress, depth, inherited, ref budget, role: NestedStreamRole.SoftMaskGroup);
        }

        private static void ScanAnnotations(PdfDictionary page, PdfDictionary pageResources,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, ref int budget)
        {
            foreach (var (stream, number, _) in
                     ContentScanGraph.AnnotationAppearanceStreams(page, objects))
            {
                // Pages × /Annots entries × /AP states, all able to share one object, multiplied without
                // bound (a 90 KB file reached 10^9 walks, each allocating a cycle set, spending no budget
                // on an empty stream). Charge every appearance to the document-wide cap, and walk a shared
                // stream's glyphs once. The cap failing closed (Reason set) is correct: a glyph used only
                // in an appearance left unscanned must not be pruned.
                if (result.AnnotationAppearancesLeft <= 0)
                {
                    result.Reason ??= "more than " + PdfLimits.MaxAnnotationsPerDocument +
                        " annotation appearances were referenced; the rest were not scanned";
                    return;
                }
                result.AnnotationAppearancesLeft--;
                if (number >= 0 && !result.SeenAppearanceStreams.Add(number)) continue;

                // An appearance stream is drawn on its own, not from page content, so it inherits
                // no font: it must set its own.
                WalkNestedStream(stream, number, pageResources, objects, options, result,
                    fontCache, new HashSet<int>(), 0, inherited: null, ref budget,
                    role: NestedStreamRole.PatternOrDirect);

                if (budget <= 0) return;
            }
        }

        // ------------------------------------------------------------------
        //  Fonts
        // ------------------------------------------------------------------

        private static FontInfo ResolveFontOperand(ContentStreamOperation op,
            PdfDictionary resources, Dictionary<int, PdfObject> objects, OptimizationOptions options,
            Result result, Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth,
            ref int budget)
        {
            if (op.Operands.Length < 1 || !(op.Operands[0] is PdfName fontName))
            {
                result.Reason = "a Tf operator named no font";
                return null;
            }

            int objectNumber = LookupReference(resources, PdfName.Font, fontName, objects);
            var fontDict = Lookup(resources, PdfName.Font, fontName, objects) as PdfDictionary;

            if (fontDict == null)
            {
                // The named font is not reachable, so any text drawn with it cannot be attributed.
                // That could be any font program in the file, so the whole scan is untrustworthy.
                result.Reason = "font resource /" + fontName.Value + " could not be resolved";
                return null;
            }

            return ResolveFont(fontDict, objectNumber, objects, options, result, fontCache, inProgress, depth,
                ref budget);
        }

        /// <summary>
        /// The font an ExtGState's <c>/Font</c> entry selects (§8.4.5 Table 58: <c>[font size]</c>, the
        /// font an indirect reference), or <paramref name="current"/> when the state sets none. Both
        /// renderers honour the entry, so text shown after the <c>gs</c> is drawn in that font; reading
        /// past it would leave those glyphs out of <see cref="Result.UsedGlyphs"/> for the pruner to
        /// remove. A state that does not resolve could select any font, so the scan is refused, as for
        /// a <c>Tf</c> naming one that does not.
        /// </summary>
        private static FontInfo ResolveGraphicsStateFont(PdfName gsName, FontInfo current, PdfDictionary resources,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth, ref int budget)
        {
            if (!(Lookup(resources, PdfName.ExtGState, gsName, objects) is PdfDictionary gs))
            {
                result.Reason = "graphics state /" + gsName.Value + " could not be resolved, so the font " +
                    "it may select is unknown";
                return current;
            }
            if (!(Deref(gs[PdfName.Font], objects) is PdfArray entry)) return current;

            PdfObject fontObject = entry.Count > 0 ? entry[0] : null;
            if (!(Deref(fontObject, objects) is PdfDictionary fontDict))
            {
                result.Reason = "graphics state /" + gsName.Value + " selects a font that could not be resolved";
                return null;
            }
            int objectNumber = fontObject is PdfReference r ? r.ReferenceObjectNumber : -1;
            return ResolveFont(fontDict, objectNumber, objects, options, result, fontCache, inProgress, depth,
                ref budget);
        }

        /// <summary>A resolved font dictionary's analysis, cached, with a Type 3 font's procedures walked.</summary>
        private static FontInfo ResolveFont(PdfDictionary fontDict, int objectNumber,
            Dictionary<int, PdfObject> objects, OptimizationOptions options, Result result,
            Dictionary<int, FontInfo> fontCache, HashSet<int> inProgress, int depth, ref int budget)
        {
            FontInfo info = null;
            bool cached = objectNumber >= 0
                ? fontCache.TryGetValue(objectNumber, out info)
                : result.DirectFonts.TryGetValue(fontDict, out info);
            if (!cached)
            {
                info = AnalyzeFont(fontDict, objects, options);
                result.FontsAnalysed++;
                if (objectNumber >= 0) fontCache[objectNumber] = info;
                else result.DirectFonts[fontDict] = info;

                if (info.FontFileObject >= 0 && !info.Analyzable)
                    result.Blocked.Add(info.FontFileObject);
            }
            else if (info.Type3CharProcs == null || info.Type3ProcsWalked)
            {
                return info;
            }

            // A Type 3 glyph procedure is a content stream, and it may draw text with other fonts.
            //
            // WALKED ON A CACHE HIT TOO when the first attempt did not get to run: the walk is a
            // no-op at the depth cap or inside the /CharProcs it is already walking, so a Type 3 font
            // first selected at depth 16 would otherwise never have its procedures examined, and the
            // glyphs they draw would be pruned out of a program the font never appeared to use.
            if (info.Type3CharProcs != null)
            {
                // Another font sharing these procedures and these resources has walked them whole.
                object resourcesKey = (object)info.Type3Resources ?? NoResources;
                if (result.WalkedCharProcs.TryGetValue(info.Type3CharProcs, out HashSet<object> under)
                    && under.Contains(resourcesKey))
                {
                    info.Type3ProcsWalked = true;
                    return info;
                }

                result.Type3ProcedureWalks++;
                bool walked = false;
                foreach (var key in info.Type3CharProcs.Keys)
                {
                    var entry = info.Type3CharProcs[key];
                    if (!(Deref(entry, objects) is PdfStream proc))
                    {
                        result.Reason = "glyph procedure /" + key.Value +
                            " of a Type 3 font is not a stream";
                        continue;
                    }
                    int number = entry is PdfReference r ? r.ReferenceObjectNumber : -1;

                    // §9.6.5 runs a glyph procedure in the current graphics state, where the font in
                    // force is this Type 3 font itself.
                    if (depth >= MaxDepth || (number >= 0 && inProgress.Contains(number)))
                    {
                        // Fail closed: the procedure was NOT examined, so any glyph it draws is
                        // unaccounted for and pruning on this evidence would delete it.
                        result.Reason =
                            "a Type 3 glyph procedure could not be walked (nesting depth " +
                            depth + " of " + MaxDepth + ", or the procedure is already being " +
                            "walked), so the glyphs it draws are unaccounted for";
                        continue;
                    }

                    WalkNestedStream(proc, number, info.Type3Resources, objects, options, result,
                        fontCache, inProgress, depth, info, ref budget, role: NestedStreamRole.PatternOrDirect);
                    walked = true;
                    if (budget <= 0) return info;
                }
                if (walked)
                {
                    info.Type3ProcsWalked = true;
                    if (under == null)
                    {
                        under = new HashSet<object>(PdfReferenceEquality.Instance);
                        result.WalkedCharProcs[info.Type3CharProcs] = under;
                    }
                    under.Add(resourcesKey);
                }
            }

            return info;
        }

        private static FontInfo AnalyzeFont(PdfDictionary font, Dictionary<int, PdfObject> objects,
            OptimizationOptions options)
        {
            var info = new FontInfo();
            var subtype = Deref(font[PdfName.Subtype], objects) as PdfName;

            if (subtype != null && subtype.Value == "Type3")
            {
                info.Type3CharProcs = Deref(font[PdfName.CharProcs], objects) as PdfDictionary;
                info.Type3Resources = Deref(font[PdfName.Resources], objects) as PdfDictionary;
                return info;   // no font program of its own
            }

            if (subtype == null || subtype.Value != "Type0")
            {
                // A simple font: record its program so it can be blocked, but do not map it.
                info.FontFileObject = FindFontFile(Deref(font[PdfName.FontDescriptor], objects)
                    as PdfDictionary, objects);
                return info;
            }

            var descendants = Deref(font[PdfName.DescendantFonts], objects) as PdfArray;
            if (descendants == null || descendants.Count < 1) return info;
            if (!(Deref(descendants[0], objects) is PdfDictionary descendant)) return info;

            var descriptor = Deref(descendant[PdfName.FontDescriptor], objects) as PdfDictionary;
            info.FontFileObject = FindFontFile(descriptor, objects);
            if (info.FontFileObject < 0) return info;

            var descendantSubtype = Deref(descendant[PdfName.Subtype], objects) as PdfName;
            if (descendantSubtype == null) return info;
            bool isCidFontType0 = descendantSubtype.Value == "CIDFontType0";
            if (!isCidFontType0 && descendantSubtype.Value != "CIDFontType2") return info;

            // Only the Identity CMaps are understood: there the two-byte code is the CID outright.
            var encoding = Deref(font[PdfName.Encoding], objects) as PdfName;
            if (encoding == null) return info;
            if (encoding.Value != "Identity-H" && encoding.Value != "Identity-V") return info;

            if (isCidFontType0)
            {
                // A CIDFontType0's descendant has no /CIDToGIDMap — §9.7.4.2 says the entry is for
                // CIDFontType2 only, and the CID reaches a glyph through the CFF charset instead.
                // A file that carries one anyway is describing a mapping that will not be applied,
                // so it is refused rather than read: whichever of the two a reader honours, this
                // scanner would be recording the other one's answer.
                if (Deref(descendant[PdfName.CIDToGIDMap], objects) is PdfName explicitMap &&
                    explicitMap.Value != "Identity")
                    return info;

                info.CidToGid = null;              // recorded values stay raw CIDs
                info.CidKeyedProgram = true;
                info.Analyzable = true;
                return info;
            }

            var cidToGid = Deref(descendant[PdfName.CIDToGIDMap], objects);
            if (cidToGid is PdfName mapName)
            {
                if (mapName.Value != "Identity") return info;
                info.CidToGid = null;                       // identity
            }
            else if (cidToGid is PdfStream mapStream)
            {
                if (!mapStream.TryGetDecodedData(options.MaxStreamDecodeBytes, out byte[] map))
                    return info;
                if (map == null) return info;
                info.CidToGid = map;
            }
            else if (cidToGid != null)
            {
                return info;                                 // some other type: not understood
            }

            info.Analyzable = true;
            return info;
        }

        /// <summary>
        /// The object number of the descriptor's embedded program, whichever key carries it.
        /// </summary>
        /// <remarks>
        /// <b><c>/FontFile3</c> counts for blocking too, not only for CFF subsetting.</b> This method
        /// is also how a <i>simple</i> font's program is recorded so it can be <c>Blocked</c>;
        /// looking at <c>/FontFile2</c> alone, a simple font sharing a CFF program with a Type0 one
        /// would be invisible, and nothing would block that program from pruning.
        /// </remarks>
        private static int FindFontFile(PdfDictionary descriptor, Dictionary<int, PdfObject> objects)
        {
            if (descriptor == null) return -1;
            if (descriptor[PdfName.FontFile2] is PdfReference truetype)
                return truetype.ReferenceObjectNumber;
            if (descriptor[PdfName.FontFile3] is PdfReference cff)
                return cff.ReferenceObjectNumber;
            return -1;
        }

        private static void RecordString(PdfObject operand, FontInfo font, Result result)
        {
            // A TJ array interleaves strings with kerning numbers; only the strings show glyphs.
            if (!(operand is PdfString text)) return;

            if (font == null)
            {
                // Text was shown with no font in force — either the stream is malformed, or a Tf
                // earlier in it named a font that would not resolve (which already set a reason).
                // Either way the glyphs cannot be attributed, so nothing may be pruned.
                result.Reason = result.Reason ?? "text was shown with no font selected";
                return;
            }

            if (!font.Analyzable || font.FontFileObject < 0) return;

            byte[] bytes = text.RawBytes;
            if (bytes == null) return;

            if (!result.UsedGlyphs.TryGetValue(font.FontFileObject, out HashSet<int> glyphs))
            {
                glyphs = new HashSet<int>();
                result.UsedGlyphs[font.FontFileObject] = glyphs;
            }

            // Record which numbering system these values are in, and refuse the program outright if
            // two fonts disagree about it. One program reached as both a CIDFontType0 and a
            // CIDFontType2 would mix raw CIDs with resolved glyph indices in one set, and the
            // resulting subset would blank glyphs the document draws — with nothing in the output to
            // show for it but missing text. Not reachable from a well-formed file, since the two
            // descendant subtypes take different /FontFile keys; blocked rather than trusted because
            // the cost of being wrong is silent.
            // Checked in BOTH directions on purpose: recording the new kind and then testing only
            // for the other one already present catches a disagreement in one visitation order and
            // misses it in the reverse, which is the shape of guard that passes its own test and
            // fails on a real file.
            HashSet<int> sameKind = font.CidKeyedProgram ? result.CidKeyedPrograms : result.GlyphIndexPrograms;
            HashSet<int> otherKind = font.CidKeyedProgram ? result.GlyphIndexPrograms : result.CidKeyedPrograms;
            sameKind.Add(font.FontFileObject);
            if (otherKind.Contains(font.FontFileObject)) result.Blocked.Add(font.FontFileObject);

            // An odd byte count cannot be a sequence of two-byte codes, so the string is malformed
            // and the last byte is a code this scanner cannot read. Readers that zero-pad the
            // remnant draw a glyph for it; recording nothing and staying quiet would let that glyph
            // be pruned, which is the one outcome this type exists to prevent. The codes that DID
            // parse are still recorded — they are drawn either way — but the reason blocks pruning.
            if ((bytes.Length & 1) != 0)
            {
                result.Reason = result.Reason ??
                    "an Identity-H string had an odd byte length, so its last code could not be read";
            }

            // Identity-H/V: every code is two bytes, big-endian, and is the CID outright.
            for (int i = 0; i + 1 < bytes.Length; i += 2)
            {
                int cid = (bytes[i] << 8) | bytes[i + 1];
                glyphs.Add(MapCidToGid(cid, font.CidToGid));
            }
        }

        private static int MapCidToGid(int cid, byte[] map)
        {
            if (map == null) return cid;                 // /Identity
            int at = cid * 2;
            if (at + 1 >= map.Length) return 0;          // beyond the map: .notdef
            return (map[at] << 8) | map[at + 1];
        }

        internal sealed class FontInfo
        {
            /// <summary>Object number of the <c>/FontFile2</c> or <c>/FontFile3</c> stream, or −1.</summary>
            internal int FontFileObject = -1;

            /// <summary>True when character codes can be mapped to glyph IDs.</summary>
            internal bool Analyzable;

            /// <summary>
            /// True for a <c>CIDFontType0</c> descendant: the values recorded for this font are
            /// <b>CIDs</b>, which only the CFF charset can turn into glyph indices.
            /// </summary>
            internal bool CidKeyedProgram;

            /// <summary><c>/CIDToGIDMap</c> stream bytes, or null for <c>/Identity</c>.</summary>
            internal byte[] CidToGid;

            internal PdfDictionary Type3CharProcs;
            internal PdfDictionary Type3Resources;

            /// <summary>
            /// Whether the <c>/CharProcs</c> walk has actually run for this font. A cache hit skips
            /// the walk, so without this a font whose first selection was refused by the depth cap
            /// would never have its procedures examined at all.
            /// </summary>
            internal bool Type3ProcsWalked;
        }

        // ------------------------------------------------------------------
        //  Document structure
        // ------------------------------------------------------------------

        /// <summary>
        /// The page's content as one stream, or <c>null</c> with <paramref name="refusal"/> set when a
        /// part does not decode or the parts together pass what the walk would accept
        /// (<see cref="ContentScanGraph.PageContent"/>).
        /// </summary>
        /// <remarks>
        /// <b>Fails closed on an unread part.</b> Skipping it left the glyphs it draws out of
        /// <see cref="Result.UsedGlyphs"/> with <see cref="Result.Reason"/> null, and those are the
        /// glyphs the pruner removes.
        /// </remarks>
        private static byte[] ConcatenatePageContent(PdfDictionary page, Dictionary<int, PdfObject> objects,
            OptimizationOptions options, Result result, int budget, out string refusal)
        {
            long walkable = Math.Min(Math.Min(MaxContentBytesPerStream, (long)budget * MinBytesPerOperation),
                result.TokenizeBytesLeft);
            byte[] content = ContentScanGraph.PageContent(page, objects, options, walkable,
                out bool partUnread, out bool overTotal);
            refusal = overTotal
                ? "a page's content parts together are larger than the scan could walk, so the glyphs they " +
                  "draw are unaccounted for"
                : partUnread
                    ? "a part of a page's content could not be decoded, so the glyphs it draws are unaccounted for"
                    : null;
            return refusal == null ? content : null;
        }

        // One implementation, in ContentScanGraph. The sibling scanner carried these three
        // token-identical, and both walk the same graph for different reasons.
        private static PdfObject Lookup(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
            => ContentScanGraph.Lookup(resources, category, name, objects);

        private static int LookupReference(PdfDictionary resources, PdfName category, PdfName name,
            Dictionary<int, PdfObject> objects)
            => ContentScanGraph.LookupReference(resources, category, name, objects);

        private static PdfObject Deref(PdfObject obj, Dictionary<int, PdfObject> objects)
            => ContentScanGraph.Deref(obj, objects);
    }
}
