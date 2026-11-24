// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  Topology: parent → children, sibling ordinals, structure paths
        // ================================================================

        /// <summary>
        /// Derives the child lists and the human-readable structure paths from the pre-order node
        /// list produced by <see cref="StructTreeWalker.Collect(PdfDictionary,PdfDocument,PdfObjectIndex,int,int)"/>. Pre-order guarantees a parent
        /// is always seen before its children, so a single forward pass suffices.
        /// </summary>
        private static void BuildTopology(CheckContext ctx)
        {
            var nodes = ctx.Elements;
            var ordinals = new Dictionary<StructNode, int>();
            var roleCounts = new Dictionary<StructNode, Dictionary<string, int>>();

            for (int i = 0; i < nodes.Count; i++)
            {
                StructNode node = nodes[i];
                if (node.ObjectNumber > 0) ctx.StructElementObjectNumbers.Add(node.ObjectNumber);

                StructNode parent = node.Parent;
                if (parent == null) continue;

                if (!ctx.Children.TryGetValue(parent, out var list))
                {
                    list = new List<StructNode>();
                    ctx.Children[parent] = list;
                }
                list.Add(node);

                if (!roleCounts.TryGetValue(parent, out var counts))
                {
                    counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    roleCounts[parent] = counts;
                }
                string role = node.Role ?? "?";
                counts.TryGetValue(role, out int n);
                counts[role] = n + 1;
                ordinals[node] = n + 1;
            }

            for (int i = 0; i < nodes.Count; i++)
            {
                StructNode node = nodes[i];
                StructNode parent = node.Parent;

                // The /StructTreeRoot itself carries no /S; it contributes no path segment.
                string segment = node.Role;
                if (parent == null)
                {
                    ctx.Paths[node] = segment ?? string.Empty;
                    continue;
                }

                string parentPath = ctx.Paths.TryGetValue(parent, out string pp) ? pp : string.Empty;
                string role = segment ?? "?";

                // ALWAYS index, 1-based among same-role siblings. See the remarks on
                // AccessibilityIssue.StructPath: emitting the index only when the role repeats
                // made the same element's path depend on how many siblings it happened to have,
                // so "TR[2]/TD" and "TR[1]/TD[1]" described the same shape and neither could be
                // used as a key or compared between two revisions of a document.
                int ordinal = ordinals.TryGetValue(node, out int o) ? o : 1;
                role = role + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";

                ctx.Paths[node] = parentPath.Length == 0 ? role : parentPath + "/" + role;
            }

            // Deep trees produce paths no human will read; keep the informative tail.
            var keys = new List<StructNode>(ctx.Paths.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                string p = ctx.Paths[keys[i]];
                if (p != null && p.Length > MaxStructPathLength)
                    ctx.Paths[keys[i]] = "…/" + p.Substring(p.Length - MaxStructPathLength);
            }
        }

        /// <summary>
        /// Collects every object number an /OBJR in the structure tree points at, with the element
        /// holding the /OBJR, so annotation reachability (UA23) and nesting (UA32) are a probe rather
        /// than a second tree walk per annotation.
        /// </summary>
        private static Dictionary<int, StructNode> CollectObjrOwners(CheckContext ctx)
        {
            var owners = new Dictionary<int, StructNode>();
            var nodes = ctx.Elements;
            if (nodes == null) return owners;

            for (int i = 0; i < nodes.Count; i++)
            {
                IReadOnlyList<PdfObject> kids = nodes[i].RawKids;
                if (kids == null) continue;

                for (int k = 0; k < kids.Count; k++)
                {
                    PdfDictionary dict = ResolveKidDictionary(ctx, kids[k]);
                    if (dict == null) continue;

                    // Resolved, not a bare cast: any dictionary value may be indirect (§7.3.10),
                    // and the typeless form is legal — see IsObjectReference.
                    string type = (ResolveObject(ctx.Doc, dict[PdfName.Type]) as PdfName)?.Value;
                    if (!IsObjectReference(ctx, dict)) continue;

                    PdfObject obj = dict[PdfName.Obj];
                    int n = obj is PdfReference r ? r.ReferenceObjectNumber : GetObjectNumber(ctx, obj, obj as PdfDictionary);
                    if (n > 0 && !owners.ContainsKey(n)) owners[n] = nodes[i];
                }
            }

            return owners;
        }

        /// <summary>
        /// Whether a resolved <c>/K</c> child dictionary is an <b>object reference</b> (<c>/OBJR</c>).
        ///
        /// <para>
        /// <b><c>/Type</c> is Optional here.</b> ISO 32000-1 Table 325 makes <c>/Obj</c> the only
        /// Required entry of an object reference dictionary and says of <c>/Type</c> only "if
        /// present, shall be OBJR". Demanding it, a perfectly legal tagged file that omits it would
        /// be told twice over that it was broken: its <c>&lt;Link&gt;</c>/<c>&lt;Form&gt;</c>
        /// wrappers reported as leaves referencing no content (UA16/UA30) and the annotations they
        /// wrap as reachable from no <c>/OBJR</c> (UA23) — with remediation advice telling the author
        /// to add an <c>/OBJR</c> they already had. The <c>/MCR</c> side has the same fallback,
        /// through <c>dict[/MCID] != null</c>.
        /// </para>
        ///
        /// <para>
        /// The typeless form additionally requires <c>/S</c> to be <i>absent</i>: <c>/S</c> is
        /// Required on every structure element (Table 323), so a dictionary carrying it is an
        /// element and not an object reference, whatever else it holds.
        /// </para>
        ///
        /// <para><b>The ladder is <c>Base.Operations.StructKidClassifier</c>'s</b>, shared with the
        /// eight other classifications in this feature and with the Conformance plugin's. A
        /// classifier asking "is the resolved <c>/Type</c> <c>OBJR</c>?" <b>first</b> would call
        /// <c>&lt;&lt; /Type /StructElem /Obj 12 0 R &gt;&gt;</c> no object reference while the
        /// plugin's said it was one — on the same file. Table 323 makes <c>/S</c> Required on an
        /// element, so what the dictionary <i>carries</i> outranks what it calls itself, and the
        /// wider reading can only remove false UA16/UA23/UA30 findings.</para>
        /// </summary>
        /// <param name="ctx">Supplies the document, so an indirect <c>/Type</c> is followed.</param>
        /// <param name="dict">The resolved child dictionary.</param>
        private static bool IsObjectReference(CheckContext ctx, PdfDictionary dict)
            => StructKidClassifier.Classify(dict, ctx?.Doc) == StructKidKind.ObjectReference;

        /// <summary>
        /// Resolves a raw /K child to a dictionary, skipping references that are already known to
        /// be structure elements (the common case, and by far the most expensive to re-resolve).
        /// Returns null for MCID integers and anything that does not resolve.
        /// </summary>
        private static PdfDictionary ResolveKidDictionary(CheckContext ctx, PdfObject kid)
        {
            if (kid == null) return null;
            if (kid is PdfDictionary direct) return direct;
            if (kid is not PdfReference reference) return null;
            if (ctx.StructElementObjectNumbers.Contains(reference.ReferenceObjectNumber)) return null;

            try
            {
                return ctx.Doc.ResolveObject(reference) as PdfDictionary;
            }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("PdfDeepAccessibilityChecker.ResolveKidDictionary",
                    "structure child " + reference.ReferenceObjectNumber.ToString(CultureInfo.InvariantCulture) +
                    " could not be resolved; it was treated as absent", ex);
                return null;
            }
        }
    }
}
