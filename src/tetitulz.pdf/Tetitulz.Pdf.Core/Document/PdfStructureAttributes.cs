// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// ISO 32000-1 §14.7.5 structure-element attributes: the inline <c>/A</c> form and the
    /// <c>/C</c> + <c>/StructTreeRoot</c> <c>/ClassMap</c> form.
    /// </summary>
    /// <remarks>
    /// <para><b>Why it is in Core.</b> The working implementation was <c>private static</c> on
    /// <c>PdfDeepAccessibilityChecker</c> — a <i>feature</i> — while
    /// <c>Base.Operations.Structure.PdfStructureTreeReader</c>, which is the public way to read a
    /// tagged document's tree, exposed no attributes at all. <c>Base → Accessibility</c> would
    /// invert the ring model (and close a cycle through the sanctioned
    /// <c>Accessibility → Conformance → Base</c> edge), so the code went <b>down</b> rather than
    /// sideways. Same move, same reason, as <see cref="PdfRoleMap"/>.</para>
    ///
    /// <para><b>It takes a <c>resolve</c> delegate rather than a <c>PdfDocument</c></b>, for the
    /// reason <see cref="PdfRoleMap"/> states: an encrypted document loads no catalog, so a caller
    /// that has reached the structure tree by hand must be able to supply its own resolver.</para>
    ///
    /// <para><b>A leaf — no static fields, so no type initializer.</b> Both a Base reader and a
    /// feature's checker call it, and CLAUDE.md records a sideways static reference crashing the
    /// test host mid-run.</para>
    ///
    /// <para><b>Both spellings matter.</b> With <c>/C</c> unconsulted, a checker-clean table that
    /// declares its merges and header scopes through a ClassMap would have every span read as 1 and
    /// every <c>/Scope</c> read as absent, and be reported as ragged with unaddressable
    /// headers.</para>
    /// </remarks>
    public static class PdfStructureAttributes
    {
        /// <summary>
        /// Longest <c>/C</c> array walked for one element. A real one names a handful; the cap is
        /// the only bound on an array a hostile file chooses the length of.
        /// </summary>
        public const int MaxClassNamesPerElement = 64;

        /// <summary>
        /// Reads the structure tree's <c>/ClassMap</c> — class name → attribute object.
        /// </summary>
        /// <param name="structTreeRoot">The <c>/StructTreeRoot</c> dictionary, or null.</param>
        /// <param name="resolve">Follows an indirect reference. Null means "values are direct".</param>
        /// <returns>An empty map when there is no <c>/ClassMap</c>; never null.</returns>
        /// <remarks>
        /// Values are kept exactly as stored, resolved only one level — so an indirect attribute
        /// object becomes the dictionary or array it points at — and interpreted by
        /// <see cref="Lookup"/>, because §14.7.5.2 gives a class's attribute object exactly the same
        /// shape as an inline one.
        /// </remarks>
        public static Dictionary<string, PdfObject> BuildClassMap(
            PdfDictionary structTreeRoot, Func<PdfObject, PdfObject> resolve)
        {
            var map = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
            if (structTreeRoot == null) return map;

            if (!(Resolve(resolve, structTreeRoot[PdfName.ClassMap]) is PdfDictionary classMap))
                return map;

            foreach (var kvp in classMap)
            {
                PdfObject value = Resolve(resolve, kvp.Value);
                if (value != null) map[kvp.Key.Value] = value;
            }

            return map;
        }

        /// <summary>
        /// One attribute of a structure element: <c>/A</c> first, then <c>/C</c> through the class
        /// map.
        /// </summary>
        /// <param name="element">The structure element dictionary.</param>
        /// <param name="key">The attribute name, e.g. <c>/ColSpan</c>.</param>
        /// <param name="owner">
        /// The attribute owner to accept (<c>"Table"</c>, <c>"Layout"</c>, …), or null to accept any.
        /// An attribute object that declares <b>no</b> owner is accepted either way.
        /// </param>
        /// <param name="classMap">From <see cref="BuildClassMap"/>. May be null.</param>
        /// <param name="resolve">Follows an indirect reference.</param>
        /// <remarks>
        /// <c>/A</c> is consulted first because, where an element carries both, the inline attribute
        /// is the more specific statement about that one element.
        /// </remarks>
        public static PdfObject Lookup(
            PdfDictionary element, PdfName key, string owner,
            IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
        {
            if (element == null || key == null) return null;

            PdfObject inline = element[PdfName.A];
            if (inline != null)
            {
                PdfObject value = ReadFrom(Resolve(resolve, inline), key, owner, resolve);
                if (value != null) return value;
            }

            if (classMap == null || classMap.Count == 0) return null;

            PdfObject classes = Resolve(resolve, element[PdfName.C]);
            if (classes is PdfName single)
                return classMap.TryGetValue(single.Value, out PdfObject attrs)
                    ? ReadFrom(attrs, key, owner, resolve) : null;

            if (classes is PdfArray array)
            {
                int limit = Math.Min(array.Count, MaxClassNamesPerElement);
                for (int i = 0; i < limit; i++)
                {
                    if (!(Resolve(resolve, array[i]) is PdfName name)) continue;
                    if (!classMap.TryGetValue(name.Value, out PdfObject attrs)) continue;
                    PdfObject value = ReadFrom(attrs, key, owner, resolve);
                    if (value != null) return value;
                }
            }

            return null;
        }

        /// <summary>
        /// Every attribute the element carries: owner → attribute name → value.
        /// </summary>
        /// <param name="element">The structure element dictionary.</param>
        /// <param name="classMap">From <see cref="BuildClassMap"/>. May be null.</param>
        /// <param name="resolve">Follows an indirect reference.</param>
        /// <returns>An empty map when the element declares none; never null.</returns>
        /// <remarks>
        /// <para>An attribute object with no <c>/O</c> is filed under the empty string, which is what
        /// §14.7.5.1 leaves it as: an owner is required in a conforming file, so a missing one is
        /// reported rather than guessed at.</para>
        ///
        /// <para><c>/C</c> is merged <b>under</b> <c>/A</c> — an inline attribute wins over a class
        /// one for the same owner and key, matching <see cref="Lookup"/>'s order.</para>
        /// </remarks>
        public static Dictionary<string, Dictionary<string, PdfObject>> ReadAll(
            PdfDictionary element,
            IReadOnlyDictionary<string, PdfObject> classMap,
            Func<PdfObject, PdfObject> resolve)
            => ReadAll(SourcesOf(element, classMap, resolve), resolve);

        /// <summary>
        /// The attribute objects an element names, in the order <see cref="ReadAll(PdfDictionary, IReadOnlyDictionary{string, PdfObject}, Func{PdfObject, PdfObject})"/>
        /// reads them — <c>/A</c>'s, then <c>/C</c>'s through the class map — each once: a repeat
        /// adds nothing, every key it holds already written first.
        /// </summary>
        /// <remarks>
        /// For a reader of many elements that name the same objects, which can then share one
        /// reading: elements × the entries of a shared attribute object is a product of two counts
        /// the file chooses.
        /// </remarks>
        internal static List<PdfDictionary> SourcesOf(
            PdfDictionary element,
            IReadOnlyDictionary<string, PdfObject> classMap,
            Func<PdfObject, PdfObject> resolve)
        {
            var sources = new List<PdfDictionary>();
            if (element == null) return sources;
            var seen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);

            // /A FIRST. CollectOne is first-writer-wins, so whichever pass runs first is the one
            // that survives — and the inline attribute must win, matching Lookup's order. Written
            // the other way round first, and the "inline beats the class map" test caught it: a
            // ClassMap /RowSpan 9 was overriding an inline /RowSpan 2.
            AddSources(sources, seen, Resolve(resolve, element[PdfName.A]), resolve);

            if (classMap == null || classMap.Count == 0) return sources;

            PdfObject classes = Resolve(resolve, element[PdfName.C]);
            if (classes is PdfName single && classMap.TryGetValue(single.Value, out PdfObject one))
                AddSources(sources, seen, one, resolve);
            else if (classes is PdfArray array)
            {
                int limit = Math.Min(array.Count, MaxClassNamesPerElement);
                for (int i = 0; i < limit; i++)
                    if (Resolve(resolve, array[i]) is PdfName name &&
                        classMap.TryGetValue(name.Value, out PdfObject attrs))
                        AddSources(sources, seen, attrs, resolve);
            }

            return sources;
        }

        /// <summary>What <paramref name="sources"/> say: owner → attribute name → value, the first writer winning.</summary>
        internal static Dictionary<string, Dictionary<string, PdfObject>> ReadAll(
            IReadOnlyList<PdfDictionary> sources, Func<PdfObject, PdfObject> resolve)
        {
            var byOwner = new Dictionary<string, Dictionary<string, PdfObject>>(StringComparer.Ordinal);
            foreach (var source in sources) CollectOne(byOwner, source, resolve);
            return byOwner;
        }

        // An attribute object — a dictionary, or an array of them among revision numbers — into the
        // sources, each dictionary once.
        private static void AddSources(
            List<PdfDictionary> sources, HashSet<PdfDictionary> seen, PdfObject attributes,
            Func<PdfObject, PdfObject> resolve)
        {
            if (attributes is PdfDictionary single) { if (seen.Add(single)) sources.Add(single); return; }

            if (attributes is PdfArray array)
                for (int i = 0; i < array.Count; i++)
                    if (Resolve(resolve, array[i]) is PdfDictionary d && seen.Add(d))
                        sources.Add(d);
        }

        // ----------------------------------------------------------------

        /// <summary>
        /// Reads one owned attribute out of an attribute object — a dictionary, or an array of them
        /// possibly interleaved with revision numbers (§14.7.5.4).
        /// </summary>
        private static PdfObject ReadFrom(
            PdfObject attributes, PdfName key, string owner, Func<PdfObject, PdfObject> resolve)
        {
            if (attributes is PdfDictionary single) return ReadOwned(single, key, owner, resolve);

            if (attributes is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++)
                {
                    if (!(Resolve(resolve, array[i]) is PdfDictionary d)) continue;  // revision numbers
                    PdfObject value = ReadOwned(d, key, owner, resolve);
                    if (value != null) return value;
                }
            }

            return null;
        }

        private static PdfObject ReadOwned(
            PdfDictionary attributes, PdfName key, string owner, Func<PdfObject, PdfObject> resolve)
        {
            // RESOLVED. Reading /O raw, an indirect owner would read as absent and fall into the
            // lenient "no owner declared" branch, so a /Layout attribute would be silently accepted
            // as a /Table one.
            string declared = (Resolve(resolve, attributes[PdfName.O]) as PdfName)?.Value;
            if (owner != null && declared != null && !string.Equals(declared, owner, StringComparison.Ordinal))
                return null;

            return Resolve(resolve, attributes[key]);
        }

        private static void CollectOne(
            Dictionary<string, Dictionary<string, PdfObject>> byOwner,
            PdfDictionary attributes, Func<PdfObject, PdfObject> resolve)
        {
            string owner = (Resolve(resolve, attributes[PdfName.O]) as PdfName)?.Value ?? string.Empty;

            if (!byOwner.TryGetValue(owner, out var entries))
                byOwner[owner] = entries = new Dictionary<string, PdfObject>(StringComparer.Ordinal);

            foreach (var kvp in attributes)
            {
                if (kvp.Key == PdfName.O) continue;                 // the owner is the key, not an entry
                if (entries.ContainsKey(kvp.Key.Value)) continue;   // first writer wins — see ReadAll
                PdfObject value = Resolve(resolve, kvp.Value);
                if (value != null) entries[kvp.Key.Value] = value;
            }
        }

        private static PdfObject Resolve(Func<PdfObject, PdfObject> resolve, PdfObject obj)
            => obj == null ? null : (resolve == null ? obj : resolve(obj) ?? obj);
    }
}
