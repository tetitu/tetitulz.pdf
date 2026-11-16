// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Annotations
{
    public sealed class PdfWidgetAnnotation : PdfAnnotation
    {
        public PdfWidgetAnnotation(PdfDictionary dictionary, PdfDocument document)
            : base(dictionary, document)
        {
        }

        /// <summary>This widget's own <c>/FT</c>, following an indirect reference.</summary>
        /// <remarks>
        /// Not inherited: a widget kid of a field has no <c>/FT</c> of its own and reports null.
        /// <c>PdfFormField.FieldType</c> is the field-level answer.
        /// </remarks>
        public PdfName FieldType => Entry(PdfName.FT) as PdfName;

        /// <summary>This widget's own <c>/T</c>, following an indirect reference.</summary>
        public string FieldName => (Entry(PdfName.T) as PdfString)?.GetText();
    }
}
