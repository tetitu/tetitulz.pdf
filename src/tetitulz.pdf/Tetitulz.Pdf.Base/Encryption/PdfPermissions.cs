// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Encryption
{
    /// <summary>
    /// PDF document permission flags (Table 22 of PDF spec).
    /// These correspond to bits in the /P entry of the encryption dictionary.
    /// </summary>
    [Flags]
    public enum PdfPermissions
    {
        None = 0,

        /// <summary>Bit 3: Print the document.</summary>
        Print = 1 << 2,

        /// <summary>Bit 4: Modify the contents of the document.</summary>
        Modify = 1 << 3,

        /// <summary>Bit 5: Copy or extract text and graphics.</summary>
        Copy = 1 << 4,

        /// <summary>Bit 6: Add or modify text annotations, fill in interactive form fields.</summary>
        AnnotateAndForms = 1 << 5,

        /// <summary>Bit 9: Fill in existing interactive form fields.</summary>
        FillForms = 1 << 8,

        /// <summary>Bit 10: Extract text and graphics for accessibility.</summary>
        ExtractForAccessibility = 1 << 9,

        /// <summary>Bit 11: Assemble the document (insert, rotate, or delete pages).</summary>
        Assemble = 1 << 10,

        /// <summary>Bit 12: Print the document to a representation from which a faithful copy could be generated.</summary>
        PrintHighQuality = 1 << 11,

        /// <summary>All permissions granted.</summary>
        All = Print | Modify | Copy | AnnotateAndForms | FillForms | ExtractForAccessibility | Assemble | PrintHighQuality
    }
}
