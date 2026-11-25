// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>
        /// How many levels of embedded files are validated: the document's own are; theirs are not,
        /// and an A99 says so.
        /// </summary>
        internal const int MaxEmbeddedFileDepth = 1;

        // How deep the validation running on this thread is inside embedded files. [ThreadStatic]
        // for the reason the diagnostic slots are.
        [ThreadStatic] private static int _embeddedFileDepth;

        /// <summary>
        /// The most embedded files one validation judges (A68): each is a whole validation of its own,
        /// and a file chooses how many it holds.
        /// </summary>
        internal const int MaxEmbeddedFilesValidated = 64;

        /// <summary>
        /// Embedded files the last validation on this thread opened and validated (A68) — the evidence,
        /// for a test, that a stream several specifications name is judged once. <c>[ThreadStatic]</c>.
        /// </summary>
        [ThreadStatic] internal static int EmbeddedFilesValidated;

        /// <summary>
        /// The embedded file rules, PDF/A-2 onwards (PDF/A-1 forbids embedded files: A03). A67 — a file
        /// specification in the <c>/EmbeddedFiles</c> name tree: with an <c>/EF</c>, both <c>/F</c> and
        /// <c>/UF</c> (19005-2/3 6.8, 19005-4 6.9); from PDF/A-3 its stream's <c>/Subtype</c> a MIME
        /// type (type/subtype); in PDF/A-3 an <c>/AFRelationship</c> and a place in some <c>/AF</c>
        /// array; PDF/A-4f's name dictionary has an <c>/EmbeddedFiles</c> tree (PDF/A-4's own
        /// <c>/AFRelationship</c> rule stays A4-05). A68 — PDF/A-2, and PDF/A-4 other than -4e and
        /// -4f: each embedded file is a PDF/A file of a part the part admits (PDF/A-1 or -2; PDF/A-1,
        /// -2 or -4) and conforms to it, validated here as the document is. the rules 6.8-1 to
        /// -5 (PDF/A-2, -3) and 6.9-1 to -5 (PDF/A-4).
        /// </summary>
        private static void CheckEmbeddedFileSpecifications(PdfDocument doc, PdfAConformancePart part, string level,
            List<PdfAViolation> v)
        {
            var catalog = doc?.Catalog?.Dictionary;
            if (catalog == null || part == PdfAConformancePart.Part1) return;
            bool part3 = part == PdfAConformancePart.Part3;
            bool part4 = part == PdfAConformancePart.Part4;
            string iso = part4 ? "ISO 19005-4 6.9; rules 6.9-1 to -5" : $"ISO 19005-{(int)part} 6.8; rules 6.8-1 to -5";

            // The file specifications: the /EmbeddedFiles name tree's, and a FileAttachment
            // annotation's /FS, for the rules are on every file specification of an embedded file (its
            // CosFileSpecification), wherever it is; conformance test files 6-8 t04 and t05 attach theirs that way.
            var pages = new List<PdfDictionary>();
            for (int p = 0; doc.Pages != null && p < doc.Pages.Count; p++) pages.Add(doc.Pages[p].Dictionary);
            var reading = EmbeddedFileSpecifications.Read(doc, catalog, pages, doc.ResolveObject);
            if (!reading.HasNameTree && part4 && level == "F")
                v.Add(new PdfAViolation("A67",
                    "A PDF/A-4f file's name dictionary has no /EmbeddedFiles (ISO 19005-4 6.9; rule 6.9-5)."));
            if (!reading.NameTreeComplete)
                ReportPartial("The A67-A68 embedded-file checks did not read the whole /EmbeddedFiles name tree.", "A67", "A68");
            var specs = reading.Specifications;
            if (specs.Count == 0) return;

            HashSet<PdfDictionary> associated = part3 ? AssociatedFilesOf(doc) : null;
            bool validateFiles = part == PdfAConformancePart.Part2 || (part4 && level != "E" && level != "F");
            var keys = new Occurrences();
            var files = new Occurrences();
            // Each embedded file is a whole validation, so a stream is judged once whatever names it — a
            // name tree of ten thousand keys can name one specification, and many specifications one
            // stream — and at most MaxEmbeddedFilesValidated streams are judged, the rest said.
            var verdicts = new Dictionary<object, string>(PdfReferenceEquality.Instance);
            bool capped = false;
            if (_embeddedFileDepth == 0) EmbeddedFilesValidated = 0;
            foreach (var (name, spec) in specs)
            {
                var ef = doc.ResolveObject(spec[PdfName.EF]) as PdfDictionary;
                var file = doc.ResolveObject(ef?[PdfName.F]) as PdfStream ?? doc.ResolveObject(ef?[PdfName.UF]) as PdfStream;
                if (ef != null)
                {
                    if (spec[PdfName.F] == null) keys.Add(name + " has no /F");
                    if (spec[PdfName.UF] == null) keys.Add(name + " has no /UF");
                }
                if (part3 && spec[PdfName.AFRelationship] == null) keys.Add(name + " has no /AFRelationship");
                if (part3 && !associated.Contains(spec)) keys.Add(name + " is in no /AF array");
                if ((part3 || part4) && file != null)
                {
                    string mime = (doc.ResolveObject(file[PdfName.Subtype]) as PdfName)?.Value;
                    if (!IsMimeTypeName(doc.ResolveObject(file[PdfName.Subtype]) as PdfName))
                        keys.Add(name + " /Subtype " + (mime == null ? "missing" : "/" + mime) + " is not a MIME type");
                }
                if (validateFiles && file != null)
                {
                    if (!verdicts.TryGetValue(file, out string why))
                    {
                        if (verdicts.Count >= MaxEmbeddedFilesValidated)
                        {
                            if (!capped)
                                ReportPartial($"More than {MaxEmbeddedFilesValidated.ToString(CultureInfo.InvariantCulture)} embedded files; " +
                                              "the rest were not validated (A68).", "A68");
                            capped = true;
                        }
                        else
                        {
                            why = WhyNotConforming(file, part);
                            verdicts[file] = why;
                        }
                    }
                    if (why != null) files.Add(name + " " + why);
                }
            }

            if (keys.Count > 0)
                v.Add(new PdfAViolation("A67", $"An embedded file's specification is not one the part admits: {keys} ({iso})."));
            if (files.Count > 0)
                v.Add(new PdfAViolation("A68",
                    $"An embedded file is not a conforming PDF/A file of a part ISO 19005-{(int)part} admits: {files} ({iso})."));
        }

        /// <summary><see cref="IsMimeType"/> of a <c>/Subtype</c> name; <see langword="false"/> for none.</summary>
        internal static bool IsMimeTypeName(PdfName subtype) => subtype != null && IsMimeType(subtype.Value);

        /// <summary>
        /// The profile's MIME pattern, <c>^[-\w+\.]+/[-\w+\.]+$</c> (JavaScript's <c>\w</c>: ASCII letters,
        /// digits and the underscore): two non-empty runs of those, a hyphen, a plus and a period,
        /// either side of one slash.
        /// </summary>
        private static bool IsMimeType(string value)
        {
            int slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0) return false;
            foreach (char c in value)
                if (c != '/' && !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9')
                    && c != '_' && c != '-' && c != '+' && c != '.')
                    return false;
            return true;
        }

        /// <summary>
        /// Every file specification some <c>/AF</c> array names, wherever in the file the array is:
        /// ISO 19005-3 6.8 asks that each embedded file be associated with the document or a part of it.
        /// </summary>
        internal static HashSet<PdfDictionary> AssociatedFilesOf(PdfDocument doc)
        {
            var found = new HashSet<PdfDictionary>();
            if (doc.Parser == null) return found;
            int nodes = 0;
            void Visit(PdfObject value, int depth)
            {
                if (value == null || depth > PdfLimits.MaxObjectGraphDepth || ++nodes > MaxLimitWalkNodes) return;
                if (value is PdfArray array)
                    for (int i = 0; i < array.Count; i++) Visit(array[i], depth + 1);
                else if (value is PdfDictionary dictionary)
                    foreach (var kvp in dictionary)
                    {
                        if (kvp.Key == PdfName.AF && doc.ResolveObject(kvp.Value) is PdfArray af)
                            for (int i = 0; i < af.Count; i++)
                                if (doc.ResolveObject(af[i]) is PdfDictionary spec) found.Add(spec);
                        Visit(kvp.Value, depth + 1);
                    }
            }
            // An object that does not parse is not an absent one: a file specification associated only from it
            // would read as unassociated, so the check says it was partial.
            int unreadable = 0;
            foreach (var kvp in doc.Parser.EnumerateAllObjects((number, ex) => unreadable++))
            {
                Visit(kvp.Value, 0);
                if (nodes > MaxLimitWalkNodes)
                {
                    ReportPartial($"The A67 association check stopped after {MaxLimitWalkNodes.ToString(CultureInfo.InvariantCulture)} objects.", "A67");
                    break;
                }
            }
            if (unreadable > 0)
                ReportPartial($"The A67 association check could not read {unreadable.ToString(CultureInfo.InvariantCulture)} object(s).", "A67");
            return found;
        }

        /// <summary>
        /// Why an embedded file is not a conforming PDF/A file of a part <paramref name="part"/>
        /// admits, or <see langword="null"/> when it is (or when this validation is already as deep
        /// in embedded files as it goes, which an A99 reports).
        /// </summary>
        private static string WhyNotConforming(PdfStream file, PdfAConformancePart part)
        {
            if (_embeddedFileDepth >= MaxEmbeddedFileDepth)
            {
                ReportPartial("An embedded file inside an embedded file was not validated (A68).", "A68");
                return null;
            }
            byte[] bytes;
            try
            {
                bytes = file.GetDecodedData();
            }
            catch (Exception ex)
            {
                return $"does not decode ({ex.GetType().Name})";
            }
            if (bytes == null || !StartsLikePdf(bytes)) return "is not a PDF file";
            if (_embeddedFileDepth == 0) EmbeddedFilesValidated++;

            PdfDocument embedded;
            try
            {
                embedded = PdfDocument.Open(new MemoryStream(bytes));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return $"does not open as a PDF ({ex.GetType().Name})";
            }

            var savedPartial = _partialChecks;
            string savedError = _lastValidationError;
            _embeddedFileDepth++;
            try
            {
                using (embedded)
                {
                    var identification = ReadIdentification(embedded);
                    int declared = int.TryParse(identification?.Part, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
                    bool admitted = declared == 1 || declared == 2 || (part == PdfAConformancePart.Part4 && declared == 4);
                    if (!admitted)
                        return $"declares pdfaid:part {(identification?.Part ?? "(none)")}";

                    var inner = new List<PdfAViolation>();
                    _partialChecks = inner;
                    var result = ValidateChecks(embedded, (PdfAConformancePart)declared, PdfAConformanceLevel.Declared, inner);
                    if (result.Violations.Count > 0)
                        return $"breaks PDF/A-{declared.ToString(CultureInfo.InvariantCulture)} " +
                               $"({result.Violations.Count.ToString(CultureInfo.InvariantCulture)} violation(s), the first {result.Violations[0].Code})";
                    if (inner.Count > 0)
                        savedPartial?.Add(new PdfAViolation(PartialCheckCode,
                            $"An embedded file's validation did not run in full — {inner[0].Message}", new[] { "A68" }));
                    return null;
                }
            }
            finally
            {
                _embeddedFileDepth--;
                _partialChecks = savedPartial;
                _lastValidationError = savedError;
            }
        }

        /// <summary>Whether <c>%PDF-</c> begins within the first 1024 bytes, where readers look for it.</summary>
        private static bool StartsLikePdf(byte[] bytes)
        {
            int end = Math.Min(bytes.Length, 1024) - 5;
            for (int i = 0; i <= end; i++)
                if (bytes[i] == '%' && bytes[i + 1] == 'P' && bytes[i + 2] == 'D' && bytes[i + 3] == 'F' && bytes[i + 4] == '-')
                    return true;
            return false;
        }
    }
}
