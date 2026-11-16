// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Reads a structure tree's <c>/RoleMap</c> (§14.7.3) and follows a producer's own structure
    /// type through it to the type it means (§14.8.4).
    /// </summary>
    /// <remarks>
    /// <para><b>Five readers existed</b>: <c>Accessibility.Internal.RoleMapResolver</c>,
    /// <c>PdfDeepAccessibilityChecker.ReadRoleMap</c>, a second <i>inline</i> read in that class's
    /// <c>CheckRoleMapping</c>, <c>PdfReadingOrderAnalyzer.BuildRoleMap</c> and
    /// <c>Plugins.Conformance.PdfUaChecker.BuildRoleMap</c>. It is in Core because the last of those
    /// is in a plugin, which cannot see a feature's internals.</para>
    ///
    /// <para><b>It takes the <c>/StructTreeRoot</c> dictionary, not the document, and that is a
    /// requirement rather than a style choice.</b> <c>PdfDocument.Load</c> leaves <c>Catalog</c>
    /// null for an <b>encrypted</b> document — which is exactly why <c>PdfUaChecker</c> carries its
    /// own <c>ResolveCatalog</c> and hands the resolved root in. A <c>Build(PdfDocument)</c>
    /// signature would silently disable <c>/RoleMap</c> resolution for every encrypted document on
    /// the plugin side: a new divergence created by the merge that exists to end divergence.</para>
    ///
    /// <para><b>A malformed target is recorded as <c>null</c>, not dropped.</b> The deep checker's
    /// UA13 reports <c>"X → (not a name)"</c>, so it needs to see the entry; every other caller only
    /// has to stop there, which <see cref="Resolve"/> does. Dropping the entry instead would make an
    /// unresolvable role indistinguishable from an unmapped one — and an unmapped role is a
    /// different, separately-reported violation.</para>
    ///
    /// <para><b>Resolution stops at the <i>terminus</i> and carries a visited set.</b> A hop cap
    /// alone is not enough: with <c>A → B → C → A</c> and a cap of 16, a reader stops wherever
    /// 16 mod 3 lands — <c>B</c> — while one stopping at the first repeat returns <c>A</c>: two
    /// answers on one file. The visited set makes the answer the role the chain
    /// re-enters, which is the only stable choice.</para>
    ///
    /// <para><b>One caller deliberately does not use <see cref="Resolve"/>.</b>
    /// <c>PdfDeepAccessibilityChecker.ResolveRole</c> stops at the first <i>standard</i> type rather
    /// than at the terminus, which is a stronger rule: it is what makes
    /// <c>/RoleMap &lt;&lt; /Custom /Figure /Figure /Div &gt;&gt;</c> resolve <c>/Custom</c> to
    /// <c>Figure</c> instead of to <c>Div</c>. The two coincide on any conforming file, because
    /// §14.7.4 forbids remapping a standard type, so a chain cannot pass <i>through</i> one. Making
    /// that the shared rule needs a standard-type vocabulary here, which is a separate decision;
    /// until then the stronger rule stays where the vocabulary already is.</para>
    ///
    /// <para><b>A leaf by construction</b> — no static fields, so no type initializer and no
    /// initialization edge onto anything that uses it.</para>
    /// </remarks>
    public static class PdfRoleMap
    {
        /// <summary>
        /// Hops allowed while following a chain of custom types, as a second bound beside the
        /// visited set. A cap alone does not give a stable answer on a cycle; a visited set alone
        /// is unbounded only in memory, so both are kept.
        /// </summary>
        public const int MaxHops = 16;

        /// <summary>
        /// The structure tree's <c>/RoleMap</c> as a plain name → name dictionary. Empty when the
        /// tree has none; an entry whose value is not a name is present with a <c>null</c> value.
        /// </summary>
        /// <param name="resolve">
        /// Follows an indirect reference. <c>/RoleMap</c> and its values are routinely written
        /// indirectly, and <c>PdfDictionary</c>'s indexer does not follow one — unresolved, the map
        /// reads as <i>absent</i>, and absent means every custom role becomes an unknown type.
        /// </param>
        /// <param name="structTreeRoot">The <c>/StructTreeRoot</c> dictionary. Null yields an empty map.</param>
        public static Dictionary<string, string> Build(
            PdfDictionary structTreeRoot, Func<PdfObject, PdfObject> resolve)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (structTreeRoot == null) return map;

            PdfObject raw = structTreeRoot[PdfName.RoleMap];
            if (resolve != null) raw = resolve(raw);
            if (!(raw is PdfDictionary roleMap)) return map;

            foreach (KeyValuePair<PdfName, PdfObject> entry in roleMap)
            {
                if (entry.Key == null) continue;

                PdfObject value = entry.Value;
                if (!(value is PdfName) && resolve != null) value = resolve(value);
                map[entry.Key.Value] = (value as PdfName)?.Value;
            }
            return map;
        }

        /// <summary>
        /// Follows <paramref name="role"/> through the map to the type it means, or returns it
        /// unchanged when the map says nothing usable about it.
        /// </summary>
        /// <remarks>
        /// Stops at the terminus, on a repeat, on a self-map, on an entry whose target was not a
        /// name, and at <see cref="MaxHops"/>.
        /// </remarks>
        public static string Resolve(string role, IReadOnlyDictionary<string, string> map)
        {
            if (role == null || map == null || map.Count == 0) return role;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            string current = role;

            for (int hop = 0; hop < MaxHops; hop++)
            {
                if (!seen.Add(current)) break;                        // cycle
                if (!map.TryGetValue(current, out string next)) break;
                if (next == null || next == current) break;           // malformed target, or self-map
                current = next;
            }
            return current;
        }

        /// <summary>
        /// Whether an element with structure type <paramref name="role"/> <b>means</b>
        /// <paramref name="target"/> — true if the raw type is already it, or the map resolves to it.
        /// </summary>
        /// <remarks>
        /// <b>Both directions are tested on purpose, and the raw one is not redundant.</b> §14.7.4
        /// forbids remapping a standard type to another standard type, but nothing stops a file
        /// containing <c>/RoleMap &lt;&lt; /Figure /Div &gt;&gt;</c> — and resolving that blindly
        /// makes every <c>&lt;Figure&gt;</c> in the document report as a <c>&lt;Div&gt;</c>, which
        /// is exactly how a document can hide its own accessibility failures from a checker that
        /// canonicalises first. Asking "does this element mean Figure?" rather than "what is this
        /// element's canonical name?" cannot be defeated that way, and needs no standard-type
        /// vocabulary to decide it.
        /// </remarks>
        public static bool Means(string role, IReadOnlyDictionary<string, string> map, string target)
            => role != null
            && (string.Equals(role, target, StringComparison.Ordinal)
                || string.Equals(Resolve(role, map), target, StringComparison.Ordinal));
    }
}
