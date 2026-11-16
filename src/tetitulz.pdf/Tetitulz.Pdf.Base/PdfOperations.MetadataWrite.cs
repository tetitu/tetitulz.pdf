// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>Writes or updates the /Info dictionary metadata.</summary>
        /// <remarks>
        /// <para><b>Every entry the document already had is preserved</b>, including keys this API
        /// has no property for — <c>/Trapped</c> and any custom one (§14.3.3 Table 317) — so calling
        /// it after <c>PdfXConverter</c> keeps the <c>/Trapped</c> that converter wrote.</para>
        /// <para>Use <see cref="PdfMetadataUpdate.Custom"/> to write or remove an entry this API does
        /// not name.</para>
        /// </remarks>
        public static void WriteMetadata(Stream input, Stream output, PdfMetadataUpdate update)
            => PdfMetadataWriter.WriteInfo(input, output, update);

        /// <summary>Writes or replaces the XMP metadata stream.</summary>
        public static void WriteXmpMetadata(Stream input, Stream output, string xmpXml)
            => PdfMetadataWriter.WriteXmpMetadata(input, output, xmpXml);
    }
}
