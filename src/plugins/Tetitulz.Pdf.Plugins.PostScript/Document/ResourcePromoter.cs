// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PostScript.Document
{
    /// <summary>
    /// Turns the collector's <b>direct</b> resource graph into one where every stream is an
    /// indirect object, ready for <c>PostScriptDocumentWriter</c> to serialise.
    ///
    /// <para><b>Why this exists at all.</b> §7.3.8 requires a stream to be an indirect object, and
    /// <c>Core.Writing.PdfWriter</c> cannot make it one: object numbers are caller-supplied
    /// arguments there, so it has no allocator. Only the code writing the file owns the numbering.
    /// This is the same ownership argument CLAUDE.md makes for the two incremental writers.</para>
    ///
    /// <para><b>Every level, not one.</b> <c>/XObject → Im1</c> is a <see cref="PdfStream"/> one
    /// level down, but a font nests deeper — <c>/Font → F1 → /FontDescriptor → /FontFile</c>. A
    /// font program left direct would be serialised by <c>PdfWriter.WriteObject</c> as dictionary
    /// entries with its <c>RawData</c> silently dropped, so importing a <c>.ps</c> that embeds its
    /// own Type 1 font would produce a <c>/FontFile</c> with no font program in it, the glyphs
    /// gone, and a success report.</para>
    ///
    /// <para><b>Copy-on-write, never mutation.</b> <c>PostScriptPageCollector.AttachResources</c>
    /// hands the same dictionary instance to every page and <c>PdfResourceCollector</c> still owns
    /// those objects, so rewriting in place would corrupt state shared across pages. A container is
    /// rebuilt only when something beneath it actually changed; otherwise the original instance is
    /// returned unchanged.</para>
    ///
    /// <para>The fragment path keeps its resources direct on purpose — there the plugin does not own
    /// the host document's object numbers, and fabricating one would alias whatever object already
    /// had it. Do not "unify" the two.</para>
    /// </summary>
    internal static class ResourcePromoter
    {
        /// <summary>
        /// State shared across every page of one conversion.
        ///
        /// <para>Memoised by <b>reference identity</b>, which is what collapses all N pages onto one
        /// set of objects: every page holds the same resource dictionary instance, so the first
        /// lookup in <see cref="Promote"/> hits for pages 2..N and no object is written twice.</para>
        /// </summary>
        internal sealed class Context
        {
            public readonly Dictionary<PdfObject, int> Promoted =
                new(PdfReferenceEquality.Instance);

            public readonly Dictionary<PdfObject, PdfObject> Rewritten =
                new(PdfReferenceEquality.Instance);

            public readonly List<(int Num, PdfObject Obj)> Collected = new();

            public int NextObjNum;

            /// <summary>How many objects have been promoted, for the per-conversion cap.</summary>
            public int PromotedCount => Promoted.Count;

            /// <summary>
            /// How many objects one conversion may promote: <see cref="DocumentCap"/>, from the
            /// collector's own ceilings. A stream past it is left out and said. Not
            /// <c>PdfLimits.MaxPluginResourceEntries</c>, which bounds the direct graph a plugin hands
            /// a host for one fragment and is sized for one: the document path's collector has
            /// bounded what it holds already, and a cap below that drops images a job drew.
            /// </summary>
            public int MaxPromoted = DocumentCap();

            /// <summary>
            /// One-shot for the <c>/FontDescriptor</c> cap refusal. On the context rather than in a
            /// static, so a wide resource graph reports once per <b>conversion</b> and needs no
            /// reset hook — a context is built per document write.
            /// </summary>
            public bool DescriptorCapReported;
        }

        /// <summary>
        /// The most objects the document path promotes, from the collector's ceilings: every image
        /// and its soft mask (<see cref="PsLimits.MaxImageXObjects"/>), and every font's program,
        /// descriptor, encoding, <c>/ToUnicode</c> and up to 256 Type 3 glyph procedures
        /// (<see cref="PsLimits.MaxFontResources"/>), the two products taken in <c>long</c>; one more
        /// for the resource dictionary itself.
        /// </summary>
        internal static int DocumentCap()
        {
            long cap = 2L * Math.Max(0, PsLimits.MaxImageXObjects)
                       + 260L * Math.Max(0, PsLimits.MaxFontResources) + 1;
            return cap > int.MaxValue ? int.MaxValue : (int)cap;
        }

        /// <summary>
        /// Promotes <paramref name="resources"/> and returns the object number of the resulting
        /// resource dictionary, or 0 when there is nothing to write.
        /// </summary>
        public static int Promote(PdfDictionary resources, Context ctx)
        {
            if (resources == null || ctx == null) return 0;

            if (ctx.Promoted.TryGetValue(resources, out int existing)) return existing;

            var onPath = new HashSet<PdfObject>(PdfReferenceEquality.Instance);
            var rewritten = (PdfDictionary)Rewrite(resources, ctx, onPath, depth: 0);

            int num = ctx.NextObjNum++;
            ctx.Promoted[resources] = num;
            ctx.Collected.Add((num, rewritten));
            return num;
        }

        /// <summary>
        /// Returns <paramref name="obj"/> with every stream beneath it replaced by a reference, or
        /// the original instance when nothing beneath it changed.
        /// </summary>
        private static PdfObject Rewrite(PdfObject obj, Context ctx, HashSet<PdfObject> onPath,
                                         int depth)
        {
            if (obj == null) return null;

            // Scalars carry nothing to promote and are shared freely.
            if (obj is not PdfDictionary && obj is not PdfArray) return obj;

            // A cyclic DIRECT graph cannot be serialised at all — PdfWriter.WriteObject would
            // recurse into it for ever — so this is a hard stop, not an optimisation.
            if (!onPath.Add(obj))
            {
                PostScriptDiagnostics.Report("ResourcePromoter",
                    "a cycle in the resource graph was cut; the repeated reference became null");
                return PdfNull.Instance;
            }

            try
            {
                if (depth >= PdfLimits.MaxObjectGraphDepth)
                {
                    PostScriptDiagnostics.Report("ResourcePromoter",
                        $"the resource graph is deeper than {PdfLimits.MaxObjectGraphDepth}; " +
                        "the rest of that branch became null");
                    return PdfNull.Instance;
                }

                if (ctx.Rewritten.TryGetValue(obj, out var memo)) return memo;

                PdfObject result = obj is PdfArray array
                    ? RewriteArray(array, ctx, onPath, depth)
                    : RewriteDictionary((PdfDictionary)obj, ctx, onPath, depth);

                ctx.Rewritten[obj] = result;
                return result;
            }
            finally
            {
                onPath.Remove(obj);
            }
        }

        /// <summary>
        /// Arrays are walked too. Nothing nests a stream in one today, but
        /// <c>PdfResourceCollector.GetOrAddCidFont</c> puts the descendant font dictionary inside a
        /// <c>/DescendantFonts</c> array — so the walker must not have the hole.
        /// </summary>
        private static PdfObject RewriteArray(PdfArray array, Context ctx,
                                              HashSet<PdfObject> onPath, int depth)
        {
            PdfArray copy = null;

            for (int i = 0; i < array.Count; i++)
            {
                var replacement = RewriteMember(array[i], ctx, onPath, depth + 1);
                if (replacement == null) continue;   // unchanged

                if (copy == null)
                {
                    copy = new PdfArray();
                    for (int k = 0; k < array.Count; k++) copy.Add(array[k]);
                }
                copy[i] = replacement;
            }

            return copy ?? (PdfObject)array;
        }

        private static PdfObject RewriteDictionary(PdfDictionary dict, Context ctx,
                                                   HashSet<PdfObject> onPath, int depth)
        {
            PdfDictionary copy = null;

            foreach (var entry in dict)
            {
                var replacement = RewriteMember(entry.Value, ctx, onPath, depth + 1, entry.Key);
                if (replacement == null) continue;   // unchanged

                copy ??= CloneShell(dict);
                copy[entry.Key] = replacement;
            }

            return copy ?? (PdfObject)dict;
        }

        /// <summary>
        /// An empty copy of <paramref name="dict"/>'s entries, <b>keeping its concrete type</b>.
        ///
        /// <para><see cref="PdfStream"/> derives from <see cref="PdfDictionary"/>, so copying a
        /// stream into a plain dictionary would compile, serialise as a dictionary, and silently
        /// drop <see cref="PdfStream.RawData"/> — reintroducing the exact defect this class exists
        /// to fix, one level further in.</para>
        /// </summary>
        private static PdfDictionary CloneShell(PdfDictionary dict)
        {
            if (dict is PdfStream stream)
            {
                var copy = new PdfStream();
                foreach (var e in stream) copy[e.Key] = e.Value;
                copy.RawData = stream.RawData;
                return copy;
            }

            var plain = new PdfDictionary();
            foreach (var e in dict) plain[e.Key] = e.Value;
            return plain;
        }

        /// <summary>
        /// The replacement for one member, or null when it is unchanged.
        ///
        /// <para><b>Streams are promoted wherever they appear</b> (§7.3.8), and so is
        /// <c>/FontDescriptor</c> — Table 111 requires that entry to be an indirect reference, which
        /// is a second such rule and was missed when this walk only knew about the first. Every
        /// other nested dictionary stays direct, because nothing requires otherwise and promoting
        /// them all would inflate the file for no reader-visible gain.</para>
        /// </summary>
        private static PdfObject RewriteMember(PdfObject value, Context ctx,
                                               HashSet<PdfObject> onPath, int depth,
                                               PdfName key = null)
        {
            if (value is PdfStream stream)
            {
                if (ctx.Promoted.TryGetValue(stream, out int already))
                    return new PdfReference(already, 0);

                if (ctx.PromotedCount >= ctx.MaxPromoted)
                {
                    PostScriptDiagnostics.Report("ResourcePromoter",
                        $"the {ctx.MaxPromoted}-object promotion cap was reached; " +
                        "this stream was omitted from the output rather than written unreadably");
                    return PdfNull.Instance;
                }

                // Rewrite the stream's own dictionary entries first: a stream may itself hold a
                // nested stream (an /SMask, say), and it has to be promoted before this one is
                // handed to the writer.
                var inner = Rewrite(stream, ctx, onPath, depth);

                int num = ctx.NextObjNum++;
                ctx.Promoted[stream] = num;
                ctx.Collected.Add((num, inner ?? stream));
                return new PdfReference(num, 0);
            }

            if (value is PdfDictionary or PdfArray)
            {
                var rewritten = Rewrite(value, ctx, onPath, depth);

                // Table 111: a font's /FontDescriptor "shall be an indirect reference".
                if (key != null && key.Value == PdfName.FontDescriptor.Value
                    && rewritten is PdfDictionary descriptor)
                {
                    if (ctx.Promoted.TryGetValue(value, out int already))
                        return new PdfReference(already, 0);

                    if (ctx.PromotedCount >= ctx.MaxPromoted)
                    {
                        // Reported, like the stream branch above: silent, the output past the cap
                        // would violate the very rule the branch exists to satisfy and the caller
                        // would have no way to learn it — and the stream branch's message does not
                        // cover it, since that one fires only while streams are still being dropped.
                        // The cap is derived from the collector's ceilings (DocumentCap), so a graph
                        // the collector admitted reaches it only through a Context sized below them.
                        if (!ctx.DescriptorCapReported)
                        {
                            ctx.DescriptorCapReported = true;
                            PostScriptDiagnostics.Report("ResourcePromoter",
                                $"the {ctx.MaxPromoted}-object promotion cap was " +
                                "reached; this font's /FontDescriptor stayed a direct dictionary, " +
                                "which Table 111 forbids, so a strict reader or preflight may reject " +
                                "the font. Reported once per conversion.");
                        }

                        return rewritten;
                    }

                    int num = ctx.NextObjNum++;
                    ctx.Promoted[value] = num;
                    ctx.Collected.Add((num, descriptor));
                    return new PdfReference(num, 0);
                }

                return ReferenceEquals(rewritten, value) ? null : rewritten;
            }

            return null;
        }
    }
}
