// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Images;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    /// <summary>
    /// Main facade class providing all high-level PDF operations.
    ///
    /// <para><b>Thread-safety:</b> All methods on this facade are safe to call
    /// concurrently from multiple threads <i>provided each call operates on
    /// independent input/output streams or file paths</i>.  Concurrent calls
    /// to the same <see cref="Stream"/> instance are NOT safe and will lead
    /// to undefined output, just like with any other code that shares a
    /// <see cref="Stream"/> across threads.</para>
    ///
    /// <para><b>Stream ownership:</b> Methods that take a <c>Stream input</c>
    /// parameter buffer the input internally if it is not seekable, but they
    /// do <i>not</i> dispose the caller's stream.  The caller retains
    /// ownership of every stream it passes in.  Methods that take a
    /// <c>string path</c> open the file via <see cref="File.OpenRead(string)"/> /
    /// <see cref="File.Create(string)"/> internally and clean up via <c>using</c>.</para>
    ///
    /// <para><b>Operation ordering for compound workflows:</b></para>
    /// <list type="bullet">
    ///   <item><b>Sign → Encrypt</b>: encrypting after signing invalidates the
    ///   signature.  Encrypt first, then sign.  When LTV/DSS is required,
    ///   apply <c>EmbedLtv</c> on the sign pass — re-encrypting later will
    ///   break the DSS chain.</item>
    ///   <item><b>Sign → FlattenForm / Redact</b>: any structural change to
    ///   the document body invalidates an existing signature.  Flatten or
    ///   redact first, then sign.</item>
    ///   <item><b>Redact → FlattenForm</b>: order matters — flattening before
    ///   redacting bakes form-field values into the content stream where the
    ///   redactor cannot reach them.  Redact first.</item>
    ///   <item><b>AddAttachment → Encrypt</b>: add attachments before
    ///   encrypting. <c>AddAttachment</c> refuses an encrypted document,
    ///   because the embedded file would be written in clear text where a
    ///   reader expects ciphertext.</item>
    ///   <item><b>OCR / Tag / Linearize</b>: linearisation must be the LAST
    ///   step.  Any modification after linearisation breaks the
    ///   /Linearized hint dictionary and viewers fall back to sequential
    ///   loading.</item>
    /// </list>
    /// </summary>
    public static partial class PdfOperations
    {
        // The class summary above lives in this file only: the compiler concatenates the summaries
        // of every partial, so a copy on each would carry it dozens of times in one member's
        // generated XML documentation. One claim to keep straight (attachments "inherit /EFF" when
        // added to an encrypted document) was false in every copy.
        //
        // NOTE: this facade calls PdfPluginHost's *render* hook (InvokePageRendered) but never its
        // before/after-*process* hooks (InvokeBeforeProcess / InvokeAfterProcess). Wiring those
        // hooks into the Stream-in/Stream-out operations is open work, not something this file
        // quietly does.

        // ---------------------------------------------------------------
        //  Facade: image extraction, visual diff, outline JSON, optimize metrics
        // ---------------------------------------------------------------

        /// <summary>Counts image XObjects on a page.</summary>
        public static int CountImagesOnPage(Stream input, int pageIndex) =>
            PdfImageExporter.CountImages(input, pageIndex);

        /// <summary>Counts image XObjects on a page of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password, never a <c>password = null</c>
        /// appended to the signature above — that is a binary break, and the shipped overload keeps
        /// its original call path so its behaviour is untouched. See the remarks on
        /// <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        public static int CountImagesOnPage(Stream input, int pageIndex, string password) =>
            WithDecrypted(input, password, s => PdfImageExporter.CountImages(s, pageIndex));

        /// <summary>Extracts the N-th image XObject from a page of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password: appending <c>password = null</c> to
        /// the signature above is a binary break, and the shipped overload keeps its original call
        /// path so its behaviour is untouched. Where the original ends in optional parameters the
        /// password sits <i>before</i> them — a required parameter cannot follow an optional one.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="imageIndex">
        /// Zero-based index into that page's image XObjects, in the order
        /// <see cref="CountImagesOnPage(Stream,int,string)"/> counts them.
        /// </param>
        public static PdfImageExporter.PdfExtractedImage ExtractImage(
            Stream input, int pageIndex, int imageIndex, string password) =>
            WithDecrypted(input, password, s => PdfImageExporter.ExtractImage(s, pageIndex, imageIndex));

        /// <summary>Extracts the N-th image XObject from a page. Returns JPEG bytes when the
        /// source stream uses DCTDecode, JPEG2000 bytes for JPXDecode, and PNG otherwise.</summary>
        public static PdfImageExporter.PdfExtractedImage ExtractImage(Stream input, int pageIndex, int imageIndex) =>
            PdfImageExporter.ExtractImage(input, pageIndex, imageIndex);

        /// <summary>
        /// Extracts the N-th image XObject from a page as a PNG whatever its filter — a JPEG or
        /// JPEG 2000 codestream decoded to its pixels, the latter by the in-house decoder.
        /// </summary>
        public static PdfImageExporter.PdfExtractedImage ExtractImageAsPng(Stream input, int pageIndex, int imageIndex) =>
            PdfImageExporter.ExtractImageAsPng(input, pageIndex, imageIndex);

        /// <summary>Extracts the N-th image XObject from a page of an <b>encrypted</b> document as a PNG whatever its filter.</summary>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        /// <param name="imageIndex">Zero-based index into that page's image XObjects.</param>
        /// <param name="password">User or owner password.</param>
        public static PdfImageExporter.PdfExtractedImage ExtractImageAsPng(
            Stream input, int pageIndex, int imageIndex, string password) =>
            WithDecrypted(input, password, s => PdfImageExporter.ExtractImageAsPng(s, pageIndex, imageIndex));

        /// <summary>Renders both PDFs page-by-page and counts pixel differences.</summary>
        public static PdfVisualDiff.Result ComparePdfVisual(Stream left, Stream right,
            int dpi = PdfVisualDiff.DefaultDiffDpi, float thresholdPct = 2f, int pixelTolerance = 8) =>
            PdfVisualDiff.Compare(left, right, dpi, thresholdPct, pixelTolerance);

        /// <summary>Exports the PDF outline tree as indented JSON.</summary>
        public static string ExportOutlineJson(Stream input) => ExportOutlineJson(input, null);

        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static string ExportOutlineJson(Stream input, string password)
            => WithDecrypted(input, password, PdfOutlineJson.Export);

        /// <summary>Replaces the outline tree with the supplied JSON representation.</summary>
        public static void ImportOutlineJson(Stream input, Stream output, string json) =>
            PdfOutlineJson.Import(input, output, json);

        /// <summary>Opens a PDF; if parsing fails, runs PdfRepair and retries once.</summary>
        public static PdfDocument OpenWithRepair(Stream input) =>
            PdfDocumentOpener.OpenWithRepair(input);

        // ---------------------------------------------------------------
        //  Facade: PDF 2.0 feature readers (thumbs, DParts, wrappers, GeoPDF)
        // ---------------------------------------------------------------

        /// <summary>Reads the /Thumb page stream, or null if none present.</summary>
        public static PdfThumbnailManager.ThumbnailInfo ReadPageThumbnail(Stream input, int pageIndex) =>
            PdfThumbnailManager.ReadThumbnail(input, pageIndex);

        /// <summary>Reads the /Thumb page stream of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password, never a <c>password = null</c>
        /// appended to the signature above — that is a binary break, and the shipped overload keeps
        /// its original call path so its behaviour is untouched. See the remarks on
        /// <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        /// <param name="pageIndex">Zero-based page index.</param>
        public static PdfThumbnailManager.ThumbnailInfo ReadPageThumbnail(
            Stream input, int pageIndex, string password) =>
            WithDecrypted(input, password, s => PdfThumbnailManager.ReadThumbnail(s, pageIndex));

        /// <summary>Generates /Thumb streams for all pages and writes an incremental-update PDF.</summary>
        public static int GeneratePageThumbnails(Stream input, Stream output, int dpi = PdfThumbnailManager.DefaultThumbnailDpi) =>
            PdfThumbnailManager.GenerateThumbnails(input, output, dpi);

        /// <summary>Reads the /DPartRoot document-parts hierarchy.</summary>
        public static PdfDPartReader.DPartNode ReadDocumentParts(Stream input) =>
            PdfDPartReader.ReadRoot(input);

        /// <summary>Reads the /DPartRoot hierarchy of an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password, never a <c>password = null</c>
        /// appended to the signature above — that is a binary break, and the shipped overload keeps
        /// its original call path so its behaviour is untouched. See the remarks on
        /// <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        public static PdfDPartReader.DPartNode ReadDocumentParts(Stream input, string password) =>
            WithDecrypted(input, password, PdfDPartReader.ReadRoot);

        /// <summary>
        /// Detects an unencrypted wrapper document (ISO 32000-2 §7.6.7): one whose file
        /// specification marked <c>/AFRelationship /EncryptedPayload</c> carries an encrypted
        /// payload, reported with the cryptographic filter its <c>/EP</c> names.
        /// </summary>
        public static PdfUnencryptedWrapperReader.WrapperInfo InspectUnencryptedWrapper(Stream input) =>
            PdfUnencryptedWrapperReader.Inspect(input);

        /// <summary>Reads page-level GeoPDF viewports (/VP with optional /Measure).</summary>
        public static System.Collections.Generic.IReadOnlyList<PdfGeoReader.Viewport> ReadGeoViewports(Stream input) =>
            PdfGeoReader.ReadViewports(input);

        /// <summary>Reads GeoPDF viewports from an <b>encrypted</b> document.</summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// A separate overload with a <b>required</b> password, never a <c>password = null</c>
        /// appended to the signature above — that is a binary break, and the shipped overload keeps
        /// its original call path so its behaviour is untouched. See the remarks on
        /// <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        /// <param name="input">Stream holding the encrypted PDF.</param>
        public static System.Collections.Generic.IReadOnlyList<PdfGeoReader.Viewport> ReadGeoViewports(
            Stream input, string password) =>
            WithDecrypted(input, password, PdfGeoReader.ReadViewports);
    }

    /// <summary>Summary of a font referenced in a PDF document.</summary>
    public sealed class PdfFontSummary
    {
        public string ResourceName { get; }
        public string BaseFont     { get; }
        public string Subtype      { get; }

        public PdfFontSummary(string resourceName, string baseFont, string subtype)
        {
            ResourceName = resourceName;
            BaseFont     = baseFont;
            Subtype      = subtype;
        }

        public override string ToString() => $"{ResourceName} / {BaseFont} ({Subtype})";
    }
}



