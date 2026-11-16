// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// How a writer that rebuilds an entry from what it read tells "absent" from "unreadable": the
    /// catalog's <c>/Names</c>, an <c>/AcroForm</c>, a form's <c>/Fields</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>An unreadable object is not an absent one</b> (CLAUDE.md). The null object answers
    /// both, and a writer that reads an unreadable entry as absent writes a fresh one holding only its
    /// own addition, dropping everything the old entry held from the revision. Examples: an
    /// attachment edit dropping every other name tree, a new field replacing a form, a signature
    /// added beside a form it never saw. Another reader whose parser can read the object then shows
    /// the loss.</para>
    /// <para>"Unreadable" is the parser's own answer: a reference whose read returned the null object
    /// with <see cref="PdfParser.LastObjectParseError"/> set, read immediately because the slot
    /// belongs to the most recent read. A reference to an object the file does not have sets no error
    /// and is absent. The refusal is the caller's, in its own exception type.</para>
    /// </remarks>
    internal static class EntryRead
    {
        /// <summary>
        /// <paramref name="value"/> followed through <paramref name="resolve"/>: the object, or null
        /// when it is absent or could not be read. In the latter case <paramref name="unreadable"/>
        /// gets the parser's reason; it is null otherwise.
        /// </summary>
        internal static PdfObject Resolve(Func<PdfObject, PdfObject> resolve, PdfObject value, out string unreadable)
        {
            unreadable = null;
            if (value == null) return null;
            if (!(value is PdfReference)) return value is PdfNull ? null : value;

            PdfObject resolved = resolve(value);
            if (resolved == null || resolved is PdfNull)
            {
                unreadable = PdfParser.LastObjectParseError;
                return null;
            }
            return resolved;
        }
    }
}
