// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.DocxToPdf
{
    /// <summary>
    /// Specifies which engine to use for DOCX to PDF conversion.
    /// </summary>
    public enum ConversionEngine
    {
        /// <summary>
        /// Built-in rendering pipeline using DocumentFormat.OpenXml and Tetitulz.Pdf.Core.
        /// Cross-platform. Default.
        /// </summary>
        BuiltIn = 0,

        /// <summary>
        /// LibreOffice headless mode (soffice --headless --convert-to pdf).
        /// Cross-platform. Requires LibreOffice installed.
        /// </summary>
        LibreOffice = 1,

        /// <summary>
        /// Microsoft Word COM automation. Windows only.
        /// Requires Microsoft Word installed.
        /// </summary>
        MicrosoftWord = 2
    }
}
