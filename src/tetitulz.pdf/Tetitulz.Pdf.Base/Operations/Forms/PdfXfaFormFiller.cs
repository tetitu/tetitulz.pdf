// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Fills a <b>static</b> XFA form: the <c>datasets</c> packet and the AcroForm shadow, in one
    /// appended revision.
    /// </summary>
    /// <remarks>
    /// <para><b>A static XFA document carries its form twice.</b> The XFA XML is the definitive one
    /// for a viewer that honours XFA; the AcroForm is what everything else draws. Filling one and
    /// not the other produces a document that <i>renders</i> one value and <i>reports</i> another,
    /// depending on which reader opens it — the single worst outcome available here, because both
    /// readers are confident. So both halves are written, and where they could not both be written
    /// <see cref="XfaFillResult.DivergenceWarning"/> says so by name.</para>
    ///
    /// <para><b>Dynamic XFA is out of charter, deliberately</b> (GAPS.md §19d). A dynamic form's
    /// pages do not exist until an XFA processor lays them out from the <c>template</c> packet;
    /// there is no AcroForm shadow to keep in step and no page content to fill. That is a layout
    /// engine, not a form filler, and this library does not contain one. Such a document is not
    /// refused — its datasets are still filled, and its (placeholder) AcroForm still updated — but
    /// what a dynamic viewer then renders is not this library's claim to make.</para>
    ///
    /// <para><b>One revision, not two.</b> The AcroForm half is <c>PdfFormFiller.Plan</c>'s object
    /// list and the XFA half is one superseded stream; appending them together means a reader never
    /// sees a revision in which the two halves disagree, and means a signature check has one
    /// boundary to reason about rather than two.</para>
    ///
    /// <para><b>Encrypted input fails closed</b>, for exactly the reason
    /// <see cref="PdfFormFiller"/> states: the values would be written in clear text where the
    /// reader expects per-object ciphertext, and this assembly cannot decrypt.</para>
    /// </remarks>
    public static class PdfXfaFormFiller
    {
        /// <summary>
        /// Fills the values into both the XFA <c>datasets</c> packet and the AcroForm.
        /// </summary>
        /// <param name="input">Source PDF stream. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="values">
        /// AcroForm fully-qualified field name → value. <b>One key space, not two</b>: the XFA path
        /// is derived from the AcroForm name by <see cref="XfaFieldPaths"/>, so a caller does not
        /// have to know that the document holds its form twice.
        /// </param>
        /// <param name="options">Fill policy for the AcroForm half; may be null.</param>
        /// <exception cref="ArgumentNullException">Any of the first three arguments is null.</exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        public static XfaFillResult Fill(
            Stream input,
            Stream output,
            IDictionary<string, string> values,
            FormFillOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (values == null) throw new ArgumentNullException(nameof(values));

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Before the AcroForm read, for PdfFormFiller's reason: an encrypted document loads no
            // catalog, so its form always reads as absent and "no AcroForm" is the wrong diagnosis.
            EncryptedRevision.Refuse(doc, "fill the XFA form of", "the datasets packet and the AcroForm values");

            FormFillPlan plan = PdfFormFiller.Plan(doc, values, options);
            var objects = new List<IndirectObject>(plan.Objects);

            var xfa = new XfaHalf(doc, values);
            if (xfa.Replacement != null)
                objects.Add(new IndirectObject(xfa.StreamObjectNumber, 0, xfa.Replacement));

            PdfPageContentAppender.AppendRevision(ms, output, doc, objects, plan.NextObjectNumber);

            return new XfaFillResult(plan.Result, xfa);
        }

        // ------------------------------------------------------------------

        /// <summary>The XFA side of one fill: what was found, what was written, and the new stream.</summary>
        internal sealed class XfaHalf
        {
            internal bool Present { get; }
            internal string UnwritableReason { get; }
            internal int StreamObjectNumber { get; }
            internal PdfStream Replacement { get; }
            internal List<string> Filled { get; } = new List<string>();
            internal List<string> Unmatched { get; } = new List<string>();

            internal XfaHalf(PdfDocument doc, IDictionary<string, string> values)
            {
                PdfDictionary acroForm = doc.AcroForm?.Dictionary;
                Present = XfaPacket.IsPresent(acroForm);
                if (!Present) return;

                XfaPacket packet = XfaPacket.Read(acroForm, doc);
                PdfStream datasets = packet?.GetPacketStream(XfaPacket.DatasetsPacket);
                if (datasets == null)
                {
                    // Two different causes, one honest message: an unpackaged single-stream XDP
                    // (whose datasets are inside it, and splitting an XDP is an XML problem this
                    // does not take on), or a packaged entry that carries no datasets packet at all.
                    UnwritableReason = packet == null
                        ? "the /XFA entry did not resolve to any packet stream"
                        : packet.IsPackaged
                            ? "the /XFA array carries no 'datasets' packet"
                            : "the /XFA entry is a single XDP stream rather than named packets, so "
                              + "its datasets cannot be addressed without splitting the XDP";
                    return;
                }

                // NO FILE REACHES THIS, and it is kept deliberately. §7.3.8 makes every stream an
                // indirect object and this parser only materialises a PdfStream from one, so a
                // packet that resolved at all carries a number — a mutation sweep reports this
                // guard as a survivor and that is the expected result. It bounds the contract of
                // whatever hands back the stream rather than of today's only caller, and
                // superseding an object with no number is not something to attempt.
                if (datasets.ObjectNumber <= 0)
                {
                    // §7.3.8 makes every stream an indirect object, so this is a malformed file —
                    // and superseding an object with no number is not something to attempt.
                    UnwritableReason = "the datasets packet is not an indirect object, so it cannot "
                                       + "be superseded by an incremental update";
                    return;
                }

                byte[] datasetBytes = XfaPacket.BoundedBytes(datasets, out string refused);
                if (datasetBytes == null)
                {
                    UnwritableReason = "the datasets packet could not be read: " + refused;
                    return;
                }

                IReadOnlyDictionary<string, string> current = XfaDatasets.ReadValues(datasetBytes);
                if (current == null)
                {
                    UnwritableReason = "the datasets packet is not well-formed XML";
                    return;
                }

                var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
                var pathOfName = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in values)
                {
                    string path = XfaFieldPaths.Resolve(pair.Key, current.Keys);
                    if (path == null || byPath.ContainsKey(path)) { Unmatched.Add(pair.Key); continue; }

                    byPath[path] = pair.Value;
                    pathOfName[path] = pair.Key;
                }

                if (byPath.Count == 0) return;

                byte[] rewritten = XfaDatasets.WriteValues(
                    datasetBytes, byPath, out List<string> written);
                if (rewritten == null)
                {
                    UnwritableReason = "the datasets packet could not be rewritten";
                    foreach (var name in pathOfName.Values) Unmatched.Add(name);
                    return;
                }

                foreach (string path in written) Filled.Add(pathOfName[path]);

                // A fresh stream rather than a mutation of the parsed one: the document is still
                // open and being read for the AcroForm half, and superseding an object must not
                // depend on the order those two reads happen in.
                var replacement = new PdfStream();
                foreach (var kvp in datasets)
                    if (kvp.Key != PdfName.Filter && kvp.Key != PdfName.DecodeParms)
                        replacement.Set(kvp.Key, kvp.Value);

                replacement.RawData = rewritten;
                // No /Length here: PdfWriter sets it from the bytes it actually writes, for every
                // stream, and it has to — an encrypted stream's ciphertext is longer than its
                // plaintext. Setting it here was overwritten, which a mutation sweep proved by
                // deleting it and reddening nothing.
                replacement.ObjectNumber = datasets.ObjectNumber;
                replacement.GenerationNumber = 0;

                StreamObjectNumber = datasets.ObjectNumber;
                Replacement = replacement;
            }
        }
    }

    /// <summary>
    /// What a static-XFA fill did to each of the document's two copies of the form.
    /// </summary>
    /// <remarks>
    /// <b><see cref="DivergenceWarning"/> is the one to read.</b> The other members say what
    /// happened; that one says whether the result is self-consistent, which is the property a
    /// static XFA document can lose without any reader noticing.
    /// </remarks>
    public sealed class XfaFillResult
    {
        private readonly List<string> _xfaFilled;
        private readonly List<string> _xfaUnmatched;

        internal XfaFillResult(FormFillResult acroForm, PdfXfaFormFiller.XfaHalf xfa)
        {
            AcroForm = acroForm;
            XfaPresent = xfa.Present;
            XfaWritten = xfa.Replacement != null;
            _xfaFilled = xfa.Filled;
            _xfaUnmatched = xfa.Unmatched;
            DivergenceWarning = BuildDivergenceWarning(acroForm, xfa);
        }

        /// <summary>What the AcroForm half of the fill did — the same result a plain fill returns.</summary>
        public FormFillResult AcroForm { get; }

        /// <summary><c>true</c> when the document carried an <c>/AcroForm /XFA</c> entry.</summary>
        public bool XfaPresent { get; }

        /// <summary><c>true</c> when a rewritten <c>datasets</c> packet was appended.</summary>
        public bool XfaWritten { get; }

        /// <summary>Requested names whose value reached the XFA <c>datasets</c> packet.</summary>
        public IReadOnlyList<string> XfaFilledFields => _xfaFilled;

        /// <summary>Requested names with no element in the <c>datasets</c> packet.</summary>
        public IReadOnlyList<string> XfaUnmatchedFields => _xfaUnmatched;

        /// <summary>
        /// Why the two copies of the form no longer agree, or <see langword="null"/> when they do.
        /// </summary>
        /// <remarks>
        /// Null for a document with no XFA at all — there is only one copy, so it cannot diverge —
        /// and null when every requested name reached both copies or neither.
        /// </remarks>
        public string DivergenceWarning { get; }

        private static string BuildDivergenceWarning(FormFillResult acroForm, PdfXfaFormFiller.XfaHalf xfa)
        {
            if (!xfa.Present) return null;

            if (xfa.UnwritableReason != null)
                return "The document carries an /AcroForm /XFA packet that was not updated, because "
                       + xfa.UnwritableReason + ". A viewer that honours XFA will show the packet's "
                       + "original values while a viewer that reads the AcroForm shows the new ones.";

            var acroOnly = new List<string>();
            var xfaSet = new HashSet<string>(xfa.Filled, StringComparer.Ordinal);
            foreach (string name in acroForm.FilledFields)
                if (!xfaSet.Contains(name)) acroOnly.Add(name);

            var xfaOnly = new List<string>();
            var acroSet = new HashSet<string>(acroForm.FilledFields, StringComparer.Ordinal);
            foreach (string name in xfa.Filled)
                if (!acroSet.Contains(name)) xfaOnly.Add(name);

            if (acroOnly.Count == 0 && xfaOnly.Count == 0) return null;

            return (acroOnly.Count > 0
                       ? "Filled in the AcroForm but not in the XFA datasets: "
                         + string.Join(", ", acroOnly) + ". "
                       : string.Empty)
                   + (xfaOnly.Count > 0
                       ? "Filled in the XFA datasets but not in the AcroForm: "
                         + string.Join(", ", xfaOnly) + ". "
                       : string.Empty)
                   + "The document's two copies of the form now disagree for those fields, and which "
                   + "value a reader shows depends on whether it honours XFA.";
        }
    }
}
