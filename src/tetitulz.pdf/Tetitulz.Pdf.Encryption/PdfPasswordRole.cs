// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Which of an encrypted document's two passwords a string is (ISO 32000-2 §7.6.4.2), as
    /// <see cref="PdfDecryptor.IdentifyPassword"/> answers.
    /// </summary>
    public enum PdfPasswordRole
    {
        /// <summary>Neither password: the document does not open with it.</summary>
        None = 0,
        /// <summary>The user password: the document opens under its <c>/P</c> permissions. The empty string, for a document that opens without one.</summary>
        User = 1,
        /// <summary>The owner password: the document opens with no restriction.</summary>
        Owner = 2,
    }
}
