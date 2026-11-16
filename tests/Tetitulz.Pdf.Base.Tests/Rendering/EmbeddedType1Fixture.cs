// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A page whose <c>/F1</c> is a genuine, minimal embedded <b>Type 1</b> font program.
/// </summary>
/// <remarks>
/// <para>The sibling of <see cref="EmbeddedCffFixture"/>, and it is not interchangeable with it.
/// The two reach <b>different raster code paths</b>: an embedded CFF is wrapped into an OTF and
/// drawn as a glyph blob, while a Type 1 program is drawn from its charstring outlines through
/// <c>PdfRenderContext.DrawGlyphPath</c>. A test that means to exercise the outline path and uses
/// the CFF fixture measures the blob path instead and cannot tell — which is exactly what happened
/// once, and is why this fixture exists separately rather than as an option on that one.</para>
///
/// <para>The program is built for real rather than hand-fed to a parser: <c>FontCache</c> reads
/// <c>/Length1</c> and <c>/Length2</c> to split the clear-text header from the <c>eexec</c> section,
/// so both the split and <c>Type1Parser</c> are under test whenever this fixture is used.</para>
/// </remarks>
internal static class EmbeddedType1Fixture
{
    /// <summary>The character code the fixture font maps to its one drawable glyph.</summary>
    public const int GlyphCharCode = 65;   // 'A'

    /// <summary>Side of the filled box the glyph draws, in 1000-unit em glyph space.</summary>
    public const int GlyphBoxEm = 400;

    /// <summary>The glyph's advance width, in 1000-unit em glyph space.</summary>
    public const int GlyphAdvanceEm = 400;

    /// <summary>
    /// One 612x792 page whose <c>/F1</c> is the embedded Type 1 font and whose content stream is
    /// <paramref name="content"/>.
    /// </summary>
    /// <param name="content">The page's content stream.</param>
    /// <param name="extraResources">More page resources, after <c>/Font &lt;&lt; /F1 5 0 R &gt;&gt;</c>.</param>
    /// <param name="trailingObjects">Objects numbered from 8, which the resources may refer to.</param>
    /// <param name="trailingObjectCount">How many objects <paramref name="trailingObjects"/> holds.</param>
    public static byte[] Page(string content, string extraResources = "", string trailingObjects = null,
                              int trailingObjectCount = 0)
    {
        (byte[] program, int length1, int length2) = MinimalProgram();

        string objects =
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /ProbeFont "
          + $"/FirstChar {GlyphCharCode} /LastChar {GlyphCharCode} /Widths [{GlyphAdvanceEm}] "
          + "/FontDescriptor 6 0 R >>\nendobj\n"
          + "6 0 obj\n<< /Type /FontDescriptor /FontName /ProbeFont /Flags 4 "
          + $"/FontBBox [0 0 {GlyphBoxEm} {GlyphBoxEm}] /ItalicAngle 0 /Ascent {GlyphBoxEm} "
          + $"/Descent 0 /CapHeight {GlyphBoxEm} /StemV 80 /FontFile 7 0 R >>\nendobj\n"
          + $"7 0 obj\n<< /Length {program.Length} /Length1 {length1} /Length2 {length2} "
          + "/Length3 0 >>\nstream\n";

        return EmbeddedCffFixture.SinglePage(
            content, "/Font << /F1 5 0 R >> " + extraResources, objects,
            extraBinary: program, extraTail: "\nendstream\nendobj\n",
            extraObjectCount: 3 + trailingObjectCount, trailingObjects: trailingObjects);
    }

    /// <summary>
    /// The font program: <c>.notdef</c> and one glyph, <c>A</c>, encoded at
    /// <see cref="GlyphCharCode"/> — built by the shared <see cref="MinimalType1"/>.
    /// </summary>
    public static (byte[] program, int length1, int length2) MinimalProgram()
    {
        // 'A': 0 400 hsbw, then a 400x400 box, closepath, endchar.
        // Type 1 number encoding matches Type 2 for these ranges: v+139 for -107..107,
        // 247/252 pairs for +-108..1131.
        byte[] charstringA =
        {
            139, 248, 36, 13,        // 0 400 hsbw
            139, 139, 21,            // 0 0 rmoveto
            139, 248, 36, 5,         // 0 400 rlineto
            248, 36, 139, 5,         // 400 0 rlineto
            139, 252, 36, 5,         // 0 -400 rlineto
            9,                       // closepath
            14,                      // endchar
        };
        byte[] notdef = { 139, 139, 13, 14 };   // 0 0 hsbw endchar

        return MinimalType1.Build(
            new[] { (".notdef", notdef), ("A", charstringA) },
            encoding: new Dictionary<int, string> { [GlyphCharCode] = "A" },
            fontBBox: $"{{0 0 {GlyphBoxEm} {GlyphBoxEm}}}");
    }
}
