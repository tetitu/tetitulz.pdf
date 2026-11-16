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
    public sealed class PdfAcroForm
    {
        private readonly PdfDictionary _dict;
        private readonly PdfDocument _document;
        private List<PdfFormField> _fields;

        internal PdfDictionary Dictionary => _dict;

        /// <param name="document">
        /// The owning document, used to follow indirect references. Required: <c>/Fields</c> is
        /// routinely written as one.
        /// </param>
        /// <exception cref="ArgumentNullException">Either argument is <c>null</c>.</exception>
        /// <remarks>
        /// The null checks are explicit because this is public API and the change that made
        /// <c>LoadFields</c> resolve <c>/Fields</c> through the document turned a null
        /// <paramref name="document"/> from "returns an empty field list" into a
        /// <see cref="NullReferenceException"/> thrown from a property getter. Failing at
        /// construction, by contract, is the honest form of that.
        /// </remarks>
        /// <param name="dictionary">The catalog's <c>/AcroForm</c> dictionary.</param>
        public PdfAcroForm(PdfDictionary dictionary, PdfDocument document)
        {
            _dict = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
            _document = document ?? throw new ArgumentNullException(nameof(document));
        }

        /// <summary>The <c>/SigFlags</c> bits, following an indirect reference; 0 when absent.</summary>
        /// <remarks>
        /// This and <see cref="NeedAppearances"/> were raw reads in a class whose field loader
        /// resolves — an indirect <c>/SigFlags</c> read as 0 and an indirect
        /// <c>/NeedAppearances true</c> as false, disagreeing with Base's own readers of the same
        /// entries. A flag word, read as one (<see cref="PdfNumber.FlagWord"/>: the low 32 bits of an
        /// integer, 0 for anything else) — the saturating <c>IntValue</c> turned an out-of-range value into
        /// every bit set.
        /// </remarks>
        public int SigFlags => (_document.ResolveObject(_dict[PdfName.SigFlags]) as PdfNumber)?.FlagWord ?? 0;

        /// <summary><c>/NeedAppearances</c>, following an indirect reference; false when absent.</summary>
        public bool NeedAppearances
            => _document.ResolveObject(_dict[PdfName.NeedAppearances]) is PdfBoolean b && b.Value;

        public IReadOnlyList<PdfFormField> Fields
        {
            get
            {
                if (_fields == null)
                    _fields = LoadFields();
                return _fields;
            }
        }

        private List<PdfFormField> LoadFields()
        {
            // /Fields is frequently written as an indirect reference, at the array and at each
            // element alike, and the typed getters do not follow one — see FormTreeChildren, which
            // is the one answer this and PdfFormField.LoadKids share.
            var fields = new List<PdfFormField>();
            foreach (var fieldObj in FormTreeChildren.Resolve(_dict, PdfName.Fields, _document))
                fields.Add(PdfFormField.Create(fieldObj, _document, null));

            return fields;
        }

        /// <summary>
        /// The signature field <paramref name="fieldName"/> names: the one whose fully qualified name
        /// it is, or else the one signature field whose partial name it is. <c>null</c> when no field
        /// matches, and when more than one field has that partial name.
        /// </summary>
        /// <remarks>
        /// <para><b>ISO 32000-2 §12.7.4.2 makes the fully qualified name the identity</b> — two parents
        /// may each hold a <c>sig</c>. This compared the partial name at every depth and returned the
        /// first hit, so <c>b.sig</c> was never found and <c>sig</c> meant whichever field the walk
        /// reached first: the verifier and the counter-signer could not be pointed at the second of two
        /// such signatures, and were pointed at the first without a word. A partial name is still
        /// accepted where it names one field, which is every document whose fields are all top-level.</para>
        ///
        /// <para>Through <see cref="GetSignatureFields"/>, so the lookup walks the tree the enumeration
        /// walks, cycle guard and all.</para>
        /// </remarks>
        public PdfSignatureField FindSignatureField(string fieldName)
            => FindSignatureField(fieldName, out _);

        /// <summary>
        /// <see cref="FindSignatureField(string)"/>, also counting the signature fields whose partial
        /// name is <paramref name="fieldName"/> — so a caller can say "ambiguous" rather than "not
        /// found" when the answer is <c>null</c> because there were several.
        /// </summary>
        internal PdfSignatureField FindSignatureField(string fieldName, out int partialNameMatches)
        {
            partialNameMatches = 0;
            PdfSignatureField byPartialName = null;

            foreach (var field in GetSignatureFields())
            {
                if (field.FullyQualifiedName == fieldName) return field;

                if (field.FieldName == fieldName)
                {
                    byPartialName ??= field;
                    partialNameMatches++;
                }
            }

            return partialNameMatches == 1 ? byPartialName : null;
        }

        // Bounds /Kids recursion. The Kids getter lazily creates a fresh
        // PdfFormField wrapper per access, so a /Kids cycle would spawn
        // infinite distinct wrappers and stack-overflow (uncatchable in .NET).
        private static int MaxFieldTreeDepth => PdfLimits.MaxTreeDepth;

        public List<PdfSignatureField> GetSignatureFields()
        {
            var result = new List<PdfSignatureField>();
            CollectSignatureFields(Fields, result, 0, NewVisitedSet());
            return result;
        }

        /// <summary>
        /// The identity set the signature-field walk below carries. <b>The depth cap alone is not a
        /// cycle guard</b>, and every other walk over this same tree already knew that:
        /// <c>Base.Operations.Forms.PdfFormReader.Walk</c>, <c>PdfFormFiller.FillFields</c> and
        /// <c>Signing.SignatureFieldRegistration.FieldNames</c> each pair the cap with a visited set.
        /// This walk — the one every <b>signature</b> enumeration and lookup goes through — had only
        /// the cap.
        /// </summary>
        /// <remarks>
        /// <para>Keyed on the field's dictionary by reference. An object number would be the more
        /// obvious key and is the wrong one here: a cyclic <c>/Kids</c> re-reached through a
        /// <i>direct</i> dictionary has no object number, and <c>PdfFormField.Kids</c> materialises
        /// a fresh wrapper per read, so the wrapper is not a stable identity either. The dictionary
        /// instance is what the parser hands back for the same object every time.</para>
        /// <para>A depth cap bounds one branch; it does not bound the number of branches. A
        /// two-node cycle under a cap of <c>MaxTreeDepth</c> still walks the cap's worth of nodes on
        /// every path, and a diamond doubles the work at each level — the same distinction
        /// <c>PdfLimits</c> draws between <c>MaxFormXObjectDepth</c> and the aggregate invocation
        /// budget.</para>
        /// </remarks>
        /// <remarks>
        /// Reference identity, not value equality. <c>PdfDictionary</c> does not override
        /// <c>Equals</c> today, so the default comparer <i>happens to</i> do this — "happens to"
        /// being the reason it is stated: a value-equality override added later would silently turn
        /// this cycle guard into a de-duplicator that discards legitimate identical siblings.
        /// </remarks>
        private static HashSet<PdfDictionary> NewVisitedSet()
            => new HashSet<PdfDictionary>(Objects.PdfReferenceEquality.Instance);

        private void CollectSignatureFields(IReadOnlyList<PdfFormField> fields, List<PdfSignatureField> result,
                                            int depth, HashSet<PdfDictionary> visited)
        {
            if (depth >= MaxFieldTreeDepth) return;
            foreach (var field in fields)
            {
                if (field?.Dictionary != null && !visited.Add(field.Dictionary)) continue;

                if (field is PdfSignatureField sigField)
                    result.Add(sigField);

                CollectSignatureFields(field.Kids, result, depth + 1, visited);
            }
        }

    }
}
