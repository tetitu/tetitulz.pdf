// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    public sealed class PdfPage
    {
        private readonly PdfDocument _document;
        private readonly PdfDictionary _inheritedResources;
        private readonly PdfArray _inheritedMediaBox;
        private readonly PdfArray _inheritedCropBox;
        private readonly int? _inheritedRotate;

        public PdfDictionary Dictionary { get; }
        internal PdfDocument Document => _document;

        public int PageIndex { get; }

        public PdfPage(PdfDictionary dictionary, PdfDocument document,
            PdfDictionary inheritedResources, int pageIndex)
            : this(dictionary, document, inheritedResources, null, null, null, pageIndex)
        {
        }

        internal PdfPage(PdfDictionary dictionary, PdfDocument document,
            PdfDictionary inheritedResources,
            PdfArray inheritedMediaBox, PdfArray inheritedCropBox, int? inheritedRotate,
            int pageIndex)
        {
            Dictionary = dictionary;
            _document = document;
            _inheritedResources = inheritedResources;
            _inheritedMediaBox = inheritedMediaBox;
            _inheritedCropBox = inheritedCropBox;
            _inheritedRotate = inheritedRotate;
            PageIndex = pageIndex;
        }

        /// <summary>
        /// Page MediaBox. Falls back to inherited value from the page tree
        /// (PDF spec §7.7.3.4: /MediaBox is an inheritable attribute).
        /// Resolved by walking up the parent chain.
        /// </summary>
        public PdfArray MediaBox
        {
            get
            {
                var mb = Dictionary.GetArray(PdfName.MediaBox);
                if (mb != null) return ElementsResolved(mb);
                var mbObj = Dictionary[PdfName.MediaBox];
                if (mbObj != null)
                {
                    var resolved = _document.ResolveObject(mbObj) as PdfArray;
                    if (resolved != null) return ElementsResolved(resolved);
                }
                return ElementsResolved(_inheritedMediaBox);
            }
        }

        /// <summary>
        /// Page CropBox. Falls back to inherited value, then to MediaBox
        /// (PDF spec §14.11.2: default CropBox equals MediaBox).
        /// </summary>
        public PdfArray CropBox
        {
            get
            {
                var cb = Dictionary.GetArray(PdfName.CropBox);
                if (cb != null) return ElementsResolved(cb);
                var cbObj = Dictionary[PdfName.CropBox];
                if (cbObj != null)
                {
                    var resolved = _document.ResolveObject(cbObj) as PdfArray;
                    if (resolved != null) return ElementsResolved(resolved);
                }
                if (_inheritedCropBox != null) return ElementsResolved(_inheritedCropBox);
                return MediaBox; // defaults to MediaBox per spec
            }
        }

        /// <summary>
        /// A page box with its numbers in place: §7.3.10 lets each be a reference, and every reader of a
        /// box takes its elements as they stand, where a reference reads as 0 and draws another page.
        /// </summary>
        private PdfArray ElementsResolved(PdfArray box)
        {
            if (box == null || _document == null) return box;
            bool direct = true;
            foreach (PdfObject element in box) direct &= !(element is PdfReference);
            if (direct) return box;

            var copy = new PdfArray();
            foreach (PdfObject element in box) copy.Add(_document.ResolveObject(element));
            return copy;
        }

        /// <summary>
        /// Page rotation. Falls back to inherited value from the page tree.
        ///
        /// <para>The indirect case is resolved explicitly, exactly as the five page boxes below do:
        /// <see cref="PdfDictionary.GetInteger"/> tests <c>v is PdfNumber</c> and never follows a
        /// <see cref="PdfReference"/>, so a producer that shares one rotation object across every
        /// page of a batch scan — <c>/Rotate 12 0 R</c> — would read back as "no rotation" and the
        /// page render, print and extract sideways with nothing reported.</para>
        /// </summary>
        public int? Rotate
        {
            get
            {
                var r = Dictionary.GetInteger(PdfName.Rotate);
                if (r.HasValue) return r;
                var rObj = Dictionary[PdfName.Rotate];
                if (rObj != null && _document != null)
                {
                    if (_document.ResolveObject(rObj) is PdfNumber resolved) return resolved.IntValue;
                }
                return _inheritedRotate;
            }
        }

        /// <summary>
        /// BleedBox — the region to which the contents should be clipped when output
        /// in a production environment. Defaults to CropBox (PDF spec §14.11.2).
        /// Exposed for print-production workflows.
        /// </summary>
        public PdfArray BleedBox
        {
            get
            {
                var bb = Dictionary.GetArray(PdfName.BleedBox);
                if (bb != null) return ElementsResolved(bb);
                var bbObj = Dictionary[PdfName.BleedBox];
                if (bbObj != null)
                {
                    var resolved = _document.ResolveObject(bbObj) as PdfArray;
                    if (resolved != null) return ElementsResolved(resolved);
                }
                return CropBox;
            }
        }

        /// <summary>
        /// TrimBox — the intended finished dimensions of the page after trimming.
        /// Defaults to CropBox (PDF spec §14.11.2).
        /// </summary>
        public PdfArray TrimBox
        {
            get
            {
                var tb = Dictionary.GetArray(PdfName.TrimBox);
                if (tb != null) return ElementsResolved(tb);
                var tbObj = Dictionary[PdfName.TrimBox];
                if (tbObj != null)
                {
                    var resolved = _document.ResolveObject(tbObj) as PdfArray;
                    if (resolved != null) return ElementsResolved(resolved);
                }
                return CropBox;
            }
        }

        /// <summary>
        /// ArtBox — the extent of the page's meaningful content as intended by the creator.
        /// Defaults to CropBox (PDF spec §14.11.2).
        /// </summary>
        public PdfArray ArtBox
        {
            get
            {
                var ab = Dictionary.GetArray(PdfName.ArtBox);
                if (ab != null) return ElementsResolved(ab);
                var abObj = Dictionary[PdfName.ArtBox];
                if (abObj != null)
                {
                    var resolved = _document.ResolveObject(abObj) as PdfArray;
                    if (resolved != null) return ElementsResolved(resolved);
                }
                return CropBox;
            }
        }

        /// <summary>
        /// UserUnit — scaling factor for default user space (PDF 1.6+).
        /// 1.0 means standard 1/72 inch per unit; print scaling reads it.
        ///
        /// <para>Indirect values are resolved for the same reason as <see cref="Rotate"/>:
        /// <see cref="PdfDictionary.GetDouble"/> does not follow a reference, so a shared
        /// <c>/UserUnit 12 0 R</c> would silently read back as the 1.0 default and every
        /// measurement derived from the page be wrong by that factor.</para>
        /// </summary>
        public double UserUnit
        {
            get
            {
                var uu = Dictionary.GetDouble(PdfName.UserUnit);
                if (uu.HasValue) return uu.Value;
                var uuObj = Dictionary[PdfName.UserUnit];
                if (uuObj != null && _document != null)
                {
                    if (_document.ResolveObject(uuObj) is PdfNumber resolved) return resolved.DoubleValue;
                }
                return 1.0;
            }
        }

        public PdfDictionary Resources
        {
            get
            {
                var resObj = Dictionary[PdfName.Resources];
                if (resObj != null)
                    return _document.ResolveObject(resObj) as PdfDictionary;
                return _inheritedResources;
            }
        }

        public byte[] GetContentStreamData()
        {
            // One reader, in PdfPageContent — §7.7.3.3's separator included. The decoder throws on
            // a damaged part, which is this method's documented contract and the reason the shared
            // form takes the decode as a delegate rather than choosing a policy for everyone. A page's
            // content is syntax, so it is decoded under the document's syntax-stream budget
            // (PdfLimits.MaxSyntaxStreamDecodeWork): a page whose /Contents named JBIG2-filtered
            // streams spent a whole JBIG2 stream budget on each, uncounted above it.
            var work = _document.Parser?.SyntaxStreamWork;
            return PdfPageContent.Concatenate(
                Dictionary, _document.ResolveObject, s => s.GetDecodedDataUnder(work));
        }

        public PdfArray GetAnnotations()
        {
            var annotsObj = Dictionary[PdfName.Annots];
            if (annotsObj == null) return null;
            return _document.ResolveObject(annotsObj) as PdfArray;
        }

        /// <summary>True when the page dictionary contains an /AA (additional actions) entry.</summary>
        public bool HasAdditionalActions => Dictionary[PdfName.AA] != null;

        /// <summary>
        /// Page-transition metadata parsed from /Trans (ISO 32000-1 §12.4.4).
        /// Null when the page carries no /Trans entry.  Transitions are
        /// page-scoped, not inheritable.
        /// </summary>
        public PdfTransition Transition
        {
            get
            {
                var transObj = Dictionary[PdfName.Trans];
                if (transObj == null) return null;
                var transDict = _document.ResolveObject(transObj) as PdfDictionary
                                ?? transObj as PdfDictionary;
                return transDict != null ? new PdfTransition(transDict) : null;
            }
        }

        public override string ToString() => $"Page {PageIndex + 1}";
    }
}
