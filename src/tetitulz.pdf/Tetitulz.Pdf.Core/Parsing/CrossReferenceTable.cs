// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Parsing
{
    public sealed class CrossReferenceTable
    {
        private readonly Dictionary<int, CrossReferenceEntry> _entries = new Dictionary<int, CrossReferenceEntry>();

        public int Count => _entries.Count;

        /// <summary>
        /// The entries as they stand now, a copy. Reading an object of a damaged file can repair the
        /// table, so a walk that reads what it visits would otherwise throw part-way through it.
        /// </summary>
        public IEnumerable<CrossReferenceEntry> AllEntries => new List<CrossReferenceEntry>(_entries.Values);

        public void AddEntry(CrossReferenceEntry entry) => AddEntry(entry, allowReplacingFree: false);

        /// <summary>
        /// Adds <paramref name="entry"/> under the ordinary first-wins rule, optionally allowing it
        /// to displace an entry that is currently <see cref="CrossReferenceEntry.EntryType.Free"/>.
        /// </summary>
        /// <param name="entry">The entry to install.</param>
        /// <param name="allowReplacingFree">
        /// Set only on the <c>/XRefStm</c> merge path of a hybrid-reference file (§7.5.8.4). That
        /// construction deliberately marks every object stored in an object stream <b>free</b> in
        /// the classic table — so a pre-1.5 reader treats it as deleted — and records its real
        /// type-2 entry in the cross-reference stream the classic trailer's <c>/XRefStm</c> names.
        /// The classic table is parsed first, so without this the free entry wins permanently and
        /// every compressed object of the file resolves to <c>PdfNull</c>; when the catalog is one
        /// of them the document cannot be opened at all.
        /// <para>
        /// <b>The caller decides it per entry, and this flag alone cannot keep a deletion.</b> The
        /// table does not know which section installed the free entry, and the walk installs the
        /// newest revision's first — so a caller setting it for a whole <c>/XRefStm</c> let an older
        /// revision's stream bring back an object a newer classic update had deleted. The parser sets
        /// it only for an object whose free entry the <c>/XRefStm</c>'s own section installed; see
        /// <c>PdfParser.XrefSectionRef.ReplaceableFreeEntries</c>.
        /// </para>
        /// </param>
        /// <returns>Whether <paramref name="entry"/> is now the table's entry for its object number.</returns>
        internal bool AddEntry(CrossReferenceEntry entry, bool allowReplacingFree)
        {
            // Later entries (from more recent incremental updates) should NOT overwrite earlier ones
            // because we parse from the most recent xref first.
            // The first entry we encounter for a given object number is the current version.
            if (!_entries.TryGetValue(entry.ObjectNumber, out var existing))
            {
                _entries[entry.ObjectNumber] = entry;
                return true;
            }

            // The replacement must itself be an in-use entry. §7.5.8.4's construction exists so a
            // free marker can be superseded by the REAL type-2 row; letting one free entry displace
            // another only swaps in a different next-free link and generation, which is a change
            // with no meaning that an /XRefStm has no business making.
            if (allowReplacingFree &&
                existing.Type == CrossReferenceEntry.EntryType.Free &&
                entry.Type != CrossReferenceEntry.EntryType.Free)
            {
                _entries[entry.ObjectNumber] = entry;
                return true;
            }

            return false;
        }

        public void SetEntry(CrossReferenceEntry entry)
        {
            _entries[entry.ObjectNumber] = entry;
        }

        public bool Contains(int objectNumber)
        {
            return _entries.ContainsKey(objectNumber);
        }

        public CrossReferenceEntry GetEntry(int objectNumber)
        {
            _entries.TryGetValue(objectNumber, out var entry);
            return entry;
        }

        public long GetOffset(int objectNumber)
        {
            if (_entries.TryGetValue(objectNumber, out var entry) && entry.Type == CrossReferenceEntry.EntryType.Normal)
                return entry.Offset;
            return -1;
        }

        public int GetMaxObjectNumber()
        {
            int max = 0;
            foreach (var kvp in _entries)
            {
                if (kvp.Key > max)
                    max = kvp.Key;
            }
            return max;
        }
    }
}
