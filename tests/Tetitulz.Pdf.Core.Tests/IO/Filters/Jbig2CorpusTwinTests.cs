// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// JBIG2 streams the corpus codes two ways for one picture, checked against each other.
/// </summary>
/// <remarks>
/// <para>The another reader JBIG2 test files come in twins: the same picture coded with Huffman tables where
/// its twin uses arithmetic coding (symbol refinement and aggregation, text refinement), or coded
/// with a feature the twin does not use (a user-defined AT, an unknown page height, a retained
/// dictionary context, a TPGRON template-1 refinement). The arithmetic-coded or the plainer twin is
/// the reference: the decoder has been checked on those for a long time, and a Huffman or
/// fancier stream that decodes to a different picture is a wrong decode, whatever the decoder
/// reports.</para>
///
/// <para>These run only when the developer-local corpus is present (see <see cref="TestCorpus"/>).</para>
/// </remarks>
public class Jbig2CorpusTwinTests
{
    private const string Reference = "bitmap-symbol-textrefine.pdf";

    // coded, reference: each coded file must draw exactly what its reference draws.
    private static readonly (string Coded, string Reference)[] Pairs =
    {
        ("bitmap-symbol-symhuffrefineone.pdf", "bitmap-symbol-symbolrefineone.pdf"),
        ("bitmap-symbol-symhuffrefineseveral.pdf", "bitmap-symbol-symbolrefineseveral.pdf"),
        ("bitmap-symbol-symhuffrefine-textrefine.pdf", "bitmap-symbol-symbolrefine-textrefine.pdf"),
        ("bitmap-symbol-texthuffrefine.pdf", Reference),
        ("bitmap-symbol-texthuffrefineB15.pdf", Reference),
        ("bitmap-symbol-texthuffrefinecustom.pdf", "bitmap-symbol-textrefine-customat.pdf"),
        ("bitmap-symbol-texthuffrefinecustomdims.pdf", Reference),
        ("bitmap-symbol-texthuffrefinecustompos.pdf", Reference),
        ("bitmap-symbol-texthuffrefinecustomposdims.pdf", Reference),
        ("bitmap-symbol-texthuffrefinecustomsize.pdf", Reference),
        ("bitmap-stripe-initially-unknown-height.pdf", Reference),
        ("bitmap-p32-eof.pdf", Reference),
        ("bitmap-symbol-context-reuse.pdf", Reference),
        ("bitmap-refine-template1-tpgron.pdf", "bitmap-refine-template1.pdf"),
    };

    [CorpusFact("bitmap-symbol-symhuffrefineone.pdf")]
    public void EveryCodedTwin_DrawsWhatItsReferenceDraws()
    {
        foreach (var (coded, reference) in Pairs)
        {
            byte[] expected = DecodeFirstImage(reference);
            byte[] actual = DecodeFirstImage(coded);

            Assert.True(expected.SequenceEqual(actual),
                $"{coded} decodes to a different picture from its reference {reference}");
        }
    }

    /// <summary>
    /// The first JBIG2 image of a corpus file, decoded the way a PDF consumer decodes it, with the
    /// decoder's own report that it fell back to raw bytes checked as well.
    /// </summary>
    private static byte[] DecodeFirstImage(string name)
    {
        string path = Path.Combine(TestCorpus.Root!, name);
        Assert.True(File.Exists(path), "corpus file missing: " + name);

        using var doc = PdfDocument.Open(new MemoryStream(File.ReadAllBytes(path)));
        var xobjects = doc.Pages[0].Resources?[PdfName.XObject] as PdfDictionary;
        Assert.NotNull(xobjects);

        foreach (var entry in xobjects)
        {
            if (!(doc.ResolveObject(entry.Value) is PdfStream image)) continue;
            if (!(doc.ResolveObject(image[PdfName.Filter]) is PdfName filter) || filter.Value != "JBIG2Decode") continue;

            var parms = doc.ResolveObject(image[PdfName.DecodeParms]) as PdfDictionary;
            byte[] output = new Jbig2DecodeFilter().Decode(image.RawData, parms);
            Assert.Null(Jbig2DecodeFilter.LastFallbackReason);
            return output;
        }

        Assert.Fail("no JBIG2 image in " + name);
        return null;
    }
}
