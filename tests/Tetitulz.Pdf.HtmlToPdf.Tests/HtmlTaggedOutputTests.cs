// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Tagged (accessible) output from the managed engine: HTML semantics the mapper already
    /// recovers — heading level, <c>&lt;th&gt;</c> vs <c>&lt;td&gt;</c>, <c>alt</c>, <c>lang</c> —
    /// surviving to the PDF instead of being discarded at that boundary.
    ///
    /// <para>Assertions reopen the file and walk the real structure tree. "The string
    /// StructTreeRoot appears in the bytes" would pass against a tree of undifferentiated
    /// paragraphs, which is the failure this whole stage exists to avoid.</para>
    /// </summary>
    public class HtmlTaggedOutputTests
    {
        private static byte[] Tagged(string html)
        {
            var o = Managed();
            o.Tagged = true;
            return ToPdf(html, o);
        }

        private static List<string> Roles(byte[] pdf)
        {
            var roles = new List<string>();
            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);

            var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
            if (root == null) return roles;

            void Walk(PdfObject node, int depth)
            {
                if (depth > 40) return;
                if (doc.ResolveObject(node) is not PdfDictionary dict) return;
                if (doc.ResolveObject(dict.Get(PdfName.S)) is PdfName s) roles.Add(s.Value);
                var k = doc.ResolveObject(dict.Get(PdfName.K));
                if (k is PdfArray arr) foreach (var kid in arr) Walk(kid, depth + 1);
                else if (k != null) Walk(k, depth + 1);
            }

            var rootK = doc.ResolveObject(root.Get(PdfName.K));
            if (rootK is PdfArray ra) foreach (var kid in ra) Walk(kid, 0);
            else if (rootK != null) Walk(rootK, 0);
            return roles;
        }

        /// <summary>The first structure element with the given role, or null.</summary>
        private static PdfDictionary FindElement(byte[] pdf, string role)
        {
            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
            if (root == null) return null;

            PdfDictionary found = null;
            void Walk(PdfObject node, int depth)
            {
                if (found != null || depth > 40) return;
                if (doc.ResolveObject(node) is not PdfDictionary dict) return;
                if ((doc.ResolveObject(dict.Get(PdfName.S)) as PdfName)?.Value == role) { found = dict; return; }
                var k = doc.ResolveObject(dict.Get(PdfName.K));
                if (k is PdfArray arr) foreach (var kid in arr) Walk(kid, depth + 1);
                else if (k != null) Walk(k, depth + 1);
            }
            var rootK = doc.ResolveObject(root.Get(PdfName.K));
            if (rootK is PdfArray ra) foreach (var kid in ra) Walk(kid, 0);
            return found;
        }

        // ------------------------------------------------------------------ opt-out

        [Fact]
        public void TaggedFalse_EmitsNoStructureAtAll()
        {
            var o = Managed();
            o.Tagged = false;
            string raw = Raw(ToPdf("<h1>Title</h1><p>Body</p>", o));

            Assert.DoesNotContain("/StructTreeRoot", raw);
            Assert.DoesNotContain("/MarkInfo", raw);
            Assert.DoesNotContain("BDC", DecodeStreams(ToPdf("<h1>Title</h1><p>Body</p>", o)));
        }

        // ------------------------------------------------------------------ headings

        /// <summary>
        /// The heading LEVEL is what makes a tag tree worth having. A document tagged as
        /// undifferentiated paragraphs passes every structural check and still tells a screen
        /// reader nothing about the document's shape.
        /// </summary>
        [Fact]
        public void HeadingLevels_SurviveToTheStructureTree()
        {
            var roles = Roles(Tagged("<h1>One</h1><h2>Two</h2><p>Body</p><h3>Three</h3>"));

            Assert.Contains(PdfStructureTypes.H1, roles);
            Assert.Contains(PdfStructureTypes.H2, roles);
            Assert.Contains(PdfStructureTypes.H3, roles);
            Assert.Contains(PdfStructureTypes.P, roles);

            // …and in document order.
            Assert.True(roles.IndexOf(PdfStructureTypes.H1) < roles.IndexOf(PdfStructureTypes.H2));
            Assert.True(roles.IndexOf(PdfStructureTypes.H2) < roles.IndexOf(PdfStructureTypes.H3));
        }

        [Fact]
        public void BlockquoteAndPre_CarryTheirOwnRoles()
        {
            var roles = Roles(Tagged("<blockquote>Quoted</blockquote><pre>code()</pre>"));

            Assert.Contains(PdfStructureTypes.BlockQuote, roles);
            Assert.Contains(PdfStructureTypes.Code, roles);
        }

        // ------------------------------------------------------------------ tables

        [Fact]
        public void TableStructure_IsTableRowAndCells()
        {
            var roles = Roles(Tagged(
                "<table><tr><th>Item</th><th>Price</th></tr><tr><td>Widget</td><td>10</td></tr></table>"));

            Assert.Contains(PdfStructureTypes.Table, roles);
            Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.TR));
            Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.TH));
            Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.TD));
        }

        [Fact]
        public void HeaderCell_CarriesAScope()
        {
            // A /TH with neither a scope nor a headers association is reported by PDF/UA checkers,
            // so the scope is inferred from position when the author did not declare one.
            byte[] pdf = Tagged("<table><tr><th>Item</th></tr><tr><td>Widget</td></tr></table>");
            var th = FindElement(pdf, PdfStructureTypes.TH);

            Assert.NotNull(th);
            var attrs = th.Get(PdfName.A) as PdfDictionary;
            Assert.NotNull(attrs);
            Assert.Equal("Column", (attrs.Get(PdfName.Scope) as PdfName)?.Value);
        }

        [Fact]
        public void ExplicitScopeAttribute_IsHonoured()
        {
            byte[] pdf = Tagged("<table><tr><th scope=\"row\">Item</th><td>Widget</td></tr></table>");
            var th = FindElement(pdf, PdfStructureTypes.TH);

            var attrs = th.Get(PdfName.A) as PdfDictionary;
            Assert.Equal("Row", (attrs.Get(PdfName.Scope) as PdfName)?.Value);
        }

        [Fact]
        public void EmptyCell_StillAppearsSoTheTableStaysRectangular()
        {
            // Dropping an empty cell makes every later row look misaligned to a regularity check.
            var roles = Roles(Tagged("<table><tr><td>A</td><td></td></tr></table>"));

            Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.TD));
        }

        [Fact]
        public void Caption_IsTaggedAsACaption()
        {
            var roles = Roles(Tagged("<table><caption>Sales</caption><tr><td>A</td></tr></table>"));
            Assert.Contains(PdfStructureTypes.Caption, roles);
        }

        // ------------------------------------------------------------------ figures

        [Fact]
        public void ImageWithAlt_IsAFigureCarryingIt()
        {
            string png = "data:image/png;base64," + Convert.ToBase64String(MakeRgbPng(4, 4));
            byte[] pdf = Tagged($"<img src=\"{png}\" width=\"20\" height=\"20\" alt=\"A red square\">");

            var figure = FindElement(pdf, PdfStructureTypes.Figure);
            Assert.NotNull(figure);
            Assert.Equal("A red square", (figure.Get(PdfName.Alt) as PdfString)?.GetText());
        }

        /// <summary>
        /// HTML defines <c>alt=""</c> as "this image is decorative", which maps exactly onto an
        /// artifact. Absent and empty are different answers and must stay so.
        /// </summary>
        [Fact]
        public void ImageWithEmptyAlt_IsAnArtifactNotAFigure()
        {
            string png = "data:image/png;base64," + Convert.ToBase64String(MakeRgbPng(4, 4));
            byte[] pdf = Tagged($"<img src=\"{png}\" width=\"20\" height=\"20\" alt=\"\">");

            Assert.DoesNotContain(PdfStructureTypes.Figure, Roles(pdf));
            Assert.Contains("/Artifact", DecodeStreams(pdf));
        }

        [Fact]
        public void ImageWithNoAltAttribute_IsStillAFigure_WithNoAlt()
        {
            // The honest outcome: a checker reports the missing description rather than the
            // engine inventing one.
            string png = "data:image/png;base64," + Convert.ToBase64String(MakeRgbPng(4, 4));
            byte[] pdf = Tagged($"<img src=\"{png}\" width=\"20\" height=\"20\">");

            var figure = FindElement(pdf, PdfStructureTypes.Figure);
            Assert.NotNull(figure);
            Assert.Null(figure.Get(PdfName.Alt));
        }

        // ------------------------------------------------------------------ language

        [Fact]
        public void HtmlLangAttribute_BecomesTheCatalogLanguage()
        {
            byte[] pdf = Tagged("<html lang=\"he-IL\"><body><p>שלום</p></body></html>");

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            Assert.Equal("he-IL", (doc.Catalog.Dictionary.Get(PdfName.Lang) as PdfString)?.GetText());
        }

        [Fact]
        public void NoLangAnywhere_WritesNoLanguage()
        {
            // No default is invented: a wrong /Lang makes a screen reader mispronounce the whole
            // document while the conformance check reports clean.
            byte[] pdf = Tagged("<p>Body</p>");

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            Assert.Null(doc.Catalog.Dictionary.Get(PdfName.Lang));
        }

        // ------------------------------------------------------------------ artifacts

        [Fact]
        public void HorizontalRule_IsAnArtifactNotAnEmptyParagraph()
        {
            // An <hr> is decoration. Tagged as a /P it would be a paragraph containing nothing —
            // hollow tagging, which is exactly what a PDF/UA checker reports.
            byte[] pdf = Tagged("<p>Above</p><hr><p>Below</p>");
            var roles = Roles(pdf);

            Assert.Equal(2, roles.Count(r => r == PdfStructureTypes.P));
            Assert.Contains("/Artifact", DecodeStreams(pdf));
        }

        [Fact]
        public void EveryMarkedSequenceIsBalanced()
        {
            string dec = DecodeStreams(Tagged(
                "<h1>Title</h1><p>Body <b>bold</b></p>" +
                "<table><tr><th>H</th><td>D</td></tr></table><hr>"));

            Assert.Equal(Regex.Matches(dec, @"\bBDC\b").Count, Regex.Matches(dec, @"\bEMC\b").Count);
        }

        [Fact]
        public void DocumentDeclaresItselfMarked()
        {
            using var ms = new MemoryStream(Tagged("<p>Body</p>"));
            using var doc = PdfDocument.Open(ms);

            var markInfo = doc.Catalog.Dictionary.Get(PdfName.MarkInfo) as PdfDictionary;
            Assert.NotNull(markInfo);
            Assert.True((markInfo.Get(PdfName.Marked) as PdfBoolean)?.Value);
        }

        // ------------------------------------------------------------------ pagination

        [Fact]
        public void PaginatedDocument_GivesEachPageAStructParentsKey()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 150; i++) sb.Append($"<p>Paragraph {i}</p>");
            byte[] pdf = Tagged(sb.ToString());

            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);
            Assert.True(doc.Pages.Count >= 2, "fixture must paginate");

            var keys = doc.Pages
                .Select(pg => (doc.ResolveObject(pg.Dictionary.Get(PdfName.StructParents)) as PdfNumber)?.IntValue)
                .ToList();

            Assert.All(keys, k => Assert.NotNull(k));
            Assert.Equal(keys.Count, keys.Distinct().Count());   // one key per page, all different
        }
    }
}
