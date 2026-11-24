// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests
{
    /// <summary>
    /// <see cref="PdfTaggedDocumentBuilder"/> — the code-first tagged-PDF builder.
    ///
    /// <para><b>Eight of these tests used to end in <c>Assert.True(ms.Length &gt; 0)</c></b> under
    /// names like <c>Build_WithHeading_NoException</c>. Any builder that emitted a blank page — or
    /// a structure tree with nothing in it — satisfied every one of them. They now read the
    /// produced document back and assert the structure element roles, the alt text, the language
    /// and the XMP title.</para>
    /// </summary>
    public class PdfTaggedDocumentBuilderTests
    {
        // ---------------------------------------------------------------
        // 1. Empty document builds without exception; starts with %PDF
        // ---------------------------------------------------------------
        [Fact]
        public void Build_Empty_ProducesValidPdf()
        {
            var ms = new MemoryStream();
            new PdfTaggedDocumentBuilder().Build(ms);

            ms.Position = 0;
            var header = new byte[5];
            ms.Read(header, 0, 5);
            Assert.Equal("%PDF-", Encoding.ASCII.GetString(header));
        }

        /// <summary>
        /// A title holding a character XML 1.0 forbids (U+0001) still yields a well-formed XMP packet:
        /// the character becomes U+FFFD, through the one <c>XmlLegalText</c>, before the markup
        /// characters are escaped — escaping cannot make such a character legal. The control, a plain
        /// title, parses too and is carried unchanged.
        /// </summary>
        [Theory]
        [InlineData("A\u0001B", "A�B")]
        [InlineData("Plain <title>", "Plain &lt;title&gt;")]
        public void Build_TitleWithACharacterXmlForbids_WritesAWellFormedPacket(string title, string expected)
        {
            byte[] pdf = BuildToBytes(b => b.SetTitle(title));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var metadata = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]) as Tetitulz.Pdf.Core.Objects.PdfStream;
            Assert.NotNull(metadata);
            string xmp = Encoding.UTF8.GetString(metadata!.GetDecodedData());

            Assert.Contains(expected, xmp, StringComparison.Ordinal);
            Assert.DoesNotContain("\u0001", xmp, StringComparison.Ordinal);
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xmp), settings);
            while (reader.Read()) { }
        }

        // ---------------------------------------------------------------
        // 2. A title reaches the two places PDF/UA looks for it
        // ---------------------------------------------------------------

        /// <summary>
        /// PDF/UA-1 requires the title in the XMP <c>dc:title</c> and
        /// <c>/ViewerPreferences /DisplayDocTitle true</c> so a reader shows it instead of the
        /// filename (UA04). This builder writes no <c>/Info</c> dictionary at all, so XMP is the
        /// only place it can be — which is exactly why "the stream is non-empty" told us nothing.
        /// </summary>
        [Fact]
        public void Build_WithTitle_WritesDcTitleAndDisplayDocTitle()
        {
            byte[] pdf = BuildToBytes(b => b.SetTitle("My Tagged Document"));

            Assert.Contains("<dc:title>", Encoding.UTF8.GetString(pdf));
            Assert.Contains("My Tagged Document", Encoding.UTF8.GetString(pdf));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var prefs = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.ViewerPreferences]) as PdfDictionary;
            Assert.NotNull(prefs);
            Assert.True((doc.ResolveObject(prefs![PdfName.DisplayDocTitle]) as PdfBoolean)?.Value);
        }

        // ---------------------------------------------------------------
        // 3. The language reaches the catalog
        // ---------------------------------------------------------------
        [Fact]
        public void Build_WithLanguage_WritesTheCatalogLangEntry()
        {
            byte[] pdf = BuildToBytes(b => b.SetLanguage("en-US"));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var lang = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Lang]) as PdfString;
            Assert.Equal("en-US", lang?.GetText());
        }

        // ---------------------------------------------------------------
        // 4-6. Each element type produces its own structure element
        // ---------------------------------------------------------------

        /// <summary>
        /// A paragraph must produce a <c>/P</c> structure element <b>and</b> draw its text. Both
        /// halves matter: a tree with no content is the hollow tagging the accessibility checkers
        /// exist to catch, and content with no tree is an untagged document.
        /// </summary>
        [Fact]
        public void Build_WithParagraph_ProducesAParagraphElementAndDrawsTheText()
        {
            byte[] pdf = BuildToBytes(b => b.AddParagraph("Hello, world!"));

            Assert.Equal(new[] { "Document", "P" }, StructureRoles(pdf));
            Assert.Contains("/P <</MCID 0>> BDC", Encoding.ASCII.GetString(pdf));
            // Read back through the structure — the MCID links the element to the drawn text, and
            // the embedded font's /ToUnicode turns the glyphs back into it.
            Assert.Equal("Hello, world!", PdfTaggedTextExtractor.ExtractText(new MemoryStream(pdf)));
        }

        /// <summary>A level-1 heading must be tagged <c>/H1</c>, not <c>/P</c>.</summary>
        [Theory]
        [InlineData(1, "H1")]
        [InlineData(2, "H2")]
        [InlineData(3, "H3")]
        public void Build_WithHeading_TagsItAtTheRequestedLevel(int level, string expectedRole)
        {
            byte[] pdf = BuildToBytes(b => b.AddHeading(level, "Introduction"));

            Assert.Equal(new[] { "Document", expectedRole }, StructureRoles(pdf));
            Assert.Equal("Introduction", PdfTaggedTextExtractor.ExtractText(new MemoryStream(pdf)));
        }

        /// <summary>
        /// A figure must carry its description as <c>/Alt</c> on the structure element — that text
        /// is the entire accessibility value of the call, and a figure without it fails UA.
        /// </summary>
        [Fact]
        public void Build_WithFigure_CarriesTheDescriptionAsAltText()
        {
            byte[] pdf = BuildToBytes(b => b.AddFigure("A chart showing sales data"));

            Assert.Equal(new[] { "Document", "Figure" }, StructureRoles(pdf));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var figure = FindElement(doc, "Figure");
            Assert.Equal("A chart showing sales data",
                (doc.ResolveObject(figure[PdfName.Alt]) as PdfString)?.GetText());
        }

        // ---------------------------------------------------------------
        // 7. Output contains /Marked (MarkInfo)
        // ---------------------------------------------------------------
        [Fact]
        public void Build_OutputContainsMarkInfo()
        {
            byte[] pdf = BuildToBytes(b => b.AddParagraph("Test"));
            Assert.Contains("Marked", Encoding.ASCII.GetString(pdf));
        }

        // ---------------------------------------------------------------
        // 8. Output contains StructTreeRoot
        // ---------------------------------------------------------------
        [Fact]
        public void Build_OutputContainsStructTreeRoot()
        {
            byte[] pdf = BuildToBytes(b => b.AddParagraph("Tree check"));
            Assert.Contains("StructTreeRoot", Encoding.ASCII.GetString(pdf));
        }

        // ---------------------------------------------------------------
        // 9. XMP stream contains pdfuaid:part=1
        // ---------------------------------------------------------------
        [Fact]
        public void Build_OutputContainsXmp_PdfuaidPart()
        {
            byte[] pdf = BuildToBytes(b => b.SetTitle("UA doc"));
            // XMP is UTF-8 embedded in the binary PDF; search raw bytes
            string pdfText = Encoding.UTF8.GetString(pdf);
            Assert.Contains("pdfuaid:part", pdfText);
            Assert.Contains(">1<", pdfText);
        }

        // ---------------------------------------------------------------
        // 10. Mixed elements keep their order and their individual roles
        // ---------------------------------------------------------------

        /// <summary>
        /// Reading order <b>is</b> the structure tree's order, so a builder that emitted the right
        /// elements in the wrong sequence would be broken while still producing a non-empty stream.
        /// The whole ordered role list is asserted, not merely that some elements exist.
        /// </summary>
        [Fact]
        public void Build_MultipleElements_KeepsTheirOrderAndRoles()
        {
            byte[] pdf = BuildToBytes(b => b
                .SetTitle("Report")
                .SetLanguage("en-US")
                .AddHeading(1, "Chapter 1")
                .AddParagraph("First paragraph.")
                .AddFigure("Bar chart")
                .AddHeading(2, "Section 1.1")
                .AddParagraph("Second paragraph."));

            Assert.Equal(
                new[] { "Document", "H1", "P", "Figure", "H2", "P" },
                StructureRoles(pdf));

            // Every element points at a distinct MCID, so the /ParentTree can resolve them.
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var mcids = new List<long>();
            foreach (var element in Elements(doc))
                if (doc.ResolveObject(element[PdfName.K]) is PdfNumber mcid) mcids.Add(mcid.LongValue);

            Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, mcids);
        }

        // ---------------------------------------------------------------
        // 11. Hebrew language tag appears in output
        // ---------------------------------------------------------------
        [Fact]
        public void Build_HebrewLanguage_SetsLang()
        {
            byte[] pdf = BuildToBytes(b => b.SetLanguage("he-IL").AddParagraph("שלום"));
            string pdfText = Encoding.UTF8.GetString(pdf);
            Assert.Contains("he-IL", pdfText);
        }

        // ---------------------------------------------------------------
        // 12. A null title is dropped, not written as the text "null"
        // ---------------------------------------------------------------

        /// <summary>
        /// The interesting outcome is not that <c>SetTitle(null)</c> fails to throw — it is that no
        /// title is written at all, rather than an empty or literal-"null" <c>dc:title</c> that a
        /// checker would accept as a real one and a reader would display.
        /// </summary>
        [Fact]
        public void SetTitle_Null_WritesNoTitleRatherThanAnEmptyOne()
        {
            byte[] pdf = BuildToBytes(b => b.SetTitle(null));
            string text = Encoding.UTF8.GetString(pdf);

            Assert.DoesNotContain("<dc:title>", text);
            Assert.DoesNotContain("null", text, StringComparison.OrdinalIgnoreCase);

            // Still a well-formed document.
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Single(doc.Pages);
        }

        // ---------------------------------------------------------------
        // 13. The PdfOperations factory returns an equivalent builder
        // ---------------------------------------------------------------

        /// <summary>
        /// The factory is a convenience wrapper, so its output must be structurally identical to a
        /// directly-constructed builder's. Comparing the two is what makes this fail if the factory
        /// ever hands back something differently configured; "the builder is not null" would not.
        /// </summary>
        [Fact]
        public void CreateTaggedDocument_ViaOperations_BuildsTheSameStructureAsTheConstructor()
        {
            var viaFactory = new MemoryStream();
            PdfOperations.CreateTaggedDocument().SetFontProvider(_ => TestFont)
                .AddHeading(1, "Title").AddParagraph("Created via PdfOperations").Build(viaFactory);

            var viaConstructor = new MemoryStream();
            new PdfTaggedDocumentBuilder().SetFontProvider(_ => TestFont)
                .AddHeading(1, "Title").AddParagraph("Created via PdfOperations").Build(viaConstructor);

            Assert.Equal(new[] { "Document", "H1", "P" }, StructureRoles(viaFactory.ToArray()));
            Assert.Equal(StructureRoles(viaConstructor.ToArray()), StructureRoles(viaFactory.ToArray()));
        }

        // ---------------------------------------------------------------
        // 14. It embeds the font it declares PDF/UA with — or refuses
        // ---------------------------------------------------------------

        /// <summary>
        /// ISO 14289-1 7.21.4.1: every font used for rendering is embedded — not a non-embedded
        /// Helvetica under a <c>pdfuaid:part</c> declaration.
        /// </summary>
        [Fact]
        public void Build_EmbedsTheFontItDrawsWith()
        {
            byte[] pdf = BuildToBytes(b => b.AddParagraph("Embedded"));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var fonts = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font])!;
            var font = (PdfDictionary)doc.ResolveObject(Assert.Single(fonts).Value)!;
            Assert.Equal("Type0", ((PdfName)font[PdfName.Subtype]!).Value);
            var descendant = (PdfDictionary)doc.ResolveObject(((PdfArray)doc.ResolveObject(font[PdfName.DescendantFonts])!)[0])!;
            var descriptor = (PdfDictionary)doc.ResolveObject(descendant[PdfName.FontDescriptor])!;
            Assert.IsType<PdfStream>(doc.ResolveObject(descriptor[PdfName.FontFile2]));
        }

        /// <summary>
        /// With text and no font program — none supplied, no installed one allowed — the document is
        /// refused, naming what to supply, and nothing is written. The text-free control builds.
        /// </summary>
        [Fact]
        public void Build_WithTextAndNoFont_IsRefused_BeforeAnythingIsWritten()
        {
            var output = new MemoryStream();
            var ex = Assert.Throws<Core.PdfException>(
                () => new PdfTaggedDocumentBuilder().AddParagraph("No font").Build(output));

            Assert.Contains("embeds every font it draws with", ex.Message, StringComparison.Ordinal);
            Assert.Contains("SetFontProvider", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, output.Length);

            var figureOnly = new MemoryStream();
            new PdfTaggedDocumentBuilder().AddFigure("A diagram").Build(figureOnly);
            Assert.Equal(new[] { "Document", "Figure" }, StructureRoles(figureOnly.ToArray()));
        }

        /// <summary>
        /// An empty paragraph draws nothing, so it needs no font — its element and its marked-content
        /// sequence are still written, and the page names no font it does not use.
        /// </summary>
        [Fact]
        public void Build_AnEmptyParagraphNeedsNoFont_AndKeepsItsElement()
        {
            var output = new MemoryStream();
            new PdfTaggedDocumentBuilder().AddParagraph("").Build(output);
            byte[] pdf = output.ToArray();

            Assert.Equal(new[] { "Document", "P" }, StructureRoles(pdf));
            Assert.Contains("/P <</MCID 0>> BDC\nEMC", Encoding.ASCII.GetString(pdf));
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Null(doc.Pages[0].Resources[PdfName.Font]);
        }

        /// <summary>
        /// Text is drawn and read back as written: past U+007F — where the ASCII encoding this
        /// replaced wrote a question mark for é, ü and all of a Hebrew paragraph — and with the
        /// characters a literal string had to escape. A control character is drawn as a space: the
        /// paragraph is one line, and no font has a glyph for it.
        /// </summary>
        [Theory]
        [InlineData("Café über", "Café über")]
        [InlineData("שלום", "שלום")]
        [InlineData(@"a(b)c\d", @"a(b)c\d")]
        [InlineData("alpha\rbeta\tgamma", "alpha beta gamma")]
        public void Build_TextIsReadBackAsWritten_WithControlCharactersAsSpaces(string paragraph, string expected)
        {
            byte[] pdf = BuildToBytes(b => b.AddParagraph(paragraph));

            Assert.Equal(expected, PdfTaggedTextExtractor.ExtractText(new MemoryStream(pdf)));
        }

        /// <summary>
        /// §14.7.4.4: the parent tree is keyed by the page's <c>/StructParents</c> and holds an
        /// array indexed by MCID. Keyed by the MCIDs, with no <c>/StructParents</c> on the page, no
        /// element could be found from its content. Read raw — this is the shape a
        /// reader relies on, and the extractor above walks the tree from the other end.
        /// </summary>
        [Fact]
        public void Build_ParentTreeMapsThePagesMarkedContentByMcid()
        {
            byte[] pdf = BuildToBytes(b => b.AddHeading(1, "One").AddFigure("Two").AddParagraph("Three"));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            int key = ((PdfNumber)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.StructParents])!).IntValue;
            var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot])!;
            var tree = (PdfDictionary)doc.ResolveObject(root[PdfName.ParentTree])!;
            var nums = (PdfArray)doc.ResolveObject(tree[PdfName.Nums])!;

            Assert.Equal(2, nums.Count);
            Assert.Equal(key, ((PdfNumber)nums[0]).IntValue);
            var byMcid = (PdfArray)doc.ResolveObject(nums[1])!;
            Assert.Equal(new[] { "H1", "Figure", "P" },
                Enumerable.Range(0, byMcid.Count).Select(mcid =>
                {
                    var element = (PdfDictionary)doc.ResolveObject(byMcid[mcid])!;
                    Assert.Equal(mcid, ((PdfNumber)element[PdfName.K]!).IntValue);
                    return ((PdfName)element[PdfName.S]!).Value;
                }));
            Assert.Equal(key + 1, ((PdfNumber)root[PdfName.ParentTreeNextKey]!).IntValue);
        }

        /// <summary>
        /// The content stream's numbers do not follow the current culture. The line position was an
        /// interpolated <c>{y:F2}</c>, which a comma-decimal culture wrote as <c>791,89</c> — two
        /// operands, and a content stream no reader parses as intended.
        /// </summary>
        [Fact]
        public void Build_WritesTheContentStreamsNumbersTheSameUnderACommaDecimalCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            byte[] pdf;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                pdf = BuildToBytes(b => b.AddParagraph("Line"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }

            Assert.Contains("50 791.89 Td", Encoding.ASCII.GetString(pdf));
            Assert.Equal("Line", PdfTaggedTextExtractor.ExtractText(new MemoryStream(pdf)));
        }

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

        /// <summary>
        /// One font for every test: printable ASCII and the letters past it these tests draw. Supplied
        /// through the provider so no host font decides the outcome.
        /// </summary>
        private static readonly byte[] TestFont = MinimalTrueType.Build(
            new string(Enumerable.Range(0x20, 0x5F).Select(c => (char)c).ToArray()) + "éüשלום");

        private static byte[] BuildToBytes(Action<PdfTaggedDocumentBuilder> configure)
        {
            var builder = new PdfTaggedDocumentBuilder().SetFontProvider(_ => TestFont);
            configure(builder);
            var ms = new MemoryStream();
            builder.Build(ms);
            return ms.ToArray();
        }

        /// <summary>
        /// The <c>/S</c> values of every structure element, in document order, starting at the
        /// <c>/StructTreeRoot</c>. A leaf element's <c>/K</c> is an MCID <i>number</i>, not a
        /// dictionary, so the walk must stop there rather than trying to resolve it.
        /// </summary>
        private static List<string> StructureRoles(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            var roles = new List<string>();
            var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
            Assert.NotNull(root);
            WalkKids(doc, root![PdfName.K], roles, 0);
            return roles;
        }

        private static void WalkKids(PdfDocument doc, PdfObject kids, List<string> roles, int depth)
        {
            if (depth > 16 || kids == null) return;

            var resolved = doc.ResolveObject(kids);
            if (resolved is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++) WalkKids(doc, array[i], roles, depth + 1);
                return;
            }
            if (resolved is not PdfDictionary element) return;   // an MCID number: a leaf

            var role = (element[PdfName.S] as PdfName)?.Value;
            if (role != null) roles.Add(role);

            WalkKids(doc, element[PdfName.K], roles, depth + 1);
        }

        /// <summary>Every structure element below the root, in document order.</summary>
        private static List<PdfDictionary> Elements(PdfDocument doc)
        {
            var found = new List<PdfDictionary>();
            var root = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.StructTreeRoot]) as PdfDictionary;
            CollectLeaves(doc, root?[PdfName.K], found, 0);
            return found;
        }

        private static void CollectLeaves(PdfDocument doc, PdfObject node, List<PdfDictionary> found, int depth)
        {
            if (depth > 16 || node == null) return;

            var resolved = doc.ResolveObject(node);
            if (resolved is PdfArray array)
            {
                for (int i = 0; i < array.Count; i++) CollectLeaves(doc, array[i], found, depth + 1);
                return;
            }
            if (resolved is not PdfDictionary element) return;

            if (doc.ResolveObject(element[PdfName.K]) is PdfNumber) found.Add(element);
            else CollectLeaves(doc, element[PdfName.K], found, depth + 1);
        }

        private static PdfDictionary FindElement(PdfDocument doc, string role)
        {
            foreach (var element in Elements(doc))
                if ((element[PdfName.S] as PdfName)?.Value == role) return element;

            throw new Xunit.Sdk.XunitException($"no /{role} structure element in the produced document");
        }
    }
}
