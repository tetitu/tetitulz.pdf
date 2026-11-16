// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations.Documents;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Writes <paramref name="input"/> with every operation of the content <paramref name="classes"/>
        /// taken out of every page, form XObject, tiling pattern and annotation appearance — the text,
        /// the images, the vector graphics, or any of them together — each the way that keeps the rest
        /// of the content as it was. See <see cref="PdfContentFilter"/> for what each class holds and
        /// what is left as it is.
        /// </summary>
        /// <param name="input">The PDF to filter. Not disposed.</param>
        /// <param name="output">Where the result is written. Not disposed.</param>
        /// <param name="classes">The classes to take out; <see cref="PdfContentClass.None"/> copies the input through.</param>
        public static ContentFilterResult RemoveContent(Stream input, Stream output, PdfContentClass classes)
            => PdfContentFilter.Remove(input, output, classes);
    }
}
