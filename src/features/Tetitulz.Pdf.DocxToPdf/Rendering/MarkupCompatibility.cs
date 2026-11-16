// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Markup compatibility (ECMA-376 Part 3) resolved in the parts a conversion reads: every
    /// <c>mc:AlternateContent</c> is replaced, in place, by what its first <c>mc:Choice</c> holds —
    /// Word writes each wps shape, text box and group inside one, its choice the DrawingML this
    /// converter draws, its fallback the VML it does not — else by its <c>mc:Fallback</c>. Done by
    /// this converter rather than by the SDK's own processing, which chooses by the version flags
    /// it is handed and takes the fallback for the 2010 shape namespace; a fallback that is VML is
    /// reported, since nothing here draws it.
    /// </summary>
    internal static class MarkupCompatibility
    {
        /// <summary>Resolves the main document, every header and footer, and the notes parts.</summary>
        internal static void ResolveAlternateContent(WordprocessingDocument document)
        {
            var main = document?.MainDocumentPart;
            if (main == null) return;
            var roots = new List<OpenXmlPartRootElement>();
            if (main.Document != null) roots.Add(main.Document);
            foreach (var header in main.HeaderParts) if (header.Header != null) roots.Add(header.Header);
            foreach (var footer in main.FooterParts) if (footer.Footer != null) roots.Add(footer.Footer);
            if (main.FootnotesPart?.Footnotes != null) roots.Add(main.FootnotesPart.Footnotes);
            if (main.EndnotesPart?.Endnotes != null) roots.Add(main.EndnotesPart.Endnotes);
            foreach (var root in roots) Resolve(root);
        }

        /// <summary>Resolves every <c>mc:AlternateContent</c> below <paramref name="root"/>, outer ones first.</summary>
        internal static void Resolve(OpenXmlElement root)
        {
            if (root == null) return;
            bool vmlReported = false;
            // Document order puts an outer wrapper before the ones inside its choice; the inner ones
            // stay in the tree when the outer's content is moved up, and are resolved in their turn.
            foreach (var alternate in root.Descendants<AlternateContent>().ToList())
            {
                var parent = alternate.Parent;
                if (parent == null) continue;
                OpenXmlElement chosen = alternate.GetFirstChild<AlternateContentChoice>()
                                        ?? (OpenXmlElement)alternate.GetFirstChild<AlternateContentFallback>();
                if (chosen is AlternateContentFallback && !vmlReported
                    && chosen.Descendants().Any(e => e.LocalName == "pict" || e.LocalName == "object"))
                {
                    vmlReported = true;
                    DocxDiagnostics.Report("MarkupCompatibility.Fallback",
                        "an mc:AlternateContent offers no choice and its fallback is VML (w:pict or w:object), which this " +
                        "converter does not draw; nothing was drawn for it. Reported once per part.");
                }
                if (chosen != null)
                {
                    foreach (var child in chosen.ChildElements.ToList())
                    {
                        child.Remove();
                        parent.InsertBefore(child, alternate);
                    }
                }
                alternate.Remove();
            }
        }
    }
}
