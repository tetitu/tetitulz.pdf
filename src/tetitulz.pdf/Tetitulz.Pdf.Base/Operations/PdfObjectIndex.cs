// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// A one-shot, read-only index over a <see cref="PdfDocument"/>'s cross-reference table
    /// that answers "which object number is this object?" in O(1).
    ///
    /// <para>
    /// <b>Why this exists.</b> It lived in <c>Tetitulz.Pdf.Accessibility</c> until plan 04 D2
    /// moved it down here, so that the Conformance plugin could reach the font-embedding
    /// remediator built on it without the banned plugin → feature reference. Without it the
    /// accessibility editors would answer that question by walking the <i>entire</i> cross-reference
    /// table and calling <c>doc.GetObject(n)</c> on
    /// every entry until reference-equality hit — a lookup invoked per figure, and
    /// nested inside it per page, per structure leaf, per <c>/K</c> child and per artifact
    /// line — a composite cost approaching O(elements × pages × objects). On a large tagged
    /// PDF that is the difference between milliseconds and minutes. Building this index once
    /// per operation resolves every object exactly once and turns every subsequent lookup
    /// into a dictionary probe.
    /// </para>
    ///
    /// <para>
    /// The index also owns the page maps (page object number ↔ page index), so callers stop
    /// re-running the inner <c>for (i = 0; i &lt; doc.Pages.Count; i++)</c> loop, and a
    /// monotonic object-number allocator, so callers stop hand-rolling <c>nextId++</c> from
    /// <c>GetMaxObjectNumber()</c> and risking collisions between two allocation sites.
    /// </para>
    ///
    /// <para>
    /// Building has no side effects on the document beyond forcing the (idempotent, lazy)
    /// page-tree parse. Instances are <b>not</b> thread-safe for concurrent
    /// <see cref="AllocateObjectNumber"/> calls; build and use one per operation.
    /// </para>
    /// </summary>
    public sealed class PdfObjectIndex
    {
        /// <summary>
        /// Reference-identity comparer for <see cref="PdfObject"/>:
        /// <see cref="PdfReferenceEquality.Instance"/>, under the name the walkers already use.
        /// </summary>
        /// <remarks>
        /// <para>The object model deliberately does <b>not</b> guarantee reference semantics for
        /// <c>Equals</c>/<c>GetHashCode</c> — <see cref="PdfName"/> and <see cref="PdfReference"/>
        /// both override them with value semantics. Keying an identity map on those would collapse
        /// every <c>/Type</c> name in the file onto one slot and return nonsense object numbers.</para>
        /// <para>It was a private comparer class of its own, one of five copies of
        /// <see cref="PdfReferenceEquality"/>, which CLAUDE.md names as the only one. The property
        /// is public API, so it stays, and returns that one instance.</para>
        /// </remarks>
        public static IEqualityComparer<PdfObject> ReferenceIdentityComparer => PdfReferenceEquality.Instance;

        private readonly Dictionary<PdfObject, int> _numbers =
            new Dictionary<PdfObject, int>(PdfReferenceEquality.Instance);

        private readonly Dictionary<int, int> _generations = new Dictionary<int, int>();

        private readonly Dictionary<int, int> _pageIndexByObjectNumber = new Dictionary<int, int>();

        private int[] _pageObjectNumbers = Array.Empty<int>();

        private int _maxObjectNumber;
        private int _nextObjectNumber = 1;

        private PdfObjectIndex() { }

        /// <summary>
        /// The highest object number present in the document's cross-reference table
        /// (0 if the table is empty or unreadable).
        /// </summary>
        public int MaxObjectNumber => _maxObjectNumber;

        /// <summary>Number of pages the page maps cover (0 if the page tree was unreadable).</summary>
        public int PageCount => _pageObjectNumbers.Length;

        /// <summary>
        /// Builds the index in a single pass over <c>doc.CrossReferenceTable.AllEntries</c>.
        /// Free entries are skipped. Every non-free entry is resolved exactly once; an object
        /// that fails to resolve is reported through <see cref="DocumentEditDiagnostics"/>
        /// and simply omitted from the identity map (its generation is still recorded), so a
        /// single corrupt object never fails the whole operation.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        public static PdfObjectIndex Build(PdfDocument doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            var index = new PdfObjectIndex();
            index.PopulateObjects(doc);
            index.PopulatePages(doc);
            index._nextObjectNumber = index._maxObjectNumber + 1;
            return index;
        }

        // ------------------------------------------------------------------
        //  Lookups
        // ------------------------------------------------------------------

        /// <summary>
        /// Returns the object number that <paramref name="obj"/> was parsed from, or
        /// <c>-1</c> when the object is unknown to this index (a direct/inline object, a
        /// caller-built clone, or an entry that failed to resolve during
        /// <see cref="Build"/>).
        ///
        /// <para>
        /// Matching is by <b>reference identity</b>, exactly like the linear scan it
        /// replaces: <c>doc.GetObject(n)</c> hands out a cached instance, so the instance a
        /// caller obtained by resolving a reference is the same instance this index recorded.
        /// </para>
        /// </summary>
        public int GetObjectNumber(PdfObject obj)
        {
            if (ReferenceEquals(obj, null)) return -1;

            if (_numbers.TryGetValue(obj, out int number))
                return number;

            // Fallback for the narrow case where Build could not resolve the entry (and so
            // never recorded the instance) but the caller obtained the object by some other
            // route: the parser stamps ObjectNumber onto every indirect object it produces.
            // Only trust it when the cross-reference table actually knows that number.
            int stamped = obj.ObjectNumber;
            if (stamped > 0 && _generations.ContainsKey(stamped))
                return stamped;

            return -1;
        }

        /// <summary>
        /// Returns the generation number recorded in the cross-reference table for
        /// <paramref name="objectNumber"/>, or <c>0</c> when the entry is unknown or carries
        /// no generation (objects inside object streams are always generation 0).
        /// </summary>
        public int GetGeneration(int objectNumber)
            => _generations.TryGetValue(objectNumber, out int gen) ? gen : 0;

        /// <summary>Whether the cross-reference table has a non-free entry for this object number.</summary>
        public bool Contains(int objectNumber) => _generations.ContainsKey(objectNumber);

        /// <summary>
        /// Maps a page's object number to its zero-based index in <c>doc.Pages</c>, or
        /// <c>-1</c> when the number is not a page (or the page tree was unreadable).
        /// </summary>
        public int GetPageIndex(int pageObjectNumber)
            => _pageIndexByObjectNumber.TryGetValue(pageObjectNumber, out int idx) ? idx : -1;

        /// <summary>
        /// Maps a zero-based page index to that page's object number, or <c>-1</c> when the
        /// index is out of range or the page's object number could not be determined.
        /// </summary>
        public int GetPageObjectNumber(int pageIndex)
            => pageIndex >= 0 && pageIndex < _pageObjectNumbers.Length ? _pageObjectNumbers[pageIndex] : -1;

        // ------------------------------------------------------------------
        //  Allocation
        // ------------------------------------------------------------------

        /// <summary>
        /// Hands out the next free object number, starting at
        /// <see cref="MaxObjectNumber"/> + 1 and increasing monotonically. All allocation
        /// sites in one operation must share a single index instance so two of them can
        /// never mint the same number.
        /// </summary>
        public int AllocateObjectNumber() => _nextObjectNumber++;

        /// <summary>
        /// Ensures the allocator will never hand out <paramref name="objectNumber"/> or any
        /// lower number. Use when an object number was assigned outside this index (for
        /// example carried over from a previous pass) to keep the two sources collision-free.
        /// </summary>
        public void ReserveObjectNumber(int objectNumber)
        {
            if (objectNumber >= _nextObjectNumber)
                _nextObjectNumber = objectNumber + 1;
            if (objectNumber > _maxObjectNumber)
                _maxObjectNumber = objectNumber;
        }

        // ------------------------------------------------------------------
        //  Construction
        // ------------------------------------------------------------------

        private void PopulateObjects(PdfDocument doc)
        {
            CrossReferenceTable xref;
            try
            {
                xref = doc.CrossReferenceTable;
            }
            catch (Exception ex)
            {
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "the cross-reference table could not be read; every object-number lookup will return -1", ex);
                return;
            }

            if (xref == null)
            {
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "the document has no cross-reference table; every object-number lookup will return -1");
                return;
            }

            try
            {
                foreach (var entry in xref.AllEntries)
                {
                    if (entry == null) continue;
                    if (entry.Type == CrossReferenceEntry.EntryType.Free) continue;
                    if (entry.ObjectNumber <= 0) continue;

                    if (entry.ObjectNumber > _maxObjectNumber)
                        _maxObjectNumber = entry.ObjectNumber;

                    // Compressed entries (objects inside an /ObjStm) are always generation 0
                    // per PDF 32000-1:2008 §7.5.8.3; the parser leaves the field at 0 for them.
                    _generations[entry.ObjectNumber] = entry.GenerationNumber;

                    PdfObject obj;
                    try
                    {
                        obj = doc.GetObject(entry.ObjectNumber);
                    }
                    catch (Exception ex)
                    {
                        DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                            "object " + entry.ObjectNumber.ToString() +
                            " could not be resolved; it is omitted from the identity map", ex);
                        continue;
                    }

                    if (ReferenceEquals(obj, null) || obj is PdfNull) continue;

                    // First entry wins: CrossReferenceTable already resolves incremental
                    // updates newest-first, so the instance recorded here is the current
                    // revision of that object.
                    if (!_numbers.ContainsKey(obj))
                        _numbers[obj] = entry.ObjectNumber;
                }
            }
            catch (Exception ex)
            {
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "enumeration of the cross-reference table failed part-way; the index is incomplete", ex);
            }

            try
            {
                int reportedMax = xref.GetMaxObjectNumber();
                if (reportedMax > _maxObjectNumber)
                    _maxObjectNumber = reportedMax;
            }
            catch (Exception ex)
            {
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "GetMaxObjectNumber() failed; new object numbers are allocated from the highest entry seen", ex);
            }
        }

        private void PopulatePages(PdfDocument doc)
        {
            if (doc.IsEncrypted)
            {
                // Encrypted documents never load a catalog (see PdfDocument.Load), so the
                // page tree is unavailable by design.  Say so rather than leaving callers to
                // wonder why every GetPageIndex call returns -1.
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "the document is encrypted, so no page tree is available; page lookups return -1");
                return;
            }

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception ex)
            {
                DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                    "the page tree could not be read; page lookups return -1", ex);
                return;
            }

            if (pages == null) return;

            var numbers = new int[pages.Count];
            for (int i = 0; i < pages.Count; i++)
            {
                numbers[i] = -1;

                PdfDictionary dict;
                try
                {
                    dict = pages[i].Dictionary;
                }
                catch (Exception ex)
                {
                    DocumentEditDiagnostics.Report("PdfObjectIndex.Build",
                        "page " + i.ToString() + " could not be read; it is omitted from the page maps", ex);
                    continue;
                }

                int number = GetObjectNumber(dict);
                if (number <= 0) continue;

                numbers[i] = number;
                if (!_pageIndexByObjectNumber.ContainsKey(number))
                    _pageIndexByObjectNumber[number] = i;
            }

            _pageObjectNumbers = numbers;
        }
    }
}
