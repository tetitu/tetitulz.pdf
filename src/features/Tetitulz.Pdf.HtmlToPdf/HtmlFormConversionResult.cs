// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Export;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// What <see cref="HtmlToPdfConverter.ConvertHtmlWithFormFields"/> produces: the PDF with a
    /// placeholder where each form control landed, and where those placeholders are.
    /// </summary>
    public sealed class HtmlFormConversionResult
    {
        internal HtmlFormConversionResult(byte[] pdf, IReadOnlyList<PlacedFormField> formFields)
        {
            Pdf = pdf ?? throw new ArgumentNullException(nameof(pdf));
            FormFields = formFields ?? throw new ArgumentNullException(nameof(formFields));
        }

        /// <summary>The PDF, with a shaded placeholder where each field goes and no fields yet.</summary>
        public byte[] Pdf { get; }

        /// <summary>
        /// Each mapped control's field, page and rectangle, in document order — ready for
        /// <c>Base.Operations.Forms.PdfFormFieldBuilder.AddFields</c>.
        /// </summary>
        public IReadOnlyList<PlacedFormField> FormFields { get; }
    }
}
