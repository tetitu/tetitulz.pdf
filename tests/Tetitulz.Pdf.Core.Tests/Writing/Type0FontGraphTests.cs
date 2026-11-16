// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// The parts of <see cref="Type0FontBuilder"/>'s object graph that decide how a reader
    /// <i>measures</i> and <i>finds</i> the embedded program: Table 117's <c>/DW</c> and <c>/W</c>,
    /// and Table 122's <c>/FontFileN</c> key.
    /// </summary>
    /// <remarks>
    /// <para><b>Why these exist.</b> Merging the writer's two branches meant mutating the survivors
    /// and counting what reddened. Three mutations survived the whole suite: dropping <c>/W</c>
    /// altogether, taking <c>/DW</c> from the wrong glyph, and embedding a CFF program under
    /// <c>/FontFile2</c>. Nothing in the tree asserted any of the three — the existing composite-font
    /// tests are about the §9.6.4 subset tag, and the feature suites that embed fonts assert the
    /// text that comes out rather than the widths that were declared.</para>
    ///
    /// <para><b>What each one costs when it is wrong.</b> <c>/DW</c> is the advance for every CID
    /// the <c>/W</c> array does not cover, so a wrong one mis-spaces exactly the glyphs nobody
    /// looked at; a missing <c>/W</c> silently measures the whole document at <c>/DW</c>; and a CFF
    /// program under <c>/FontFile2</c> tells a reader to parse a <c>glyf</c> table that is not
    /// there. All three produce a file that opens.</para>
    ///
    /// <para>The synthetic font is <see cref="Type0FontSubsetTagTests"/>'s, deliberately: glyph
    /// <c>g</c> advances <c>500 + g</c> at 1000 units per em, so every number below is predictable
    /// without re-deriving anything.</para>
    /// </remarks>
    public class Type0FontGraphTests
    {
        [Fact]
        public void DefaultWidth_IsGlyphZerosAdvance()
        {
            Graph g = Emit(Type0FontSubsetTagTests.BuildFont(), Used(1, 3));

            // hmtx gives glyph 0 an advance of 500; glyph 1 would be 501.
            Assert.Equal(500d, ((PdfNumber)g.CidFont.Get(PdfName.DW)).DoubleValue, 3);
        }

        [Fact]
        public void WidthsArray_NamesEveryUsedGlyphWithItsOwnAdvance()
        {
            Graph g = Emit(Type0FontSubsetTagTests.BuildFont(), Used(3, 1));

            var w = (PdfArray)g.CidFont.Get(PdfName.W);

            // /W [ cid [ w ] … ], ascending by CID whatever order the caller supplied them in.
            Assert.Equal(4, w.Count);
            Assert.Equal(1d, ((PdfNumber)w[0]).DoubleValue, 3);
            Assert.Equal(501d, ((PdfNumber)((PdfArray)w[1])[0]).DoubleValue, 3);
            Assert.Equal(3d, ((PdfNumber)w[2]).DoubleValue, 3);
            Assert.Equal(503d, ((PdfNumber)((PdfArray)w[3])[0]).DoubleValue, 3);
        }

        /// <summary>
        /// A <c>glyf</c> program is a CIDFontType2 descendant whose descriptor names it through
        /// <c>/FontFile2</c>.
        /// </summary>
        [Fact]
        public void GlyfProgram_IsEmbeddedAsFontFile2_UnderACidFontType2()
        {
            Graph g = Emit(Type0FontSubsetTagTests.BuildFont(), Used(1));

            Assert.Equal(PdfName.CIDFontType2.Value, ((PdfName)g.CidFont.Get(PdfName.Subtype)).Value);
            Assert.IsType<PdfReference>(g.Descriptor.Get(PdfName.FontFile2));
            Assert.Null(g.Descriptor.Get(PdfName.FontFile3));
        }

        /// <summary>
        /// A CFF program is a CIDFontType0 descendant whose descriptor names it through
        /// <c>/FontFile3</c>, and that stream carries <c>/Subtype /OpenType</c>. Table 126 admits no
        /// other spelling, and a reader handed <c>/FontFile2</c> looks for outlines that do not
        /// exist.
        /// </summary>
        [Fact]
        public void CffProgram_IsEmbeddedAsFontFile3_UnderACidFontType0()
        {
            Graph g = Emit(Type0FontSubsetTagTests.BuildFont(asCff: true), Used(1));

            Assert.Equal(PdfName.CIDFontType0.Value, ((PdfName)g.CidFont.Get(PdfName.Subtype)).Value);
            Assert.Null(g.Descriptor.Get(PdfName.FontFile2));

            var fontFileRef = Assert.IsType<PdfReference>(g.Descriptor.Get(PdfName.FontFile3));
            var fontFile = (PdfStream)g.ByNumber[fontFileRef.ReferenceObjectNumber];
            Assert.Equal(PdfName.OpenType.Value, ((PdfName)fontFile.Get(PdfName.Subtype)).Value);
        }

        /// <summary>
        /// Table 117 defines <c>/CIDToGIDMap</c> for CIDFontType2 only: a CFF descendant reaches its
        /// glyphs through the charset, so writing one there would be meaningless rather than merely
        /// redundant. This is the control for the branch parameter that carries it.
        /// </summary>
        [Fact]
        public void CffDescendant_HasNoCidToGidMap()
        {
            Graph g = Emit(Type0FontSubsetTagTests.BuildFont(asCff: true), Used(1));

            Assert.Null(g.CidFont.Get(PdfName.CIDToGIDMap));
            Assert.NotNull(Emit(Type0FontSubsetTagTests.BuildFont(), Used(1)).CidFont.Get(PdfName.CIDToGIDMap));
        }

        /// <summary>
        /// §9.7.4.2: a CID-keyed CFF's CIDs reach its glyphs through its charset, and the content's
        /// codes are glyph ids — so a program whose charset gives glyph 1 the CID 10 is embedded with
        /// its charset made the identity, and the embedded font maps CID 1 to glyph 1. Embedded as it
        /// was, <c>&lt;0001&gt;</c> selected no glyph in this library's reader and in any that follows
        /// the clause.
        /// </summary>
        [Fact]
        public void CidKeyedCffProgram_IsEmbeddedWithItsCharsetMadeTheIdentity()
        {
            byte[] otf = MinimalOpenTypeCff.WrapInOtto(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20, 30 }), "ABC");
            // The premise: the program's own charset gives glyph 1 the CID 10.
            Assert.Equal(1, CffParser.Parse(otf).CharCodeToGid[10]);

            Graph g = Emit(otf, Used(1, 3));
            var fontFileRef = Assert.IsType<PdfReference>(g.Descriptor.Get(PdfName.FontFile3));
            var fontFile = (PdfStream)g.ByNumber[fontFileRef.ReferenceObjectNumber];
            CffFont embedded = CffParser.Parse(fontFile.GetDecodedData());

            Assert.True(embedded.IsCidKeyed);
            Assert.Equal(1, embedded.CharCodeToGid[1]);
            Assert.Equal(3, embedded.CharCodeToGid[3]);
            Assert.False(embedded.CharCodeToGid.ContainsKey(10));
            Assert.Equal(4, embedded.Charstrings.Count);
        }

        // ----------------------------------------------------------------

        private sealed class Graph
        {
            internal Graph(
                Dictionary<int, PdfObject> byNumber, PdfDictionary type0, PdfDictionary cidFont,
                PdfDictionary descriptor)
            {
                ByNumber = byNumber;
                Type0 = type0;
                CidFont = cidFont;
                Descriptor = descriptor;
            }

            internal Dictionary<int, PdfObject> ByNumber { get; }
            internal PdfDictionary Type0 { get; }
            internal PdfDictionary CidFont { get; }
            internal PdfDictionary Descriptor { get; }
        }

        private static Graph Emit(byte[] fontBytes, IReadOnlyDictionary<ushort, int> used)
        {
            var program = TrueTypeFontProgram.Parse(fontBytes);
            if (program == null)
                throw new InvalidOperationException("the synthetic font did not parse; fix the fixture");

            int next = 1;
            var (type0Num, objects) = Type0FontBuilder.BuildObjects(ref next, program, used);
            var byNumber = objects.ToDictionary(o => o.objNum, o => o.obj);

            var type0 = (PdfDictionary)byNumber[type0Num];
            var descendants = (PdfArray)type0.Get(PdfName.DescendantFonts);
            var cidFont = (PdfDictionary)byNumber[((PdfReference)descendants[0]).ReferenceObjectNumber];
            var descriptor = (PdfDictionary)byNumber[
                ((PdfReference)cidFont.Get(PdfName.FontDescriptor)).ReferenceObjectNumber];

            return new Graph(byNumber, type0, cidFont, descriptor);
        }

        private static Dictionary<ushort, int> Used(params ushort[] gids)
        {
            var map = new Dictionary<ushort, int>();
            foreach (ushort gid in gids) map[gid] = 'A' + gid;
            return map;
        }
    }
}
