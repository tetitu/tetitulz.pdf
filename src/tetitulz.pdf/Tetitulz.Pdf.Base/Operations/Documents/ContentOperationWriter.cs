// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Documents
{
    /// <summary>
    /// A parsed content-stream operation written back as content: its operands, then its operator —
    /// and an inline image as <c>BI</c>, its entries under the keys it was written with, <c>ID</c>, its
    /// bytes as they were, <c>EI</c>. Every rewriter that keeps an operation it parsed writes it here.
    /// </summary>
    internal static class ContentOperationWriter
    {
        internal static void Write(PdfWriter w, ContentStreamOperation op)
        {
            if (op.Operator == PdfOperators.BeginInlineImage)
            {
                if (op.Operands.Length >= 1 && op.Operands[0] is PdfStream image) WriteInlineImage(w, image);
                return;
            }
            foreach (var operand in op.Operands) { w.WriteObject(operand); w.WriteRaw(" "); }
            w.WriteRaw(op.Operator);
            w.WriteRaw("\n");
        }

        private static void WriteInlineImage(PdfWriter w, PdfStream s)
        {
            w.WriteRaw(PdfOperators.BeginInlineImage + "\n");
            foreach (var kvp in s)
            {
                w.WriteObject(kvp.Key);
                w.WriteRaw(" ");
                w.WriteObject(kvp.Value);
                w.WriteRaw("\n");
            }
            w.WriteRaw(PdfOperators.InlineImageData + " ");
            if (s.RawData != null) w.WriteRaw(s.RawData);
            w.WriteRaw("\n" + PdfOperators.EndInlineImage + "\n");
        }
    }
}
