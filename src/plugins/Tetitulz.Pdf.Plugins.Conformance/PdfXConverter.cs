// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>Options for <see cref="PdfXConverter.Convert"/>.</summary>
    public sealed class PdfXConversionOptions
    {
        /// <summary>Target conformance level. Defaults to <see cref="PdfXConformanceLevel.X1a"/>.</summary>
        public PdfXConformanceLevel Level { get; set; } = PdfXConformanceLevel.X1a;

        /// <summary>
        /// ICC profile bytes embedded as the output intent's <c>/DestOutputProfile</c>.
        /// ISO 15930 §6.2.3 requires one for X-1a; the later parts accept a registered
        /// characterisation name instead (see <see cref="OutputConditionIdentifier"/>).
        /// </summary>
        public byte[] IccProfile { get; set; }

        /// <summary>
        /// Ignored: the profile stream's <c>/N</c> is the profile's own component count, read from its
        /// header.
        /// </summary>
        /// <remarks>
        /// Written as <c>/N</c> whatever the profile says, an RGB profile under this default of 4
        /// would disagree with its own stream — the mismatch the PDF/A validator's A20 fails.
        /// A value the profile already answers cannot usefully be the caller's.
        /// </remarks>
        [System.Obsolete("Ignored: /N is read from the profile's header. Remove the assignment.")]
        public int IccProfileComponents { get; set; } = 4;

        /// <summary>
        /// The output intent's <c>/OutputConditionIdentifier</c> — a registered characterisation
        /// name where one applies. Required by X11.
        /// </summary>
        public string OutputConditionIdentifier { get; set; } = "Custom";

        /// <summary>
        /// Value for the <c>/Trapped</c> entry X10 requires. <c>null</c> leaves it alone, which
        /// keeps X10 blocking — the PDF default is <c>/Unknown</c>, exactly the non-answer
        /// ISO 15930-1 §6.2.2 refuses, so this is a question only the producer can answer.
        /// </summary>
        public bool? Trapped { get; set; }

        /// <summary>
        /// Give a page declaring neither <c>/TrimBox</c> nor <c>/ArtBox</c> a <c>/TrimBox</c> taken
        /// from its <c>/CropBox</c> (which itself falls back to <c>/MediaBox</c>). Default
        /// <c>true</c>.
        /// </summary>
        /// <remarks>
        /// <b>This asserts the page has no bleed</b> — <c>/TrimBox</c> is the finished size after
        /// trimming, so deriving it from the visible page says the visible page is the finished
        /// one. True of the ordinary document, false of artwork designed to run off the edge, and
        /// nothing in a file with no box distinguishes the two. Turn this off to leave the X05
        /// violation standing for a human instead.
        /// </remarks>
        public bool RepairPageBoxes { get; set; } = true;

        /// <summary>
        /// Delete the <c>/ArtBox</c> from a page carrying both boxes, which §6.2.4 forbids.
        /// Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Opt-in because it discards a statement the producer made, where
        /// <see cref="RepairPageBoxes"/> supplies a missing one. <c>/TrimBox</c> is the box kept:
        /// it states the finished page size, which is what a print workflow imposes on.
        /// </remarks>
        public bool RemoveArtBoxWhenBothPresent { get; set; }

        /// <summary>
        /// Overrides the <c>GTS_PDFXVersion</c> string written to XMP. See
        /// <see cref="PdfXConverter"/> remarks for why this is overridable.
        /// </summary>
        public string GtsPdfXVersion { get; set; }

        /// <summary>Written to <c>dc:title</c>.</summary>
        public string DocumentTitle { get; set; }

        /// <summary>
        /// Embeds the fonts X04 requires. Given the document so far, writes an equivalent document
        /// with font programs embedded and returns <c>true</c> when it changed anything.
        /// <c>null</c> — the default — leaves X04 reported as a blocker.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a delegate rather than a direct call.</b> The embedder that does this well is
        /// <c>PdfFontEmbeddingRemediator</c>, which retrofits <c>/FontFile2</c> / <c>/FontFile3</c>
        /// onto existing font dictionaries without moving a glyph. It lives in
        /// <c>Tetitulz.Pdf.Accessibility</c>, and CLAUDE.md forbids this plugin referencing a
        /// feature. Rather than duplicate it — the DRY violation this repository has been bitten by
        /// — the operation travels here as a delegate, which is the same inversion §5a uses to run
        /// plaintext-only operations on encrypted documents without adding a reference.</para>
        ///
        /// <para>Wire it at the composition root:
        /// <code>
        /// options.EmbedFonts = (input, output) =>
        ///     PdfAccessibility.EmbedMissingFonts(input, output, fontOptions).FontsEmbedded > 0;
        /// </code></para>
        ///
        /// <para><b>Embedding redistributes the font, so the licence is the caller's call</b> — which
        /// is a second reason this is opt-in rather than automatic. Returning <c>false</c>, or
        /// throwing, leaves the input untouched and X04 standing; a throw is additionally reported
        /// on <see cref="PdfXConversionResult.Blockers"/> rather than escaping.</para>
        /// </remarks>
        public Func<Stream, Stream, bool> EmbedFonts { get; set; }

        /// <summary>
        /// Write the PDF/X identifier even when the conversion could not reach conformance.
        /// Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// A false PDF/X claim is worse than a missing one: it reaches a RIP. This exists only for
        /// callers completing the remaining steps themselves.
        /// </remarks>
        public bool DeclareConformanceRegardless { get; set; }

        /// <summary>
        /// Convert a document that carries a digital signature, destroying it. Default
        /// <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <para>Conversion renumbers every object (the sanitize step writes a fresh single-revision
        /// file), so a signature's <c>/ByteRange</c> no longer covers what it signed. The output is
        /// a document whose signature is <i>corrupt</i>, not merely "changed since signed" — and a
        /// PDF/X workflow is exactly where nobody looks at the signature panel again.</para>
        /// <para>The same opt-in shape, and the same reason, as
        /// <c>EncryptedRoundTripOptions.AllowSignatureInvalidation</c>.</para>
        /// </remarks>
        public bool AllowSignatureInvalidation { get; set; }

        /// <summary>
        /// Flatten the transparency PDF/X-1a and X-3 forbid (X09) rather than report it: each page that
        /// uses it is replaced by an opaque image of its separations — <c>/DeviceCMYK</c>, or
        /// <c>/DeviceN</c> keeping each spot ink as a channel of its own — with its text kept as an
        /// invisible layer (<c>PdfTransparencyFlattener</c>). Default <c>false</c>. PDF/X-4 permits
        /// transparency, so it changes nothing there.
        /// </summary>
        /// <remarks>
        /// Off by default because it is lossy: a flattened page's vector artwork and text outlines become
        /// pixels at <see cref="FlattenDpi"/>. RGB content reaches the plates through
        /// <see cref="IccProfile"/> when it is a CMYK output profile, and by the naive inverse of the CMYK
        /// conversion otherwise. The pages it flattened are named in
        /// <see cref="PdfXConversionResult.FlattenedPages"/>.
        /// </remarks>
        public bool FlattenTransparency { get; set; }

        /// <summary>
        /// Convert the colour X08 forbids into CMYK through <see cref="IccProfile"/>, which must then be a CMYK
        /// output profile with <c>B2A</c> tables (<c>PdfColorSpaceConverter.ConvertToCmyk</c>): at X-1a every RGB
        /// and Lab colour, at X-3 and X-4 DeviceRGB alone — device-independent colour is admitted there. Default
        /// <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Off by default because it changes what the document says about its colour: the press's CMYK replaces
        /// the document's own. What it converted and what it left is on
        /// <see cref="PdfXConversionResult.ColourConversion"/>, and anything left stays an X08 blocker. An RGB
        /// output intent (X-3 or X-4) would forbid CMYK and grey instead, and conversion into RGB is not offered.
        /// </remarks>
        public bool ConvertColour { get; set; }

        /// <summary>Resolution a page is flattened at under <see cref="FlattenTransparency"/>. Default 300.</summary>
        public int FlattenDpi { get; set; } = 300;

        /// <summary>
        /// Under <see cref="FlattenTransparency"/>, flatten only where a page paints transparently and
        /// keep the rest of it as vector (<c>TransparencyFlattenOptions.KeepOpaqueContentAsVector</c>).
        /// Off by default. The vector content keeps its own colour, so RGB in it is still RGB.
        /// </summary>
        public bool FlattenKeepOpaqueContentAsVector { get; set; }

        /// <summary>
        /// Remove every graphics state's halftone (<c>/HT</c>, and with it a halftone's
        /// <c>/TransferFunction</c>) and halftone phase (<c>/HTP</c>, <c>/HTO</c>). On by default.
        /// </summary>
        /// <remarks>
        /// What PDF/X says of a halftone's transfer function is in a part of ISO 15930 that was not
        /// available (GAPS.md §12b); output that holds no halftone is safe under every reading, which is
        /// what PDF/X producers commonly write. A RIP screens with its own halftone, so colours are
        /// unchanged; a screen the producer chose is lost, and
        /// <see cref="PdfXConversionResult.HalftonesRemoved"/> counts the graphics states it came from.
        /// </remarks>
        public bool RemoveHalftones { get; set; } = true;

        /// <summary>
        /// Give every optional content configuration a non-empty <c>/Name</c> no other carries, and remove
        /// its <c>/AS</c>, the strictest reading available of what PDF/X asks of it (PDF/A-2's 6.9, without
        /// its <c>/Order</c> rule, which ISO 15930-7:2010 removed). On by default.
        /// </summary>
        public bool RepairOptionalContentConfigurations { get; set; } = true;
    }

    /// <summary>What <see cref="PdfXConverter.Convert"/> achieved.</summary>
    public sealed class PdfXConversionResult
    {
        /// <summary>
        /// True when nothing <see cref="PdfXValidator"/> checks is left outstanding.
        /// </summary>
        /// <remarks>
        /// <b>Not a conformance certificate.</b> ISO 15930 has requirements this validator does
        /// not implement — see GAPS.md §12b — and this converter embeds fonts, flattens transparency
        /// and converts colour only when asked.
        /// </remarks>
        public bool IsConformant { get; internal set; }

        /// <summary>Whether the <c>GTS_PDFXVersion</c> identifier was written.</summary>
        public bool DeclaredPdfXIdentifier { get; internal set; }

        /// <summary>Whether an ICC profile was embedded as <c>/DestOutputProfile</c>.</summary>
        public bool EmbeddedOutputProfile { get; internal set; }

        /// <summary>Whether <c>/Trapped</c> was written to the <c>/Info</c> dictionary.</summary>
        public bool SetTrapped { get; internal set; }

        /// <summary>
        /// Pages that declared neither <c>/TrimBox</c> nor <c>/ArtBox</c> and were given a
        /// <c>/TrimBox</c> derived from the visible page. Non-zero means a geometric assumption was
        /// made — see <see cref="PdfXConversionOptions.RepairPageBoxes"/>.
        /// </summary>
        public int PagesGivenTrimBox { get; internal set; }

        /// <summary>Pages that carried both boxes and had the <c>/ArtBox</c> removed.</summary>
        public int PagesArtBoxRemoved { get; internal set; }

        /// <summary>
        /// Whether the caller's <see cref="PdfXConversionOptions.EmbedFonts"/> ran and reported that
        /// it embedded something. <c>false</c> when none was supplied, when it declined, or when it
        /// threw — the last also appears in <see cref="Blockers"/>.
        /// </summary>
        public bool EmbeddedFonts { get; internal set; }

        /// <summary>
        /// Zero-based indices of the pages <see cref="PdfXConversionOptions.FlattenTransparency"/>
        /// replaced by an opaque image of their separations; empty when none was.
        /// </summary>
        public IReadOnlyList<int> FlattenedPages { get; internal set; } = Array.Empty<int>();

        /// <summary>
        /// What <see cref="PdfXConversionOptions.ConvertColour"/> converted and what it left; null when it did not run.
        /// </summary>
        public ColourConversionResult ColourConversion { get; internal set; }

        /// <summary>
        /// Graphics states <see cref="PdfXConversionOptions.RemoveHalftones"/> took a halftone or a
        /// halftone phase from.
        /// </summary>
        public int HalftonesRemoved { get; internal set; }

        /// <summary>
        /// Optional content configurations <see cref="PdfXConversionOptions.RepairOptionalContentConfigurations"/>
        /// named, or took an <c>/AS</c> from.
        /// </summary>
        public int OptionalContentConfigurationsRepaired { get; internal set; }

        /// <summary>
        /// Pages whose geometry was left as it was, each with the reason.
        /// </summary>
        /// <remarks>
        /// Such a page keeps its X05 violation, so this never hides a failure — <see cref="Blockers"/>
        /// still reports it. What this adds is the part the validator cannot know: <i>why the
        /// converter declined</i>, and which option would change that.
        /// </remarks>
        public IReadOnlyList<string> PageBoxesLeftAlone => _pageBoxesLeftAlone;

        internal List<string> _pageBoxesLeftAlone = new List<string>();

        /// <summary>Reasons the output is not conformant. Empty when <see cref="IsConformant"/>.</summary>
        public IReadOnlyList<string> Blockers => _blockers;

        internal readonly List<string> _blockers = new List<string>();

        /// <summary>
        /// Things the conversion did that a caller would want to know about but which do not stop
        /// the output conforming — currently, destroying a signature under
        /// <see cref="PdfXConversionOptions.AllowSignatureInvalidation"/>.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="Blockers"/> on purpose: a blocker means "this is not PDF/X",
        /// and folding an advisory into that list would make a conformant document report itself
        /// non-conformant.
        /// </remarks>
        public IReadOnlyList<string> Warnings => _warnings;

        internal readonly List<string> _warnings = new List<string>();

        internal void AddBlocker(string reason) => _blockers.Add(reason);
        internal void AddWarning(string reason) => _warnings.Add(reason);
    }

    /// <summary>
    /// Moves a PDF towards PDF/X (ISO 15930) conformance and reports what it could not do.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> A caller that learns from <see cref="PdfXValidator"/> that a file is
    /// non-conforming has a supported way to act on it, as `PdfAConverter` is for PDF/A.</para>
    ///
    /// <para><b>What it does:</b> strips the actions X07 forbids, writes <c>/Trapped</c> when the
    /// caller supplies it (X10), repairs page geometry (X05), adds a <c>GTS_PDFX</c> output intent
    /// with the supplied ICC profile (X02, X11), and writes XMP declaring <c>GTS_PDFXVersion</c>
    /// (X03).</para>
    ///
    /// <para><b>Page geometry is the one repair that makes an assumption</b>, and it is split in
    /// two accordingly: a page declaring no box gets a <c>/TrimBox</c> from its visible extent
    /// (<see cref="PdfXConversionOptions.RepairPageBoxes"/>, default on — it supplies a missing
    /// statement, but asserts the page has no bleed), while a page declaring *both* boxes is left
    /// alone unless the caller opts in
    /// (<see cref="PdfXConversionOptions.RemoveArtBoxWhenBothPresent"/>) — deleting one discards a
    /// statement the producer made. Both counts are on the result.</para>
    ///
    /// <para><b>What it does only when asked, and reports otherwise:</b> font embedding (X04, a
    /// caller-supplied embedder), colour conversion (X08, <see cref="PdfXConversionOptions.ConvertColour"/>
    /// into the output intent's CMYK profile) and transparency flattening (X09,
    /// <see cref="PdfXConversionOptions.FlattenTransparency"/>). Each changes the document's content, and
    /// whatever any of them leaves stays a blocker — half-doing one would produce a file that claims
    /// PDF/X and is not.</para>
    ///
    /// <para><b>The identifier is withheld unless the result is actually conformant</b>, on the
    /// same rule as `PdfAConverter` and the accessibility writers: a false claim a downstream
    /// reader cannot check is worse than a missing one, and for PDF/X the downstream reader is a
    /// RIP.</para>
    ///
    /// <para><b>One value here is recollection, and is therefore overridable.</b> The
    /// <c>GTS_PDFXVersion</c> strings written by default (<c>PDF/X-1a:2001</c>,
    /// <c>PDF/X-3:2002</c>, <c>PDF/X-4</c>) satisfy the prefixes <see cref="PdfXValidator"/>
    /// already checks, but the exact year suffixes are not quoted from ISO 15930 here. GAPS.md
    /// §12b's stopping rule is about not inventing *checks*; this is a written value, it is
    /// verified against the shipped validator, and
    /// <see cref="PdfXConversionOptions.GtsPdfXVersion"/> exists so a caller with the standard in
    /// hand can correct it without a code change.</para>
    /// </remarks>
    public static class PdfXConverter
    {
        /// <summary>Converts <paramref name="input"/> towards PDF/X and reports the outcome.</summary>
        public static PdfXConversionResult Convert(Stream input, Stream output,
            PdfXConversionOptions options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            options = options ?? new PdfXConversionOptions();

            var result = new PdfXConversionResult();

            // PDF/X-6 is validated only in part (PdfXValidator.X6.cs), so no conversion could be shown
            // to reach it, and the identifier is never written without that.
            if (options.Level == PdfXConformanceLevel.X6)
            {
                result.AddBlocker("Conversion to PDF/X-6 is not offered: ISO 15930-9 was not available, and the " +
                                  "validator checks PDF/X-6 only in part, so a converted file could not be shown to conform.");
                return result;
            }

            // Step 0 — refuse what this converter cannot convert, as a reported blocker.
            //
            // Left to PdfSanitizer, one step down and in a different assembly, encrypted input
            // would surface as a PdfEncryptedDocumentException thrown out of Convert — a caller
            // reading Blockers to learn what went wrong would get an exception from a type it never
            // called — and a SIGNED document would be renumbered like any other, its signature
            // silently destroyed, with nothing on the result saying so.
            if (!ConverterInputInspection.TryInspect(input, out bool isEncrypted, out bool isSigned, out string readError))
            {
                result.AddBlocker($"The input could not be read as a PDF: {readError}");
                return result;
            }

            if (isEncrypted)
            {
                // Not merely unsupported: PdfXValidator's X01 forbids encryption outright, so a
                // converted-and-still-encrypted file could never conform. Decrypting here would
                // also mean referencing Tetitulz.Pdf.Encryption from a plugin whose whole job is
                // metadata rewriting.
                result.AddBlocker(
                    "The input is encrypted. PDF/X (X01) forbids encryption, so this cannot be " +
                    "converted in place — decrypt it first (Tetitulz.Pdf.Encryption), or run the " +
                    "conversion inside PdfEncryptedRoundTrip.Run, whose output would then have to " +
                    "be left unencrypted to conform.");
                return result;
            }

            if (isSigned && !options.AllowSignatureInvalidation)
            {
                result.AddBlocker(
                    "The input carries a digital signature, and conversion rewrites every object, " +
                    "so the signature would verify as corrupt rather than as modified. Set " +
                    "PdfXConversionOptions.AllowSignatureInvalidation to convert anyway.");
                return result;
            }

            if (isSigned)
                result.AddWarning(
                    "The input was signed and the signature has been invalidated by conversion " +
                    "(AllowSignatureInvalidation was set).");

            // Step 1 — strip what X07 forbids. Form submissions and URI actions are left alone:
            // X07 is about additional-actions and script, not about every interactive feature.
            var sanitized = new MemoryStream();
            PdfSanitizer.Sanitize(input, sanitized, new SanitizeOptions
            {
                RemoveJavaScript = true,
                RemoveEmbeddedFiles = true,    // X16: "No embedded files" (GAPS.md §12b)
                RemoveLaunchActions = true,
                RemoveOpenAction = true,
                RemoveFormActions = false,
                RemoveURIActions = false,
                // X07 forbids an /AA entry; remove it wholesale rather than only its dangerous
                // action types, so a benign /AA does not survive to fail validation.
                RemoveAllAdditionalActions = true,
                // Decided above: a signed input reaches this line only when the caller allowed it.
                AllowSignatureInvalidation = true,
            });
            sanitized.Position = 0;

            // Step 2 — page geometry (X05). Before the output intent only so each step reads the
            // previous step's bytes; both append incremental updates and neither depends on the
            // other. A document whose pages already conform passes through byte-identically.
            Stream boxed = sanitized;
            if (options.RepairPageBoxes || options.RemoveArtBoxWhenBothPresent)
            {
                var repairedStream = new MemoryStream();
                var repaired = PageBoxRepair.Repair(sanitized, repairedStream,
                    options.RepairPageBoxes, options.RemoveArtBoxWhenBothPresent);
                repairedStream.Position = 0;

                result.PagesGivenTrimBox    = repaired.TrimBoxesAdded;
                result.PagesArtBoxRemoved   = repaired.ArtBoxesRemoved;
                result._pageBoxesLeftAlone  = repaired.Skipped;
                boxed = repairedStream;
            }

            // Step 2a — leave out what PDF/X may forbid where the rule was not available (X26, X27):
            // halftones and halftone phases, a configuration's missing or shared /Name and its /AS.
            if (options.RemoveHalftones || options.RepairOptionalContentConfigurations)
            {
                boxed.Position = 0;
                var leftOut = new MemoryStream();
                var printState = PrintStateRepair.Repair(boxed, leftOut,
                    options.RemoveHalftones, options.RepairOptionalContentConfigurations);
                leftOut.Position = 0;
                boxed = leftOut;

                result.HalftonesRemoved = printState.StatesChanged;
                result.OptionalContentConfigurationsRepaired = printState.ConfigurationsChanged;
                if (printState.HalftonesRemoved > 0 || printState.HalftonePhasesRemoved > 0)
                    result.AddWarning(
                        $"Removed the halftone from {printState.HalftonesRemoved} and the halftone phase from " +
                        $"{printState.HalftonePhasesRemoved} graphics state(s): a RIP screens with its own, and " +
                        "what PDF/X allows of a halftone was not available (RemoveHalftones).");
                if (printState.ConfigurationsNamed > 0 || printState.AutoStatesRemoved > 0)
                    result.AddWarning(
                        $"Named {printState.ConfigurationsNamed} optional content configuration(s) and removed /AS " +
                        $"from {printState.AutoStatesRemoved} (RepairOptionalContentConfigurations).");
                if (printState.Unreadable > 0 || printState.TooDeep > 0)
                    result.AddWarning(
                        $"{printState.Unreadable} object(s) could not be read and {printState.TooDeep} structure(s) " +
                        "were nested too deep to read, so halftones and configurations in them were left as they were.");
            }

            // Step 2b — fonts (X04), if the caller supplied an embedder. Before the output intent
            // so the pre-validation below sees the embedded result; a failure here must leave the
            // document exactly as it was rather than half-embedded, so the new bytes are adopted
            // only once the delegate has returned successfully and produced something.
            if (options.EmbedFonts != null)
            {
                var embedded = new MemoryStream();
                try
                {
                    boxed.Position = 0;
                    if (options.EmbedFonts(boxed, embedded) && embedded.Length > 0)
                    {
                        embedded.Position = 0;
                        boxed = embedded;
                        result.EmbeddedFonts = true;
                    }
                }
                catch (Exception ex)
                {
                    // Reported rather than thrown: the caller's embedder failing is a reason X04
                    // stays open, not a reason to lose the conversion done so far.
                    result._blockers.Add($"font embedding failed: {ex.Message}");
                }

                if (!result.EmbeddedFonts) boxed.Position = 0;
            }

            // Step 2b' — convert the colour X08 forbids, when asked, into CMYK through the output intent's
            // profile: before the flattening (which would otherwise separate the RGB itself) and before the
            // pre-validation below, which then judges the converted colour.
            if (options.ConvertColour)
            {
                if (options.IccProfile == null)
                    result.AddBlocker("ConvertColour needs IccProfile, a CMYK output profile to convert into.");
                else
                {
                    bool x1a = options.Level == PdfXConformanceLevel.X1a;
                    var converted = new MemoryStream();
                    try
                    {
                        boxed.Position = 0;
                        result.ColourConversion = PdfColorSpaceConverter.ConvertToCmyk(boxed, converted, new CmykConversionOptions
                        {
                            OutputIccProfile = options.IccProfile,
                            ConvertDeviceRgb = true,
                            ConvertCalRgb = x1a,
                            ConvertLab = x1a,
                            ConvertIccBasedRgb = x1a,
                        });
                        converted.Position = 0;
                        boxed = converted;
                        foreach (string left in result.ColourConversion.NotConverted)
                            result.AddWarning("Colour conversion: " + left);
                    }
                    catch (ArgumentException ex)
                    {
                        // An RGB intent, or a profile with no B2A table: nothing is converted, and X08 reports
                        // whatever is still there.
                        result.AddWarning("The colour was not converted: " + ex.Message);
                        boxed.Position = 0;
                    }
                }
            }

            // Step 2c — flatten the transparency X-1a and X-3 forbid, when asked, to the page's
            // separations: CMYK, its spot inks as DeviceN channels. Before the output intent so the
            // pre-validation judges the flattened pages; after the fonts, which flattened pages lose.
            if (options.FlattenTransparency && options.Level != PdfXConformanceLevel.X4)
            {
                boxed.Position = 0;
                var source = new MemoryStream();
                boxed.CopyTo(source);
                var flattenOptions = new TransparencyFlattenOptions
                {
                    Dpi = options.FlattenDpi > 0 ? options.FlattenDpi : 300,
                    ColorSpace = FlattenedColorSpace.Cmyk,
                    OutputIccProfile = options.IccProfile,
                    KeepOpaqueContentAsVector = options.FlattenKeepOpaqueContentAsVector,
                    AllowSignedDocument = true,
                };
                var flattened = new MemoryStream();
                TransparencyFlattenResult flattening;
                try
                {
                    flattening = PdfTransparencyFlattener.Flatten(new MemoryStream(source.ToArray(), false), flattened, flattenOptions);
                }
                catch (ArgumentException)
                {
                    // The output profile is not a CMYK one the plates can convert through — an RGB
                    // intent, say. The plates then take RGB content by the naive inverse, as without one.
                    flattenOptions.OutputIccProfile = null;
                    flattened = new MemoryStream();
                    flattening = PdfTransparencyFlattener.Flatten(new MemoryStream(source.ToArray(), false), flattened, flattenOptions);
                    result.AddWarning("The output intent's profile is not a CMYK output profile, so RGB content " +
                                      "reached the flattened plates by the naive inverse of the CMYK conversion.");
                }
                result.FlattenedPages = flattening.FlattenedPages;
                if (flattening.FlattenedPages.Count > 0)
                    foreach (string w in flattening.Warnings) result.AddWarning(w);
                flattened.Position = 0;
                boxed = flattened;
            }

            // Step 3 — add the output intent and /Trapped, because both are things the
            // validator checks and both are fixable here. Validating before writing them would
            // report blockers this converter is about to remove.
            var withIntent = new MemoryStream();
            var written = OutputIntentWriter.Write(boxed, withIntent, "GTS_PDFX",
                options.OutputConditionIdentifier ?? "Custom",
                options.IccProfile, options.Trapped);
            withIntent.Position = 0;

            result.EmbeddedOutputProfile = written.EmbeddedProfile;
            result.SetTrapped = written.SetTrapped;

            // Step 4 — what is still outstanding. X03 is excluded: it reports the absent
            // GTS_PDFXVersion, which is exactly what step 5 is about to write.
            try
            {
                var pre = PdfXValidator.Validate(withIntent, options.Level);
                foreach (var violation in pre.Violations)
                {
                    if (violation.Code == "X03") continue;
                    result._blockers.Add($"{violation.Code}: {violation.Message}");
                }
                // X99 says a check did not run. It is not a violation, but it does mean the
                // verdict is partial — so it cannot support a conformance claim either.
                foreach (var info in pre.Informational)
                    result._blockers.Add($"{info.Code}: {info.Message}");
            }
            catch (Exception ex)
            {
                result._blockers.Add($"the document could not be validated before conversion: {ex.Message}");
            }
            withIntent.Position = 0;

            result.IsConformant = result._blockers.Count == 0;

            bool declare = result.IsConformant || options.DeclareConformanceRegardless;
            result.DeclaredPdfXIdentifier = declare;

            // Step 5 — the identifier, when the claim is warranted.
            if (declare)
            {
                string xmp = BuildPdfXXmp(
                    options.GtsPdfXVersion ?? DefaultVersionString(options.Level),
                    options.DocumentTitle);
                PdfMetadataWriter.WriteXmpMetadata(withIntent, output, xmp);
            }
            else
            {
                withIntent.CopyTo(output);
            }

            return result;
        }

        /// <summary>
        /// The <c>GTS_PDFXVersion</c> value written for each level. These satisfy the prefixes
        /// <see cref="PdfXValidator"/> checks (<c>"PDF/X-1a:"</c>, <c>"PDF/X-3:"</c>,
        /// <c>"PDF/X-4"</c>) — pinned by a round-trip test rather than asserted from memory.
        /// </summary>
        internal static string DefaultVersionString(PdfXConformanceLevel level) => level switch
        {
            PdfXConformanceLevel.X1a => "PDF/X-1a:2001",
            PdfXConformanceLevel.X3  => "PDF/X-3:2002",
            PdfXConformanceLevel.X4  => "PDF/X-4",
            _ => "PDF/X-1a:2001",
        };

        /// <summary>
        /// The PDF/X identification packet. Envelope, escaping and the W3CDTF date live in
        /// <see cref="XmpConformancePacket"/>, shared with the PDF/A converter.
        /// </summary>
        private static string BuildPdfXXmp(string version, string title)
            => XmpConformancePacket.Build(
                schemaPrefix: "pdfx",
                schemaNamespace: "http://ns.adobe.com/pdfx/1.3/",
                schemaProperties: new[]
                {
                    new KeyValuePair<string, string>("GTS_PDFXVersion", version),
                },
                title: title,
                creatorTool: null,   // PDF/X does not set one; PDF/A does
                modifiedUtc: DateTime.UtcNow);

    }
}
