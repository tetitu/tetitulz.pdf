// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="FontCache.GetEncodingCodec"/> is how both renderers learn how to split a composite
/// font's bytes into codes (§9.7.5). It owns three decisions the Core resolver cannot: the
/// <c>null</c> that means "not a composite font, nothing to cache", the memoisation, and the
/// translation of Core's <c>LastEncodingError</c> into a <see cref="RenderDiagnostics"/> report.
///
/// <para><b>Why this file exists.</b> The wrapper had <b>no coverage whatsoever</b>, established by
/// mutation rather than by reading: replacing its codec with <c>CMapCodec.Identity(1)</c> — not a
/// subtle change, but <i>vertical</i> writing mode for every composite font in the document — left
/// all 3 010 <c>Base.Tests</c> green. That was found while consolidating the wrapper's duplicated
/// resolution into Core, and it is the reason CLAUDE.md insists a merge's <i>survivor</i> be
/// mutated: a consolidation whose survivor no test reaches is a deletion, and a green suite cannot
/// tell the two apart.</para>
/// </summary>
public class FontCacheEncodingCodecTests
{
    private const string RegisteredIdentityH = "Identity-H";

    /// <summary>
    /// A one-page document whose font <c>F0</c> is either a Type 0 font with the given
    /// <c>/Encoding</c>, or a simple Type 1 font.
    /// </summary>
    private static byte[] Document(string encodingEntry, bool composite = true)
    {
        string font = composite
            ? $"<< /Type /Font /Subtype /Type0 /BaseFont /Test {encodingEntry} /DescendantFonts [6 0 R] >>"
            : "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>",
            font,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        if (composite)
            bodies.Add("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /DW 1000 >>");

        var sb = new StringBuilder();
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void WithCodec(byte[] pdf, System.Action<Core.Fonts.CMapCodec> assert)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        assert(cache.GetEncodingCodec("F0", doc.Pages[0].Resources, doc));
    }

    /// <summary>
    /// The decision the wrapper owns and Core deliberately does not: a font that is not composite
    /// has no encoding codec to cache. <c>CMapCodec.FromFontEncoding</c> never returns null,
    /// because for a descendant CIDFont handed in directly Identity is the right answer.
    /// </summary>
    [Fact]
    public void ASimpleFontHasNoEncodingCodec()
    {
        WithCodec(Document(null, composite: false), codec => Assert.Null(codec));
    }

    /// <summary>
    /// The mutation that used to survive: the codec's <b>writing mode</b>. Identity-H is
    /// horizontal, and a wrapper that quietly handed back <c>Identity(1)</c> would turn every
    /// composite font in the document vertical with nothing noticing.
    /// </summary>
    [Fact]
    public void IdentityHYieldsAHorizontalTwoByteCodec()
    {
        WithCodec(Document($"/Encoding /{RegisteredIdentityH}"), codec =>
        {
            Assert.NotNull(codec);
            Assert.Equal(0, codec.WMode);

            var code = Assert.Single(codec.Decode(new byte[] { 0x00, 0x41 }));
            Assert.Equal(2, code.ByteLength);
            Assert.Equal(0x0041, code.Value);
            Assert.Equal(0x0041, code.Cid);      // Identity: CID == code
        });
    }

    /// <summary>Identity-V is the same codec in vertical mode — the control for the test above.</summary>
    [Fact]
    public void IdentityVYieldsAVerticalCodec()
    {
        WithCodec(Document("/Encoding /Identity-V"), codec =>
        {
            Assert.NotNull(codec);
            Assert.Equal(1, codec.WMode);
        });
    }

    /// <summary>
    /// An embedded CMap stream really is parsed, not silently replaced by Identity: this one maps
    /// code <c>0x0041</c> to CID 5, which Identity never would.
    /// </summary>
    [Fact]
    public void AnEmbeddedCMapStreamIsParsedAndItsCidMappingUsed()
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin\nbegincmap\n" +
            "begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 begincidrange\n<0041> <0041> 5\nendcidrange\nendcmap\n";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /Font << /F0 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 6 0 R /DescendantFonts [7 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {cmap.Length} >>\nstream\n{cmap}\nendstream",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /DW 1000 >>",
        };
        var sb = new StringBuilder();
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        WithCodec(Encoding.ASCII.GetBytes(sb.ToString()), codec =>
        {
            Assert.NotNull(codec);
            var code = Assert.Single(codec.Decode(new byte[] { 0x00, 0x41 }));
            Assert.Equal(5, code.Cid);           // the stream's mapping, not Identity's
            Assert.Equal(0x0041, code.Value);
        });
    }

    /// <summary>
    /// The third thing the wrapper owns: turning Core's <c>LastEncodingError</c> into a render
    /// diagnostic. "No <c>/Encoding</c>" and "an <c>/Encoding</c> naming a CMap nobody registered"
    /// produce identical bytes and very different glyphs, so the fallback has to be audible.
    ///
    /// <para>The handler is filtered to this thread: an unfiltered handler on the process-wide
    /// <c>RenderDiagnostics.OnEvent</c> makes any assertion about it a claim about the whole
    /// process, which CLAUDE.md records as a real source of flaky whole-solution runs.</para>
    /// </summary>
    [Fact]
    public void AnUnregisteredPredefinedCMapIsReportedRatherThanSilentlyBecomingIdentity()
    {
        int thisThread = System.Environment.CurrentManagedThreadId;
        var seen = new List<string>();

        void Handler(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (System.Environment.CurrentManagedThreadId != thisThread) return;
            if (kind == RenderDiagnosticKind.MissingPredefinedCMap) lock (seen) seen.Add(detail);
        }

        RenderDiagnostics.OnEvent += Handler;
        try
        {
            WithCodec(Document("/Encoding /UniJIS-UCS2-H"), codec =>
            {
                Assert.NotNull(codec);                 // it still falls back, it just says so
                Assert.Equal(0, codec.WMode);
            });
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Handler;
        }

        string reported = Assert.Single(seen);
        Assert.Contains("UniJIS-UCS2-H", reported);
    }

    /// <summary>
    /// The control for the test above: a codec that resolves cleanly must report nothing, or the
    /// diagnostic fires on conformant files and callers learn to ignore it — the failure mode
    /// CLAUDE.md records for the `/HT /Default` false reports.
    /// </summary>
    [Fact]
    public void AResolvableEncodingReportsNothing()
    {
        int thisThread = System.Environment.CurrentManagedThreadId;
        var seen = new List<string>();

        void Handler(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (System.Environment.CurrentManagedThreadId != thisThread) return;
            if (kind == RenderDiagnosticKind.MissingPredefinedCMap) lock (seen) seen.Add(detail);
        }

        RenderDiagnostics.OnEvent += Handler;
        try
        {
            WithCodec(Document($"/Encoding /{RegisteredIdentityH}"), codec => Assert.NotNull(codec));
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Handler;
        }

        Assert.Empty(seen);
    }

    /// <summary>The memo is the wrapper's other job: the same name must not re-resolve.</summary>
    [Fact]
    public void TheCodecIsMemoisedPerResourceName()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document($"/Encoding /{RegisteredIdentityH}")));
        using var cache = new FontCache();

        var first = cache.GetEncodingCodec("F0", doc.Pages[0].Resources, doc);
        var second = cache.GetEncodingCodec("F0", doc.Pages[0].Resources, doc);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }
}
