// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core
{
    /// <summary>
    /// Usage Rights flags exposed via the <c>/UR3</c> signature reference
    /// (PDF §12.8.2.4 Table 256). Combine with <c>|</c> to grant multiple.
    /// </summary>
    [Flags]
    public enum UsageRights
    {
        /// <summary>No /UR3 reference — ordinary signature.</summary>
        None = 0,
        /// <summary>Allow form fill-in in Reader.</summary>
        FormFillIn = 1 << 0,
        /// <summary>Allow form-data import / export.</summary>
        FormImportExport = 1 << 1,
        /// <summary>Allow document save with form data.</summary>
        FormSave = 1 << 2,
        /// <summary>Allow annotation creation/modification.</summary>
        Annotation = 1 << 3,
        /// <summary>Allow document save in any state.</summary>
        DocumentSave = 1 << 4,
    }
}
