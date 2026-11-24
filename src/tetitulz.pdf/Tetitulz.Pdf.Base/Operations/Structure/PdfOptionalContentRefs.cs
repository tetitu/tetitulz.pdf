// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Optional content as object numbers (ISO 32000-1 §8.11.4.3): <see cref="Collect"/> reads one
    /// reference list — <c>/ON</c>, <c>/OFF</c> or <c>/OCGs</c> — and <see cref="BuildHiddenSet"/>
    /// combines them with <c>/BaseState</c> into the single answer to <b>"which groups does the
    /// default configuration hide?"</b>.
    /// </summary>
    /// <remarks>
    /// <b>One reading for every caller in <c>Base</c></b> — the renderer's
    /// (<c>PdfPageRenderer.CollectOcgRefs</c>) and the manager's (<c>PdfOcgManager.BuildOcgList</c>)
    /// among them. A reading that took the document and never used it would read an indirect
    /// <c>/OFF</c> as an empty set, and its caller's write would then <b>replace</b> the config's
    /// list — making one layer visible reset every other layer's state.
    ///
    /// <para><b>Elements are collected as references, never resolved.</b> An optional-content group
    /// is identified BY object number, so resolving an element would throw away the very identity
    /// being recorded. A non-reference element is skipped rather than guessed at.</para>
    ///
    /// <para>There is no single-membership test: "is this group hidden?" has one answer,
    /// <see cref="BuildHiddenSet"/>, which merges <c>/BaseState</c>, and a shorter path that skips
    /// the merge would be a second, wrong one. Ask it and test its result.</para>
    ///
    /// <para>No static fields, so no type initializer — a leaf by construction. That matters here:
    /// the alternative was for <c>PdfOcgManager</c> to call <c>PdfPageRenderer</c>'s static, and
    /// <c>PdfPageRenderer</c> lives beside a type that <b>has</b> an initializer — the sideways
    /// call CLAUDE.md records as having crashed the Base test host mid-run.</para>
    /// </remarks>
    internal static class PdfOptionalContentRefs
    {
        /// <summary>
        /// The object numbers named by <paramref name="listObj"/>, which may be the array itself or
        /// a reference to it. An absent, unresolvable or non-array value yields an empty set —
        /// never null, because every caller treats the result as a set to test membership in.
        /// </summary>
        /// <param name="doc">
        /// Used to follow indirect references; may be null, in which case only a direct array is
        /// read. <c>/ON</c> and <c>/OFF</c> are routinely written indirectly, so passing the
        /// document is what makes this read the list at all.
        /// </param>
        /// <param name="listObj">
        /// The <c>/ON</c> or <c>/OFF</c> value — an array of OCG references, or a reference to one.
        /// </param>
        internal static HashSet<int> Collect(PdfDocument doc, PdfObject listObj)
        {
            var set = new HashSet<int>();

            var arr = (doc != null ? doc.ResolveObject(listObj) as PdfArray : null)
                      ?? listObj as PdfArray;
            if (arr == null) return set;

            for (int i = 0; i < arr.Count; i++)
                if (arr[i] is PdfReference r) set.Add(r.ReferenceObjectNumber);

            return set;
        }

        /// <summary>
        /// The object numbers of the optional-content groups that <paramref name="ocProperties"/>'
        /// <b>default configuration</b> (<c>/D</c>) leaves switched off, per ISO 32000-1 §8.11.4.3.
        /// Returns <see langword="null"/> when nothing is hidden — including when the document has
        /// no <c>/OCProperties</c> or no <c>/D</c> — so a caller can skip the lookup entirely.
        /// </summary>
        /// <param name="doc">
        /// Used to follow indirect references. Common producers write <c>/D</c> as a reference, and
        /// <c>/ON</c>, <c>/OFF</c> and <c>/OCGs</c> routinely are too, so passing the document is
        /// what makes this read the configuration at all.
        /// </param>
        /// <param name="ocProperties">
        /// The catalog's <c>/OCProperties</c> dictionary, already resolved. Taken as a parameter
        /// rather than walked to from <paramref name="doc"/> because both callers already hold it.
        /// </param>
        /// <remarks>
        /// <para><b>This is the one answer to "is this group hidden by default?".</b> It was
        /// answered twice — once here (as <c>PdfPageRenderer.BuildHiddenOcgSet</c>, feeding both
        /// renderers and the render session) and once in <c>PdfOcgManager.CollectOcgs</c>, which
        /// feeds <c>PdfOperations.ListLayers</c> — and the two disagreed on <c>/BaseState</c>:
        /// the renderer honoured it, the layer listing did not read it at all. A document whose
        /// <c>/D</c> says <c>/BaseState /OFF</c> therefore had every one of its layers reported
        /// <c>DefaultVisible = true</c> while both renderers correctly drew none of them, so a host
        /// building a layer UI showed every checkbox ticked over a page that was blank.</para>
        ///
        /// <para><b>Order of the three rules, and it is the order that matters.</b>
        /// <c>/BaseState</c> initialises <i>every</i> group named by the top-level <c>/OCGs</c>
        /// array — <c>/OFF</c> turns them all off, and the default <c>/ON</c> (which is also what
        /// an unrecognised value such as <c>/Unchanged</c> falls back to here, there being no prior
        /// state for the default configuration to leave unchanged) turns them all on. The
        /// <c>/ON</c> and <c>/OFF</c> arrays then override individual groups, and
        /// <b><c>/ON</c> wins for a group named by both</b>: re-enabling a layer is the explicit
        /// act, so a producer that lists a group in both is asking for it. Both copies already
        /// agreed on that last rule, which is why it survives the merge unchanged.</para>
        ///
        /// <para>There is deliberately <b>no <c>/OCGs</c> gate</b>. The top-level array is
        /// spec-required and real producers omit it; using it as an early return once showed every
        /// hidden layer of such a document. It is read only where <c>/BaseState /OFF</c> genuinely
        /// needs the document-wide list.</para>
        /// </remarks>
        internal static HashSet<int> BuildHiddenSet(PdfDocument doc, PdfDictionary ocProperties)
        {
            if (ocProperties == null) return null;

            var defaultConfig = Resolve<PdfDictionary>(doc, ocProperties[PdfName.D]);
            if (defaultConfig == null) return null;

            // /ON and /OFF are the explicit overrides; both may be indirect, and so may the
            // individual OCG entries inside them (rare, but legal).
            var onSet  = Collect(doc, defaultConfig[PdfName.ON]);
            var offSet = Collect(doc, defaultConfig[PdfName.OFF]);

            bool baseOff = Resolve<PdfName>(doc, defaultConfig[PdfName.BaseState])?.Value == "OFF";

            var hidden = new HashSet<int>();
            if (baseOff)
            {
                // Start from the document-wide OCG list and subtract /ON. /OFF is redundant under
                // BaseState /OFF but is honoured below for documents that list it anyway.
                foreach (int n in Collect(doc, ocProperties[PdfName.OCGs]))
                    if (!onSet.Contains(n)) hidden.Add(n);
            }

            // /OFF hides regardless of base state; /ON always wins over /OFF.
            foreach (int n in offSet)
                if (!onSet.Contains(n)) hidden.Add(n);

            return hidden.Count > 0 ? hidden : null;
        }

        /// <summary>
        /// Resolves <paramref name="obj"/> through <paramref name="doc"/> and casts it to
        /// <typeparamref name="T"/>, falling back to a direct cast when there is no document.
        /// </summary>
        private static T Resolve<T>(PdfDocument doc, PdfObject obj) where T : PdfObject
            => (doc != null ? doc.ResolveObject(obj) as T : null) ?? obj as T;
    }
}
