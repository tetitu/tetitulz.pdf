// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// One operator and its operands, as produced by <see cref="ContentStreamParser"/>.
    ///
    /// <para><b>Public because a plugin needs it</b> — see the note on
    /// <see cref="ContentStreamParser"/>. Every operand type
    /// (<see cref="PdfName"/>, <see cref="PdfNumber"/>, <see cref="PdfDictionary"/>,
    /// <see cref="PdfStream"/>) was already public, so this exposes no new shape.</para>
    /// </summary>
    public sealed class ContentStreamOperation
    {
        public string Operator { get; }
        public PdfObject[] Operands { get; }

        public ContentStreamOperation(string op, PdfObject[] operands)
        {
            Operator = op;
            Operands = operands;
        }

        public override string ToString() => $"{Operator} ({Operands.Length} operands)";
    }
}
