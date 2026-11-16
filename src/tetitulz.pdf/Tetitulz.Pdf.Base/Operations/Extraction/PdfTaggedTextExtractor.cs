// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>A logical-structure block extracted from a Tagged PDF, in reading order.</summary>
    public sealed class PdfTextBlock
    {
        /// <summary>The structure type from the element's /S (e.g. "P", "H1", "Table", "TD", "LI", "Figure").</summary>
        public string StructureType { get; init; }
        /// <summary>1–6 for H1…H6 headings, otherwise 0.</summary>
        /// <remarks>
        /// <para><b>Two spellings fall outside the 1–6 range and both report 0</b>, which this
        /// property's contract makes indistinguishable from "not a heading": a PDF 2.0
        /// <c>H7</c>+, and the bare <c>H</c> that PDF/UA "strong structure" uses, whose level comes
        /// from its <c>/Sect</c> nesting rather than its name. <see cref="StructureType"/> carries
        /// the raw <c>/S</c> value, so a caller that needs either can read it there.</para>
        ///
        /// <para><b>Deliberately not unified with
        /// <c>Accessibility.StructTreeWalker.HeadingLevel</c></b>, which encodes the same question
        /// as <c>-1</c> = not a heading, <c>0</c> = bare <c>H</c>, <i>n</i> = <c>H</c><i>n</i>. That
        /// is the right encoding for PDF/UA analysis, where the distinction drives a real finding —
        /// and the wrong one here, where <c>0</c> already means "not a heading" in a published
        /// contract that <c>&gt; 0</c> tests depend on. The two zeros mean opposite things on
        /// purpose; changing this one to match would be an API break wearing a deduplication's
        /// clothes.</para>
        /// </remarks>
        public int HeadingLevel { get; init; }
        /// <summary>The block's text (the element's /ActualText when present, else its marked content).</summary>
        public string Text { get; init; } = string.Empty;
        /// <summary>The element's /Alt (alternate description, e.g. for a Figure), or null.</summary>
        public string AltText { get; init; }
        /// <summary>Zero-based page index the block is bound to, or -1 if unknown.</summary>
        public int PageIndex { get; init; } = -1;

        public override string ToString()
            => HeadingLevel > 0 ? $"[H{HeadingLevel}] {Text}" : $"[{StructureType}] {Text}";
    }

    /// <summary>
    /// Structure-aware text extraction for Tagged PDFs.  Walks the document's logical structure tree
    /// (/StructTreeRoot) and binds each structure element to its marked content (via /MCID), yielding
    /// text in the authoritative <em>logical</em> reading order with semantic roles — which is more
    /// reliable than geometry for multi-column / reordered layouts, and exposes headings, table cells,
    /// list items, and figure /Alt text.  Honors element-level /ActualText (glyph-overriding text).
    ///
    /// <para>Returns an empty list for non-tagged documents (check <see cref="IsTagged(Stream)"/> or
    /// fall back to <see cref="PdfStructuredTextExtractor"/>).  Marked-content-level /ActualText (on a
    /// BDC rather than a struct element) is not yet applied — element-level /ActualText is.</para>
    ///
    /// <para>The tree is walked by <see cref="StructureTreeWalk"/> — the one traversal of it, shared
    /// with <c>PdfStructureTreeReader</c>, the accessibility feature and the PDF/UA checker, so the
    /// four cannot disagree about what bounds the walk — a depth cap and a visited set with no edge
    /// budget among them.</para>
    /// </summary>
    public static class PdfTaggedTextExtractor
    {
        /// <summary>
        /// Last non-fatal issue (e.g. document not tagged). <c>null</c> after a clean run.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent extraction on another thread
        /// cannot overwrite it — read it on the thread that ran the call (see the Diagnostics
        /// Pattern section of CLAUDE.md).
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
        /// <c>/K</c> child slots the last <see cref="Extract(Stream, WordAssemblyOptions)"/> on this
        /// thread examined — the liveness probe for the edge ceiling.
        /// </summary>
        /// <remarks>
        /// <para><b>A bound on work is not observable in the answer, which is why this exists.</b>
        /// Once the budget is spent every further slot resolves to an already-visited element and
        /// produces no block, so a walk that stops at the ceiling and one that sets the flag and
        /// keeps going return the <i>same blocks</i> and the <i>same</i> <see cref="LastError"/> —
        /// they differ only in how much work they did. That is the optimisation-liveness blind spot
        /// of CLAUDE.md's testing section, and the answer is the same one
        /// <c>CMapCodec.UsesCidRangeIndex</c> uses: assert the liveness separately.</para>
        ///
        /// <para><c>internal</c>, and deliberately not on the public surface: it is evidence for a
        /// test, not a diagnostic a host acts on. It is <c>[ThreadStatic]</c> for the reason every
        /// <c>Last*</c> slot is.</para>
        /// </remarks>
        internal static int LastEdgesExamined => _lastEdgesExamined;

        [System.ThreadStatic]
        private static int _lastEdgesExamined;

        /// <summary>True when the document declares a logical structure tree (/StructTreeRoot).</summary>
        public static bool IsTagged(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            byte[] bytes = ReadAll(input);
            using var doc = PdfDocument.Open(new MemoryStream(bytes));
            EncryptedRead.Refuse(doc, "whether it is tagged", "PdfOperations.IsTaggedPdf(Stream, string)");
            return GetStructRoot(doc) != null;
        }

        /// <summary>Extracts logical-structure blocks in reading order. Empty when the PDF is not tagged.</summary>
        public static IReadOnlyList<PdfTextBlock> Extract(Stream input, WordAssemblyOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            LastError = null;
            _lastEdgesExamined = 0;
            options ??= new WordAssemblyOptions { ApplyBidi = true };

            byte[] bytes = ReadAll(input);
            using var doc = PdfDocument.Open(new MemoryStream(bytes));
            // Refused rather than reported as "not a tagged PDF", which is what an encrypted
            // document's missing catalog looked like.
            EncryptedRead.Refuse(doc, "the tagged text", "PdfOperations.ExtractTaggedText with a password");

            // Per-page MCID -> reading-order text, from the geometric extractor (which now stamps MCIDs).
            var layouts = PdfStructuredTextExtractor.Extract(new MemoryStream(bytes), options);
            var mcidText = BuildMcidText(layouts, options);

            var structRoot = GetStructRoot(doc);
            if (structRoot == null)
            {
                LastError = "document has no /StructTreeRoot (not a tagged PDF)";
                return Array.Empty<PdfTextBlock>();
            }

            var ctx = new WalkCtx
            {
                Doc          = doc,
                McidText     = mcidText,
                Blocks       = new List<PdfTextBlock>(),
            };

            // The ceilings are the walk's defaults — PdfLimits.MaxStructTreeDepth, MaxStructTreeNodes
            // and MaxStructTreeEdges, read when the walk is made. An element's own marked content is
            // gathered slot by slot as the walk examines its /K, and its block is emitted when the walk
            // leaves it, after its child elements have emitted theirs.
            string unreadable = null;
            var walk = new StructureTreeWalk(doc)
            {
                Enter = node => node.Tag = new StringBuilder(),
                Kid   = kid => AppendKid(kid, ctx),
                Leave = node => EmitBlock(node, ctx),
                // An element the parser cannot read is a subtree the extraction lacks: said, once,
                // below the ceilings, since a tree missing one branch is otherwise a tree that ends there.
                Report = (walkEvent, at, objectNumber, ex) =>
                {
                    if (walkEvent == StructureTreeWalkEvent.UnresolvableKid && unreadable == null)
                        unreadable = "structure element " + objectNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                     " could not be read (" + ex?.Message + "), so the tagged extraction is incomplete";
                },
            };
            walk.Run(structRoot);
            _lastEdgesExamined = walk.EdgesExamined;

            // Reported rather than silent: a truncated walk returns fewer blocks than the document
            // has, and "this document's structure is small" and "we stopped reading it" produce
            // indistinguishable output otherwise.
            // The edge ceiling is reported first when several fired: it bounds total work across the
            // whole tree, so it is the more complete statement about what was not read. The node
            // ceiling bounds the number of branches; the depth cap bounds one branch and says nothing
            // about the rest, so it is reported — a branch abandoned at 1000 levels is not a tree
            // that ends.
            if (walk.EdgeCapReached)
            {
                LastError = "the structure tree's /K arrays exceed the "
                            + PdfLimits.MaxStructTreeEdges.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "-slot ceiling, so the tagged extraction is incomplete";
            }
            else if (walk.NodeCapReached)
            {
                LastError = "the structure tree has more than "
                            + PdfLimits.MaxStructTreeNodes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " elements, so the tagged extraction is incomplete";
            }
            else if (walk.DepthCapReached)
            {
                LastError = "the structure tree is nested deeper than "
                            + PdfLimits.MaxStructTreeDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " levels, so the tagged extraction is incomplete";
            }
            else if (unreadable != null)
            {
                LastError = unreadable;
            }

            return ctx.Blocks;
        }

        /// <summary>Flattens the structure blocks into a plain-text string (one block per line).</summary>
        public static string ExtractText(Stream input, WordAssemblyOptions options = null)
        {
            var sb = new StringBuilder();
            foreach (var b in Extract(input, options))
            {
                string line = !string.IsNullOrEmpty(b.Text) ? b.Text : b.AltText;
                if (!string.IsNullOrEmpty(line)) sb.AppendLine(line);
            }
            return sb.ToString().TrimEnd();
        }

        // ----------------------------------------------------------------
        //  Structure-tree walk
        // ----------------------------------------------------------------

        private sealed class WalkCtx
        {
            public PdfDocument Doc;
            /// <summary>
            /// Marked-content text keyed on (page, content-stream object number, MCID).
            /// The stream component is what keeps a Form XObject's MCID 0 distinct from the
            /// page's own — §14.7.4.2 scopes an MCID to its stream, not to its page.
            /// </summary>
            public Dictionary<(int Page, int StreamObj, int Mcid), string> McidText;
            public List<PdfTextBlock> Blocks;

            /// <summary>
            /// The marked-content sequences already given to an element. §14.7.4.4: a sequence
            /// belongs to one structure element, so its text is given once, to the first slot that
            /// names it. Each slot appended it: one element listing an MCID two million times, or a
            /// thousand elements sharing a /K of one, held the text that many times over — gigabytes,
            /// or past a string's length, from a few kilobytes of file.
            /// </summary>
            public HashSet<(int Page, int StreamObj, int Mcid)> Given = new HashSet<(int, int, int)>();
        }

        /// <summary>
        /// One <c>/K</c> slot of an element: a marked-content identifier or an <c>/MCR</c> appends to
        /// the element's own text; a child element is entered by the walk and emits its own block; an
        /// <c>/OBJR</c> (an annotation, say) and a dictionary that is no kid at all carry no text.
        /// </summary>
        private static void AppendKid(StructureTreeKid kid, WalkCtx ctx)
        {
            var own = (StringBuilder)kid.Parent.Tag;
            int page = kid.Parent.PageIndex;

            // A bare integer /K is an MCID in the *page's* content stream (§14.7.4.2), so its
            // stream scope is 0 — the same value PdfStructuredTextExtractor stamps on page-level
            // glyphs. Only an /MCR can name a different stream, via /Stm.
            if (kid.Resolved is PdfNumber n)
            {
                AppendMcid(ctx, own, page, 0, n.IntValue);   // saturating, as MarkedContentInfo reads the content's
                return;
            }

            // Classified on content when /Type is absent — Table 324 makes /Type OPTIONAL on an
            // MCR and /MCID Required, so `/K << /MCID 0 >>` is legal; the struct-element branch would
            // find no /K of its own, emit no block, and ExtractText return "" for a document whose
            // content really is marked.
            if (kid.Kind != StructKidKind.MarkedContentReference) return;

            var dict = (PdfDictionary)kid.Resolved;
            int mp = page;
            // An /MCR's own /Pg, through the document's one index of its pages by object number.
            if (dict[PdfName.Pg] is PdfReference mref && ctx.Doc.PageIndexOfObject(mref.ReferenceObjectNumber) is int mpi && mpi >= 0) mp = mpi;

            // /Stm (Table 324) names the content stream holding the marked content when it is
            // not the page's own — a Form XObject, typically. It is the entry that keeps a
            // form's MCID 0 distinct from the page's, and it was not read at all.
            int stm = dict[PdfName.Stm] is PdfReference sref ? sref.ReferenceObjectNumber : 0;

            if (ctx.Doc.ResolveObject(dict[PdfName.MCID]) is PdfNumber mn)
                AppendMcid(ctx, own, mp, stm, mn.IntValue);
        }

        /// <summary>
        /// Emits an element's block once the walk has left it — after its child elements have emitted
        /// theirs. The <c>/StructTreeRoot</c> is not an element and emits none.
        /// </summary>
        private static void EmitBlock(StructureTreeNode node, WalkCtx ctx)
        {
            if (node.Parent == null) return;

            var own = (StringBuilder)node.Tag;
            string actual = (ctx.Doc.ResolveObject(node.Dictionary[PdfName.ActualText]) as PdfString)?.GetText();
            string alt    = (ctx.Doc.ResolveObject(node.Dictionary[PdfName.Alt]) as PdfString)?.GetText();

            string text = actual ?? (own.Length > 0 ? own.ToString() : null);
            if (text == null && alt == null) return;

            ctx.Blocks.Add(new PdfTextBlock
            {
                StructureType = node.Role,
                HeadingLevel  = HeadingLevel(node.Role),
                Text          = text ?? string.Empty,
                AltText       = alt,
                PageIndex     = node.PageIndex,
            });
        }

        private static void AppendMcid(WalkCtx ctx, StringBuilder own, int page, int streamObj, int mcid)
        {
            // One of a pair with BuildMcidText's `c.Mcid < 0` filter: each masks the other, so
            // either alone is an equivalent mutation. Both gone hands untagged glyphs to an element
            // naming a negative MCID — AnElementNamingANegativeMcidGetsNoUntaggedText.
            if (mcid < 0) return;
            if (!ctx.McidText.TryGetValue((page, streamObj, mcid), out string t) || string.IsNullOrEmpty(t))
                return;
            if (!ctx.Given.Add((page, streamObj, mcid))) return;
            if (own.Length > 0) own.Append(' ');
            own.Append(t);
        }

        // ----------------------------------------------------------------
        //  Per-page MCID -> text (reading order)
        // ----------------------------------------------------------------

        /// <summary>
        /// Groups a page's glyphs by the marked-content sequence that produced them, keyed on
        /// <c>(page, content stream, mcid)</c>.
        /// </summary>
        /// <remarks>
        /// <b>The stream component is load-bearing.</b> §14.7.4.2 scopes an /MCID to its content
        /// stream, and the structured extractor recurses into Form XObjects, so keying on
        /// <c>(page, mcid)</c> alone merged a page's own <c>/H1 &lt;&lt;/MCID 0&gt;&gt;</c> with a
        /// form's <c>/P &lt;&lt;/MCID 0&gt;&gt;</c> and handed the same concatenated string to
        /// both structure elements — the heading text duplicated into the paragraph and back.
        /// </remarks>
        private static Dictionary<(int, int, int), string> BuildMcidText(
            IReadOnlyList<PdfPageTextLayout> layouts, WordAssemblyOptions options)
        {
            var map = new Dictionary<(int, int, int), string>();
            foreach (var layout in layouts)
            {
                var byMcid = new Dictionary<(int stream, int mcid), List<PdfTextChar>>();
                foreach (var c in layout.Chars)
                {
                    if (c.Mcid < 0) continue;
                    var key = (c.McidStreamObject, c.Mcid);
                    if (!byMcid.TryGetValue(key, out var list))
                        byMcid[key] = list = new List<PdfTextChar>();
                    list.Add(c);
                }

                foreach (var kv in byMcid)
                {
                    var lines = PdfStructuredTextExtractor.BuildLines(kv.Value, options);
                    string text = string.Join(" ", lines.Select(l => l.Text));
                    map[(layout.PageNumber, kv.Key.stream, kv.Key.mcid)] = text;
                }
            }
            return map;
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static PdfDictionary GetStructRoot(PdfDocument doc)
        {
            if (doc.Catalog == null) return null;
            return doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
        }

        private static int HeadingLevel(string type)
        {
            if (type != null && type.Length == 2 && type[0] == 'H' && type[1] >= '1' && type[1] <= '6')
                return type[1] - '0';
            return 0;
        }

        private static byte[] ReadAll(Stream input)
        {
            if (input.CanSeek) input.Position = 0;
            using var ms = new MemoryStream();
            input.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
