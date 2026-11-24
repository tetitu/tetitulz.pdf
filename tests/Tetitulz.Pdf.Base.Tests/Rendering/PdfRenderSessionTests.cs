// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfRenderSession"/> — an open document plus the caches worth reusing across renders.
/// </summary>
/// <remarks>
/// The whole point of the session is that it changes <i>nothing</i> about the output while reusing
/// state that the one-shot path rebuilds. So the load-bearing test is
/// <see cref="SessionTiles_ArePixelIdenticalToStandaloneTiles"/>: sharing a font cache, a
/// hidden-OCG set or a parsed-operation cache across tiles is exactly the kind of change that
/// produces *nearly* the same picture, and "nearly" is invisible without a pixel comparison.
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public class PdfRenderSessionTests
{
    private const int Dpi = 72;

    /// <summary>
    /// Every tile rendered through a session must match the same tile rendered standalone, pixel
    /// for pixel. State leaking between tiles — a graphics state, a cached operation list consumed
    /// by a previous tile — shows up here and essentially nowhere else.
    /// </summary>
    [Fact]
    public void SessionTiles_ArePixelIdenticalToStandaloneTiles()
    {
        byte[] pdf = TiledPdf();
        const int grid = 2;
        const float tileW = 100f, tileH = 100f;

        using var session = PdfRenderSession.Open(new MemoryStream(pdf));

        for (int ty = 0; ty < grid; ty++)
            for (int tx = 0; tx < grid; tx++)
            {
                byte[] viaSession = session.RenderPageTile(0, tx * tileW, ty * tileH, tileW, tileH, Dpi);
                byte[] standalone = PdfPageRenderer.RenderPageTile(
                    new MemoryStream(pdf), 0, tx * tileW, ty * tileH, tileW, tileH, Dpi);

                using SKBitmap a = Decode(viaSession);
                using SKBitmap b = Decode(standalone);

                Assert.Equal(b.Width, a.Width);
                Assert.Equal(b.Height, a.Height);

                int differing = 0;
                for (int y = 0; y < a.Height; y++)
                    for (int x = 0; x < a.Width; x++)
                        if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;

                Assert.True(differing == 0,
                    $"tile ({tx},{ty}) differs from the standalone render in {differing} pixels");
            }
    }

    /// <summary>
    /// The tiles must actually differ from one another — otherwise the test above would pass with
    /// every tile rendering the same thing (or nothing).
    /// </summary>
    [Fact]
    public void SessionTiles_EachShowItsOwnQuadrant()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));

        // The fixture paints a red square in the bottom-left quadrant only.
        using SKBitmap bottomLeft  = Decode(session.RenderPageTile(0, 0, 0, 100, 100, Dpi));
        using SKBitmap topRight    = Decode(session.RenderPageTile(0, 100, 100, 100, 100, Dpi));

        Assert.True(HasRed(bottomLeft),  "the bottom-left tile should contain the red square");
        Assert.False(HasRed(topRight),   "the top-right tile should be empty");
    }

    /// <summary>Rendering the same tile twice from one session must be byte-identical.</summary>
    [Fact]
    public void SameTileTwice_FromOneSession_IsIdentical()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));

        byte[] first  = session.RenderPageTile(0, 0, 0, 100, 100, Dpi);
        byte[] second = session.RenderPageTile(0, 0, 0, 100, 100, Dpi);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A whole page rendered through a session must match the one-shot render pixel for pixel.
    /// This is the claim the WinForms viewer depends on when it switches to a session.
    /// </summary>
    [Fact]
    public void SessionPageRender_IsPixelIdenticalToTheOneShotRender()
    {
        byte[] pdf = TiledPdf();

        using var session = PdfRenderSession.Open(new MemoryStream(pdf));
        using SKBitmap viaSession = Decode(session.RenderPage(0, Dpi));
        using SKBitmap oneShot    = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));

        Assert.Equal(oneShot.Width, viaSession.Width);
        Assert.Equal(oneShot.Height, viaSession.Height);

        int differing = 0;
        for (int y = 0; y < viaSession.Height; y++)
            for (int x = 0; x < viaSession.Width; x++)
                if (viaSession.GetPixel(x, y) != oneShot.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);
    }

    /// <summary>
    /// Rendering the same page repeatedly from one session must not drift. A cache that handed out
    /// a list a previous render had consumed, or a font cache mutated by the first pass, would show
    /// up on the second render and not the first.
    /// </summary>
    [Fact]
    public void RepeatedPageRenders_FromOneSession_DoNotDrift()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));

        byte[] first = session.RenderPage(0, Dpi);
        for (int i = 0; i < 3; i++)
            Assert.Equal(first, session.RenderPage(0, Dpi));
    }

    /// <summary>
    /// The unencoded-pixel path must agree with the PNG path — same dimensions, same colours.
    /// </summary>
    [Fact]
    public void SessionPixels_AgreeWithTheSessionPng()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));

        RenderedPage pixels = session.RenderPageToPixels(0, Dpi);
        using SKBitmap png = Decode(session.RenderPage(0, Dpi));

        Assert.Equal(png.Width, pixels.Width);
        Assert.Equal(png.Height, pixels.Height);

        // The red square sits at PDF (20,20)-(80,80); sample its middle in both.
        int px = 50, py = png.Height - 1 - 50;
        SKColor fromPng = png.GetPixel(px, py);

        int offset = py * pixels.Stride + px * 4;   // RGBA8888
        Assert.Equal(fromPng.Red,   pixels.Pixels[offset]);
        Assert.Equal(fromPng.Green, pixels.Pixels[offset + 1]);
        Assert.Equal(fromPng.Blue,  pixels.Pixels[offset + 2]);
        Assert.True(fromPng.Red > 150 && fromPng.Green < 110,
            $"the sampled pixel should be the red square, was {fromPng}");
    }

    /// <summary>
    /// An encrypted document opened with the right password must render exactly as the plaintext
    /// original does.
    /// </summary>
    /// <remarks>
    /// The one-shot entry points call <c>EnsureDecryptedStream</c> per call, and the session uses
    /// <see cref="PdfRenderOptions.Password"/> the same way; a session <c>Open</c> going straight to
    /// <c>PdfDocument.Open</c> would make every encrypted document rendered through a session disagree
    /// with the same document rendered through <c>PdfPageRenderer</c> — the two-paths-disagreeing
    /// shape this repository keeps finding.
    /// </remarks>
    [Fact]
    public void EncryptedDocument_RendersThroughASessionAsItDoesOneShot()
    {
        Tetitulz.Pdf.Encryption.PdfEncryptionOperations.Register();

        byte[] plain = TiledPdf();
        byte[] encrypted = Encrypt(plain, "s3cret");

        var options = new PdfRenderOptions { Password = "s3cret" };
        using var session = PdfRenderSession.Open(new MemoryStream(encrypted), options);

        using SKBitmap viaSession = Decode(session.RenderPage(0, Dpi));
        using SKBitmap oneShot    = Decode(PdfPageRenderer.RenderPage(
            new MemoryStream(encrypted), 0, Dpi, options));

        Assert.Equal(oneShot.Width, viaSession.Width);
        Assert.Equal(oneShot.Height, viaSession.Height);

        int differing = 0;
        for (int y = 0; y < viaSession.Height; y++)
            for (int x = 0; x < viaSession.Width; x++)
                if (viaSession.GetPixel(x, y) != oneShot.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);

        // And it must be the real artwork, not a blank page both paths agree on.
        Assert.True(HasRed(viaSession), "the decrypted page should contain the red square");
    }

    private static byte[] Encrypt(byte[] pdf, string userPassword)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(input, output,
            new Base.Encryption.EncryptionParameters
            {
                UserPassword  = userPassword,
                OwnerPassword = "owner-only",
                Algorithm     = Base.Encryption.EncryptionAlgorithm.AES128,
                Permissions   = Base.Encryption.PdfPermissions.All,
            });
        return output.ToArray();
    }

    // ------------------------------------------------------------------
    //  The page-content memo (why ContentOperationCache can hit across tiles)
    // ------------------------------------------------------------------

    /// <summary>
    /// A page whose <c>/Contents</c> is an <b>array</b> of streams gets a freshly concatenated
    /// byte array from every <c>GetContentStreamData()</c> call, and
    /// <see cref="ContentOperationCache"/> keys on reference identity — so without a memo sixteen
    /// tiles of one multi-stream page miss the cache sixteen times and re-tokenise the whole page
    /// each time, which is precisely the cost the session exists to remove.
    /// </summary>
    [Fact]
    public void ArrayContents_AreConcatenatedOncePerSession()
    {
        using var ms  = new MemoryStream(MultiStreamContentsPdf());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var shared = PdfRenderSession.CreateSharedState(doc);
        try
        {
            // The fixture really is the shape under test: two raw reads hand back two arrays.
            Assert.NotSame(doc.Pages[0].GetContentStreamData(), doc.Pages[0].GetContentStreamData());

            byte[] first  = PdfPageRenderer.PageContent(doc.Pages[0], shared);
            byte[] second = PdfPageRenderer.PageContent(doc.Pages[0], shared);

            Assert.Same(first, second);
            Assert.Equal(doc.Pages[0].GetContentStreamData(), first);
        }
        finally { shared.Fonts.Dispose(); }
    }

    /// <summary>
    /// The consequence that matters: four tiles of a multi-stream page leave <b>one</b> entry in
    /// the shared parsed-operation cache, not four. This is the assertion that would have caught
    /// the miss, since the tiles themselves looked perfectly correct.
    /// </summary>
    [Fact]
    public void ArrayContents_AreTokenisedOnceAcrossTiles()
    {
        using var ms  = new MemoryStream(MultiStreamContentsPdf());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var shared = PdfRenderSession.CreateSharedState(doc);
        try
        {
            for (int ty = 0; ty < 2; ty++)
                for (int tx = 0; tx < 2; tx++)
                    PdfPageRenderer.RenderTileWithSharedState(doc, 0,
                        tx * 100f, ty * 100f, 100f, 100f, Dpi, shared, PdfRenderOptions.Default);

            Assert.Equal(1, shared.Operations.Count);
        }
        finally { shared.Fonts.Dispose(); }
    }

    /// <summary>
    /// Without a session there is no memo at all. The one-shot entry points promise that everything
    /// they allocate dies with the render, and a page-content memo hanging off a per-render object
    /// would quietly break that.
    /// </summary>
    [Fact]
    public void PageContent_WithoutASession_IsNotMemoised()
    {
        using var ms  = new MemoryStream(MultiStreamContentsPdf());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);

        byte[] first  = PdfPageRenderer.PageContent(doc.Pages[0], null);
        byte[] second = PdfPageRenderer.PageContent(doc.Pages[0], null);

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// The memo is bounded: past its ceiling nothing new is retained, so a long document cannot
    /// pin every page it renders in memory for the session's life. Correctness is unaffected — the
    /// content is still returned, just re-concatenated.
    /// </summary>
    [Fact]
    public void PageContent_PastItsCeiling_IsNotMemoised()
    {
        using var ms  = new MemoryStream(MultiStreamContentsPdf());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var shared = PdfRenderSession.CreateSharedState(doc);
        try
        {
            shared.MaxPageContentBytes = 0;

            byte[] first  = PdfPageRenderer.PageContent(doc.Pages[0], shared);
            byte[] second = PdfPageRenderer.PageContent(doc.Pages[0], shared);

            Assert.NotSame(first, second);
            Assert.Equal(first, second);
            Assert.Equal(0L, shared.PageContentBytes);
        }
        finally { shared.Fonts.Dispose(); }
    }

    /// <summary>
    /// A page with no <c>/Contents</c> memoises as <c>null</c> rather than counting as a miss —
    /// otherwise it re-resolves its absent content on every tile, which is the one case a
    /// "treat null as not-cached" memo gets wrong.
    /// </summary>
    [Fact]
    public void PageWithoutContents_IsMemoisedAsAKnownAbsence()
    {
        using var ms  = new MemoryStream(NoContentsPdf());
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
        var shared = PdfRenderSession.CreateSharedState(doc);
        try
        {
            Assert.Null(PdfPageRenderer.PageContent(doc.Pages[0], shared));
            Assert.True(shared.PageContent.ContainsKey(doc.Pages[0]),
                "the absence was not recorded, so every tile re-resolves /Contents");
        }
        finally { shared.Fonts.Dispose(); }
    }

    [Fact]
    public void PageCount_MatchesTheDocument()
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));
        Assert.Equal(1, session.PageCount);
    }

    [Fact]
    public void UsingAfterDispose_Throws()
    {
        var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.RenderPageTile(0, 0, 0, 100, 100, Dpi));
        Assert.Throws<ObjectDisposedException>(() => session.PageCount);
    }

    /// <summary>
    /// A second <see cref="IDisposable.Dispose"/> is idempotent: it neither throws nor un-disposes.
    /// </summary>
    /// <remarks>
    /// Asserting the post-state rather than only that the call is quiet: two <c>Dispose()</c> calls
    /// and nothing else would pass with disposal deleted outright. "Does not throw" is only the
    /// behaviour under test when the input is hostile; here the interesting claim is that the object
    /// is still disposed, which is what a resurrecting second Dispose would break.
    /// </remarks>
    [Fact]
    public void DisposingTwice_LeavesTheSessionDisposed()
    {
        var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));
        session.Dispose();
        session.Dispose();

        Assert.Throws<ObjectDisposedException>(() => session.PageCount);
        Assert.Throws<ObjectDisposedException>(() => session.RenderPageTile(0, 0, 0, 100, 100, Dpi));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void OutOfRangePage_Throws(int pageIndex)
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => session.RenderPageTile(pageIndex, 0, 0, 100, 100, Dpi));
    }

    [Theory]
    [InlineData(0f, 100f)]
    [InlineData(100f, -5f)]
    public void NonPositiveClip_Throws(float w, float h)
    {
        using var session = PdfRenderSession.Open(new MemoryStream(TiledPdf()));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.RenderPageTile(0, 0, 0, w, h, Dpi));
    }

    // ------------------------------------------------------------------

    private static bool HasRed(SKBitmap bmp)
    {
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red > 150 && c.Green < 110 && c.Blue < 110) return true;
            }
        return false;
    }

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// A 200x200 page with a red square in the bottom-left quadrant and a form XObject drawn
    /// several times, so the session's parsed-operation cache is genuinely exercised.
    /// </summary>
    private static byte[] TiledPdf()
    {
        string formBody = "0 0 1 rg 0 0 10 10 re f\n";
        var content = new StringBuilder("1 0 0 rg 20 20 60 60 re f\n");
        for (int i = 0; i < 8; i++)
            content.Append($"q 1 0 0 1 {10 + i * 12} 150 cm /Fx Do Q\n");

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        string body = content.ToString();

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
             + "/Resources << /XObject << /Fx 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
        BeginObj(5);
        Emit("<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> "
             + $"/Length {formBody.Length} >>\nstream\n{formBody}\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    /// <summary>
    /// A 200x200 page whose <c>/Contents</c> is an <b>array</b> of two streams — the shape whose
    /// concatenation has no stable identity, so it is the one the memo exists for.
    /// </summary>
    private static byte[] MultiStreamContentsPdf()
    {
        string first  = "1 0 0 rg 20 20 60 60 re f\n";
        string second = "0 0 1 rg 120 120 60 60 re f\n";

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
             + "/Resources << >> /Contents [4 0 R 5 0 R] >>\nendobj\n");
        BeginObj(4); Emit($"<< /Length {first.Length} >>\nstream\n{first}\nendstream\nendobj\n");
        BeginObj(5); Emit($"<< /Length {second.Length} >>\nstream\n{second}\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    /// <summary>A legal page with no <c>/Contents</c> entry at all (§7.7.3.3 makes it optional).</summary>
    private static byte[] NoContentsPdf()
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3); Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 4\n0000000000 65535 f \n");
        for (int i = 1; i <= 3; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
