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
using Tetitulz.Pdf.Base.Operations.Documents;
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
        /// <summary>
        /// The document's revisions, oldest first. An incrementally-updated PDF keeps every
        /// earlier state in the file; this is what enumerates them.
        /// </summary>
        public static IReadOnlyList<PdfRevisionInfo> GetRevisions(Stream input)
            => PdfRevisions.List(input);

        /// <summary>
        /// The document's revisions with each attributed to the signature(s) covering it, read
        /// from the declared <c>/ByteRange</c>. Use this to answer "which of these revisions is
        /// signed, and by which field" without a <c>Tetitulz.Pdf.Signing</c> reference.
        ///
        /// <para>Unlike <see cref="GetRevisions"/> this half needs the object graph, so it does
        /// <b>not</b> work on an encrypted file. It says so rather than pretending — check
        /// <see cref="PdfRevisionAttribution.SignaturesExamined"/> before reading
        /// <see cref="PdfRevisionInfo.SignedBy"/>.</para>
        /// </summary>
        public static PdfRevisionAttribution GetRevisionsWithSignatures(Stream input)
            => PdfRevisions.ListWithSignatures(input);

        /// <summary>
        /// Writes the document as it stood at <paramref name="revisionIndex"/> (0 = the original)
        /// to <paramref name="output"/>. Use this to recover what a signature-preserving edit —
        /// <see cref="ReplaceImage"/>, form filling, an accessibility writer — superseded.
        /// </summary>
        public static void ExtractRevision(Stream input, int revisionIndex, Stream output)
            => PdfRevisions.Extract(input, revisionIndex, output);
    }
}
