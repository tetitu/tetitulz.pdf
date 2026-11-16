// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// What marked content a content stream already carries: where each sequence begins and ends,
    /// which MCID it claims, and — the entry that decides everything — whether that reading is
    /// complete enough to build on.
    /// </summary>
    /// <remarks>
    /// <para><b>Not a boolean and a refusal.</b>
    /// <c>ContentScanResult.HasMarkedContent</c> is set by three operators and says nothing about
    /// what they mark, so refusing on it would skip a page carrying any marked content at all:
    /// splicing a second
    /// set of MCIDs on top of an existing one would produce duplicate identifiers and a
    /// <c>/ParentTree</c> resolving content to the wrong element. That is a correct fear and the
    /// wrong conclusion — the identifiers only collide if you start counting at zero.</para>
    ///
    /// <para><b>Everything here is read from tokens the scanner already produced.</b> The scan
    /// keeps every operator's operands and byte offsets. The
    /// one thing it cannot supply is the page's <c>/Resources /Properties</c>, because a
    /// <c>BDC</c>'s property list may be a <i>name</i> resolved through it — so the caller passes
    /// that dictionary, and an unresolvable name is one of the ways this reading declares itself
    /// incomplete.</para>
    ///
    /// <para><b>Incomplete is the safe answer and is preferred to a clever one.</b> Nested
    /// sequences, an unbalanced <c>EMC</c>, a duplicate MCID, a non-artifact sequence carrying no
    /// MCID: each is a shape a completion pass could probably reason about and none of them is
    /// worth the risk, because being wrong here means writing operators into the middle of someone
    /// else's marked-content sequence. The caller refuses the page exactly as it did before, and
    /// says which of these it met.</para>
    /// </remarks>
    internal sealed class MarkedContentInventory
    {
        /// <summary>One balanced marked-content sequence in the stream.</summary>
        internal sealed class Sequence
        {
            /// <summary>The MCID it claims, or -1 for an artifact, which claims none.</summary>
            internal int Mcid = -1;

            /// <summary>The tag operand — <c>/P</c>, <c>/Span</c>, <c>/Artifact</c>, …</summary>
            internal string Tag;

            /// <summary>Byte offset of the opening <c>BDC</c>/<c>BMC</c> operation's first byte.</summary>
            internal int OpenStart;

            /// <summary>Exclusive byte offset one past the opening operator.</summary>
            internal int OpenEnd;

            /// <summary>Byte offset of the closing <c>EMC</c>.</summary>
            internal int CloseStart;

            /// <summary>Exclusive byte offset one past the closing <c>EMC</c>.</summary>
            internal int CloseEnd;

            /// <summary>True for a sequence tagged <c>/Artifact</c>, which carries no MCID by design.</summary>
            internal bool IsArtifact;

            /// <summary>Whether <paramref name="offset"/> falls inside the sequence's content.</summary>
            internal bool Contains(int offset) => offset >= OpenEnd && offset < CloseStart;
        }

        /// <summary>Sequences in stream order.</summary>
        internal IReadOnlyList<Sequence> Sequences { get; private set; } = Array.Empty<Sequence>();

        /// <summary>The greatest MCID already used, or -1 when none is.</summary>
        internal int MaxMcid { get; private set; } = -1;

        /// <summary>
        /// Why this reading cannot be built on, or <see langword="null"/> when it can.
        /// </summary>
        /// <remarks>
        /// Phrased as a caller-facing sentence rather than a code, because it becomes a
        /// <c>PageTagResult.SkipReason</c> and then an <c>AutoTagResult.SkippedPageReasons</c>
        /// entry — the only place a caller can learn why one page of a document went untagged.
        /// </remarks>
        internal string Incomplete { get; private set; }

        /// <summary>
        /// Reads the marked content of one already-scanned stream.
        /// </summary>
        /// <param name="tokens">The scanner's tokens, in stream order.</param>
        /// <param name="properties">
        /// The stream's <c>/Resources /Properties</c>, for a <c>BDC</c> whose property list is a
        /// name. Null is allowed and simply makes any such <c>BDC</c> unresolvable.
        /// </param>
        /// <param name="document">Used to follow an indirect property list; may be null.</param>
        internal static MarkedContentInventory Read(
            IReadOnlyList<ContentToken> tokens, PdfDictionary properties, PdfDocument document)
        {
            var inventory = new MarkedContentInventory();
            if (tokens == null) return inventory;

            var sequences = new List<Sequence>();
            var mcids = new HashSet<int>();
            Sequence open = null;

            for (int i = 0; i < tokens.Count; i++)
            {
                ContentToken token = tokens[i];

                switch (token.Operator)
                {
                    case PdfOperators.BeginMarkedContent:
                    case PdfOperators.BeginMarkedContentProps:
                        if (open != null)
                            return inventory.Refuse(
                                "the page nests one marked-content sequence inside another, so the "
                                + "byte range a new sequence would occupy cannot be established without "
                                + "reasoning about the nesting");

                        open = Begin(token, properties, document, out string beginError);
                        if (beginError != null) return inventory.Refuse(beginError);
                        break;

                    case PdfOperators.EndMarkedContent:
                        if (open == null)
                            return inventory.Refuse(
                                "the page closes a marked-content sequence it never opened (EMC with no "
                                + "matching BDC/BMC), so its existing marked content cannot be bounded");

                        open.CloseStart = token.StartOffset;
                        open.CloseEnd = token.EndOffset;

                        if (open.Mcid >= 0 && !mcids.Add(open.Mcid))
                            return inventory.Refuse(
                                "the page uses MCID " + open.Mcid.ToString(CultureInfo.InvariantCulture)
                                + " twice, so an existing identifier cannot be matched to one range");

                        sequences.Add(open);
                        open = null;
                        break;
                }
            }

            if (open != null)
                return inventory.Refuse(
                    "the page opens a marked-content sequence it never closes, so where it ends — and "
                    + "therefore where anything may be inserted — is unknown");

            inventory.Sequences = sequences;
            foreach (Sequence s in sequences)
                if (s.Mcid > inventory.MaxMcid) inventory.MaxMcid = s.Mcid;

            // The identifiers this pass mints count up from the largest one the page carries, and
            // the parent tree's array for the page holds one slot per identifier up to the last:
            // an /MCID of two billion is a two-billion-slot array, and one more than int.MaxValue
            // wraps. A page past the ceiling is left as it is, and said.
            if (inventory.MaxMcid > PdfLimits.MaxMarkedContentIdentifier)
                return inventory.Refuse(
                    "the page's marked content carries /MCID " + inventory.MaxMcid.ToString(CultureInfo.InvariantCulture) +
                    ", past PdfLimits.MaxMarkedContentIdentifier (" +
                    PdfLimits.MaxMarkedContentIdentifier.ToString(CultureInfo.InvariantCulture) +
                    "): the parent tree's array for the page would hold one slot per identifier");

            return inventory;
        }

        /// <summary>Reads one <c>BDC</c>/<c>BMC</c> into a half-open sequence.</summary>
        /// <remarks>
        /// <b>An artifact is the one sequence allowed to carry no MCID.</b> §14.8.2.2 makes
        /// <c>/Artifact</c> content that is deliberately outside the structure, so it claims no
        /// identifier and needs no element — which is also why it is safe to leave exactly where it
        /// is. Any other tag without an MCID marks content that the structure tree cannot reach,
        /// and adopting it would mean inventing an identifier inside a sequence this pass did not
        /// write.
        /// </remarks>
        private static Sequence Begin(
            ContentToken token, PdfDictionary properties, PdfDocument document, out string error)
        {
            error = null;

            var sequence = new Sequence
            {
                OpenStart = token.StartOffset,
                OpenEnd = token.EndOffset,
            };

            IReadOnlyList<PdfObject> operands = token.Operands;
            if (operands.Count > 0 && operands[0] is PdfName tag) sequence.Tag = tag.Value;

            sequence.IsArtifact = string.Equals(sequence.Tag, ArtifactTag, StringComparison.Ordinal);

            if (!string.Equals(token.Operator, PdfOperators.BeginMarkedContentProps, StringComparison.Ordinal))
            {
                // BMC has no property list, so it can carry no MCID.
                if (!sequence.IsArtifact)
                    error = "the page carries a /" + (sequence.Tag ?? "?") + " BMC sequence, which marks "
                          + "content with no MCID: the structure tree cannot reach it, and adopting it "
                          + "would mean inventing an identifier inside a sequence this pass did not write";
                return sequence;
            }

            PdfObject propsOperand = operands.Count > 1 ? operands[1] : null;
            PdfDictionary props = ResolveProperties(propsOperand, properties, document, out string resolveError);
            if (resolveError != null) { error = resolveError; return sequence; }

            PdfObject mcid = props == null ? null : Resolve(document, props[PdfName.MCID]);
            if (mcid is PdfNumber number && number.IsInteger && number.IntValue >= 0)
            {
                sequence.Mcid = number.IntValue;
                return sequence;
            }

            if (!sequence.IsArtifact)
                error = "the page carries a /" + (sequence.Tag ?? "?") + " BDC sequence whose property "
                      + "list holds no /MCID, so it marks content the structure tree cannot reach";

            return sequence;
        }

        /// <summary>
        /// Resolves a <c>BDC</c>'s property-list operand — an inline dictionary, or a name looked up
        /// in the stream's <c>/Resources /Properties</c>.
        /// </summary>
        private static PdfDictionary ResolveProperties(
            PdfObject operand, PdfDictionary properties, PdfDocument document, out string error)
        {
            error = null;

            PdfObject resolved = Resolve(document, operand);
            if (resolved is PdfDictionary inline) return inline;

            if (!(resolved is PdfName name))
            {
                error = "a BDC operator's property list is neither a dictionary nor a name, so what it "
                      + "marks cannot be read";
                return null;
            }

            PdfObject entry = properties == null ? null : Resolve(document, properties[name]);
            if (entry is PdfDictionary fromResources) return fromResources;

            error = "a BDC operator names the property list /" + name.Value + ", which the page's "
                  + "/Resources /Properties does not define, so the MCID it claims cannot be read";
            return null;
        }

        private static PdfObject Resolve(PdfDocument document, PdfObject value)
        {
            if (!(value is PdfReference) || document == null) return value;

            try { return document.ResolveObject(value); }
            catch (Exception ex)
            {
                AccessibilityDiagnostics.Report("MarkedContentInventory.Resolve", "reference not resolvable", ex);
                return null;
            }
        }

        private MarkedContentInventory Refuse(string reason)
        {
            Incomplete = reason;
            Sequences = Array.Empty<Sequence>();
            MaxMcid = -1;
            return this;
        }

        /// <summary>§14.8.2.2's tag for content outside the structure.</summary>
        private const string ArtifactTag = "Artifact";

    }
}
