// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfNull : PdfObject
    {
        public static readonly PdfNull Instance = new PdfNull();

        private PdfNull() { }

        public override string ToString() => "null";
    }
}
