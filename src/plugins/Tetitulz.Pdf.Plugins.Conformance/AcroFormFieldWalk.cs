// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

[assembly: InternalsVisibleTo("Tetitulz.Pdf.Plugins.Conformance.Tests")]
[assembly: InternalsVisibleTo("Tetitulz.Pdf.Accessibility")]

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Resolves one PDF object, following an indirect reference if that is what it is.
    /// <para>
    /// The walk deliberately does not own resolution: each caller passes its own resolver so that
    /// a failure keeps being recorded in that caller's own <c>Last*Error</c> diagnostic slot
    /// (<c>PdfUaChecker.LastValidationError</c> / <c>PdfAccessibilityFixer.LastFixError</c>).
    /// A resolver must return <see langword="null"/> — never throw — for anything it cannot resolve.
    /// </para>
    /// </summary>
    internal delegate PdfObject AcroFormResolver(PdfObject obj);

    /// <summary>
    /// Determines the indirect object identity of a field dictionary. Shaped to match
    /// <c>PdfAccessibilityFixer.TryGetObjectId</c> so it can be passed as a lambda over it.
    /// </summary>
    internal delegate bool AcroFormObjectIdResolver(PdfObject obj, out int objectNumber, out int generation);

    /// <summary>
    /// One node reached by <see cref="AcroFormFieldWalk"/>, together with every fact its two
    /// consumers need in order to reach the <b>same</b> conclusion about it.
    /// </summary>
    internal sealed class AcroFormField
    {
        private readonly AcroFormObjectIdResolver _objectId;

        internal AcroFormField(
            PdfDictionary dictionary,
            int depth,
            bool isTerminal,
            PdfName fieldType,
            bool? hasAccessibleName,
            PdfString partialName,
            AcroFormObjectIdResolver objectId)
        {
            Dictionary        = dictionary;
            Depth             = depth;
            IsTerminal        = isTerminal;
            FieldType         = fieldType;
            HasAccessibleName = hasAccessibleName;
            PartialName       = partialName;
            _objectId         = objectId;
        }

        /// <summary>The field dictionary itself.</summary>
        internal PdfDictionary Dictionary { get; }

        /// <summary>Nesting depth below the AcroForm <c>/Fields</c> array (0 = a top-level entry).</summary>
        internal int Depth { get; }

        /// <summary>
        /// <see langword="true"/> when this node has no <c>/Kids</c> entry that is itself a field —
        /// ISO 32000-1 §12.7.3.1's definition of a <i>terminal</i> field, and the only kind of node a
        /// user tabs to. Note this is <b>not</b> "has an <c>/FT</c>": a group node may carry the
        /// inheritable <c>/FT</c> for its children and still be non-terminal.
        /// </summary>
        internal bool IsTerminal { get; }

        /// <summary>
        /// The <c>/FT</c> field type resolved through the inheritable <c>/Parent</c> chain
        /// (ISO 32000-1 Table 220), or <see langword="null"/> when none is reachable.
        /// <para>
        /// <b>Resolved for terminal nodes only</b>, because that is the only place either consumer
        /// looks at it, and resolving it costs traversal budget. On a non-terminal node this is
        /// always <see langword="null"/> and means "not asked", not "has no type".
        /// </para>
        /// </summary>
        internal PdfName FieldType { get; }

        /// <summary>
        /// Whether <c>/TU</c> holds a non-blank string. <see langword="null"/> when the question was
        /// not asked — i.e. for any node that is not a terminal field with a resolvable <c>/FT</c>,
        /// which is exactly the set neither consumer would have probed.
        /// </summary>
        internal bool? HasAccessibleName { get; }

        /// <summary>
        /// The <c>/T</c> partial name, resolved only when <see cref="NeedsAccessibleName"/> is
        /// <see langword="true"/> (the only case any consumer reads it).
        /// </summary>
        internal PdfString PartialName { get; }

        /// <summary>
        /// <b>The single fact this whole type exists to produce.</b> A terminal field, with a
        /// resolvable <c>/FT</c>, whose <c>/TU</c> is missing or blank: what the checker reports as
        /// a PDF/UA-1 UA08 violation and what the fixer writes a tooltip onto. Both consumers read
        /// this property, so the two cannot disagree.
        /// </summary>
        internal bool NeedsAccessibleName
            => IsTerminal && FieldType != null && HasAccessibleName == false;

        /// <summary>
        /// The indirect object identity of <see cref="Dictionary"/>, needed to revise it. Deferred
        /// rather than computed during the walk: a consumer's resolver may have to build a
        /// document-wide object map, which must not be paid for by a consumer that never revises
        /// anything (the checker) nor by a document in which nothing needs fixing.
        /// </summary>
        internal bool TryGetObjectId(out int objectNumber, out int generation)
        {
            if (_objectId != null)
                return _objectId(Dictionary, out objectNumber, out generation);

            objectNumber = 0;
            generation   = 0;
            if (Dictionary == null || Dictionary.ObjectNumber <= 0) return false;

            objectNumber = Dictionary.ObjectNumber;
            generation   = Dictionary.GenerationNumber;
            return true;
        }
    }

    /// <summary>The outcome of one <see cref="AcroFormFieldWalk.Walk"/>.</summary>
    internal sealed class AcroFormFieldWalkResult
    {
        internal AcroFormFieldWalkResult(
            IReadOnlyList<AcroFormField> fields, bool budgetExhausted, int nodesExamined,
            bool depthExceeded)
        {
            Fields          = fields;
            BudgetExhausted = budgetExhausted;
            NodesExamined   = nodesExamined;
            DepthExceeded   = depthExceeded;
        }

        /// <summary>Every field node reached, in document (depth-first) order.</summary>
        internal IReadOnlyList<AcroFormField> Fields { get; }

        /// <summary>
        /// <see langword="true"/> when the node budget ran out and the walk is therefore partial.
        /// The walk does not report this itself — each consumer records it through its own
        /// diagnostic slot, in its own words.
        /// </summary>
        internal bool BudgetExhausted { get; }

        /// <summary>
        /// Edges examined, i.e. budget consumed. Exposed so an adversarial test can assert that a
        /// cyclic <c>/Parent</c> or <c>/Kids</c> graph terminated after a bounded amount of work
        /// rather than merely "eventually".
        /// </summary>
        internal int NodesExamined { get; }

        /// <summary>
        /// <see langword="true"/> when the <c>/Kids</c> descent hit
        /// <see cref="AcroFormFieldWalk.MaxFieldDepth"/> and stopped, so fields below that depth
        /// were never examined.
        /// </summary>
        /// <remarks>
        /// <b>Without it a truncated walk is silent.</b> A tree truncated at depth 50 would produce
        /// exactly the same result as one walked to the end, and a consumer's "no field needs a
        /// tooltip" would mean "none of the ones I reached", indistinguishably. That is the failure
        /// <c>PdfUaChecker</c>'s UA99 exists to prevent: a check that could not run must not report
        /// a clean verdict.
        ///
        /// <para>The sibling walk, <c>PageEdits.AcroFormPageFilter</c>, sets a
        /// <c>DepthExceeded</c> flag at both of its descent caps and reports it too. Like
        /// <see cref="BudgetExhausted"/>, the walk records the fact and each consumer words its own
        /// diagnostic.</para>
        /// </remarks>
        internal bool DepthExceeded { get; }
    }

    /// <summary>
    /// The <b>one</b> AcroForm field traversal in this assembly.
    ///
    /// <para>
    /// <b>Why this type exists.</b> <c>PdfUaChecker</c> decides that a document is broken (UA08:
    /// a form field has no <c>/TU</c> accessible name) and <c>PdfAccessibilityFixer</c> decides
    /// what to repair, and the two must classify every dictionary identically: a fixer testing for
    /// a <i>direct</i> <c>/FT</c> beside a checker resolving the inheritable one through
    /// <c>/Parent</c> would report fields the fixer never touches, and the fixer would report
    /// "nothing to do" on a document the checker calls non-conforming. A producer and its checker
    /// that share no code agree only by luck, and when they agree wrongly (a key both honour and
    /// no PDF reader does) every test passes and the output helps nobody. So the traversal lives
    /// here once, and both consume it.
    /// </para>
    ///
    /// <para>
    /// <b>What it owns.</b> Which dictionaries count as fields; which of them are <i>terminal</i>
    /// (ISO 32000-1 §12.7.3.1 — no <c>/Kids</c> that are themselves fields, which is not the same
    /// as "has an <c>/FT</c>"); resolution of the inheritable <c>/FT</c> through the <c>/Parent</c>
    /// chain (Table 220); the cycle guard, depth cap and node budget that keep a hostile
    /// <c>/Parent</c> or <c>/Kids</c> ring bounded; and the composite
    /// <see cref="AcroFormField.NeedsAccessibleName"/> answer both consumers act on.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not own.</b> Resolution diagnostics and the wording of budget warnings stay
    /// with each consumer, so each keeps populating its own <c>Last*Error</c> slot.
    /// </para>
    /// </summary>
    internal static class AcroFormFieldWalk
    {
        /// <summary>
        /// Maximum <c>/Kids</c> nesting. A ring costs a fixed number of lookups rather than
        /// spinning.
        /// </summary>
        /// <remarks>
        /// <b>Descent only.</b> The <c>/Parent</c> climb takes
        /// <see cref="PdfLimits.MaxFieldInheritanceDepth"/>. The distinction matters because THIS
        /// value is pinned to <c>PageEdits.AcroFormPageFilter.MaxFieldDepth</c>, which
        /// <c>PdfSplitter</c> reads by symbol — so moving it to raise the climb would make the
        /// checker accept field trees the splitter still refuses, one-sidedly and with nothing in
        /// PageEdits changing to say so. <c>CrossPluginConstantInvariantTests</c> holds that pin.
        /// </remarks>
        internal const int MaxFieldDepth = 50;

        /// <summary>
        /// Ceiling on edges examined. Spent per <i>iteration</i>, never per distinct node reached:
        /// a visited set stops re-expansion but does nothing about the cost of one expansion, and
        /// N field dictionaries that all name one shared N-entry <c>/Kids</c> array cost N²
        /// resolve-and-probe operations while a per-node counter moves only N times.
        /// </summary>
        internal const int MaxFieldNodes = 200_000;

        /// <summary>
        /// Walks the AcroForm field tree reachable from <paramref name="catalog"/>.
        /// </summary>
        /// <param name="catalog">The document catalog. No <c>/AcroForm</c>, or no <c>/Fields</c>
        /// array in it, yields an empty result — a document with no form is not a broken one.</param>
        /// <param name="resolve">Indirect-reference resolver; see <see cref="AcroFormResolver"/>.</param>
        /// <param name="objectId">Optional object-identity resolver used by
        /// <see cref="AcroFormField.TryGetObjectId"/>. When <see langword="null"/> the field
        /// dictionary's own object number is used.</param>
        /// <param name="nodeBudget">Edge ceiling; defaults to <see cref="MaxFieldNodes"/>. Lowering
        /// it is how a test exercises the budget guard without building a 200 000-node file.</param>
        internal static AcroFormFieldWalkResult Walk(
            PdfDictionary catalog,
            AcroFormResolver resolve,
            AcroFormObjectIdResolver objectId = null,
            int nodeBudget = MaxFieldNodes)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));

            var state = new State(resolve, objectId, nodeBudget);

            var acroForm = catalog == null ? null : resolve(catalog[PdfName.AcroForm]) as PdfDictionary;
            var fields   = acroForm == null ? null : resolve(acroForm[PdfName.Fields]) as PdfArray;

            if (fields != null)
                WalkFields(fields, state, 0);

            return new AcroFormFieldWalkResult(
                state.Fields, state.BudgetExhausted, nodeBudget - state.Budget, state.DepthExceeded);
        }

        /// <summary>
        /// Entries of the top-level <c>/Fields</c> array are fields by definition (ISO 32000-1
        /// Table 218); deeper entries are fields only when <see cref="IsFieldNode"/> says so.
        /// </summary>
        private static void WalkFields(PdfArray fields, State state, int depth)
        {
            if (depth > MaxFieldDepth) { state.DepthExceeded = true; return; }

            for (int i = 0; i < fields.Count; i++)
            {
                if (state.Budget <= 0) { state.BudgetExhausted = true; return; }
                state.Budget--;

                var field = state.Resolve(fields[i]) as PdfDictionary;
                if (field == null) continue;

                // Below the root, a kid that is a bare widget annotation is part of its parent
                // field's presentation, not a field in its own right.
                if (depth > 0 && !IsFieldNode(field, state)) continue;

                // Cycle guard: /Kids pointing back at an ancestor is a cheap loop bomb.
                // PdfDictionary does not override Equals, so this set is reference identity.
                if (!state.Visited.Add(field)) continue;

                var kids = state.Resolve(field[PdfName.Kids]) as PdfArray;

                // A field whose kids are fields is *non-terminal* (ISO 32000-1 §12.7.3.1): the user
                // tabs to its children, not to it, and /TU is not inheritable, so a tooltip written
                // here reaches nobody.
                //
                // "…that has not already been visited" is what makes this hold on a /Kids ring. In a
                // well-formed tree every kid is fresh, so the test reduces exactly to the ISO
                // definition; in a ring, a node whose every field kid was already accounted for
                // higher up reaches no further field and is the last tab stop on its branch.
                // Without that clause a ring of mutually-parented fields would contain no terminal
                // field at all, and the checker would fall silent on adversarial input — the one
                // outcome it may not produce.
                bool terminal = true;
                if (kids != null)
                {
                    for (int k = 0; k < kids.Count; k++)
                    {
                        if (state.Budget <= 0) { state.BudgetExhausted = true; return; }
                        state.Budget--;

                        var kid = state.Resolve(kids[k]) as PdfDictionary;
                        if (kid != null && IsFieldNode(kid, state) && !state.Visited.Contains(kid))
                        {
                            terminal = false;
                            break;
                        }
                    }
                }

                // Evaluated in this order, and only this far, so that a node nobody would have
                // probed is not probed: the type costs budget, and both are consumer-visible.
                PdfName fieldType         = terminal ? InheritedFieldType(field, state) : null;
                bool?   hasAccessibleName = fieldType == null
                    ? (bool?)null
                    : HasNonEmptyString(state, field[PdfName.TU]);
                PdfString partialName     = hasAccessibleName == false
                    ? state.Resolve(field[PdfName.T]) as PdfString
                    : null;

                state.Fields.Add(new AcroFormField(
                    field, depth, terminal, fieldType, hasAccessibleName, partialName, state.ObjectId));

                if (!terminal)
                    WalkFields(kids, state, depth + 1);
            }
        }

        /// <summary>
        /// Whether a <c>/Kids</c> entry is itself a <b>field</b> rather than a widget annotation.
        /// <para>
        /// ISO 32000-1 §12.7.3.1 splits a field's kids into two kinds: child <i>fields</i>, and the
        /// widget annotations that give the field its on-page appearance. A radio group's kids are
        /// bare widgets (<c>/Subtype /Widget</c>, <c>/Parent</c>, an appearance — no name, no type),
        /// which is why a widget kid must not be counted as a field in its own right: doing so would
        /// report, or write, the same tooltip once per appearance.
        /// </para>
        /// <para>
        /// <b>The rule itself lives in <c>Core.Forms.AcroFieldClassification</c></b>, because
        /// <c>Core.Forms.PdfFormField.IsTerminal</c> asks the identical question for form filling.
        /// A direct <c>/FT</c> does not make a field: Table 220 makes the field type
        /// <i>inheritable</i>, so a merged widget carrying its field's <c>/FT</c> read as a field in
        /// its own right would let the group escape UA08 and have the fixer write the <c>/TU</c>
        /// onto the widget, where it names nothing.
        /// </para>
        /// </summary>
        /// <param name="state">
        /// Its resolver is passed through so an indirect <c>/T</c> still reads as a partial name.
        /// <c>State.Resolve</c> is the bare delegate and charges no budget, so this costs nothing;
        /// the budget is charged by the <c>/Kids</c> loop that calls here.
        /// </param>
        /// <param name="dict">The candidate node.</param>
        private static bool IsFieldNode(PdfDictionary dict, State state)
            => AcroFieldClassification.IsFieldNode(dict, obj => state.Resolve(obj));

        /// <summary>
        /// Resolves <c>/FT</c> through the <c>/Parent</c> chain. ISO 32000-1 Table 220 marks the
        /// field type <b>inheritable</b>, so a terminal field may legitimately carry none of its own
        /// — the standard shape for a radio group and for any generator that hoists /FT onto a group
        /// node. Treating "has a direct /FT" as "is a field" skips exactly those terminal fields and
        /// instead demands /TU from the non-terminal parent, which is not a field a user tabs to and
        /// whose /TU is not inheritable, so it could not help them.
        /// <para>Bounded by <see cref="PdfLimits.MaxFieldInheritanceDepth"/> hops and by the shared
        /// traversal budget, so a <c>/Parent</c> cycle costs a fixed number of lookups rather than
        /// spinning. That is a CLIMB bound, deliberately not this type's
        /// <see cref="MaxFieldDepth"/>, which bounds the <c>/Kids</c> descent and is pinned
        /// cross-plugin — see there. The values are equal, and that is a coincidence rather than a
        /// constraint.</para>
        /// </summary>
        private static PdfName InheritedFieldType(PdfDictionary field, State state)
        {
            var resolve = state.Resolve;

            // MaxFieldInheritanceDepth + 1 NODES, not + 0: this was `hop <= cap`, which is 51
            // iterations, and the /Parent-ring test asserts the resulting 52 lookups as a literal.
            var raw = AcroFieldInheritance.Find(
                field, PdfName.FT,
                d => resolve(d[PdfName.Parent]) as PdfDictionary,
                o => resolve(o),
                o => o is PdfName,
                PdfLimits.MaxFieldInheritanceDepth + 1,
                ref state.Budget,
                out _);

            return resolve(raw) as PdfName;
        }

        /// <summary>
        /// True when the value resolves to a string that carries actual characters. An empty or
        /// whitespace-only /TU is the same as having none.
        /// </summary>
        private static bool HasNonEmptyString(State state, PdfObject obj)
        {
            var s = state.Resolve(obj) as PdfString;
            if (s == null || s.RawBytes == null || s.RawBytes.Length == 0) return false;
            // On an encrypted document the bytes are ciphertext and GetText() yields mojibake
            // rather than null, which is still the honest answer to "is a value present?".
            return !string.IsNullOrWhiteSpace(s.GetText());
        }

        /// <summary>
        /// Traversal state. A class rather than a pile of <c>ref</c> parameters because the budget
        /// is shared between the recursion and <see cref="InheritedFieldType"/>.
        /// </summary>
        private sealed class State
        {
            internal State(AcroFormResolver resolve, AcroFormObjectIdResolver objectId, int budget)
            {
                Resolve  = resolve;
                ObjectId = objectId;
                Budget   = budget;
            }

            internal AcroFormResolver Resolve { get; }
            internal AcroFormObjectIdResolver ObjectId { get; }
            internal int Budget;
            internal bool BudgetExhausted;
            internal bool DepthExceeded;

            internal readonly HashSet<PdfDictionary> Visited = new HashSet<PdfDictionary>();
            internal readonly List<AcroFormField> Fields = new List<AcroFormField>();
        }
    }
}
