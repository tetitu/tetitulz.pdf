// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Rewrites one category of the catalog's name dictionary (§7.7.4, Table 32 — <c>/Dests</c>,
    /// <c>/EmbeddedFiles</c>, <c>/JavaScript</c>, …) in an appended revision, as one flat leaf
    /// (§7.9.6).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is one helper.</b> The Base writers that edit a name tree (the
    /// attachments and the destinations) ask the same questions, and each has one answer here:</para>
    /// <list type="bullet">
    ///   <item><b>Which other categories survive.</b> All of them. Replacing the catalog's whole
    ///     <c>/Names</c> dictionary with one holding only <c>/EmbeddedFiles</c> would make adding
    ///     or removing an attachment delete every named destination, every document-level script
    ///     and every other name tree the document had — while the output still opened and its
    ///     attachments were correct, so nothing would notice.</item>
    ///   <item><b>What a key is.</b> The bytes as stored. §7.9.6 compares keys "on a simple
    ///     byte-by-byte basis", and rebuilding a key from its decoded text with
    ///     <c>new PdfString(text)</c> changes the bytes: a key stored as UTF-16BE whose text is
    ///     ASCII would come back one byte per character, and a PDFDocEncoded key holding a bullet
    ///     or a dash as UTF-16BE. Either way a <c>/GoTo</c> naming the destination by its original
    ///     bytes would stop finding it.</item>
    ///   <item><b>Which order.</b> §7.9.6 sorts by bytes, not decoded text, and
    ///     <c>PdfNameTree.CompareKeys</c> is that rule.</item>
    ///   <item><b>A truncated read.</b> A tree that could not be read in full is not rewritten:
    ///     rewriting the part read would drop the rest.</item>
    /// </list>
    /// <para><b>Where the leaf goes.</b> An indirect tree root is superseded in place, so nothing
    /// that points at it changes. Otherwise a new leaf object is minted, and the name dictionary
    /// is rewritten where it lives: in place when it is indirect, inside a catalog clone when it
    /// is direct. An emptied category is removed from the dictionary rather than left as an empty
    /// tree. PDF/A-1 forbids an <c>/EmbeddedFiles</c> entry at all, empty or not, so removing a
    /// document's last attachment must leave none behind.</para>
    /// </remarks>
    internal static class CatalogNameTree
    {
        /// <summary>A name-tree entry: its key as stored, and its value unresolved.</summary>
        internal readonly struct Entry
        {
            internal Entry(PdfString key, PdfObject value) { Key = key; Value = value; }

            /// <summary>The key, with the bytes it was stored with.</summary>
            internal PdfString Key { get; }

            /// <summary>The value exactly as the leaf held it — normally a reference.</summary>
            internal PdfObject Value { get; }

            /// <summary>The key as text, for matching a caller's name against it.</summary>
            internal string Text => Key.GetText();
        }

        /// <summary>
        /// Every entry of <paramref name="category"/>'s tree, in tree order, for a caller that is
        /// about to rewrite it.
        /// </summary>
        /// <exception cref="Core.PdfException">
        /// The walk stopped at the entry budget or the depth limit. The caller replaces the tree
        /// with what this returns, so a partial read would delete the entries it did not reach.
        /// </exception>
        /// <remarks>
        /// Each key is a fresh <see cref="PdfString"/> over the stored bytes. A fresh object,
        /// because the parsed one can carry the object number of an indirect string. The stored
        /// bytes, for the byte-comparison reason in the class remarks. A <c>/Name</c> key, which
        /// §7.9.6 does not allow but real files carry, becomes a string of its text. The tree is
        /// being rewritten anyway, and a string is the only key type a lookup is required to
        /// match. A key that is neither a string nor a name is dropped, because no lookup can
        /// find it.
        /// </remarks>
        internal static List<Entry> ReadForRewrite(PdfDocument doc, PdfDictionary catalog, PdfName category)
        {
            var entries = new List<Entry>();
            var names = ReadNameDictionary(doc, catalog);
            var root = names?[category];
            if (root == null) return entries;

            var flat = new List<KeyValuePair<PdfObject, PdfObject>>();
            int budget = PdfLimits.MaxNameTreeEntries;
            if (!PdfNameTree.TryFlatten(root, doc, flat, ref budget))
                throw new Core.PdfException(
                    "The document's /" + category.Value + " name tree could not be read in full " +
                    "(entry budget or depth limit reached), so rewriting it would discard the " +
                    "entries that were not visited. No changes were written.");

            foreach (var pair in flat)
            {
                switch (doc.ResolveObject(pair.Key))
                {
                    case PdfString s:
                        entries.Add(new Entry(new PdfString(s.RawBytes, s.IsHex), pair.Value));
                        break;
                    case PdfName n:
                        entries.Add(new Entry(new PdfString(n.Value), pair.Value));
                        break;
                }
            }
            return entries;
        }

        /// <summary>
        /// Writes <paramref name="entries"/> as <paramref name="category"/>'s tree, adding what
        /// the revision must carry to <paramref name="objects"/>.
        /// </summary>
        /// <param name="doc">The document being revised.</param>
        /// <param name="catalog">The document's catalog as read.</param>
        /// <param name="updatedCatalog">
        /// A catalog clone the caller has already made for its own edits, or <c>null</c>.
        /// </param>
        /// <param name="category">The name-dictionary key: <c>/Dests</c>, <c>/EmbeddedFiles</c>, ….</param>
        /// <param name="entries">The whole tree after the caller's edit, in any order.</param>
        /// <param name="objects">Receives the objects to append.</param>
        /// <param name="nextObjectNumber">The next free object number; advanced past any minted.</param>
        /// <returns>
        /// The catalog clone to supersede the catalog with: <paramref name="updatedCatalog"/>,
        /// one made here, or <c>null</c> when neither the caller nor this changed the catalog.
        /// </returns>
        internal static PdfDictionary Write(
            PdfDocument doc,
            PdfDictionary catalog,
            PdfDictionary updatedCatalog,
            PdfName category,
            List<Entry> entries,
            List<IndirectObject> objects,
            ref int nextObjectNumber)
        {
            var namesObj = catalog[PdfName.Names];
            var names = ReadNameDictionary(doc, catalog);
            var rootObj = names?[category];

            if (entries.Count == 0)
            {
                if (rootObj == null) return updatedCatalog;
                var withoutCategory = PdfDictionary.CloneShallow(names);
                withoutCategory.Remove(category);
                return PutNameDictionary(catalog, updatedCatalog, namesObj, names, withoutCategory, objects);
            }

            var leaf = new PdfDictionary();
            leaf[PdfName.Names] = SortedLeafArray(entries);

            if (rootObj is PdfReference rootRef && doc.ResolveObject(rootRef) is PdfDictionary)
            {
                // The writer keeps the superseded object's generation (ExistingObjectGeneration).
                objects.Add(new IndirectObject(rootRef.ReferenceObjectNumber, 0, leaf));
                return updatedCatalog;
            }

            int leafNumber = nextObjectNumber++;
            objects.Add(new IndirectObject(leafNumber, 0, leaf));

            var updatedNames = names != null ? PdfDictionary.CloneShallow(names) : new PdfDictionary();
            updatedNames[category] = new PdfReference(leafNumber, 0);
            return PutNameDictionary(catalog, updatedCatalog, namesObj, names, updatedNames, objects);
        }

        /// <summary>
        /// The catalog's name dictionary, or null when it has none. One the parser cannot read is
        /// refused rather than read as none.
        /// </summary>
        /// <exception cref="Core.PdfException">
        /// <c>/Names</c> names an object the parser could not read.
        /// </exception>
        /// <remarks>
        /// An unreadable <c>/Names</c> is not read as no name dictionary: the rewrite would then write
        /// one holding only the edited category, dropping every other tree (the attachments, the
        /// document JavaScript, the named destinations) from the document's current revision.
        /// </remarks>
        private static PdfDictionary ReadNameDictionary(PdfDocument doc, PdfDictionary catalog)
        {
            var names = EntryRead.Resolve(doc.ResolveObject, catalog[PdfName.Names], out string unreadable) as PdfDictionary;
            if (unreadable != null)
                throw new Core.PdfException(
                    "The document's /Names dictionary could not be read (" + unreadable + "), so " +
                    "rewriting one of its trees would discard the others. No changes were written.");
            return names;
        }

        /// <summary>
        /// Puts the rewritten name dictionary where the original lives: in place when it is an
        /// indirect object, inside the catalog otherwise. Writing it into the catalog
        /// unconditionally would turn an indirect <c>/Names</c> into a direct copy and leave the
        /// original object stale.
        /// </summary>
        private static PdfDictionary PutNameDictionary(
            PdfDictionary catalog, PdfDictionary updatedCatalog,
            PdfObject namesObj, PdfDictionary names, PdfDictionary updatedNames,
            List<IndirectObject> objects)
        {
            if (namesObj is PdfReference namesRef && names != null)
            {
                objects.Add(new IndirectObject(namesRef.ReferenceObjectNumber, 0, updatedNames));
                return updatedCatalog;
            }

            updatedCatalog ??= PdfDictionary.CloneShallow(catalog);
            updatedCatalog[PdfName.Names] = updatedNames;
            return updatedCatalog;
        }

        /// <summary>
        /// The leaf's <c>/Names</c> array, sorted by key bytes (§7.9.6). Equal keys keep their
        /// input order. A malformed tree can hold duplicates, and nothing here decides which one
        /// wins.
        /// </summary>
        private static PdfArray SortedLeafArray(List<Entry> entries)
        {
            var order = new List<int>(entries.Count);
            for (int i = 0; i < entries.Count; i++) order.Add(i);
            order.Sort((x, y) =>
            {
                int c = PdfNameTree.CompareKeys(entries[x].Key.RawBytes, entries[y].Key.RawBytes);
                return c != 0 ? c : x.CompareTo(y);
            });

            var array = new PdfArray();
            foreach (int i in order)
            {
                array.Add(entries[i].Key);
                array.Add(entries[i].Value);
            }
            return array;
        }
    }
}
