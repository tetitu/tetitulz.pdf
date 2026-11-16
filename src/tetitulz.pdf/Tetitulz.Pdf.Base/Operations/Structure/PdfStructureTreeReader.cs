// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Represents a node in the PDF logical structure tree.
    /// </summary>
    public sealed class PdfStructElement
    {
        /// <summary>
        /// Structure element type: "Document", "Part", "P", "H1"–"H6",
        /// "Table", "TR", "TD", "Figure", "Span", "Link", "Artifact", etc.
        /// </summary>
        public string ElementType { get; init; }

        /// <summary>/ActualText if present, otherwise null.</summary>
        public string ActualText { get; init; }

        /// <summary>/Alt (alternate text) for figures, otherwise null.</summary>
        public string AltText { get; init; }

        /// <summary>Language (/Lang), or null if not specified.</summary>
        public string Lang { get; init; }

        /// <summary>Zero-based page index the element is bound to, or -1 if unbound.</summary>
        public int PageIndex { get; init; }

        /// <summary>Child structure elements.</summary>
        public IReadOnlyList<PdfStructElement> Children { get; init; }

        /// <summary>PDF 2.0 namespace URI (from the element's /NS entry), or null
        /// when the element is not namespaced. Namespaces allow custom vocabularies
        /// such as HTML or MathML to coexist with the standard PDF role map.</summary>
        public string Namespace { get; init; }

        /// <summary>
        /// ISO 32000-1 §14.7.5 attributes: <b>owner → attribute name → value</b>. Empty when the
        /// element declares none; never null.
        /// </summary>
        /// <remarks>
        /// <para>The owner is the attribute object's <c>/O</c> — <c>"Table"</c> for <c>/ColSpan</c>,
        /// <c>/RowSpan</c>, <c>/Scope</c> and <c>/Headers</c>; <c>"Layout"</c> for <c>/Placement</c>,
        /// <c>/BBox</c>, <c>/TextAlign</c>; <c>"List"</c> for <c>/ListNumbering</c>;
        /// <c>"PrintField"</c> for <c>/Role</c> and <c>/checked</c>. An attribute object that
        /// declares no owner is filed under the empty string, because §14.7.5.1 requires one and a
        /// missing owner is reported rather than guessed at.</para>
        ///
        /// <para><b>Both spellings are read.</b> The inline <c>/A</c> form and the <c>/C</c> +
        /// <c>/StructTreeRoot</c> <c>/ClassMap</c> form give the same information, and a producer
        /// picks whichever it likes; a reader that consults only <c>/A</c> reports every span as 1
        /// and every scope as absent on a document that uses classes. Where an element carries both
        /// for one owner and key, the inline value wins — it is the more specific statement about
        /// that one element.</para>
        /// </remarks>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PdfObject>> Attributes { get; init; }
    }

    /// <summary>
    /// Reads the logical structure tree from a Tagged PDF.
    /// </summary>
    /// <remarks>
    /// <para>The traversal is <see cref="StructureTreeWalk"/>'s — the one walk of this tree, shared
    /// with the tagged-text extractor, the accessibility feature and the PDF/UA checker. Before it
    /// was shared, this reader's own walk guarded a cycle in one of the two <c>/K</c> spellings only
    /// (an element whose <c>/K</c> was a single reference back up the tree recursed to the depth cap
    /// and threw, while every other reader stopped at the second visit), read <c>/S</c>, <c>/Alt</c>
    /// and <c>/ActualText</c> unresolved while the extractor resolved them, and was the one walk of
    /// the tree without an edge ceiling. Each was found by comparing two walks of the same tree; a
    /// walk in one place is compared with nothing.</para>
    /// <para><b>A ceiling is a refusal here, not a truncation.</b> A tree cut short at the depth,
    /// node or edge ceiling would come back looking complete — wrong but invisible, which is worse
    /// than an exception the caller can see — so the reader throws
    /// <see cref="Core.PdfException"/> naming the ceiling instead.</para>
    /// </remarks>
    public static class PdfStructureTreeReader
    {
        /// <summary>Reads the logical structure tree from a Tagged PDF file.</summary>
        public static PdfStructElement ReadStructureTree(string inputPath)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            using var fs = File.OpenRead(inputPath);
            return ReadStructureTree(fs);
        }

        /// <summary>Reads the logical structure tree from a Tagged PDF stream.</summary>
        public static PdfStructElement ReadStructureTree(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            EncryptedRead.Refuse(doc, "the structure tree", "PdfOperations.ReadStructureTree(Stream, string)");
            return ReadTree(doc);
        }

        // -------------------------------------------------------------------
        //  Implementation
        // -------------------------------------------------------------------

        private static PdfStructElement ReadTree(PdfDocument doc)
        {
            if (doc.Catalog == null) return EmptyRoot();

            var structRootObj = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]);
            if (!(structRootObj is PdfDictionary structRoot)) return EmptyRoot();

            // §14.7.5.2 /ClassMap, read ONCE for the whole tree rather than per element: it lives on
            // the root and every element's /C names into it.
            var classMap = PdfStructureAttributes.BuildClassMap(structRoot, doc.ResolveObject);
            var attributes = new SharedAttributes(doc, classMap);

            Pending root = null;
            var walk = new StructureTreeWalk(doc)
            {
                // The reader refuses the element *at* PdfLimits.MaxStructTreeDepth, as it always has;
                // the walk's cap is the deepest level it enters. The node and edge ceilings are the
                // walk's defaults.
                MaxDepth = PdfLimits.MaxStructTreeDepth - 1,
                Report = (walkEvent, at, objectNumber, ex) => Refuse(walkEvent, objectNumber, ex),
                Enter = node =>
                {
                    var pending = new Pending();
                    node.Tag = pending;
                    if (node.Parent == null) root = pending;
                    else ((Pending)node.Parent.Tag).Children.Add(pending);
                },
                Kid = kid => AddLeaf(kid, doc),
                Leave = node => ((Pending)node.Tag).Result = Build(node, doc, attributes),
            };
            walk.Run(structRoot);

            return root?.Result ?? EmptyRoot();
        }

        private static PdfStructElement EmptyRoot()
            => new PdfStructElement { ElementType = "StructTreeRoot", PageIndex = -1, Children = Array.Empty<PdfStructElement>() };

        /// <summary>
        /// An element under construction: its children in <c>/K</c> order — a leaf is a finished
        /// <see cref="PdfStructElement"/>, a child element another <see cref="Pending"/>, finished by
        /// the time its parent is.
        /// </summary>
        private sealed class Pending
        {
            internal readonly List<object> Children = new List<object>();
            internal PdfStructElement Result;
        }

        /// <summary>
        /// Refuses the tree at a ceiling, and at an element the parser could not read. It throws
        /// rather than returning, and that is the whole point: a silent <c>return</c> would produce a
        /// tree truncated at the limit, or missing the element's subtree, for any such input. One
        /// guard, for every ceiling.
        /// </summary>
        private static void Refuse(StructureTreeWalkEvent walkEvent, int objectNumber, Exception ex)
        {
            switch (walkEvent)
            {
                case StructureTreeWalkEvent.UnresolvableKid:
                    // A reference to no object at all contributes nothing and is not reported; this
                    // is an object that exists and cannot be parsed, whose subtree the tree would
                    // silently lack.
                    throw new Core.PdfException(
                        $"Structure element {objectNumber} could not be read ({ex?.Message}), so the " +
                        "structure tree cannot be read whole.");
                case StructureTreeWalkEvent.DepthCapReached:
                    throw new Core.PdfException(
                        $"Structure tree depth exceeds {PdfLimits.MaxStructTreeDepth}; " +
                        "malformed or adversarial document.");
                case StructureTreeWalkEvent.EdgeCapReached:
                    // Many elements may point /K at one shared array, walked again for each of them:
                    // n elements over an n-entry array is n² slots from O(n) bytes of file, and here
                    // each slot also allocates a leaf.
                    throw new Core.PdfException(
                        $"Structure tree /K arrays exceed {PdfLimits.MaxStructTreeEdges} slots; " +
                        "malformed or adversarial document.");
                case StructureTreeWalkEvent.NodeCapReached:
                    throw new Core.PdfException(
                        $"Structure tree exceeds {PdfLimits.MaxStructTreeNodes} elements; " +
                        "malformed or adversarial document.");
                // A revisit is a shared subtree or a cycle, entered once; an indirect /K that cannot
                // be resolved is read as one reference kid, which is then an unresolvable kid or an
                // element. Neither leaves the tree read incomplete.
            }
        }

        /// <summary>
        /// A <c>/K</c> slot: a marked-content identifier, an <c>/MCR</c> or an <c>/OBJR</c> becomes a
        /// leaf. A child element adds itself when the walk enters it, and one already in the tree (a
        /// cycle, a shared subtree) is not added twice.
        /// </summary>
        private static void AddLeaf(StructureTreeKid kid, PdfDocument doc)
        {
            if (kid.Resolved == null) return;
            var children = ((Pending)kid.Parent.Tag).Children;

            if (kid.Resolved is PdfNumber)
            {
                children.Add(Leaf("MCID"));
                return;
            }

            if (!(kid.Resolved is PdfDictionary dict)) return;

            // Not a kid of any kind — no /S, no /MCID, no /Obj, no /Type that names one — is
            // skipped rather than reported: a deduced-type fallback would call it "OBJR" (anything
            // that is not an MCR), inventing an object reference the file never wrote.
            if (kid.Kind == StructKidKind.Element || kid.Kind == StructKidKind.Unknown) return;

            // Leaf node. The reported type is what the file says when it says anything, and the kind
            // we deduced when it does not — Table 324 makes /Type Optional on an /MCR, and /Type is
            // resolved because it may be indirect.
            string typeVal = (doc.ResolveObject(dict[PdfName.Type]) as PdfName)?.Value;
            children.Add(Leaf(typeVal ?? (kid.Kind == StructKidKind.MarkedContentReference ? "MCR" : "OBJR")));
        }

        private static PdfStructElement Leaf(string type)
            => new PdfStructElement { ElementType = type, PageIndex = -1, Children = Array.Empty<PdfStructElement>() };

        /// <summary>
        /// One reading of the attributes for every element that names the same attribute objects.
        /// </summary>
        /// <remarks>
        /// Each element copied every entry of the objects its <c>/A</c> and <c>/C</c> named into a
        /// map of its own, which it kept: half a million elements naming one shared attribute object
        /// of a few thousand keys held billions of entries, from a few hundred kilobytes of file.
        /// Elements whose <c>/A</c> and <c>/C</c> are the same objects share the map at once; elements
        /// whose entries differ but name the same attribute objects — a direct <c>/A [12 0 R]</c> each —
        /// share it once the objects are known. A caller reading one element's map reads the same
        /// answer as before.
        /// </remarks>
        private sealed class SharedAttributes
        {
            private readonly PdfDocument _doc;
            private readonly Dictionary<string, PdfObject> _classMap;
            private readonly Dictionary<(Identity A, Identity C), Dictionary<string, IReadOnlyDictionary<string, PdfObject>>> _byEntries =
                new Dictionary<(Identity, Identity), Dictionary<string, IReadOnlyDictionary<string, PdfObject>>>();
            private readonly Dictionary<Sources, Dictionary<string, IReadOnlyDictionary<string, PdfObject>>> _bySources =
                new Dictionary<Sources, Dictionary<string, IReadOnlyDictionary<string, PdfObject>>>();

            internal SharedAttributes(PdfDocument doc, Dictionary<string, PdfObject> classMap)
            {
                _doc = doc;
                _classMap = classMap;
            }

            internal Dictionary<string, IReadOnlyDictionary<string, PdfObject>> Of(PdfDictionary element)
            {
                // A class named by a name is the name's text: two elements' /C /Bordered are two
                // name objects naming one class.
                PdfObject c = _doc.ResolveObject(element[PdfName.C]);
                var entries = (new Identity(_doc.ResolveObject(element[PdfName.A])),
                               new Identity(c is PdfName name ? name.Value : (object)c));
                if (_byEntries.TryGetValue(entries, out var known)) return known;

                var sources = new Sources(PdfStructureAttributes.SourcesOf(element, _classMap, _doc.ResolveObject));
                if (!_bySources.TryGetValue(sources, out known))
                {
                    known = new Dictionary<string, IReadOnlyDictionary<string, PdfObject>>(StringComparer.Ordinal);
                    foreach (var owned in PdfStructureAttributes.ReadAll(sources.Objects, _doc.ResolveObject))
                        known[owned.Key] = owned.Value;
                    _bySources[sources] = known;
                }
                _byEntries[entries] = known;
                return known;
            }

            // An object by identity, a string by its text.
            private readonly struct Identity : IEquatable<Identity>
            {
                private readonly object _value;
                internal Identity(object value) => _value = value;
                public bool Equals(Identity other) => _value is string s
                    ? string.Equals(s, other._value as string, StringComparison.Ordinal)
                    : PdfReferenceEquality.Instance.Equals(_value, other._value);
                public override bool Equals(object obj) => obj is Identity other && Equals(other);
                public override int GetHashCode() => _value is string s
                    ? StringComparer.Ordinal.GetHashCode(s)
                    : _value == null ? 0 : PdfReferenceEquality.Instance.GetHashCode(_value);
            }

            // The attribute objects an element names, in order, compared by identity.
            private readonly struct Sources : IEquatable<Sources>
            {
                internal readonly List<PdfDictionary> Objects;
                private readonly int _hash;
                internal Sources(List<PdfDictionary> objects)
                {
                    Objects = objects;
                    int hash = objects.Count;
                    foreach (var o in objects) hash = unchecked(hash * 31 + PdfReferenceEquality.Instance.GetHashCode(o));
                    _hash = hash;
                }
                public bool Equals(Sources other)
                {
                    if (Objects.Count != other.Objects.Count) return false;
                    for (int i = 0; i < Objects.Count; i++)
                        if (!ReferenceEquals(Objects[i], other.Objects[i])) return false;
                    return true;
                }
                public override bool Equals(object obj) => obj is Sources other && Equals(other);
                public override int GetHashCode() => _hash;
            }
        }

        /// <summary>
        /// Builds an element once the walk has left it, so its children are complete. Every read
        /// resolves indirect references: §7.3.10 lets any value except a stream be indirect, and
        /// object streams make <c>/S 40 0 R</c> common in linearized output.
        /// </summary>
        private static PdfStructElement Build(StructureTreeNode node, PdfDocument doc,
            SharedAttributes sharedAttributes)
        {
            PdfDictionary dict = node.Dictionary;

            string elementType = node.Role ?? (node.Parent == null ? "StructTreeRoot" : "StructElem");
            string altText = (doc.ResolveObject(dict[PdfName.Alt]) as PdfString)?.GetText();
            string actualText = (doc.ResolveObject(dict[PdfName.ActualText]) as PdfString)?.GetText();

            string lang = null;
            var langObj = doc.ResolveObject(dict[PdfName.Lang]);
            if (langObj is PdfString langStr) lang = langStr.GetText();
            else if (langObj is PdfName langName) lang = langName.Value;

            // PDF 2.0 /NS — structure-element namespace. The /NS value is a reference
            // to a dict with { /Type /Namespace /NS <uri> /RoleMapNS ... }.
            string nsUri = null;
            var nsObj = doc.ResolveObject(dict[PdfName.NS]);
            if (nsObj is PdfDictionary nsDict)
            {
                var uri = nsDict[PdfName.NS];
                if (uri is PdfString nsStr) nsUri = nsStr.GetText();
                else if (uri is PdfName nsName) nsUri = nsName.Value;
            }

            // §14.7.5 attributes, from both the inline /A and the /C + /ClassMap forms. The engine
            // is Core's, shared with the deep accessibility checker's UA12 table rules — a second
            // reading of "what does this element declare" is the duplication this repository keeps
            // re-finding.
            var attributes = sharedAttributes.Of(dict);

            var pending = (Pending)node.Tag;
            var children = new List<PdfStructElement>(pending.Children.Count);
            foreach (object child in pending.Children)
            {
                PdfStructElement element = child as PdfStructElement ?? (child as Pending)?.Result;
                if (element != null) children.Add(element);
            }

            return new PdfStructElement
            {
                ElementType = elementType,
                ActualText  = actualText,
                AltText     = altText,
                Lang        = lang,
                // The element's own /Pg (the page; /P is the parent element and is not read). An
                // element with none reports -1 here, as this reader always has; the walk knows the
                // inherited page for consumers that want it.
                PageIndex   = node.OwnPageIndex,
                Children    = children.AsReadOnly(),
                Namespace   = nsUri,
                Attributes  = attributes
            };
        }
    }
}
