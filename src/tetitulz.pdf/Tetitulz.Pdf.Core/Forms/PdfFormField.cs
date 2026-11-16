// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    public enum PdfFieldType
    {
        Unknown,
        Text,
        Button,
        Choice,
        Signature
    }

    public class PdfFormField
    {
        private readonly PdfDocument _document;
        private List<PdfFormField> _kids;

        internal PdfDictionary Dictionary { get; }

        public PdfFormField(PdfDictionary dictionary, PdfDocument document, PdfFormField parent)
        {
            Dictionary = dictionary;
            _document = document;
            Parent = parent;
        }

        public PdfFormField Parent { get; }

        /// <summary>
        /// The field's type from <c>/FT</c> (§12.7.3.1), inherited through <c>/Parent</c> and — when
        /// that yields nothing — taken from a widget kid that declares one.
        /// </summary>
        /// <remarks>
        /// <b>The downward step is a safety fix, not a convenience.</b>
        /// <see cref="AcroFieldClassification"/> reads a kid with no usable <c>/T</c> as a widget
        /// (see there for why <c>/FT</c> is not part of that test), so a group written
        /// <c>&lt;&lt; /T (sig) /Kids [7 0 R] &gt;&gt;</c> whose kid carries
        /// <c>/FT /Sig</c> and no <c>/T</c> is terminal here — and had no <c>/FT</c> anywhere in its
        /// own chain, so it resolved to <see cref="PdfFieldType.Unknown"/>.
        /// <c>PdfFormFiller.ApplyValue</c> then fell past its <c>case PdfFieldType.Signature</c>
        /// guard into <c>ApplyVariableTextValue</c> and <b>wrote a text value onto a signature
        /// field</b>. Unknown must not be the answer when a widget of this very field says what it
        /// is.
        /// </remarks>
        public PdfFieldType FieldType
        {
            get
            {
                var ft = ResolveFieldType(Dictionary, _document, Parent);
                if (ft == null) return PdfFieldType.Unknown;
                switch (ft.Value)
                {
                    case PdfFieldTypes.Text: return PdfFieldType.Text;
                    case PdfFieldTypes.Button: return PdfFieldType.Button;
                    case PdfFieldTypes.Choice: return PdfFieldType.Choice;
                    case PdfFieldTypes.Signature: return PdfFieldType.Signature;
                    default: return PdfFieldType.Unknown;
                }
            }
        }

        public string FieldName
        {
            get
            {
                // Resolved, like the /Kids read below: PdfDictionary's typed getters do NOT follow
                // an indirect reference, and /T written as one is legal. Unresolved it read as
                // null, which this class treats as "a widget, not a field".
                var t = _document?.ResolveObject(Dictionary[PdfName.T]) as PdfString
                        ?? Dictionary.GetString(PdfName.T);
                return t?.GetText();
            }
        }

        /// <summary>
        /// The partial names from the root down to this field, joined by <see cref="FieldNames.Qualify"/>;
        /// the empty string when no node on the path has a name. A name past
        /// <see cref="PdfLimits.MaxQualifiedFieldNameLength"/> is its stand-in.
        /// </summary>
        /// <remarks>
        /// Each node on the path keeps the name it last answered, with the <c>/T</c> object and the
        /// parent's name it was answered from, and answers it again while both are the same objects —
        /// so the siblings under one parent join their names onto the one string the parent answered,
        /// and a leaf under a chain a hundred deep costs one join, not a hundred. A <c>/T</c> replaced in
        /// the dictionary is a new object, and the name is joined afresh.
        /// </remarks>
        public string FullyQualifiedName
        {
            get
            {
                var path = new List<PdfFormField>();
                for (var current = this; current != null; current = current.Parent)
                    path.Add(current);

                string name = null;
                for (int i = path.Count - 1; i >= 0; i--)
                    name = path[i].QualifiedUnder(name);
                return name ?? string.Empty;
            }
        }

        /// <summary>This node's name joined under <paramref name="parent"/>, its parent's name, through the memo.</summary>
        private string QualifiedUnder(string parent)
        {
            PdfObject source = Dictionary[PdfName.T];
            NameMemo memo = _name;
            if (memo != null && ReferenceEquals(memo.Source, source) && ReferenceEquals(memo.Parent, parent))
                return memo.Name;
            string name = FieldNames.Qualify(parent, FieldName);
            _name = new NameMemo(source, parent, name);
            return name;
        }

        private NameMemo _name;

        /// <summary>One answered name and what it was answered from, replaced whole so a reader sees one answer.</summary>
        private sealed class NameMemo
        {
            internal NameMemo(PdfObject source, string parent, string name) { Source = source; Parent = parent; Name = name; }
            internal PdfObject Source { get; }
            internal string Parent { get; }
            internal string Name { get; }
        }

        /// <summary>
        /// The name a report gives this field: <see cref="FullyQualifiedName"/>, or <c>null</c> when
        /// no node in the chain carries a <c>/T</c>.
        /// </summary>
        /// <remarks>
        /// The fully qualified name rather than the bare <c>/T</c>, because only it identifies a field
        /// (§12.7.4.2): two parents may each hold a <c>sig</c>, and a report calling both "sig" names
        /// neither. The verifier, the DocMDP analysis, the signature listing and the incremental-save
        /// report all name signatures through this.
        /// </remarks>
        internal string ReportedName
        {
            get
            {
                string name = FullyQualifiedName;
                return name.Length == 0 ? null : name;
            }
        }

        /// <summary>The field's own <c>/V</c> (§12.7.3.1, Table 220), following an indirect reference.</summary>
        /// <remarks>
        /// <para><b>It was a raw read.</b> §7.3.10 lets any value be written as an indirect
        /// reference, and a signature's <c>/V</c> almost always is — so this returned a
        /// <c>PdfReference</c> and every consumer had to know to resolve it. Two of the three did;
        /// the rule is that the property answers the question it names.</para>
        /// <para><b>Deliberately still not inherited.</b> <c>/V</c> is inheritable, but whether a
        /// named child field takes its group's value is the open chain question, and
        /// <see cref="PdfSignatureField.SignatureValue"/> reads its own <c>/V</c> on purpose —
        /// a widget showing its parent's value is not a second signature. Resolution and
        /// inheritance are separable and only the first is settled.</para>
        /// </remarks>
        public PdfObject Value => _document?.ResolveObject(Dictionary[PdfName.V]) ?? Dictionary[PdfName.V];

        /// <summary>The field's own <c>/DV</c> default value, following an indirect reference.</summary>
        /// <remarks>Same reasoning as <see cref="Value"/>: resolved, not inherited.</remarks>
        public PdfObject DefaultValue => _document?.ResolveObject(Dictionary[PdfName.DV]) ?? Dictionary[PdfName.DV];

        /// <summary>
        /// The <c>/Ff</c> flag bits (see <see cref="PdfFieldFlags"/>), resolved through the
        /// inheritable <c>/Parent</c> chain — a radio kid carries no <c>/Ff</c> of its own.
        /// </summary>
        public int Flags => GetInheritedFlagWord(PdfName.Ff) ?? 0;

        /// <summary>
        /// A text field's <c>/MaxLen</c>, resolved through the inheritable chain, or 0 when no node
        /// sets one.
        /// </summary>
        /// <remarks>
        /// ISO 32000-2 Table 232 marks it "(Optional; inheritable)". <c>PdfFormFiller</c> and
        /// <c>PdfFormReader</c> read <c>Dictionary.GetInteger(PdfName.MaxLen)</c>, which is neither
        /// — so a comb field whose group holds the cell count, or whose count is written
        /// <c>/MaxLen 9 0 R</c>, was filled as ordinary text under a warning that it had no
        /// <c>/MaxLen</c>, while <c>PdfFormFlattener</c>, which climbs, laid out the comb.
        /// </remarks>
        internal int MaxLength => GetInheritedInteger(PdfName.MaxLen) ?? 0;

        /// <summary>
        /// This field's child field nodes. <b>Never null</b> — an empty list means "no kids", so a
        /// call site does not null-check something it also has to length-check.
        /// </summary>
        public IReadOnlyList<PdfFormField> Kids => _kids ??= LoadKids();

        /// <summary>
        /// True when this node is a <i>terminal</i> field — it holds the value. A node is terminal
        /// when it has no kids at all, or when every kid is a widget annotation rather than
        /// another field (ISO 32000-1 §12.7.3.1).
        /// </summary>
        /// <remarks>
        /// The widget-versus-field question is <see cref="AcroFieldClassification"/>'s, shared with
        /// the Conformance plugin's walk so the two cannot answer it differently. "A widget kid is
        /// distinguished by having no <c>/T</c>" is two thirds of it: a kid
        /// carrying <c>/Kids</c> is a field however it is named, because a widget annotation has no
        /// children.
        /// </remarks>
        public bool IsTerminal
        {
            get
            {
                var kids = Kids;
                if (kids.Count == 0) return true;
                foreach (var kid in kids)
                    if (AcroFieldClassification.IsFieldNode(kid.Dictionary, _document)) return false;
                return true;
            }
        }

        /// <summary>
        /// True when this node is the field a fully qualified name <b>addresses</b> and a value
        /// belongs to: a field node (§12.7.3.1) that is also <see cref="IsTerminal"/>.
        /// </summary>
        /// <remarks>
        /// <para><b><see cref="IsTerminal"/> alone is not this question, and the difference is the
        /// whole reason this exists.</b> A bare widget annotation has no kids, so it is terminal —
        /// but it is not a field, it has no <c>/T</c> of its own, and its fully qualified name is
        /// therefore its parent's. Enumerating terminal nodes hands back the same name twice for
        /// one signature field with one separate widget kid.</para>
        ///
        /// <para><b>It is a property rather than a filter on the walks, deliberately.</b> The walks
        /// must stay permissive: on a document whose <c>/Kids</c> form a cycle every node is
        /// non-terminal, so a walk that applied this predicate would return <i>nothing</i> and a
        /// signed document would report as unsigned. Tightening
        /// <c>PdfAcroForm.GetSignatureFields</c> this way was tried and reverted for exactly that,
        /// and it took the cycle guard's own assertion with it. <b>Whether a node is signable and
        /// whether the walk reached it are two questions</b>; this answers the first and leaves the
        /// second alone, so a caller that would rather see a malformed entry than miss one simply
        /// does not ask.</para>
        ///
        /// <para><c>/FT</c> is not consulted, for the reason
        /// <see cref="AcroFieldClassification"/> gives at length: Table 220 makes it inheritable, so
        /// a group legitimately carries its children's and a merged widget its field's. It is the
        /// one key that cannot discriminate.</para>
        /// </remarks>
        public bool IsAddressableField
            => AcroFieldClassification.IsFieldNode(Dictionary, _document) && IsTerminal;

        /// <summary>
        /// The widget annotation dictionaries through which this terminal field appears on a page:
        /// the field dictionary itself when field and widget are merged into one object (the common
        /// single-widget case), otherwise its <c>/Kids</c>. Empty for a non-terminal node.
        /// </summary>
        internal IReadOnlyList<PdfDictionary> WidgetDictionaries
        {
            get
            {
                var result = new List<PdfDictionary>();
                if (!IsTerminal) return result;

                var kids = Kids;
                if (kids.Count == 0)
                {
                    result.Add(Dictionary);
                    return result;
                }
                foreach (var kid in kids)
                    result.Add(kid.Dictionary);
                return result;
            }
        }

        private List<PdfFormField> LoadKids()
        {
            // Unresolved, /Kids 11 0 R would make this group look TERMINAL: WidgetDictionaries would
            // return the group's own dictionary, so a radio-button fill would find no /AP on-states
            // and be silently refused, and a signature group's /FT would never resolve, so a text
            // value could be written onto it. The reading — indirect array, indirect elements, and a
            // null document tolerated because this constructor is public — is FormTreeChildren's,
            // shared with PdfAcroForm.LoadFields.
            var kids = new List<PdfFormField>();
            foreach (var kidObj in FormTreeChildren.Resolve(Dictionary, PdfName.Kids, _document))
                kids.Add(Create(kidObj, _document, this));

            return kids;
        }

        /// <summary>
        /// The field type this node <b>means</b>: <c>/FT</c> inherited up the chain (§12.7.3.1,
        /// Table 220) and, when the chain declares none, taken from a widget kid that does.
        /// </summary>
        /// <remarks>
        /// <para><b>One ladder, two callers — <see cref="FieldType"/> and <see cref="Create"/>.</b>
        /// Walking the chain by hand without the downward widget step, for a signature written as
        /// <c>&lt;&lt; /T (sig) /V 8 0 R /Kids [widget carrying /FT /Sig] &gt;&gt;</c> — the shape
        /// <c>FormPdfFixtures.SignatureGroupWithTypeOnTheWidget</c> builds —
        /// <c>field.FieldType</c> would answer <c>Signature</c> while <c>Create</c> made a plain
        /// <see cref="PdfFormField"/>. <c>PdfAcroForm.GetSignatureFields</c> type-tests, so it would
        /// return the <i>widget</i> instead of the field; the widget carries no <c>/V</c>, so
        /// <c>HasSignature</c> would be false, <c>SignatureFieldWalk.DistinctSignatures</c> yield
        /// nothing, and <b>a signed document be reported as unsigned</b> — by the signature
        /// listing, by revision attribution and by <c>DocMdpAnalyzer</c> alike.</para>
        ///
        /// <para><b>Iterative and capped, and the cap is not decoration.</b>
        /// <see cref="Kids"/> is public and materialises a fresh wrapper per read, so a host looping
        /// it over a <c>/Kids</c> cycle can build an unbounded <see cref="Parent"/> chain — and a
        /// recursive climb over that is a <c>StackOverflowException</c>, which .NET does not let a
        /// caller catch.</para>
        ///
        /// <para><b>It reads kid <i>dictionaries</i> rather than kid wrappers on purpose.</b>
        /// Asking <c>Kids</c> here would make <see cref="Create"/> construct every descendant to
        /// answer one question, and a <c>/Kids</c> cycle would then never terminate at construction
        /// time. One level down, on the raw dictionaries, is all §12.7.3.1 needs.</para>
        /// </remarks>
        private static PdfName ResolveFieldType(
            PdfDictionary dict, PdfDocument document, PdfFormField parent)
        {
            if (dict == null) return null;

            // Up the chain: this node, then each ancestor, bounded.
            var inherited = AcroFieldInheritance.Find(
                dict, PdfName.FT, ArrivalChain(parent), Resolver(document),
                o => o is PdfName, PdfLimits.MaxTreeDepth, out bool capExhausted);
            if (inherited != null)
                return (document?.ResolveObject(inherited) ?? inherited) as PdfName;

            // The climb ends two ways and only one of them may continue. The chain genuinely running
            // out with no /FT is the case the downward repair exists for. The CAP stopping it with
            // ancestors still unread means what the file says is simply unknown — falling through
            // there turned a bounded-work refusal into a confidently wrong answer: a 101-deep chain
            // whose root says /Sig took /Tx from a widget kid, with nothing recording that the climb
            // had been cut short.
            if (capExhausted) return null;

            // Then one level down: the /FT a BARE WIDGET kid declares, null when the kids disagree.
            // A kid that is itself a field is skipped: reading its /FT would be inheritance
            // backwards, and ungated, a node whose child fields are /Sig became a
            // PdfSignatureField with no /V of its own and appeared in GetSignatureFields as a
            // phantom beside the real ones. One implementation, shared with the signer.
            return AcroFieldClassification.WidgetFieldType(dict, Resolver(document));
        }

        /// <summary>
        /// An inheritable flag word (<c>/Ff</c>): the low 32 bits (<see cref="PdfNumber.FlagWord"/>),
        /// never <see cref="PdfNumber.IntValue"/>, which saturates an unsigned bit 32 to every flag set.
        /// </summary>
        /// <remarks>
        /// <b>Resolved, like the four reads above it.</b> <c>PdfDictionary.GetInteger</c> tests
        /// <c>v is PdfNumber</c> against the <i>raw</i> stored value and does not follow a reference
        /// (§7.3.10 permits one anywhere), so a raw read of <c>/Ff 5 0 R</c> would be <b>absent</b> —
        /// and absent is not neutral here: <see cref="Flags"/> coalesces it to <c>0</c>, "no flags
        /// set". A radio group would look like an ordinary check box (<c>Radio</c> is bit 16), a
        /// password field like plain text (bit 14), a multiline field like a single line (bit 13) —
        /// every field-behaviour decision taken on a value the file did not say. An integer read is
        /// easy to miss beside <c>/Kids</c>, <c>/T</c> and <c>/FT</c>, the shapes a structural walk
        /// notices.
        /// </remarks>
        private int? GetInheritedFlagWord(PdfName key)
        {
            var raw = AcroFieldInheritance.Find(
                Dictionary, key, ArrivalChain(Parent), Resolver(_document),
                o => o is PdfNumber, PdfLimits.MaxTreeDepth, out _);

            return (_document?.ResolveObject(raw) ?? raw) is PdfNumber n ? n.FlagWord : (int?)null;
        }

        /// <summary>An inheritable integer entry, resolved through the <c>/Parent</c> chain as <see cref="GetInheritedFlagWord"/> is.</summary>
        private int? GetInheritedInteger(PdfName key)
        {
            var raw = AcroFieldInheritance.Find(
                Dictionary, key, ArrivalChain(Parent), Resolver(_document),
                o => o is PdfNumber, PdfLimits.MaxTreeDepth, out _);

            return (_document?.ResolveObject(raw) ?? raw) is PdfNumber n ? n.IntValue : (int?)null;
        }

        /// <summary>
        /// Resolves an inheritable entry through the <c>/Parent</c> chain. Used for the variable-text
        /// entries (<c>/DA</c>, <c>/Q</c>) that a widget kid normally leaves to its parent field.
        /// </summary>
        /// <param name="key">The inheritable entry to look for.</param>
        /// <param name="accept">
        /// What counts as an answer. A value the caller cannot use — the wrong type — must not end
        /// the climb, or the ancestor that holds the usable one is never reached. Null accepts any
        /// resolved non-null, non-<c>PdfNull</c> value.
        /// </param>
        /// <remarks>
        /// <para><b>It resolved nothing and accepted anything, and its sibling one method up does
        /// neither.</b> <see cref="GetInheritedInteger"/> resolves each node's value and type-tests
        /// it, so a wrong-typed or unreadable <c>/Ff</c> keeps climbing. This terminated on
        /// <c>node.Dictionary[key] != null</c> — the RAW stored value — which loses the real answer
        /// in three live ways:</para>
        /// <list type="bullet">
        ///   <item><c>/DA 99 0 R</c> on a child, where 99 is free or missing: the climb stops at the
        ///   child and hands back a reference that resolves to nothing, so the parent's real
        ///   <c>/DA</c> is never reached and <c>ParseDefaultAppearance(null)</c> yields the
        ///   fallback size in black.</item>
        ///   <item>A direct <c>/DA null</c>: same stop, same loss.</item>
        ///   <item><c>/DA /Helv</c> — a name where a string belongs: same stop, and the caller's
        ///   <c>as PdfString</c> then yields null while the parent's usable one is discarded.</item>
        /// </list>
        /// <para>In all three, "the file said nothing" and "the file said something unusable one
        /// level down" are indistinguishable, as for <c>/Ff</c> one method away — a guard one level short. It is
        /// resolved here rather than in a shared helper, where the shortfall would become a property
        /// of the shared code.</para>
        /// <para><b>Still returns the value RAW</b>, because all four callers resolve at the call
        /// site and the resolution here only decides <i>which node wins</i>. Returning the resolved
        /// object instead would be a second, invisible behaviour change.</para>
        /// </remarks>
        internal PdfObject GetInherited(PdfName key, Func<PdfObject, bool> accept = null)
            => AcroFieldInheritance.Find(
                   Dictionary, key, ArrivalChain(Parent), Resolver(_document),
                   accept, PdfLimits.MaxTreeDepth, out _);

        /// <summary>
        /// The chain this field was <b>descended</b> through, as an
        /// <see cref="AcroFieldInheritance.Find(PdfDictionary, PdfName, AncestorStep, Func{PdfObject, PdfObject}, Func{PdfObject, bool}, int, out bool)"/>
        /// stepper — not the <c>/Parent</c> key.
        /// </summary>
        /// <remarks>
        /// <para><b>Stateful, and it has to be.</b> Descending a <c>/Kids</c> cycle builds a chain in
        /// which one dictionary appears at several depths, so the next ancestor is not a function of
        /// the node just examined — a stepper written as a lookup on its argument would loop. The
        /// cursor here is what makes it correct, and <see cref="AncestorStep"/> documents the
        /// contract.</para>
        ///
        /// <para>A fresh instance per call, because the cursor is consumed by the walk.</para>
        /// </remarks>
        private static AncestorStep ArrivalChain(PdfFormField parent)
        {
            var cursor = parent;
            return _ =>
            {
                var next = cursor?.Dictionary;
                cursor = cursor?.Parent;
                return next;
            };
        }

        /// <summary>
        /// The reference-follower, or <see langword="null"/> when there is no document to follow one
        /// with — which the climb reads as "take the raw value", the behaviour every call site here
        /// already had through <c>?? node.GetName(key)</c>.
        /// </summary>
        private static Func<PdfObject, PdfObject> Resolver(PdfDocument document)
            => document == null ? (Func<PdfObject, PdfObject>)null : document.ResolveObject;

        internal static PdfFormField Create(PdfDictionary dict, PdfDocument document, PdfFormField parent)
        {
            // The same ladder FieldType uses — see ResolveFieldType for what the second copy that
            // stood here cost. PdfName defines value equality and a null-safe operator==, so this
            // covers the "no /FT anywhere" case too.
            if (ResolveFieldType(dict, document, parent) == PdfName.Sig)
                return new PdfSignatureField(dict, document, parent);

            return new PdfFormField(dict, document, parent);
        }

        public override string ToString() => $"{FieldType}: {FullyQualifiedName}";
    }
}
