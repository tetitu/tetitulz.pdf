// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// One step from a field dictionary to the next node up whichever ancestor chain the caller is
    /// walking.
    /// </summary>
    /// <param name="examined">The node just examined.</param>
    /// <returns>The next ancestor, or <see langword="null"/> when the chain has ended.</returns>
    /// <remarks>
    /// <para><b>Stateful by contract, and one fresh instance per
    /// <see cref="AcroFieldInheritance.Find(PdfDictionary, PdfName, AncestorStep, Func{PdfObject, PdfObject}, Func{PdfObject, bool}, int, out bool)"/>
    /// call.</b> It is not required to be a function of <paramref name="examined"/>, and for the
    /// <c>/Kids</c>-descent chain it cannot be: one dictionary legitimately occupies several depths
    /// of a chain built by descending a <c>/Kids</c> cycle, so the same argument has different
    /// correct answers at different points of one walk. A caller that implements this as a lookup
    /// keyed on its argument will loop.</para>
    ///
    /// <para>For a <c>/Parent</c>-key chain the step is stateless in practice
    /// (<c>d =&gt; resolve(d[PdfName.Parent]) as PdfDictionary</c>) and satisfies the contract
    /// trivially.</para>
    /// </remarks>
    public delegate PdfDictionary AncestorStep(PdfDictionary examined);

    /// <summary>
    /// Resolves an <b>inheritable</b> AcroForm field attribute up an ancestor chain — ISO 32000-1
    /// Table 220 (<c>/FT</c>, <c>/Ff</c>, <c>/V</c>, <c>/DV</c>) and Table 222 §12.7.3.3
    /// (<c>/DA</c>, <c>/Q</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Five hand-written copies of this loop existed</b>, across three assemblies, and they
    /// did not agree. Three were in <see cref="PdfFormField"/> alone with three different rules for
    /// what ends a climb; one was in the Conformance walk; one was in the annotation renderer, which
    /// had three copies of its own. Every defect this family has produced was one copy lagging
    /// another: an unusable <c>/DA</c> hiding a parent's real one, a cap degrading into a
    /// confidently wrong type, a cap of 8 that no conformant file could rely on.</para>
    ///
    /// <para><b>What it does NOT own, deliberately.</b> Which chain to walk is the caller's — the
    /// <c>AncestorStep</c> is a parameter precisely because Core walks the arrival chain while the
    /// Conformance walk and the renderer walk the <c>/Parent</c> key. <b>That split is decided, not
    /// pending: it stays</b> (owner decision). ISO 32000-2 §12.7.4.1 settles the
    /// semantics — whole chain, nearest ancestor, <i>"shall not limit the range of inheritance"</i> —
    /// and this method implements them for every caller. The two chains are two directions of one
    /// Table 220 edge and only a <i>malformed</i> file separates them, where the spec is silent; a
    /// checker and a structural model are different jobs, reconciling them changes verdicts, and the
    /// corpus measured zero nodes reached by a <c>/Kids</c> edge. <c>FieldInheritanceChainSplitTests</c>
    /// pins the split, so unifying it must fail a test first. Nor does it own
    /// <see cref="PdfFormField"/>'s downward widget-kid repair, its ambiguity rule, the renderer's
    /// <c>/AcroForm</c> fallback, or the renderer's <c>PdfString</c>-or-<c>PdfName</c> value
    /// switch. Those are policy; this is the climb.</para>
    ///
    /// <para><b>Public because Core's <c>InternalsVisibleTo</c> names no plugin</b> and
    /// <c>Plugins.Conformance.AcroFormFieldWalk</c> is a caller — the identical reason
    /// <see cref="AcroFieldClassification"/> states for itself, in this folder and this
    /// namespace.</para>
    ///
    /// <para><b>A leaf by construction: no static fields, so no type initializer.</b> That is the
    /// property that makes a type safe to share between assemblies here; its absence once crashed
    /// the Base test host mid-run and silently dropped ~190 tests.</para>
    /// </remarks>
    public static class AcroFieldInheritance
    {
        /// <summary>
        /// The raw stored value of <paramref name="key"/> on the nearest node — starting at
        /// <paramref name="start"/> — whose <b>resolved</b> value <paramref name="accept"/> takes.
        /// </summary>
        /// <param name="start">The field or widget dictionary to begin at. Examined first.</param>
        /// <param name="key">The inheritable entry to look for.</param>
        /// <param name="ancestors">
        /// The chain to climb. See <see cref="AncestorStep"/> — stateful by contract, one fresh
        /// instance per call. <see langword="null"/> examines <paramref name="start"/> only.
        /// </param>
        /// <param name="resolve">
        /// Follows an indirect reference (§7.3.10 permits one anywhere). <see langword="null"/>
        /// reads raw, which is what a caller with no document must do.
        /// </param>
        /// <param name="accept">
        /// What counts as an answer, applied to the RESOLVED value. A value the caller cannot use
        /// must not end the climb, or the ancestor holding the usable one is never reached — the
        /// defect that made an unreadable <c>/DA</c> on a child hide its parent's real one.
        /// <see langword="null"/> accepts any non-null, non-<see cref="PdfNull"/> value.
        /// </param>
        /// <param name="maxNodes">
        /// Ceiling on dictionaries examined, <paramref name="start"/> included. The only guard
        /// against a <c>/Parent</c> ring, so it may be raised but never removed.
        /// </param>
        /// <param name="capExhausted">
        /// <see langword="true"/> when the ceiling stopped the climb with ancestors still unread —
        /// as opposed to the chain genuinely ending. <b>The caller must not treat those the same.</b>
        /// A cap that degrades into a fallback answers confidently about a file it never finished
        /// reading; <see cref="PdfFormField"/>'s downward repair used to, and reported a signature
        /// field as text.
        /// </param>
        /// <returns>
        /// The winning node's <b>raw</b> stored value — unresolved, because callers differ on
        /// whether they want the reference or the target, and resolving here would only decide that
        /// for them. What the climb uses resolution for is choosing the node.
        /// </returns>
        public static PdfObject Find(
            PdfDictionary start,
            PdfName key,
            AncestorStep ancestors,
            Func<PdfObject, PdfObject> resolve,
            Func<PdfObject, bool> accept,
            int maxNodes,
            out bool capExhausted)
        {
            // Effectively unbudgeted: maxNodes is the only ceiling for a caller with no shared one.
            int unbudgeted = int.MaxValue;
            return Find(start, key, ancestors, resolve, accept, maxNodes, ref unbudgeted, out capExhausted);
        }

        /// <summary>
        /// As the other overload, additionally charging a <b>shared</b> traversal budget that spans
        /// more than this one climb.
        /// </summary>
        /// <param name="budget">
        /// Decremented once per dictionary examined, <b>before</b> the key is read. Exhaustion ends
        /// the climb and returns <see langword="null"/> — silently, and that silence is deliberate:
        /// the Conformance walk reports budget exhaustion for the traversal as a whole and must not
        /// have a per-climb flag raised underneath it.
        /// </param>
        /// <remarks>
        /// Charged once per NODE, never per resolve. Per-resolve doubles the count a caller can
        /// observe — the Conformance walk's ring case lands on exactly 52 lookups, and per-resolve
        /// charging makes it 103.
        /// </remarks>
        /// <param name="start">The field dictionary to begin the climb at.</param>
        /// <param name="key">The inheritable key being resolved (Table 220 — <c>/FT</c>, <c>/Ff</c>,
        /// <c>/V</c>, <c>/DV</c>).</param>
        /// <param name="ancestors">
        /// Supplies the next dictionary up the chain, so the caller decides what "parent" means —
        /// <c>/Parent</c> for a field, the page tree's <c>/Parent</c> for a page attribute.
        /// </param>
        /// <param name="resolve">
        /// Follows an indirect reference. Supplied by the caller because Core must not depend on a
        /// particular document type, and a non-resolving read makes an inherited value look
        /// <b>absent</b> rather than indirect.
        /// </param>
        /// <param name="accept">
        /// Decides whether a value found at this level ends the climb. A key present but of the
        /// wrong type keeps climbing rather than yielding a value the caller cannot use.
        /// </param>
        /// <param name="maxNodes">
        /// Hard ceiling on dictionaries examined in this climb, so a <c>/Parent</c> cycle
        /// terminates even with no shared budget.
        /// </param>
        /// <param name="capExhausted">
        /// True when the climb stopped because <paramref name="maxNodes"/> ran out rather than
        /// because the chain ended — which is what tells "no such inherited value" apart from
        /// "gave up looking".
        /// </param>
        public static PdfObject Find(
            PdfDictionary start,
            PdfName key,
            AncestorStep ancestors,
            Func<PdfObject, PdfObject> resolve,
            Func<PdfObject, bool> accept,
            int maxNodes,
            ref int budget,
            out bool capExhausted)
        {
            capExhausted = false;
            if (start == null || key == null) return null;

            var node = start;
            for (int examined = 0; node != null && examined < maxNodes; examined++)
            {
                // Tested before the decrement, matching the walk this was extracted from exactly:
                // its /Parent ring lands on 52 lookups and a test asserts that literal.
                if (budget <= 0) return null;
                budget--;

                var raw = node[key];
                var value = resolve == null ? raw : resolve(raw) ?? raw;
                if (value != null && !(value is PdfNull) && (accept == null || accept(value)))
                    return raw;

                node = ancestors?.Invoke(node);
            }

            // Told apart on purpose: `node == null` is the chain ending, which is a fact about the
            // file. A non-null node means the ceiling stopped us with ancestors unread, which is a
            // fact about this walk — and the two must not lead to the same fallback.
            capExhausted = node != null;
            return null;
        }
    }
}
