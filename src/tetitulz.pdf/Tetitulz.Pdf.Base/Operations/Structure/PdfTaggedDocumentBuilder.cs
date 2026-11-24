// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Metadata;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// Internal model for a tagged PDF structure element.
    /// </summary>
    internal sealed class PdfTaggedElement
    {
        /// <summary>Structure type (P, H1, H2, H3, H4, H5, H6, Figure, Document).</summary>
        public string Type { get; set; }

        /// <summary>Text content (for P and heading elements).</summary>
        public string Text { get; set; }

        /// <summary>Alternate text description (for Figure elements).</summary>
        public string AltText { get; set; }
    }

    /// <summary>
    /// Builds a complete Tagged PDF (PDF/UA-1) document in memory: one page of marked-content
    /// sequences, a structure tree whose parent tree maps them back, the font the text is drawn with
    /// embedded, and an XMP packet declaring <c>pdfuaid:part=1</c>. Does not depend on SkiaSharp.
    /// </summary>
    /// <remarks>
    /// <para><b>It declares PDF/UA-1, so it embeds its font — and refuses when it cannot.</b>
    /// ISO 14289-1 §7.21.4.1 requires every font used for rendering to be embedded, and ASCII would
    /// turn every character past U+007F — é, ü, any Hebrew — into a question mark. The text takes the
    /// producers' path: <see cref="TextRunComposer"/> shapes it against a font program from
    /// <see cref="SetFontProvider"/>, or an installed one when <see cref="AllowSystemFontSubstitution"/>
    /// says that redistributing it is acceptable, and the glyphs used are embedded with a
    /// <c>/ToUnicode</c>. A document with text and neither is refused, not written under a claim it
    /// does not meet.</para>
    ///
    /// <para><b>The parent tree maps the page's marked content (§14.7.4.4).</b> It is keyed by the
    /// page's <c>/StructParents</c>, and the value is an array indexed by MCID — not keyed by the
    /// MCIDs themselves with no <c>/StructParents</c> on the page, which would leave nothing
    /// reachable from the content side.</para>
    /// </remarks>
    public sealed class PdfTaggedDocumentBuilder
    {
        private string _language = "en-US";
        private string _title = string.Empty;
        private readonly List<PdfTaggedElement> _elements = new List<PdfTaggedElement>();
        private Func<string, byte[]> _fontProvider;
        private bool _allowSystemFontSubstitution;

        /// <summary>The page's <c>/StructParents</c>: its key in the parent tree.</summary>
        private const int PageStructParents = 0;

        // Page dimensions: A4 in PDF points.
        //
        // PdfPageSizes stores these as float, and 595.28f / 841.89f are not exactly
        // representable: widening them to double yields 595.280029296875 and
        // 841.8900146484375, which PdfNumber's "F6" formatting writes into /MediaBox
        // as "595.280029" / "841.890015". Rounding back to the two decimals the
        // constant documents writes the box as 595.28 / 841.89, while still deriving
        // it from the shared constant.
        private static readonly double PageWidth  = Math.Round((double)PdfPageSizes.A4Width,  2);
        private static readonly double PageHeight = Math.Round((double)PdfPageSizes.A4Height, 2);

        /// <summary>Sets the natural language of the document (e.g. "en-US" or "he-IL").</summary>
        public PdfTaggedDocumentBuilder SetLanguage(string lang)
        {
            _language = lang ?? string.Empty;
            return this;
        }

        /// <summary>
        /// Sets the document title, written to the XMP <c>dc:title</c> — where PDF/UA reads it — and
        /// shown by viewers through <c>/DisplayDocTitle</c>.
        /// </summary>
        public PdfTaggedDocumentBuilder SetTitle(string title)
        {
            _title = title ?? string.Empty;
            return this;
        }

        /// <summary>
        /// Supplies the font the text is drawn with and embedded as: the bytes of a TrueType or
        /// OpenType file. The argument is a family hint; return null to decline.
        /// </summary>
        public PdfTaggedDocumentBuilder SetFontProvider(Func<string, byte[]> provider)
        {
            _fontProvider = provider;
            return this;
        }

        /// <summary>
        /// Allows a font installed on this machine to be embedded when no provider supplies one.
        /// </summary>
        /// <remarks>
        /// Off by default, and that is a licensing decision: embedding a font redistributes it, and
        /// only the caller can say that is acceptable. The same setting, and the same default, as
        /// <c>FormFillOptions.AllowSystemFontSubstitution</c>.
        /// </remarks>
        public PdfTaggedDocumentBuilder AllowSystemFontSubstitution(bool allow = true)
        {
            _allowSystemFontSubstitution = allow;
            return this;
        }

        /// <summary>Adds a paragraph (P) structure element with the given text.</summary>
        public PdfTaggedDocumentBuilder AddParagraph(string text)
        {
            _elements.Add(new PdfTaggedElement { Type = "P", Text = text ?? string.Empty });
            return this;
        }

        /// <summary>
        /// Adds a heading structure element. <paramref name="level"/> must be 1–6;
        /// values outside that range are clamped.
        /// </summary>
        public PdfTaggedDocumentBuilder AddHeading(int level, string text)
        {
            level = Math.Max(1, Math.Min(6, level));
            _elements.Add(new PdfTaggedElement { Type = $"H{level}", Text = text ?? string.Empty });
            return this;
        }

        /// <summary>Adds a Figure structure element with an /Alt description.</summary>
        public PdfTaggedDocumentBuilder AddFigure(string altText)
        {
            _elements.Add(new PdfTaggedElement { Type = "Figure", AltText = altText ?? string.Empty });
            return this;
        }

        /// <summary>Writes the complete tagged PDF to <paramref name="output"/>.</summary>
        /// <exception cref="Core.PdfException">
        /// The document has text and no font program covering it is available — none from
        /// <see cref="SetFontProvider"/>, and no installed one allowed. Nothing is written.
        /// </exception>
        public void Build(Stream output)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));

            // Shaped before a byte is written, so a refusal leaves the output untouched.
            ShapedText text = ShapeText();

            var writer = new PdfWriter(output);

            // PDF 1.7 header (PDF/UA requires at least 1.7 for modern tools)
            writer.WriteRaw("%PDF-1.7\n");
            writer.WriteRaw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            int nextObj = 1;

            // Reserve object numbers for top-level objects
            int catalogObjNum       = nextObj++;  // 1
            int pagesRootObjNum     = nextObj++;  // 2
            int pageObjNum          = nextObj++;  // 3
            int contentStreamObjNum = nextObj++;  // 4
            int xmpStreamObjNum     = nextObj++;  // 5
            int structTreeRootNum   = nextObj++;  // 6
            int docStructElemNum    = nextObj++;  // 7 — Document root StructElem
            int parentTreeObjNum    = nextObj++;  // 8

            // Each element gets its own StructElem object number
            var elemObjNums = new List<int>();
            for (int i = 0; i < _elements.Count; i++)
                elemObjNums.Add(nextObj++);

            // The embedded font's objects come last, numbered by the builder that makes them.
            int fontObjNum = 0;
            List<(int objNum, PdfObject obj)> fontObjects = null;
            if (text != null)
                (fontObjNum, fontObjects) = Type0FontBuilder.BuildObjects(ref nextObj, text.Program, text.GidToUnicode, text.GidToText);

            byte[] contentBytes = BuildContentStream(_elements, text);
            byte[] xmpBytes = BuildXmpStream(_title, _language);

            var xrefEntries = new List<(int objNum, long offset)>();
            xrefEntries.Add((0, 0)); // free head

            if (fontObjects != null)
                foreach (var (num, obj) in fontObjects)
                    xrefEntries.Add((num, writer.WriteIndirectObject(num, 0, obj)));

            var contentStream = new PdfStream();
            contentStream.Set(PdfName.Length, new PdfNumber(contentBytes.Length));
            contentStream.RawData = contentBytes;
            xrefEntries.Add((contentStreamObjNum, writer.WriteIndirectObject(contentStreamObjNum, 0, contentStream)));

            var xmpStream = new PdfStream();
            xmpStream.Set(PdfName.Type, PdfName.Metadata);
            xmpStream.Set(PdfName.Subtype, PdfName.XML);
            xmpStream.Set(PdfName.Length, new PdfNumber(xmpBytes.Length));
            xmpStream.RawData = xmpBytes;
            xrefEntries.Add((xmpStreamObjNum, writer.WriteIndirectObject(xmpStreamObjNum, 0, xmpStream)));

            // Structure elements, and the parent tree's entry for the page: an array indexed by
            // MCID, holding the element each marked-content sequence belongs to.
            var mcidParents = new PdfArray();
            for (int i = 0; i < _elements.Count; i++)
            {
                var el = _elements[i];
                var elemDict = new PdfDictionary();
                elemDict.Set(PdfName.Type, PdfName.StructElem);
                elemDict.Set(PdfName.S, new PdfName(el.Type));
                elemDict.Set(PdfName.P, new PdfReference(docStructElemNum, 0));
                elemDict.Set(PdfName.Pg, new PdfReference(pageObjNum, 0));

                if (el.Type == "Figure" && el.AltText != null)
                    elemDict.Set(PdfName.Alt, new PdfString(el.AltText));

                // K is the MCID integer
                elemDict.Set(PdfName.K, new PdfNumber(i));

                xrefEntries.Add((elemObjNums[i], writer.WriteIndirectObject(elemObjNums[i], 0, elemDict)));
                mcidParents.Add(new PdfReference(elemObjNums[i], 0));
            }

            var parentTreeNums = new PdfArray();
            parentTreeNums.Add(new PdfNumber(PageStructParents));
            parentTreeNums.Add(mcidParents);
            var parentTreeDict = new PdfDictionary();
            parentTreeDict.Set(PdfName.Nums, parentTreeNums);
            xrefEntries.Add((parentTreeObjNum, writer.WriteIndirectObject(parentTreeObjNum, 0, parentTreeDict)));

            // Document root StructElem
            var kidsArray = new PdfArray();
            foreach (int n in elemObjNums)
                kidsArray.Add(new PdfReference(n, 0));

            var docStructElem = new PdfDictionary();
            docStructElem.Set(PdfName.Type, PdfName.StructElem);
            docStructElem.Set(PdfName.S, PdfName.Document);
            docStructElem.Set(PdfName.P, new PdfReference(structTreeRootNum, 0));
            docStructElem.Set(PdfName.K, kidsArray);
            xrefEntries.Add((docStructElemNum, writer.WriteIndirectObject(docStructElemNum, 0, docStructElem)));

            // StructTreeRoot
            var structTreeRoot = new PdfDictionary();
            structTreeRoot.Set(PdfName.Type, PdfName.StructTreeRoot);

            var treeKids = new PdfArray();
            treeKids.Add(new PdfReference(docStructElemNum, 0));
            structTreeRoot.Set(PdfName.K, treeKids);

            // RoleMap (optional but good practice)
            structTreeRoot.Set(PdfName.RoleMap, new PdfDictionary());

            structTreeRoot.Set(PdfName.ParentTree, new PdfReference(parentTreeObjNum, 0));
            structTreeRoot.Set(PdfName.ParentTreeNextKey, new PdfNumber(PageStructParents + 1));
            xrefEntries.Add((structTreeRootNum, writer.WriteIndirectObject(structTreeRootNum, 0, structTreeRoot)));

            // Resources: the embedded font, when there is text to draw with it
            var resources = new PdfDictionary();
            if (text != null)
            {
                var fontResources = new PdfDictionary();
                fontResources.Set(new PdfName(FontResourceName), new PdfReference(fontObjNum, 0));
                resources.Set(PdfName.Font, fontResources);
            }

            // Page
            var mediaBox = new PdfArray();
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(0));
            mediaBox.Add(new PdfNumber(PageWidth));
            mediaBox.Add(new PdfNumber(PageHeight));

            var pageDict = new PdfDictionary();
            pageDict.Set(PdfName.Type, PdfName.Page);
            pageDict.Set(PdfName.Parent, new PdfReference(pagesRootObjNum, 0));
            pageDict.Set(PdfName.MediaBox, mediaBox);
            pageDict.Set(PdfName.Contents, new PdfReference(contentStreamObjNum, 0));
            pageDict.Set(PdfName.Resources, resources);
            pageDict.Set(PdfName.StructParents, new PdfNumber(PageStructParents));
            xrefEntries.Add((pageObjNum, writer.WriteIndirectObject(pageObjNum, 0, pageDict)));

            // Pages tree root — one implementation, in Core.Writing.PdfPageTreeWriter.
            var pagesDict = PdfPageTreeWriter.BuildRoot(new[] { pageObjNum }, pagesRootObjNum);
            xrefEntries.Add((pagesRootObjNum, writer.WriteIndirectObject(pagesRootObjNum, 0, pagesDict)));

            // Catalog
            var markInfo = new PdfDictionary();
            markInfo.Set(PdfName.Marked, new PdfBoolean(true));

            var viewerPrefs = new PdfDictionary();
            viewerPrefs.Set(PdfName.DisplayDocTitle, new PdfBoolean(true));

            var catalogDict = new PdfDictionary();
            catalogDict.Set(PdfName.Type, PdfName.Catalog);
            catalogDict.Set(PdfName.Pages, new PdfReference(pagesRootObjNum, 0));
            catalogDict.Set(PdfName.MarkInfo, markInfo);
            catalogDict.Set(PdfName.Lang, new PdfString(_language));
            catalogDict.Set(PdfName.StructTreeRoot, new PdfReference(structTreeRootNum, 0));
            catalogDict.Set(PdfName.ViewerPreferences, viewerPrefs);
            catalogDict.Set(PdfName.Metadata, new PdfReference(xmpStreamObjNum, 0));
            xrefEntries.Add((catalogObjNum, writer.WriteIndirectObject(catalogObjNum, 0, catalogDict)));

            // xref + trailer
            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Size, new PdfNumber(nextObj));
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
            trailer.Set(PdfName.ID, PdfTrailerBuilder.NewFileId());

            long xrefOffset = writer.WriteXrefTable(xrefEntries);
            writer.WriteTrailer(trailer, xrefOffset);
        }

        /// <summary>Writes the complete tagged PDF to the file at <paramref name="path"/>.</summary>
        public void Build(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                Build(fs);
        }

        // ----------------------------------------------------------------
        // Private helpers
        // ----------------------------------------------------------------

        /// <summary>Resource name the page's content draws the embedded font by.</summary>
        private const string FontResourceName = "F1";

        /// <summary>The embedded font and each element's shaped run.</summary>
        private sealed class ShapedText
        {
            internal TrueTypeFontProgram Program;
            internal readonly Dictionary<ushort, int> GidToUnicode = new Dictionary<ushort, int>();
            /// <summary>A ligature glyph's typed letters, for <c>/ToUnicode</c> (<see cref="GlyphMapping.Text"/>).</summary>
            internal readonly Dictionary<ushort, string> GidToText = new Dictionary<ushort, string>();

            /// <summary>By element index; null for an element with nothing to draw.</summary>
            internal ShapedGlyphRun[] Runs;
        }

        private static bool Draws(PdfTaggedElement el) => el.Type != "Figure" && !string.IsNullOrEmpty(el.Text);

        /// <summary>
        /// The element's text as one drawn line: each C0 control character and DEL becomes a space.
        /// </summary>
        /// <remarks>
        /// A paragraph here is drawn as one line, and a control character has a glyph in no font —
        /// so counted as a code point the font must cover, a line break or a tab made every font
        /// fail the coverage check and the whole document be refused.
        /// </remarks>
        private static string Drawable(string text)
        {
            char[] chars = null;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= ' ' && text[i] != '\u007F') continue;
                chars ??= text.ToCharArray();
                chars[i] = ' ';
            }
            return chars == null ? text : new string(chars);
        }

        /// <summary>
        /// Resolves one font covering every element's text and shapes each element against it, or
        /// returns null when nothing is drawn.
        /// </summary>
        private ShapedText ShapeText()
        {
            // The code points the DRAWING needs — for Arabic the shaped forms, not the base letters;
            // see TextRunComposer.CodePointsToCover.
            var required = new HashSet<int>();
            foreach (var el in _elements)
                if (Draws(el))
                    foreach (int cp in TextRunComposer.CodePointsToCover(Drawable(el.Text))) required.Add(cp);
            if (required.Count == 0) return null;

            var resolution = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = StandardFonts.Helvetica,
                    RequiredCodePoints = required,
                    AllowSystemFontSubstitution = _allowSystemFontSubstitution,
                },
                _fontProvider);

            if (!resolution.Success)
                throw new Core.PdfException(
                    "A PDF/UA document embeds every font it draws with (ISO 14289-1 7.21.4.1), and no font " +
                    "program covers this document's text (" + resolution.FailureReason + "). Supply one " +
                    "with SetFontProvider, or allow an installed font with AllowSystemFontSubstitution.");

            var shaped = new ShapedText { Program = resolution.Program, Runs = new ShapedGlyphRun[_elements.Count] };
            for (int i = 0; i < _elements.Count; i++)
            {
                if (!Draws(_elements[i])) continue;
                var run = TextRunComposer.Compose(Drawable(_elements[i].Text), shaped.Program, new TextRunOptions());
                run.RecordUsedGlyphs(shaped.GidToUnicode, shaped.GidToText);
                shaped.Runs[i] = run;
            }
            return shaped;
        }

        /// <remarks>
        /// Numbers are formatted in the invariant culture. An interpolated <c>{y:F2}</c> follows the
        /// current culture and would write <c>791,89</c> —
        /// two operands, and a broken content stream — on any machine whose decimal separator is a
        /// comma.
        /// </remarks>
        private static byte[] BuildContentStream(List<PdfTaggedElement> elements, ShapedText text)
        {
            var sb = new StringBuilder();

            double y = PageHeight - 50.0; // start near top of page
            const double lineStep = 20.0;

            for (int i = 0; i < elements.Count; i++)
            {
                var el = elements[i];
                string mcid = i.ToString(CultureInfo.InvariantCulture);

                // Every element is a marked-content sequence, so each MCID the structure names
                // exists in the content. A figure, or a paragraph with no text, draws nothing in it.
                sb.Append('/').Append(Core.Objects.PdfNameEscape.Escape(el.Type)).Append(" <</MCID ").Append(mcid).Append(">> BDC\n");
                if (Draws(el))
                {
                    // Choose font size: larger for headings
                    int fontSize = el.Type == "P" ? 12
                                 : el.Type == "H1" ? 24
                                 : el.Type == "H2" ? 20
                                 : el.Type == "H3" ? 16
                                 : 14;

                    sb.Append("BT\n/").Append(FontResourceName).Append(' ')
                      .Append(fontSize.ToString(CultureInfo.InvariantCulture)).Append(" Tf\n");
                    sb.Append("50 ").Append(y.ToString("F2", CultureInfo.InvariantCulture)).Append(" Td\n");
                    sb.Append(GlyphRunShowText.Operators(text.Runs[i], fontSize)).Append("\nET\n");
                }
                sb.Append("EMC\n");

                y -= lineStep;
            }

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static byte[] BuildXmpStream(string title, string language)
        {
            // A minimal PDF/UA-1 XMP packet.
            //
            // The dc:title block is emitted ONLY when there is a title. Written unconditionally, a
            // document built without one would carry an *empty* dc:title
            // alongside /ViewerPreferences /DisplayDocTitle true — which is a claim, not a
            // formality: a PDF/UA checker reads "a title is present" and passes UA04 (ISO
            // 14289-1 §7.1, which requires a *non-empty* title) on a document that has none. That
            // hides the true finding and manufactures a false pass, exactly the failure mode
            // Tetitulz.Pdf.Accessibility refuses elsewhere ("a document that cannot be tagged is
            // never made to claim it was"). Omitting the element leaves UA04 honestly open.
            string titleBlock = string.IsNullOrEmpty(title)
                ? string.Empty
                : $@"
      <dc:title>
        <rdf:Alt>
          <rdf:li xml:lang=""{XmlEscape(language)}"">{XmlEscape(title)}</rdf:li>
        </rdf:Alt>
      </dc:title>";

            string xmp = XmpPacket.Header + $@"
<x:xmpmeta xmlns:x=""{XmpPacket.XmpMetaNamespace}"">
  <rdf:RDF xmlns:rdf=""{XmpPacket.RdfNamespace}"">
    <rdf:Description rdf:about=""""
        xmlns:dc=""{XmpPacket.DublinCoreNamespace}""
        xmlns:xmp=""{XmpPacket.XmpBasicNamespace}""
        xmlns:pdf=""http://ns.adobe.com/pdf/1.3/""
        xmlns:pdfuaid=""{XmpPacket.PdfUaIdNamespace}"">{titleBlock}
      <dc:language>
        <rdf:Bag>
          <rdf:li>{XmlEscape(language)}</rdf:li>
        </rdf:Bag>
      </dc:language>
      <pdfuaid:part>1</pdfuaid:part>
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>" + XmpPacket.Trailer;
            return Encoding.UTF8.GetBytes(xmp);
        }

        /// <summary>
        /// <paramref name="text"/> as XML character data: the five markup characters escaped, and
        /// first every code point XML 1.0 forbids (a control character, a lone surrogate) replaced
        /// through the one <see cref="XmlLegalText"/> — escaping cannot make such a character legal,
        /// and a title holding one made the packet not well-formed.
        /// </summary>
        private static string XmlEscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return XmlLegalText.Replace(text, out _)
                .Replace("&",  "&amp;")
                .Replace("<",  "&lt;")
                .Replace(">",  "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'",  "&apos;");
        }
    }
}
