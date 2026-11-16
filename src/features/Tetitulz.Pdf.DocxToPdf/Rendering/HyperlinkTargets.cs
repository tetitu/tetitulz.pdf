// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Where a <c>w:hyperlink</c> leads — the renderer's and the Builder mapper's one answer: its relationship's
    /// target, looked up in the part that holds the hyperlink, or <c>#</c> and its <c>w:anchor</c>, an internal
    /// bookmark. Whether the target may be written as a link is <c>Core.Writing.LinkTargets</c>'s to say.
    /// </summary>
    /// <remarks>
    /// Reading the anchor alone, an external hyperlink would keep its text and lose its link; looking the
    /// relationship up in the main document part only would miss a header's or a footnote's hyperlink.
    /// <para>A part's relationships are indexed once (<see cref="ByPart"/>), held as long as the part is: a search
    /// for each hyperlink would be hyperlinks × relationships, the two counts a file chooses.</para>
    /// </remarks>
    internal static class HyperlinkTargets
    {
        private static readonly ConditionalWeakTable<OpenXmlPart, Dictionary<string, string>> ByPart =
            new ConditionalWeakTable<OpenXmlPart, Dictionary<string, string>>();

        internal static string Of(Hyperlink hyperlink, MainDocumentPart main)
        {
            var id = hyperlink.Id?.Value;
            if (id != null)
            {
                OpenXmlPart part = hyperlink.Ancestors<OpenXmlPartRootElement>().FirstOrDefault()?.OpenXmlPart ?? main;
                if (part != null && ByPart.GetValue(part, Index).TryGetValue(id, out string target)) return target;
            }
            var anchor = hyperlink.Anchor?.Value;
            return anchor != null ? "#" + anchor : null;
        }

        /// <summary>A part's hyperlink targets by relationship id, the first with an id as a search finds.</summary>
        private static Dictionary<string, string> Index(OpenXmlPart part)
        {
            var index = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var relationship in part.HyperlinkRelationships)
                if (relationship.Id != null && !index.ContainsKey(relationship.Id)) index[relationship.Id] = relationship.Uri?.ToString();
            return index;
        }
    }
}
