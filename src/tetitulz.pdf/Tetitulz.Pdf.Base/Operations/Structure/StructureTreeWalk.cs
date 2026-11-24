// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>What stopped, or bent, a <see cref="StructureTreeWalk"/>.</summary>
    public enum StructureTreeWalkEvent
    {
        /// <summary>A node deeper than <see cref="StructureTreeWalk.MaxDepth"/> was not entered.</summary>
        DepthCapReached,

        /// <summary>
        /// <see cref="StructureTreeWalk.MaxNodes"/> nodes had been entered and a further one was not.
        /// </summary>
        NodeCapReached,

        /// <summary>
        /// Every <c>/K</c> slot <see cref="StructureTreeWalk.MaxEdges"/> allows had been examined; the
        /// rest of an array was not.
        /// </summary>
        EdgeCapReached,

        /// <summary>
        /// An element already entered was reached again — a <c>/K</c> cycle, or a shared subtree
        /// (a DAG, which §14.7.2 permits). It is entered at its first position only.
        /// </summary>
        Revisit,

        /// <summary>
        /// A <c>/K</c> slot's reference could not be resolved. The slot is still handed to
        /// <see cref="StructureTreeWalk.Kid"/>, with no resolved object, and nothing is entered through it.
        /// </summary>
        UnresolvableKid,

        /// <summary>
        /// An element's indirect <c>/K</c> could not be resolved; the element is treated as having
        /// that one reference kid.
        /// </summary>
        UnresolvableKids,
    }

    /// <summary>One structure element (or the <c>/StructTreeRoot</c>) a <see cref="StructureTreeWalk"/> entered.</summary>
    public sealed class StructureTreeNode
    {
        internal StructureTreeNode(PdfDictionary dictionary, StructureTreeNode parent, int objectNumber, int depth,
            string role, IReadOnlyList<PdfObject> rawKids, int ownPageIndex)
        {
            Dictionary = dictionary;
            Parent = parent;
            ObjectNumber = objectNumber;
            Depth = depth;
            Role = role;
            RawKids = rawKids;
            OwnPageIndex = ownPageIndex;
            PageIndex = ownPageIndex >= 0 ? ownPageIndex : parent?.PageIndex ?? -1;
        }

        /// <summary>The element dictionary. Never null.</summary>
        public PdfDictionary Dictionary { get; }

        /// <summary>The node this one was reached from, or null for the node the walk started at.</summary>
        public StructureTreeNode Parent { get; }

        /// <summary>
        /// The object number the document names this element by — the reference it was reached
        /// through, else the index's or the parser's record — or <c>-1</c> for a direct dictionary.
        /// </summary>
        public int ObjectNumber { get; }

        /// <summary>Depth below the start node (0 for that node).</summary>
        public int Depth { get; }

        /// <summary>
        /// The structure type from <c>/S</c>, resolved (§7.3.10 lets it be indirect), or null when the
        /// dictionary carries none — in practice the <c>/StructTreeRoot</c>.
        /// </summary>
        public string Role { get; }

        /// <summary>
        /// The element's <c>/K</c> children exactly as stored, <b>unresolved</b>: references stay
        /// <see cref="PdfReference"/>, marked-content identifiers stay <see cref="PdfNumber"/>, and
        /// <c>/MCR</c> / <c>/OBJR</c> dictionaries stay dictionaries. A view over the document's array,
        /// never a copy: a shared <c>/K</c> array retained once per element would make retained memory
        /// O(nodes × kids). Never null.
        /// </summary>
        public IReadOnlyList<PdfObject> RawKids { get; }

        /// <summary>The zero-based page index this element's own <c>/Pg</c> names, or <c>-1</c>.</summary>
        public int OwnPageIndex { get; }

        /// <summary>
        /// <see cref="OwnPageIndex"/>, or the nearest ancestor's when the element has no <c>/Pg</c> of
        /// its own (Table 323 makes it inheritable), or <c>-1</c>.
        /// </summary>
        public int PageIndex { get; }

        /// <summary>
        /// A consumer's own state for this node — the text it is accumulating, the children it is
        /// collecting. The walk never reads it; a consumer with several nodes in flight keeps its
        /// state here rather than in a dictionary keyed by node.
        /// </summary>
        public object Tag { get; set; }
    }

    /// <summary>One <c>/K</c> slot a <see cref="StructureTreeWalk"/> examined, in array order.</summary>
    public readonly struct StructureTreeKid
    {
        internal StructureTreeKid(StructureTreeNode parent, int slot, PdfObject raw, PdfObject resolved,
            StructKidKind kind, int objectNumber, bool alreadyVisited)
        {
            Parent = parent;
            Slot = slot;
            Raw = raw;
            Resolved = resolved;
            Kind = kind;
            ObjectNumber = objectNumber;
            AlreadyVisited = alreadyVisited;
        }

        /// <summary>The element whose <c>/K</c> holds the slot.</summary>
        public StructureTreeNode Parent { get; }

        /// <summary>The slot's position in <see cref="StructureTreeNode.RawKids"/>.</summary>
        public int Slot { get; }

        /// <summary>The slot's object as stored.</summary>
        public PdfObject Raw { get; }

        /// <summary>
        /// The slot's object resolved — a <see cref="PdfNumber"/> for a marked-content identifier, a
        /// dictionary for an element, <c>/MCR</c> or <c>/OBJR</c> — or null when it is <c>null</c>,
        /// could not be resolved, or the walk has no document to resolve through.
        /// </summary>
        public PdfObject Resolved { get; }

        /// <summary>
        /// <see cref="StructKidClassifier"/>'s answer for a dictionary;
        /// <see cref="StructKidKind.Unknown"/> for anything that is not one.
        /// </summary>
        public StructKidKind Kind { get; }

        /// <summary>The reference's object number when <see cref="Raw"/> is one, else <c>-1</c>.</summary>
        public int ObjectNumber { get; }

        /// <summary>
        /// True for an element the walk had already entered elsewhere — a cycle or a shared subtree.
        /// It is not entered again; a consumer that counts what a parent reaches still sees the slot.
        /// </summary>
        public bool AlreadyVisited { get; }

        /// <summary>True when the slot is a bare marked-content identifier (§14.7.4.2).</summary>
        public bool IsMarkedContentIdentifier => Resolved is PdfNumber;

        /// <summary>True for a structure element the walk enters after this call (not already visited).</summary>
        public bool IsEntered => Kind == StructKidKind.Element && !AlreadyVisited && Resolved is PdfDictionary;
    }

    /// <summary>Receives a <see cref="StructureTreeWalkEvent"/> as it happens.</summary>
    /// <param name="walkEvent">What happened.</param>
    /// <param name="at">The node being expanded when it happened, or null before any was entered.</param>
    /// <param name="objectNumber">The object number concerned (the refused or revisited element, the unresolvable reference), or <c>-1</c>.</param>
    /// <param name="exception">The failure, for the two unresolvable events; else null.</param>
    public delegate void StructureTreeWalkReport(StructureTreeWalkEvent walkEvent, StructureTreeNode at,
        int objectNumber, Exception exception);

    /// <summary>
    /// The one cycle-safe, bounded traversal of a PDF logical structure tree (§14.7.2): the
    /// accessibility feature's walker, the tagged-text extractor, <see cref="PdfStructureTreeReader"/>
    /// and the PDF/UA checker all read the tree through it.
    /// </summary>
    /// <remarks>
    /// <para><b>Why one.</b> A hand-written walk of this tree caps recursion depth and tracks
    /// visited elements, and separate walks each come to lack a bound another carries — an edge
    /// budget, a cycle guard over both <c>/K</c> spellings, a node budget that fires on a
    /// shared-array graph. A walk in one place is compared with nothing.</para>
    ///
    /// <para><b>What it bounds.</b> A global visited set — object number where the element has one,
    /// reference identity for a direct dictionary — makes every reachable element expand at most
    /// once, so a branching <c>/K</c> cycle costs its size rather than 2^depth. <see cref="MaxDepth"/>
    /// bounds one branch, <see cref="MaxNodes"/> the number of branches, and <see cref="MaxEdges"/>
    /// the total <c>/K</c> slots examined — the one a visited set does nothing about, since N elements
    /// sharing one N-entry array is N² slots from O(N) bytes of file while every element is expanded
    /// exactly once. A ceiling is reported through <see cref="Report"/> and recorded on the walk; what
    /// a consumer makes of it (a partial result, a refusal) is the consumer's.</para>
    ///
    /// <para><b>What it tells a consumer.</b> <see cref="Enter"/> for each element in document
    /// (pre-order) order, <see cref="Kid"/> for each <c>/K</c> slot in array order — before the element
    /// it names is entered, so a consumer interleaving leaves and children sees them in the file's
    /// order — and <see cref="Leave"/> after an element's kids, so a consumer accumulating a subtree
    /// finishes it there. A revisited element is a <see cref="Kid"/> that is not entered.</para>
    /// </remarks>
    public sealed class StructureTreeWalk
    {
        private static readonly IReadOnlyList<PdfObject> _noKids = Array.Empty<PdfObject>();

        private readonly PdfDocument _doc;
        private readonly PdfObjectIndex _index;
        private readonly Dictionary<int, int> _pageByNumber = new Dictionary<int, int>();
        private readonly Dictionary<PdfObject, int> _pageByIdentity =
            new Dictionary<PdfObject, int>(PdfReferenceEquality.Instance);

        private HashSet<int> _visitedNumbers;
        private HashSet<PdfObject> _visitedDirect;
        private int _edgesLeft;

        /// <summary>
        /// Prepares a walk over <paramref name="doc"/>'s structure tree.
        /// </summary>
        /// <param name="doc">
        /// The document, for resolving indirect <c>/K</c> children, <c>/S</c> and <c>/Pg</c>. May be
        /// null, in which case nothing indirect is resolved: a reference kid has no
        /// <see cref="StructureTreeKid.Resolved"/> object and is not entered.
        /// </param>
        /// <param name="index">
        /// An object index for <paramref name="doc"/>, used only to number an element reached
        /// without a reference (the start node, a direct dictionary the parser stamped). May be null.
        /// </param>
        public StructureTreeWalk(PdfDocument doc, PdfObjectIndex index = null)
        {
            _doc = doc;
            _index = index;
            IndexPages(doc);
        }

        /// <summary>Nodes deeper than this are not entered. Defaults to <see cref="PdfLimits.MaxStructTreeDepth"/>.</summary>
        public int MaxDepth { get; set; } = PdfLimits.MaxStructTreeDepth;

        /// <summary>At most this many nodes are entered. Defaults to <see cref="PdfLimits.MaxStructTreeNodes"/>.</summary>
        public int MaxNodes { get; set; } = PdfLimits.MaxStructTreeNodes;

        /// <summary>
        /// At most this many <c>/K</c> slots are examined across the whole walk. Defaults to
        /// <see cref="PdfLimits.MaxStructTreeEdges"/>.
        /// </summary>
        public int MaxEdges { get; set; } = PdfLimits.MaxStructTreeEdges;

        /// <summary>Called for each node entered, in document (pre-order) order, before its kids.</summary>
        public Action<StructureTreeNode> Enter { get; set; }

        /// <summary>Called for each node entered after its kids have been examined and its child elements left.</summary>
        public Action<StructureTreeNode> Leave { get; set; }

        /// <summary>
        /// Called for each <c>/K</c> slot examined, in array order, before the element it names (if any)
        /// is entered. Not called for a slot past <see cref="MaxEdges"/>.
        /// </summary>
        public Action<StructureTreeKid> Kid { get; set; }

        /// <summary>
        /// Called once for the first occurrence of each ceiling and of a revisit, and for every
        /// unresolvable reference. A consumer that refuses a truncated tree throws from here.
        /// </summary>
        public StructureTreeWalkReport Report { get; set; }

        /// <summary><c>/K</c> slots the last <see cref="Run"/> examined — the liveness evidence for <see cref="MaxEdges"/>.</summary>
        public int EdgesExamined { get; private set; }

        /// <summary>Nodes the last <see cref="Run"/> entered.</summary>
        public int NodesVisited { get; private set; }

        /// <summary>True when a node was refused for depth.</summary>
        public bool DepthCapReached { get; private set; }

        /// <summary>True when a node was refused for the node ceiling.</summary>
        public bool NodeCapReached { get; private set; }

        /// <summary>True when a <c>/K</c> array was left partly unexamined for the edge ceiling.</summary>
        public bool EdgeCapReached { get; private set; }

        /// <summary>True when an element was reached a second time.</summary>
        public bool Revisited { get; private set; }

        /// <summary>
        /// The first ceiling that stopped part of the walk, or null when every reachable element was
        /// entered. A revisit is not truncation: a shared subtree is entered once and nothing is missing.
        /// </summary>
        public StructureTreeWalkEvent? FirstCeiling { get; private set; }

        /// <summary>
        /// Walks the tree below <paramref name="start"/> — the <c>/StructTreeRoot</c>, or any structure
        /// element for a subtree. The start node is always entered, <c>/S</c> or not. No-ops when null.
        /// A second call starts afresh.
        /// </summary>
        public void Run(PdfDictionary start)
        {
            _visitedNumbers = new HashSet<int>();
            _visitedDirect = new HashSet<PdfObject>(PdfReferenceEquality.Instance);
            _edgesLeft = MaxEdges;
            EdgesExamined = 0;
            NodesVisited = 0;
            DepthCapReached = NodeCapReached = EdgeCapReached = Revisited = false;
            FirstCeiling = null;

            if (start == null) return;
            Visit(start, null, 0, -1);
        }

        /// <summary>
        /// The element's <c>/K</c> children as raw (unresolved) objects, in every shape <c>/K</c> may
        /// take: absent, an array, a single dictionary, a reference, a reference to an array, a bare
        /// integer. Never null. <paramref name="doc"/> resolves only the reference-to-an-array case;
        /// when it is null, or the reference cannot be resolved (<paramref name="onUnresolvable"/> is
        /// told), such a <c>/K</c> is one reference kid.
        /// </summary>
        public static IReadOnlyList<PdfObject> RawKidsOf(PdfDictionary node, PdfDocument doc,
            Action<Exception> onUnresolvable = null)
        {
            if (node == null) return _noKids;

            PdfObject k = node[PdfName.K];
            if (k == null || k is PdfNull) return _noKids;

            if (k is PdfReference && doc != null)
            {
                PdfObject resolved;
                try
                {
                    resolved = doc.ResolveObject(k);
                }
                catch (Exception ex)
                {
                    onUnresolvable?.Invoke(ex);
                    resolved = null;
                }

                if (resolved is PdfArray resolvedArray)
                    return View(resolvedArray);
            }

            if (k is PdfArray array)
                return View(array);

            return new[] { k };
        }

        /// <summary>
        /// The structure type from <c>/S</c>, resolved, or null when <paramref name="node"/> is null or
        /// carries none. The indexer does not follow a reference, and <c>/S 9 0 R</c> read raw is null —
        /// which three accessibility checks took to mean "this is the root" and skipped the element.
        /// </summary>
        public static string RoleOf(PdfDictionary node, PdfDocument doc)
        {
            PdfObject s = node?[PdfName.S];
            return ((doc?.ResolveObject(s) ?? s) as PdfName)?.Value;
        }

        // ------------------------------------------------------------------

        private void Visit(PdfDictionary dict, StructureTreeNode parent, int depth, int knownObjectNumber)
        {
            if (NodesVisited >= MaxNodes)
            {
                Ceiling(StructureTreeWalkEvent.NodeCapReached, ref _nodeCapReported, parent, knownObjectNumber);
                NodeCapReached = true;
                return;
            }

            if (depth > MaxDepth)
            {
                Ceiling(StructureTreeWalkEvent.DepthCapReached, ref _depthCapReported, parent, knownObjectNumber);
                DepthCapReached = true;
                return;
            }

            int objectNumber = ResolveObjectNumber(dict, knownObjectNumber);
            bool fresh = objectNumber > 0 ? _visitedNumbers.Add(objectNumber) : _visitedDirect.Add(dict);
            if (!fresh)
            {
                // Reached here only for the start node, or an element whose number the kid loop
                // could not see (a direct dictionary first entered through a reference, say): the
                // loop answers the ordinary revisit before it gets this far.
                Revisit(parent, objectNumber);
                return;
            }

            StructureTreeNode node = new StructureTreeNode(dict, parent, objectNumber, depth,
                RoleOf(dict, _doc),
                RawKidsOf(dict, _doc, ex => Report?.Invoke(StructureTreeWalkEvent.UnresolvableKids, parent, objectNumber, ex)),
                OwnPageIndexOf(dict));

            NodesVisited++;
            Enter?.Invoke(node);

            IReadOnlyList<PdfObject> kids = node.RawKids;
            for (int i = 0; i < kids.Count; i++)
            {
                // Every slot examined costs budget, whether or not it turns into a node: the visited
                // set bounds how many times an element is expanded, not the cost of expanding one.
                if (_edgesLeft <= 0)
                {
                    Ceiling(StructureTreeWalkEvent.EdgeCapReached, ref _edgeCapReported, node, -1);
                    EdgeCapReached = true;
                    break;
                }
                _edgesLeft--;
                EdgesExamined++;

                PdfObject raw = kids[i];
                int childNumber = (raw as PdfReference)?.ReferenceObjectNumber ?? -1;
                PdfObject resolved = Resolve(raw, node, childNumber);

                StructKidKind kind = resolved is PdfDictionary childDict
                    ? StructKidClassifier.Classify(childDict, _doc)
                    : StructKidKind.Unknown;

                bool alreadyVisited = kind == StructKidKind.Element &&
                    (childNumber > 0 ? _visitedNumbers.Contains(childNumber) : _visitedDirect.Contains(resolved));

                Kid?.Invoke(new StructureTreeKid(node, i, raw, resolved, kind, childNumber, alreadyVisited));

                if (kind != StructKidKind.Element) continue;
                if (alreadyVisited)
                {
                    Revisit(node, childNumber);
                    continue;
                }

                Visit((PdfDictionary)resolved, node, depth + 1, childNumber);
            }

            Leave?.Invoke(node);
        }

        private PdfObject Resolve(PdfObject raw, StructureTreeNode node, int childNumber)
        {
            if (raw == null || raw is PdfNull) return null;
            if (!(raw is PdfReference)) return raw;
            if (_doc == null) return null;

            try
            {
                PdfObject resolved = _doc.ResolveObject(raw);
                if (resolved == null || resolved is PdfNull)
                {
                    // The null object answers an absent object and an unreadable one alike; the
                    // parse error, read at once, tells them apart. An unreadable kid is reported
                    // as an unresolvable one: its subtree is not read, and a reader that said
                    // nothing handed back a tree missing it as the whole.
                    if (Core.Parsing.PdfParser.LastObjectParseError is string reason)
                        Report?.Invoke(StructureTreeWalkEvent.UnresolvableKid, node, childNumber,
                            new Core.PdfException($"structure element {childNumber} could not be read: {reason}"));
                    return null;
                }
                return resolved;
            }
            catch (Exception ex)
            {
                Report?.Invoke(StructureTreeWalkEvent.UnresolvableKid, node, childNumber, ex);
                return null;
            }
        }

        private bool _depthCapReported, _nodeCapReported, _edgeCapReported, _revisitReported;

        private void Ceiling(StructureTreeWalkEvent walkEvent, ref bool reported, StructureTreeNode at, int objectNumber)
        {
            FirstCeiling ??= walkEvent;
            if (reported) return;
            reported = true;
            Report?.Invoke(walkEvent, at, objectNumber, null);
        }

        private void Revisit(StructureTreeNode at, int objectNumber)
        {
            Revisited = true;
            if (_revisitReported) return;
            _revisitReported = true;
            Report?.Invoke(StructureTreeWalkEvent.Revisit, at, objectNumber, null);
        }

        private int ResolveObjectNumber(PdfDictionary dict, int knownObjectNumber)
        {
            // The reference we arrived through is the number the document itself names this
            // element by; the index and the parser's stamp are for an element reached without one.
            if (knownObjectNumber > 0) return knownObjectNumber;

            if (_index != null)
            {
                int number = _index.GetObjectNumber(dict);
                if (number > 0) return number;
            }

            return dict.ObjectNumber > 0 ? dict.ObjectNumber : -1;
        }

        private int OwnPageIndexOf(PdfDictionary dict)
        {
            PdfObject pg = dict[PdfName.Pg];
            if (pg is PdfReference pageRef)
                return _pageByNumber.TryGetValue(pageRef.ReferenceObjectNumber, out int byNumber) ? byNumber : -1;
            if (pg is PdfDictionary pageDict)
                return _pageByIdentity.TryGetValue(pageDict, out int byIdentity) ? byIdentity : -1;
            return -1;
        }

        private void IndexPages(PdfDocument doc)
        {
            if (doc == null || doc.IsEncrypted) return;

            IReadOnlyList<PdfPage> pages;
            try
            {
                pages = doc.Pages;
            }
            catch (Exception)
            {
                // No page tree, no page indices: every node answers -1, as it does for an
                // encrypted document, which never loads a catalog.
                return;
            }
            if (pages == null) return;

            for (int i = 0; i < pages.Count; i++)
            {
                PdfDictionary pageDict = pages[i]?.Dictionary;
                if (pageDict == null) continue;
                // First wins: a malformed file repeating an object number must not retarget an
                // earlier page.
                if (pageDict.ObjectNumber > 0 && !_pageByNumber.ContainsKey(pageDict.ObjectNumber))
                    _pageByNumber[pageDict.ObjectNumber] = i;
                if (!_pageByIdentity.ContainsKey(pageDict))
                    _pageByIdentity[pageDict] = i;
            }
        }

        private static IReadOnlyList<PdfObject> View(PdfArray array)
            => array.Count == 0 ? _noKids : new PdfArrayView(array);

        /// <summary>An O(1) read-only view over a <see cref="PdfArray"/>; the walk never mutates the document's arrays.</summary>
        private sealed class PdfArrayView : IReadOnlyList<PdfObject>
        {
            private readonly PdfArray _array;

            internal PdfArrayView(PdfArray array) { _array = array; }

            public int Count => _array.Count;

            public PdfObject this[int index] => _array[index];

            public IEnumerator<PdfObject> GetEnumerator()
            {
                for (int i = 0; i < _array.Count; i++) yield return _array[i];
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
