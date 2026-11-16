// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Reads usage-rights signatures (ISO 32000-2 §12.8.2.3, "UR"; deprecated in PDF 2.0) and the
    /// rights they declare.
    ///
    /// Usage rights allow a PDF author to grant extended capabilities (form fill-in, annotation,
    /// save) to viewers that would not normally permit them. viewers honour them only when
    /// the signature certificate chains to an Adobe-recognised root; this reader surfaces the
    /// declared rights regardless of the certificate chain so callers can inspect what rights the
    /// document claims without needing a live trust-anchor check.
    /// </summary>
    /// <remarks>
    /// <para><b>Where it looks.</b> §12.8.1: a usage rights signature's dictionary "shall be
    /// referenced from the UR3 (PDF 1.6) entry in the permissions dictionary … (not from a signature
    /// field)". That is <c>/Perms /UR3</c>, and it is read first; the form's signature fields alone
    /// would give an empty list for every conforming Reader-extended document. The field walk
    /// stays, for the shape this library's own signer writes (GAPS.md §1); a signature reachable both ways is
    /// reported once.</para>
    /// </remarks>
    public static class PdfUsageRightsReader
    {
        /// <summary>
        /// Reason for the most recent <see cref="Read(Stream)"/> failure.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent read on another thread
        /// cannot overwrite it — read it on the thread that ran the call (see the Diagnostics
        /// Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        [System.ThreadStatic]
        private static string _lastError;

        /// <summary>Reads usage rights from a PDF file path.</summary>
        public static IReadOnlyList<UsageRightsEntry> Read(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Read(fs);
        }

        /// <summary>Reads usage rights from a PDF stream.</summary>
        public static IReadOnlyList<UsageRightsEntry> Read(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            LastError = null;
            try
            {
                using var doc = PdfDocument.Open(input);
                // Said in LastError, this door's channel for every failure: an encrypted document
                // loads no catalog, and the empty list it returned with nothing said claimed that a
                // Reader-extended document grants no rights.
                EncryptedRead.Refuse(doc, "the usage rights", "PdfOperations.ReadUsageRights(Stream, string)");
                return ReadFromDocument(doc);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return Array.Empty<UsageRightsEntry>();
            }
        }

        private static IReadOnlyList<UsageRightsEntry> ReadFromDocument(PdfDocument doc)
        {
            var results = new List<UsageRightsEntry>();

            var catalog = doc.Catalog;
            if (catalog == null) return results;

            // One signature dictionary reachable from /Perms and from a field, or from two fields,
            // is still one signature — so the set spans both searches.
            var seenSignatures = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            if (doc.ResolveObject(catalog.Dictionary[PdfName.Perms]) is PdfDictionary perms
                && doc.ResolveObject(perms[PdfName.UR3]) is PdfDictionary urSignature
                && seenSignatures.Add(urSignature))
            {
                var entry = ExtractUr3Entry(doc, urSignature);
                if (entry != null) results.Add(entry);
            }

            var acroFormObj = doc.ResolveObject(catalog.Dictionary[PdfName.AcroForm]);
            if (acroFormObj is not PdfDictionary acroForm) return results;

            var fieldsObj = doc.ResolveObject(acroForm[PdfName.Fields]);
            if (fieldsObj is not PdfArray fields) return results;

            // Hoisted out of the loop on purpose: a /Kids graph shared between two top-level fields
            // is still walked once.
            var visited = new HashSet<PdfObject>(PdfObjectIndex.ReferenceIdentityComparer);

            foreach (var fieldRef in fields)
            {
                if (doc.ResolveObject(fieldRef) is not PdfDictionary field) continue;
                CollectSignatureFields(doc, field, results, 0, visited, null, null, seenSignatures);
            }

            return results;
        }

        /// <summary>
        /// Walks a field and its <c>/Kids</c>, collecting UR3 usage-rights entries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The depth cap alone was not a guard, and the comment that said it was is the lesson.</b>
        /// It read "bounds /Kids recursion at <c>PdfLimits.MaxTreeDepth</c> so a self-referential
        /// field tree cannot stack-overflow" — true, and it stopped the wrong failure. With no record
        /// of what had already been visited, a field whose <c>/Kids</c> names the same object twice
        /// <i>doubles</i> the work at every level, so a cap of 100 permits 2<sup>100</sup> calls, not
        /// 100. A ~300-byte PDF (<c>1 0 obj &lt;&lt; /FT /Sig /Kids [1 0 R 1 0 R] &gt;&gt;</c>) spins
        /// at 100% CPU forever without ever throwing.
        /// </para>
        /// <para>
        /// <paramref name="visited"/> is keyed on the resolved dictionary instance, so it catches a
        /// repeat whether the file reaches it by the same reference or a different one, and it covers
        /// direct dictionaries that have no object number. This is the same shape
        /// <c>PdfImageReplacer.Walk</c> uses.
        /// </para>
        /// <para><b><c>/FT</c> and <c>/V</c> are carried down the descent</b> because Table 220
        /// makes both inheritable. A signature is routinely written as <c>&lt;&lt; /T (Sig1) /V 4 0 R
        /// /Kids [widget carrying /FT /Sig] &gt;&gt;</c>: the group node has no <c>/FT</c> and the
        /// widget kid has no <c>/V</c>, so requiring both on one dictionary returned an empty list
        /// for a Reader-extended document. Carried down rather than climbed up <c>/Parent</c>,
        /// matching <c>Core.Document.PdfSignatureDetection</c> and
        /// <c>Plugins.Maintenance.FontGlyphUsageScanner</c>: the descent is already happening, so
        /// it needs no second traversal and cannot follow a <c>/Parent</c> ring.</para>
        ///
        /// <para><b>The dedup set is not decoration.</b> Inheritance makes a widget kid see its
        /// parent's <c>/FT</c> and <c>/V</c>, so a <i>merged</i> field with a widget kid — where
        /// both entries sit on the parent — would match twice and report one signature's rights
        /// as two. Keyed on the signature dictionary's reference identity, so one signature yields
        /// one entry however many nodes can reach it.</para>
        /// </remarks>
        private static void CollectSignatureFields(PdfDocument doc, PdfDictionary field,
            List<UsageRightsEntry> results, int depth,
            HashSet<PdfObject> visited,
            PdfName inheritedType, PdfObject inheritedValue,
            HashSet<PdfObject> seenSignatures)
        {
            if (depth >= PdfLimits.MaxTreeDepth) return;
            if (!visited.Add(field)) return;

            // Resolved: §7.3.10 lets either be written as a reference, and an unresolved read is
            // indistinguishable from an absent key — which here means "not a signature".
            PdfName ft = doc.ResolveObject(field[PdfName.FT]) as PdfName ?? inheritedType;
            PdfObject v = doc.ResolveObject(field[PdfName.V]) ?? inheritedValue;

            if (ft == PdfName.Sig && v is PdfDictionary sigDict && seenSignatures.Add(sigDict))
            {
                var entry = ExtractUr3Entry(doc, sigDict);
                if (entry != null) results.Add(entry);
            }

            if (doc.ResolveObject(field[PdfName.Kids]) is PdfArray kids)
            {
                foreach (var kid in kids)
                {
                    if (doc.ResolveObject(kid) is PdfDictionary kidDict)
                        CollectSignatureFields(doc, kidDict, results, depth + 1, visited, ft, v,
                                               seenSignatures);
                }
            }
        }

        /// <summary>
        /// The first usage-rights reference of <paramref name="sigDict"/>, or null when it has none.
        /// </summary>
        /// <remarks>
        /// <para><b>The references are <c>DocMdpAnalyzer.SignatureReferences</c>'</b> — the one
        /// reading of <c>/Reference</c>, which also accepts the lone dictionary producers write where
        /// Table 255 says an array. This walk required the array, so a usage-rights grant spelled that
        /// way was not seen at all, which is the disagreement that reader was made shared to end.</para>
        ///
        /// <para><b>Either spelling of the method.</b> Table 256 names it <c>/UR</c>; viewers and this
        /// library's signer write <c>/UR3</c>. This accepted only the second.</para>
        /// </remarks>
        private static UsageRightsEntry ExtractUr3Entry(PdfDocument doc, PdfDictionary sigDict)
        {
            foreach (PdfDictionary sigRef in DocMdpAnalyzer.SignatureReferences(sigDict, doc.ResolveObject))
            {
                var method = doc.ResolveObject(sigRef[PdfName.TransformMethod]) as PdfName;
                if (!UsageRightsTransform.IsUsageRightsMethod(method)) continue;

                if (!(doc.ResolveObject(sigRef[PdfName.TransformParams]) is PdfDictionary tp)) continue;

                // Resolved like every other read here; an unresolved one blanks a field of the report.
                string signerName = (doc.ResolveObject(sigDict[PdfName.Name]) as PdfString)?.GetText();
                string digestMethod = (doc.ResolveObject(sigRef[PdfName.DigestMethod]) as PdfName)?.Value;
                string urVersion = (doc.ResolveObject(tp[PdfName.V]) as PdfName)?.Value;

                return new UsageRightsEntry
                {
                    Rights = UsageRightsTransform.Read(tp, doc.ResolveObject),
                    SignerName = signerName,
                    DigestMethod = digestMethod,
                    // Table 258: "Default value: 2.2."
                    Ur3Version = urVersion ?? "2.2",
                };
            }
            return null;
        }
    }

    /// <summary>
    /// Usage rights found in a single usage-rights signature reference within a PDF.
    /// </summary>
    public sealed class UsageRightsEntry
    {
        /// <summary>Combination of rights granted by this entry.</summary>
        public UsageRights Rights { get; init; }

        /// <summary>Signer name from the signature dictionary /Name entry, or null.</summary>
        public string SignerName { get; init; }

        /// <summary>Digest method (e.g. "SHA256") from the /SigRef /DigestMethod entry.</summary>
        public string DigestMethod { get; init; }

        /// <summary>/TransformParams /V version string (typically "2.2").</summary>
        public string Ur3Version { get; init; }

        public bool HasFormFillIn      => (Rights & UsageRights.FormFillIn)      != 0;
        public bool HasFormImportExport => (Rights & UsageRights.FormImportExport) != 0;
        public bool HasFormSave         => (Rights & UsageRights.FormSave)         != 0;
        public bool HasAnnotation       => (Rights & UsageRights.Annotation)       != 0;
        public bool HasDocumentSave     => (Rights & UsageRights.DocumentSave)     != 0;
    }
}
