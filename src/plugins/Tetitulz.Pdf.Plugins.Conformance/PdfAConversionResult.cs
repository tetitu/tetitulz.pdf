// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// Options for <see cref="PdfAConverter.ConvertToPdfA1b(System.IO.Stream, System.IO.Stream, PdfAConversionOptions)"/>.
    /// </summary>
    public sealed class PdfAConversionOptions
    {
        /// <summary>Written to <c>dc:title</c>.</summary>
        public string DocumentTitle { get; set; }

        /// <summary>Written to <c>xmp:CreatorTool</c>. Defaults to <c>Tetitulz.Pdf</c>.</summary>
        public string DocumentCreator { get; set; }

        /// <summary>
        /// ICC profile bytes (e.g. sRGB IEC61966-2.1) embedded as the output intent's
        /// <c>/DestOutputProfile</c>. ISO 19005-1 §6.2.2 requires it, so without one the output
        /// cannot be conformant and the PDF/A identifier is withheld — see
        /// <see cref="DeclareConformanceRegardless"/>.
        /// </summary>
        public byte[] IccProfile { get; set; }

        /// <summary>
        /// Ignored: the profile stream's <c>/N</c> is the profile's own component count, read from its
        /// header — the value <c>PdfAValidator</c>'s A16-A18 cross-check against the device colour
        /// spaces the content uses (ISO 19005-1 §6.2.3.3).
        /// </summary>
        /// <remarks>
        /// Written as <c>/N</c> whatever the profile says, a CMYK profile under this default of 3
        /// would disagree with its own stream, which A20 fails. A value the profile already
        /// answers cannot usefully be the caller's.
        /// </remarks>
        [System.Obsolete("Ignored: /N is read from the profile's header. Remove the assignment.")]
        public int IccProfileComponents { get; set; } = 3;

        /// <summary>
        /// Write the <c>pdfaid</c> identifier even when the conversion could not reach
        /// conformance. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <para>Exists for callers who are embedding fonts or supplying the profile themselves
        /// in a later step and want the identifier written now. It is <b>not</b> a way to make a
        /// file conformant — a reader that trusts the identifier over a non-conformant file is
        /// exactly the harm this defaults to preventing.</para>
        /// </remarks>
        public bool DeclareConformanceRegardless { get; set; }

        /// <summary>
        /// Convert a <b>signed</b> document, invalidating its signature. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Conversion rewrites every object, so the signature's <c>/ByteRange</c> no longer covers
        /// what was signed and the signature verifies as <i>corrupt</i> rather than as modified —
        /// a worse outcome than a refusal, because it looks like tampering. Without this flag a
        /// signed input is reported as a blocker; with it, the invalidation is reported as a
        /// warning. Mirrors <c>PdfXConversionOptions.AllowSignatureInvalidation</c>.
        /// </remarks>
        public bool AllowSignatureInvalidation { get; set; }

        /// <summary>
        /// Flatten the transparency PDF/A-1 forbids (A09–A14) rather than report it as a blocker:
        /// each page that uses it is replaced by an opaque image of itself, its text kept as an
        /// invisible layer (<c>PdfTransparencyFlattener</c>). Default <c>false</c>. Parts 2 and 3
        /// permit transparency, so it changes nothing there.
        /// </summary>
        /// <remarks>
        /// Off by default because it is lossy: a flattened page's vector artwork and text outlines
        /// become pixels at <see cref="FlattenDpi"/>. The pages it flattened are named in
        /// <see cref="PdfAConversionResult.FlattenedPages"/>.
        /// </remarks>
        public bool FlattenTransparency { get; set; }

        /// <summary>Resolution a page is flattened at under <see cref="FlattenTransparency"/>. Default 300.</summary>
        public int FlattenDpi { get; set; } = 300;

        /// <summary>
        /// Under <see cref="FlattenTransparency"/>, flatten only where a page paints transparently and
        /// keep the rest of it as vector (<c>TransparencyFlattenOptions.KeepOpaqueContentAsVector</c>).
        /// Off by default.
        /// </summary>
        public bool FlattenKeepOpaqueContentAsVector { get; set; }

        /// <summary>
        /// Convert the device colour the output intent does not admit (A16–A18) into the intent's own family:
        /// under an RGB <see cref="IccProfile"/>, DeviceCMYK into that profile's RGB
        /// (<c>PdfColorSpaceConverter.ConvertToRgb</c>); under a CMYK one, DeviceRGB into its CMYK
        /// (<c>PdfColorSpaceConverter.ConvertToCmyk</c>, which needs its <c>B2A</c> tables). Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Off by default because it changes the document's colour values. Device-independent colour (ICC-based,
        /// Lab, the Cal spaces) is admitted whatever the intent and is left alone, as is DeviceGray. What was
        /// converted and left is on <see cref="PdfAConversionResult.ColourConversion"/>; whether the output then
        /// satisfies A16–A18 is for a caller who validates it, as without this.
        /// </remarks>
        public bool ConvertColour { get; set; }
    }

    /// <summary>
    /// What <see cref="PdfAConverter"/> actually achieved.
    /// </summary>
    /// <remarks>
    /// <para>The identifier is written only when the result conforms: a document with
    /// non-embedded fonts stamped <c>pdfaid:part 1</c> would <i>declare</i> PDF/A-1b while
    /// <see cref="PdfAValidator"/> rejects it — a false claim a downstream reader has no way to
    /// detect. The accessibility side applies the same rule: a document that could not be tagged
    /// is never made to claim it was.</para>
    /// </remarks>
    public sealed class PdfAConversionResult
    {
        /// <summary>
        /// True when the conversion reached PDF/A-1b as far as this converter can verify —
        /// nothing left that <see cref="PdfAValidator"/> reports, and an embedded output profile.
        /// </summary>
        /// <remarks>
        /// <b>Not a conformance certificate.</b> The validator checks A01–A18 (A09-A14
        /// transparency, A15-A18 colour-OutputIntent consistency, both detection-only — see the
        /// remarks on this class and on <see cref="PdfAConverter"/>), which covers what ISO
        /// 19005-1 requires beyond the basics this converter's own pipeline satisfies by
        /// construction. This converter removes transparency and converts colour only when asked
        /// (<see cref="PdfAConversionOptions.FlattenTransparency"/>, <see cref="PdfAConversionOptions.ConvertColour"/>),
        /// and it does not judge A15–A18 before writing them.
        /// </remarks>
        public bool IsConformant { get; internal set; }

        /// <summary>
        /// Whether anything at all was written to the caller's output stream.
        /// </summary>
        /// <remarks>
        /// <b>Check this before anything else.</b> The converter refuses an encrypted input, and a
        /// signed one unless <see cref="PdfAConversionOptions.AllowSignatureInvalidation"/> is set —
        /// and a refusal RETURNS rather than throwing, leaving the output stream untouched. Without
        /// this flag a refusal is indistinguishable from a successful conversion of an empty
        /// document: <see cref="IsConformant"/> is false in both cases, and a caller that only reads
        /// <see cref="Blockers"/> cannot tell "converted, not conformant" from "not converted".
        /// <see cref="PdfAConverter.ConvertToPdfA1b(System.IO.Stream, System.IO.Stream, string, string, byte[])"/>
        /// reads it to re-raise the refusal, because that overload has no other channel.
        /// </remarks>
        public bool OutputWritten { get; internal set; }

        /// <summary>Whether the <c>pdfaid</c> identifier was written to the output.</summary>
        public bool DeclaredPdfAIdentifier { get; internal set; }

        /// <summary>Whether an ICC profile was embedded as <c>/DestOutputProfile</c>.</summary>
        public bool EmbeddedOutputProfile { get; internal set; }

        /// <summary>
        /// Reasons the output is not conformant, in the converter's own words — one per
        /// blocking condition. Empty when <see cref="IsConformant"/>.
        /// </summary>
        public IReadOnlyList<string> Blockers => _blockers;

        /// <summary>
        /// Things the caller should know that did not stop the conversion — today, a signature
        /// invalidated because <see cref="PdfAConversionOptions.AllowSignatureInvalidation"/> was
        /// set. Empty when there is nothing to say.
        /// </summary>
        public IReadOnlyList<string> Warnings => _warnings;

        /// <summary>
        /// Zero-based indices of the pages <see cref="PdfAConversionOptions.FlattenTransparency"/>
        /// replaced by an opaque image of themselves; empty when none was.
        /// </summary>
        public IReadOnlyList<int> FlattenedPages { get; internal set; } = System.Array.Empty<int>();

        /// <summary>
        /// What <see cref="PdfAConversionOptions.ConvertColour"/> converted and what it left; null when it did not run.
        /// </summary>
        public Tetitulz.Pdf.Base.Operations.Metadata.ColourConversionResult ColourConversion { get; internal set; }

        internal readonly List<string> _blockers = new List<string>();
        internal readonly List<string> _warnings = new List<string>();
    }
}
