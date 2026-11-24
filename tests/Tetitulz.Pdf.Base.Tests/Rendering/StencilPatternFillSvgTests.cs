// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An <c>/ImageMask</c> stencil painted with a <b>pattern</b> fill.
///
/// <para>The SVG writer bakes a stencil's colour into the PNG it emits, recovering the components by
/// <b>parsing <c>_gs.FillColor</c> back out of its CSS string</b>. That round trip is safe for every
/// solid colour — <c>PdfColor.ToCssRgb</c> is the only producer and emits <c>rgb(r,g,b)</c>
/// exclusively — but a pattern fill's value is <c>url(#patN)</c>, which the parser cannot read and
/// which fell through to its opaque-black default. So a stencil under a pattern exported as a solid
/// BLACK shape: not missing, not approximate, but confidently wrong, which is the failure mode
/// <c>RenderDiagnosticKind.PatternFillUnresolved</c> was introduced to stop.</para>
///
/// <para>The stencil is now emitted as a luminance <c>&lt;mask&gt;</c> with the pattern painted
/// through it. Assertions are on the parsed <see cref="XDocument"/>, never on substrings.</para>
/// </summary>
public sealed class StencilPatternFillSvgTests
{
    private sealed class ObjPdf
    {
        private readonly List<byte> _acc = new();
        private readonly Dictionary<int, int> _off = new();
        public ObjPdf() => Emit("%PDF-1.4\n");
        public void Emit(string s) => _acc.AddRange(Encoding.Latin1.GetBytes(s));
        public void Obj(int n, string dict, byte[] streamData = null)
        {
            _off[n] = _acc.Count;
            Emit($"{n} 0 obj\n{dict}");
            if (streamData != null) { Emit("\nstream\n"); _acc.AddRange(streamData); Emit("\nendstream"); }
            Emit("\nendobj\n");
        }
        public byte[] Finish(int objCount)
        {
            int xref = _acc.Count;
            Emit($"xref\n0 {objCount}\n0000000000 65535 f \n");
            for (int i = 1; i < objCount; i++)
                Emit(_off.TryGetValue(i, out int o) ? $"{o:D10} 00000 n \n" : "0000000000 65535 f \n");
            Emit($"trailer\n<< /Size {objCount} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return _acc.ToArray();
        }
    }

    /// <param name="patternFill">
    /// When true the stencil is drawn under <c>/Pattern cs /P1 scn</c>; when false under a plain
    /// <c>1 0 0 rg</c>. The two differ in nothing else, so any difference in the output is the
    /// pattern branch and not the fixture.
    /// </param>
    private static string RenderSvg(bool patternFill)
    {
        var p = new ObjPdf();
        p.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        p.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        p.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Resources << " +
                 "/XObject << /Stencil 5 0 R >> /Pattern << /P1 6 0 R >> >> /Contents 4 0 R >>");

        string setFill = patternFill ? "/Pattern cs /P1 scn" : "1 0 0 rg";
        string cs = $"q {setFill} 72 0 0 72 0 0 cm /Stencil Do Q";
        p.Obj(4, $"<< /Length {cs.Length} >>", Encoding.Latin1.GetBytes(cs));

        // 2×1 stencil, 0x80 → bit 0 set, bit 1 clear. Under the default /Decode [0 1] a ZERO bit
        // paints, so the right-hand pixel is the painted one.
        p.Obj(5, "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ImageMask true " +
                 "/BitsPerComponent 1 /Length 1 >>", new byte[] { 0x80 });

        // A tiling pattern (PatternType 1) painting a green square in each 8×8 cell.
        const string cell = "0 1 0 rg 0 0 8 8 re f";
        p.Obj(6, "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
                 $"/BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >> /Length {cell.Length} >>",
              Encoding.Latin1.GetBytes(cell));

        using var ms = new MemoryStream(p.Finish(7));
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static XDocument Parse(string svg) => XDocument.Parse(svg);

    [Fact]
    public void StencilUnderPatternFill_IsPaintedWithThePattern_NotBlack()
    {
        var doc = Parse(RenderSvg(patternFill: true));

        // The element carrying the stencil must reference a pattern paint AND be masked by it.
        var painted = doc.Descendants()
            .Where(e => (string)e.Attribute("fill") is string f && f.StartsWith("url(#")
                     && e.Attribute("mask") != null)
            .ToList();

        Assert.True(painted.Count > 0,
            "no element paints the stencil with a pattern; the stencil is baked to a solid colour.");

        // And nothing paints it black — a black shape with no pattern at all is the failure.
        Assert.DoesNotContain(doc.Descendants(),
            e => (string)e.Attribute("fill") == "rgb(0,0,0)" && e.Attribute("mask") != null);
    }

    [Fact]
    public void StencilUnderPatternFill_DefinesTheMaskItReferences()
    {
        // A mask="url(#x)" naming a mask that does not exist renders as *nothing* in a viewer, so
        // the reference and the definition have to be asserted together — this is the assertion
        // that would catch the id counter regressing to a per-writer one.
        var doc = Parse(RenderSvg(patternFill: true));

        var maskRefs = doc.Descendants()
            .Select(e => (string)e.Attribute("mask"))
            .Where(m => m != null && m.StartsWith("url(#stencil"))
            .Select(m => m.Substring("url(#".Length).TrimEnd(')'))
            .ToList();

        Assert.NotEmpty(maskRefs);

        var definedIds = doc.Descendants()
            .Where(e => e.Name.LocalName == "mask")
            .Select(e => (string)e.Attribute("id"))
            .ToHashSet();

        foreach (string id in maskRefs)
            Assert.Contains(id, definedIds);
    }

    [Fact]
    public void StencilUnderSolidFill_StillTakesThePlainImagePath()
    {
        // The control. Without it the writer could route EVERY stencil through the mask branch
        // and both assertions above would still pass, while every ordinary stencil changed shape.
        var doc = Parse(RenderSvg(patternFill: false));

        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "mask");
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "image");
    }
}
