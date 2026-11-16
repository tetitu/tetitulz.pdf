// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A producer's lam-alef ligature reads back as lam and alef: one glyph drawn for two letters, its
/// <c>/ToUnicode</c> text the pair — in logical order in a right-to-left line, where the extractor's
/// line reversal would otherwise turn it round — from both extractors.
/// </summary>
public class ArabicLigatureExtractionTests
{
    // "la" and "sala": lam-alef isolated, then seen with lam-alef final.
    private const string Typed = "لا سلا";
    private const string Covers = "ﻟﻠﺎﺳ ﻻﻼ";

    [Fact]
    public void ALamAlefLigature_ExtractsAsLamThenAlef()
    {
        using var ms = new MemoryStream();
        new PdfTaggedDocumentBuilder().SetFontProvider(_ => MinimalTrueType.Build(Covers)).AddParagraph(Typed).Build(ms);
        byte[] pdf = ms.ToArray();

        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            // Glyphs number from 1 in code-point order: space 1, alef 2, seen 3, the lams 4 and 5, the
            // ligatures 6 and 7. Both ligatures are drawn, and neither lam on its own.
            string content = System.Text.Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
            var glyphs = System.Text.RegularExpressions.Regex.Matches(content, "<([0-9A-F]+)>").Cast<System.Text.RegularExpressions.Match>()
                .SelectMany(m => Enumerable.Range(0, m.Groups[1].Value.Length / 4).Select(i => m.Groups[1].Value.Substring(4 * i, 4))).ToList();
            Assert.Contains("0006", glyphs);
            Assert.Contains("0007", glyphs);
            Assert.DoesNotContain("0004", glyphs);
            Assert.DoesNotContain("0005", glyphs);
            Assert.Equal(Typed, Core.Text.PdfTextExtractor.ExtractText(doc).Trim());
        }

        var words = PdfOperations.ExtractStructuredText(new MemoryStream(pdf))
            .SelectMany(p => p.Lines).SelectMany(l => l.Words).Select(w => w.Text).ToList();
        Assert.Equal(new[] { "لا", "سلا" }, words);
    }
}
