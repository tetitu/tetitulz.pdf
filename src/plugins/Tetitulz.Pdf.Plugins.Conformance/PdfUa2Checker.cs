// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Validates a PDF against the PDF/UA-2 (ISO 14289-2:2024) document-level requirements: Clause 5
    /// "Version identification", Table 1 "PDF/UA identification schema" — the XMP metadata must
    /// declare <c>pdfuaid:part=2</c> and <c>pdfuaid:rev</c> (the four digits of the year of
    /// publication or revision), both REQUIRED — and the file a PDF 2.0 file (UA2-6.1, after veraPDF's
    /// PDF/UA-2 profile rule 6.1-1; see <see cref="CheckFileVersion"/>).
    ///
    /// <para>
    /// <b>The document-level slice of PDF/UA-2 support.</b> PDF/UA-2 is a new companion standard,
    /// not an extension of PDF/UA-1 — ISO 14289-1 stays tied to PDF 1.7/ISO 32000-1, which
    /// <see cref="PdfUaChecker"/> checks. PDF/UA-2's structural requirements — clause
    /// 8.2.5.2-8.2.5.33's per-structure-type child/role rules, and the requirement (ISO 32000-2:2020
    /// §14.8.6, referenced from ISO 14289-2) that every structure element belong to, or be
    /// role-mapped to, a known namespace — need a tree walk, and are the Accessibility feature's
    /// <c>PdfUa2StructureChecker</c>. This type checks what is catalog- and file-level: the XMP
    /// identifier every conforming file carries — the role <c>PdfUaChecker</c>'s own UA03 plays for
    /// PDF/UA-1 — and the file's version.
    /// </para>
    ///
    /// <para>
    /// Per ISO 14289-2:2024 Clause 5: <i>"The values of the pdfuaid:part and pdfuaid:rev
    /// properties do not determine conformity with this document by themselves; these criteria
    /// are specified in Clause 6."</i> A clean result from this checker is therefore not a
    /// PDF/UA-2 conformance certificate, any more than <see cref="PdfUaChecker"/>'s is for
    /// PDF/UA-1.
    /// </para>
    ///
    /// <para>
    /// Violation codes use a <c>UA2-</c> prefix, never the <c>UA00</c>-<c>UA99</c> range —
    /// PDF/UA-2 is a different conformance target a caller opts into, not more checkpoints on
    /// PDF/UA-1's numbering (GAPS.md §22d), so a <c>UA2-5</c> finding can never be confused with
    /// an unrelated same-numbered UA-1 finding.
    /// </para>
    /// </summary>
    public static class PdfUa2Checker
    {
        [ThreadStatic] private static string _lastValidationError;

        /// <summary>
        /// Describes the most recent recoverable problem encountered by this thread while
        /// validating. <see langword="null"/> when the last validation ran cleanly.
        /// </summary>
        public static string LastValidationError => _lastValidationError;

        private static void Report(string operation, string detail, Exception ex = null)
        {
            _lastValidationError = ex == null
                ? operation + ": " + detail
                : operation + ": " + detail + " — " + ex.GetType().Name + ": " + ex.Message;
        }

        /// <summary>Validates a PDF file's PDF/UA-2 version identification.</summary>
        public static PdfUa2ValidationResult Validate(string pdfPath)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return Validate(fs);
        }

        /// <summary>Validates a PDF stream's PDF/UA-2 version identification.</summary>
        public static PdfUa2ValidationResult Validate(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);
            return ValidateDocument(doc);
        }

        private static PdfUa2ValidationResult ValidateDocument(PdfDocument doc)
        {
            _lastValidationError = null;
            UnreadableObjects.Begin();
            var violations = new List<PdfUa2Violation>();

            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null)
            {
                // PdfDocument skips catalog loading for encrypted documents (see PdfUaChecker's
                // own ResolveCatalog) — the catalog is almost always a top-level object, so
                // resolve it directly rather than giving up.
                try
                {
                    catalog = doc.ResolveObject(doc.Trailer?[PdfName.Root]) as PdfDictionary;
                }
                catch (Exception ex)
                {
                    Report("PdfUa2Checker.ValidateDocument", "trailer /Root is not resolvable", ex);
                }
            }

            if (catalog == null)
            {
                violations.Add(new PdfUa2Violation("UA2-5", "No document catalog found; the " +
                    "pdfuaid:part=2 / pdfuaid:rev identifiers required by ISO 14289-2 Clause 5 " +
                    "could not be checked."));
                return Completed(violations);
            }

            CheckFileVersion(doc, catalog, violations);

            try
            {
                CheckXmpIdentifier(doc, catalog, violations);
            }
            catch (Exception ex)
            {
                Report("PdfUa2Checker.CheckXmpIdentifier", "check aborted", ex);
                violations.Add(new PdfUa2Violation("UA2-99",
                    $"The version-identification check could not be completed " +
                    $"({ex.GetType().Name}: {ex.Message}). The conformance report is incomplete."));
            }

            return Completed(violations);
        }

        /// <summary>The result, with a UA2-99 notice when an object the check read could not be read.</summary>
        private static PdfUa2ValidationResult Completed(List<PdfUa2Violation> violations)
        {
            string unreadable = UnreadableObjects.Notice();
            if (unreadable != null) violations.Add(new PdfUa2Violation("UA2-99", unreadable));
            return new PdfUa2ValidationResult(violations);
        }

        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => UnreadableObjects.Resolve(doc, obj,
                ex => Report("PdfUa2Checker.Resolve", "could not resolve indirect reference", ex));

        // ----------------------------------------------------------------
        //  UA2-6.1 — the file is a PDF 2.0 file (veraPDF's PDF/UA-2 profile, rule 6.1-1)
        // ----------------------------------------------------------------

        /// <summary>
        /// UA2-6.1 — "The file version shall be 2.0, specified either in the header or as the value of
        /// the Version entry": veraPDF's PDF/UA-2 profile, rule 6.1-1, which this check follows so that
        /// one file gets one verdict from both. The sentence is the profile's, not ISO 14289-2's: the
        /// standard asks that a file adhere to ISO 32000-2 (§6.2) and that its <c>Document</c> element be
        /// in the PDF 2.0 namespace (§8.2.5.2, whose NOTE 1 says a PDF/UA-2 file "cannot solely conform to
        /// PDF 1.7"), while §6.1 NOTE 2 bases its requirements on ISO 32000-2 "regardless of the version
        /// number present in the conforming file". The message names the profile for that reason. The
        /// document's version is the header's raised by a later catalog <c>/Version</c> (§7.7.2).
        /// </summary>
        private static void CheckFileVersion(PdfDocument doc, PdfDictionary catalog, List<PdfUa2Violation> v)
        {
            int major = doc.Version.Major, minor = doc.Version.Minor;
            // An encrypted document's Version is its header's: PdfDocument stops at /Encrypt before it
            // raises the version by the catalog's. Names are not encrypted, so the catalog's
            // /Version is read here, by the same rule (§7.7.2: the later of the two).
            if (doc.IsEncrypted && Resolve(doc, catalog[PdfName.Version]) is PdfName declared)
            {
                string[] parts = declared.Value.Split('.');
                if (parts.Length == 2
                    && int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int dMajor)
                    && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int dMinor)
                    && (dMajor > major || (dMajor == major && dMinor > minor)))
                {
                    major = dMajor;
                    minor = dMinor;
                }
            }
            if (major == 2 && minor == 0) return;
            v.Add(new PdfUa2Violation("UA2-6.1",
                $"The file version is {major}.{minor}; a PDF/UA-2 file is a PDF 2.0 file, its header or its " +
                "catalog's /Version saying 2.0 (veraPDF's PDF/UA-2 profile, rule 6.1-1; ISO 14289-2 §6.2 asks " +
                "for adherence to ISO 32000-2). Setting the catalog's /Version to /2.0 satisfies it."));
        }

        // ----------------------------------------------------------------
        //  Clause 5 — pdfuaid:part=2, pdfuaid:rev (four-digit year)
        // ----------------------------------------------------------------

        private static void CheckXmpIdentifier(PdfDocument doc, PdfDictionary catalog,
            List<PdfUa2Violation> v)
        {
            bool encrypted = doc.IsEncrypted;

            var metaRef = catalog[PdfName.Metadata];
            if (metaRef == null)
            {
                v.Add(new PdfUa2Violation("UA2-5",
                    "PDF/UA-2 requires an XMP metadata stream (/Metadata in catalog) declaring " +
                    "pdfuaid:part=2 and pdfuaid:rev (ISO 14289-2 Clause 5, Table 1)."));
                return;
            }

            var metaStream = Resolve(doc, metaRef) as PdfStream;
            if (metaStream == null)
            {
                v.Add(new PdfUa2Violation("UA2-5", "The /Metadata entry does not point to a valid stream."));
                return;
            }

            byte[] xmpBytes;
            try
            {
                xmpBytes = XmpStreamBytes.Read(metaStream);
            }
            catch (Exception ex)
            {
                Report("PdfUa2Checker.CheckXmpIdentifier", "XMP stream could not be decoded", ex);
                if (encrypted) v.Add(Unverifiable("the stream could not be decoded"));
                else
                    v.Add(new PdfUa2Violation("UA2-5",
                        $"The XMP metadata stream could not be decoded: {ex.Message}"));
                return;
            }

            if (xmpBytes == null || xmpBytes.Length == 0)
            {
                v.Add(new PdfUa2Violation("UA2-5", "The XMP metadata stream is empty."));
                return;
            }

            string partValue = null;
            string revValue = null;
            try
            {
                // One reader for the identification schema: PdfUaChecker's UA03, this check and
                // the accessibility audit's target detection all ask which part the packet declares.
                PdfUaXmpIdentifier.Read(xmpBytes, out partValue, out revValue);
            }
            catch (XmlException ex)
            {
                Report("PdfUa2Checker.CheckXmpIdentifier", "XMP is not well-formed XML", ex);
                // On an encrypted document the stream bytes are ciphertext unless
                // /EncryptMetadata is false, so "not well-formed" says nothing about the
                // document's conformance — but neither does silence (see PdfUaChecker's UA03).
                if (encrypted) v.Add(Unverifiable("the packet is not well-formed XML"));
                else
                    v.Add(new PdfUa2Violation("UA2-5",
                        $"XMP metadata is not well-formed XML: {ex.Message}"));
                return;
            }

            if (partValue != "2")
                v.Add(new PdfUa2Violation("UA2-5",
                    $"XMP metadata must declare pdfuaid:part=\"2\" " +
                    $"(found: \"{partValue ?? "(missing)"}\")."));

            if (!IsFourDigitYear(revValue))
                v.Add(new PdfUa2Violation("UA2-5",
                    "XMP metadata must declare pdfuaid:rev as the four digits of the year of " +
                    $"publication or revision (found: \"{revValue ?? "(missing)"}\")."));
        }

        private static bool IsFourDigitYear(string value)
        {
            if (value == null || value.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (value[i] < '0' || value[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// The XMP packet of an encrypted document is ciphertext (unless <c>/EncryptMetadata</c>
        /// is false), and this checker does not decrypt — so the pdfuaid:part/rev declaration can
        /// be neither confirmed nor refuted. Reported as UA2-99 (the "check incomplete" code)
        /// rather than UA2-5, mirroring <see cref="PdfUaChecker"/>'s UA03/UA99 handling: asserting
        /// the declaration is wrong would be a claim this checker equally cannot support, and a
        /// suppressed check is indistinguishable from one that ran and found nothing.
        /// </summary>
        private static PdfUa2Violation Unverifiable(string cause)
            => new PdfUa2Violation("UA2-99",
                "UA2-5 could not be verified: the document is encrypted, so its XMP metadata " +
                $"stream is ciphertext to this checker ({cause}). The pdfuaid:part=\"2\" / " +
                "pdfuaid:rev declaration that PDF/UA-2 requires was therefore neither confirmed " +
                "nor refuted — decrypt the document and re-run the audit, or inspect the packet " +
                "with a tool that holds the key. The conformance report is incomplete.");
    }

    // ----------------------------------------------------------------
    //  Result types
    // ----------------------------------------------------------------

    /// <summary>Result of a PDF/UA-2 version-identification check.</summary>
    public sealed class PdfUa2ValidationResult
    {
        /// <summary>List of violations found. Empty list means the document passed all checks.</summary>
        public IReadOnlyList<PdfUa2Violation> Violations { get; }

        /// <summary>
        /// True when this checker found no violations.
        ///
        /// <para>
        /// <b>This is not a PDF/UA-2 conformance certificate.</b> It means only that the
        /// document declares a well-formed <c>pdfuaid:part=2</c>/<c>pdfuaid:rev</c> XMP
        /// identifier — nothing here checks clause 8.2.5.x's per-structure-type rules, namespace
        /// membership, or any other substantive PDF/UA-2 requirement. See this type's own remarks
        /// and GAPS.md §22d.
        /// </para>
        /// </summary>
        public bool IsConforming => Violations.Count == 0;

        internal PdfUa2ValidationResult(List<PdfUa2Violation> violations)
            => Violations = violations;
    }

    /// <summary>A single PDF/UA-2 version-identification violation.</summary>
    public sealed class PdfUa2Violation
    {
        /// <summary>Check code (e.g. "UA2-5"). Never in the UA00-UA99 range used by PDF/UA-1 checks.</summary>
        public string Code { get; }
        /// <summary>Human-readable description of the violation.</summary>
        public string Message { get; }

        internal PdfUa2Violation(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public override string ToString() => $"[{Code}] {Message}";
    }
}
