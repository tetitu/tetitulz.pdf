// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Checking
{
    public static partial class PdfDeepAccessibilityChecker
    {
        // ================================================================
        //  UA15 — Figure alt-text quality
        // ================================================================

        private static void CheckFigureAltTextQuality(CheckContext ctx)
        {
            var placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "", " ", "image", "figure", "graphic", "picture", "photo", "untitled", "logo" };

            var empty = new List<Occurrence>();
            var generic = new List<Occurrence>();

            foreach (StructNode figure in ctx.Elements)
            {
                // /RoleMap-resolved, so a producer's own illustration type mapped to /Figure has
                // its alt text judged too.
                if (RoleOf(ctx, figure) != StructureTypes.Figure || figure.Dict == null) continue;

                PdfObject altObj = figure.Dict[PdfName.Alt];
                if (altObj is PdfReference) altObj = ResolveObject(ctx.Doc, altObj);
                if (altObj is not PdfString alt) continue;   // missing /Alt is UA07's finding

                string text = alt.GetText()?.Trim() ?? string.Empty;
                if (text.Length == 0) empty.Add(Occurrence.For(ctx, figure));
                else if (placeholders.Contains(text))
                {
                    var occurrence = Occurrence.For(ctx, figure);
                    occurrence.Detail = text;
                    generic.Add(occurrence);
                }
            }

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA15", IssueCategory.Images, WcagLevel.A,
                null, "1.1.1", empty,
                o => "Figure has an empty /Alt string. An empty alt text is only valid for a purely " +
                     "decorative figure, which should be marked as an artifact instead of being a Figure.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " Figure element(s) have an empty /Alt " +
                    "string (e.g. " + objects + ").",
                "Provide meaningful alt text, or mark the image as an artifact in the content stream if it " +
                "is decorative.");

            EmitOccurrences(ctx, IssueSeverity.Warning, "UA15", IssueCategory.Images, WcagLevel.A,
                null, "1.1.1", generic,
                o => "Figure has generic placeholder alt text (\"" + o.Detail + "\"). Screen reader users " +
                     "receive no information from it.",
                (count, objects) =>
                    count.ToString(CultureInfo.InvariantCulture) + " Figure element(s) have generic placeholder " +
                    "alt text such as \"image\" or \"figure\" (e.g. " + objects + ").",
                "Replace placeholder alt text with a description of what each image conveys.");
        }
    }
}
