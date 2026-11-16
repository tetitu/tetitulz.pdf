// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    public static partial class PdfAValidator
    {
        /// <summary>The operators ISO 32000-1 Table A.1 defines (ISO 32000-2 adds none).</summary>
        private static readonly HashSet<string> DefinedOperators = new HashSet<string>(StringComparer.Ordinal)
        {
            "b", "B", "b*", "B*", "BDC", "BI", "BMC", "BT", "BX", "c", "cm", "CS", "cs", "d", "d0", "d1", "Do",
            "DP", "EI", "EMC", "ET", "EX", "f", "F", "f*", "G", "g", "gs", "h", "i", "ID", "j", "J", "K", "k",
            "l", "m", "M", "MP", "n", "q", "Q", "re", "RG", "rg", "ri", "s", "S", "SC", "sc", "SCN", "scn", "sh",
            "T*", "Tc", "Td", "TD", "Tf", "TJ", "Tj", "TL", "Tm", "Tr", "Ts", "Tw", "Tz", "v", "w", "W", "W*",
            "y", "'", "\"",
        };

        /// <summary>The four rendering intents ISO 32000 defines (Table 70; ISO 32000-2 Table 69).</summary>
        private static readonly HashSet<string> RenderingIntents = new HashSet<string>(StringComparer.Ordinal)
        {
            "RelativeColorimetric", "AbsoluteColorimetric", "Perceptual", "Saturation",
        };

        /// <summary>
        /// The content stream rules. A50 — no operator ISO 32000 does not define, inside BX/EX too
        /// (ISO 19005-1 6.2.10, 19005-2/3/4 6.2.2; rules 6.2.10-1, 6.2.2-1). A51 — every rendering
        /// intent is one of the four (the <c>ri</c> operand, an <c>/ExtGState</c>'s <c>/RI</c>, an
        /// image's <c>/Intent</c>; ISO 19005-1 6.2.9, 19005-2/3 6.2.6, 19005-4 6.2.4.6). A52 — PDF/A-2
        /// onwards: a stream that names a resource has a resource dictionary of its own — a page's
        /// not inherited from its parent, a form's, an appearance's, a Type 3 font's (6.2.2-2). A53 —
        /// PDF/A-4: every name a stream uses is in its resources (6.2.2-3).
        /// </summary>
        private static void CheckContentStreams(PdfDocument doc, PdfAConformancePart part, List<PdfAViolation> v)
        {
            if (doc?.Pages == null) return;
            bool part1 = part == PdfAConformancePart.Part1;
            bool part4 = part == PdfAConformancePart.Part4;
            var undefinedOps = new Occurrences();
            var intents = new Occurrences();
            var inherited = new Occurrences();
            var undefinedNames = new Occurrences();
            var seenStreams = new HashSet<PdfStream>();
            var seenResources = new HashSet<PdfDictionary>();
            long documentBytes = 0;

            for (int p = 0; p < doc.Pages.Count; p++)
            {
                string page = "page " + (p + 1).ToString(CultureInfo.InvariantCulture);
                var scan = PdfXValidator.ScanPage(doc, doc.Pages[p],
                    PdfXValidator.DefaultMaxPageContentBytes, ref documentBytes,
                    PdfXValidator.DefaultMaxDocumentContentBytes);
                if (scan.Incomplete != null)
                    ReportPartial($"Page {p + 1}: the A50-A53 content-stream checks did not run in full — {scan.Incomplete}.",
                        "A50", "A51", "A52", "A53");

                bool pageResourcesInherited = doc.Pages[p].Dictionary?[PdfName.Resources] == null;
                // Only what the page draws is judged: a named form that nothing
                // invokes is not used (ISO 19005-1 Corrigendum 2).
                bool skip = false, ownResources = false;
                string where = null;
                PdfStream source = null;
                PdfDictionary resources = null;
                WalkDrawnOperations(doc, scan,
                    (i, initialState) =>
                    {
                        source = scan.ContentStreamSources[i];
                        skip = source != null && !seenStreams.Add(source);
                        resources = scan.ContentStreamResources[i];
                        where = source == null ? page + " content"
                            : page + ", object " + source.ObjectNumber.ToString(CultureInfo.InvariantCulture);
                        ownResources = source == null ? !pageResourcesInherited : source[PdfName.Resources] != null;
                        return true;
                    },
                    (i, op) =>
                    {
                        if (skip) return true;
                        if (!DefinedOperators.Contains(op.Operator))
                            undefinedOps.Add(where + ": " + op.Operator);
                        if (op.Operator == "ri" && LastName(op) is PdfName intent && !RenderingIntents.Contains(intent.Value))
                            intents.Add(where + ": ri /" + intent.Value);
                        if (op.Operator == PdfOperators.BeginInlineImage && op.Operands.Length > 0 && op.Operands[0] is PdfStream inline
                            && doc.ResolveObject(inline[PdfName.Intent]) is PdfName inlineIntent && !RenderingIntents.Contains(inlineIntent.Value))
                            intents.Add(where + ": inline image /Intent /" + inlineIntent.Value);

                        if (part1) return true;
                        var named = NamedResource(op);
                        if (named.Category == null) return true;
                        // A Type 3 glyph's resources are its font's; ScanPage hands them over as the
                        // stream's own when the font has them.
                        bool glyphWithFontResources = source != null && source[PdfName.Resources] == null && resources != null;
                        if (!ownResources && !glyphWithFontResources)
                            inherited.Add($"{where}: /{named.Name.Value} ({named.Category.Value})");
                        else if (part4 && !Defines(doc, resources, named.Category, named.Name))
                            undefinedNames.Add($"{where}: /{named.Name.Value} ({named.Category.Value})");
                        return true;
                    });

                foreach (var dictionary in scan.Resources)
                {
                    if (!ScanReuse.FirstVisit(seenResources, dictionary)) continue;
                    if (doc.ResolveObject(dictionary[PdfName.ExtGState]) is PdfDictionary states)
                        foreach (var kvp in states)
                            if (doc.ResolveObject(kvp.Value) is PdfDictionary gs
                                && doc.ResolveObject(gs[PdfName.RI]) is PdfName ri && !RenderingIntents.Contains(ri.Value))
                                intents.Add($"{page}: /ExtGState /{kvp.Key.Value} /RI /{ri.Value}");
                    if (doc.ResolveObject(dictionary[PdfName.XObject]) is PdfDictionary xobjects)
                        foreach (var kvp in xobjects)
                            if (doc.ResolveObject(kvp.Value) is PdfStream image
                                && doc.ResolveObject(image[PdfName.Intent]) is PdfName imageIntent && !RenderingIntents.Contains(imageIntent.Value))
                                intents.Add($"{page}: /XObject /{kvp.Key.Value} /Intent /{imageIntent.Value}");
                }
            }

            string iso = part1 ? "ISO 19005-1" : $"ISO 19005-{(int)part}";
            if (undefinedOps.Count > 0)
                v.Add(new PdfAViolation("A50",
                    $"A content stream uses an operator ISO 32000 does not define: {undefinedOps} ({iso} " +
                    (part1 ? "6.2.10; rule 6.2.10-1" : "6.2.2; rule 6.2.2-1") + ")."));
            if (intents.Count > 0)
                v.Add(new PdfAViolation("A51",
                    $"A rendering intent is not one of the four ISO 32000 defines: {intents} ({iso} " +
                    (part1 ? "6.2.9" : part4 ? "6.2.4.6" : "6.2.6") + ")."));
            if (inherited.Count > 0)
                v.Add(new PdfAViolation("A52",
                    $"A content stream names a resource without a resource dictionary of its own: {inherited} " +
                    $"({iso} 6.2.2; rule 6.2.2-2)."));
            if (undefinedNames.Count > 0)
                v.Add(new PdfAViolation("A53",
                    $"A content stream names a resource its resources do not define: {undefinedNames} " +
                    "(ISO 19005-4 6.2.2; rule 6.2.2-3)."));
        }

        /// <summary>The resource an operation names, by category and name; a device colour space or none gives no category.</summary>
        private static (PdfName Category, PdfName Name) NamedResource(ContentStreamOperation op)
        {
            switch (op.Operator)
            {
                case PdfOperators.SetColorSpaceFill:
                case PdfOperators.SetColorSpaceStroke:
                    if (LastName(op) is PdfName space && !IsReservedColourSpaceName(space.Value))
                        return (PdfName.ColorSpace, space);
                    break;
                case PdfOperators.SetColorFillN:
                case PdfOperators.SetColorStrokeN:
                    if (LastName(op) is PdfName pattern) return (PdfName.Pattern, pattern);
                    break;
                case PdfOperators.ShadingFill:
                    if (LastName(op) is PdfName shading) return (PdfName.Shading, shading);
                    break;
                case PdfOperators.InvokeXObject:
                    if (LastName(op) is PdfName xobject) return (PdfName.XObject, xobject);
                    break;
                case PdfOperators.SetFont:
                    if (op.Operands.Length > 0 && op.Operands[0] is PdfName font) return (PdfName.Font, font);
                    break;
                case PdfOperators.SetExtGState:
                    if (LastName(op) is PdfName state) return (PdfName.ExtGState, state);
                    break;
                case "BDC":
                case "DP":
                    if (op.Operands.Length > 1 && op.Operands[1] is PdfName properties) return (PdfName.Properties, properties);
                    break;
            }
            return (null, null);
        }

        private static bool IsReservedColourSpaceName(string name)
        {
            // Full names alone: the abbreviations (/G, /RGB, /CMYK, /I) are reserved for inline images
            // (§8.9.7), and a cs operand spelled so names a /ColorSpace resource that must exist.
            string expanded = name;
            return expanded == PdfColorSpaceNames.DeviceGray || expanded == PdfColorSpaceNames.DeviceRGB
                   || expanded == PdfColorSpaceNames.DeviceCMYK || expanded == PdfColorSpaceNames.Pattern;
        }

        private static bool Defines(PdfDocument doc, PdfDictionary resources, PdfName category, PdfName name)
            => doc.ResolveObject(resources?[category]) is PdfDictionary entries && entries[name] != null;
    }
}
