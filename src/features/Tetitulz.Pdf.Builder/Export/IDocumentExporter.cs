// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Export
{
    /// <summary>Exports a Document to a specific format.</summary>
    public interface IDocumentExporter
    {
        void Export(Document document, Stream output);
    }
}
