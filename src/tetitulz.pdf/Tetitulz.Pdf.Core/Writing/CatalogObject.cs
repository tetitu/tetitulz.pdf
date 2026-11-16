// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The catalog's object number, for a writer that appends a revision: refused when the trailer's
    /// <c>/Root</c> is not an indirect reference.
    /// </summary>
    /// <remarks>
    /// <para>§7.5.5 requires <c>/Root</c> to be an indirect reference. A file that writes the catalog
    /// directly in its trailer still opens. An appended revision cannot carry it, though, because
    /// <see cref="IncrementalUpdateWriter"/> copies <c>/Root</c> into the new trailer only as a
    /// reference, and there is no object to supersede. A writer that went ahead over such a file used
    /// to "succeed" in silence: the bookmarks manager wrote an outline nothing pointed to, and the
    /// form flattener left <c>/AcroForm</c> in place over the widgets it had flattened.</para>
    /// <para>Signing refuses the same case in its own exception type
    /// (<c>PdfSigner.CatalogObjectNumberOrRefuse</c>).</para>
    /// </remarks>
    internal static class CatalogObject
    {
        /// <summary>The catalog's object number.</summary>
        /// <exception cref="PdfException"><c>/Root</c> is not an indirect reference.</exception>
        internal static int NumberOrRefuse(PdfDictionary trailer, string what)
        {
            if (trailer?[PdfName.Root] is PdfReference root && root.ReferenceObjectNumber > 0)
                return root.ReferenceObjectNumber;
            throw new PdfException(
                "The trailer's /Root is not an indirect reference, so a revision cannot carry the " +
                "catalog and " + what + " would be written where nothing points to it. No changes were written.");
        }
    }
}
