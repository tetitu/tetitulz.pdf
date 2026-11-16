// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Checks the field locks a document's signatures declare — ISO 32000-2 §12.8.2.4, FieldMDP:
    /// for each signature whose <c>/Reference</c> carries a FieldMDP transform, whether any field it
    /// locked has changed value in a revision appended after it.
    /// </summary>
    /// <remarks>
    /// <para><b>§12.8.2.4:</b> "The FieldMDP transform method shall be used to detect changes to the
    /// values of a list of form fields", and such signatures "shall be validated in a similar manner
    /// to DocMDP signatures". The transform parameters (Table 259) name the fields: <c>/Action /All</c>,
    /// <c>/Include</c> with a <c>/Fields</c> array, or <c>/Exclude</c> with one. The names are fully
    /// qualified (§12.7.4.2).</para>
    ///
    /// <para><b>What is compared.</b> Each locked field's own <c>/V</c> and the appearance it and its
    /// widgets name (<c>/AP</c>, <c>/AS</c>: a reader shows that, not the value), every field of the
    /// name taken together, in the revision the signature signed against the current document, in
    /// <see cref="DocMdpAnalyzer.Canonical"/> form — the comparison the DocMDP analysis makes of every
    /// changed object. So a field added under a locked name, listed first with the old value, does not
    /// stand in for the one a reader shows. A name in <c>/Include</c> locks the fields below it too. A
    /// locked field the signed revision did not have is not reported: the lock speaks about the values
    /// that existed when it was made. A locked field that is gone is reported, since a reader can no
    /// longer show what was signed.</para>
    ///
    /// <para><b>Fails closed like its sibling.</b> A signature whose <c>/ByteRange</c> cannot be read, or
    /// whose signed revision cannot be opened, reports a violation rather than a clean sheet: the lock
    /// exists to be checked, and "could not check" is not "unchanged".</para>
    /// </remarks>
    public static class FieldMdpAnalyzer
    {
        /// <summary>
        /// The field locks the signatures of <paramref name="doc"/> declare, each with the values that
        /// changed after it. Empty when no signature carries a FieldMDP reference.
        /// </summary>
        /// <param name="doc">The document, whose <see cref="PdfDocument.BaseStream"/> holds the file as stored.</param>
        /// <param name="fileLength">The file's length in bytes.</param>
        public static IReadOnlyList<FieldMdpAnalysis> Analyze(PdfDocument doc, long fileLength)
            => Analyze(doc, doc, fileLength, null);

        /// <summary>
        /// <see cref="Analyze(PdfDocument, long)"/> with the object graph and the byte offsets read
        /// from different views of one file — the shape <see cref="DocMdpAnalyzer"/> takes for an
        /// encrypted document, whose objects are read from a decrypted copy while its
        /// <c>/ByteRange</c> still indexes the original.
        /// </summary>
        /// <param name="objectGraph">Where fields and signature dictionaries are read from.</param>
        /// <param name="offsetSource">Where the file's bytes and cross-reference offsets are read from.</param>
        /// <param name="fileLength">The file's length in bytes.</param>
        /// <param name="openSignedRevision">
        /// Opens a signed revision's bytes as a document — a decryptor for an encrypted original;
        /// <c>null</c> opens them directly.
        /// </param>
        public static IReadOnlyList<FieldMdpAnalysis> Analyze(
            PdfDocument objectGraph, PdfDocument offsetSource, long fileLength,
            Func<byte[], PdfDocument> openSignedRevision)
        {
            if (objectGraph == null) throw new ArgumentNullException(nameof(objectGraph));
            if (offsetSource == null) throw new ArgumentNullException(nameof(offsetSource));

            var results = new List<FieldMdpAnalysis>();
            if (objectGraph.AcroForm == null || offsetSource.BaseStream == null) return results;

            Dictionary<string, string> current = null;
            var seen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            foreach (var field in objectGraph.AcroForm.GetSignatureFields())
            {
                var signature = field.SignatureValue;
                if (signature == null || !seen.Add(signature)) continue;
                if (!TryReadLock(signature, objectGraph.ResolveObject, out string action, out List<string> names))
                    continue;

                var analysis = new FieldMdpAnalysis(field.ReportedName, action, names, signature);
                results.Add(analysis);

                long signedEnd = DocMdpAnalyzer.GetByteRangeEnd(signature, objectGraph);
                if (signedEnd <= 0 || signedEnd > fileLength)
                {
                    analysis.Violations.Add(
                        "the signature's /ByteRange is missing, malformed or out of bounds, so the fields it " +
                        "locked cannot be checked.");
                    continue;
                }

                // Nothing was appended after this signature, so nothing it locked can have changed.
                if (SignedByteRanges.ReachesEndOfFile(offsetSource.BaseStream, signedEnd, fileLength))
                    continue;

                PdfDocument signedRevision;
                try
                {
                    byte[] prefix = DocMdpAnalyzer.ReadPrefix(offsetSource.BaseStream, signedEnd);
                    signedRevision = openSignedRevision != null
                        ? openSignedRevision(prefix)
                        : PdfDocument.Open(new MemoryStream(prefix, writable: false));
                }
                catch (Exception ex) when (ex is PdfException || ex is IOException)
                {
                    analysis.Violations.Add(
                        "the revision this signature signed could not be read, so the fields it locked " +
                        "cannot be checked: " + ex.Message);
                    continue;
                }

                using (signedRevision)
                {
                    var signed = FieldValues(signedRevision);
                    current ??= FieldValues(objectGraph);

                    foreach (string name in LockedNames(action, names, signed.Keys))
                    {
                        if (!signed.TryGetValue(name, out string before)) continue;
                        if (!current.TryGetValue(name, out string after))
                            analysis.Violations.Add($"field '{name}' was removed after this signature locked it.");
                        else if (!string.Equals(before, after, StringComparison.Ordinal))
                            analysis.Violations.Add($"field '{name}' changed value or appearance after this signature locked it.");
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// The FieldMDP transform parameters a signature dictionary carries, or <c>false</c> when it
        /// carries none it can be checked against.
        /// </summary>
        private static bool TryReadLock(
            PdfDictionary signature, Func<PdfObject, PdfObject> resolve,
            out string action, out List<string> names)
        {
            action = null;
            names = new List<string>();

            // Through DocMdpAnalyzer's reader, which accepts Table 252's array AND the lone
            // dictionary producers write. Requiring an array here would make this fail OPEN —
            // the certification honoured through the lenient reading while the lock that enforces
            // it is dropped — which contradicts this type's own "fails closed like its sibling".
            foreach (PdfDictionary sigRef in DocMdpAnalyzer.SignatureReferences(signature, resolve))
            {
                if ((resolve(sigRef[PdfName.TransformMethod]) as PdfName) != PdfName.FieldMDP) continue;
                if (!(resolve(sigRef[PdfName.TransformParams]) is PdfDictionary parameters)) continue;

                string a = (resolve(parameters[PdfName.Action]) as PdfName)?.Value;
                if (a != PdfName.All.Value && a != PdfName.Include.Value && a != PdfName.Exclude.Value) continue;

                if (resolve(parameters[PdfName.Fields]) is PdfArray fields)
                    foreach (var f in fields)
                        if (resolve(f) is PdfString s) names.Add(s.GetText());

                action = a;
                return true;
            }
            return false;
        }

        /// <summary>The fully qualified names <paramref name="action"/> and <paramref name="names"/> lock, out of <paramref name="existing"/>.</summary>
        private static IEnumerable<string> LockedNames(string action, List<string> names, IEnumerable<string> existing)
        {
            var listed = new HashSet<string>(names, StringComparer.Ordinal);
            var locked = new List<string>();
            foreach (string name in existing)
            {
                bool wanted = action == PdfName.All.Value
                    || (action == PdfName.Include.Value ? HasListedAncestor(name, listed) : !HasListedAncestor(name, listed));
                if (wanted) locked.Add(name);
            }
            return locked;
        }

        /// <summary>Whether <paramref name="name"/> or a field above it (§12.7.4.2: the parts joined by periods) is listed.</summary>
        private static bool HasListedAncestor(string name, HashSet<string> listed)
        {
            if (listed.Contains(name)) return true;
            for (int dot = name.LastIndexOf('.'); dot > 0; dot = name.LastIndexOf('.', dot - 1))
                if (listed.Contains(name.Substring(0, dot))) return true;
            return false;
        }

        /// <summary>
        /// Every addressable field's own <c>/V</c> and appearance in canonical form, by fully qualified
        /// name, a second field of a name joined to the first — the whole tree, with the cycle guard and
        /// depth cap every other walk over it carries.
        /// </summary>
        private static Dictionary<string, string> FieldValues(PdfDocument doc)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (doc.AcroForm == null) return values;

            var visited = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            Collect(doc.AcroForm.Fields, values, visited, 0);
            return values;
        }

        private static void Collect(IReadOnlyList<PdfFormField> fields, Dictionary<string, string> values,
                                    HashSet<PdfDictionary> visited, int depth)
        {
            if (depth >= PdfLimits.MaxTreeDepth) return;
            foreach (var field in fields)
            {
                if (field?.Dictionary == null || !visited.Add(field.Dictionary)) continue;

                string name = field.ReportedName;
                if (name != null && field.IsAddressableField)
                {
                    string shown = Shown(field);
                    values[name] = values.TryGetValue(name, out string earlier) ? earlier + "\n" + shown : shown;
                }

                Collect(field.Kids, values, visited, depth + 1);
            }
        }

        /// <summary>What a field shows: its value, and the appearance it and its widgets name.</summary>
        private static string Shown(PdfFormField field)
        {
            var shown = new System.Text.StringBuilder(DocMdpAnalyzer.Canonical(field.Value));
            AppendAppearance(shown, field.Dictionary);
            foreach (var kid in field.Kids)
                if (kid?.Dictionary != null && !kid.IsAddressableField)
                    AppendAppearance(shown, kid.Dictionary);
            return shown.ToString();
        }

        private static void AppendAppearance(System.Text.StringBuilder shown, PdfDictionary widget)
        {
            shown.Append('|').Append(DocMdpAnalyzer.Canonical(widget[PdfName.AP]));
            shown.Append('|').Append(DocMdpAnalyzer.Canonical(widget[PdfName.AS]));
        }
    }

    /// <summary>One signature's field lock (§12.8.2.4) and what changed after it.</summary>
    public sealed class FieldMdpAnalysis
    {
        internal FieldMdpAnalysis(string signatureFieldName, string action, IReadOnlyList<string> fields, PdfDictionary signature)
        {
            SignatureFieldName = signatureFieldName;
            Action = action;
            Fields = fields;
            Signature = signature;
        }

        /// <summary>Fully qualified name of the field whose signature declares the lock.</summary>
        public string SignatureFieldName { get; }

        /// <summary><c>All</c>, <c>Include</c> or <c>Exclude</c> (Table 259).</summary>
        public string Action { get; }

        /// <summary>The field names the transform parameters list; empty for <c>All</c>.</summary>
        public IReadOnlyList<string> Fields { get; }

        /// <summary>Locked fields whose values changed after the signature, or why they could not be checked.</summary>
        public List<string> Violations { get; } = new List<string>();

        /// <summary>True when every locked field still holds the value the signature signed.</summary>
        public bool IsCompliant => Violations.Count == 0;

        /// <summary>The signature dictionary — the identity a verifier matches its results against.</summary>
        internal PdfDictionary Signature { get; }
    }
}
