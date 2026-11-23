// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// How a visible signature's appearance draws an image: through an inner form XObject whose own
    /// <c>/Resources</c> names the image, so the appearance's <c>/Resources /XObject</c> names a form
    /// and never an image.
    /// </summary>
    /// <remarks>
    /// <para><b>Acrobat Reader does not validate a signature whose appearance names an image in its
    /// <c>/Resources /XObject</c>.</b> Clicking the signature reports "Error during signature
    /// verification. Adobe Acrobat error. Expected a dict object", and the document stays at "At
    /// least one signature requires validating". The same image drawn through a form that carries
    /// its own <c>/Resources</c> validates, as an approval signature and as a DocMDP certification.
    /// The specification permits both shapes; the rule is Acrobat's, established by testing files
    /// that differ in this alone, and Adobe does not document it. Acrobat appears to read each
    /// XObject the appearance names as a form and to look for that form's <c>/Resources</c>, which
    /// an image has none of.</para>
    ///
    /// <para><b>Both appearance writers draw their image here</b> — <c>VisualSignatureBuilder</c>'s
    /// stamp and <see cref="SignatureAppearanceGenerator"/>'s logo — so the appearance names
    /// <see cref="FormResourceName"/> and nothing else under <c>/XObject</c>. The inner form's box
    /// is the appearance's own and its matrix the identity, so it clips and places nothing the
    /// appearance did not: the page renders as it would with the image drawn directly.</para>
    /// </remarks>
    internal static class SignatureAppearanceImage
    {
        /// <summary>The name the appearance's <c>/Resources /XObject</c> gives the inner form.</summary>
        internal const string FormResourceName = "SigForm";

        /// <summary>The appearance's content that draws the inner form, and through it the image.</summary>
        internal const string Invocation = "q /" + FormResourceName + " Do Q";

        /// <summary>
        /// The inner form: a <paramref name="width"/> × <paramref name="height"/> box (the
        /// appearance's own), whose <c>/Resources /XObject</c> names image object
        /// <paramref name="imageObjectNumber"/> as <paramref name="imageResourceName"/> and whose
        /// content is <paramref name="drawImage"/> — the operators that place and draw that image.
        /// </summary>
        /// <param name="objectNumber">The object number the caller allocated for the form.</param>
        /// <param name="width">The appearance's width, in its own units.</param>
        /// <param name="height">The appearance's height, in its own units.</param>
        /// <param name="imageResourceName">The name <paramref name="drawImage"/> invokes the image by.</param>
        /// <param name="imageObjectNumber">The image XObject's object number (generation 0).</param>
        /// <param name="drawImage">The content stream, ASCII: a <c>cm</c> and a <c>Do</c> of the image.</param>
        internal static PdfStream InnerForm(
            int objectNumber, double width, double height,
            string imageResourceName, int imageObjectNumber, string drawImage)
        {
            var images = new PdfDictionary();
            images.Set(new PdfName(imageResourceName), new PdfReference(imageObjectNumber, 0));
            var resources = new PdfDictionary();
            resources.Set(PdfName.XObject, images);

            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(0));
            bbox.Add(new PdfNumber(width));
            bbox.Add(new PdfNumber(height));

            byte[] content = Encoding.ASCII.GetBytes(drawImage);

            var form = new PdfStream();
            form.Set(PdfName.Type, PdfName.XObject);
            form.Set(PdfName.Subtype, PdfName.Form);
            form.Set(PdfName.BBox, bbox);
            form.Set(PdfName.Resources, resources);
            form.Set(PdfName.Length, new PdfNumber(content.Length));
            form.RawData = content;
            form.ObjectNumber = objectNumber;
            form.GenerationNumber = 0;
            return form;
        }

        /// <summary>
        /// The appearance's <c>/XObject</c> resource dictionary: the inner form under
        /// <see cref="FormResourceName"/>, and nothing else.
        /// </summary>
        internal static PdfDictionary AppearanceXObjects(int innerFormObjectNumber)
        {
            var xobjects = new PdfDictionary();
            xobjects.Set(new PdfName(FormResourceName), new PdfReference(innerFormObjectNumber, 0));
            return xobjects;
        }
    }
}
