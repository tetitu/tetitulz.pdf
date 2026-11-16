// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Reading a table cell's Table-owner attributes — and, in particular, what its
    /// <c>/RowSpan</c> or <c>/ColSpan</c> <i>is</i> — once, for both PDF/UA checkers.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a leaf.</b> PDF/UA-1's UA12 (<c>PdfDeepAccessibilityChecker</c>) and PDF/UA-2's
    /// §8.2.5.26 (<c>PdfUa2StructureChecker</c>) both run a table-regularity model over the same
    /// spans. <c>PdfDeepAccessibilityChecker</c> has static initializers, so the UA-2 checker
    /// reaching into it would add a type-initialization edge between peers; the shared answer
    /// goes down into this type instead, which has no static fields.</para>
    ///
    /// <para><b>The divergence this closes.</b> UA-1 read spans through
    /// <see cref="PdfStructureAttributes.Lookup"/>; the UA-2 checker read them as entries on the
    /// structure element dictionary itself — <c>cell[/ColSpan]</c> — which ISO 32000-2 Table 355
    /// does not define. Spans are Table-owner <i>attributes</i> (§14.8.5.7), declared in
    /// <c>/A</c> or through <c>/C</c> + <c>/ClassMap</c>. So every conforming merged cell,
    /// including every one this library's own Builder writes
    /// (<c>/A &lt;&lt; /O /Table /ColSpan n &gt;&gt;</c>), read as a span of 1, and the UA-2 checker
    /// reported a regular table as irregular. Its tests stayed green because their fixtures wrote
    /// the span in the same wrong place the code read it from.</para>
    /// </remarks>
    internal static class TableAttributes
    {
        /// <summary>The attribute owner ISO 32000-2 §14.8.5.7 gives the table attributes.</summary>
        internal const string Owner = "Table";

        /// <summary>
        /// Largest span accepted as meant. A span of 0 or a hostile 2^31 would make the grid
        /// meaningless, so anything outside 1..<see cref="MaxSpan"/> is read as 1 rather than
        /// trusted.
        /// </summary>
        internal const int MaxSpan = 1000;

        /// <summary>One Table-owner attribute of <paramref name="element"/>, from <c>/A</c> or <c>/C</c>.</summary>
        internal static PdfObject Lookup(
            PdfDictionary element, PdfName key,
            IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
            => PdfStructureAttributes.Lookup(element, key, Owner, classMap, resolve);

        /// <summary>
        /// The cell's <c>/RowSpan</c> or <c>/ColSpan</c> (<paramref name="key"/>): the declared
        /// value when it is a number within 1..<see cref="MaxSpan"/>, otherwise 1 — the attribute's
        /// own default.
        /// </summary>
        internal static int ReadSpan(
            PdfDictionary cell, PdfName key,
            IReadOnlyDictionary<string, PdfObject> classMap, Func<PdfObject, PdfObject> resolve)
        {
            if (Lookup(cell, key, classMap, resolve) is PdfNumber n)
            {
                int span = n.IntValue;
                if (span >= 1 && span <= MaxSpan) return span;
            }
            return 1;
        }
    }
}
