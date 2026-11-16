// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    internal interface IDocxElementRenderer
    {
        void Render(OpenXmlElement element, DocxRenderContext context);
    }
}
