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
using Tetitulz.Pdf.Base.Operations.Extraction;
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
        /// <summary>Searches all pages of a PDF for the given query string.</summary>
        public static IReadOnlyList<TextSearchHit> SearchText(
            string inputPath, string query, bool ignoreCase = true, int contextChars = 40)
            => PdfTextSearch.Search(inputPath, query, ignoreCase, contextChars);

        /// <summary>Searches all pages of a PDF stream for the given query string.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<TextSearchHit> SearchText(
            Stream input, string query, bool ignoreCase = true, int contextChars = 40)
            => SearchText(input, query, (string)null, ignoreCase, contextChars);

        /// <summary>
        /// Searches all pages of a PDF stream for the given query string, decrypting first when the
        /// document is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to search.</param>
        /// <param name="query">The text to look for.</param>
        /// <param name="password">
        /// The user password. Ignored for an unencrypted document. Without it an encrypted document
        /// searches as if it had no text at all — the same wrong answer a page with no text layer
        /// gives, which is what <see cref="LastExtractionError"/> exists to tell apart.
        /// </param>
        /// <param name="ignoreCase">Whether the match is case-insensitive.</param>
        /// <param name="contextChars">How much surrounding text each hit's snippet carries.</param>
        /// <remarks>
        /// <paramref name="password"/> sits <i>ahead</i> of the pre-existing optional parameters
        /// because a required parameter cannot follow an optional one. That is also what keeps the
        /// two overloads unambiguous at every arity: this one is inapplicable unless a password is
        /// supplied, so no existing call site can start resolving here.
        /// </remarks>
        public static IReadOnlyList<TextSearchHit> SearchText(
            Stream input, string query, string password, bool ignoreCase = true,
            int contextChars = 40)
            => WithDecrypted(input, password, s => PdfTextSearch.Search(s, query, ignoreCase, contextChars));

        /// <summary>Returns true if the PDF contains the given query text.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter — see the binary-compatibility note on
        /// <see cref="SearchText(Stream, string, bool, int)"/>.
        /// </remarks>
        public static bool ContainsText(Stream input, string query, bool ignoreCase = true)
            => ContainsText(input, query, (string)null, ignoreCase);

        /// <summary>
        /// Returns true if the PDF contains the given query text, decrypting first when the document
        /// is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to search.</param>
        /// <param name="query">The text to look for.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        /// <param name="ignoreCase">Whether the match is case-insensitive.</param>
        public static bool ContainsText(Stream input, string query, string password,
            bool ignoreCase = true)
            => WithDecrypted(input, password, s => PdfTextSearch.Contains(s, query, ignoreCase));
    }
}
