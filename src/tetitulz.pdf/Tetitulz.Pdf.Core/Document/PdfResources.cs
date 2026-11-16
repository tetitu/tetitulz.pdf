// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    public sealed class PdfResources
    {
        private readonly PdfDictionary _dict;
        private readonly PdfDocument _document;

        public PdfResources(PdfDictionary dictionary, PdfDocument document)
        {
            _dict = dictionary;
            _document = document;
        }

        public PdfDictionary Fonts
        {
            get
            {
                var fontObj = _dict[PdfName.Font];
                return fontObj != null ? _document.ResolveObject(fontObj) as PdfDictionary : null;
            }
        }

        public PdfDictionary XObjects
        {
            get
            {
                var xObj = _dict[PdfName.XObject];
                return xObj != null ? _document.ResolveObject(xObj) as PdfDictionary : null;
            }
        }

        public PdfDictionary ExtGState
        {
            get
            {
                var gsObj = _dict[PdfName.ExtGState];
                return gsObj != null ? _document.ResolveObject(gsObj) as PdfDictionary : null;
            }
        }

        public PdfDictionary ColorSpaces
        {
            get
            {
                var csObj = _dict[PdfName.ColorSpace];
                return csObj != null ? _document.ResolveObject(csObj) as PdfDictionary : null;
            }
        }
    }
}
