// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// OCR → searchable PDF: rasterise each page, recognise the words, and lay invisible text over the
/// scan at their positions.
///
/// <para>The two halves shipped years apart and nothing joined them — <c>PdfOcrLayerWriter</c>'s
/// only callers were its own tests, and <c>IOcrPlugin</c> returned a bare string, so word geometry
/// never survived recognition. These tests drive the joined pipeline with a <b>stub engine</b>, so
/// the assertions are about this library's plumbing — coordinate conversion, ordering, encoding,
/// revision count — and not about whether Tesseract is installed.</para>
///
/// <para><b>In the plugin-host collection, which is not optional.</b> The stub is registered with
/// the process-wide <c>PdfPluginHost</c>, so while it is live <i>any</i> concurrent OCR dispatch in
/// the assembly can find it — and did: <c>EncryptedReaderOverloadTests</c> failed in a full run
/// while passing alone, because its own engine was shadowed by this one. That collection's own
/// documentation says every test that registers a plugin must join it.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class SearchablePdfTests : IDisposable
{
    private const string Hebrew = "שלום";
    private const string HebrewSecond = "עולם";

    // ---------------------------------------------------------------- stub engine

    /// <summary>
    /// An <see cref="IOcrWordPlugin"/> that returns whatever the test tells it to, so the pipeline
    /// can be asserted end to end with no engine installed.
    /// </summary>
    private sealed class StubWordEngine : IOcrWordPlugin
    {
        private readonly Func<int, int, IReadOnlyList<OcrWordBox>> _boxes;
        private int _calls;

        /// <summary>
        /// Set instead of <c>_boxes</c> when the fixture needs the image's own dimensions.
        /// </summary>
        /// <remarks>
        /// <c>_boxes</c> is handed <c>(callIndex, height)</c> — the width is not among its
        /// arguments, and a fixture that assumed otherwise silently got the call index as a width
        /// and produced zero-width boxes. A second delegate rather than a changed signature,
        /// because the call index is what the multi-page cases key on.
        /// </remarks>
        private readonly Func<int, int, IReadOnlyList<OcrWordBox>> _byImageSize;

        internal StubWordEngine(Func<int, int, IReadOnlyList<OcrWordBox>> boxes) => _boxes = boxes;

        internal StubWordEngine(
            Func<int, int, IReadOnlyList<OcrWordBox>> byImageSize, bool _)
            => _byImageSize = byImageSize;

        /// <summary>Image sizes the engine was handed, in call order.</summary>
        internal List<(int Width, int Height)> SeenImages { get; } = new();

        internal int Calls => _calls;

        public string Name => "Tetitulz.Tests.StubWordEngine";
        public string Description => "Returns scripted word boxes.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent)
            => components == 3 && bitsPerComponent == 8;

        public string RecognizeText(byte[] pixels, int width, int height, int components)
            => string.Join(" ", (RecognizeWords(pixels, width, height, components) ?? new List<OcrWordBox>())
                                .Select(b => b.Text));

        public IReadOnlyList<OcrWordBox> RecognizeWords(byte[] pixels, int width, int height, int components)
        {
            SeenImages.Add((width, height));
            _calls++;
            return _byImageSize != null
                ? _byImageSize(width, height)
                : _boxes(_calls - 1, width == 0 ? 1 : height);
        }
    }

    private readonly List<IPdfPlugin> _registered = new();

    private StubWordEngine Register(Func<int, int, IReadOnlyList<OcrWordBox>> boxes)
    {
        var engine = new StubWordEngine(boxes);
        PdfPluginHost.Instance.Register(engine);
        _registered.Add(engine);
        return engine;
    }

    /// <summary>
    /// Registers an engine whose boxes are a function of the <b>image's</b> width and height.
    /// </summary>
    /// <remarks>
    /// The rotation cases need this: the image an engine is handed is the page as <i>displayed</i>,
    /// so its dimensions are transposed on a quarter turn, and a fixture that hard-coded pixel
    /// coordinates would be describing a different part of the page for each rotation.
    /// </remarks>
    private StubWordEngine RegisterByImageSize(Func<int, int, IReadOnlyList<OcrWordBox>> boxes)
    {
        var engine = new StubWordEngine(boxes, true);

        // A test that lays the same fixture out twice — the scale comparison does, once upright and
        // once turned — registers twice, and the host refuses a duplicate NAME. The previous one is
        // withdrawn first rather than the test being written around it: registration is
        // process-wide, which is the hazard Dispose exists for.
        foreach (var previous in _registered.Where(p => p.Name == engine.Name).ToList())
        {
            PdfPluginHost.Instance.Unregister(previous.Name);
            _registered.Remove(previous);
        }

        PdfPluginHost.Instance.Register(engine);
        _registered.Add(engine);
        return engine;
    }

    /// <summary>
    /// A renderer that draws nothing itself (so the built-in renderer does) and records the document
    /// each page was rendered from: through one render session, every page brings the same one.
    /// </summary>
    private sealed class DocumentRecordingRenderer : IRendererPlugin
    {
        internal List<Tetitulz.Pdf.Core.Document.PdfDocument> Documents { get; } = new();
        public string Name => "Tetitulz.Tests.DocumentRecordingRenderer";
        public string Description => "Records the document of each render.";
        public Version Version => new Version(1, 0, 0);
        public int Priority => 100;
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanRender(Tetitulz.Pdf.Core.Document.PdfPage page, Tetitulz.Pdf.Core.Document.PdfDocument document) => true;
        public byte[] RenderPage(Tetitulz.Pdf.Core.Document.PdfPage page, Tetitulz.Pdf.Core.Document.PdfDocument document, int dpi)
        {
            Documents.Add(document);
            return null;
        }
    }

    /// <summary>
    /// A document's pages are rendered from one open document, not one a page: a one-shot render
    /// opens the whole document, and this took two of them a page — a scan of many pages cost the
    /// square of its length.
    /// </summary>
    [Fact]
    public void EveryPageOfAScan_IsRenderedFromOneOpenDocument()
    {
        Register((page, h) => new[] { new OcrWordBox("Hello", 10, 20, 40, 12) });
        var recorder = new DocumentRecordingRenderer();
        PdfPluginHost.Instance.Register(recorder);
        _registered.Add(recorder);

        MakeSearchable(BlankPage(pages: 3), out var result);

        Assert.Equal(3, result.PagesWithText);
        Assert.Equal(3, recorder.Documents.Count);
        Assert.Single(recorder.Documents.Distinct());
    }

    /// <summary>
    /// With no engine the page is not read at all: even a page the document does not have answers
    /// that no engine was consulted, rather than whatever measuring a missing page says.
    /// </summary>
    [Fact]
    public void WithNoEngine_ThePageIsNotRead_EvenOneTheDocumentLacks()
    {
        using var input = new MemoryStream(BlankPage(pages: 1), writable: false);

        Assert.Null(PdfOperations.RecognizeWordsWithOcr(input, 5));
        Assert.Contains("IOcrWordPlugin", PdfOperations.LastOcrError, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        // PdfPluginHost is a process-wide singleton, so a plugin left registered is a plugin every
        // other test in this assembly runs with — the shared-state hazard CLAUDE.md records three
        // separate intermittents for.
        foreach (var p in _registered) PdfPluginHost.Instance.Unregister(p.Name);
        _registered.Clear();
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>A one-page 200x100 pt document with no text of its own — a stand-in for a scan.</summary>
    /// <param name="pages">How many pages.</param>
    /// <param name="rotate">A <c>/Rotate</c> entry to put on each page, or empty for none.</param>
    /// <param name="mediaBox">The page's <c>/MediaBox</c> array.</param>
    /// <param name="encrypt">
    /// Adds a standard-security <c>/Encrypt</c> dictionary and the trailer entry pointing at it.
    /// The keys are nonsense — nothing here decrypts it; what is being asserted is that a writer
    /// <i>refuses</i>, and refusing is decided by the entry's presence.
    /// </param>
    private static byte[] BlankPage(int pages = 1, string rotate = "", bool encrypt = false,
                                    string mediaBox = "[0 0 200 100]")
    {
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{4 + i} 0 R"));
        var sb = new StringBuilder();
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        };
        for (int i = 0; i < pages; i++)
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} {rotate} /Contents 3 0 R >>");

        string encryptEntry = "";
        if (encrypt)
        {
            bodies.Add("<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                       "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                       "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>");
            encryptEntry = $" /Encrypt {bodies.Count} 0 R";
        }

        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R")
          .Append(encryptEntry).Append(" >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] MakeSearchable(byte[] pdf, out PdfOperations.SearchablePdfResult result, int dpi = 72)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        result = PdfOperations.MakeSearchable(input, output, dpi);
        return output.ToArray();
    }

    private static string ExtractedText(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractText(ms) ?? string.Empty;
    }

    // ---------------------------------------------------------------- the join

    /// <summary>
    /// The whole point: a page with no text of its own comes back with the recognised words
    /// extractable from it.
    /// </summary>
    [Fact]
    public void MakeSearchable_PutsTheRecognisedWordsWhereExtractionCanFindThem()
    {
        Register((page, h) => new[]
        {
            new OcrWordBox("Hello", 10, 20, 40, 12),
            new OcrWordBox("world", 60, 20, 40, 12),
        });

        Assert.Equal(string.Empty, ExtractedText(BlankPage()).Trim());   // the control: nothing to find before

        byte[] searchable = MakeSearchable(BlankPage(), out var result);

        Assert.Equal(1, result.PagesWithText);
        Assert.Equal(2, result.WordsWritten);
        string text = ExtractedText(searchable);
        Assert.Contains("Hello", text);
        Assert.Contains("world", text);
    }

    /// <summary>
    /// Hebrew must survive into the layer. <c>PdfContentStreamBuilder.ShowText</c> writes one byte
    /// per character and substitutes <c>?</c> above U+00FF, so the layer over a Hebrew scan used to
    /// be a row of question marks — a document that reports a text layer, looks identical, and
    /// cannot be searched in its own language.
    /// </summary>
    [Fact]
    public void MakeSearchable_HebrewWords_AreExtractableRatherThanQuestionMarks()
    {
        Register((page, h) => new[]
        {
            new OcrWordBox(Hebrew, 100, 20, 40, 12),
            new OcrWordBox(HebrewSecond, 40, 20, 40, 12),
        });

        byte[] searchable = MakeSearchable(BlankPage(), out _);
        string text = ExtractedText(searchable);

        Assert.Contains(Hebrew, text);
        Assert.Contains(HebrewSecond, text);
        Assert.DoesNotContain("?", text);
    }

    /// <summary>
    /// ...and the layer really is a composite font, not Helvetica with the characters destroyed.
    /// Extraction alone cannot separate those two: a reader that made the same mistake in both
    /// directions would agree with itself.
    /// </summary>
    [Fact]
    public void MakeSearchable_HebrewWords_WriteACompositeFontWithAToUnicodeMap()
    {
        Register((page, h) => new[] { new OcrWordBox(Hebrew, 10, 20, 40, 12) });

        byte[] searchable = MakeSearchable(BlankPage(), out _);

        using var ms = new MemoryStream(searchable, writable: false);
        using var doc = PdfDocument.Open(ms);
        PdfDictionary font = LayerFont(doc);

        Assert.Equal("Type0", font.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Identity-H", font.GetName(PdfName.Encoding)?.Value);
        Assert.NotNull(doc.ResolveObject(font[PdfName.ToUnicode]) as PdfStream);

        // The map has to carry the code points actually shown, or extraction gets nothing back.
        string cmap = Encoding.ASCII.GetString(
            (doc.ResolveObject(font[PdfName.ToUnicode]) as PdfStream).GetDecodedData());
        foreach (char c in Hebrew)
            Assert.Contains(((int)c).ToString("X4"), cmap, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Latin-only page keeps the standard-14 simple font. Without this the composite path could
    /// have replaced the simple one outright and every other assertion would still pass — at the
    /// cost of a font dictionary on every scanned page that never needed one.
    /// </summary>
    [Fact]
    public void MakeSearchable_LatinWords_StillUseTheStandard14SimpleFont()
    {
        Register((page, h) => new[] { new OcrWordBox("Hello", 10, 20, 40, 12) });

        byte[] searchable = MakeSearchable(BlankPage(), out _);

        using var ms = new MemoryStream(searchable, writable: false);
        using var doc = PdfDocument.Open(ms);
        Assert.Equal("Type1", LayerFont(doc).GetName(PdfName.Subtype)?.Value);
    }

    /// <summary>The font the appended layer references, resolved from the page's resources.</summary>
    private static PdfDictionary LayerFont(PdfDocument doc)
    {
        var resources = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]) as PdfDictionary;
        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(resources[PdfName.Font]));
        Assert.NotEmpty(fonts.Keys);
        return Assert.IsType<PdfDictionary>(doc.ResolveObject(fonts[fonts.Keys.First()]));
    }

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// Pixel rectangles become PDF user space: the y axis flips, and it flips about the box's
    /// BOTTOM edge. Flipping the top edge instead puts every word one line-height too high — a
    /// wrong-but-plausible layer that nothing visible reveals.
    /// </summary>
    [Fact]
    public void ToUserSpace_FlipsTheYAxisAboutTheBoxesBottomEdge()
    {
        var boxes = new[] { new OcrWordBox("x", 10, 20, 40, 12) };

        var words = PdfOperations.ToUserSpace(boxes, imageWidthPx: 200, imageHeightPx: 100,
                                              pageWidthPt: 200, pageHeightPt: 100);

        Assert.Equal(10f, words[0].X, 3);
        Assert.Equal(100f - (20f + 12f), words[0].Y, 3);   // NOT 100 - 20
        Assert.Equal(40f, words[0].Width, 3);
        Assert.Equal(12f, words[0].Height, 3);
    }

    /// <summary>
    /// The scale comes from the image the engine actually saw, not from the dpi that was asked
    /// for. A page rasterised at a reduced effective resolution — the renderer does that when the
    /// pixel extent would breach its safety limits — is otherwise scaled wrongly throughout.
    /// </summary>
    [Fact]
    public void ToUserSpace_ScalesByTheImageActuallyRecognised()
    {
        var boxes = new[] { new OcrWordBox("x", 100, 50, 100, 20) };

        // 400x200 pixels for a 200x100 pt page: 2 pixels per point on both axes.
        var words = PdfOperations.ToUserSpace(boxes, 400, 200, 200, 100);

        Assert.Equal(50f, words[0].X, 3);
        Assert.Equal(100f - (50f + 20f) / 2f, words[0].Y, 3);
        Assert.Equal(50f, words[0].Width, 3);
        Assert.Equal(10f, words[0].Height, 3);
    }

    // ---------------------------------------------------------------- ordering

    /// <summary>
    /// A right-to-left line arrives from the engine rightmost-word-first, and must be <b>written
    /// left to right</b> with each word's characters reordered logical → visual — because that is
    /// how a PDF stores text, and every extractor runs the bidi algorithm on what it reads.
    /// </summary>
    /// <remarks>
    /// <b>The register's constraint for this item said the opposite</b> — "the layer must be
    /// written in logical order, not the visual order the boxes arrive in" — and this test is what
    /// refuted it. Emitting logical order round-trips through <c>ExtractText</c>'s bidi pass and
    /// comes back <i>reversed</i>: <c>שלום</c> extracts as <c>םולש</c>. The words are in the right
    /// places on the page either way, so nothing visible distinguishes the two.
    /// </remarks>
    [Fact]
    public void MakeSearchable_RightToLeftLine_ExtractsInLogicalReadingOrder()
    {
        Register((page, h) => new[]
        {
            new OcrWordBox(Hebrew, 120, 20, 40, 12),         // rightmost — read first
            new OcrWordBox(HebrewSecond, 40, 20, 40, 12),
        });

        string text = ExtractedText(MakeSearchable(BlankPage(), out _));

        int first = text.IndexOf(Hebrew, StringComparison.Ordinal);
        int second = text.IndexOf(HebrewSecond, StringComparison.Ordinal);
        Assert.True(first >= 0 && second >= 0, "both words must be extractable: " + text);
        Assert.True(first < second,
            $"the rightmost word must be read first; got '{text}'");
    }

    /// <summary>
    /// Lines come out top to bottom, and a word on a lower line never precedes one above it —
    /// which a flat left-to-right sort of the whole page would get wrong for any multi-line scan.
    /// </summary>
    [Fact]
    public void MakeSearchable_MultipleLines_ExtractsTopToBottom()
    {
        Register((page, h) => new[]
        {
            // Deliberately supplied out of order, and with the lower line's word further left.
            new OcrWordBox("second", 5, 60, 40, 12),
            new OcrWordBox("first", 120, 20, 40, 12),
        });

        string text = ExtractedText(MakeSearchable(BlankPage(), out _));

        int first = text.IndexOf("first", StringComparison.Ordinal);
        int second = text.IndexOf("second", StringComparison.Ordinal);
        Assert.True(first >= 0 && second >= 0, "both words must be extractable: " + text);
        Assert.True(first < second, $"the upper line must come first; got '{text}'");
    }

    // ---------------------------------------------------------------- revisions

    /// <summary>
    /// A multi-page scan gains <b>one</b> revision, not one per page. A per-page loop grows the
    /// file by a complete cross-reference section per page and leaves a revision history claiming
    /// edits that never happened.
    /// </summary>
    [Fact]
    public void MakeSearchable_ManyPages_AppendsExactlyOneRevision()
    {
        Register((page, h) => new[] { new OcrWordBox("p" + page, 10, 20, 20, 12) });

        byte[] searchable = MakeSearchable(BlankPage(pages: 5), out var result);

        Assert.Equal(5, result.PagesWithText);

        using var ms = new MemoryStream(searchable, writable: false);
        Assert.Equal(2, PdfOperations.GetRevisions(ms).Count);   // the original, plus one
    }

    // ---------------------------------------------------------------- honest failure

    /// <summary>
    /// With no engine registered, nothing is recognised and the result says so — rather than
    /// reporting a clean pass over a document it never examined.
    /// </summary>
    [Fact]
    public void MakeSearchable_WithNoEngine_ReportsThatNoneWasConsulted()
    {
        byte[] pdf = BlankPage();
        byte[] searchable = MakeSearchable(pdf, out var result);

        Assert.False(result.AnyTextWritten);
        Assert.Contains(result.Warnings, w => w.Contains("IOcrWordPlugin"));
        Assert.Equal(pdf, searchable);      // copied verbatim, not rewritten with empty layers
    }

    /// <summary>
    /// An engine that runs and finds nothing is a different fact from an engine that never ran,
    /// and the warning distinguishes them. Both produce zero words, so the counts cannot.
    /// </summary>
    [Fact]
    public void MakeSearchable_EngineFindsNothing_SaysSoRatherThanBlamingTheEngine()
    {
        Register((page, h) => Array.Empty<OcrWordBox>());

        MakeSearchable(BlankPage(), out var result);

        Assert.False(result.AnyTextWritten);
        Assert.Contains(result.Warnings, w => w.Contains("found no text"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("IOcrWordPlugin"));
    }

    /// <summary>
    /// A rotated page gets a layer, and the words come back in reading order.
    /// </summary>
    /// <remarks>
    /// <para>This replaces <c>MakeSearchable_RotatedPage_IsRefusedWithItsReason</c>, which pinned
    /// the refusal this closes. The behaviour changed, so the test was replaced rather than edited
    /// to assert something else under a name that promised a refusal.</para>
    ///
    /// <para><b>The engine's boxes are given in the ROTATED image's pixels</b>, which is what an
    /// engine actually sees — it is handed the rendered image and knows nothing about
    /// <c>/Rotate</c>. Two words are placed on one line of that image, left to right, so the
    /// discriminating assertion is that they extract in that order: a layer whose words were
    /// ordered in unrotated space comes out column-first, which is gibberish while every word is
    /// individually in the right place.</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void MakeSearchable_RotatedPage_WritesALayerInReadingOrder(int rotate)
    {
        // Two words side by side near the top of the image the engine is handed, whatever the
        // page's rotation does to that image's shape.
        RegisterByImageSize((width, height) => new[]
        {
            new OcrWordBox("alpha", width * 0.10f, height * 0.10f, width * 0.25f, height * 0.08f),
            new OcrWordBox("beta",  width * 0.45f, height * 0.10f, width * 0.25f, height * 0.08f),
        });

        string entry = rotate == 0 ? "" : $"/Rotate {rotate}";
        byte[] searchable = MakeSearchable(BlankPage(rotate: entry), out var result);

        Assert.True(result.AnyTextWritten, string.Join("; ", result.Warnings));

        using var ms = new MemoryStream(searchable, writable: false);
        string text = PdfOperations.ExtractText(ms) ?? "";

        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
        Assert.True(text.IndexOf("alpha", StringComparison.Ordinal)
                    < text.IndexOf("beta", StringComparison.Ordinal),
            $"/Rotate {rotate}: the words extracted as '{text.Trim()}', so the layer was not " +
            "ordered as the page reads.");
    }

    /// <summary>
    /// A word lands on the part of the page it was recognised on, for every rotation.
    /// </summary>
    /// <remarks>
    /// <para><b>Extraction alone cannot see a misplaced layer</b> — the words come back whatever
    /// coordinates they carry — so this asserts the recognised rectangle in <i>user space</i>
    /// through <c>RecognizeWords</c>, which is the step that undoes the rotation.</para>
    ///
    /// <para>The fixture is a 200×100 pt page and a box hugging the top-left <b>of the image</b>.
    /// Where that lands on the unrotated page is a different corner for each rotation, and those
    /// four corners are the whole claim: an implementation that ignored <c>/Rotate</c> would put
    /// all four in the same place, and one that transposed its inverse would swap two of them.</para>
    /// </remarks>
    [Theory]
    [InlineData(0,   0f,   90f)]    // image top-left -> page top-left
    [InlineData(90,  0f,   0f)]     // -> page bottom-left
    [InlineData(180, 180f, 0f)]     // -> page bottom-right
    [InlineData(270, 180f, 90f)]    // -> page top-right
    public void RecognizeWords_UndoesTheRotation(int rotate, float expectX, float expectY)
    {
        // A box in the top-left tenth of the rendered image, in the image's own pixels.
        RegisterByImageSize((width, height) => new[]
        {
            new OcrWordBox("corner", 0f, 0f, width * 0.10f, height * 0.10f),
        });

        string entry = rotate == 0 ? "" : $"/Rotate {rotate}";
        using var input = new MemoryStream(BlankPage(rotate: entry), writable: false);
        var words = PdfOperations.RecognizeWordsWithOcr(input, 0, 72);

        var word = Assert.Single(words);

        // The page is 200x100 pt and the box is a tenth of the image, so the word occupies a
        // 20x10 pt corner. Asserted to the point, with a tolerance for the pixel grid.
        Assert.InRange(word.X, expectX - 2f, expectX + 2f);
        Assert.InRange(word.Y, expectY - 2f, expectY + 2f);

        // The EXTENT, because a collapsed box lands at a plausible corner and covers nothing —
        // which is how the first version of this fixture passed its position assertions while
        // reporting zero-width words. It is also the half that proves the axes were transposed
        // rather than merely translated: 20x10 in user space for every rotation, though the
        // engine reported 20x10 for two of them and 10x20 for the other two.
        Assert.InRange(word.Width, 18f, 22f);
        Assert.InRange(word.Height, 8f, 12f);
    }

    /// <summary>
    /// The invisible baseline runs the way the page reads, which is not the way user space runs.
    /// </summary>
    /// <remarks>
    /// <b>Nothing about the extracted text can show this.</b> A word whose glyphs advance along the
    /// wrong axis still extracts as that word; what breaks is selection, and the highlight a reader
    /// drags over it. So the text matrix is asserted from the appended content stream: a quarter
    /// turn must produce an off-diagonal matrix, and an upright page a diagonal one.
    /// </remarks>
    [Theory]
    [InlineData(0,   true)]
    [InlineData(90,  false)]
    [InlineData(180, true)]
    [InlineData(270, false)]
    public void TheBaselineRunsWithThePage(int rotate, bool expectDiagonal)
    {
        var (matrix, word) = LayerMatrixAndWord(rotate);

        if (expectDiagonal)
        {
            Assert.Equal(0f, matrix.B);
            Assert.Equal(0f, matrix.C);
            Assert.NotEqual(0f, matrix.A);
        }
        else
        {
            Assert.Equal(0f, matrix.A);
            Assert.NotEqual(0f, matrix.B);
            Assert.NotEqual(0f, matrix.C);
        }

        // The TRANSLATION, which is the corner the first glyph sits on. The axes above say which
        // way the baseline runs and say nothing about where it starts, so a layer that put every
        // word at the box's bottom-left corner whatever the rotation passed them — a mutation
        // showed exactly that. The word is then one word-length from where it belongs, in the
        // direction it reads.
        (float expectX, float expectY) = rotate switch
        {
            90  => (word.X + word.Width, word.Y),
            180 => (word.X + word.Width, word.Y + word.Height),
            270 => (word.X, word.Y + word.Height),
            _   => (word.X, word.Y),
        };

        Assert.InRange(matrix.E, expectX - 0.6f, expectX + 0.6f);
        Assert.InRange(matrix.F, expectY - 0.6f, expectY + 0.6f);
    }

    /// <summary>
    /// A quarter turn scales the text by the box's <b>width</b>, because that is the box's extent
    /// across the line once the page is turned.
    /// </summary>
    /// <remarks>
    /// <para><b>Asserted as a ratio, so it needs no access to the nominal font size.</b> The same
    /// word box is laid out upright and at 90; the drawn size is <c>nominal x scale</c> either way,
    /// so the two scales must stand in the same ratio as the extents they are drawn from. A
    /// mutation that used the height for both makes that ratio 1.</para>
    ///
    /// <para>The fixture's box is deliberately far from square — three times as wide as it is tall
    /// — because a square one makes every wrong answer right.</para>
    /// </remarks>
    [Fact]
    public void AQuarterTurnScalesTheTextByTheBoxWidth()
    {
        var (upright, uprightWord) = LayerMatrixAndWord(0);
        var (turned, turnedWord) = LayerMatrixAndWord(90);

        // The glyphs' height is the scale ACROSS the baseline: D upright, C at a quarter turn. Along
        // the baseline (A, B) each word is fitted to its box's length instead, so those entries say
        // nothing about height.
        float uprightScale = Math.Abs(upright.D);
        float turnedScale = Math.Abs(turned.C);

        Assert.True(uprightScale > 0 && turnedScale > 0,
            $"scales were {uprightScale} and {turnedScale}");

        // Upright the scale comes from the height; turned, from the width.
        float expected = turnedWord.Width / uprightWord.Height;
        float actual = turnedScale / uprightScale;

        Assert.InRange(actual, expected * 0.9f, expected * 1.1f);
        Assert.True(expected > 1.5f, "the fixture must not be square, or this cannot discriminate");
    }

    /// <summary>The appended layer's text matrix, and the word it was drawn for.</summary>
    private (TextMatrix Matrix, PdfOcrWord Word) LayerMatrixAndWord(int rotate)
    {
        // Three times as wide as tall in the IMAGE, so the user-space box is far from square
        // whichever way the page is turned.
        RegisterByImageSize((width, height) => new[]
        {
            new OcrWordBox("word", width * 0.1f, height * 0.1f, width * 0.3f, height * 0.1f),
        });

        string entry = rotate == 0 ? "" : $"/Rotate {rotate}";
        byte[] page = BlankPage(rotate: entry);

        using var forWords = new MemoryStream(page, writable: false);
        var word = Assert.Single(PdfOperations.RecognizeWordsWithOcr(forWords, 0, 72));

        byte[] searchable = MakeSearchable(page, out var result);
        Assert.True(result.AnyTextWritten, string.Join("; ", result.Warnings));

        string stream = AppendedContent(searchable);
        int tm = stream.IndexOf(" Tm", StringComparison.Ordinal);
        Assert.True(tm > 0, $"no text matrix in the appended layer: {stream}");

        string[] operands = stream.Substring(0, tm)
            .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        Assert.True(operands.Length >= 6, $"a text matrix takes six operands: {stream}");

        float N(int fromEnd) => float.Parse(
            operands[operands.Length - fromEnd], System.Globalization.CultureInfo.InvariantCulture);

        return (new TextMatrix(N(6), N(5), N(4), N(3), N(2), N(1)), word);
    }

    /// <summary>The six operands of a <c>Tm</c>.</summary>
    private readonly record struct TextMatrix(
        float A, float B, float C, float D, float E, float F);

    /// <summary>The decoded text of the layer this library appended to page 0.</summary>
    private static string AppendedContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[Core.Objects.PdfName.Contents]);
        var array = contents as Core.Objects.PdfArray;
        Assert.NotNull(array);

        for (int i = array.Count - 1; i >= 0; i--)
        {
            var stream = doc.ResolveObject(array[i]) as Core.Objects.PdfStream;
            if (stream == null) continue;
            string text = System.Text.Encoding.ASCII.GetString(stream.GetDecodedData());
            if (text.Contains(" Tm", StringComparison.Ordinal)) return text;
        }

        throw new InvalidOperationException("no appended text layer on page 0");
    }

    /// <summary>
    /// A text-only <see cref="IOcrPlugin"/> cannot place anything, so it must not be mistaken for
    /// a word engine — "no text on this page" would be the wrong conclusion about the document.
    /// </summary>
    [Fact]
    public void MakeSearchable_WithATextOnlyEngine_StillReportsNoWordEngine()
    {
        var textOnly = new TextOnlyEngine();
        PdfPluginHost.Instance.Register(textOnly);
        try
        {
            MakeSearchable(BlankPage(), out var result);
            Assert.Contains(result.Warnings, w => w.Contains("IOcrWordPlugin"));
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(textOnly.Name);
        }
    }

    /// <summary>
    /// A text-only engine registered <b>ahead of</b> a word-capable one must not hide it.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that makes <c>FindOcrWordPlugin</c> falsifiable, and its absence let a
    /// mutation through.</b> Replacing the lookup with "find any OCR plugin, then cast" passes
    /// every other test here, because those register one plugin and a failed cast reports the same
    /// "no word engine" the correct code does. Only a registry holding both tells them apart — and
    /// there the cast reports no engine while a capable one sits in the registry behind the first.
    /// The hazard was written in <c>FindOcrWordPlugin</c>'s own doc comment before the test
    /// existed, which is the shape CLAUDE.md names: a documented hazard is a missing test.
    /// </remarks>
    [Fact]
    public void MakeSearchable_TextOnlyEngineRegisteredFirst_DoesNotHideTheWordEngine()
    {
        var textOnly = new TextOnlyEngine();
        PdfPluginHost.Instance.Register(textOnly);
        try
        {
            Register((page, h) => new[] { new OcrWordBox("Hello", 10, 20, 40, 12) });

            MakeSearchable(BlankPage(), out var result);

            Assert.True(result.AnyTextWritten,
                "a word-capable engine was registered; the text-only one in front of it must not " +
                "make the pipeline report that none was found");
            Assert.Equal(1, result.WordsWritten);
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(textOnly.Name);
        }
    }

    private sealed class TextOnlyEngine : IOcrPlugin
    {
        public string Name => "Tetitulz.Tests.TextOnlyEngine";
        public string Description => "Recognises text with no geometry.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
        public bool CanProcess(int components, int bitsPerComponent) => true;
        public string RecognizeText(byte[] pixels, int width, int height, int components) => "Hello";
    }

    /// <summary>
    /// An encrypted document is refused rather than given a clear-text layer.
    /// </summary>
    /// <remarks>
    /// The layer's content stream would be written unencrypted where the reader expects per-object
    /// ciphertext, so it would extract as noise: a document that reports a searchable text layer
    /// and cannot be searched. Same rule, and same failure mode, as <c>PdfFormFiller.Fill</c>.
    /// </remarks>
    [Fact]
    public void MakeSearchable_EncryptedDocument_IsRefusedRatherThanWrittenInClearText()
    {
        Register((page, h) => new[] { new OcrWordBox("Hello", 10, 20, 40, 12) });

        using var input = new MemoryStream(BlankPage(encrypt: true), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOperations.MakeSearchable(input, output, dpi: 72));
        Assert.Contains("clear text", ex.Message);
    }

    // ---------------------------------------------------------------- single page

    /// <summary>
    /// The single-page entry point hands back user-space rectangles a caller can pass straight to
    /// <see cref="PdfOcrLayerWriter"/>.
    /// </summary>
    [Fact]
    public void RecognizeWordsWithOcr_ReturnsUserSpaceRectangles()
    {
        Register((page, h) => new[] { new OcrWordBox("Hello", 0, 0, 20, 10) });

        using var input = new MemoryStream(BlankPage(), writable: false);
        var words = PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72);

        Assert.Single(words);
        Assert.Equal(0f, words[0].X, 3);
        Assert.Equal(100f - 10f, words[0].Y, 3);   // top-left box on a 100 pt page
    }

    // ---------------------------------------------------------------- counts and give-ups

    /// <summary>
    /// <see cref="PdfOperations.SearchablePdfResult.WordsWritten"/> is the whole document's count,
    /// not the last page's: two words on the first page and one on the second are three.
    /// </summary>
    [Fact]
    public void MakeSearchable_WordsWritten_SumsEveryPage()
    {
        Register((call, h) => call == 0
            ? new[] { new OcrWordBox("one", 10, 20, 30, 12), new OcrWordBox("two", 50, 20, 30, 12) }
            : new[] { new OcrWordBox("three", 10, 20, 40, 12) });

        MakeSearchable(BlankPage(pages: 2), out var result);

        Assert.Equal(2, result.PagesWithText);
        Assert.Equal(3, result.WordsWritten);
    }

    /// <summary>
    /// A box the engine reports with no text places nothing: there is no word to make searchable,
    /// and an empty string in the layer would only add an empty <c>Tj</c>.
    /// </summary>
    [Fact]
    public void RecognizeWordsWithOcr_BoxWithNoText_IsDropped()
    {
        Register((page, h) => new[]
        {
            new OcrWordBox("Hello", 0, 0, 20, 10),
            new OcrWordBox("", 30, 0, 20, 10),
        });

        using var input = new MemoryStream(BlankPage(), writable: false);
        var words = PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72);

        Assert.Equal(new[] { "Hello" }, words.Select(w => w.Text));
    }

    /// <summary>
    /// An engine that returns <c>null</c> could not run, which is not the same as finding nothing:
    /// the answer is <c>null</c> with the engine named, and an engine that answers with an empty
    /// list — the control — gives an empty list and no reason.
    /// </summary>
    [Fact]
    public void RecognizeWordsWithOcr_EngineReturnsNull_IsReportedNotTreatedAsNoText()
    {
        Register((page, h) => null);
        using (var input = new MemoryStream(BlankPage(), writable: false))
        {
            Assert.Null(PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72));
            Assert.Contains("StubWordEngine ran over page 0 and returned no result", PdfOperations.LastOcrError);
        }

        Dispose();
        Register((page, h) => Array.Empty<OcrWordBox>());
        using (var input = new MemoryStream(BlankPage(), writable: false))
        {
            Assert.Empty(PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72));
            Assert.Null(PdfOperations.LastOcrError);
        }
    }

    /// <summary>
    /// A page with no area — on either axis — is not rasterised for recognition, and the reason says so — rather than
    /// a render or decode failure further down being blamed. The control is the same page with an
    /// area, which reaches the engine.
    /// </summary>
    [Theory]
    [InlineData("[0 0 0 0]")]
    [InlineData("[0 0 200 0]")]
    [InlineData("[0 0 0 100]")]
    public void RecognizeWordsWithOcr_PageWithNoArea_IsReportedAsHavingNoExtent(string mediaBox)
    {
        var engine = Register((page, h) => new[] { new OcrWordBox("Hello", 0, 0, 20, 10) });

        using (var input = new MemoryStream(BlankPage(mediaBox: mediaBox), writable: false))
        {
            Assert.Null(PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72));
            Assert.Contains("has no measurable extent", PdfOperations.LastOcrError);
        }
        Assert.Equal(0, engine.Calls);

        using (var input = new MemoryStream(BlankPage(), writable: false))
            Assert.Single(PdfOperations.RecognizeWordsWithOcr(input, 0, dpi: 72));
        Assert.Equal(1, engine.Calls);
    }

    /// <summary>
    /// The pixel-to-point scale is a division by the image's extent; an extent of zero on either
    /// axis places nothing rather than words at infinite coordinates. The control: the same box
    /// over a real extent is placed.
    /// </summary>
    [Fact]
    public void ToUserSpace_ZeroImageExtent_PlacesNothing()
    {
        var boxes = new[] { new OcrWordBox("x", 10, 20, 40, 12) };

        Assert.Empty(PdfOperations.ToUserSpace(boxes, 0, 100, 200, 100));
        Assert.Empty(PdfOperations.ToUserSpace(boxes, 200, 0, 200, 100));
        Assert.Single(PdfOperations.ToUserSpace(boxes, 200, 100, 200, 100));
    }
}
