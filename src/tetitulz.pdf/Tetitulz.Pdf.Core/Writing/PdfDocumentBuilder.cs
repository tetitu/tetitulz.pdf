// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Builds a new PDF document from scratch.
    ///
    /// Usage:
    ///   var doc = new PdfDocumentBuilder();
    ///   var page = doc.AddPage(595, 842); // A4 points
    ///   string f1 = page.AddStandardFont("Helvetica");
    ///   page.BeginText().SetFont(f1, 14).SetTextPosition(72, 750).ShowText("Hello").EndText();
    ///   doc.Build(outputStream);
    /// </summary>
    public sealed class PdfDocumentBuilder
    {
        private sealed class PageSpec
        {
            public PdfContentStreamBuilder Builder;
            public double WidthPt;
            public double HeightPt;
        }

        /// <summary>
        /// A composite (Type0 / Identity-H) font program shared across the document.
        /// Pages select it with <see cref="PdfContentStreamBuilder.UseEmbeddedFont"/> +
        /// <see cref="PdfContentStreamBuilder.SetFont"/> and draw with
        /// <see cref="PdfContentStreamBuilder.ShowGlyphs(ushort[])"/>; the caller records each glyph
        /// it draws via <see cref="Note"/> so the embedded subset, /W and /ToUnicode stay
        /// minimal. The font's objects are written once, at <see cref="Build"/> time.
        /// </summary>
        public sealed class EmbeddedFont
        {
            public string ResourceName { get; internal set; } = "";
            internal TrueTypeFontProgram Program { get; set; } = null!;
            internal readonly Dictionary<ushort, int> UsedGidToUnicode = new Dictionary<ushort, int>();
            internal readonly Dictionary<ushort, string> UsedGidToText = new Dictionary<ushort, string>();

            /// <summary>Records that glyph <paramref name="gid"/> (mapping back to Unicode
            /// <paramref name="codePoint"/>) was drawn, so it is embedded + described; a ligature
            /// glyph's <paramref name="text"/> (<see cref="Text.GlyphMapping.Text"/>) is what
            /// <c>/ToUnicode</c> writes for it instead.</summary>
            public void Note(ushort gid, int codePoint, string text = null)
            {
                if (gid == 0 || UsedGidToUnicode.ContainsKey(gid)) return;
                UsedGidToUnicode[gid] = codePoint;
                if (text != null) UsedGidToText[gid] = text;
            }
        }

        private readonly List<PageSpec> _pages = new List<PageSpec>();
        private readonly List<EmbeddedFont> _embeddedFonts = new List<EmbeddedFont>();
        private int _embeddedFontCounter;
        private PdfVersion _version = PdfVersion.Pdf14;
        private bool _compressStreams = true;
        private string _title, _author, _subject;

        /// <summary>
        /// Sets the PDF version the header asks for (default 1.4). The header written is the higher
        /// of this and what the document uses — a JPEG 2000 image needs 1.5, an OpenType font program
        /// 1.6 — so a file never claims a version below its content; <see cref="WrittenVersion"/> and
        /// <see cref="VersionRaisedBecause"/> say what <see cref="Build"/> wrote and why.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is not a published
        /// version (1.0 to 1.7, or 2.0).</exception>
        public PdfDocumentBuilder WithVersion(PdfVersion version)
        {
            if (!version.IsPublished)
                throw new ArgumentOutOfRangeException(nameof(version), version.ToString(),
                    "A PDF version is 1.0 to 1.7, or 2.0.");
            _version = version;
            return this;
        }

        /// <summary>The version the last <see cref="Build"/> wrote in the header; null before one.</summary>
        public PdfVersion? WrittenVersion { get; private set; }

        /// <summary>
        /// The feature that made the last <see cref="Build"/> write a version above the one
        /// <see cref="WithVersion"/> asked for, such as <c>a JPEG 2000 image (JPXDecode)</c>; null
        /// when the header is the version asked for.
        /// </summary>
        public string VersionRaisedBecause { get; private set; }

        /// <summary>
        /// Sets the document information (/Info dictionary) — Title, Author and Subject.
        /// Null/empty values are omitted. A /Producer is always written.
        /// </summary>
        public PdfDocumentBuilder WithMetadata(string title, string author, string subject)
        {
            _title = title;
            _author = author;
            _subject = subject;
            return this;
        }

        /// <summary>Enables or disables FlateDecode compression on content streams (default true).</summary>
        public PdfDocumentBuilder WithCompression(bool compress)
        {
            _compressStreams = compress;
            return this;
        }

        private PdfStructureTree _structure;

        /// <summary>
        /// The document's logical structure. Touching it opts into tagged output; leaving it
        /// alone produces exactly the bytes this builder always produced, so no existing caller
        /// moves.
        ///
        /// <para>Add elements as content is written, marking each sequence through
        /// <see cref="PdfContentStreamBuilder.BeginMarkedContent"/>. The tree is serialised in
        /// <see cref="Build"/>, once the page object numbers it has to reference exist.</para>
        /// </summary>
        public PdfStructureTree Structure => _structure ??= new PdfStructureTree();

        /// <summary>
        /// Adds a blank page of the given dimensions and returns its content stream builder.
        /// </summary>
        /// <param name="widthPt">Page width in PDF points (1 pt = 1/72 inch).</param>
        /// <param name="heightPt">Page height in PDF points.</param>
        public PdfContentStreamBuilder AddPage(double widthPt, double heightPt)
        {
            // Finite first: NaN passes `<= 0`, and a NaN side reached the writer as a /MediaBox of
            // zeros — a page with no size, built without a word, by every producer above this
            // (the Builder's PageSize(NaN, NaN) exported a one-page document).
            Util.NumericArguments.RequireFinite(widthPt, nameof(widthPt));
            Util.NumericArguments.RequireFinite(heightPt, nameof(heightPt));
            if (widthPt <= 0) throw new ArgumentOutOfRangeException(nameof(widthPt));
            if (heightPt <= 0) throw new ArgumentOutOfRangeException(nameof(heightPt));

            var builder = new PdfContentStreamBuilder();
            _pages.Add(new PageSpec { Builder = builder, WidthPt = widthPt, HeightPt = heightPt });
            return builder;
        }

        /// <summary>
        /// Registers a TrueType font program to embed as a composite (Type0 / Identity-H)
        /// font shared by all pages. Returns a handle whose <see cref="EmbeddedFont.ResourceName"/>
        /// is passed to <see cref="PdfContentStreamBuilder.UseEmbeddedFont"/> +
        /// <see cref="PdfContentStreamBuilder.SetFont"/>. Record each drawn glyph with
        /// <see cref="EmbeddedFont.Note"/>; a font with no recorded glyphs is omitted.
        /// </summary>
        public EmbeddedFont AddEmbeddedFont(TrueTypeFontProgram program)
        {
            if (program == null) throw new ArgumentNullException(nameof(program));
            var ef = new EmbeddedFont { ResourceName = "EF" + (++_embeddedFontCounter), Program = program };
            _embeddedFonts.Add(ef);
            return ef;
        }

        /// <summary>
        /// Writes the complete PDF document to <paramref name="output"/>.
        /// </summary>
        public void Build(Stream output)
        {
            WrittenVersion = null;
            VersionRaisedBecause = null;
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (_pages.Count == 0) throw new InvalidOperationException("Cannot build a PDF with no pages.");

            int nextObjNum = 1;

            // Reserve object numbers for catalog and pages tree root
            int catalogObjNum = nextObjNum++;
            int pagesRootObjNum = nextObjNum++;

            var allObjects = new List<(int objNum, PdfObject obj)>();
            var pageObjNums = new List<int>();
            // Which page dictionary each content builder produced — the structure tree needs it
            // for /Pg and for the per-page /StructParents key.
            var pageObjByBuilder = new Dictionary<PdfContentStreamBuilder, int>();
            var pageDictByBuilder = new Dictionary<PdfContentStreamBuilder, PdfDictionary>();

            // Embedded composite fonts: build their object graphs once (object numbers are
            // allocated before the pages, so each page's /Font resource can reference the
            // Type0 object). A font with no glyphs actually drawn is skipped.
            var embeddedFontObjNums = new Dictionary<string, int>();
            foreach (var ef in _embeddedFonts)
            {
                if (ef.UsedGidToUnicode.Count == 0) continue;
                var (type0ObjNum, fontObjs) =
                    Type0FontBuilder.BuildObjects(ref nextObjNum, ef.Program, ef.UsedGidToUnicode, ef.UsedGidToText);
                allObjects.AddRange(fontObjs);
                embeddedFontObjNums[ef.ResourceName] = type0ObjNum;
            }

            // Named destinations, gathered across every page before any /GoTo action is written.
            // A link may point FORWARD -- a table of contents is nothing but forward links -- so
            // resolution cannot happen inside the page loop.
            var destinations = new Dictionary<string, (int pageObjNum, double y)>(StringComparer.Ordinal);
            var pendingInternalLinks =
                new List<(PdfDictionary pageDict, double x0, double y0, double x1, double y1, string dest)>();

            // Build each page. An image registered on several pages — the same arrays, the same dictionary — is
            // written once and drawn from there (ImageObjectShare).
            var images = new ImageObjectShare();
            foreach (var spec in _pages)
            {
                var (contentRef, resources, sideObjs) = spec.Builder.BuildObjects(
                    ref nextObjNum, _compressStreams, embeddedFontObjNums, images);

                // Accumulate side objects (fonts, images, content stream)
                allObjects.AddRange(sideObjs);

                // Build page dictionary
                int pageObjNum = nextObjNum++;
                var mediaBox = new PdfArray();
                mediaBox.Add(new PdfNumber(0));
                mediaBox.Add(new PdfNumber(0));
                mediaBox.Add(new PdfNumber(spec.WidthPt));
                mediaBox.Add(new PdfNumber(spec.HeightPt));

                var pageDict = new PdfDictionary();
                pageDict.Set(PdfName.Type, PdfName.Page);
                pageDict.Set(PdfName.Parent, new PdfReference(pagesRootObjNum, 0));
                pageDict.Set(PdfName.MediaBox, mediaBox);
                pageDict.Set(PdfName.Contents, contentRef);
                pageDict.Set(PdfName.Resources, resources);
                pageDict.ObjectNumber = pageObjNum;
                pageDict.GenerationNumber = 0;

                // A destination declared twice under one name is a caller error with no correct
                // answer, so FIRST WINS and the later one is ignored rather than silently
                // overwriting: a table of contents whose entries all jumped to the last chapter
                // would look like a link bug rather than a duplicate-name bug.
                foreach (var (name, y) in spec.Builder.Anchors)
                    if (!destinations.ContainsKey(name))
                        destinations[name] = (pageObjNum, y);

                foreach (var (lx0, ly0, lx1, ly1, dest) in spec.Builder.InternalLinks)
                    pendingInternalLinks.Add((pageDict, lx0, ly0, lx1, ly1, dest));

                // Clickable /Link annotations collected by the page's content builder.
                if (spec.Builder.Links.Count > 0)
                {
                    var annots = new PdfArray();
                    foreach (var (x0, y0, x1, y1, uri) in spec.Builder.Links)
                    {
                        int annNum = nextObjNum++;
                        var action = new PdfDictionary();
                        action.Set(PdfName.Type, PdfName.Action);
                        action.Set(PdfName.S, PdfName.URI);
                        action.Set(PdfName.URI, new PdfString(uri ?? ""));
                        var ann = LinkAnnotationBuilder.Build(x0, y0, x1, y1, action, annNum);
                        allObjects.Add((annNum, ann));
                        annots.Add(new PdfReference(annNum, 0));
                    }
                    pageDict.Set(PdfName.Annots, annots);
                }

                allObjects.Add((pageObjNum, pageDict));
                pageObjNums.Add(pageObjNum);
                pageObjByBuilder[spec.Builder] = pageObjNum;
                pageDictByBuilder[spec.Builder] = pageDict;
            }

            // Pages tree root — one implementation, in PdfPageTreeWriter.
            var pagesDict = PdfPageTreeWriter.BuildRoot(pageObjNums, pagesRootObjNum);

            // Structure tree. Written only when it actually reaches content: a document that
            // could not be tagged must not CLAIM to be tagged, because every conformance checker
            // keys "does this assert Tagged-PDF conventions?" off /StructTreeRoot and /MarkInfo,
            // and a hollow tree silences the true finding while switching on checks that then
            // report nothing. Untouched Structure means none of this runs at all.
            int structTreeRootNum = 0;
            if (_structure != null)
            {
                _structure.Prune();
                if (_structure.HasContent)
                {
                    var structure = PdfStructureTreeWriter.Build(_structure, pageObjByBuilder, ref nextObjNum);
                    allObjects.AddRange(structure.Objects);
                    structTreeRootNum = structure.StructTreeRootObjNum;

                    foreach (var kv in structure.PageStructParents)
                        if (pageDictByBuilder.TryGetValue(kv.Key, out var pd))
                            pd.Set(PdfName.StructParents, new PdfNumber(kv.Value));
                }
            }

            // Internal links, now that every destination is known. An /Annots array may already
            // exist on the page from the URI pass, so this appends rather than assigns.
            foreach (var (pageDict, x0, y0, x1, y1, dest) in pendingInternalLinks)
            {
                // A link naming a destination nothing declared is DROPPED, not written as a
                // dangling /GoTo: a viewer's response to one is unspecified, and in practice ranges
                // from doing nothing to jumping to page 1, which is worse than an inert rectangle.
                if (!destinations.TryGetValue(dest, out var target)) continue;

                int annNum = nextObjNum++;

                // /D is a STRING here because this writer emits the /Names /Dests name tree, whose
                // keys are strings. DocxToPdf writes the catalog /Dests dictionary and so uses a
                // name - see LinkAnnotationBuilder for why the two must not be made to agree.
                var action = new PdfDictionary();
                action.Set(PdfName.Type, PdfName.Action);
                action.Set(PdfName.S, PdfName.GoTo);
                action.Set(PdfName.D, new PdfString(dest));

                var ann = LinkAnnotationBuilder.Build(x0, y0, x1, y1, action, annNum);
                allObjects.Add((annNum, ann));

                var existing = pageDict[PdfName.Annots] as PdfArray;
                if (existing == null)
                {
                    existing = new PdfArray();
                    pageDict.Set(PdfName.Annots, existing);
                }
                existing.Add(new PdfReference(annNum, 0));
            }

            // The /Names /Dests name tree (§7.9.6, §12.3.2.3). One flat leaf, which is legal at any
            // size and is the shape Base.Operations.Structure.PdfNamedDestinations writes too --
            // the reader accepts the legacy catalog /Dests dictionary as well, but writing the tree
            // keeps the two writers in this repository producing one shape.
            //
            // The /Names array must be sorted by key: §7.9.6 says a name tree's entries are ordered,
            // and a reader is entitled to binary-search it. An unsorted leaf works in every viewer
            // that scans linearly and fails in one that does not, which is the worst kind of bug to
            // ship.
            int namesObjNum = 0;
            if (destinations.Count > 0)
            {
                var namesArr = new PdfArray();
                var ordered = new List<string>(destinations.Keys);
                ordered.Sort(StringComparer.Ordinal);
                foreach (string name in ordered)
                {
                    var (pageObjNum, y) = destinations[name];
                    namesArr.Add(new PdfString(name));
                    namesArr.Add(XyzDestination.Build(pageObjNum, left: null, top: y, zoom: null));
                }

                int destsLeafObjNum = nextObjNum++;
                var destsLeaf = new PdfDictionary();
                destsLeaf.Set(PdfName.Names, namesArr);
                destsLeaf.ObjectNumber = destsLeafObjNum;
                destsLeaf.GenerationNumber = 0;
                allObjects.Add((destsLeafObjNum, destsLeaf));

                namesObjNum = nextObjNum++;
                var namesDict = new PdfDictionary();
                namesDict.Set(PdfName.Dests, new PdfReference(destsLeafObjNum, 0));
                namesDict.ObjectNumber = namesObjNum;
                namesDict.GenerationNumber = 0;
                allObjects.Add((namesObjNum, namesDict));
            }

            // Catalog
            var catalogDict = new PdfDictionary();
            catalogDict.Set(PdfName.Type, PdfName.Catalog);
            catalogDict.Set(PdfName.Pages, new PdfReference(pagesRootObjNum, 0));
            if (namesObjNum > 0)
                catalogDict.Set(PdfName.Names, new PdfReference(namesObjNum, 0));
            if (structTreeRootNum > 0)
            {
                catalogDict.Set(PdfName.StructTreeRoot, new PdfReference(structTreeRootNum, 0));
                var markInfo = new PdfDictionary();
                markInfo.Set(PdfName.Marked, PdfBoolean.True);
                catalogDict.Set(PdfName.MarkInfo, markInfo);
            }
            if (_structure != null && !string.IsNullOrEmpty(_structure.Language))
                catalogDict.Set(PdfName.Lang, new PdfString(_structure.Language));
            catalogDict.ObjectNumber = catalogObjNum;
            catalogDict.GenerationNumber = 0;

            // Document information dictionary (/Info), numbered after every other object.
            int infoObjNum = nextObjNum++;
            var info = new PdfDictionary();
            if (!string.IsNullOrEmpty(_title)) info.Set(PdfName.Title, new PdfString(_title));
            if (!string.IsNullOrEmpty(_author)) info.Set(PdfName.Author, new PdfString(_author));
            if (!string.IsNullOrEmpty(_subject)) info.Set(PdfName.Subject, new PdfString(_subject));
            info.Set(PdfName.Producer, new PdfString(PdfProducer.Value));
            info.ObjectNumber = infoObjNum;
            info.GenerationNumber = 0;

            var trailer = new PdfDictionary();
            trailer.Set(PdfName.Size, new PdfNumber(nextObjNum)); // /Size = max object number + 1
            trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
            trailer.Set(PdfName.ID, PdfTrailerBuilder.NewFileId());
            trailer.Set(PdfName.Info, new PdfReference(infoObjNum, 0));

            // The header is written once every object exists, so it names what they use: the
            // version asked for, raised to what any object or content stream needs.
            var floor = new PdfFeatureVersions.Floor();
            foreach (var (_, obj) in allObjects) PdfFeatureVersions.Scan(obj, floor);
            PdfFeatureVersions.Scan(pagesDict, floor);
            PdfFeatureVersions.Scan(catalogDict, floor);
            PdfFeatureVersions.Scan(info, floor);
            PdfFeatureVersions.Scan(trailer, floor);
            foreach (var spec in _pages) spec.Builder.RequireContentFeatures(floor);

            PdfVersion version = floor.HeaderFor(_version);
            var writer = new PdfWriter(output);
            writer.WriteHeader(version);
            WrittenVersion = version;
            VersionRaisedBecause = floor.RaisedBecause(_version);

            // Write all side objects first (fonts, images, content streams, page dicts)
            var xrefEntries = new List<(int objNum, long offset)>();
            xrefEntries.Add((0, 0)); // free head

            foreach (var (n, obj) in allObjects)
            {
                long off = writer.WriteIndirectObject(n, 0, obj);
                xrefEntries.Add((n, off));
            }

            // Write pages tree root, catalog and /Info
            {
                long off = writer.WriteIndirectObject(pagesRootObjNum, 0, pagesDict);
                xrefEntries.Add((pagesRootObjNum, off));
            }
            {
                long off = writer.WriteIndirectObject(catalogObjNum, 0, catalogDict);
                xrefEntries.Add((catalogObjNum, off));
            }
            {
                long off = writer.WriteIndirectObject(infoObjNum, 0, info);
                xrefEntries.Add((infoObjNum, off));
            }

            long xrefOffset = writer.WriteXrefTable(xrefEntries);
            writer.WriteTrailer(trailer, xrefOffset);
        }
    }
}
