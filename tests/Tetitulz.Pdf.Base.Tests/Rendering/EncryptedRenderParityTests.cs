// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Every rasterizing entry point must resolve an encrypted document before reading its content
/// streams, and must accept a password to do so.
///
/// <para><b>What one entry point alone would leave.</b> Decryption written inline in
/// <c>RenderPage(Stream,int,int,PdfRenderOptions)</c> and nowhere else would let
/// <see cref="PdfPageRenderer.RenderPageAsJpg(Stream,int,int,int,PdfRenderOptions)"/>, the
/// <c>RenderAllPages*</c> family and both tile paths hand still-encrypted content streams to the
/// interpreter. Those streams do not inflate, so the page would come out blank: <b>the same PDF
/// rendered correctly to PNG and blank to JPEG.</b> A hardcoded empty password would leave a
/// document with a real user password unrenderable.</para>
///
/// <para>Each test below asserts a decoded red pixel from the fixture's rectangle. A blank page is
/// a valid non-empty PNG/JPEG, so "bytes came back" would pass against the defect — these read
/// the pixel instead.</para>
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedRenderParityTests
{
    private const string UserPassword = "s3cret";

    // MinimalPdfBuilder.CreatePdfWithRedRectangle fills x ∈ [0,306] on a 612×792 page.
    // At 72 dpi that is 1 px per point, so this point is inside the rectangle.
    private const int ProbeX = 100;
    private const int ProbeY = 400;

    private static byte[] EncryptedRedRectangle()
    {
        PdfEncryptionOperations.Register();   // wires PdfDecryptorHolder.Current

        using var input  = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }

    private static PdfRenderOptions WithPassword() =>
        new PdfRenderOptions { Password = UserPassword };

    private const string SpotName = "SPOT_185_C";

    /// <summary>
    /// A 100×100 pt page whose whole area is filled with a Separation ink at full tint — the only
    /// content <c>ExtractInkCoverage</c> can see, since a page with no spot colour yields no
    /// channels by design.
    /// </summary>
    private static byte[] SpotColourPage()
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100]"
                  + " /Resources << /ColorSpace << /Cs1 [/Separation /" + SpotName
                  + " /DeviceGray 5 0 R] >> >> /Contents 4 0 R >>\nendobj\n");

        const string cs = "q\n/Cs1 cs\n1 scn\n0 0 100 100 re\nf\nQ\n";
        int o4 = sb.Length;
        sb.AppendFormat(ci, "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
                        cs.Length, cs);

        // Identity tint transform, so tint 1 deposits full coverage.
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /FunctionType 2 /Domain [0 1] /Range [0 1] /N 1 /C0 [0] /C1 [1] >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5 })
            sb.AppendFormat(ci, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat(ci, "startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] EncryptedSpotColourPage()
    {
        PdfEncryptionOperations.Register();

        using var input  = new MemoryStream(SpotColourPage());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });
        return output.ToArray();
    }

    private static void AssertRedAtProbe(byte[] image, string what)
    {
        using var bmp = SKBitmap.Decode(image);
        Assert.NotNull(bmp);
        SKColor c = bmp!.GetPixel(ProbeX, ProbeY);
        // JPEG is lossy, so this is a "clearly red" test rather than an exact match.
        Assert.True(c.Red > 180 && c.Green < 90 && c.Blue < 90,
            $"{what} should have decrypted and drawn the red rectangle, got {c}");
    }

    /// <summary>
    /// The <b>SVG</b> entry points are stream entry points too, and were never added to this rule.
    /// </summary>
    /// <remarks>
    /// This class enumerated every <i>rasterizing</i> entry point, and `EnsureDecryptedStream`'s own
    /// note says "every stream entry point must go through this" — but `PdfSvgRenderer` did not,
    /// and nothing here would have noticed. The failure was worse than a blank page: an encrypted
    /// document's page tree is itself unreadable, so the export threw
    /// <c>"Page index 0 is out of range (document has 0 page(s))"</c> — telling the caller their
    /// file is empty when it is merely locked.
    /// <para>Asserted on <b>painted elements</b>, not on "an SVG string came back": a document
    /// whose content stream never decrypts yields a perfectly valid, perfectly empty SVG.</para>
    /// </remarks>
    [Fact]
    public void RenderPageToSvg_WithUserPassword_DecryptsAndDraws()
    {
        using var enc = new MemoryStream(EncryptedRedRectangle());
        string svg = PdfSvgRenderer.RenderPageToSvg(enc, 0, WithPassword());

        using var plain = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
        string plainSvg = PdfSvgRenderer.RenderPageToSvg(plain, 0);

        Assert.True(PaintedElements(plainSvg) > 0, "the unencrypted control painted nothing");
        Assert.Equal(PaintedElements(plainSvg), PaintedElements(svg));
    }

    [Fact]
    public void RenderAllPagesToSvg_WithUserPassword_DecryptsAndDraws()
    {
        using var enc = new MemoryStream(EncryptedRedRectangle());
        string[] pages = PdfSvgRenderer.RenderAllPagesToSvg(enc, WithPassword());

        Assert.NotEmpty(pages);
        Assert.True(PaintedElements(pages[0]) > 0,
            "the all-pages SVG path drew nothing from an encrypted document");
    }

    private static int PaintedElements(string svg)
    {
        int n = 0;
        foreach (var e in System.Xml.Linq.XDocument.Parse(svg).Descendants())
            if (e.Name.LocalName is "path" or "rect" or "image" or "use" or "text" or "polygon") n++;
        return n;
    }

    [Fact]
    public void RenderPageToPng_WithUserPassword_DecryptsAndDraws()
    {
        using var ms = new MemoryStream(EncryptedRedRectangle());
        AssertRedAtProbe(PdfOperations.RenderPageToPng(ms, 0, 72, WithPassword()), "PNG");
    }

    /// <summary>The defect: this path never decrypted, so it produced a blank page.</summary>
    [Fact]
    public void RenderPageToJpg_WithUserPassword_DecryptsAndDraws()
    {
        using var ms = new MemoryStream(EncryptedRedRectangle());
        AssertRedAtProbe(PdfOperations.RenderPageToJpg(ms, 0, 72, 90, WithPassword()), "JPEG");
    }

    [Fact]
    public void RenderAllPagesToPng_WithUserPassword_DecryptsAndDraws()
    {
        using var ms = new MemoryStream(EncryptedRedRectangle());
        byte[][] pages = PdfOperations.RenderAllPagesToPng(ms, 72, WithPassword());
        Assert.Single(pages);
        AssertRedAtProbe(pages[0], "all-pages PNG");
    }

    [Fact]
    public void RenderAllPagesToJpg_WithUserPassword_DecryptsAndDraws()
    {
        using var ms = new MemoryStream(EncryptedRedRectangle());
        byte[][] pages = PdfOperations.RenderAllPagesToJpg(ms, 72, 90, WithPassword());
        Assert.Single(pages);
        AssertRedAtProbe(pages[0], "all-pages JPEG");
    }

    [Fact]
    public void RenderPageTile_WithUserPassword_DecryptsAndDraws()
    {
        using var ms = new MemoryStream(EncryptedRedRectangle());
        // A 200×200 pt tile whose origin is inside the red rectangle.
        byte[] png = PdfOperations.RenderPageTile(ms, 0, 50, 300, 200, 200, 72, WithPassword());
        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        SKColor c = bmp!.GetPixel(50, 100);
        Assert.True(c.Red > 180 && c.Green < 90 && c.Blue < 90,
            $"tile should have decrypted and drawn the red rectangle, got {c}");
    }

    /// <summary>
    /// The halftoned entry points decrypt as the plain ones do: their one-bit output equals the unencrypted
    /// control's, and the control screens ink where the red rectangle is and none on the white beside it.
    /// </summary>
    [Fact]
    public void RenderPageHalftoned_WithUserPassword_DecryptsAndScreens()
    {
        var control = PdfOperations.RenderPageHalftoned(new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle()), 0, 72);
        var page = PdfOperations.RenderPageHalftoned(new MemoryStream(EncryptedRedRectangle()), 0, 72, null, WithPassword());

        int inside = 0, outside = 0;
        for (int y = ProbeY; y < ProbeY + 12; y++)
            for (int x = 0; x < 12; x++)
            {
                if (control.IsInk(ProbeX + x, y)) inside++;
                if (control.IsInk(500 + x, y)) outside++;
            }
        Assert.InRange(inside, 1, 143);   // red is a mid grey: screened, neither paper nor solid
        Assert.Equal(0, outside);
        Assert.Equal(control.Bits, page.Bits);

        var plates = PdfOperations.RenderPageSeparationsHalftoned(new MemoryStream(EncryptedRedRectangle()), 0, 72, null, WithPassword());
        var controlPlates = PdfOperations.RenderPageSeparationsHalftoned(new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle()), 0, 72);
        Assert.True(controlPlates["Magenta"].IsInk(ProbeX, ProbeY));
        Assert.Equal(controlPlates.Plates.Select(p => p.Bits), plates.Plates.Select(p => p.Bits));
    }

    /// <summary>
    /// <b>Ink coverage is a rasterizing entry point too</b>, and it was the one left out of this
    /// enumeration: it opened the document itself instead of going through
    /// <c>EnsureDecryptedStream</c>, and it had no password parameter at all to go through it with.
    /// </summary>
    /// <remarks>
    /// <para>The wrong answer was the quiet kind, and it lands on a prepress caller: an encrypted
    /// content stream does not inflate, so no operator ran, no Separation was ever selected, and the
    /// accumulator came back with <b>zero channels</b> — byte-for-byte the answer a page that uses
    /// no spot ink gives. "Which inks does this page need?" was answered "none" for every encrypted
    /// document.</para>
    /// <para>Asserted against the unencrypted control's own coverage map and on a decoded coverage
    /// byte. An empty <c>InkAccumulator</c> is a perfectly valid object, so "one came back" would
    /// pass against the defect.</para>
    /// </remarks>
    [Fact]
    public void ExtractInkCoverage_WithUserPassword_DecryptsAndAccumulatesSpotInk()
    {
        using var plain = new MemoryStream(SpotColourPage());
        var control = PdfOperations.ExtractInkCoverage(plain, 0, 72f);

        using var enc = new MemoryStream(EncryptedSpotColourPage());
        var acc = PdfOperations.ExtractInkCoverage(enc, 0, WithPassword(), 72f);

        Assert.Contains(SpotName, control.Channels.Keys);   // the control really deposits ink
        Assert.Contains(SpotName, acc.Channels.Keys);
        Assert.Equal(InkedPixels(control.Channels[SpotName]), InkedPixels(acc.Channels[SpotName]));
        // 100 pt page at 72 dpi = 100 px wide; full tint through an identity transform = 255.
        Assert.Equal(255, acc.Channels[SpotName][50 * 100 + 50]);
    }

    private static int InkedPixels(byte[] coverage)
    {
        int n = 0;
        foreach (byte b in coverage) if (b > 0) n++;
        return n;
    }

    /// <summary>
    /// An owner-password-only document opens on the empty user password, which is what the
    /// pre-existing PNG path relied on. It must keep working with no options supplied.
    /// </summary>
    [Fact]
    public void RenderPageToJpg_EmptyUserPassword_StillDecryptsWithNoOptions()
    {
        PdfEncryptionOperations.Register();

        using var input  = new MemoryStream(MinimalPdfBuilder.CreatePdfWithRedRectangle());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = "",
            OwnerPassword = "owner-only",
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All,
        });

        using var ms = new MemoryStream(output.ToArray());
        AssertRedAtProbe(PdfOperations.RenderPageToJpg(ms, 0, 72), "JPEG (empty user password)");
    }
}
