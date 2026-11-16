// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The halftone dictionaries a graphics state's <c>/HT</c> names: the halftone itself, and a type 5
    /// halftone's components, each under the colorant its key names (ISO 32000-1 §10.5.5.6, Table 130).
    /// PDF/A's A57 judges them and PDF/X's X26 reports their transfer functions; both walk them here.
    /// </summary>
    internal static class Halftones
    {
        /// <summary>A type 5 halftone's components are themselves halftones; two levels is the deepest
        /// walk, so a component naming another type 5 is read and its components are not.</summary>
        internal const int MaxNesting = 2;

        /// <summary>
        /// Calls <paramref name="visit"/> with each halftone dictionary <paramref name="value"/> reaches,
        /// its location, and its colorant: <c>null</c> for the halftone the graphics state names, the
        /// key for a type 5's component. <c>/Default</c> (a name) and an absent value reach nothing.
        /// A halftone <paramref name="seen"/> has already met under the same colorant is not visited
        /// again, nor are its components: what it is does not depend on which graphics state names it.
        /// </summary>
        internal static void Visit(PdfDocument doc, PdfObject value, string where, Seen seen,
            Action<PdfDictionary, string, string> visit)
            => Walk(doc, value, where, null, seen, visit, 0);

        private static void Walk(PdfDocument doc, PdfObject value, string where, string colorant, Seen seen,
            Action<PdfDictionary, string, string> visit, int depth)
        {
            if (depth > MaxNesting || !(doc.ResolveObject(value) is PdfDictionary halftone)) return;
            if (!seen.FirstVisit(halftone, colorant)) return;
            visit(halftone, where, colorant);
            if (Type(doc, halftone) != 5) return;
            foreach (var kvp in halftone)
            {
                string key = kvp.Key.Value;
                if (key == "Type" || key == "HalftoneType" || key == "HalftoneName") continue;
                Walk(doc, kvp.Value, where + " /" + key, key, seen, visit, depth + 1);
            }
        }

        /// <summary>
        /// The halftones one validation has visited, each under each colorant it was reached as, so a
        /// type 5 halftone many graphics states name is walked once: N states naming one of M components
        /// cost N + M visits, not N × M — both counts are the file's. Reported at the first location
        /// that reaches it.
        /// </summary>
        internal sealed class Seen
        {
            private readonly Dictionary<object, HashSet<string>> _visited =
                new Dictionary<object, HashSet<string>>(PdfReferenceEquality.Instance);

            /// <summary>How many visits were passed over as already made: the guard's evidence.</summary>
            internal int Skipped { get; private set; }

            internal bool FirstVisit(PdfDictionary halftone, string colorant)
            {
                if (!_visited.TryGetValue(halftone, out HashSet<string> colorants))
                    _visited[halftone] = colorants = new HashSet<string>(StringComparer.Ordinal);
                if (colorants.Add(colorant ?? string.Empty)) return true;
                Skipped++;
                return false;
            }
        }

        /// <summary>The halftone's <c>/HalftoneType</c>, or <c>null</c> when it has none that is a number.</summary>
        internal static int? Type(PdfDocument doc, PdfDictionary halftone)
            => (doc.ResolveObject(halftone[PdfName.HalftoneType]) as PdfNumber)?.IntValue;

        /// <summary>
        /// Whether ISO 32000-1 Table 130 requires the halftone for <paramref name="colorant"/> to carry a
        /// <c>/TransferFunction</c>: a type 5's component for a colorant other than the primaries — read
        /// as the rule 6.2.5-6 reads it, the four process colorants. <c>/Default</c> is neither
        /// required nor forbidden one, and a top-level halftone (<c>null</c>) has no colorant.
        /// </summary>
        internal static bool? TransferFunctionRequired(string colorant)
        {
            if (colorant == "Default") return null;
            return !(colorant == null || colorant == "Cyan" || colorant == "Magenta" || colorant == "Yellow" || colorant == "Black");
        }
    }
}
