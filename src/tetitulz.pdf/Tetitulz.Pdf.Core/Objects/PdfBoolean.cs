// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfBoolean : PdfObject
    {
        public static readonly PdfBoolean True = new PdfBoolean(true);
        public static readonly PdfBoolean False = new PdfBoolean(false);

        public bool Value { get; }

        public PdfBoolean(bool value)
        {
            Value = value;
        }

        public override string ToString() => Value ? "true" : "false";
    }
}
