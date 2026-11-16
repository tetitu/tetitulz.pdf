// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Objects
{
    public abstract class PdfObject
    {
        public int ObjectNumber { get; set; }
        public int GenerationNumber { get; set; }
        public bool IsIndirect => ObjectNumber > 0;
    }
}
