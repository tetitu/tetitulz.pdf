// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// <see cref="CompositeFontDictionaries"/>, the Table 114, 116 and 117 dictionaries every
    /// composite-font writer shares: their entries, their values and the order they are written in,
    /// which each caller's own entries follow.
    /// </summary>
    public class CompositeFontDictionariesTests
    {
        [Fact]
        public void CidSystemInfo_CarriesTheCollection_AndIdentityIsAdobeIdentity0()
        {
            PdfDictionary japan = CompositeFontDictionaries.CidSystemInfo("Adobe", "Japan1", 6);
            PdfDictionary identity = CompositeFontDictionaries.IdentityCidSystemInfo();

            Assert.Equal(new[] { "Registry", "Ordering", "Supplement" }, japan.Keys.Select(k => k.Value));
            Assert.Equal("Adobe", ((PdfString)japan[PdfName.Registry]).GetText());
            Assert.Equal("Japan1", ((PdfString)japan[PdfName.Ordering]).GetText());
            Assert.Equal(6, ((PdfNumber)japan[PdfName.Supplement]).IntValue);
            Assert.Equal("Identity", ((PdfString)identity[PdfName.Ordering]).GetText());
            Assert.Equal(0, ((PdfNumber)identity[PdfName.Supplement]).IntValue);
        }

        [Fact]
        public void DescendantCidFont_WritesTheCommonEntries_InOrder()
        {
            var info = CompositeFontDictionaries.IdentityCidSystemInfo();
            var descriptor = new PdfReference(7, 0);

            PdfDictionary cid = CompositeFontDictionaries.DescendantCidFont(PdfName.CIDFontType2, "ABCDEF+Sans", info, descriptor);

            Assert.Equal(new[] { "Type", "Subtype", "BaseFont", "CIDSystemInfo", "FontDescriptor" }, cid.Keys.Select(k => k.Value));
            Assert.Equal(PdfName.Font, cid[PdfName.Type]);
            Assert.Equal(PdfName.CIDFontType2, cid[PdfName.Subtype]);
            Assert.Equal("ABCDEF+Sans", ((PdfName)cid[PdfName.BaseFont]).Value);
            Assert.Same(info, cid[PdfName.CIDSystemInfo]);
            Assert.Same(descriptor, cid[PdfName.FontDescriptor]);
        }

        [Fact]
        public void Type0_NamesItsEncodingAndItsOneDescendant_InOrder()
        {
            var descendant = new PdfDictionary();

            PdfDictionary type0 = CompositeFontDictionaries.Type0("Ryumin-Light-90ms-RKSJ-H", PdfName.IdentityH, descendant);

            Assert.Equal(new[] { "Type", "Subtype", "BaseFont", "Encoding", "DescendantFonts" }, type0.Keys.Select(k => k.Value));
            Assert.Equal(PdfName.Font, type0[PdfName.Type]);
            Assert.Equal(PdfName.Type0, type0[PdfName.Subtype]);
            Assert.Equal("Ryumin-Light-90ms-RKSJ-H", ((PdfName)type0[PdfName.BaseFont]).Value);
            Assert.Equal(PdfName.IdentityH, type0[PdfName.Encoding]);
            var descendants = Assert.IsType<PdfArray>(type0[PdfName.DescendantFonts]);
            Assert.Same(descendant, Assert.Single(descendants));
        }
    }
}
