// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// One node of a document's logical structure — a paragraph, a heading, a table cell.
    ///
    /// <para>An element owns <b>marked-content sequences</b> rather than text: each entry in
    /// <see cref="Content"/> pairs a page with an MCID on that page. Keeping the page alongside
    /// the id is what makes a paragraph that spans a page break expressible — it is one element
    /// with MCIDs on two pages, and the writer emits a bare integer for the element's own
    /// <c>/Pg</c> and an <c>/MCR</c> dictionary for the other. An element that stored only ids
    /// would silently address the wrong page's content.</para>
    /// </summary>
    public sealed class PdfStructureElement
    {
        private readonly List<PdfStructureElement> _children = new();
        private readonly List<(PdfContentStreamBuilder Page, int Mcid)> _content = new();

        internal PdfStructureElement(string role) => Role = role ?? PdfStructureTypes.P;

        /// <summary>The <c>/S</c> structure type — see <see cref="PdfStructureTypes"/>.</summary>
        public string Role { get; internal set; }

        /// <summary>Alternative description (<c>/Alt</c>) — required on a <c>Figure</c>.</summary>
        public string Alt { get; set; }

        /// <summary>Replacement text (<c>/ActualText</c>) for content whose characters differ from its meaning.</summary>
        public string ActualText { get; set; }

        /// <summary>Natural language of this subtree (<c>/Lang</c>), when it differs from the document's.</summary>
        public string Lang { get; set; }

        /// <summary>
        /// Attribute dictionary (<c>/A</c>) — e.g. <c>&lt;&lt;/O /Table /Scope /Column&gt;&gt;</c>
        /// on a header cell, or <c>/ColSpan</c> / <c>/RowSpan</c>.
        /// </summary>
        public PdfDictionary Attributes { get; set; }

        /// <summary>
        /// Never pruned even with no content — a table cell must exist for the table to stay
        /// rectangular, and a missing one makes every later row look misaligned to a checker.
        /// </summary>
        internal bool ExemptFromPruning { get; set; }

        public IReadOnlyList<PdfStructureElement> Children => _children;

        internal IReadOnlyList<(PdfContentStreamBuilder Page, int Mcid)> Content => _content;

        /// <summary>Adds a child element and returns it.</summary>
        public PdfStructureElement AddChild(string role)
        {
            var child = new PdfStructureElement(role);
            _children.Add(child);
            return child;
        }

        internal void AddContent(PdfContentStreamBuilder page, int mcid) => _content.Add((page, mcid));

        /// <summary>
        /// True when this element, or anything beneath it, actually reaches page content.
        ///
        /// <para>The transitive form matters: an element whose every descendant is also empty is
        /// a hollow branch, and a structure tree full of those is the "claims to be tagged, tags
        /// nothing" shape that a PDF/UA checker reports as an error. Testing only this element
        /// would keep the branch.</para>
        /// </summary>
        internal bool ReachesContent()
        {
            if (_content.Count > 0) return true;
            foreach (var c in _children) if (c.ReachesContent()) return true;
            return false;
        }

        /// <summary>Removes hollow children, depth-first. Cell-like elements are kept.</summary>
        internal void Prune()
        {
            for (int i = _children.Count - 1; i >= 0; i--)
            {
                _children[i].Prune();
                if (!_children[i].ExemptFromPruning && !_children[i].ReachesContent())
                    _children.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// A document's logical structure, built up while its content streams are written and
    /// serialised into <c>/StructTreeRoot</c> at the end.
    ///
    /// <para>Untouched, it writes nothing at all: a <see cref="PdfDocumentBuilder"/> whose caller
    /// never asks for structure produces exactly the bytes it always did.</para>
    /// </summary>
    public sealed class PdfStructureTree
    {
        /// <summary>The tree's top element — a <c>/Document</c>.</summary>
        public PdfStructureElement Root { get; } = new PdfStructureElement(PdfStructureTypes.Document);

        /// <summary>
        /// The document's natural language (catalog <c>/Lang</c>), e.g. <c>"he-IL"</c>.
        ///
        /// <para>Left null when nothing declared one. There is no honest default: writing a wrong
        /// language makes a screen reader pronounce Hebrew with English phonemes while the
        /// conformance check reports clean, which is worse than writing none.</para>
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Whether anything in this tree reaches page content — the only condition under which a
        /// document may claim to be tagged.
        /// </summary>
        public bool HasContent => Root.ReachesContent();

        /// <summary>Drops hollow branches. Called once before serialisation.</summary>
        public void Prune() => Root.Prune();
    }
}
