// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// §12.7.3.1's one question: is a <c>/Kids</c> entry a child <b>field</b>, or a widget
    /// annotation that gives its parent an appearance on the page?
    /// </summary>
    /// <remarks>
    /// <para>Everything else about terminality follows from it. A field whose kids are fields is
    /// non-terminal — the user tabs to the children, not to it — so this predicate decides where a
    /// value is written, where a tooltip is required, and which dictionaries are the widgets to
    /// draw an appearance into.</para>
    ///
    /// <para><b>Why it is public, and in Core.</b> Both callers are in different assemblies:
    /// <c>Core.Forms.PdfFormField.IsTerminal</c> and
    /// <c>Plugins.Conformance.AcroFormFieldWalk</c>, the latter shared in turn by the PDF/UA
    /// checker and the fixer. Core's <c>InternalsVisibleTo</c> deliberately names no plugin, so
    /// <c>internal</c> would not reach the second caller. Same shape as
    /// <see cref="Tetitulz.Pdf.Core.Document.PdfNameTree"/>, which is public in Core for the same
    /// reason.</para>
    ///
    /// <para><b>Each caller was right about a shape the other got wrong</b>, which is why this is a
    /// merge rather than one side adopting the other. Core asked for a non-empty <c>/T</c>; the walk
    /// asked for <c>/FT</c> <i>or</i> <c>/T</c> <i>or</i> <c>/Kids</c>. Three inputs separated
    /// them:</para>
    /// <list type="bullet">
    ///   <item><description><b><c>/FT</c> and no <c>/T</c></b> — a widget with its field type merged
    ///   in, the shape a producer emits when it merges a single widget into its field and then wraps
    ///   the result in a group. <b>Core was right</b>: it is a widget. The walk called it a field, so
    ///   the group escaped UA08 and the fixer wrote the <c>/TU</c> onto the <i>widget</i> — and
    ///   <c>/TU</c> is not inheritable, so that tooltip names nothing a user ever reaches. Core reads
    ///   such a kid's <c>/FT</c> through <see cref="WidgetFieldType"/>, the downward step
    ///   <c>PdfFormField</c> and the signer's adoption walk share, which is what makes keeping the
    ///   field terminal the useful answer rather than merely the permissive one. (This once named a
    ///   <c>WidgetFieldType()</c> that existed nowhere in the tree, while the step lived privately
    ///   in <c>PdfFormField.ResolveFieldType</c>; the signer, with no access to it, read
    ///   <c>/FT</c> from a node and its ancestors only.)</description></item>
    ///   <item><description><b><c>/T ()</c></b> — present but empty, what a generator that writes
    ///   every key unconditionally emits. <b>Core was right</b>: an empty partial name contributes
    ///   nothing to a fully qualified name (§12.7.3.2), so the kid is not addressable and is not a
    ///   field.</description></item>
    ///   <item><description><b><c>/Kids</c> and no name or type</b> — <b>the walk was right</b>. A
    ///   widget annotation has no children at all, so anything carrying <c>/Kids</c> is a field node
    ///   whatever else it omits. Core called it a widget, and
    ///   <c>PdfFormField.WidgetDictionaries</c> then handed it back as a dictionary to draw an
    ///   appearance into, skipping the real widgets beneath it.</description></item>
    /// </list>
    ///
    /// <para><b><c>/FT</c> is deliberately not a clause.</b> Table 220 marks the field type
    /// <i>inheritable</i>, so its presence says nothing about whether this node is a field: a group
    /// legitimately carries the <c>/FT</c> for its children, and a merged widget legitimately
    /// carries its field's. It is the one key of the three that cannot discriminate.</para>
    /// </remarks>
    public static class AcroFieldClassification
    {
        /// <summary>
        /// Whether <paramref name="dict"/> is a child field rather than a widget annotation.
        /// </summary>
        /// <param name="dict">The <c>/Kids</c> entry, already resolved to a dictionary.</param>
        /// <param name="document">
        /// Resolves indirect values; may be null, in which case only direct values are read. A null
        /// document is a shape callers can build — <see cref="PdfFormField"/>'s constructor is
        /// public and takes it as a plain parameter.
        /// </param>
        public static bool IsFieldNode(PdfDictionary dict, PdfDocument document)
            => IsFieldNode(dict, obj => document?.ResolveObject(obj) ?? obj);

        /// <summary>
        /// The same question for a caller that resolves through something other than a
        /// <see cref="PdfDocument"/> — the Conformance walk carries its own budgeted resolver.
        /// </summary>
        /// <param name="resolve">
        /// May be null, and may return its argument unchanged for a graph of direct objects.
        /// </param>
        /// <param name="dict">The candidate node. A <c>null</c> is not a field node.</param>
        public static bool IsFieldNode(PdfDictionary dict, Func<PdfObject, PdfObject> resolve)
        {
            if (dict == null) return false;

            // Tested on the raw value rather than the resolved one: /Kids written as a reference is
            // still /Kids, and a caller with no resolver must not read that as "no children".
            if (dict[PdfName.Kids] != null) return true;

            var t = Resolve(resolve, dict[PdfName.T]) as PdfString;
            return t != null && t.GetText().Length > 0;
        }

        /// <summary>
        /// The <c>/FT</c> a node's <b>bare widget</b> kids declare, or <c>null</c> when none does or
        /// they disagree.
        /// </summary>
        /// <remarks>
        /// <para>The repair for a producer that put a terminal field's Required <c>/FT</c> on its
        /// widget, read by <c>PdfFormField</c> when the node's chain declares no type. It is not
        /// inheritance, which §12.7.3.1 flows ancestor to descendant only: a kid that is itself a
        /// field (<see cref="IsFieldNode(PdfDictionary, Func{PdfObject, PdfObject})"/>) inherits from
        /// this node, so reading its <c>/FT</c> would give a grouping node its children's type.</para>
        /// <para><b>Here so the signer asks the same question.</b> Its adoption walk reads a tree
        /// through a parser rather than a document, and took <c>/FT</c> from a node and its ancestors
        /// only, so a field whose type sat on its nameless widget was a signature field to Core's
        /// model and not to the signer, which then wrote a second field beside it.</para>
        /// </remarks>
        /// <param name="dict">The node whose kids are read.</param>
        /// <param name="resolve">May be null for a graph of direct objects.</param>
        internal static PdfName WidgetFieldType(PdfDictionary dict, Func<PdfObject, PdfObject> resolve)
        {
            if (!(Resolve(resolve, dict?[PdfName.Kids]) is PdfArray kids)) return null;

            PdfName found = null;
            for (int i = 0; i < kids.Count; i++)
            {
                if (!(Resolve(resolve, kids[i]) is PdfDictionary kid)) continue;
                if (IsFieldNode(kid, resolve)) continue;

                if (!(Resolve(resolve, kid[PdfName.FT]) is PdfName ft)) continue;
                if (found == null) { found = ft; continue; }
                if (found.Value != ft.Value) return null;   // ambiguous: do not guess
            }
            return found;
        }

        private static PdfObject Resolve(Func<PdfObject, PdfObject> resolve, PdfObject obj)
            => obj == null ? null : (resolve == null ? obj : resolve(obj) ?? obj);
    }
}
