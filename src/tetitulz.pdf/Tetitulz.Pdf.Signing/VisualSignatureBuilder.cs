// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Signs a PDF with a visible signature stamp that includes a JPEG image.
    /// </summary>
    public sealed class VisualSignatureBuilder
    {
        /// <summary>The name the image-only stamp's inner form gives the image in its <c>/Resources</c>.</summary>
        private const string StampImageResourceName = "SigImg";

        /// <summary>
        /// Signs a PDF with a visible JPEG image stamp on the specified page.
        /// </summary>
        /// <param name="inputPath">Path to the input PDF.</param>
        /// <param name="outputPath">Path for the signed output PDF.</param>
        /// <param name="pfxPath">Path to the .pfx/.p12 certificate.</param>
        /// <param name="password">Certificate password.</param>
        /// <param name="imagePath">Path to the JPEG image for the signature stamp.</param>
        /// <param name="pageNumber">Page number (1-based) to place the signature.</param>
        /// <param name="x">X coordinate of the signature rectangle (lower-left).</param>
        /// <param name="y">Y coordinate of the signature rectangle (lower-left).</param>
        /// <param name="width">Width of the signature rectangle.</param>
        /// <param name="height">Height of the signature rectangle.</param>
        /// <param name="reason">Reason for signing.</param>
        /// <param name="location">Location of signing.</param>
        public static void Sign(
            string inputPath, string outputPath,
            string pfxPath, string password,
            string imagePath,
            int pageNumber = 1,
            float x = 50, float y = 50,
            float width = 200, float height = 80,
            string? reason = null, string? location = null)
        {
            byte[] imageBytes = File.ReadAllBytes(imagePath);

            using (var input = File.OpenRead(inputPath))
            using (var output = File.Create(outputPath))
            {
                SignInternal(input, output, pfxPath, password, imageBytes, null, pageNumber, x, y, width, height, reason, location, null);
            }
        }

        /// <summary>
        /// Signs a PDF with a visible JPEG image stamp on the specified page.
        /// </summary>
        public static void Sign(
            Stream input, Stream output,
            string pfxPath, string password,
            byte[] jpegImageData,
            int pageNumber = 1,
            float x = 50, float y = 50,
            float width = 200, float height = 80,
            string? reason = null, string? location = null)
        {
            SignInternal(input, output, pfxPath, password, jpegImageData, null, pageNumber, x, y, width, height, reason, location, null);
        }

        /// <summary>
        /// Signs a PDF with a visible signature that <b>draws text</b> — signer name, date,
        /// <c>Reason</c>, <c>Location</c>, and an optional logo — on the specified page.
        /// </summary>
        /// <param name="input">Source PDF. Not disposed.</param>
        /// <param name="output">Receives the signed document.</param>
        /// <param name="pfxPath">Path to the .pfx/.p12 certificate.</param>
        /// <param name="password">Certificate password.</param>
        /// <param name="appearance">
        /// What to draw. <see cref="SignatureAppearanceOptions.Width"/> and
        /// <see cref="SignatureAppearanceOptions.Height"/> give the stamp's size, so this overload
        /// takes no width/height of its own.
        /// </param>
        /// <param name="pageNumber">Page number (1-based) to place the signature.</param>
        /// <param name="x">X coordinate of the signature rectangle (lower-left).</param>
        /// <param name="y">Y coordinate of the signature rectangle (lower-left).</param>
        /// <param name="reason">Reason recorded in the signature dictionary.</param>
        /// <param name="location">Location recorded in the signature dictionary.</param>
        /// <exception cref="PdfException">
        /// The appearance could not be drawn — most often because the text needs a composite font
        /// and neither <see cref="SignatureAppearanceOptions.FontProvider"/> nor
        /// <see cref="SignatureAppearanceOptions.AllowSystemFontSubstitution"/> yielded one.
        /// <b>Nothing is signed in that case</b>: a signature whose visible block is blank or shows
        /// the wrong text is worse than a refusal, because it is the half a reader trusts.
        /// </exception>
        /// <remarks>
        /// <para><b>The image-only overloads are unchanged and are not deprecated by this one.</b>
        /// They stretch the JPEG across the whole rectangle, which is what a scanned-signature stamp
        /// wants; this one lays out a text block with the logo in a column beside it. Neither is a
        /// special case of the other, so delegating one to the other would silently re-lay-out every
        /// existing caller's stamp.</para>
        ///
        /// <para><b><paramref name="reason"/> and <paramref name="location"/> are the signature
        /// *dictionary* entries, and are separate from
        /// <see cref="SignatureAppearanceOptions.Reason"/> / <see cref="SignatureAppearanceOptions.Location"/>,
        /// which are what gets *drawn*.</b> They are usually the same text and this method does not
        /// assume so: §12.8.1's `/Reason` is a machine-readable claim a verifier reports, while the
        /// appearance is a picture, and a caller localising the drawn block must not thereby change
        /// what the signature asserts.</para>
        ///
        /// <para>For Hebrew or Arabic, set <see cref="SignatureAppearanceOptions.BaseDirection"/>
        /// explicitly rather than leaving it on auto — first-strong detection is wrong for a line
        /// that opens with a date or a Latin name inside otherwise right-to-left text.</para>
        /// </remarks>
        public static void Sign(
            Stream input, Stream output,
            string pfxPath, string password,
            SignatureAppearanceOptions appearance,
            int pageNumber = 1,
            float x = 50, float y = 50,
            string? reason = null, string? location = null)
        {
            if (appearance == null) throw new ArgumentNullException(nameof(appearance));

            SignInternal(input, output, pfxPath, password, null, appearance, pageNumber, x, y,
                         (float)appearance.Width, (float)appearance.Height, reason, location, null);
        }

        /// <summary>
        /// Draws a visible signature <b>and</b> signs under the caller's full
        /// <see cref="SignatureParameters"/> — the one call that gets an appearance together with
        /// LTV, a timestamp, a PAdES baseline or a DocMDP permission.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this overload exists.</b> The overloads above build a
        /// <c>SignatureParameters</c> from scratch carrying five fields, so every other setting is
        /// left at its default: <c>EmbedLtv</c>, <c>TimestampAuthorityUrl</c>,
        /// <c>TimestampNetwork</c>, <c>DigestAlgorithm</c>, <c>SignatureAlgorithmOid</c>,
        /// <c>EstimatedSignatureSize</c>, <c>SigningTime</c>, <c>ContactInfo</c>,
        /// <c>UseEtsiPadesSubFilter</c>, <c>DocMdpPermission</c>, <c>UsageRights</c> and any
        /// <c>ApplyPadesBaseline</c> already applied. A caller who wants a visible B-LT
        /// signature passes the parameters here, or gets a visible <i>basic</i> one, with no error — the appearance is drawn either
        /// way, so nothing looks wrong until a verifier reports no revocation data.</para>
        ///
        /// <para><b>Three fields on <paramref name="parameters"/> are overwritten, on the instance
        /// you pass.</b> <c>CertificatePath</c> and <c>CertificatePassword</c> come from this
        /// method's own arguments, and <c>SignatureFieldName</c> is <b>minted here</b> — it has to
        /// match the widget this method just wrote, so a caller-chosen name would leave the
        /// signature and its appearance naming different fields. Everything else is used exactly as
        /// supplied. The instance is not cloned, because <c>PadesBaselineLevel</c> has no public
        /// setter and a field-by-field copy would quietly drop it.</para>
        ///
        /// <para><b>Ordering is unchanged and is PAdES', not a limitation here:</b> the appearance
        /// revision is written first, the signature second, and <c>PdfSigner</c> appends the
        /// <c>/DSS</c> third when <c>EmbedLtv</c> is set, and a B-LTA document timestamp after it when
        /// the parameters ask for that level.</para>
        /// </remarks>
        /// <param name="input">The document to sign.</param>
        /// <param name="output">Receives the signed document.</param>
        /// <param name="pfxPath">PKCS#12 file holding the signing key.</param>
        /// <param name="password">Password for <paramref name="pfxPath"/>.</param>
        /// <param name="appearance">What the visible block draws.</param>
        /// <param name="parameters">
        /// Everything else about the signature. Its three owned fields are overwritten — see the
        /// remarks.
        /// </param>
        /// <param name="pageNumber">1-based page to place the appearance on.</param>
        /// <param name="x">Left edge, in points from the page's left.</param>
        /// <param name="y">Bottom edge, in points from the page's bottom.</param>
        public static void Sign(
            Stream input, Stream output,
            string pfxPath, string password,
            SignatureAppearanceOptions appearance,
            SignatureParameters parameters,
            int pageNumber = 1,
            float x = 50, float y = 50)
        {
            if (appearance == null) throw new ArgumentNullException(nameof(appearance));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            SignInternal(input, output, pfxPath, password, null, appearance, pageNumber, x, y,
                         (float)appearance.Width, (float)appearance.Height,
                         parameters.Reason, parameters.Location, parameters);
        }

        /// <summary>
        /// Signs with a visible <b>image</b> stretched over the rectangle — a JPEG, or a PNG with its
        /// transparency kept (a scanned signature on a clear background) — under the caller's full
        /// <see cref="SignatureParameters"/>, including an <see cref="SignatureParameters.ExternalSigner"/>
        /// whose key never leaves a smart card, token or HSM.
        /// </summary>
        /// <param name="input">The document to sign.</param>
        /// <param name="output">Receives the signed document.</param>
        /// <param name="imageData">The stamp: JPEG or PNG bytes.</param>
        /// <param name="parameters">
        /// How to sign: <c>ExternalSigner</c>, or <c>CertificatePath</c>/<c>CertificateStream</c> with a
        /// password, and everything else (<c>Reason</c>, <c>Location</c>, a timestamp, LTV, PAdES, DocMDP).
        /// Its <c>SignatureFieldName</c> is overwritten with the name of the field this method adds.
        /// </param>
        /// <param name="pageNumber">1-based page to place the signature on.</param>
        /// <param name="x">Left edge, in user-space units from the page's origin.</param>
        /// <param name="y">Bottom edge, in user-space units from the page's origin.</param>
        /// <param name="width">The rectangle's width; positive and finite.</param>
        /// <param name="height">The rectangle's height; positive and finite.</param>
        /// <exception cref="PdfException">The image is neither a readable JPEG nor a readable PNG; nothing is written.</exception>
        public static void Sign(
            Stream input, Stream output,
            byte[] imageData,
            SignatureParameters parameters,
            int pageNumber,
            float x, float y,
            float width, float height)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (imageData == null) throw new ArgumentNullException(nameof(imageData));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            Core.Util.NumericArguments.RequireFinite(x, nameof(x));
            Core.Util.NumericArguments.RequireFinite(y, nameof(y));
            Core.Util.NumericArguments.RequirePositive(width, nameof(width));
            Core.Util.NumericArguments.RequirePositive(height, nameof(height));
            SignInternal(input, output, null, null, imageData, null, pageNumber, x, y, width, height,
                         parameters.Reason, parameters.Location, parameters);
        }

        /// <summary>
        /// Signs with a visible block that <b>draws text</b> — the signer's name, the date, the
        /// reason, the location, an optional logo — under the caller's full
        /// <see cref="SignatureParameters"/>, including an <see cref="SignatureParameters.ExternalSigner"/>
        /// whose key never leaves a smart card, token or HSM.
        /// </summary>
        /// <param name="input">The document to sign.</param>
        /// <param name="output">Receives the signed document.</param>
        /// <param name="appearance">
        /// What the block draws, and its size (<see cref="SignatureAppearanceOptions.Width"/>,
        /// <see cref="SignatureAppearanceOptions.Height"/>). Its reason and location are what is
        /// <i>drawn</i>; the signature dictionary's are <paramref name="parameters"/>'.
        /// </param>
        /// <param name="parameters">
        /// How to sign: <c>ExternalSigner</c>, or <c>CertificatePath</c>/<c>CertificateStream</c> with a
        /// password, and everything else (<c>Reason</c>, <c>Location</c>, a timestamp, LTV, PAdES, DocMDP).
        /// Its <c>SignatureFieldName</c> is overwritten with the name of the field this method adds.
        /// </param>
        /// <param name="pageNumber">1-based page to place the signature on.</param>
        /// <param name="x">Left edge, in user-space units from the page's origin.</param>
        /// <param name="y">Bottom edge, in user-space units from the page's origin.</param>
        /// <exception cref="PdfException">The appearance could not be drawn; nothing is signed.</exception>
        public static void Sign(
            Stream input, Stream output,
            SignatureAppearanceOptions appearance,
            SignatureParameters parameters,
            int pageNumber,
            float x, float y)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (appearance == null) throw new ArgumentNullException(nameof(appearance));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            Core.Util.NumericArguments.RequireFinite(x, nameof(x));
            Core.Util.NumericArguments.RequireFinite(y, nameof(y));
            Core.Util.NumericArguments.RequirePositive(appearance.Width, nameof(appearance), nameof(SignatureAppearanceOptions.Width));
            Core.Util.NumericArguments.RequirePositive(appearance.Height, nameof(appearance), nameof(SignatureAppearanceOptions.Height));
            SignInternal(input, output, null, null, null, appearance, pageNumber, x, y,
                         (float)appearance.Width, (float)appearance.Height,
                         parameters.Reason, parameters.Location, parameters);
        }

        private static void SignInternal(
            Stream input, Stream output,
            string? pfxPath, string? password,
            byte[]? jpegImageData,
            SignatureAppearanceOptions? appearance,
            int pageNumber,
            float x, float y,
            float width, float height,
            string? reason, string? location,
            SignatureParameters? parameters)
        {
            // Every overload's numbers, checked here once: '{0:F2}' writes NaN and Infinity into the
            // /Rect and the cm operator as they are.
            Core.Util.NumericArguments.RequireFinite(x, nameof(x));
            Core.Util.NumericArguments.RequireFinite(y, nameof(y));
            Core.Util.NumericArguments.RequirePositive(width, nameof(width));
            Core.Util.NumericArguments.RequirePositive(height, nameof(height));

            // We need to create an intermediate PDF that has the visual appearance,
            // then sign it. The approach:
            // 1. Read the original PDF
            // 2. Create an incremental update that adds:
            //    - An image XObject for the JPEG
            //    - An appearance stream for the signature widget
            //    - The signature field/widget with /Rect and /AP
            // 3. Use the Core PdfSigner to sign the result

            // Capture a single stable ID so the field name in the widget and the
            // SignatureFieldName in PdfSigner always match.
            string fieldName = $"VisualSig{DateTime.UtcNow.Ticks}";

            // Ensure the input is seekable; buffer to a MemoryStream if not.
            if (!input.CanSeek)
            {
                var buf = new MemoryStream();
                input.CopyTo(buf);
                input = buf;
            }
            input.Position = 0;

            // First, create a temp PDF with the visual appearance added via incremental update
            using (var tempMs = new MemoryStream())
            {
                // Copy original
                input.CopyTo(tempMs);

                // Parse to get structure. Through PdfDocument, which refuses a document with no
                // catalog here — before anything is written — rather than letting it surface as a
                // page number "out of range".
                tempMs.Position = 0;
                using var document = PdfDocument.Open(tempMs);
                var parser = document.Parser;
                var trailer = parser.Trailer;

                if (trailer[PdfName.Encrypt] != null)
                    throw new PdfException("Cannot add a visual signature to an encrypted PDF. Decrypt the document first.");

                int maxObjNum = parser.CrossReferenceTable.GetMaxObjectNumber();

                // The target page, by the page tree every reader walks. §7.7.3.2 lets a /Kids entry be
                // a page OR another /Pages node, and this indexed the root node's /Kids: in any
                // balanced tree page N was an intermediate node, the widget went into that node's
                // /Annots where no reader looks, and a page past the root's own child count was
                // refused as out of range.
                var root = document.Catalog.Dictionary;
                if (pageNumber < 1 || pageNumber > document.Pages.Count)
                    throw new PdfException($"Page number {pageNumber} is out of range.");

                var pageDict = document.Pages[pageNumber - 1].Dictionary;
                int pageObjNum = pageDict.ObjectNumber;

                if (pageObjNum <= 0)
                    throw new PdfException($"Page {pageNumber} could not be resolved to an indirect object. Inline page dictionaries are not supported.");

                // Object numbers come off a running counter rather than fixed offsets from
                // maxObjNum, because the appearance path allocates a number of objects only
                // SignatureAppearanceGenerator knows (a form, plus a font and its descendants, plus
                // a logo and its inner form). For the image-only path the counter hands out the
                // image (after a PNG's soft mask), then its inner form, the appearance and the
                // widget, and /Size is one past the widget.
                int nextObjNum       = maxObjNum + 1;
                int catalogObjNum    = SigningHelpers.ObjectNumberOf(trailer[PdfName.Root]);

                // A DIRECT /Root — `/Root << /Type /Catalog … >>` — has no object number to
                // supersede, and ObjectNumberOf answers 0, which §7.5.4 reserves as the head of
                // the free list. Without this the writer emitted `0 0 obj` and an xref entry for
                // it, so the updated catalog was written but unreachable: a malformed file that
                // reported success. The page above is already refused for the same reason
                // ("Inline page dictionaries are not supported"); the catalog was not.
                if (catalogObjNum <= 0)
                    throw new PdfException(
                        "/Root is not an indirect reference, so the catalog cannot be superseded by " +
                        "an incremental update. Object 0 is reserved as the head of the free list " +
                        "and cannot carry the updated catalog.");

                // The form this revision extends. The field name needs no uniqueness pass: it is minted
                // from the clock above and carries no period, and a mutation that dropped the pass here
                // changed nothing — so there is none.
                var acroDict = SignatureFieldRegistration.FormOf(root[PdfName.AcroForm], parser.ResolveObject);

                // §7.5.6 — the page and the catalog already exist, so this revision supersedes them
                // only by re-declaring them at the generation they already carry. Written as 0
                // against a gen>0 object it supersedes nothing and a strict reader shows the page
                // without the signature widget it just added.
                int pageGen = Core.Writing.ExistingObjectGeneration.Of(
                    parser.CrossReferenceTable, pageObjNum);
                int catalogGen = Core.Writing.ExistingObjectGeneration.Of(
                    parser.CrossReferenceTable, catalogObjNum, trailer[PdfName.Root]);

                // The parser already located the startxref — use it directly.
                long prevXrefOffset = parser.StartXrefOffset;

                // …but verify it before appending. The third writer that builds its own trailer,
                // and it would otherwise reproduce the same outcome as the other two: an appended
                // revision naming a handful of objects, no /Prev to chain to, and a document that
                // reopens with a catalog and no pages while its signature still verifies. Fixing
                // only PdfSigner is the "landed one level short" pattern this repository records.
                SigningHelpers.RefuseUnnameableCrossReferenceChain(tempMs, prevXrefOffset);

                // Write incremental update
                long appendStart = tempMs.Length;
                tempMs.Position = appendStart;
                var writer = new PdfWriter(tempMs);

                int appearanceObjNum;
                if (appearance != null)
                {
                    // The appearance path. Everything this writes — the form, the font, its
                    // descendants and `/ToUnicode`, a logo — is allocated and built by the
                    // generator; this method only has to place the objects and point `/AP /N` at
                    // the form it hands back.
                    var (form, sideObjects) = SignatureAppearanceGenerator.Build(
                        appearance, ref nextObjNum, out string appearanceError);

                    if (form == null)
                        throw new PdfException(
                            "The signature appearance could not be drawn, so no signature was " +
                            "applied: " + appearanceError);

                    foreach (var (objNum, obj) in sideObjects)
                        writer.WriteIndirectObject(objNum, 0, obj);

                    appearanceObjNum = form.ObjectNumber;
                    writer.WriteIndirectObject(appearanceObjNum, 0, form);
                }
                else
                {
                    // Read the frame header, and REFUSE anything that is not a JPEG: a payload
                    // GetJpegDimensions cannot parse, written into a /DCTDecode stream anyway at a
                    // 100x100 fallback, would be a stamp every reader fails to decode, with no error
                    // anywhere in the pipeline.
                    //
                    // It reads here rather than at the top of the method because only this path has
                    // a JPEG at all — the appearance path hands its logo to the generator, which
                    // does its own. Nothing has been written to `output` yet either way, so a
                    // refusal is still a refusal.
                    // Exactly one of the two appearance inputs is supplied, and which one selects
                    // this branch — so a null here is a caller reaching SignInternal with neither,
                    // not a JPEG that failed to parse. Those two deserve different messages.
                    byte[] logoBytes = jpegImageData
                        ?? throw new ArgumentNullException(nameof(jpegImageData),
                            "The image-only path needs JPEG or PNG bytes; supply them, or use the " +
                            "SignatureAppearanceOptions overload instead.");

                    // 1. Write the image XObject (and a PNG's soft mask).
                    int imageObjNum = WriteStampImage(writer, logoBytes, ref nextObjNum);

                    // 2. The inner form that scales the image over the box and draws it. The
                    // appearance names this form, never the image: Acrobat does not validate a
                    // signature whose appearance names an image under /XObject
                    // (Core.Writing.SignatureAppearanceImage).
                    int innerFormObjNum = nextObjNum++;
                    string drawImage = string.Format(CultureInfo.InvariantCulture,
                        "q {0:F2} 0 0 {1:F2} 0 0 cm /{2} Do Q", width, height, StampImageResourceName);
                    writer.WriteIndirectObject(innerFormObjNum, 0, SignatureAppearanceImage.InnerForm(
                        innerFormObjNum, width, height, StampImageResourceName, imageObjNum, drawImage));

                    // 3. The appearance stream: a form XObject that draws the inner form.
                    appearanceObjNum = nextObjNum++;
                    byte[] apBytes = Encoding.ASCII.GetBytes(SignatureAppearanceImage.Invocation);

                    var apStream = new PdfStream();
                    apStream.Set(PdfName.Type, PdfName.XObject);
                    apStream.Set(PdfName.Subtype, PdfName.Form);
                    apStream.Set(PdfName.BBox, CreateRectArray(0, 0, width, height));
                    apStream.Set(PdfName.Length, new PdfNumber(apBytes.Length));

                    var apResources = new PdfDictionary();
                    apResources.Set(PdfName.XObject, SignatureAppearanceImage.AppearanceXObjects(innerFormObjNum));
                    apStream.Set(PdfName.Resources, apResources);
                    apStream.RawData = apBytes;

                    writer.WriteIndirectObject(appearanceObjNum, 0, apStream);
                }

                int sigFieldObjNum = nextObjNum++;   // Signature field/widget
                int newSize        = nextObjNum;     // one past the highest new object number

                // 3. Write signature dictionary placeholder (will be replaced by PdfSigner)
                // We don't write it here - PdfSigner handles this.
                // Instead, we write the sig field with appearance, pointing to V as the sig dict.

                // 4. Write signature field / widget with visual appearance
                long sigFieldOffset = tempMs.Position;
                writer.WriteRaw($"{sigFieldObjNum} 0 obj\n");
                writer.WriteRaw("<< /Type /Annot");
                writer.WriteRaw(" /Subtype /Widget");
                writer.WriteRaw(" /FT /Sig");
                writer.WriteRaw($" /T ({fieldName})");
                writer.WriteRaw(string.Format(CultureInfo.InvariantCulture,
                    " /Rect [{0:F2} {1:F2} {2:F2} {3:F2}]", x, y, x + width, y + height));
                writer.WriteRaw($" /F {PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked}");
                writer.WriteRaw($" /P {pageObjNum} {pageGen} R");
                // Appearance dictionary
                writer.WriteRaw($" /AP << /N {appearanceObjNum} 0 R >>");
                writer.WriteRaw(" >>\n");
                writer.WriteRaw("endobj\n");

                // 5. Write updated page with annotation reference
                // Resolved, like every other read in this method. The loop below SKIPS the page's
                // own /Annots and writes a replacement built from this array, so reading it as
                // absent — which GetArray does for the legal /Annots 12 0 R — erased every
                // annotation on the page: links, comments and any other widget, destroyed by
                // adding a visible signature to the page.
                var pageAnnotsArray = parser.ResolveObject(pageDict[PdfName.Annots]) as PdfArray
                                      ?? pageDict.GetArray(PdfName.Annots);
                long pageOffset = tempMs.Position;
                writer.WriteRaw($"{pageObjNum} {pageGen} obj\n");
                writer.WriteRaw("<<");
                foreach (var kvp in pageDict)
                {
                    if (kvp.Key == PdfName.Annots) continue; // replaced below with updated array
                    writer.WriteRaw(" ");
                    writer.WriteObject(kvp.Key);
                    writer.WriteRaw(" ");
                    writer.WriteObject(kvp.Value);
                }
                // Build Annots array
                writer.WriteRaw(" /Annots [");
                if (pageAnnotsArray != null)
                {
                    for (int i = 0; i < pageAnnotsArray.Count; i++)
                    {
                        writer.WriteObject(pageAnnotsArray[i]);
                        writer.WriteRaw(" ");
                    }
                }
                writer.WriteRaw($"{sigFieldObjNum} 0 R]");
                writer.WriteRaw(" >>\n");
                writer.WriteRaw("endobj\n");

                // 6. Write updated catalog with AcroForm — the form through the one builder the signer
                // and the timestamper use, so the three cannot disagree about what it says.
                var catalog = PdfDictionary.CloneShallow(root);
                catalog.Set(PdfName.AcroForm, SignatureFieldRegistration.FormWithSignature(
                    acroDict, SignatureFieldRegistration.FieldsOf(acroDict, parser.ResolveObject),
                    new PdfReference(sigFieldObjNum, 0)));

                // Header and body written separately, not through WriteIndirectObject: that call
                // records the object for the gen-0 entries the xref loop below collects, and the
                // catalog's entry has to carry its own generation.
                long catalogOffset = tempMs.Position;
                writer.WriteRaw($"{catalogObjNum} {catalogGen} obj\n");
                writer.WriteObject(catalog);
                writer.WriteRaw("\nendobj\n");

                // 7. Write xref
                var xrefEntries = new List<(int objNum, int gen, long offset)>();
                // The writer tracked imageObjNum and appearanceObjNum via WriteIndirectObject
                foreach (var kvp in writer.ObjectOffsets)
                    xrefEntries.Add((kvp.Key, 0, kvp.Value));
                xrefEntries.Add((sigFieldObjNum, 0, sigFieldOffset));
                xrefEntries.Add((pageObjNum, pageGen, pageOffset));
                xrefEntries.Add((catalogObjNum, catalogGen, catalogOffset));

                long xrefOffset = writer.WriteXrefTable(xrefEntries);

                // 8. Write trailer.
                // ONE implementation, in Core.Writing.PdfTrailerBuilder — including the guard
                // against an unusable /Prev, which this site lacked. prevXrefOffset comes from
                // parser.StartXrefOffset, which is 0 whenever the parser had to recover the file by
                // scanning; writing /Prev 0 then makes the original revision unreachable.
                var newTrailer = PdfTrailerBuilder.ForAppendedRevision(
                    trailer, newSize, prevXrefOffset, xrefOffset);
                writer.WriteTrailer(newTrailer, xrefOffset);

                // Now use PdfSigner to sign this intermediate PDF
                tempMs.Position = 0;
                var signer = new PdfSigner();
                // The caller's parameters when supplied, so EmbedLtv / TSA / PAdES baseline /
                // DocMDP survive; a fresh one otherwise, which is the historical five-field shape.
                // Not cloned: PadesBaselineLevel has no public setter, so a field-by-field copy
                // would silently drop an ApplyPadesBaseline the caller had already made.
                var sigParams = parameters ?? new SignatureParameters();

                // The three this method owns. SignatureFieldName in particular is minted above and
                // must match the widget just written — a caller-chosen name would leave the
                // signature and its appearance naming different fields.
                // The PKCS#12 overloads own the certificate; the image-and-parameters overload passes
                // none, so the caller's ExternalSigner, CertificatePath or CertificateStream stands.
                if (pfxPath != null)
                {
                    sigParams.CertificatePath = pfxPath;
                    sigParams.CertificatePassword = password;
                }
                sigParams.SignatureFieldName = fieldName;
                sigParams.Reason = reason;
                sigParams.Location = location;

                signer.Sign(tempMs, output, sigParams);
            }
        }

        /// <summary>
        /// Writes the stamp image as an image XObject and returns its object number: a PNG through
        /// <see cref="PngImageDecoder"/>, its alpha as a real <c>/SMask</c> (§11.6.5.3; a fully opaque
        /// one is left out), or a JPEG as it is, under <c>/DCTDecode</c>.
        /// </summary>
        /// <exception cref="PdfException">The bytes are neither a PNG nor a JPEG this library can describe.</exception>
        private static int WriteStampImage(PdfWriter writer, byte[] imageBytes, ref int nextObjNum)
        {
            if (PngImageDecoder.TryDecode(imageBytes, out DecodedPng png))
            {
                int maskObjNum = 0;
                if (png.Alpha != null && Array.Exists(png.Alpha, a => a != 255))
                {
                    maskObjNum = nextObjNum++;
                    writer.WriteIndirectObject(maskObjNum, 0,
                        FlateImage(png.Width, png.Height, PdfName.DeviceGray, png.Alpha, null));
                }
                int pngObjNum = nextObjNum++;
                writer.WriteIndirectObject(pngObjNum, 0,
                    FlateImage(png.Width, png.Height, PdfName.DeviceRGB, png.Rgb, maskObjNum > 0 ? maskObjNum : (int?)null));
                return pngObjNum;
            }

            // Read the frame header, and REFUSE anything that is not a JPEG: a payload the inspector
            // cannot parse, written under /DCTDecode anyway, would be a stamp no reader can decode.
            if (!RasterImageInspector.TryInspectJpeg(imageBytes, out RasterImageInfo frame)
                || frame.Width <= 0 || frame.Height <= 0)
                throw new PdfException(
                    "The signature stamp image is neither a readable PNG nor a readable JPEG (no SOI/SOF " +
                    "marker found), so it cannot be embedded. Supply a PNG, or a baseline or progressive JPEG.");

            // The colour space and the /Decode are the frame's, by the one rule for every writer that
            // embeds a JPEG (Core.Imaging.JpegColorSpace): a fixed /DeviceRGB would read a grayscale
            // scanned-ink signature's samples as RGB triples and render noise; a four-component frame
            // is CMYK only under Adobe's APP14 marker, and then its samples are inverted, so it takes
            // the inverting /Decode or renders as a negative; any other four-component frame is refused.
            string colorSpace = JpegColorSpace.Of(frame, out string refused);
            if (colorSpace == null)
                throw new PdfException("The signature stamp JPEG cannot be embedded: " + refused + ".");

            int imageObjNum = nextObjNum++;
            var imageDict = new PdfStream();
            imageDict.Set(PdfName.Type, PdfName.XObject);
            imageDict.Set(PdfName.Subtype, PdfName.Image);
            imageDict.Set(PdfName.Width, new PdfNumber(frame.Width));
            imageDict.Set(PdfName.Height, new PdfNumber(frame.Height));
            imageDict.Set(PdfName.ColorSpace, new PdfName(colorSpace));
            if (JpegColorSpace.DecodeFor(frame) is double[] decode)
                imageDict.Set(PdfName.Decode, PdfArray.OfNumbers(decode));
            imageDict.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            imageDict.Set(PdfName.Filter, PdfName.DCTDecode);
            imageDict.Set(PdfName.Length, new PdfNumber(imageBytes.Length));
            imageDict.RawData = imageBytes;
            writer.WriteIndirectObject(imageObjNum, 0, imageDict);
            return imageObjNum;
        }

        /// <summary>An 8-bit image XObject of raw samples, Flate-compressed, with an optional soft mask.</summary>
        private static PdfStream FlateImage(int width, int height, PdfName colorSpace, byte[] samples, int? softMaskObjNum)
        {
            var image = new PdfStream();
            image.Set(PdfName.Type, PdfName.XObject);
            image.Set(PdfName.Subtype, PdfName.Image);
            image.Set(PdfName.Width, new PdfNumber(width));
            image.Set(PdfName.Height, new PdfNumber(height));
            image.Set(PdfName.ColorSpace, colorSpace);
            image.Set(PdfName.BitsPerComponent, new PdfNumber(8));
            image.RawData = Core.IO.Filters.FlateEncodeFilter.Encode(samples);
            image.Set(PdfName.Filter, PdfName.FlateDecode);
            image.Set(PdfName.Length, new PdfNumber(image.RawData.Length));
            if (softMaskObjNum.HasValue) image.Set(PdfName.SMask, new PdfReference(softMaskObjNum.Value, 0));
            return image;
        }

        // Fallback reported when no SOF marker is found (truncated header, EOI/SOS first, or a
        // non-JPEG payload). Parsing never throws; it degrades to this placeholder square, which
        // is written as the image XObject's /Width and /Height.
        private const int UnknownJpegDimension = 100;

        internal static void GetJpegDimensions(byte[] jpegData, out int width, out int height)
        {
            // Kept for callers that want the historical "never fails" shape; the embedder uses
            // TryReadJpegFrame, which reports the failure instead of substituting a square.
            TryReadJpegFrame(jpegData, out width, out height, out _);
        }


        /// <summary>
        /// Reads width, height and the colour-component count from the JPEG frame header (SOF),
        /// through <see cref="RasterImageInspector"/> — the one JPEG marker walk.
        /// Returns <c>false</c> — leaving the placeholder dimensions and 0 components — when the
        /// payload has no SOI or no SOF, i.e. when it is not a JPEG this library can describe.
        /// </summary>
        /// <remarks>
        /// <c>Core.Imaging.RasterImageInspector</c> is the one marker walk, in Core because Signing,
        /// Builder and DocxToPdf all read JPEG headers and none of them may reference
        /// <c>Tetitulz.Pdf.Base</c>.
        /// </remarks>
        internal static bool TryReadJpegFrame(byte[] jpegData, out int width, out int height, out int components)
        {
            width = UnknownJpegDimension;
            height = UnknownJpegDimension;
            components = 0;

            if (!RasterImageInspector.TryInspect(jpegData, out var info)) return false;
            if (info.Format != RasterImageFormat.Jpeg) return false;
            if (info.Width <= 0 || info.Height <= 0) return false;

            width = info.Width;
            height = info.Height;
            components = info.Components;
            return true;
        }

        private static PdfArray CreateRectArray(float x1, float y1, float x2, float y2)
        {
            var arr = new PdfArray();
            arr.Add(new PdfNumber(x1));
            arr.Add(new PdfNumber(y1));
            arr.Add(new PdfNumber(x2));
            arr.Add(new PdfNumber(y2));
            return arr;
        }
    }
}
