// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Extracts text from a PDF with per-character, word, and line bounding boxes
    /// in PDF page-coordinate space (origin bottom-left, Y-up, units = PDF points).
    ///
    /// <para>
    /// Each <see cref="PdfTextChar"/> carries the character's baseline origin and an
    /// approximate advance-width rectangle derived from the font size, the horizontal
    /// scale factor, and the scale carried by the text matrix and CTM — so a producer that
    /// writes <c>1 Tf</c> and puts the size in the matrix (<c>12 0 0 12 x y Tm</c>, which is
    /// what Cairo and matplotlib emit) reports the same box as <c>12 Tf</c> under an identity
    /// matrix.  Characters are clustered into <see cref="PdfTextWord"/>s by
    /// horizontal gap, and words are clustered into <see cref="PdfTextLine"/>s by
    /// baseline: glyphs on one baseline are one row, and rows whose baselines lie within
    /// half the smaller glyph's height (<see cref="WordAssemblyOptions.YTolerance"/>) join
    /// side by side — never two rows that overlap horizontally further apart than that, and
    /// never on a tall glyph's say alone.
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   var pages = PdfStructuredTextExtractor.Extract(stream);
    ///   foreach (var line in pages[0].Lines)
    ///       Console.WriteLine($"[{line.X:F1},{line.Y:F1}] {line.Text}");
    /// </code>
    /// </summary>
    public static class PdfStructuredTextExtractor
    {
        // ----------------------------------------------------------------
        //  Diagnostics
        // ----------------------------------------------------------------

        /// <summary>
        /// Last non-fatal error encountered during structured text extraction
        /// (failed Form XObject decode, malformed content stream, etc.).
        /// <c>null</c> on a clean run. Reset at the start of each <see cref="Extract(Stream)"/>
        /// (and overload) call.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent extraction on another
        /// thread cannot overwrite it — read it on the thread that ran the call (see the
        /// Diagnostics Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [System.ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// The steps the last line grouping on this thread took — a glyph put in a row, a row of a
        /// line looked at for the nearest, a pair of runs checked for stacking, a line let go out of
        /// reach, a rotated glyph walked — for a test to see the work bounded. Reset at the start of
        /// each grouping. <c>[ThreadStatic]</c>.
        /// </summary>
        internal static int LastLineGroupingWork => _lastLineGroupingWork;

        [System.ThreadStatic]
        private static int _lastLineGroupingWork;

        /// <summary>
        /// The most steps one row took joining a line in the last line grouping on this thread —
        /// for a test to see each row held to <see cref="MaxStepsPerRow"/>. Reset at the start of
        /// each grouping. <c>[ThreadStatic]</c>.
        /// </summary>
        internal static int LastLineGroupingRowSteps => _lastLineGroupingRowSteps;

        [System.ThreadStatic]
        private static int _lastLineGroupingRowSteps;

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Extracts structured text from all pages of a PDF file.</summary>
        public static IReadOnlyList<PdfPageTextLayout> Extract(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return Extract(fs);
        }

        /// <summary>Extracts structured text from all pages of a PDF stream.</summary>
        public static IReadOnlyList<PdfPageTextLayout> Extract(Stream input)
            => Extract(input, WordAssemblyOptions.Default);

        /// <summary>Extracts structured text from all pages using custom word assembly tolerances.</summary>
        public static IReadOnlyList<PdfPageTextLayout> Extract(Stream input, WordAssemblyOptions options)
        {
            if (input == null)   throw new ArgumentNullException(nameof(input));
            if (options == null) throw new ArgumentNullException(nameof(options));

            LastError = null;
            using var doc  = PdfDocument.Open(input);
            // An encrypted document reads as no pages, and an empty layout from one is a confident
            // wrong answer: refused, naming the door that takes a password.
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.ExtractStructuredText(Stream, string password)");
            var result     = new List<PdfPageTextLayout>(doc.Pages.Count);
            // One duplicate-overlap index for the document's pages, emptied between them with its
            // storage kept (OverlappingTextIndex.Reset) — each page grew its own from empty.
            var drawn      = new OverlappingTextIndex<(string Text, int Mcid, int Stream)>();

            for (int pi = 0; pi < doc.Pages.Count; pi++)
            {
                try
                {
                    if (!drawn.Reset()) drawn = new OverlappingTextIndex<(string Text, int Mcid, int Stream)>();
                    result.Add(ExtractPage(doc.Pages[pi], doc, pi, options, drawn));
                }
                catch (Exception ex)
                {
                    LastError = $"Page {pi + 1}: {ex.Message}";
                    result.Add(new PdfPageTextLayout(pi, Array.Empty<PdfTextLine>(),
                                                     Array.Empty<PdfTextChar>()));
                }
            }

            return result;
        }

        /// <summary>Extracts structured text from a single page using custom word assembly tolerances.</summary>
        /// <remarks>
        /// The page alone: each page's extraction is its own (its font cache, its overlap index), so
        /// this is what the whole-document door answers for the page, without extracting every page —
        /// which would make a caller asking page by page pay the square of the document's length. <see cref="LastError"/> is this page's alone.
        /// </remarks>
        public static PdfPageTextLayout Extract(Stream input, int pageIndex, WordAssemblyOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            LastError = null;
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the text", "PdfOperations.ExtractStructuredText(Stream, int, string password, ...)");
            return Extract(doc, pageIndex, options);
        }

        /// <summary>Extracts structured text from one page of a document the caller holds open.</summary>
        /// <remarks>
        /// For a caller asking page by page — a viewer indexing the pages a search or a selection
        /// reaches — which would otherwise open the document once a page. The layout is the stream
        /// door's for the same page; <see cref="LastError"/> is this page's alone.
        /// </remarks>
        public static PdfPageTextLayout Extract(PdfDocument doc, int pageIndex, WordAssemblyOptions options = null)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            options ??= WordAssemblyOptions.Default;

            LastError = null;
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            try
            {
                return ExtractPage(doc.Pages[pageIndex], doc, pageIndex, options,
                    new OverlappingTextIndex<(string Text, int Mcid, int Stream)>());
            }
            catch (Exception ex)
            {
                LastError = $"Page {pageIndex + 1}: {ex.Message}";
                return new PdfPageTextLayout(pageIndex, Array.Empty<PdfTextLine>(), Array.Empty<PdfTextChar>());
            }
        }

        // ----------------------------------------------------------------
        //  Per-page extraction
        // ----------------------------------------------------------------

        private static PdfPageTextLayout ExtractPage(PdfPage page, PdfDocument doc, int pageIndex,
                                                      WordAssemblyOptions opts,
                                                      OverlappingTextIndex<(string Text, int Mcid, int Stream)> drawn)
        {
            opts ??= WordAssemblyOptions.Default;
            byte[] content = page.GetContentStreamData();
            if ((content == null || content.Length == 0) && !opts.IncludeAnnotationAppearances)
                return new PdfPageTextLayout(pageIndex, Array.Empty<PdfTextLine>(), Array.Empty<PdfTextChar>());

            var ops   = content == null || content.Length == 0 ? new List<ContentStreamOperation>() : ContentStreamParser.Parse(content);
            var chars = new List<PdfTextChar>(GlyphsShownAtMost(ops));

            using var fontCache = new Rendering.FontCache();

            // One aggregate budget per page, charged per form invocation. The depth cap and the
            // branch-scoped visited set each bound one dimension and neither bounds the product —
            // see PdfLimits.MaxFormXObjectInvocationsPerPage for the doubling chain that defeats
            // both. WalkBudget rather than WorkBudget: extraction returns a partial page and says
            // so on LastError, it does not abort the document.
            var formBudget = new WalkBudget(PdfLimits.MaxFormXObjectInvocationsPerPage);
            if (ops.Count > 0) InterpretOps(ops, page.Resources, doc, chars, fontCache, budget: formBudget);
            if (opts.IncludeAnnotationAppearances) AppendAnnotationText(page, doc, pageIndex, chars, fontCache, formBudget);

            if (formBudget.Exhausted)
                LastError =
                    $"Page {pageIndex + 1}: form-XObject invocations exceeded " +
                    $"{PdfLimits.MaxFormXObjectInvocationsPerPage:N0}; the remaining forms were " +
                    "not walked and the extracted text for this page is incomplete.";

            DropInvisibleCopies(chars);
            DropOverlappingDuplicates(chars, drawn);
            if (drawn.CeilingReached)
                LastError ??=
                    $"Page {pageIndex + 1}: the duplicate-text comparisons exceeded " +
                    $"{PdfLimits.MaxOverlappingTextComparisons:N0} (PdfLimits.MaxOverlappingTextComparisons); " +
                    "text drawn again over itself after that point is kept once for each drawing.";

            var lines = BuildLines(chars, opts);
            return new PdfPageTextLayout(pageIndex, lines, chars);
        }

        /// <summary>
        /// The lines of text the page's annotation appearances draw, and nothing of the page's own
        /// content — for a caller that has to know what an appearance shows, such as the redactor
        /// reading its result back. Each appearance is read where a reader shows it: hidden and
        /// optional-content-hidden annotations and popups are left out, the stream's <c>/BBox</c>
        /// and <c>/Matrix</c> are fitted onto <c>/Rect</c> (§12.5.5), and its content runs against
        /// §12.7.3.3's resources. <see cref="LastError"/> says when the walk stopped short.
        /// </summary>
        internal static IReadOnlyList<PdfTextLine> AnnotationLines(PdfDocument doc, int pageIndex)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (pageIndex < 0 || pageIndex >= doc.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            LastError = null;
            var chars = new List<PdfTextChar>();
            using var fontCache = new Rendering.FontCache();
            var budget = new WalkBudget(PdfLimits.MaxFormXObjectInvocationsPerPage);
            AppendAnnotationText(doc.Pages[pageIndex], doc, pageIndex, chars, fontCache, budget);
            DropInvisibleCopies(chars);
            return BuildLines(chars, WordAssemblyOptions.Default);
        }

        /// <summary>
        /// Adds the glyphs the page's annotation appearances draw, as a reader shows them
        /// (<see cref="WordAssemblyOptions.IncludeAnnotationAppearances"/>): a filled field's value,
        /// a free-text note, a stamp's caption. An annotation a reader does not present — hidden,
        /// on a layer that is off, a popup — draws nothing; the appearance is the one the
        /// renderers draw (<see cref="Rendering.AnnotationAppearance.NormalAppearance"/>), fitted
        /// onto <c>/Rect</c> as they fit it, its text in page space.
        /// </summary>
        private static void AppendAnnotationText(PdfPage page, PdfDocument doc, int pageIndex,
                                                 List<PdfTextChar> chars, Rendering.FontCache fontCache, WalkBudget budget)
        {
            var annotsObj = page.Dictionary[PdfName.Annots];
            if (annotsObj == null) return;
            if (doc.ResolveObject(annotsObj) is not PdfArray annots) return;

            HashSet<int> hiddenSet = null;
            var hidden = new Lazy<ISet<int>>(() => hiddenSet ??= Rendering.PdfPageRenderer.BuildHiddenOcgSet(doc));
            int walked = 0;
            foreach (PdfObject entry in annots)
            {
                // Pages may share one /Annots array: the page ceiling bounds this page's walk, and the cut is said.
                if (++walked > PdfLimits.MaxAnnotationsPerPage)
                {
                    LastError ??= $"Page {pageIndex + 1}: more than {PdfLimits.MaxAnnotationsPerPage:N0} annotations " +
                                  "(PdfLimits.MaxAnnotationsPerPage); the rest were not read.";
                    break;
                }
                if (doc.ResolveObject(entry) is not PdfDictionary annot) continue;
                if (PdfResolve.Name(doc, annot, PdfName.Subtype)?.Value == PdfAnnotationSubtypes.Popup) continue;
                if (Rendering.AnnotationVisibility.IsHidden(annot, doc, null)) continue;
                if (Rendering.ContentStreamInterpreter.IsAnnotationOcHidden(annot, hidden.Value, doc)) continue;

                PdfStream appearance = Rendering.AnnotationAppearance.NormalAppearance(doc, annot);
                if (appearance == null) continue;
                var rect = doc.ResolveObject(annot[PdfName.Rect]) as PdfArray;
                if (!Rendering.AnnotationAppearance.TryFitToRect(doc, rect, appearance, out float[] fit, out _, out _)) continue;

                var gs = new TextGs { HiddenLayers = hidden, CTM = fit };
                RunFormStream(appearance, "an annotation appearance",
                              Rendering.AnnotationAppearance.ResolveResources(appearance, doc, page) ?? page.Resources,
                              doc, gs, chars, fontCache, depth: 1, visited: null, budget);
            }
        }

        /// <summary>
        /// The most glyphs a page's own content can show — a byte of shown string is at most one
        /// glyph — so the page's glyph list is sized once rather than doubled up to it. A list grown
        /// from empty allocated about twice its final size in discarded arrays, and kept up to half
        /// its capacity unused. Text in a form is not counted (the list grows for it as before), and
        /// the figure is capped, so a stream of a few long strings reserves no more than
        /// <see cref="MaxPresizedGlyphs"/>.
        /// </summary>
        internal static int GlyphsShownAtMost(List<ContentStreamOperation> ops)
        {
            long bytes = 0;
            foreach (var op in ops)
            {
                PdfObject[] operands = op.Operands;
                if (operands.Length == 0) continue;
                switch (op.Operator)
                {
                    case PdfOperators.ShowText:
                    case PdfOperators.NextLineShowText:
                    case PdfOperators.NextLineSetSpacingShowText:
                        if (operands[operands.Length - 1] is PdfString s) bytes += s.RawBytes.Length;
                        break;
                    case PdfOperators.ShowTextArray:
                        if (operands[0] is PdfArray a)
                            foreach (var item in a)
                                if (item is PdfString t) bytes += t.RawBytes.Length;
                        break;
                }
                if (bytes >= MaxPresizedGlyphs) return MaxPresizedGlyphs;
            }
            return (int)bytes;
        }

        /// <summary>The most glyphs a page's list is sized for up front (8 MB of references).</summary>
        internal const int MaxPresizedGlyphs = 1 << 20;

        // ----------------------------------------------------------------
        //  Content-stream walker (text-state only)
        // ----------------------------------------------------------------

        /// <param name="depth">Form-XObject nesting depth, bounded by
        /// <see cref="PdfLimits.MaxFormXObjectDepth"/>.</param>
        /// <param name="visited">
        /// Form-XObject object numbers already entered on this branch, so a form that draws itself
        /// terminates. <c>null</c> at the page level; created on first descent.
        /// </param>
        /// <param name="budget">
        /// Aggregate form-invocation budget for the whole page. <c>null</c> only in the nested
        /// calls that inherit one. Neither <paramref name="depth"/> nor <paramref name="visited"/>
        /// bounds total work — a chain of distinct forms each drawn twice satisfies both and still
        /// runs 2<sup>31</sup> invocations.
        /// </param>
        /// <param name="streamObj">
        /// Object number of the content stream being walked — 0 for the page's own content,
        /// otherwise the Form XObject's. Stamped onto every emitted
        /// <see cref="PdfTextChar.McidStreamObject"/>, because §14.7.4.2 scopes an /MCID to its
        /// content stream and a form's MCID 0 is not the page's MCID 0.
        /// </param>
        /// <remarks>
        /// <b>This walk had no guard of any kind</b> — no depth cap, no visited set, no
        /// <c>WorkBudget</c> — while <c>Core.Text.PdfTextExtractor</c>, which does the same job,
        /// has both. A form XObject that draws itself therefore recursed until the stack overflowed,
        /// which is uncatchable and takes the process down. It needs no self-reference in the form's
        /// own <c>/Resources</c> either: a form with no <c>/Resources</c> inherits the caller's, so
        /// a page whose <c>/XObject</c> dictionary names the form is enough.
        /// </remarks>
        /// <param name="ops">Parsed operations of the stream being walked.</param>
        /// <param name="resources">
        /// The stream's <c>/Resources</c>. A form with none inherits the caller's, which is why a
        /// self-drawing form needs no self-reference of its own to recurse.
        /// </param>
        /// <param name="doc">The document, for resolving indirect references.</param>
        /// <param name="output">Accumulates the characters found, in content-stream order.</param>
        /// <param name="fontCache">
        /// Shared font cache. Passed down rather than rebuilt per form, which is the cost H12 closed
        /// on this class's other walk.
        /// </param>
        /// <param name="initialGs">
        /// The state a form XObject starts in: the graphics state at its <c>Do</c>, with the form's
        /// <c>/Matrix</c> composed into the CTM (§8.10.1). Null for page content.
        /// </param>
        /// <param name="inheritedMcid">
        /// The MCID of the marked-content sequence a form's <c>Do</c> was painted in (§14.7.5.2), which
        /// its glyphs take outside any sequence of its own; -1 for page content or none.
        /// </param>
        /// <param name="inheritedMcidStream">The content stream scope of <paramref name="inheritedMcid"/>.</param>
        private static void InterpretOps(
            List<ContentStreamOperation> ops,
            PdfDictionary resources,
            PdfDocument doc,
            List<PdfTextChar> output,
            Rendering.FontCache fontCache,
            int depth = 0,
            HashSet<int> visited = null,
            WalkBudget budget = null,
            int streamObj = 0,
            TextGs initialGs = null,
            int inheritedMcid = -1,
            int inheritedMcidStream = 0)
        {
            // Graphics-state stack (only the fields we need)
            var gsStack = new Stack<TextGs>();
            var gs      = initialGs ?? new TextGs { HiddenLayers = new Lazy<ISet<int>>(() => Rendering.PdfPageRenderer.BuildHiddenOcgSet(doc)) };

            // A layer that is off paints nothing, as render mode 3 does, and its text is emitted and
            // marked the same way. Q does not end a marked-content section, so the state's mark is
            // worked out again after one.
            bool drawnInHiddenLayer = gs.InHiddenLayer;
            int hiddenSections = 0;
            void NoteLayer() => gs.InHiddenLayer = drawnInHiddenLayer || hiddenSections > 0;

            // Marked-content stack — tracks the active /MCID so each emitted glyph can be
            // bound back to a logical-structure element (tagged-PDF extraction). -1 = untagged.
            //
            // It also carries §14.9.4 /ActualText and the index in `output` where the section's
            // glyphs began, because ActualText is "an exact replacement for the marked-content
            // sequence's content" — which cannot be applied until EMC says what that content was.
            // §14.7.5.2: a form painted inside a page's marked-content sequence belongs to it, so a
            // form that opens no sequence of its own emits under the caller's MCID and scope; a BDC
            // inside the form overrides it.
            var mcStack = new Stack<MarkedContent>();
            int CurMcid() => mcStack.Count > 0 ? mcStack.Peek().Mcid : inheritedMcid;
            int CurMcidStream() => mcStack.Count > 0 ? streamObj : inheritedMcidStream;
            // The open sections that are /ReversedChars, and that carry /ActualText: what an EMC asks
            // to know whether an enclosing section will redo its work.
            int openReversed = 0, openActualText = 0;
            void Open(MarkedContent section)
            {
                mcStack.Push(section);
                if (section.Reversed) openReversed++;
                if (section.ActualText != null) openActualText++;
            }

            // The line operators move the text-line matrix by a translation and set the text matrix
            // to it (§9.4.2). Both are the state's own arrays (q copies them, BT and Tm make new
            // ones), so they are updated in place through one scratch translation — three new
            // arrays an operator were most of what a page of text lines allocated in matrices.
            // ComposeInto is Compose's arithmetic, so every coordinate is what it was.
            float[] step = { 1, 0, 0, 1, 0, 0 };
            void MoveLine(float tx, float ty)
            {
                step[4] = tx; step[5] = ty;
                Rendering.PdfMatrix.ComposeInto(step, gs.Tlm, gs.Tlm);
                Array.Copy(gs.Tlm, gs.Tm, 6);
            }

            foreach (var op in ops)
            {
                switch (op.Operator)
                {
                    case PdfOperators.Save: gsStack.Push(gs.Clone()); break;
                    case PdfOperators.Restore:
                        if (gsStack.Count > 0)
                        {
                            // §9.3.1: the text matrices are not graphics state, so Q leaves them where
                            // the text has reached, as both renderers do.
                            float[] tm = gs.Tm, tlm = gs.Tlm;
                            gs = gsStack.Pop();
                            gs.Tm = tm;
                            gs.Tlm = tlm;
                            NoteLayer();
                        }
                        break;

                    case PdfOperators.BeginMarkedContentProps:
                    {
                        // The property list is read by Core.Text.MarkedContentInfo, shared with
                        // Core's own text extractor: two extractors must not disagree about what a
                        // sequence declares, or a document extracts differently depending on which
                        // entry point a caller reached for.
                        MarkedContentInfo info = op.Operands.Length > 1
                            ? MarkedContentInfo.Read(op.Operands[1], resources, doc)
                            : default;

                        bool hidden = OpensHiddenLayer(op, resources, doc, gs);
                        if (hidden) { hiddenSections++; NoteLayer(); }
                        Open(new MarkedContent(info.Mcid, info.ActualText, output.Count,
                            MarkedContentInfo.IsReversedChars(op.Operands.Length > 0 ? op.Operands[0] : null), hidden));
                        break;
                    }
                    case PdfOperators.BeginMarkedContent:
                        Open(new MarkedContent(-1, null, output.Count,
                            MarkedContentInfo.IsReversedChars(op.Operands.Length > 0 ? op.Operands[0] : null)));
                        break;
                    case PdfOperators.EndMarkedContent:
                        if (mcStack.Count > 0)
                        {
                            var section = mcStack.Pop();
                            if (section.HiddenLayer) { hiddenSections--; NoteLayer(); }
                            if (section.Reversed) openReversed--;
                            if (section.ActualText != null) openActualText--;

                            // An outer section's range starts before an inner one's, so an enclosing
                            // /ActualText replaces whatever this section produced and an enclosing
                            // /ReversedChars marks it again: the work is done once, by the outermost
                            // section that does it. Each EMC re-walked every glyph since its section
                            // began, so N nested sections around G glyphs cost N × G. (A guard of this
                            // shape was once removed because no test saw an outcome change; its
                            // evidence is the work, MarkedContentWork.)
                            if (section.Reversed && openReversed == 0 && openActualText == 0)
                                MarkReversedRun(output, section);
                            if (openActualText == 0)
                                ApplyActualText(output, section);
                        }
                        break;

                    case PdfOperators.ConcatMatrix:
                        if (op.Operands.Length >= 6)
                        {
                            float[] m =
                            {
                                ContentOperand.Float(op, 0), ContentOperand.Float(op, 1),
                                ContentOperand.Float(op, 2), ContentOperand.Float(op, 3),
                                ContentOperand.Float(op, 4), ContentOperand.Float(op, 5)
                            };
                            gs.CTM = Compose(m, gs.CTM);
                        }
                        break;

                    case PdfOperators.BeginText:
                        gs.Tm  = Identity();
                        gs.Tlm = Identity();
                        break;

                    case PdfOperators.SetFont:
                        if (op.Operands.Length >= 2)
                        {
                            gs.FontName = (op.Operands[0] as PdfName)?.Value;
                            gs.FontSize = ContentOperand.Float(op, 1);
                        }
                        break;

                    case PdfOperators.SetExtGState:
                        // §8.4.5: a graphics state can set the font and its size. Text placed without
                        // it stands somewhere the page does not draw it, and a region made from it misses.
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfName stateName
                            && doc.ResolveObject(resources?[PdfName.ExtGState]) is PdfDictionary states
                            && doc.ResolveObject(states[stateName]) is PdfDictionary state
                            && Rendering.ExtGStateFont.TryRead(doc, state, resources, "", out string stateFont, out float? stateSize))
                        {
                            if (stateFont != null) gs.FontName = stateFont;
                            if (stateSize != null) gs.FontSize = stateSize.Value;
                        }
                        break;

                    case PdfOperators.SetCharSpacing: gs.CharSpacing  = ContentOperand.Float(op, 0); break;
                    case PdfOperators.SetWordSpacing: gs.WordSpacing  = ContentOperand.Float(op, 0); break;
                    case PdfOperators.SetHorizontalScaling: gs.HorizScale   = ContentOperand.Float(op, 0) / 100f; break;
                    case PdfOperators.SetLeading: gs.Leading      = ContentOperand.Float(op, 0); break;
                    case PdfOperators.SetTextRise: gs.TextRise     = ContentOperand.Float(op, 0); break;
                    case PdfOperators.SetTextRenderMode: gs.TextRenderMode = SaturatingCast.ToInt(ContentOperand.Float(op, 0)); break;

                    // Non-stroking (fill) color operators
                    case PdfOperators.SetRgbFill:  // rg r g b
                        if (op.Operands.Length >= 3)
                            gs.FillColor = new[] { ContentOperand.Float(op,0), ContentOperand.Float(op,1), ContentOperand.Float(op,2) };
                        break;
                    case PdfOperators.SetGrayFill:   // g gray
                        if (op.Operands.Length >= 1)
                            gs.FillColor = GrayToRgb(ContentOperand.Float(op, 0));
                        break;
                    case PdfOperators.SetCmykFill:   // k c m y k
                        if (op.Operands.Length >= 4)
                            gs.FillColor = new[] { ContentOperand.Float(op,0), ContentOperand.Float(op,1), ContentOperand.Float(op,2), ContentOperand.Float(op,3) };
                        break;
                    case PdfOperators.SetColorFill:  // sc c1 c2 ...
                    case PdfOperators.SetColorFillN: // scn c1 c2 ... [name]
                        gs.FillColor = CollectFloatOperands(op);
                        break;

                    // Stroking color operators
                    case PdfOperators.SetRgbStroke:  // RG r g b
                        if (op.Operands.Length >= 3)
                            gs.StrokeColor = new[] { ContentOperand.Float(op,0), ContentOperand.Float(op,1), ContentOperand.Float(op,2) };
                        break;
                    case PdfOperators.SetGrayStroke:   // G gray
                        if (op.Operands.Length >= 1)
                            gs.StrokeColor = GrayToRgb(ContentOperand.Float(op, 0));
                        break;
                    case PdfOperators.SetCmykStroke:   // K c m y k
                        if (op.Operands.Length >= 4)
                            gs.StrokeColor = new[] { ContentOperand.Float(op,0), ContentOperand.Float(op,1), ContentOperand.Float(op,2), ContentOperand.Float(op,3) };
                        break;
                    case PdfOperators.SetColorStroke:  // SC c1 c2 ...
                    case PdfOperators.SetColorStrokeN: // SCN c1 c2 ... [name]
                        gs.StrokeColor = CollectFloatOperands(op);
                        break;

                    case PdfOperators.TextMove:
                        MoveLine(ContentOperand.Float(op, 0), ContentOperand.Float(op, 1));
                        break;
                    case PdfOperators.TextMoveSetLeading:
                        gs.Leading = -ContentOperand.Float(op, 1);
                        MoveLine(ContentOperand.Float(op, 0), ContentOperand.Float(op, 1));
                        break;
                    case PdfOperators.SetTextMatrix:
                        if (op.Operands.Length >= 6)
                        {
                            float[] m =
                            {
                                ContentOperand.Float(op, 0), ContentOperand.Float(op, 1),
                                ContentOperand.Float(op, 2), ContentOperand.Float(op, 3),
                                ContentOperand.Float(op, 4), ContentOperand.Float(op, 5)
                            };
                            gs.Tm  = m;
                            gs.Tlm = CopyArr(m);
                        }
                        break;
                    case PdfOperators.NextLine:
                        MoveLine(0, -gs.Leading);
                        break;

                    case PdfOperators.ShowText:
                        if (op.Operands.Length >= 1)
                            EmitString(ContentOperand.StringBytes(op.Operands[0]), ref gs, resources, doc, output, fontCache, CurMcid(), CurMcidStream());
                        break;

                    case PdfOperators.ShowTextArray:
                        if (op.Operands.Length >= 1 && op.Operands[0] is PdfArray tjArr)
                        {
                            foreach (var item in tjArr)
                            {
                                if (item is PdfString ps)
                                    EmitString(ps.RawBytes, ref gs, resources, doc, output, fontCache, CurMcid(), CurMcidStream());
                                else if (item is PdfNumber kn)
                                {
                                    // Along the line under Th, or down a vertical font's column
                                    // without it (§9.4.4) — the renderers' one rule.
                                    bool vertical = fontCache != null && fontCache.GetWMode(gs.FontName, resources, doc) == 1;
                                    float[] kern = Rendering.TextAdjustment.Of(kn.DoubleValue, gs.FontSize, gs.HorizScale, vertical);
                                    Rendering.PdfMatrix.ComposeInto(kern, gs.Tm, gs.Tm);
                                }
                            }
                        }
                        break;

                    case PdfOperators.NextLineShowText:
                    {
                        MoveLine(0, -gs.Leading);
                        if (op.Operands.Length >= 1)
                            EmitString(ContentOperand.StringBytes(op.Operands[0]), ref gs, resources, doc, output, fontCache, CurMcid(), CurMcidStream());
                        break;
                    }
                    case PdfOperators.NextLineSetSpacingShowText:
                        if (op.Operands.Length >= 3)
                        {
                            gs.WordSpacing = ContentOperand.Float(op, 0);
                            gs.CharSpacing = ContentOperand.Float(op, 1);
                            MoveLine(0, -gs.Leading);
                            EmitString(ContentOperand.StringBytes(op.Operands[2]), ref gs, resources, doc, output, fontCache, CurMcid(), CurMcidStream());
                        }
                        break;

                    case PdfOperators.InvokeXObject:
                        // Recurse into Form XObjects.
                        if (op.Operands.Length >= 1)
                        {
                            string xname = (op.Operands[0] as PdfName)?.Value;
                            if (xname == null) break;

                            if (depth < PdfLimits.MaxFormXObjectDepth)
                                RecurseForm(xname, resources, doc, gs, output, fontCache,
                                            depth, visited, budget, CurMcid(), CurMcidStream());
                            else
                                // The other two guards on this walk report and this one did not, so
                                // a document nested past the cap came back short and silent. The cap
                                // is the only one that bounds the STACK: the visited set refuses a
                                // repeat, which a chain of distinct forms never is, and the budget
                                // allows 100 000 invocations — which is 100 000 frames deep.
                                LastError =
                                    $"Form XObject '{xname}' is nested deeper than " +
                                    $"{PdfLimits.MaxFormXObjectDepth}; the remaining forms were not " +
                                    "walked and the extracted text for this page is incomplete.";
                        }
                        break;
                }
            }
        }

        /// <summary>Whether a <c>BDC</c> opens optional content that is off (§8.11.3.2): the raw entry, an OCG being known by its number.</summary>
        private static bool OpensHiddenLayer(ContentStreamOperation op, PdfDictionary resources, PdfDocument doc, TextGs gs)
        {
            if (op.Operands.Length < 2 || (op.Operands[0] as PdfName)?.Value != "OC") return false;
            PdfObject entry = op.Operands[1] is PdfName name
                ? (doc.ResolveObject(resources?[PdfName.Properties]) as PdfDictionary)?[name]
                : op.Operands[1];
            return Rendering.ContentStreamInterpreter.IsOcEntryHidden(entry, gs.HiddenLayers.Value, doc);
        }

        // ----------------------------------------------------------------
        //  Emit characters for a byte string at the current text position
        // ----------------------------------------------------------------

        private static void EmitString(
            byte[] bytes, ref TextGs gs,
            PdfDictionary resources, PdfDocument doc,
            List<PdfTextChar> output,
            Rendering.FontCache fontCache,
            int mcid = -1,
            int streamObj = 0)
        {
            if (bytes == null || bytes.Length == 0) return;

            float charH = gs.FontSize;

            // Try to get actual font widths for accurate positioning.
            //
            // Through the per-page FontCache this method already holds, NOT a fresh
            // PdfFont.Create. The cache memoises one PdfFont per (resources, resource name),
            // and DecodeBytes below reaches that very same entry through
            // FontCache.DecodeText -> GetPdfFont — so building one here re-parsed the font's
            // /ToUnicode CMap and re-read its /Widths on EVERY shown string, next to a cached
            // copy of the identical object.
            //
            // The `fontDict != null` guard stays. FontCache.GetPdfFont answers an unresolvable
            // font dictionary with a FallbackFont where this method's contract is a null
            // pdfFont; both report width 0 today (FallbackFont does not override GetWidth), so
            // the guard preserves the shape rather than a visible difference — but the shape is
            // what the `fontWidth > 0` approximation branch below is written against.
            Core.Fonts.PdfFont pdfFont = null;
            var fontDict = LookupFontDict(gs.FontName, resources, doc);
            if (fontDict != null)
                pdfFont = fontCache?.GetPdfFont(gs.FontName, resources, doc);

            // Resolve resource font name → BaseFont (Phase 2 metadata)
            string resolvedFontName = null;
            if (fontDict != null)
                resolvedFontName = (doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName)?.Value
                                   ?? gs.FontName;
            else
                resolvedFontName = gs.FontName;

            float effectiveFontSize = gs.FontSize * gs.HorizScale;

            // Codes come from the font's own encoding CMap, NOT from a fixed stride.
            // A fixed stride (FontCache.GetCharCodeSize, since removed) took the narrowest codespace, so a mixed-width CMap —
            // 1-byte ASCII plus 2-byte CJK, which is what the Shift-JIS-based Adobe CMaps are —
            // reported 1 and split every 2-byte code in half: the width lookup was wrong and the
            // decoded cluster was garbage from that point on. The codec is the splitter.
            // PdfRedactionEngine and PdfTemplateEngine were converted in the same
            // change; a fixed stride must not come back to any of the three.
            // A code at a time from the codec (GlyphIdResolver.NextCode), not a list per string.
            var codec = fontCache?.GetEncodingCodec(gs.FontName, resources, doc);

            // Allocated once a shown string, not once a glyph: the glyph's matrices are composed
            // into scratch (PdfMatrix.ComposeInto, Compose's own arithmetic) and the advance goes
            // into Tm in place — no other reference to gs.Tm exists; every assignment to it is a
            // fresh array or a copy. Three arrays a glyph were a quarter of this extractor's
            // allocation. The colours stay a copy a glyph: each PdfTextChar's is its own.
            float[] riseMatrix = gs.TextRise != 0f ? new float[] { 1, 0, 0, 1, 0, gs.TextRise } : null;
            float[] risenTm = riseMatrix != null ? new float[6] : null;
            float[] fullTm = new float[6];
            float[] adv = { 1, 0, 0, 1, 0, 0 };

            // §9.7.4.3: a vertical font draws each glyph at the pen less its position vector and
            // moves the pen down the column, by the renderers' one step rule; laid out along x, two
            // columns came out as one line with their glyphs interleaved.
            bool vertical = fontCache != null && fontCache.GetWMode(gs.FontName, resources, doc) == 1;
            float[] positionShift = vertical ? new float[] { 1, 0, 0, 1, 0, 0 } : null;
            float[] shiftedTm = vertical ? new float[6] : null;

            // Iterate per SOURCE character code, not per decoded UTF-16 unit. A single
            // code can map (via ToUnicode) to a multi-char cluster — a ligature ("ﬁ"→"fi")
            // or a surrogate pair — and the previous foreach-over-decoded-chars loop
            // advanced byteIdx once per UTF-16 unit, over-running the byte cursor and
            // desyncing the width / charCode lookups for every subsequent code.
            int byteIdx = 0;
            while (byteIdx < bytes.Length)
            {
                int codeLen = GlyphIdResolver.NextCode(codec, bytes, byteIdx, out CMapCodec.Code code);
                int charCode = code.Value;

                // Decode this single source code so the emitted Unicode cluster stays aligned
                // with its charCode (ligatures/surrogates emit >1 char) — from the font's per-code
                // cache, not a copy of the code's bytes decoded afresh (a byte array and a
                // StringBuilder per glyph, a third of this extractor's allocation on text).
                // Advanced here, before the skip below: in a `foreach` an early skip would leave it
                // parked and every later code would be read from the wrong offset.
                byteIdx += codeLen;

                string cluster = DecodeCode(code, gs.FontName, resources, doc, fontCache);
                if (cluster.Length == 0) continue;

                // A font's width, an explicit 0 included — a combining mark's — and its default only
                // when it declares none (Core.Fonts.GlyphWidth, the one rule). A private 0.6 em stood
                // in for every zero, so each placed niqqud point pushed the rest of its line 0.6 em
                // along and split its words where they were not. A resource naming no font at all
                // keeps the 0.6 em approximation: there is no font to ask.
                // In text space already: a Type 3 font's width goes through its /FontMatrix, not 1000.
                // Keyed on the CID, not the raw code: a composite font's /W is indexed by CID
                // (§9.7.4.3), and the two differ under any encoding CMap but Identity.
                float charW = pdfFont == null
                    ? gs.FontSize * gs.HorizScale * 0.6f
                    : (float)GlyphWidth.TextSpace(pdfFont, code.Cid) * gs.FontSize * gs.HorizScale;

                // Apply the text rise (Ts): it shifts the glyph origin up (super-) or
                // down (subscript) in unscaled text space before mapping through Tm/CTM,
                // so super/sub-scripts cluster onto their own baseline rather than the
                // main line. ISO 32000-1 §9.4.4.
                float[] risen = gs.Tm;
                if (riseMatrix != null) { Rendering.PdfMatrix.ComposeInto(riseMatrix, gs.Tm, risenTm); risen = risenTm; }
                Rendering.VerticalGlyphMetrics verticalMetrics = default;
                if (vertical)
                {
                    verticalMetrics = fontCache.GetVerticalMetrics(gs.FontName, code.Cid, resources, doc, 1f);
                    positionShift[4] = -verticalMetrics.PositionXEm * gs.FontSize;
                    positionShift[5] = -verticalMetrics.PositionYEm * gs.FontSize;
                    Rendering.PdfMatrix.ComposeInto(positionShift, risen, shiftedTm);
                    risen = shiftedTm;
                }
                Rendering.PdfMatrix.ComposeInto(risen, gs.CTM, fullTm);
                float px = fullTm[4];
                float py = fullTm[5];

                // The emitted box is in PAGE space (see the class doc), but charW/charH are in
                // unscaled TEXT space. A producer that writes "1 Tf" and carries the size in the
                // matrix instead — Cairo and matplotlib both do, e.g. "12 0 0 12 x y Tm" — got a
                // box 12x too small, while px/py went through fullTm and were correct. Every
                // inter-glyph gap then dwarfed the word-split threshold in AssembleLines
                // (gap = next.X - (prev.X + prev.Width)) and EVERY glyph became its own word.
                // Column norms of the [a b c d] sub-matrix, so a pure rotation (norm 1) leaves
                // the box unchanged. charW/charH themselves stay in text space: the advance
                // below is composed INTO Tm and must not be pre-scaled.
                float mSx = (float)Math.Sqrt(fullTm[0] * fullTm[0] + fullTm[1] * fullTm[1]);
                float mSy = (float)Math.Sqrt(fullTm[2] * fullTm[2] + fullTm[3] * fullTm[3]);

                // Derive rotation and upright flag from text matrix (Phase 2 metadata)
                float rotation = DeriveRotation(fullTm);
                bool isUpright = Math.Abs(rotation) < 15f;

                char firstNonSpace = FirstNonSpace(cluster);

                // Modes 3 (invisible) and 7 (clip-only) paint no ink, and the glyph is emitted all
                // the same: it is how a scanned page carries searchable text. It is only marked, so
                // DropInvisibleCopies can remove the ones laid over visible glyphs — the duplication
                // that once made this skip every such glyph, and a scanned page extract as nothing.
                // The text state, shared with the glyph before when it is the same — on a run of
                // text, every glyph after the first. The colours are the state's own arrays: every
                // colour operator makes a new one and nothing writes into one after.
                PdfTextGlyphStyle previous = output.Count > 0 ? output[output.Count - 1].Style : PdfTextGlyphStyle.Default;
                PdfTextGlyphStyle style = previous.With(
                    resolvedFontName, effectiveFontSize, gs.FillColor, gs.StrokeColor, isUpright, rotation, vertical);

                output.Add(new PdfTextChar(cluster, px, py, charW * mSx, charH * mSy, IsRtlChar(firstNonSpace))
                {
                    Style       = style,
                    Mcid        = mcid,
                    McidStreamObject = streamObj,
                    IsInvisible = gs.TextRenderMode == 3 || gs.TextRenderMode == 7 || gs.InHiddenLayer
                });

                // Advance Tm using actual width.
                // ISO 32000-1 §9.4.4: tx = ((w0/1000)·Tfs + Tc + Tw) · Th
                // charW already includes the Th factor; the Tc/Tw spacing terms must be
                // scaled by Th separately. Do NOT multiply charW by Th again here.
                // §9.3.3: word spacing applies to the single-byte code 32 alone (the one rule,
                // CMapCodec.Code.IsWordSpaceCode) — not to a two-byte code that decodes to a space.
                if (vertical)
                {
                    adv[4] = 0f;
                    adv[5] = -verticalMetrics.Step(gs.FontSize, gs.CharSpacing, gs.WordSpacing, code.IsWordSpaceCode);
                }
                else
                {
                    float advX = charW
                                 + (gs.CharSpacing + (code.IsWordSpaceCode ? gs.WordSpacing : 0f))
                                   * gs.HorizScale;
                    adv[4] = advX;
                    adv[5] = 0f;
                }
                Rendering.PdfMatrix.ComposeInto(adv, gs.Tm, gs.Tm);
            }
        }

        // ----------------------------------------------------------------
        //  Form XObject recursion
        // ----------------------------------------------------------------

        private static void RecurseForm(
            string name, PdfDictionary resources, PdfDocument doc,
            TextGs parentGs, List<PdfTextChar> output,
            Rendering.FontCache fontCache,
            int depth, HashSet<int> visited, WalkBudget budget = null,
            int enclosingMcid = -1, int enclosingMcidStream = 0)
        {
            // Charged before the work, once per invocation, so the aggregate is bounded even
            // where every path is short and no form repeats on its own branch. Returning here
            // leaves budget.Exhausted set, which ExtractPage turns into a LastError naming the
            // page as incomplete — a silent truncation would be worse than the hang.
            if (budget != null && !budget.Take()) return;

            var cat = resources?[PdfName.XObject];
            if (cat == null) return;
            var catDict = doc.ResolveObject(cat) as PdfDictionary;
            if (catDict == null) return;
            var entry = doc.ResolveObject(catDict[new PdfName(name)]);
            if (entry is not PdfStream formStream) return;

            string subtype = PdfResolve.Name(doc, formStream, PdfName.Subtype)?.Value;
            if (subtype != "Form") return;

            var formRes = formStream[PdfName.Resources] is PdfObject ro
                ? doc.ResolveObject(ro) as PdfDictionary ?? resources
                : resources;
            RunFormStream(formStream, $"Form XObject '{name}'", formRes, doc, parentGs, output, fontCache,
                          depth, visited, budget, enclosingMcid, enclosingMcidStream);
        }

        /// <summary>
        /// Runs a form's content — a form XObject's, or an annotation appearance's — from
        /// <paramref name="parentGs"/> with the form's <c>/Matrix</c> composed in, against
        /// <paramref name="formRes"/>. <paramref name="what"/> names the stream in a report.
        /// </summary>
        private static void RunFormStream(
            PdfStream formStream, string what, PdfDictionary formRes, PdfDocument doc,
            TextGs parentGs, List<PdfTextChar> output, Rendering.FontCache fontCache,
            int depth, HashSet<int> visited, WalkBudget budget,
            int enclosingMcid = -1, int enclosingMcidStream = 0)
        {
            // The cycle half of the guard. The depth cap alone bounds the stack but still walks the
            // same form 32 times over on a self-drawing file; the visited set stops it at the first
            // repeat. Scoped to this branch — removed on the way out — so a form legitimately drawn
            // twice side by side is still extracted twice.
            visited ??= new HashSet<int>();
            int formObj = formStream.ObjectNumber;
            if (formObj > 0 && !visited.Add(formObj))
            {
                LastError = $"{what} draws itself; the recursion was stopped.";
                return;
            }
            try
            {

            byte[] data;
            try { data = formStream.GetDecodedData(); }
            catch (Exception ex)
            {
                LastError = $"{what} decode failed: {ex.Message}";
                return;
            }

            var ops = ContentStreamParser.Parse(data);
            var childGs = parentGs.Clone();
            if (formStream[PdfName.OC] != null
                && Rendering.ContentStreamInterpreter.IsOcEntryHidden(formStream[PdfName.OC], childGs.HiddenLayers.Value, doc))
                childGs.InHiddenLayer = true;

            // Apply form /Matrix if present — resolved, since GetArray reads only a direct array.
            var matArr = PdfResolve.Array(doc, formStream, PdfName.Matrix);
            if (matArr != null && matArr.Count >= 6)
            {
                float[] m =
                {
                    Rendering.PdfResolve.ArrayFloat(doc, matArr, 0), Rendering.PdfResolve.ArrayFloat(doc, matArr, 1),
                    Rendering.PdfResolve.ArrayFloat(doc, matArr, 2), Rendering.PdfResolve.ArrayFloat(doc, matArr, 3),
                    Rendering.PdfResolve.ArrayFloat(doc, matArr, 4), Rendering.PdfResolve.ArrayFloat(doc, matArr, 5)
                };
                childGs.CTM = Compose(m, childGs.CTM);
            }

            // The form's own object number becomes the MCID scope for the sequences it opens; what it
            // emits outside any sequence of its own belongs to the sequence the Do was painted in
            // (§14.7.5.2), under the caller's scope. It starts in `childGs` — the state at the Do
            // with the form's /Matrix composed in — so a form drawn under `1 0 0 1 100 50 cm`
            // reports its words where the page shows them.
            InterpretOps(ops, formRes, doc, output, fontCache, depth + 1, visited, budget,
                         streamObj: formObj, initialGs: childGs,
                         inheritedMcid: enclosingMcid, inheritedMcidStream: enclosingMcidStream);
            }
            finally
            {
                if (formObj > 0) visited.Remove(formObj);
            }
        }

        // ----------------------------------------------------------------
        //  RTL helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns the first non-space char of a decoded cluster (used to classify the
        /// cluster's script direction). Falls back to the first char (or U+0000 when empty).
        /// </summary>
        private static char FirstNonSpace(string cluster)
        {
            for (int i = 0; i < cluster.Length; i++)
                if (cluster[i] != ' ') return cluster[i];
            return cluster.Length > 0 ? cluster[0] : '\0';
        }

        /// <summary>Returns true when the Unicode codepoint is a strong RTL character.</summary>
        private static bool IsRtlChar(char c) => TextDirection.IsRtlChar(c);

        /// <summary>
        /// Returns true when more than half the (non-space) characters in the group
        /// are strong RTL characters.
        /// </summary>
        private static bool IsRtlGroup(List<PdfTextChar> chars) => TextDirection.IsRtlGroup(chars);

        // ----------------------------------------------------------------
        //  Cluster chars → words → lines
        // ----------------------------------------------------------------

        internal static IReadOnlyList<PdfTextLine> BuildLines(List<PdfTextChar> chars,
                                                              WordAssemblyOptions opts = null)
        {
            _lastLineGroupingWork = 0;
            _lastLineGroupingRowSteps = 0;
            opts = opts ?? WordAssemblyOptions.Default;

            // Blank chars are left out of words unless the caller keeps them. The upright ones are
            // kept aside all the same: a space the content stream drew is where a word ends.
            List<PdfTextChar> filtered;
            List<PdfTextChar> blanks = null;
            if (opts.KeepBlankChars)
                filtered = chars;
            else
            {
                filtered = new List<PdfTextChar>(chars.Count);
                blanks = new List<PdfTextChar>();
                foreach (var c in chars)
                {
                    if (c.Char == null) continue;
                    if (c.Char.Trim().Length > 0) filtered.Add(c);
                    else if (c.IsUpright && IsFinite(c.X) && IsFinite(c.Y) && IsFinite(c.Width)) blanks.Add(c);
                }
            }

            if (filtered.Count == 0) return Array.Empty<PdfTextLine>();

            // Upright glyphs are lines by baseline; rotated glyphs form independent line clusters
            // tagged IsVertical; a vertical font's upright glyphs (§9.7.4.3) are columns, each a line
            // — grouped by baseline they put the glyph at one height of every column on one line,
            // the columns interleaved.
            // A page of upright text — nearly every page — is assembled from the filtered list itself
            // (its own copy, unless it is the caller's), not a second copy of it.
            int rotatedCount = 0, columnCount = 0;
            foreach (var c in filtered)
            {
                if (!c.IsUpright) rotatedCount++;
                else if (c.VerticalWriting) columnCount++;
            }
            List<PdfTextChar> upright, rotated, columns;
            if (rotatedCount == 0 && columnCount == 0)
            {
                upright = ReferenceEquals(filtered, chars) ? new List<PdfTextChar>(chars) : filtered;
                rotated = new List<PdfTextChar>(0);
                columns = rotated;
            }
            else
            {
                upright = filtered.Where(c => c.IsUpright && !c.VerticalWriting).ToList();
                columns = filtered.Where(c => c.IsUpright && c.VerticalWriting).ToList();
                rotated = filtered.Where(c => !c.IsUpright).ToList();
            }

            var result = new List<PdfTextLine>();
            if (upright.Count  > 0) result.AddRange(AssembleLines(upright,  opts, isVertical: false, blanks));
            if (columns.Count  > 0) result.AddRange(AssembleColumns(columns));
            if (rotated.Count  > 0) result.AddRange(AssembleLines(rotated,  opts, isVertical: true, blanks: null));
            return result;
        }

        /// <summary>
        /// A vertical font's glyphs (<see cref="PdfTextChar.VerticalWriting"/>) in columns: glyphs
        /// whose x lies within <see cref="SameBaselineFraction"/> of the smaller glyph's height of the
        /// column's first are one column, read top to bottom, and the columns follow right to left,
        /// as the script is read. Each column is one line of one word, <see cref="PdfTextLine.IsVertical"/>
        /// set, its box the glyphs'. The glyphs are counted in <see cref="LastLineGroupingWork"/>.
        /// </summary>
        private static IReadOnlyList<PdfTextLine> AssembleColumns(List<PdfTextChar> working)
        {
            // Right to left, then top to bottom; a glyph whose geometry overflowed to NaN joins no column.
            working.Sort((a, b) =>
            {
                int xc = b.X.CompareTo(a.X);
                return xc != 0 ? xc : b.Y.CompareTo(a.Y);
            });
            var lines = new List<PdfTextLine>();
            for (int start = 0; start < working.Count; )
            {
                var first = working[start];
                float firstH = Math.Abs(first.Height);
                int end = start + 1;
                while (end < working.Count)
                {
                    var g = working[end];
                    if (!(Math.Abs(g.X - first.X) <= SameBaselineFraction * Math.Min(Math.Abs(g.Height), firstH))) break;
                    end++;
                }
                var glyphs = working.GetRange(start, end - start);
                glyphs.Sort((a, b) => b.Y.CompareTo(a.Y));
                _lastLineGroupingWork += glyphs.Count;

                var text = new StringBuilder(glyphs.Count);
                float left = float.MaxValue, right = float.MinValue, bottom = float.MaxValue, top = float.MinValue;
                foreach (var g in glyphs)
                {
                    text.Append(g.Char);
                    left = Math.Min(left, g.X);
                    right = Math.Max(right, g.X + g.Width);
                    bottom = Math.Min(bottom, g.Y);
                    top = Math.Max(top, g.Y + Math.Abs(g.Height));
                }
                string columnText = text.ToString();
                var word = new PdfTextWord(columnText, left, bottom, right - left, top - bottom, glyphs);
                lines.Add(new PdfTextLine(columnText, left, bottom, right - left, top - bottom, new[] { word })
                {
                    IsVertical = true
                });
                start = end;
            }
            return lines;
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>True when a glyph's text is a combining mark alone (Unicode category Mn).</summary>
        private static bool IsCombiningMark(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (char c in text)
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    return false;
            return true;
        }

        /// <summary>Points either side of a letter's box a mark's centre may lie and still be over it.</summary>
        private const float MarkSlack = 0.5f;

        /// <summary>
        /// Most letters one mark is tried against for the nearest in height among those it lies over
        /// — the first it meets lies over it, so a mark past the bound still has its letter. A
        /// pointed letter has a handful; only a hostile page stacks thousands at one x.
        /// </summary>
        internal const int MaxLettersPerMark = 1024;

        /// <summary>
        /// The letters' horizontal spans (a box widened by <see cref="MarkSlack"/> each side), sorted by
        /// left edge, with the furthest right edge reached so far: which spans hold a point is then a
        /// binary search and a walk left while that furthest edge still reaches the point. Each mark
        /// scanned every letter on the page, twice — marks × letters, tens of billions on a page of
        /// pointed text.
        /// </summary>
        private sealed class LetterSpans
        {
            private readonly PdfTextChar[] _letters;
            private readonly int[] _drawn;          // order among the letters, for the earlier-wins tie
            private readonly float[] _starts, _ends, _furthestEnd;
            private readonly int[] _furthestAt;     // the span reaching _furthestEnd: it holds any point it reaches

            internal LetterSpans(IEnumerable<PdfTextChar> letters)
            {
                var spans = new List<(PdfTextChar Letter, int Drawn, float Start, float End)>();
                int drawn = 0;
                foreach (var letter in letters)
                {
                    float start = letter.X - MarkSlack, end = letter.X + letter.Width + MarkSlack;
                    if (IsFinite(start) && IsFinite(end)) spans.Add((letter, drawn, start, end));
                    drawn++;
                }
                spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Drawn.CompareTo(b.Drawn));
                _letters = new PdfTextChar[spans.Count];
                _drawn = new int[spans.Count];
                _starts = new float[spans.Count];
                _ends = new float[spans.Count];
                _furthestEnd = new float[spans.Count];
                _furthestAt = new int[spans.Count];
                float furthest = float.MinValue;
                int at = -1;
                for (int i = 0; i < spans.Count; i++)
                {
                    (_letters[i], _drawn[i], _starts[i], _ends[i]) = spans[i];
                    if (_ends[i] > furthest) { furthest = _ends[i]; at = i; }
                    _furthestEnd[i] = furthest;
                    _furthestAt[i] = at;
                }
            }

            // The last span starting at or before the point, or -1.
            private int LastStartingBy(float point)
            {
                int lo = 0, hi = _starts.Length - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (_starts[mid] <= point) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                return found;
            }

            internal bool Hold(float point)
            {
                int i = LastStartingBy(point);
                return i >= 0 && _furthestEnd[i] >= point;
            }

            /// <summary>
            /// Of the letters whose span holds the mark's centre, the nearest in height, the earliest
            /// drawn among equals — what the scan of every letter chose; at most
            /// <see cref="MaxLettersPerMark"/> looked at.
            /// </summary>
            internal PdfTextChar Nearest(PdfTextChar mark, ref int work)
            {
                float centre = mark.X + mark.Width / 2;
                int last = LastStartingBy(centre);
                if (last < 0 || _furthestEnd[last] < centre) return null;

                // The span reaching furthest right holds the centre: a letter before any search,
                // so the bound below cannot leave a mark without one — narrow letters in the gaps of
                // one wide letter would otherwise walk every one of them back to it.
                int held = _furthestAt[last];
                PdfTextChar best = _letters[held];
                float bestDy = Math.Abs(mark.Y - best.Y);
                int bestDrawn = _drawn[held];
                int steps = 0;
                for (int i = last; i >= 0 && _furthestEnd[i] >= centre; i--)
                {
                    if (steps++ >= MaxLettersPerMark) break;
                    if (_ends[i] < centre) continue;
                    float dy = Math.Abs(mark.Y - _letters[i].Y);
                    if (dy < bestDy || (dy == bestDy && _drawn[i] < bestDrawn))
                    {
                        bestDy = dy;
                        best = _letters[i];
                        bestDrawn = _drawn[i];
                    }
                }
                work += steps;
                return best;
            }
        }

        /// <summary>
        /// Each mark's letter — the one whose box its centre lies over, the nearest in height among
        /// those — placed in that letter's line. Every mark here lies over a letter (the caller asked
        /// <see cref="LetterSpans.Hold"/>), which outranked any distance in the scoring this replaces.
        /// The marks of one letter keep the order they were drawn in.
        /// </summary>
        private static Dictionary<PdfTextChar, List<PdfTextChar>> AttachMarks(
            List<List<PdfTextChar>> lineGroups, List<PdfTextChar> marks)
        {
            var marksOf = new Dictionary<PdfTextChar, List<PdfTextChar>>();
            if (marks.Count == 0) return marksOf;
            var spans = new LetterSpans(lineGroups.SelectMany(line => line));
            foreach (var mark in marks)
            {
                PdfTextChar best = spans.Nearest(mark, ref _lastLineGroupingWork);
                if (best == null) continue;
                if (!marksOf.TryGetValue(best, out var own)) marksOf[best] = own = new List<PdfTextChar>();
                own.Add(mark);
            }
            return marksOf;
        }

        // Core line/word assembly for a homogeneous char set (all upright or all rotated).
        // `blanks` holds the blank glyphs left out of `working`, or null; it is sorted by Y here, and
        // a word ends where one of them lies between two neighbouring glyphs of a line.
        //
        // A combining mark (a niqqud point, a harakah, a Latin accent drawn as its own glyph) placed
        // over a letter — its centre inside the letter's box, as a producer placing it by its font's
        // anchors draws it, raised or lowered, with little or no width — is set aside first and
        // attached to that letter (AttachMarks): neither its height nor its x says which line and
        // where in the word it goes, and sorting it as a letter put a point before its letter, or on
        // a line of its own. A mark laid out between letters, with an advance of its own, is not
        // over either and stays in sequence as before.
        private static IReadOnlyList<PdfTextLine> AssembleLines(
            List<PdfTextChar> working, WordAssemblyOptions opts, bool isVertical, List<PdfTextChar> blanks)
        {
            blanks?.Sort((a, b) => a.Y.CompareTo(b.Y));

            // The letters are needed only to place marks; a line with none (nearly every line) takes no copy.
            bool anyMark = false;
            foreach (var c in working) if (IsCombiningMark(c.Char)) { anyMark = true; break; }
            var letters = anyMark ? new LetterSpans(working.Where(c => !IsCombiningMark(c.Char))) : null;
            var marks = anyMark
                ? working.Where(c => IsCombiningMark(c.Char) && letters.Hold(c.X + c.Width / 2)).ToList()
                : new List<PdfTextChar>(0);
            if (marks.Count > 0)
            {
                var placed = new HashSet<PdfTextChar>(marks);
                working = working.Where(c => !placed.Contains(c)).ToList();
            }

            // Sort by Y descending (top of page first), then X ascending as tie-breaker.
            working.Sort((a, b) =>
            {
                int yc = b.Y.CompareTo(a.Y);
                return yc != 0 ? yc : a.X.CompareTo(b.X);
            });

            // Group into lines: upright glyphs by baseline, rotated ones by band.
            List<List<GlyphRow>> lineRows = null;
            List<List<PdfTextChar>> lineGroups;
            if (isVertical)
                lineGroups = GroupByBand(working, opts);
            else
            {
                lineRows = GroupByBaseline(working, opts);
                lineGroups = new List<List<PdfTextChar>>(lineRows.Count);
                foreach (var l in lineRows)
                {
                    int n = 0;
                    foreach (var r in l) n += r.Glyphs.Count;
                    var g = new List<PdfTextChar>(n);
                    foreach (var r in l) g.AddRange(r.Glyphs);
                    lineGroups.Add(g);
                }
            }
            Dictionary<PdfTextChar, List<PdfTextChar>> marksOf = AttachMarks(lineGroups, marks);

            var lines = new List<PdfTextLine>(lineGroups.Count);
            var wordStarts = new List<int>();
            var wordText = new StringBuilder();
            for (int li = 0; li < lineGroups.Count; li++)
            {
                var grp = lineGroups[li];
                bool rtl = IsRtlGroup(grp);

                // For RTL lines sort descending (rightmost = first Hebrew letter).
                // For LTR sort ascending.
                if (rtl)
                    grp.Sort((a, b) => b.X.CompareTo(a.X));
                else
                    grp.Sort((a, b) => a.X.CompareTo(b.X));

                // Each letter's marks follow it, in the order they were drawn.
                if (marksOf.Count > 0)
                {
                    var withMarks = new List<PdfTextChar>(grp.Count);
                    foreach (var c in grp)
                    {
                        withMarks.Add(c);
                        if (marksOf.TryGetValue(c, out var own)) withMarks.AddRange(own);
                    }
                    grp.Clear();
                    grp.AddRange(withMarks);
                }

                // Cluster into words by horizontal gap, or by a space glyph drawn between two
                // glyphs. The gap alone misses single spacing: a 12 pt Helvetica space is 3.3 pt,
                // and 0.8 of the "o" before it is 5.3 pt.
                float[] blankCentres = BlankCentresOnLine(lineRows?[li], blanks, opts);
                // Words as runs of the sorted line — where each starts — each copied once into its own array.
                wordStarts.Clear();
                wordStarts.Add(0);
                PdfTextChar prev = grp[0];   // the last letter: a mark is measured as part of it

                for (int i = 1; i < grp.Count; i++)
                {
                    if (marksOf.Count > 0 && IsCombiningMark(grp[i].Char)) continue;
                    float gap;
                    if (rtl)
                        // Sorted descending: prev is to the right of grp[i].
                        // Gap = left edge of prev minus right edge of grp[i].
                        gap = prev.X - (grp[i].X + grp[i].Width);
                    else
                        gap = grp[i].X - (prev.X + prev.Width);

                    float avgW = prev.Width;
                    if (gap > avgW * opts.XTolerance || BlankBetween(blankCentres, prev, grp[i]))
                        wordStarts.Add(i);
                    prev = grp[i];
                }

                var words = new PdfTextWord[wordStarts.Count];
                // Line bounding box, from the same glyphs.
                float lx = float.MaxValue, ly = float.MaxValue, lr = float.MinValue, lh = 0f;
                for (int w = 0; w < wordStarts.Count; w++)
                {
                    int start = wordStarts[w], end = w + 1 < wordStarts.Count ? wordStarts[w + 1] : grp.Count;
                    var wg = new PdfTextChar[end - start];
                    grp.CopyTo(start, wg, 0, wg.Length);

                    // Bounding box: always physical min/max regardless of sort order.
                    float wx = float.MaxValue, wy = float.MaxValue, wr = float.MinValue, wh = 0f;
                    var wb = wordText.Clear();
                    bool wordRtl = TextDirection.IsRtlGroup(wg);

                    foreach (var c in wg)
                    {
                        wb.Append(c.Char);
                        if (c.X < wx) wx = c.X;
                        if (c.Y < wy) wy = c.Y;
                        float right = c.X + c.Width;
                        if (right > wr) wr = right;
                        if (c.Height > wh) wh = c.Height;
                    }

                    words[w] = new PdfTextWord(wb.ToString(), wx, wy, wr - wx, wh, wg, wordRtl);
                    if (wx < lx) lx = wx;
                    if (wy < ly) ly = wy;
                    if (wr > lr) lr = wr;
                    if (wh > lh) lh = wh;
                }

                // Line text.  By default the words are already in reading order (RTL=right-to-left,
                // LTR=left-to-right) because we sorted chars in reading order and word groups follow
                // that order.  With ApplyBidi the line text is instead rebuilt from the line's words
                // in physical (visual) order and run through the full Unicode bidi algorithm, which
                // restores logical order for mixed-direction lines without reversing embedded numbers.
                // The !isVertical half survives mutation, and the reason is worth knowing before
                // anyone deletes it as redundant: at 90° or 270° each glyph of a run lands on its
                // own Y, so the rotated glyphs group into one line PER GLYPH and ToReadingOrder is
                // handed a single word it cannot reorder. Only a 180° line — one Y, several words —
                // can tell the two programs apart, and the direction of such a line is exactly the
                // "undefined" this clause declines to guess at. It is a stated decision with no
                // fixture rather than dead code.
                string lineText;
                if (opts.ApplyBidi && !isVertical)
                {
                    // A drawn line whose paragraph nothing names: its direction from its ends and
                    // its count of strong characters (UnicodeBidi.VisualLineDirection), as the glyph
                    // overload and Core's extractor take it — not from the visual string's first
                    // strong character, which is the end of a right-to-left line.
                    lineText = TextDirection.ToReadingOrderOfLine(words);
                }
                else
                {
                    var lt = new StringBuilder();
                    bool first = true;
                    foreach (var w in words)
                    {
                        if (!first) lt.Append(' ');
                        lt.Append(w.Text);
                        first = false;
                    }
                    lineText = lt.ToString();
                }

                lines.Add(new PdfTextLine(lineText, lx, ly, lr - lx, lh, words, rtl)
                {
                    IsVertical = isVertical
                });
            }

            return lines;
        }

        /// <summary>
        /// The horizontal centres, sorted, of the blank glyphs on a line, or null when there are
        /// none: a blank is the line's when its baseline lies within the band a row of the line
        /// would join it in (<see cref="Band"/>) — the rule its glyphs were joined by — and its
        /// centre lies inside one of that row's runs, among the row's glyphs. The blanks arrive
        /// sorted by Y.
        /// </summary>
        /// <remarks>
        /// A band around the whole line, from its tallest glyph, took in the spaces of the small
        /// print above a mixed-size line and broke its words where those spaces fell. A band alone
        /// takes in the spaces of the next column's line, staggered half a line lower, and breaks
        /// the words of this column's where they fall.
        /// </remarks>
        private static float[] BlankCentresOnLine(
            List<GlyphRow> rows, List<PdfTextChar> blanksByY, WordAssemblyOptions opts)
        {
            if (rows == null || blanksByY == null || blanksByY.Count == 0) return null;

            List<float> centres = null;
            foreach (var row in rows)
            {
                // No blank's band is wider than the row's own.
                float reach = Band(row.MinH, row.MinH, opts);
                float low = row.Y - reach, high = row.Y + reach;

                int first = 0, end = blanksByY.Count;
                while (first < end)
                {
                    int mid = (first + end) >> 1;
                    if (blanksByY[mid].Y < low) first = mid + 1;
                    else end = mid;
                }

                for (int i = first; i < blanksByY.Count && blanksByY[i].Y <= high; i++)
                {
                    var blank = blanksByY[i];
                    float centre = blank.X + blank.Width / 2f;
                    if (Math.Abs(blank.Y - row.Y) <= Band(Math.Abs(blank.Height), row.MinH, opts) && InsideARun(row, centre))
                        (centres ??= new List<float>()).Add(centre);
                }
            }
            if (centres == null) return null;

            centres.Sort();
            return centres.ToArray();
        }

        // Whether x lies inside one of the row's runs, which are sorted and disjoint.
        private static bool InsideARun(GlyphRow row, float x)
        {
            int lo = 0, hi = row.Runs.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (row.Runs[mid].Left <= x) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return found >= 0 && x <= row.Runs[found].Right;
        }

        // ----------------------------------------------------------------
        //  Line grouping
        // ----------------------------------------------------------------

        /// <summary>The share of the smaller glyph's height within which two baselines are one.</summary>
        private const float SameBaselineFraction = 0.1f;

        /// <summary>How many of the most recently joined lines a row is tried against.</summary>
        private const int LinesTriedPerRow = 16;

        /// <summary>
        /// The most steps one row may take — looking through the lines it is tried against for its
        /// nearest row, and checking a join for stacking — before it keeps a line of its own. A row
        /// of a real page takes a few dozen; only a hostile page reaches this.
        /// </summary>
        internal const int MaxStepsPerRow = 1024;

        /// <summary>Glyphs on one baseline, and their runs — glyphs closer than an em, left to right.</summary>
        private sealed class GlyphRow
        {
            internal GlyphRow(int capacity) { Glyphs = new List<PdfTextChar>(capacity); }

            internal readonly List<PdfTextChar> Glyphs;
            internal float Y;
            internal float MinH = float.MaxValue;
            internal readonly List<(float Left, float Right, float H)> Runs = new List<(float, float, float)>();
        }

        /// <summary>
        /// The band two baselines are joined within: <see cref="WordAssemblyOptions.YTolerance"/> of
        /// the smaller glyph's height, and never under 2 pt. A tall glyph lends it to nothing.
        /// </summary>
        private static float Band(float h1, float h2, WordAssemblyOptions opts)
            => Math.Max(Math.Min(h1, h2) * opts.YTolerance, 2f);

        /// <summary>
        /// Upright glyphs in lines. Glyphs on one baseline — within <see cref="SameBaselineFraction"/>
        /// of the smaller one's height of the row's first — are a row, however far apart. A row joins
        /// the line one of whose rows lies within its <see cref="Band"/>, the nearest such, unless the
        /// join would stack: put two runs that overlap horizontally, their baselines further apart
        /// than their band, in one line. Lines are in the order of their first row, top first.
        /// </summary>
        /// <remarks>
        /// <para>Side by side, a centred amount beside its smaller label, a cell of a skewed
        /// recognised row and a value raised beside its label stay on the label's line. Stacked, the
        /// lines of a document's header do not chain: comparing each glyph with the one before it in
        /// a band from the tallest glyph the line holds so far, a title would lend its band to the
        /// smaller lines beside it, and its line take in the supplier's
        /// name above, the identification line below and the logo, their characters interleaved
        /// when the line was sorted by x. A raised initial merged three body lines the same way.</para>
        /// <para><b>Bounded.</b> A row is tried against the <see cref="LinesTriedPerRow"/> lines most
        /// recently joined, a line out of every band's reach is no longer tried, and a row takes at
        /// most <see cref="MaxStepsPerRow"/> steps, its nearest-row searches and stacking checks
        /// together; past them it keeps a line of its own. The steps — one per glyph for the rows,
        /// then each row's and one per line let go — are counted in
        /// <see cref="LastLineGroupingWork"/>.</para>
        /// </remarks>
        private static List<List<GlyphRow>> GroupByBaseline(List<PdfTextChar> working, WordAssemblyOptions opts)
        {
            // Rows, from glyphs sorted top to bottom (then left to right). Asked as "within", so a
            // glyph whose geometry overflowed to NaN joins no row.
            // A row is a run of the sorted glyphs; each is found first and then filled into a list of
            // its size, not grown a doubling at a time.
            var rows = new List<GlyphRow>();
            for (int start = 0; start < working.Count; )
            {
                var first = working[start];
                float firstH = Math.Abs(first.Height);
                int end = start + 1;
                while (end < working.Count)
                {
                    var g = working[end];
                    if (!(Math.Abs(g.Y - first.Y) <= SameBaselineFraction * Math.Min(Math.Abs(g.Height), firstH))) break;
                    end++;
                }
                var current = new GlyphRow(end - start) { Y = first.Y };
                for (int i = start; i < end; i++)
                {
                    _lastLineGroupingWork++;
                    var g = working[i];
                    current.Glyphs.Add(g);
                    float h = Math.Abs(g.Height);
                    if (h < current.MinH) current.MinH = h;
                }
                rows.Add(current);
                start = end;
            }
            foreach (var row in rows) Runs(row);

            // Lines: each row joins the nearest line within its band that it stacks on nothing in.
            var lines = new List<List<GlyphRow>>();
            var open = new List<(List<GlyphRow> Rows, float MaxMinH)>();   // most recently joined last
            var candidates = new List<(float Dy, List<GlyphRow> Rows)>();
            foreach (var row in rows)
            {
                candidates.Clear();
                int steps = 0;
                for (int k = open.Count - 1, tried = 0; k >= 0 && tried < LinesTriedPerRow; k--)
                {
                    var (lineRows, maxMinH) = open[k];
                    // Rows arrive top first, so a line's last row is its lowest; past every band's
                    // reach, nothing below this row can join it either.
                    if (lineRows[lineRows.Count - 1].Y - row.Y > Band(maxMinH, maxMinH, opts))
                    {
                        _lastLineGroupingWork++;
                        open.RemoveAt(k);
                        continue;
                    }
                    tried++;
                    float nearest = NearestRow(lineRows, row, opts, ref steps);
                    if (nearest != float.MaxValue) candidates.Add((nearest, lineRows));
                }

                List<GlyphRow> joined = null;
                candidates.Sort((a, b) => a.Dy.CompareTo(b.Dy));
                foreach (var (_, lineRows) in candidates)
                    if (StacksOnNothing(lineRows, row, opts, ref steps)) { joined = lineRows; break; }
                _lastLineGroupingWork += steps;
                if (steps > _lastLineGroupingRowSteps) _lastLineGroupingRowSteps = steps;

                if (joined == null)
                {
                    var line = new List<GlyphRow> { row };
                    lines.Add(line);
                    open.Add((line, row.MinH));
                }
                else
                {
                    int at = open.FindIndex(o => ReferenceEquals(o.Rows, joined));
                    float maxMinH = open[at].MaxMinH;
                    joined.Add(row);
                    open.RemoveAt(at);
                    open.Add((joined, Math.Max(maxMinH, row.MinH)));
                }
            }
            return lines;
        }

        // A row's runs: its glyphs left to right, a run ending where the gap to the next glyph is
        // wider than either glyph's height — a column's gap, not a word space.
        private static void Runs(GlyphRow row)
        {
            row.Glyphs.Sort((a, b) => Math.Min(a.X, a.X + a.Width).CompareTo(Math.Min(b.X, b.X + b.Width)));
            float left = 0f, right = 0f, h = 0f;
            bool started = false;
            foreach (var g in row.Glyphs)
            {
                float gl = Math.Min(g.X, g.X + g.Width), gr = Math.Max(g.X, g.X + g.Width), gh = Math.Abs(g.Height);
                if (started && gl - right <= Math.Max(h, gh))
                {
                    if (gr > right) right = gr;
                    if (gh < h) h = gh;
                    continue;
                }
                if (started) row.Runs.Add((left, right, h));
                left = gl; right = gr; h = gh; started = true;
            }
            if (started) row.Runs.Add((left, right, h));
        }

        // How far `row` lies from the nearest row of `line` it is within the band of, or MaxValue
        // when there is none — or when the row is out of steps.
        private static float NearestRow(List<GlyphRow> line, GlyphRow row, WordAssemblyOptions opts, ref int steps)
        {
            float nearest = float.MaxValue;
            foreach (var q in line)
            {
                if (steps >= MaxStepsPerRow) return float.MaxValue;
                steps++;
                float dy = Math.Abs(q.Y - row.Y);
                if (dy <= Band(q.MinH, row.MinH, opts) && dy < nearest) nearest = dy;
            }
            return nearest;
        }

        // Whether `row` joins `line` without stacking: no run of it overlaps one of the line's
        // horizontally with their baselines further apart than their band. A row's runs are sorted
        // and disjoint, so two rows' runs are walked together. Out of steps, the answer is no — the
        // row keeps a line of its own.
        private static bool StacksOnNothing(List<GlyphRow> line, GlyphRow row, WordAssemblyOptions opts, ref int steps)
        {
            foreach (var q in line)
            {
                float dy = Math.Abs(q.Y - row.Y);
                for (int i = 0, j = 0; i < row.Runs.Count && j < q.Runs.Count; )
                {
                    if (steps >= MaxStepsPerRow) return false;
                    steps++;
                    var a = row.Runs[i];
                    var b = q.Runs[j];
                    if (a.Left < b.Right && b.Left < a.Right && dy > Band(a.H, b.H, opts)) return false;
                    if (a.Right < b.Right) i++; else j++;
                }
            }
            return true;
        }

        /// <summary>
        /// Rotated glyphs in lines: a glyph joins the line of the glyph before it within the band of
        /// the tallest glyph the line holds; at 90° or 270° each glyph
        /// of a run lands on its own Y. The tallest is kept as the line grows, not looked for again.
        /// </summary>
        private static List<List<PdfTextChar>> GroupByBand(List<PdfTextChar> working, WordAssemblyOptions opts)
        {
            var lineGroups = new List<List<PdfTextChar>>();
            var current = new List<PdfTextChar> { working[0] };
            float maxH = Math.Max(0f, working[0].Height);
            for (int i = 1; i < working.Count; i++)
            {
                _lastLineGroupingWork++;
                float tol = Math.Max(maxH * opts.YTolerance, 2f);
                if (Math.Abs(working[i].Y - current[current.Count - 1].Y) <= tol)
                {
                    current.Add(working[i]);
                    if (working[i].Height > maxH) maxH = working[i].Height;
                }
                else
                {
                    lineGroups.Add(current);
                    current = new List<PdfTextChar> { working[i] };
                    maxH = Math.Max(0f, working[i].Height);
                }
            }
            lineGroups.Add(current);
            return lineGroups;
        }

        /// <summary>
        /// True when a blank glyph's centre lies strictly between the centres of two glyphs, in
        /// either order, so a right-to-left line works the same way.
        /// </summary>
        private static bool BlankBetween(float[] blankCentres, PdfTextChar a, PdfTextChar b)
        {
            if (blankCentres == null) return false;

            float ca = a.X + a.Width / 2f, cb = b.X + b.Width / 2f;
            float left = Math.Min(ca, cb), right = Math.Max(ca, cb);

            int first = 0, end = blankCentres.Length;
            while (first < end)
            {
                int mid = (first + end) >> 1;
                if (blankCentres[mid] <= left) first = mid + 1;
                else end = mid;
            }
            return first < blankCentres.Length && blankCentres[first] < right;
        }

        // ----------------------------------------------------------------
        //  Marked-content helper
        // ----------------------------------------------------------------

        /// <summary>
        /// Glyphs the marked-content sections' reversal and <c>/ActualText</c> passes walked on the
        /// calling thread: the evidence that nested sections do their work once.
        /// </summary>
        [ThreadStatic] internal static long MarkedContentWork;

        /// <summary>One open marked-content section.</summary>
        /// <remarks>
        /// <c>StartIndex</c> is where this section's glyphs begin in the output list, which is the
        /// only way to know at <c>EMC</c> what §14.9.4's "the marked-content sequence's content"
        /// turned out to be.
        /// </remarks>
        private readonly struct MarkedContent
        {
            internal MarkedContent(int mcid, string actualText, int startIndex, bool reversed, bool hiddenLayer = false)
            {
                Mcid = mcid;
                ActualText = actualText;
                StartIndex = startIndex;
                Reversed = reversed;
                HiddenLayer = hiddenLayer;
            }

            internal int Mcid { get; }
            internal string ActualText { get; }
            internal int StartIndex { get; }
            /// <summary>Whether the section is tagged §14.8.2.3.3 <c>/ReversedChars</c>.</summary>
            internal bool Reversed { get; }
            /// <summary>Whether the section is optional content that is off.</summary>
            internal bool HiddenLayer { get; }
        }

        /// <summary>
        /// Marks a <c>/ReversedChars</c> section's glyphs (<see cref="PdfTextChar.InReversedRun"/>),
        /// so a reader building a visual line for the bidi algorithm takes their drawn shapes.
        /// </summary>
        private static void MarkReversedRun(List<PdfTextChar> output, MarkedContent section)
        {
            int start = Math.Max(0, Math.Min(section.StartIndex, output.Count));
            MarkedContentWork += output.Count - start;
            for (int i = start; i < output.Count; i++)
            {
                PdfTextChar c = output[i];
                output[i] = new PdfTextChar(c.Char, c.X, c.Y, c.Width, c.Height, c.IsRtl)
                {
                    Style = c.Style,
                    Mcid = c.Mcid,
                    McidStreamObject = c.McidStreamObject,
                    IsInvisible = c.IsInvisible,
                    InReversedRun = true,
                };
            }
        }

        /// <summary>
        /// Replaces a marked-content section's glyphs with its <c>/ActualText</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>§14.9.4: the string is "an exact replacement for the marked-content sequence's
        /// content".</b> It is what a producer writes when the glyphs do not spell what the text
        /// says — an <c>fi</c> ligature drawn from one glyph, a phone number set in a decorative
        /// face, a date assembled from separate runs. Extraction that ignores it returns what was
        /// drawn rather than what was written, and the two differ precisely where the producer
        /// thought it mattered enough to say so.</para>
        ///
        /// <para><b>The replacement characters are spread across the extent of the glyphs they
        /// replace</b>, keeping the first glyph's font, size, colour and MCID. Positions have to
        /// come from somewhere, and the run's own extent is the only honest source: it puts the
        /// replacement where the reader sees it, so word and line grouping downstream still work
        /// on geometry rather than on invented coordinates.</para>
        ///
        /// <para><b>A section that drew no glyphs is left alone</b>, deliberately. <c>/ActualText</c>
        /// on an image or an empty span has no extent to place text at, and inventing one would put
        /// a word somewhere the page does not have it. That case belongs to the tagged extractor,
        /// which reads the structure element's own <c>/Alt</c> and <c>/ActualText</c> and knows
        /// where the element is.</para>
        ///
        /// <para><b>Right-to-left is taken from the replacement, not from the glyphs.</b> The whole
        /// point is that the two can disagree, and a Hebrew <c>/ActualText</c> over Latin-looking
        /// glyphs must group as Hebrew.</para>
        /// </remarks>
        private static void ApplyActualText(List<PdfTextChar> output, MarkedContent section)
        {
            string actual = section.ActualText;
            if (actual == null) return;

            int start = Math.Max(0, Math.Min(section.StartIndex, output.Count));
            int count = output.Count - start;
            if (count <= 0) return;                     // nothing was drawn; see the remarks
            MarkedContentWork += count;

            PdfTextChar first = output[start];

            float left = float.MaxValue, right = float.MinValue;
            float top = float.MinValue, bottom = float.MaxValue;
            bool invisible = true;
            for (int i = start; i < output.Count; i++)
            {
                PdfTextChar c = output[i];
                if (c.X < left) left = c.X;
                if (c.X + c.Width > right) right = c.X + c.Width;
                if (c.Y > top) top = c.Y;
                if (c.Y < bottom) bottom = c.Y;
                if (!c.IsInvisible) invisible = false;
            }

            output.RemoveRange(start, count);

            if (actual.Length == 0) return;             // /ActualText () — the run contributes none

            // One cell per replacement character, across the extent the glyphs occupied. Text
            // elements are enumerated rather than indexed so a surrogate pair or a combining mark
            // stays one cell, which is what the glyph run it replaced was.
            var cells = new List<string>();
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(actual);
            while (enumerator.MoveNext()) cells.Add((string)enumerator.Current);
            if (cells.Count == 0) return;

            float width = right > left ? (right - left) / cells.Count : first.Width;

            for (int i = 0; i < cells.Count; i++)
            {
                char firstNonSpace = FirstNonSpace(cells[i]);

                output.Add(new PdfTextChar(
                    cells[i], left + width * i, bottom, width, top - bottom + first.Height,
                    IsRtlChar(firstNonSpace))
                {
                    Style = first.Style,
                    Mcid = first.Mcid,
                    McidStreamObject = first.McidStreamObject,
                    // Visible if any glyph it replaces was: the replacement stands for what is seen.
                    IsInvisible = invisible,
                });
            }
        }

        // ----------------------------------------------------------------
        //  Invisible text laid over visible text
        // ----------------------------------------------------------------

        /// <summary>Side of one cell of the grid visible glyphs are indexed by, in points.</summary>
        private const float CoverCellSize = 16f;

        /// <summary>
        /// A visible glyph whose box spans more cells than this on either axis goes in the list of
        /// large glyphs rather than into every cell it touches.
        /// </summary>
        private const long MaxCoverCellSpan = 8;

        /// <summary>
        /// Removes every invisible glyph that copies visible text drawn at the same place.
        /// </summary>
        /// <remarks>
        /// <para><b>Invisible text is text.</b> Render modes 3 and 7 are how an OCR tool, and
        /// <c>PdfOperations.MakeSearchable</c>, put searchable text over a scanned page, and
        /// <c>PdfOperations.ExtractText</c> returns it. Dropping every such glyph, a scanned page
        /// would extract as nothing, and search-driven redaction, which reads this extractor, find
        /// nothing to remove.</para>
        ///
        /// <para><b>A copy laid over visible glyphs is still dropped</b>, which is why the modes
        /// were filtered at all. A statement template draws a searchable layer at the position of
        /// its styled text. Kept, the two copies interleave once sorted by X, and every character
        /// comes out twice.</para>
        ///
        /// <para><b>An invisible glyph is a copy when a visible glyph covers its centre</b>, at a
        /// similar size and the same rotation. The centre rather than the origin, so a layer set in
        /// a different font, whose glyphs drift against the visible ones, still matches. The size,
        /// so a large watermark letter over a scanned page does not swallow the OCR words under
        /// it, since a copy of a word is set at about the word's size.</para>
        /// </remarks>
        private static void DropInvisibleCopies(List<PdfTextChar> chars)
        {
            bool anyVisible = false, anyInvisible = false;
            foreach (var c in chars)
            {
                if (c.IsInvisible) anyInvisible = true;
                else anyVisible = true;
            }
            if (!anyVisible || !anyInvisible) return;

            var grid = new Dictionary<(long, long), List<PdfTextChar>>();
            var large = new List<PdfTextChar>();
            foreach (var v in chars)
            {
                if (v.IsInvisible) continue;

                long x0 = CoverCell(Math.Min(v.X, v.X + v.Width)), x1 = CoverCell(Math.Max(v.X, v.X + v.Width));
                long y0 = CoverCell(Math.Min(v.Y, v.Y + v.Height)), y1 = CoverCell(Math.Max(v.Y, v.Y + v.Height));
                long spanX = x1 - x0, spanY = y1 - y0;
                if (spanX > MaxCoverCellSpan || spanY > MaxCoverCellSpan)
                {
                    if (large.Count < PdfLimits.MaxInvisibleGlyphCoverCandidates) large.Add(v);
                    continue;
                }

                // Counted rather than run from x0 to x1: a coordinate too large for a cell number
                // converts to an extreme value, and a loop run up to one can wrap and never end.
                for (long i = 0; i <= spanX; i++)
                    for (long j = 0; j <= spanY; j++)
                    {
                        var key = (x0 + i, y0 + j);
                        if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<PdfTextChar>();
                        cell.Add(v);
                    }
            }

            chars.RemoveAll(g => g.IsInvisible && IsCoveredByVisibleCopy(g, grid, large));
        }

        private static bool IsCoveredByVisibleCopy(
            PdfTextChar invisible, Dictionary<(long, long), List<PdfTextChar>> grid, List<PdfTextChar> large)
        {
            float cx = invisible.X + invisible.Width / 2f;
            float cy = invisible.Y + invisible.Height / 2f;

            if (grid.TryGetValue((CoverCell(cx), CoverCell(cy)), out var cell))
            {
                int candidates = Math.Min(cell.Count, PdfLimits.MaxInvisibleGlyphCoverCandidates);
                for (int i = 0; i < candidates; i++)
                    if (IsCopyAt(cell[i], invisible, cx, cy)) return true;
            }

            foreach (var v in large)
                if (IsCopyAt(v, invisible, cx, cy)) return true;

            return false;
        }

        private static bool IsCopyAt(PdfTextChar visible, PdfTextChar invisible, float cx, float cy)
        {
            if (visible.Rotation != invisible.Rotation) return false;

            float visibleSize = Math.Abs(visible.Height), invisibleSize = Math.Abs(invisible.Height);
            if (visibleSize > 2f * invisibleSize || invisibleSize > 2f * visibleSize) return false;

            return cx >= Math.Min(visible.X, visible.X + visible.Width)
                && cx <= Math.Max(visible.X, visible.X + visible.Width)
                && cy >= Math.Min(visible.Y, visible.Y + visible.Height)
                && cy <= Math.Max(visible.Y, visible.Y + visible.Height);
        }

        private static long CoverCell(float coordinate) => SaturatingCast.ToLong(Math.Floor(coordinate / CoverCellSize));

        /// <summary>
        /// Removes a glyph drawn again, with the same text and in the same marked-content sequence,
        /// at nearly the same place as an earlier one.
        /// </summary>
        /// <remarks>
        /// <para><b>Visible text is often drawn twice.</b> A layout program strokes and then fills its
        /// slug line at one position, and "fake bold" repaints a run a fraction of a point to the side.
        /// Neither copy is invisible, so <see cref="DropInvisibleCopies"/> does not apply, and once
        /// sorted by X the two interleave: <c>PEC_CH00_FM_2PP.indd</c> came back as
        /// <c>PPEECC__CCHH0000__FFMM__22PPPP..iinndddd</c>.</para>
        ///
        /// <para><b>The rule is the established one</b>, and plain extraction applies it too, through the one
        /// implementation: <see cref="OverlappingTextIndex{TKey}"/>. The earlier copy is the one
        /// kept.</para>
        ///
        /// <para><b>One addition: both copies must share a marked-content sequence.</b> Where one
        /// copy is tagged and the other is not, keeping the earlier could drop the glyph a structure
        /// element owns, and tagged extraction would lose its text. Doubled text is the lesser
        /// harm.</para>
        /// </remarks>
        private static void DropOverlappingDuplicates(List<PdfTextChar> chars,
            OverlappingTextIndex<(string Text, int Mcid, int Stream)> drawn)
        {
            int kept = 0;
            for (int i = 0; i < chars.Count; i++)
            {
                var c = chars[i];
                bool copy = !string.IsNullOrEmpty(c.Char) &&
                            drawn.IsCopyOfEarlier((c.Char, c.Mcid, c.McidStreamObject), c.X, c.Y, c.Width, c.Char.Length);
                if (!copy) chars[kept++] = c;
            }
            chars.RemoveRange(kept, chars.Count - kept);
        }

        // ----------------------------------------------------------------
        //  Font decode helper
        // ----------------------------------------------------------------

        /// <summary>One code's text — the font's, from its per-code cache; Latin-1 of the code's bytes when no font is named.</summary>
        private static string DecodeCode(
            Core.Fonts.CMapCodec.Code code, string fontName,
            PdfDictionary resources, PdfDocument doc,
            Rendering.FontCache fontCache)
        {
            if (fontName == null || resources == null || doc == null)
                return Core.Text.Latin1Text.Decode(Core.Fonts.PdfFont.CodeBytes(code));
            return fontCache.DecodeCode(fontName, code, resources, doc);
        }

        private static PdfDictionary LookupFontDict(string fontName, PdfDictionary resources, PdfDocument doc)
        {
            if (fontName == null || resources == null || doc == null) return null;
            var fontRes = resources[PdfName.Font];
            if (fontRes == null) return null;
            var fontDict = doc.ResolveObject(fontRes) as PdfDictionary;
            if (fontDict == null) return null;
            var entry = fontDict[new PdfName(fontName)];
            if (entry == null) return null;
            return doc.ResolveObject(entry) as PdfDictionary ?? entry as PdfDictionary;
        }

        // ----------------------------------------------------------------
        //  Matrix helpers
        // ----------------------------------------------------------------

        private static float[] Identity() => Rendering.PdfMatrix.Identity();
        private static float[] CopyArr(float[] m) => (float[])m.Clone();

        // Through Rendering.PdfMatrix — one of four byte-identical copies of §8.3.3.
        private static float[] Compose(float[] A, float[] B) => Rendering.PdfMatrix.Compose(A, B);

        // ----------------------------------------------------------------
        //  Color / rotation helpers (Phase 2)
        // ----------------------------------------------------------------

        private static float[] GrayToRgb(float g) => new[] { g, g, g };

        private static float[] CollectFloatOperands(ContentStreamOperation op)
        {
            var result = new List<float>(4);
            foreach (var o in op.Operands)
                if (o is PdfNumber n) result.Add((float)n.DoubleValue);
            return result.Count > 0 ? result.ToArray() : null;
        }

        // Returns rotation in degrees quantised to nearest 90° from the [a b c d e f] matrix.
        private static float DeriveRotation(float[] m)
        {
            // PDF text matrix: a=cos θ·sx, b=sin θ·sx  (columns of rotation×scale)
            float a = m[0], b = m[1];
            float angleDeg = (float)(Math.Atan2(b, a) * (180.0 / Math.PI));
            // Normalise to [0, 360)
            if (angleDeg < 0f) angleDeg += 360f;
            // Quantise to nearest 90°
            int quad = SaturatingCast.ToInt(Math.Round(angleDeg / 90f)) % 4;
            return quad * 90f;
        }

        // ----------------------------------------------------------------
        //  Minimal graphics-state bag for text extraction
        // ----------------------------------------------------------------

        private sealed class TextGs
        {
            public float[] CTM  = { 1, 0, 0, 1, 0, 0 };
            public float[] Tm   = { 1, 0, 0, 1, 0, 0 };
            public float[] Tlm  = { 1, 0, 0, 1, 0, 0 };
            public string FontName    = null;
            public float  FontSize    = 12f;
            public float  CharSpacing = 0f;
            public float  WordSpacing = 0f;
            public float  HorizScale  = 1f;
            public float  Leading     = 0f;
            public float  TextRise    = 0f;
            /// <summary>ISO 32000-1 Table 106. Mode 3 ("invisible") and mode 7 ("add to clip,
            /// neither fill nor stroke") paint no ink — the standard technique for a searchable
            /// OCR/accessibility text layer positioned under or alongside styled visible content.
            /// EmitString marks such glyphs, and DropInvisibleCopies removes the ones that copy
            /// visible text.</summary>
            public int TextRenderMode = 0;
            public float[] FillColor   = null;   // non-stroking color (RGB, CMYK, or gray)
            public float[] StrokeColor = null;   // stroking color
            /// <summary>The groups that are off, read once a page, and only if the page asks.</summary>
            public Lazy<ISet<int>> HiddenLayers;
            /// <summary>Whether what is drawn now stands in optional content that is off.</summary>
            public bool InHiddenLayer;

            public TextGs Clone()
            {
                var c = (TextGs)MemberwiseClone();
                c.CTM         = (float[])CTM.Clone();
                c.Tm          = (float[])Tm.Clone();
                c.Tlm         = (float[])Tlm.Clone();
                c.FillColor   = FillColor   != null ? (float[])FillColor.Clone()   : null;
                c.StrokeColor = StrokeColor != null ? (float[])StrokeColor.Clone() : null;
                return c;
            }
        }
    }

    // ====================================================================
    //  Public result types
    // ====================================================================

    /// <summary>Controls how characters are grouped into words and lines during extraction.</summary>
    public sealed class WordAssemblyOptions
    {
        /// <summary>
        /// A positive gap between adjacent characters (measured in fractions of the preceding
        /// character's advance width) triggers a new word boundary.  Default 0.8.
        /// Set to 0 to split every character into its own word; set to a large value (e.g. 999)
        /// to collapse all chars on the same baseline into a single word, except where a space
        /// glyph separates them (see <see cref="KeepBlankChars"/>).
        /// </summary>
        public float XTolerance { get; init; } = 0.8f;

        /// <summary>
        /// Maximum vertical distance between two baselines (in fractions of the smaller character's
        /// height, and never under 2 pt) that still joins them into one text line — and the most two
        /// characters one above the other on a line may be apart, so a line never takes in the line
        /// above or below one of its own.  Rotated text is joined within this fraction of the
        /// tallest character's height.  Default 0.5.
        /// </summary>
        public float YTolerance { get; init; } = 0.5f;

        /// <summary>
        /// When false (default), blank/whitespace characters are excluded from word assembly
        /// (they are used only for spacing): a blank glyph drawn between two glyphs of a line
        /// ends the word, however narrow the gap it leaves.  When true, they appear as PdfTextChar
        /// entries inside words, matching pdfplumber's keep_blank_chars=True behaviour.
        /// </summary>
        public bool KeepBlankChars { get; init; } = false;

        /// <summary>
        /// When true, each assembled line's text is rebuilt in logical reading order via the full
        /// Unicode bidi algorithm (<see cref="Tetitulz.Pdf.Core.Text.UnicodeBidi"/>) instead of the
        /// geometric right-to-left/left-to-right sort.  This fixes mixed-direction lines common in
        /// Hebrew/Arabic invoices (e.g. an RTL label followed by an LTR amount or part-code): the
        /// label reads right-to-left while embedded numbers/codes stay intact.  Character, word, and
        /// line bounding boxes are unaffected — only <see cref="PdfTextLine.Text"/> changes.
        /// <para>Default <c>false</c> so existing string-exact output is preserved; opt in for invoice
        /// / RTL extraction.  Rotated (vertical) lines are left untouched (direction is undefined).</para>
        /// </summary>
        public bool ApplyBidi { get; init; } = false;

        /// <summary>
        /// When true, the text the page's annotation appearances draw — a filled field's value, a
        /// free-text note, a stamp's caption — is extracted beside the page's own, each appearance
        /// placed where a reader shows it (§12.5.5: its <c>/BBox</c> through its <c>/Matrix</c>,
        /// fitted onto <c>/Rect</c>). An annotation a reader does not present — hidden, on a layer
        /// that is off, a popup — adds nothing. Default <c>false</c>: the page's content alone, as a
        /// reader of the content stream sees it.
        /// </summary>
        public bool IncludeAnnotationAppearances { get; init; } = false;

        /// <summary>Shared default instance (equivalent to a default-constructed instance).</summary>
        public static readonly WordAssemblyOptions Default = new WordAssemblyOptions();
    }

    /// <summary>A single extracted character with its approximate bounding box.</summary>
    public sealed class PdfTextChar
    {
        /// <summary>Unicode character string (usually one char, occasionally a ligature).</summary>
        public string Char   { get; }
        /// <summary>X coordinate of the glyph's baseline origin in PDF points (origin = bottom-left).</summary>
        public float X       { get; }
        /// <summary>Y coordinate of the glyph's baseline origin in PDF points.</summary>
        public float Y       { get; }
        /// <summary>Approximate advance width in PDF points, including the scale carried by the
        /// text matrix and CTM (so <c>1 Tf</c> under a <c>12 0 0 12</c> matrix reports the same
        /// width as <c>12 Tf</c> under an identity one).</summary>
        public float Width   { get; }
        /// <summary>Approximate glyph height in PDF points: the <c>Tf</c> size scaled by the text
        /// matrix and CTM. Equal to the <c>Tf</c> size only when that combined scale is 1.</summary>
        public float Height  { get; }
        /// <summary>True if this character belongs to a right-to-left script (Hebrew, Arabic).</summary>
        public bool IsRtl    { get; }

        // -- Phase 2 per-character metadata --

        // The text state a glyph was drawn in — font, size, colours, rotation — is one shared,
        // immutable object (PdfTextGlyphStyle), since every glyph of a run has the same one. A
        // glyph was 88 bytes with them inline and is 64 with the reference; the properties read and
        // initialise exactly as before, an initialiser making the glyph its own copy.
        private PdfTextGlyphStyle _style = PdfTextGlyphStyle.Default;

        /// <summary>The glyph's text state, shared with the glyphs drawn in the same one.</summary>
        internal PdfTextGlyphStyle Style { get => _style; init => _style = value ?? PdfTextGlyphStyle.Default; }

        /// <summary>Resolved BaseFont name (e.g. "Helvetica-Bold"), or the resource key if not resolvable. Null for characters emitted without font context.</summary>
        public string FontName
        {
            get => _style.FontName;
            init => _style = _style.With(value, _style.FontSize, _style.Fill, _style.Stroke, _style.IsUpright, _style.Rotation, _style.VerticalWriting);
        }
        /// <summary>Effective font size in PDF points (Tf size × Tz/100 horizontal scale factor).</summary>
        public float FontSize
        {
            get => _style.FontSize;
            init => _style = _style.With(_style.FontName, value, _style.Fill, _style.Stroke, _style.IsUpright, _style.Rotation, _style.VerticalWriting);
        }
        /// <summary>
        /// Non-stroking (fill) color. RGB → float[3] [r,g,b]; CMYK → float[4] [c,m,y,k]; null if no
        /// explicit color set. Each read returns the caller's own copy.
        /// </summary>
        /// <remarks>
        /// <b>Stored once per colour, copied per read.</b> The extractor gave every glyph its own
        /// copy of the fill and the stroke colour, so that a caller changing one glyph's array
        /// changed no other's. Two arrays a glyph were, on a page that sets a colour (nearly every
        /// page), a third of extraction's allocation and half its time. The glyphs drawn in one
        /// colour hold one array, never written after it is made, and a copy is made where a caller
        /// asks for it — isolation paid for only by a reader.
        /// </remarks>
        public float[] FillColor
        {
            get => _style.Fill == null ? null : (float[])_style.Fill.Clone();
            init => _style = _style.With(_style.FontName, _style.FontSize,
                value == null ? null : (float[])value.Clone(), _style.Stroke, _style.IsUpright, _style.Rotation, _style.VerticalWriting);
        }
        /// <summary>Stroking color. Same encoding as FillColor, and copied per read alike. Null if no explicit stroking color set.</summary>
        public float[] StrokeColor
        {
            get => _style.Stroke == null ? null : (float[])_style.Stroke.Clone();
            init => _style = _style.With(_style.FontName, _style.FontSize, _style.Fill,
                value == null ? null : (float[])value.Clone(), _style.IsUpright, _style.Rotation, _style.VerticalWriting);
        }

        /// <summary>
        /// The stored fill colour, shared by the glyphs drawn in it and never written: for a reader
        /// inside this assembly that will not change it.
        /// </summary>
        internal float[] SharedFillColor => _style.Fill;

        /// <summary>The stored stroke colour, as <see cref="SharedFillColor"/>.</summary>
        internal float[] SharedStrokeColor => _style.Stroke;
        /// <summary>True when the text matrix carries no significant rotation (|angle| &lt; 15°).</summary>
        public bool IsUpright
        {
            get => _style.IsUpright;
            init => _style = _style.With(_style.FontName, _style.FontSize, _style.Fill, _style.Stroke, value, _style.Rotation, _style.VerticalWriting);
        }
        /// <summary>Text rotation in degrees, quantised to 0, 90, 180, or 270.</summary>
        public float Rotation
        {
            get => _style.Rotation;
            init => _style = _style.With(_style.FontName, _style.FontSize, _style.Fill, _style.Stroke, _style.IsUpright, value, _style.VerticalWriting);
        }
        /// <summary>
        /// True when a vertical font drew the glyph (<c>/WMode 1</c>, §9.7.4.3): it stands in a
        /// column, which <see cref="PdfStructuredTextExtractor.BuildLines"/> assembles as one line.
        /// </summary>
        internal bool VerticalWriting => _style.VerticalWriting;
        /// <summary>Marked-content identifier (/MCID) of the enclosing tagged sequence, or -1 when the
        /// glyph is not inside a marked-content sequence. Used to bind glyphs to logical-structure
        /// elements during tagged-PDF extraction (<see cref="PdfTaggedTextExtractor"/>).
        /// <para><b>An MCID is only unique within one content stream</b> — see
        /// <see cref="McidStreamObject"/>, which must be compared alongside it.</para></summary>
        public int Mcid           { get; init; } = -1;

        /// <summary>
        /// Object number of the content stream that defined <see cref="Mcid"/>: <c>0</c> for the
        /// page's own content, otherwise the invoked Form XObject's object number.
        /// </summary>
        /// <remarks>
        /// §14.7.4.2 scopes an MCID to its content stream, not to its page, and extraction
        /// recurses into Form XObjects. Keying marked content on <c>(page, mcid)</c> alone
        /// therefore merged a page's own <c>/H1 &lt;&lt;/MCID 0&gt;&gt;</c> with a form's
        /// <c>/P &lt;&lt;/MCID 0&gt;&gt;</c> into one string that both structure elements then
        /// received — and Word and LibreOffice routinely put headers, footers and figures in
        /// Form XObjects. The <c>/MCR</c> entry that disambiguates them is <c>/Stm</c>.
        /// </remarks>
        public int McidStreamObject { get; init; }

        /// <summary>
        /// True when the glyph was drawn in text render mode 3 or 7, which paint nothing — the
        /// searchable layer an OCR tool puts over a scanned page — or in optional content that is off.
        /// </summary>
        internal bool IsInvisible { get; init; }

        /// <summary>
        /// True when the glyph was drawn inside a §14.8.2.3.3 <c>/ReversedChars</c> sequence: it
        /// stands where a right-to-left line draws it, but decodes to its logical character, so a
        /// bracket is the one it is rather than the one it looks like. A reader building a visual
        /// line for the bidi algorithm takes its drawn shape
        /// (<see cref="TextDirection.VisualText"/>); one reading the glyphs in place does not.
        /// </summary>
        internal bool InReversedRun { get; init; }

        public PdfTextChar(string ch, float x, float y, float w, float h, bool isRtl = false)
        { Char = ch; X = x; Y = y; Width = w; Height = h; IsRtl = isRtl; }   // the default style is upright
    }

    /// <summary>
    /// The text state a <see cref="PdfTextChar"/> was drawn in — font name, size, fill and stroke
    /// colour, rotation — shared by the glyphs drawn in the same one and never written after it is
    /// made. The extractor reuses the previous glyph's when it <see cref="Matches"/>, which on a
    /// run of text is every glyph after the first.
    /// </summary>
    internal sealed class PdfTextGlyphStyle
    {
        /// <summary>No font, size 0, no colours, upright, horizontal — a glyph built without a text state.</summary>
        internal static readonly PdfTextGlyphStyle Default = new PdfTextGlyphStyle(null, 0f, null, null, true, 0f, false);

        internal readonly string FontName;
        internal readonly float FontSize;
        internal readonly float[] Fill;
        internal readonly float[] Stroke;
        internal readonly bool IsUpright;
        internal readonly float Rotation;
        /// <summary>Drawn by a vertical font (<c>/WMode 1</c>, §9.7.4.3): the run stands down a column.</summary>
        internal readonly bool VerticalWriting;

        internal PdfTextGlyphStyle(string fontName, float fontSize, float[] fill, float[] stroke,
            bool isUpright, float rotation, bool verticalWriting)
        {
            FontName = fontName; FontSize = fontSize; Fill = fill; Stroke = stroke;
            IsUpright = isUpright; Rotation = rotation; VerticalWriting = verticalWriting;
        }

        /// <summary>This style when it already is the one described, else a new one.</summary>
        internal PdfTextGlyphStyle With(string fontName, float fontSize, float[] fill, float[] stroke,
            bool isUpright, float rotation, bool verticalWriting)
            => Matches(fontName, fontSize, fill, stroke, isUpright, rotation, verticalWriting)
                ? this
                : new PdfTextGlyphStyle(fontName, fontSize, fill, stroke, isUpright, rotation, verticalWriting);

        /// <summary>
        /// Whether this is the style described: the same font name and colour arrays (by identity —
        /// a colour operator makes a new array), and the same numbers. A NaN never matches, which
        /// costs a new style, never a wrong one.
        /// </summary>
        internal bool Matches(string fontName, float fontSize, float[] fill, float[] stroke,
            bool isUpright, float rotation, bool verticalWriting)
            => ReferenceEquals(Fill, fill) && ReferenceEquals(Stroke, stroke)
               && string.Equals(FontName, fontName, StringComparison.Ordinal)
               && FontSize == fontSize && IsUpright == isUpright && Rotation == rotation
               && VerticalWriting == verticalWriting;
    }

    /// <summary>A run of adjacent characters forming a single word.</summary>
    public sealed class PdfTextWord
    {
        public string Text   { get; }
        public float X       { get; }
        public float Y       { get; }
        public float Width   { get; }
        public float Height  { get; }
        public IReadOnlyList<PdfTextChar> Chars { get; }
        /// <summary>True if this word is written right-to-left.</summary>
        public bool IsRtl    { get; }

        public PdfTextWord(string text, float x, float y, float w, float h,
                           IReadOnlyList<PdfTextChar> chars, bool isRtl = false)
        { Text = text; X = x; Y = y; Width = w; Height = h; Chars = chars; IsRtl = isRtl; }

        public override string ToString() => Text;
    }

    /// <summary>A horizontal line of text composed of one or more words.</summary>
    public sealed class PdfTextLine
    {
        public string Text   { get; }
        public float X       { get; }
        public float Y       { get; }
        public float Width   { get; }
        public float Height  { get; }
        public IReadOnlyList<PdfTextWord> Words { get; }
        /// <summary>True if this line has a dominant right-to-left writing direction.</summary>
        public bool IsRtl    { get; }
        /// <summary>
        /// True when the line runs down the page: every character has a non-zero rotation (rotated
        /// text), or the line is a vertical font's column (§9.7.4.3), its text read top to bottom.
        /// </summary>
        public bool IsVertical { get; init; }

        public PdfTextLine(string text, float x, float y, float w, float h,
                           IReadOnlyList<PdfTextWord> words, bool isRtl = false)
        { Text = text; X = x; Y = y; Width = w; Height = h; Words = words; IsRtl = isRtl; }

        public override string ToString() => Text;
    }

    /// <summary>All text lines extracted from a single PDF page.</summary>
    public sealed class PdfPageTextLayout
    {
        /// <summary>Zero-based page index.</summary>
        public int PageNumber { get; }
        public IReadOnlyList<PdfTextLine> Lines { get; }

        /// <summary>
        /// Detected column zones for this page, ordered left-to-right.
        /// Each entry is (X, Y, Width, Height) in PDF page coordinates.
        /// Populated when the layout is produced by <see cref="PdfStructuredTextExtractor"/>.
        /// Empty when constructed externally.
        /// </summary>
        public IReadOnlyList<(float X, float Y, float Width, float Height)> Columns { get; }

        /// <summary>Raw per-character data. Populated by the extractor; empty when constructed externally.</summary>
        internal IReadOnlyList<PdfTextChar> Chars { get; }

        public PdfPageTextLayout(int pageNumber, IReadOnlyList<PdfTextLine> lines)
        {
            PageNumber = pageNumber;
            Lines      = lines;
            Chars      = Array.Empty<PdfTextChar>();
            Columns    = Array.Empty<(float, float, float, float)>();
        }

        internal PdfPageTextLayout(int pageNumber, IReadOnlyList<PdfTextLine> lines,
                                   IReadOnlyList<PdfTextChar> chars)
        {
            PageNumber = pageNumber;
            Lines      = lines;
            Chars      = chars;
            Columns    = PdfLayoutAnalyzer.DetectColumns(this);
        }

        /// <summary>
        /// Returns a new layout restricted to the given bounding box (PDF user-space coordinates,
        /// origin bottom-left). Words and lines are re-assembled from the filtered character set;
        /// no re-parsing of the PDF is required.
        /// <para>
        /// Only layouts produced by <see cref="PdfStructuredTextExtractor"/> retain the underlying
        /// per-character data needed for cropping. Layouts built via the public
        /// <see cref="PdfPageTextLayout(int, IReadOnlyList{PdfTextLine})"/> constructor have no
        /// chars and will return an empty cropped layout.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="width"/> or
        /// <paramref name="height"/> is negative.</exception>
        public PdfPageTextLayout Crop(float x, float y, float width, float height,
                                      WordAssemblyOptions options = null)
        {
            if (width  < 0f) throw new ArgumentOutOfRangeException(nameof(width),  "width must be non-negative");
            if (height < 0f) throw new ArgumentOutOfRangeException(nameof(height), "height must be non-negative");

            var filtered = new List<PdfTextChar>();
            foreach (var c in Chars)
            {
                // One intersection predicate, shared with PdfSpatialQuery's region queries. It was
                // open-coded here under a comment there calling the two "the same predicate" — and
                // they had already drifted on the contract, since only this side refused a negative
                // extent.
                if (PdfSpatialQuery.RectIntersects(c.X, c.Y, c.Width, c.Height, x, y, width, height))
                    filtered.Add(c);
            }
            var lines = PdfStructuredTextExtractor.BuildLines(filtered, options ?? WordAssemblyOptions.Default);
            return new PdfPageTextLayout(PageNumber, lines, filtered);
        }

        /// <summary>Concatenates all lines into a plain-text string.</summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var line in Lines) { sb.AppendLine(line.Text); }
            return sb.ToString();
        }
    }
}
