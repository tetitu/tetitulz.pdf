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
using Tetitulz.Pdf.Base.Operations.Structure;
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
        /// <summary>Returns the bookmarks (outline items) from the document.</summary>
        public static IReadOnlyList<PdfBookmark> GetBookmarks(Stream input, string password = null)
            => WithDecrypted(input, password, PdfBookmarksManager.ReadBookmarks);

        /// <summary>Replaces the bookmark tree with the supplied list.</summary>
        public static void WriteBookmarks(Stream input, Stream output,
            IReadOnlyList<PdfBookmark> bookmarks)
            => PdfBookmarksManager.WriteBookmarks(input, output, bookmarks);
    }
}
