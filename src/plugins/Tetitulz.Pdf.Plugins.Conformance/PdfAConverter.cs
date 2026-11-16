// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Converts a PDF to PDF/A-1b, -2b or -3b conformance (ISO 19005-1/2/3) on a best-effort basis.
    /// 
    ///
    /// <para>
    /// Steps performed:
    /// <list type="number">
    ///   <item>Strip prohibited content: JavaScript, /AA, /OpenAction (JS) — and embedded files,
    ///     except when converting to Part 3, which explicitly permits any embedded file
    ///     (ISO 19005-3 lifts the Part 1/2 restriction A03 checks for).</item>
    ///   <item>Write an XMP metadata stream declaring the correct <c>pdfaid:part</c> /
    ///     <c>pdfaid:conformance=B</c> for the part being converted to.</item>
    ///   <item>Add a minimal sRGB <c>/OutputIntents</c> entry to the catalog — <c>/S /GTS_PDFA1</c>
    ///     regardless of part; that subtype string does not change across ISO 19005-1/2/3, only
    ///     <c>pdfaid:part</c> in the XMP does.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>What it does only when asked:</b> transparency removal
    /// (<see cref="PdfAConversionOptions.FlattenTransparency"/>, Part 1) and colour conversion into the
    /// output intent's family (<see cref="PdfAConversionOptions.ConvertColour"/>). Font
    /// subsetting/embedding is not performed — but this converter does not need to do it itself:
    /// <c>Tetitulz.Pdf.Accessibility.Fonts.PdfFontEmbeddingRemediator</c> already exists (built for
    /// PDF/UA-1 §7.21.4.1, which asks the identical question — "embed a program for every font the
    /// document does not already carry one for") and can be run against the input <b>before</b>
    /// handing the result to this converter, since <c>Tetitulz.Pdf.Accessibility</c> already
    /// references <c>Tetitulz.Pdf.Plugins.Conformance</c> and can therefore reach both — the
    /// reverse reference is not possible, since <c>Accessibility → Plugins.Conformance</c> is
    /// already a sanctioned architecture edge and the opposite direction would close a cycle.
    /// Fonts that are not embedded before conversion will still cause
    /// <see cref="PdfAValidator"/> violation A06.
    /// </para>
    ///
    /// <para>
    /// <b>OutputIntent:</b> ISO 19005-1 §6.2.2 (carried forward unchanged by Parts 2 and 3) requires
    /// the OutputIntent to embed an ICC profile via <c>/DestOutputProfile</c>. Pass the sRGB (or
    /// other RGB) ICC profile bytes via the <c>iccProfile</c> parameter to produce a fully-conformant
    /// OutputIntent. When it is <c>null</c> (the default), only the OutputIntent <em>dictionary</em>
    /// is written — without the profile the file is NOT fully conformant (note that
    /// <see cref="PdfAValidator"/> checks for the embedded profile as A08).
    /// </para>
    /// </summary>
    public static class PdfAConverter
    {
        /// <summary>
        /// Converts a PDF to PDF/A-1b by applying sanitization and adding the required XMP metadata.
        /// </summary>
        /// <param name="iccProfile">
        /// Optional ICC profile bytes (e.g. sRGB IEC61966-2.1) embedded as the OutputIntent's
        /// <c>/DestOutputProfile</c>. Required for full ISO 19005-1 §6.2.2 conformance; when
        /// null the OutputIntent has no embedded profile (see remarks).
        /// </param>
        /// <remarks>
        /// <para><b>This overload throws where the options overload returns a result.</b> It has no
        /// channel to report through, and the options overload now RETURNS — writing nothing — on an
        /// encrypted or signed input. Forwarding blindly therefore turned a thrown exception into a
        /// zero-byte output file and a normal return, which is strictly worse: a batch job cannot
        /// tell that from a successful conversion of an empty document. So the result is inspected
        /// and a refusal is re-raised as the exception this signature has always used.</para>
        /// </remarks>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the PDF/A-1b output.</param>
        /// <param name="documentTitle">
        /// Written to <c>/Info</c> and the XMP packet. PDF/A requires the two to agree, so supplying
        /// it here is what keeps them consistent.
        /// </param>
        /// <param name="documentCreator">Author, written to the same two places.</param>
        public static void ConvertToPdfA1b(
            Stream input,
            Stream output,
            string documentTitle = null,
            string documentCreator = null,
            byte[] iccProfile = null)
            => ConvertThrowing(nameof(ConvertToPdfA1b), PdfAConformancePart.Part1,
                input, output, documentTitle, documentCreator, iccProfile);

        /// <summary>
        /// Converts a PDF to PDF/A-2b (ISO 19005-2), throwing form. See
        /// <see cref="ConvertToPdfA1b(Stream, Stream, string, string, byte[])"/> for what "throwing
        /// form" means and <see cref="ConvertToPdfA2b(Stream, Stream, PdfAConversionOptions)"/> for
        /// the overload that reports rather than throws.
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the PDF/A-2b output.</param>
        /// <param name="documentTitle">Written to <c>/Info</c> and the XMP packet.</param>
        /// <param name="documentCreator">Author, written to the same two places.</param>
        /// <param name="iccProfile">
        /// Optional ICC profile bytes embedded as the OutputIntent's <c>/DestOutputProfile</c>.
        /// </param>
        public static void ConvertToPdfA2b(
            Stream input,
            Stream output,
            string documentTitle = null,
            string documentCreator = null,
            byte[] iccProfile = null)
            => ConvertThrowing(nameof(ConvertToPdfA2b), PdfAConformancePart.Part2,
                input, output, documentTitle, documentCreator, iccProfile);

        /// <summary>
        /// Converts a PDF to PDF/A-3b (ISO 19005-3), throwing form. Unlike Parts 1 and 2, Part 3
        /// permits embedded files of any kind, so this overload's sanitize step does not strip
        /// them. See <see cref="ConvertToPdfA1b(Stream, Stream, string, string, byte[])"/> for what
        /// "throwing form" means and <see cref="ConvertToPdfA3b(Stream, Stream, PdfAConversionOptions)"/>
        /// for the overload that reports rather than throws.
        /// </summary>
        /// <param name="input">Stream holding the source PDF.</param>
        /// <param name="output">Receives the PDF/A-3b output.</param>
        /// <param name="documentTitle">Written to <c>/Info</c> and the XMP packet.</param>
        /// <param name="documentCreator">Author, written to the same two places.</param>
        /// <param name="iccProfile">
        /// Optional ICC profile bytes embedded as the OutputIntent's <c>/DestOutputProfile</c>.
        /// </param>
        public static void ConvertToPdfA3b(
            Stream input,
            Stream output,
            string documentTitle = null,
            string documentCreator = null,
            byte[] iccProfile = null)
            => ConvertThrowing(nameof(ConvertToPdfA3b), PdfAConformancePart.Part3,
                input, output, documentTitle, documentCreator, iccProfile);

        private static void ConvertThrowing(
            string methodName, PdfAConformancePart part,
            Stream input, Stream output, string documentTitle, string documentCreator, byte[] iccProfile)
        {
            var result = Convert(input, output, new PdfAConversionOptions
            {
                DocumentTitle = documentTitle,
                DocumentCreator = documentCreator,
                IccProfile = iccProfile,
                // The historical signature always wrote the identifier. Keeping that for this
                // overload means no existing caller changes behaviour; the honest default lives
                // on the options overload, which is the one that can report back.
                DeclareConformanceRegardless = true,
            }, part);

            if (result != null && !result.OutputWritten)
                throw new Tetitulz.Pdf.Core.PdfException(
                    $"{methodName}: the input was refused and nothing was written" +
                    (result.Warnings != null && result.Warnings.Count > 0
                        ? " — " + string.Join("; ", result.Warnings)
                        : ".") +
                    " Use the PdfAConversionOptions overload to inspect the result instead of " +
                    "catching this.");
        }

        /// <summary>
        /// Converts a PDF to PDF/A-1b and reports what was actually achieved.
        /// </summary>
        /// <remarks>
        /// <para><b>The identifier is withheld when the output cannot be conformant.</b> The
        /// older overload wrote <c>pdfaid:part 1</c> unconditionally, so a document with
        /// non-embedded fonts came out declaring PDF/A-1b while <see cref="PdfAValidator"/>
        /// rejected it — a claim a downstream reader has no way to check. This overload runs the
        /// validator over the sanitized document first and, unless
        /// <see cref="PdfAConversionOptions.DeclareConformanceRegardless"/> is set, writes the
        /// identifier only when nothing blocks it.</para>
        /// <para>The blockers it can see are the ones <see cref="PdfAValidator"/> checks plus a
        /// missing output profile. Transparency and colour-space requirements are neither
        /// converted nor detected — see the type remarks.</para>
        /// </remarks>
        public static PdfAConversionResult ConvertToPdfA1b(
            Stream input, Stream output, PdfAConversionOptions options)
            => Convert(input, output, options, PdfAConformancePart.Part1);

        /// <summary>
        /// Converts a PDF to PDF/A-2b (ISO 19005-2) and reports what was actually achieved. See
        /// <see cref="ConvertToPdfA1b(Stream, Stream, PdfAConversionOptions)"/> for the shape of
        /// what this reports; the only normative difference in <em>this converter's</em> own steps
        /// is which <c>pdfaid:part</c> the XMP declares — everything else (blockers, output intent,
        /// the transparency/colour-conversion/font-embedding limitations) is identical.
        /// </summary>
        public static PdfAConversionResult ConvertToPdfA2b(
            Stream input, Stream output, PdfAConversionOptions options)
            => Convert(input, output, options, PdfAConformancePart.Part2);

        /// <summary>
        /// Converts a PDF to PDF/A-3b (ISO 19005-3) and reports what was actually achieved.
        /// </summary>
        /// <remarks>
        /// <b>The one real behavioural difference from Parts 1/2:</b> ISO 19005-3 lifts the
        /// embedded-files restriction A03 checks for, so this overload's sanitize step keeps
        /// embedded files rather than stripping them — a PDF/A-3 "package" document (e.g. an
        /// invoice with its machine-readable source data attached) is exactly the shape this
        /// standard exists for.
        /// </remarks>
        public static PdfAConversionResult ConvertToPdfA3b(
            Stream input, Stream output, PdfAConversionOptions options)
            => Convert(input, output, options, PdfAConformancePart.Part3);

        /// <summary>An ICC profile's data colour space signature (ICC.1 §7.2.6, bytes 16–19: <c>"RGB "</c>, <c>"CMYK"</c> …), or null.</summary>
        private static string IccProfileFamily(byte[] profile)
            => profile != null && profile.Length >= 20 ? System.Text.Encoding.ASCII.GetString(profile, 16, 4) : null;

        /// <summary>
        /// The checks the pre-validation cannot judge, because they are about what this converter is
        /// about to write: A07 is the PDF/A identifier itself, and A15-A18 are about the
        /// <c>/OutputIntents</c> entry, which does not exist in the sanitized stream yet. A42 and A43
        /// are about the last trailer and what follows the last <c>%%EOF</c> — the revision this
        /// converter appends, whose trailer always carries an <c>/ID</c> (<see cref="PdfIccEmbedder"/>
        /// derives one when the file has none). See the comment at the pre-validation step for why
        /// each is left to a caller validating the output.
        /// </summary>
        private static readonly HashSet<string> NotJudgedBeforeWriting =
            new HashSet<string>(StringComparer.Ordinal) { "A07", "A15", "A16", "A17", "A18", "A42", "A43" };

        /// <summary>
        /// Whether a pre-validation entry — a violation, or an A99 saying a check did not run in
        /// full — concerns any check this converter judges before claiming conformance.
        /// </summary>
        private static bool JudgedBeforeWriting(PdfAViolation entry)
        {
            foreach (string check in entry.Concerns)
                if (!NotJudgedBeforeWriting.Contains(check)) return true;
            return false;
        }

        private static PdfAConversionResult Convert(
            Stream input, Stream output, PdfAConversionOptions options, PdfAConformancePart part)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options = options ?? new PdfAConversionOptions();

            var result = new PdfAConversionResult();

            // Step 0 — refuse what this converter cannot convert, as a REPORTED blocker.
            //
            // Left to PdfSanitizer one step down, an encrypted input would surface as a
            // PdfEncryptedDocumentException thrown out of a type the caller never invoked, and a
            // SIGNED document would be renumbered like any other, its signature silently
            // destroyed, with nothing on the result saying so and DeclaredPdfAIdentifier able to
            // come back true. PdfXConverter performs the same two checks through the same code.
            if (!ConverterInputInspection.TryInspect(input, out bool isEncrypted, out bool isSigned,
                                                     out string readError))
            {
                result._blockers.Add($"The input could not be read as a PDF: {readError}");
                return result;
            }

            if (isEncrypted)
            {
                // A01 forbids encryption outright, so this is a conformance blocker rather than an
                // unsupported input: a converted-and-still-encrypted file could never conform.
                result._blockers.Add(
                    $"A01: the input is encrypted. PDF/A-{(int)part} forbids encryption, so this " +
                    "cannot be converted in place — decrypt it first (Tetitulz.Pdf.Encryption), or " +
                    "run the conversion inside PdfEncryptedRoundTrip.Run, whose output would then " +
                    "have to be left unencrypted to conform.");
                return result;
            }

            if (isSigned && !options.AllowSignatureInvalidation)
            {
                result._blockers.Add(
                    "The input carries a digital signature, and conversion rewrites every object, " +
                    "so the signature would verify as corrupt rather than as modified. Set " +
                    "PdfAConversionOptions.AllowSignatureInvalidation to convert anyway.");
                return result;
            }

            if (isSigned)
                result._warnings.Add(
                    "The input was signed and the signature has been invalidated by conversion " +
                    "(AllowSignatureInvalidation was set).");

            // Step 1 — sanitize: remove JavaScript, /AA, JS /OpenAction, and embedded files —
            // except for Part 3, which explicitly permits them (ISO 19005-3 lifts the restriction
            // A03 checks for; PdfAValidator already skips A03 for Part3, so keeping the files here
            // is what makes the sanitized document agree with what it is about to be validated
            // against).
            bool allowEmbeddedFiles = part == PdfAConformancePart.Part3;
            var sanitized = new MemoryStream();
            PdfSanitizer.Sanitize(input, sanitized, new SanitizeOptions
            {
                RemoveJavaScript = true,
                RemoveEmbeddedFiles = !allowEmbeddedFiles,
                RemoveLaunchActions = true,
                RemoveOpenAction = true,
                RemoveFormActions = false,  // keep form submissions
                RemoveURIActions = false,
                // ISO 19005 forbids the /AA key itself (A04), whatever it holds, so remove it
                // wholesale — otherwise an /AA of only benign triggers survives the sanitize and
                // the pre-validation below reports A04 as a blocker.
                RemoveAllAdditionalActions = true,
                // Decided above: a signed input reaches this line only when the caller allowed it.
                AllowSignatureInvalidation = true
            });
            sanitized.Position = 0;

            // Step 1b — flatten the transparency Part 1 forbids, when asked. After sanitizing, so the
            // flattener sees the document that will be written, and before the pre-validation, so
            // A09-A14 are judged on the flattened pages. The signature question was settled in step 0.
            if (options.FlattenTransparency && part == PdfAConformancePart.Part1)
            {
                var flattened = new MemoryStream();
                var flattening = PdfTransparencyFlattener.Flatten(sanitized, flattened, new TransparencyFlattenOptions
                {
                    Dpi = options.FlattenDpi > 0 ? options.FlattenDpi : 300,
                    KeepOpaqueContentAsVector = options.FlattenKeepOpaqueContentAsVector,
                    AllowSignedDocument = true,
                });
                result.FlattenedPages = flattening.FlattenedPages;
                if (flattening.FlattenedPages.Count > 0) result._warnings.AddRange(flattening.Warnings);
                sanitized = flattened;
                sanitized.Position = 0;
            }

            // Step 1b' — convert the device colour the output intent does not admit (A16-A18), when asked:
            // into the intent profile's own family, which its header names.
            if (options.ConvertColour)
            {
                string family = IccProfileFamily(options.IccProfile);
                var converted = new MemoryStream();
                try
                {
                    sanitized.Position = 0;
                    if (family == "RGB ")
                        result.ColourConversion = PdfColorSpaceConverter.ConvertToRgb(sanitized, converted,
                            new RgbConversionOptions { OutputIccProfile = options.IccProfile });
                    else if (family == "CMYK")
                        result.ColourConversion = PdfColorSpaceConverter.ConvertToCmyk(sanitized, converted, new CmykConversionOptions
                        {
                            OutputIccProfile = options.IccProfile,
                            ConvertCalRgb = false, ConvertLab = false, ConvertIccBasedRgb = false,   // device-independent: admitted
                        });
                    else
                        result._warnings.Add("The colour was not converted: ConvertColour needs IccProfile, an RGB or CMYK profile, to convert into"
                                             + (family != null ? $" (this one is '{family.Trim()}')" : "") + ".");
                    if (result.ColourConversion != null)
                    {
                        converted.Position = 0;
                        sanitized = converted;
                        foreach (string left in result.ColourConversion.NotConverted)
                            result._warnings.Add("Colour conversion: " + left);
                    }
                }
                catch (ArgumentException ex)
                {
                    result._warnings.Add("The colour was not converted: " + ex.Message);
                }
                sanitized.Position = 0;
            }

            // Step 1c — the annotation repairs that change nothing on screen (PdfAAnnotationRepair):
            // the Print flag where nothing hides the annotation, an /AP trimmed to /N. What it cannot
            // repair the pre-validation below reports and blocks on.
            {
                var repaired = new MemoryStream();
                var annotationRepair = PdfAAnnotationRepair.Repair(sanitized.ToArray(), repaired);
                if (annotationRepair.PrintFlagsSet > 0)
                    result._warnings.Add($"Set the Print flag on {annotationRepair.PrintFlagsSet} annotation(s), " +
                                         $"which PDF/A-{(int)part} requires of every annotation it keeps.");
                if (annotationRepair.AppearancesTrimmed > 0)
                    result._warnings.Add($"Kept only the normal appearance of {annotationRepair.AppearancesTrimmed} " +
                                         "annotation(s); PDF/A forbids rollover and down appearances.");
                if (annotationRepair.Unrevisable > 0)
                    result._warnings.Add($"{annotationRepair.Unrevisable} page(s) hold direct annotations in a direct " +
                                         "array on a page with no object number; those were not repaired.");
                sanitized = repaired;
                sanitized.Position = 0;
            }

            // Step 1d — PDF/A-3 keeps its attachments, so it supplies what A67 asks of them that
            // needs no guess at their content (PdfAEmbeddedFileRepair): both names, a relationship of
            // /Unspecified, application/octet-stream for an unknown type, a place in the catalog's /AF.
            if (part == PdfAConformancePart.Part3)
            {
                var repaired = new MemoryStream();
                var fileRepair = PdfAEmbeddedFileRepair.Repair(sanitized.ToArray(), repaired);
                if (fileRepair.SpecificationsRepaired > 0)
                    result._warnings.Add($"Completed {fileRepair.SpecificationsRepaired} embedded file specification(s): " +
                                         "/F and /UF both, and /AFRelationship /Unspecified where none was given.");
                if (fileRepair.SubtypesSet > 0)
                    result._warnings.Add($"Declared {fileRepair.SubtypesSet} embedded file(s) application/octet-stream, " +
                                         "having no MIME type of their own.");
                if (fileRepair.Associated > 0)
                    result._warnings.Add($"Associated {fileRepair.Associated} embedded file(s) with the document through the catalog's /AF.");
                if (fileRepair.Unrevisable > 0)
                    result._warnings.Add($"{fileRepair.Unrevisable} embedded file(s) could not be repaired: a direct " +
                                         "specification or a direct catalog has no object number to supersede.");
                sanitized = repaired;
                sanitized.Position = 0;
            }

            // Step 2 — find out what would still be wrong, BEFORE claiming anything.
            //
            // Reuses PdfAValidator rather than duplicating its scans; A07 is excluded because it
            // reports the absent PDF/A identifier, which is precisely what this method is about
            // to write. A15-A18 are excluded for the identical reason, one step removed: they are
            // about the /OutputIntents entry this method has not written yet either (A15: multiple
            // intents share one profile; A16-A18: the document's device colour usage is consistent
            // with the intent's class) — checking `sanitized` here would always find "no matching
            // OutputIntent" whatever the supplied profile's class, since the
            // OutputIntent that answer depends on does not exist in this stream yet. Unlike A08
            // (a one-line `options.IccProfile != null` check, cheap enough to answer directly) these
            // need the same colour-usage scan PdfAValidator itself runs, which is not worth
            // duplicating here — so, like transparency and colour conversion, whether the FINAL
            // output actually satisfies A16-A18 is left to a caller who validates the output.
            sanitized.Position = 0;
            try
            {
                // At level B, the level the identifier this converter writes declares — not the
                // input's own letter, whose A or U rules the output does not claim.
                var pre = PdfAValidator.Validate(sanitized, part, PdfAConformanceLevel.B);
                foreach (var violation in pre.Violations)
                {
                    if (!JudgedBeforeWriting(violation)) continue;
                    result._blockers.Add($"{violation.Code}: {violation.Message}");
                }

                // A99 says a check did not run in full. It is not a violation, but a check that did
                // not finish cannot support a conformance claim either — this is PdfXConverter's
                // X99 rule, and before it a document whose font walk ran out of budget came out
                // stamped pdfaid:part with its fonts never examined. A partial run of a check this
                // method sets aside anyway (A16-A18) is set aside with it.
                foreach (var info in pre.Informational)
                {
                    if (!JudgedBeforeWriting(info)) continue;
                    result._blockers.Add($"{info.Code}: {info.Message}");
                }
            }
            catch (Exception ex)
            {
                // A document the validator cannot read is not one this converter can vouch for.
                result._blockers.Add($"the document could not be validated before conversion: {ex.Message}");
            }
            sanitized.Position = 0;

            bool hasProfile = options.IccProfile != null && options.IccProfile.Length > 0;
            if (!hasProfile)
                result._blockers.Add(
                    $"A08: no ICC profile supplied, so the output intent carries no " +
                    $"/DestOutputProfile (ISO 19005-{(int)part} §6.2.2 requires one).");

            result.EmbeddedOutputProfile = hasProfile;
            result.IsConformant = result._blockers.Count == 0;

            bool declare = result.IsConformant || options.DeclareConformanceRegardless;
            result.DeclaredPdfAIdentifier = declare;

            // Step 3 — write XMP metadata declaring the target part, when that claim is warranted.
            var afterXmp = new MemoryStream();
            if (declare)
            {
                string xmpXml = BuildPdfAXmp(part, options.DocumentTitle, options.DocumentCreator);
                PdfMetadataWriter.WriteXmpMetadata(sanitized, afterXmp, xmpXml);
            }
            else
            {
                sanitized.CopyTo(afterXmp);
            }
            afterXmp.Position = 0;

            // Step 4 — add /OutputIntents (sRGB IEC61966-2.1), embedding the ICC
            // profile via /DestOutputProfile when supplied. Shared with PdfXConverter:
            // both standards need an output intent and only the /S subtype differs.
            OutputIntentWriter.Write(afterXmp, output, "GTS_PDFA1",
                "sRGB IEC61966-2.1", options.IccProfile, trapped: null);

            // Set only here, on the one path that actually reaches the caller's stream. Every
            // `return result` above is a refusal that wrote nothing.
            result.OutputWritten = true;
            return result;
        }

        // ----------------------------------------------------------------
        //  XMP generator
        // ----------------------------------------------------------------

        /// <summary>
        /// The PDF/A identification packet for the given part, always declaring conformance level
        /// B (Basic) — the only level this converter attempts, since A (Accessible) and U (Unicode)
        /// both require tagging/Unicode guarantees this converter does not check or produce.
        /// Envelope, escaping and the W3CDTF date live in <see cref="XmpConformancePacket"/>,
        /// shared with the PDF/X converter.
        /// </summary>
        private static string BuildPdfAXmp(PdfAConformancePart part, string title, string creator)
            => XmpConformancePacket.Build(
                schemaPrefix: "pdfaid",
                schemaNamespace: XmpPacket.PdfAIdNamespace,
                schemaProperties: new[]
                {
                    new KeyValuePair<string, string>("part", ((int)part).ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("conformance", "B"),
                },
                title: title,
                creatorTool: creator ?? "Tetitulz.Pdf",
                modifiedUtc: DateTime.UtcNow);

        // ----------------------------------------------------------------
        //  OutputIntents
        // ----------------------------------------------------------------

        // The output intent is Base's PdfIccEmbedder.WriteOutputIntent, through OutputIntentWriter.

    }
}
