// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Rendering;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Embeds an ICC color profile as an /OutputIntent in a PDF document.
    ///
    /// <para>
    /// An /OutputIntent describes the intended output color space of the document.
    /// It is required by PDF/A-1, PDF/A-2, PDF/X, and other standards.
    /// This implementation adds an /OutputIntent stream to the document catalog
    /// via an incremental update.
    /// </para>
    ///
    /// <para>
    /// The catalog /OutputIntents array is updated (or created) with a new OutputIntent
    /// dictionary whose /DestOutputProfile stream holds the raw ICC bytes.
    /// Common S values: "GTS_PDFA1" (PDF/A), "GTS_PDFX" (PDF/X), "ISO_PDF_Output_Intent".
    /// </para>
    ///
    /// Usage:
    /// <code>
    ///   byte[] iccBytes = File.ReadAllBytes("sRGB.icc");
    ///   PdfIccEmbedder.EmbedProfile(input, output, iccBytes,
    ///       outputConditionId: "sRGB",
    ///       outputCondition: "sRGB IEC61966-2.1",
    ///       registryName: "http://www.color.org",
    ///       info: "sRGB",
    ///       subtype: "GTS_PDFA1");
    /// </code>
    /// </summary>
    public static class PdfIccEmbedder
    {
        /// <summary>
        /// The <c>/OutputConditionIdentifier</c> used when the caller does not name a registered
        /// output condition. PDF 32000-1:2008 §14.11.5 reserves "Custom" for exactly that case.
        /// </summary>
        public const string DefaultOutputConditionId = "Custom";

        /// <summary>The <c>/S</c> output-intent subtype for PDF/A-1 (the default here).</summary>
        public const string PdfA1Subtype = "GTS_PDFA1";

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Embeds an ICC color profile from a file into the PDF as an /OutputIntent.
        /// </summary>
        public static void EmbedProfile(
            Stream input, Stream output,
            string iccPath,
            string outputConditionId = DefaultOutputConditionId,
            string outputCondition   = null,
            string registryName      = null,
            string info              = null,
            string subtype           = PdfA1Subtype)
        {
            if (iccPath == null) throw new ArgumentNullException(nameof(iccPath));
            byte[] iccBytes = File.ReadAllBytes(iccPath);
            EmbedProfile(input, output, iccBytes, outputConditionId,
                outputCondition, registryName, info, subtype);
        }

        /// <summary>
        /// Embeds a raw ICC color profile byte array into the PDF as an /OutputIntent.
        /// </summary>
        /// <remarks>
        /// <see cref="WriteOutputIntent"/> with a profile — so an existing intent of the same
        /// <paramref name="subtype"/> is replaced, and <c>/N</c> is the profile's own.
        /// </remarks>
        public static void EmbedProfile(
            Stream input, Stream output,
            byte[] iccBytes,
            string outputConditionId = DefaultOutputConditionId,
            string outputCondition   = null,
            string registryName      = null,
            string info              = null,
            string subtype           = PdfA1Subtype)
        {
            if (iccBytes == null) throw new ArgumentNullException(nameof(iccBytes));

            Write(input, output, new OutputIntentSpec
            {
                Subtype = subtype,
                OutputConditionIdentifier = outputConditionId,
                OutputCondition = outputCondition,
                RegistryName = registryName,
                Info = info,
                Profile = iccBytes,
            }, nameof(iccBytes));
        }

        /// <summary>
        /// Writes an output intent as one incremental revision: the profile stream when there is
        /// one, the intent dictionary, the catalog's <c>/OutputIntents</c>, and — when asked —
        /// <c>/Trapped</c> in <c>/Info</c>.
        /// </summary>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">
        /// The subtype or the condition identifier is empty, or <see cref="OutputIntentSpec.Profile"/>
        /// is not an ICC profile this library can read.
        /// </exception>
        /// <exception cref="Core.PdfEncryptedDocumentException">The document is encrypted.</exception>
        /// <exception cref="Core.PdfException">The document has no catalog object to revise.</exception>
        /// <remarks>
        /// <para><b>One implementation</b>, behind the Conformance plugin's <c>OutputIntentWriter</c>
        /// too, so every decision a validator checks is made once: the profile validated, <c>/N</c>
        /// from its header (a caller's <c>/N</c> of 3 on a CMYK profile would fail A20), an encrypted
        /// document refused, objects never written with no <c>/Root</c> reference to attach them to,
        /// an intent without a profile and <c>/Trapped</c> both writable.</para>
        ///
        /// <para><b>An intent of the same <c>/S</c> is replaced, not joined.</b> Both appended
        /// unconditionally. So converting a document that already carried a <c>GTS_PDFX</c> intent
        /// wrote a second one, which this library's <c>PdfXValidator</c> fails (X11: the printing
        /// condition is ambiguous). <c>PdfAValidator</c>'s A15 fails the PDF/A equivalent, two
        /// intents naming different profiles. Intents of other subtypes are kept: ISO 32000-2
        /// §14.11.5 NOTE 1 is explicit that a file may carry one per workflow.</para>
        ///
        /// <para><b>The profile is checked before the document is read</b>, through the header gate
        /// every reader of a profile in this tree shares (<c>IccProfile.TryReadHeader</c>, behind
        /// <see cref="IccProfileComponents"/>). Reading byte 16 alone and answering 3 for anything
        /// unrecognised would make a file that is not an ICC profile at all (a PNG, a truncated
        /// download) an RGB output intent.</para>
        /// </remarks>
        public static OutputIntentWritten WriteOutputIntent(Stream input, Stream output, OutputIntentSpec spec)
            => Write(input, output, spec, nameof(spec));

        // profileParameter is the caller's name for the profile, for the ArgumentException that
        // refuses it: `iccBytes` through EmbedProfile, whose callers were promised that name.
        private static OutputIntentWritten Write(
            Stream input, Stream output, OutputIntentSpec spec, string profileParameter)
        {
            if (input  == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (spec   == null) throw new ArgumentNullException(nameof(spec));
            if (string.IsNullOrEmpty(spec.Subtype))
                throw new ArgumentException("An output intent needs a subtype (/S).", nameof(spec));
            if (string.IsNullOrEmpty(spec.OutputConditionIdentifier))
                throw new ArgumentException(
                    "An output intent needs an /OutputConditionIdentifier (ISO 32000-2 Table 401).",
                    nameof(spec));

            int components = 0;
            if (spec.Profile != null
                && !IccProfileComponents.TryGetDataColorSpaceComponentCount(spec.Profile, out components))
                throw new ArgumentException(
                    "The profile is not an ICC profile this library can read: the header is missing, " +
                    "has no 'acsp' signature, is over the size cap, or names a data colour space " +
                    "ICC.1 does not define.", profileParameter);

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Fail closed on encrypted input, before anything reads doc.Catalog. An encrypted
            // document loads no catalog at all (PdfDocument.Load sets IsEncrypted and returns
            // before assigning it). Proceeding would be wrong even with a catalog: the profile and
            // the intent's strings would be written in clear text into a file whose objects are
            // ciphertext, and this assembly cannot encrypt them.
            if (doc.IsEncrypted)
                throw new Core.PdfEncryptedDocumentException(
                    "Cannot write an output intent into an encrypted document: the profile " +
                    "stream and the /OutputIntent strings would be written in clear text where " +
                    "the reader expects per-object ciphertext, and this assembly cannot " +
                    "encrypt. Decrypt first (Tetitulz.Pdf.Encryption.PdfDecryptor), write, " +
                    "then re-encrypt — or use Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip.Run.");

            PdfDictionary catalog = doc.Catalog?.Dictionary
                ?? throw new Core.PdfException(
                    "Cannot write an output intent: the document has no catalog dictionary.");
            int catalogObjNum = CatalogObject.NumberOrRefuse(doc.Trailer, "the output intent");

            var result = new OutputIntentWritten();
            int nextObj = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            var objects = new List<IndirectObject>();

            var intent = new PdfDictionary();
            intent.Set(PdfName.Type, PdfName.OutputIntent);
            intent.Set(PdfName.S, new PdfName(spec.Subtype));
            intent.Set(PdfName.OutputConditionIdentifier, new PdfString(spec.OutputConditionIdentifier));
            if (!string.IsNullOrEmpty(spec.OutputCondition))
                intent.Set(PdfName.OutputCondition, new PdfString(spec.OutputCondition));
            if (!string.IsNullOrEmpty(spec.RegistryName))
                intent.Set(PdfName.RegistryName, new PdfString(spec.RegistryName));
            if (!string.IsNullOrEmpty(spec.Info))
                intent.Set(PdfName.Info, new PdfString(spec.Info));

            if (spec.Profile != null)
            {
                var profile = new PdfStream { RawData = spec.Profile };
                profile.Set(PdfName.N, new PdfNumber(components));
                profile.Set(PdfName.Length, new PdfNumber(spec.Profile.Length));
                int profileObjNum = nextObj++;
                objects.Add(new IndirectObject(profileObjNum, 0, profile));
                intent.Set(PdfName.DestOutputProfile, new PdfReference(profileObjNum, 0));
                result.EmbeddedProfile = true;
                result.ProfileComponents = components;
            }

            int intentObjNum = nextObj++;
            objects.Add(new IndirectObject(intentObjNum, 0, intent));

            // The existing intents, resolved — `/OutputIntents 7 0 R` is legal, and reading it raw
            // once skipped this loop and deleted a PDF/X file's intent — less any of this subtype.
            // An unreadable /OutputIntents is refused rather than read as none, which would replace a
            // PDF/X file's intents with the new one alone (Core.Document.EntryRead).
            var intents = new PdfArray();
            var existingIntents = EntryRead.Resolve(doc.ResolveObject, catalog[PdfName.OutputIntents], out string unreadable);
            if (unreadable != null)
                throw new Core.PdfException(
                    "The document's /OutputIntents could not be read (" + unreadable + "), so writing an " +
                    "output intent would replace the ones it holds. No changes were written.");
            if (existingIntents is PdfArray existing)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    var existingIntent = doc.ResolveObject(existing[i]) as PdfDictionary;
                    if ((doc.ResolveObject(existingIntent?[PdfName.S]) as PdfName)?.Value == spec.Subtype)
                        result.ReplacedIntents++;
                    else
                        intents.Add(existing[i]);
                }
            }
            intents.Add(new PdfReference(intentObjNum, 0));

            PdfDictionary revisedCatalog = PdfDictionary.CloneShallow(catalog);
            revisedCatalog.Set(PdfName.OutputIntents, intents);
            objects.Add(new IndirectObject(catalogObjNum, 0, revisedCatalog));

            PdfDictionary trailer = spec.Trapped.HasValue
                ? SetTrapped(doc, spec.Trapped.Value, ref nextObj, objects)
                : null;
            result.SetTrapped = spec.Trapped.HasValue;

            // An output intent is written for PDF/A or PDF/X, both of which require the trailer's /ID
            // (ISO 19005 6.1.3, ISO 15930); the revision carries the file's own, or one derived from
            // its bytes as IncrementalUpdateWriter derives it, when the file has none.
            trailer ??= PdfTrailerBuilder.ForUpdate(doc.Trailer, nextObj);
            if (PdfFileId.Existing(doc.ResolveObject(doc.Trailer?[PdfName.ID]), resolve: null) == null)
            {
                long position = ms.Position;
                ms.Position = 0;
                var original = new byte[ms.Length];
                Core.IO.StreamBytes.Fill(ms, original, 0, original.Length);
                ms.Position = position;
                trailer.Set(PdfName.ID, IncrementalUpdateWriter.ResolveOrSynthesiseId(
                    doc.Trailer, original, new IncrementalUpdateOptions { Document = doc }));
            }

            Documents.PdfPageContentAppender.AppendRevision(ms, output, doc, objects, nextObj, trailer);
            return result;
        }

        /// <summary>
        /// Stages <c>/Info</c> with <c>/Trapped</c> set, superseding the existing dictionary or
        /// creating one; returns the trailer to write when one had to be created, else null.
        /// </summary>
        /// <remarks>
        /// PDF/X requires a definite <c>/Trapped</c> (X10), and it lives in <c>/Info</c>, which many
        /// documents do not have. The plugin's copy of this once threw on exactly those, because
        /// <c>CloneShallow</c> refuses a null source; the branch that mints a new <c>/Info</c> could
        /// not run.
        /// </remarks>
        private static PdfDictionary SetTrapped(
            PdfDocument doc, bool trapped, ref int nextObj, List<IndirectObject> objects)
        {
            var infoRef = doc.Trailer[PdfName.Info] as PdfReference;
            var existingInfo = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
            PdfDictionary info = existingInfo == null ? new PdfDictionary() : PdfDictionary.CloneShallow(existingInfo);
            info.Set(PdfName.Trapped, new PdfName(trapped ? "True" : "False"));

            int infoObjNum = infoRef?.ReferenceObjectNumber ?? nextObj++;
            objects.Add(new IndirectObject(infoObjNum, 0, info));
            if (infoRef != null) return null;

            PdfDictionary trailer = PdfTrailerBuilder.ForUpdate(doc.Trailer, nextObj);
            trailer.Set(PdfName.Info, new PdfReference(infoObjNum, 0));
            return trailer;
        }
    }
}
