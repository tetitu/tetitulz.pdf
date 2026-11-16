// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Adding a signature field to a document's interactive form — the one decision behind every
    /// writer in this assembly that does it: <see cref="PdfSigner"/>,
    /// <see cref="Timestamping.PdfDocumentTimestamper"/> and <see cref="VisualSignatureBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, for the signer, the timestamper and the visible-signature
    /// builder.</b> Keeping an existing form's <c>/SigFlags</c> would let a timestamp added to a form
    /// declaring <c>/SigFlags 0</c> — or none — leave the document claiming it holds no signatures;
    /// ignoring the names already in use would make a second document timestamp a second field
    /// called <c>DocTimeStamp1</c>. ISO 32000-2 §12.7.4.2 requires field dictionaries sharing a
    /// fully qualified name to share their value, which two different timestamps cannot.</para>
    ///
    /// <para><b>Where the form is written stays each writer's own.</b> The timestamper supersedes an
    /// indirect <c>/AcroForm</c> in place, and the signer and the visible-signature builder write the
    /// form into the catalog; both are conforming, and what this owns is what the form says.</para>
    /// </remarks>
    internal static class SignatureFieldRegistration
    {
        /// <summary>
        /// ISO 32000-2 Table 225: <c>SignaturesExist</c> (bit 1) and <c>AppendOnly</c> (bit 2). The
        /// other thirty bits are reserved and "shall be set to 0", so there is nothing to preserve
        /// from the form's previous value.
        /// </summary>
        private const int SignatureFlags = 3;

        /// <summary>
        /// The form a revision that adds a signature writes: every entry of
        /// <paramref name="existingForm"/>, <c>/Fields</c> as <paramref name="existingFields"/> plus
        /// <paramref name="newField"/>, and <c>/SigFlags 3</c>.
        /// </summary>
        /// <param name="existingForm">The document's resolved <c>/AcroForm</c>, or <c>null</c> for none.</param>
        /// <param name="existingFields">The form's resolved <c>/Fields</c> entries, in order.</param>
        /// <param name="newField">
        /// The field being added, or <c>null</c> when an existing field was adopted — it is already
        /// reachable, and listing it again would give it two places in the tree.
        /// </param>
        /// <remarks>
        /// Everything else the form declared survives. Building a fresh dictionary destroyed
        /// <c>/DA</c>, <c>/DR</c>, <c>/NeedAppearances</c>, <c>/CO</c> and <c>/XFA</c> once, and every
        /// existing text field lost the resources its appearance names.
        /// </remarks>
        internal static PdfDictionary FormWithSignature(
            PdfDictionary? existingForm, IEnumerable<PdfObject> existingFields, PdfReference? newField)
        {
            var form = existingForm != null ? PdfDictionary.CloneShallow(existingForm) : new PdfDictionary();

            var fields = new PdfArray();
            foreach (var field in existingFields) fields.Add(field);
            if (newField != null) fields.Add(newField);

            form.Set(PdfName.Fields, fields);
            form.Set(PdfName.SigFlags, new PdfNumber(SignatureFlags));
            return form;
        }

        /// <summary>The form's <c>/Fields</c> entries, following a reference to the array (§7.3.10).</summary>
        /// <remarks>
        /// The result becomes the whole of the new revision's <c>/Fields</c>, so an unresolved read is
        /// destructive rather than incomplete: <c>/Fields 12 0 R</c> read as absent dropped every form
        /// field the document had.
        /// </remarks>
        /// <exception cref="Core.PdfSigningException">The <c>/Fields</c> array names an object that could not be read.</exception>
        internal static List<PdfObject> FieldsOf(PdfDictionary? form, Func<PdfObject?, PdfObject?> resolve)
        {
            var fields = new List<PdfObject>();
            if (form == null) return fields;
            if (Core.Document.EntryRead.Resolve(o => resolve(o)!, form[PdfName.Fields]!, out string? unreadable) is PdfArray array)
                for (int i = 0; i < array.Count; i++) fields.Add(array[i]);
            else if (unreadable != null)
                throw new Core.PdfSigningException(
                    "The form's /Fields array could not be read (" + unreadable + "), so a revision " +
                    "adding a signature field would replace every field the form has. Nothing was signed.");
            return fields;
        }

        /// <summary>
        /// The document's <c>/AcroForm</c>, resolved, or <c>null</c> when it has none.
        /// </summary>
        /// <exception cref="Core.PdfSigningException">
        /// <c>/AcroForm</c> names an object that could not be read.
        /// </exception>
        /// <remarks>
        /// An unreadable form is not read as none: the revision would then write a form holding only
        /// the signature field and judge the signature-ordering rules against no existing signatures,
        /// and a reader whose parser can read the old form would show every field it held gone from
        /// the signed revision (<c>Core.Document.EntryRead</c>).
        /// </remarks>
        internal static PdfDictionary? FormOf(PdfObject? acroForm, Func<PdfObject?, PdfObject?> resolve)
        {
            var form = Core.Document.EntryRead.Resolve(o => resolve(o)!, acroForm!, out string? unreadable) as PdfDictionary;
            if (unreadable != null)
                throw new Core.PdfSigningException(
                    "The document's /AcroForm could not be read (" + unreadable + "), so a revision " +
                    "adding a signature field would replace the whole form. Nothing was signed.");
            return form;
        }

        /// <summary>
        /// Every field name the form already uses — each field's partial name and its fully qualified
        /// name — so a new field can avoid all of them.
        /// </summary>
        /// <remarks>
        /// <para>It walks <c>/Kids</c>, not just the top level of <c>/Fields</c>: a signature field is
        /// routinely a kid of a parent node, and a top-level read saw no names at all for those
        /// documents. Both spellings are recorded deliberately — a leaf name that two parents share is
        /// ambiguous, and an ambiguous name is still one a new field must not take.</para>
        ///
        /// <para>Depth is bounded by <see cref="PdfLimits.MaxTreeDepth"/> and a repeated object is
        /// visited once, so a <c>/Kids</c> graph that branches back on itself cannot spin here. A depth
        /// cap alone bounds one branch, never the number of branches.</para>
        /// </remarks>
        internal static HashSet<string> FieldNames(PdfDictionary? form, Func<PdfObject?, PdfObject?> resolve)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Walk(form, resolve, (_, leaf, qualified) =>
            {
                // An empty /T is no name (AcroFieldClassification): it added "" and "parent." here.
                // Spelled out rather than IsNullOrEmpty, which netstandard2.0 does not annotate.
                if (leaf == null || leaf.Length == 0) return;
                names.Add(leaf);
                if (qualified != null) names.Add(qualified);
            });
            return names;
        }

        /// <summary>
        /// Visits every field dictionary in the form's <c>/Fields</c> tree — kids included — with its
        /// partial name and its fully qualified name, either of which may be <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The one walk over a form's field tree in this assembly that is not an adoption search:
        /// <see cref="FieldNames"/> collects names through it and <see cref="SignatureOrdering"/> reads
        /// the signatures a document already carries. Depth is bounded and a repeated object is
        /// visited once, so a <c>/Kids</c> graph that branches back on itself cannot spin.
        /// </remarks>
        internal static void Walk(
            PdfDictionary? form, Func<PdfObject?, PdfObject?> resolve,
            Action<PdfDictionary, string?, string?> visit)
            => Collect(resolve(form?[PdfName.Fields]) as PdfArray, resolve, visit, prefix: null, depth: 0,
                       visited: new HashSet<int>());

        /// <summary>
        /// A partial field name for a new field: <paramref name="wanted"/> when no field uses it,
        /// otherwise the first free numbered variant of it.
        /// </summary>
        /// <remarks>
        /// <para><b>A period is replaced.</b> ISO 32000-2 §12.7.4.2: "a partial name shall not contain a
        /// PERIOD character", because the period separates the parts of a fully qualified name. A caller
        /// asking to sign <c>parent.approval</c> when that field is already signed would otherwise get
        /// a new top-level field whose own name is <c>parent.approval2</c> — which every other reader
        /// takes for a kid called <c>approval2</c>.</para>
        /// <para>The name is the caller's only when nothing forces otherwise; <c>PdfDeferredSigner</c>
        /// reads the field name back out of the document for exactly this reason.</para>
        /// </remarks>
        internal static string UniqueName(string wanted, ISet<string> taken)
        {
            string name = string.IsNullOrEmpty(wanted) ? "Signature1" : wanted.Replace('.', '_');
            if (!taken.Contains(name)) return name;

            string stem = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (stem.Length == 0) stem = "Signature";

            int suffix = 2;
            while (taken.Contains(stem + suffix)) suffix++;
            return stem + suffix;
        }

        private static void Collect(
            PdfArray? fields, Func<PdfObject?, PdfObject?> resolve, Action<PdfDictionary, string?, string?> visit,
            string? prefix, int depth, HashSet<int> visited)
        {
            if (fields == null || depth > PdfLimits.MaxTreeDepth) return;

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i] is PdfReference reference && !visited.Add(reference.ReferenceObjectNumber))
                    continue;

                if (!(resolve(fields[i]) is PdfDictionary field)) continue;

                string? leaf = (resolve(field[PdfName.T]) as PdfString)?.GetText();
                string? qualified = Core.Forms.FieldNames.Qualify(prefix, leaf);

                visit(field, leaf, qualified);

                Collect(resolve(field[PdfName.Kids]) as PdfArray, resolve, visit, qualified, depth + 1, visited);
            }
        }
    }
}
