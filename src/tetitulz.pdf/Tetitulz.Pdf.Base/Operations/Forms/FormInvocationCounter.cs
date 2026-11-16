// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Answers one question for the operations that rewrite a Form XObject's content in place:
    /// <b>is this form drawn exactly once in the whole document?</b>
    ///
    /// <para><b>Why the obvious count is wrong.</b> Counting <c>Do</c> operators in page content
    /// alone misses invocations that live <i>inside another form</i>. A form invoked once from a
    /// page and once from within a second form scores 1, looks uniquely drawn, and gets rewritten —
    /// silently changing the other place it appears. That was the shape of
    /// <c>PdfRedactionEngine.BuildFormUseCount</c>, which walked <c>doc.Pages</c> and nothing else;
    /// for redaction it meant content disappearing from a page the caller never named.</para>
    ///
    /// <para><b>The rule implemented here is deliberately conservative.</b> A form is uniquely drawn
    /// when it has exactly one invocation site anywhere in the document <i>and</i> that site is
    /// either page content or a form which is itself uniquely drawn. Anything else — two sites, or
    /// one site inside a form that is drawn twice — answers false. Erring toward "shared" costs a
    /// refused edit; erring the other way corrupts a page nobody looked at.</para>
    /// </summary>
    internal static class FormInvocationCounter
    {
        /// <summary>Where a form is invoked from.</summary>
        private readonly struct Site
        {
            /// <summary>Object number of the enclosing form, or 0 when the site is page content.</summary>
            internal readonly int EnclosingForm;
            internal Site(int enclosingForm) { EnclosingForm = enclosingForm; }
        }

        /// <summary>
        /// Builds the invocation map for the whole document.
        /// </summary>
        /// <returns>
        /// A predicate: given a form XObject's object number, whether it is drawn exactly once.
        /// An unknown object number answers <c>false</c> — a form we never saw invoked is not
        /// something to rewrite on the strength of not having found it.
        /// </returns>
        internal static Func<int, bool> BuildUniquelyDrawnTest(PdfDocument doc)
        {
            var sites = new Dictionary<int, List<Site>>();
            var visitedForms = new HashSet<int>();

            foreach (var page in doc.Pages)
            {
                byte[] content;
                try { content = page.GetContentStreamData(); }
                catch (Exception) { continue; }
                CollectSites(content, page.Resources, doc, enclosingForm: 0, sites, visitedForms, depth: 0);
            }

            var memo = new Dictionary<int, bool>();

            bool IsUnique(int objNum)
            {
                if (memo.TryGetValue(objNum, out bool known)) return known;

                // Seed before recursing: a cycle must not answer "unique" by falling off the end of
                // the recursion, and a form that (transitively) invokes itself is never safe to
                // rewrite in place anyway.
                //
                // NO INPUT REACHES IT, and a mutation sweep is how that was established — deleting
                // it reddens nothing, and the reason is the shape of the walk rather than the shape
                // of any fixture. CollectSites descends only from page content, so a form with a
                // recorded site has a chain back to a page; entering a cycle from a page gives the
                // entry form a second site, which fails the `Count != 1` test above before this
                // recursion begins, and a cycle no page reaches records no sites at all. Kept as a
                // bound on this function's own contract rather than on today's caller's graph.
                memo[objNum] = false;

                if (!sites.TryGetValue(objNum, out var where) || where.Count != 1) return false;

                int enclosing = where[0].EnclosingForm;
                bool unique = enclosing == 0 || IsUnique(enclosing);
                memo[objNum] = unique;
                return unique;
            }

            return IsUnique;
        }

        private static void CollectSites(
            byte[] content, PdfDictionary resources, PdfDocument doc, int enclosingForm,
            Dictionary<int, List<Site>> sites, HashSet<int> visitedForms, int depth)
        {
            if (content == null || content.Length == 0) return;
            if (depth > PdfLimits.MaxFormXObjectDepth) return;

            var xobjs = doc.ResolveObject(resources?[PdfName.XObject]) as PdfDictionary;
            if (xobjs == null) return;

            IReadOnlyList<ContentStreamOperation> ops;
            try { ops = ContentStreamParser.Parse(content); }
            catch (Exception) { return; }

            foreach (var op in ops)
            {
                if (op.Operator != PdfOperators.InvokeXObject || op.Operands.Length < 1) continue;

                string name = (op.Operands[0] as PdfName)?.Value;
                if (name == null || !(xobjs[new PdfName(name)] is PdfReference reference)) continue;
                if (!(doc.ResolveObject(reference) is PdfStream stream)) continue;
                // Resolved: an unresolved /Subtype reads as absent, so the invocation is not
                // recorded as a site at all. IsUnique answers false for a form with no sites — the
                // conservative direction — so redaction substitutes a clone where it could have
                // edited in place. Wasteful rather than damaging, which is why plan 15's
                // "data-loss" severity for this row is corrected there.
                // An image XObject is a site too: a partly redacted image is cloned at
                // its invocation like a form, and the same test decides whether the original can
                // go. The walk descends into forms only.
                string subtype = (doc.ResolveObject(stream[PdfName.Subtype]) as PdfName)?.Value;
                if (subtype != "Form" && subtype != "Image") continue;

                int objNum = reference.ReferenceObjectNumber;
                if (!sites.TryGetValue(objNum, out var list)) sites[objNum] = list = new List<Site>();
                list.Add(new Site(enclosingForm));
                if (subtype == "Image") continue;

                // Descend once per form, not once per invocation: the sites inside a form are the
                // same whichever call reached it, and re-walking a form invoked a thousand times
                // would make this quadratic on exactly the documents it most needs to handle.
                if (!visitedForms.Add(objNum)) continue;

                byte[] inner;
                try { inner = stream.GetDecodedData(); }
                catch (Exception) { continue; }

                var innerResources = doc.ResolveObject(stream[PdfName.Resources]) as PdfDictionary
                                     ?? resources;
                CollectSites(inner, innerResources, doc, objNum, sites, visitedForms, depth + 1);
            }
        }
    }
}
