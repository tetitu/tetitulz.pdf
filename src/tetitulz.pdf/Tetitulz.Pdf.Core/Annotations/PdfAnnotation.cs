// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Annotations
{
    public class PdfAnnotation
    {
        private readonly PdfDocument _document;

        internal PdfDictionary Dictionary { get; }

        public PdfAnnotation(PdfDictionary dictionary, PdfDocument document)
        {
            Dictionary = dictionary;
            _document = document;
        }

        /// <summary>
        /// An entry of this annotation's dictionary, following an indirect reference (§7.3.10).
        /// </summary>
        /// <remarks>
        /// Every property here read through <c>PdfDictionary</c>'s typed getters, which do not follow
        /// a reference — while the document that resolves one sat in a field, unused. An indirect
        /// <c>/Subtype</c> made a widget <see cref="PdfAnnotationType.Unknown"/> and
        /// <see cref="Create"/> build a plain annotation for it; an indirect <c>/F</c> read as "no
        /// flags", which for a hidden annotation is the opposite answer.
        /// </remarks>
        internal PdfObject Entry(PdfName key) => Resolve(Dictionary[key], _document);

        private static PdfObject Resolve(PdfObject raw, PdfDocument document)
            => raw == null || document == null ? raw : document.ResolveObject(raw) ?? raw;

        public PdfAnnotationType AnnotationType
        {
            get
            {
                var subtype = Entry(PdfName.Subtype) as PdfName;
                if (subtype == null) return PdfAnnotationType.Unknown;

                switch (subtype.Value)
                {
                    case PdfAnnotationSubtypes.Text: return PdfAnnotationType.Text;
                    case PdfAnnotationSubtypes.Link: return PdfAnnotationType.Link;
                    case PdfAnnotationSubtypes.FreeText: return PdfAnnotationType.FreeText;
                    case PdfAnnotationSubtypes.Line: return PdfAnnotationType.Line;
                    case PdfAnnotationSubtypes.Square: return PdfAnnotationType.Square;
                    case PdfAnnotationSubtypes.Circle: return PdfAnnotationType.Circle;
                    case PdfAnnotationSubtypes.Polygon: return PdfAnnotationType.Polygon;
                    case PdfAnnotationSubtypes.PolyLine: return PdfAnnotationType.PolyLine;
                    case PdfAnnotationSubtypes.Highlight: return PdfAnnotationType.Highlight;
                    case PdfAnnotationSubtypes.Underline: return PdfAnnotationType.Underline;
                    case PdfAnnotationSubtypes.Squiggly: return PdfAnnotationType.Squiggly;
                    case PdfAnnotationSubtypes.StrikeOut: return PdfAnnotationType.StrikeOut;
                    case PdfAnnotationSubtypes.Stamp: return PdfAnnotationType.Stamp;
                    case PdfAnnotationSubtypes.Caret: return PdfAnnotationType.Caret;
                    case PdfAnnotationSubtypes.Ink: return PdfAnnotationType.Ink;
                    case PdfAnnotationSubtypes.Popup: return PdfAnnotationType.Popup;
                    case PdfAnnotationSubtypes.FileAttachment: return PdfAnnotationType.FileAttachment;
                    case PdfAnnotationSubtypes.Sound: return PdfAnnotationType.Sound;
                    case PdfAnnotationSubtypes.Movie: return PdfAnnotationType.Movie;
                    case PdfAnnotationSubtypes.Widget: return PdfAnnotationType.Widget;
                    case PdfAnnotationSubtypes.Screen: return PdfAnnotationType.Screen;
                    case PdfAnnotationSubtypes.PrinterMark: return PdfAnnotationType.PrinterMark;
                    case PdfAnnotationSubtypes.TrapNet: return PdfAnnotationType.TrapNet;
                    case PdfAnnotationSubtypes.Watermark: return PdfAnnotationType.Watermark;
                    case PdfAnnotationSubtypes.ThreeD: return PdfAnnotationType.ThreeD;
                    default: return PdfAnnotationType.Unknown;
                }
            }
        }

        public PdfArray Rect => Entry(PdfName.Rect) as PdfArray;

        public string Contents => (Entry(PdfName.Contents) as PdfString)?.GetText();

        /// <summary>The <c>/F</c> flag word; 0 when absent or not an integer (§12.5.3's default).</summary>
        public int AnnotationFlags => PdfAnnotationFlags.FromNumber(Entry(PdfName.F)) ?? 0;

        public static PdfAnnotation Create(PdfDictionary dict, PdfDocument document)
        {
            var subtype = Resolve(dict[PdfName.Subtype], document) as PdfName;
            if (subtype != null && subtype.Value == PdfAnnotationSubtypes.Widget)
                return new PdfWidgetAnnotation(dict, document);
            return new PdfAnnotation(dict, document);
        }

        public override string ToString() => $"Annotation: {AnnotationType}";
    }
}
