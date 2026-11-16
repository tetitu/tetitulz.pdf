// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Signing
{
    /// <summary>
    /// Metadata read from a PDF signature field (/V signature dictionary).
    /// Does not include cryptographic verification — use
    /// <see cref="PdfOperations.GetSignatures(string)"/> to retrieve these alongside the field.
    /// </summary>
    public sealed class PdfSignatureInfo
    {
        /// <summary>
        /// Fully qualified name of the AcroForm signature field (ISO 32000-2 §12.7.4.2) — its own
        /// <c>/T</c> for a top-level field, <c>parent.child</c> for a nested one; <c>null</c> when no
        /// node in the chain carries a <c>/T</c>.
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>Reason for signing (from /Reason entry).</summary>
        public string Reason { get; set; }

        /// <summary>Signing location (from /Location entry).</summary>
        public string Location { get; set; }

        /// <summary>Raw signing time string as stored in the PDF (from /M entry, PDF date format).</summary>
        public string SigningTime { get; set; }

        /// <summary>CMS /Filter name (typically "Adobe.PPKLite").</summary>
        public string Filter { get; set; }

        /// <summary>CMS /SubFilter name (typically "adbe.pkcs7.detached").</summary>
        public string SubFilter { get; set; }

        /// <summary>True if the signature field contains a /V signature dictionary.</summary>
        public bool HasSignature { get; set; }
    }
}
