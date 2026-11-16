// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <see cref="PostScriptConversion.ConvertDocument"/> must read its source from the beginning, not
/// from wherever the caller left the position.
/// </summary>
/// <remarks>
/// <para>The read was a bare <c>source.CopyTo(ms)</c>, which starts at the <b>current</b> position.
/// A host that had already sniffed the <c>%!PS</c> marker handed the interpreter a headerless tail;
/// one that had read to the end handed it <b>nothing</b>.</para>
///
/// <para><b>And nothing failed.</b> An empty program is a valid conversion producing an empty
/// document, so the caller got a successful result describing a blank PDF rather than an error. That
/// is why these assert on the converted <i>content</i> rather than on the absence of an
/// exception.</para>
///
/// <para>The fixture is a <see cref="MemoryStream"/> here on purpose and it still reproduces,
/// because unlike the buffering helpers in the Encryption assembly this site had no
/// <c>MemoryStream</c> fast path at all — every seekable stream took the same unrewound
/// <c>CopyTo</c>.</para>
/// </remarks>
public class PrePositionedSourceTests
{
    private const string Program = "%!PS-Adobe-3.0\n100 100 moveto 200 200 lineto stroke showpage\n";

    /// <summary>
    /// <b>The defect.</b> A source already read past produces the same PDF as one at position 0.
    /// </summary>
    /// <remarks>
    /// <b>The positions are chosen to land inside the drawing commands, and that is not arbitrary.</b>
    /// The first version of this theory used 1 and 15, which fall inside the <c>%!PS-Adobe-3.0</c>
    /// comment — and both passed with the defect fully intact, because losing a comment changes
    /// nothing about the converted output. A position that discriminates has to remove something
    /// that draws. Verified by mutation: with the unrewound copy restored, every case below fails.
    /// </remarks>
    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(45)]
    public void APrePositionedSourceConvertsTheWholeProgram(int startPosition)
    {
        byte[] fromStart = Convert(0);
        byte[] fromMiddle = Convert(startPosition);

        Assert.Equal(fromStart.Length, fromMiddle.Length);
    }

    /// <summary>
    /// The extreme: a caller that read to the end used to hand over zero bytes, and the conversion
    /// reported success for a blank document.
    /// </summary>
    [Fact]
    public void ASourceReadToItsEndStillConvertsTheWholeProgram()
    {
        byte[] fromStart = Convert(0);
        byte[] fromEnd = Convert(Encoding.ASCII.GetByteCount(Program));

        Assert.Equal(fromStart.Length, fromEnd.Length);
    }

    /// <summary>
    /// <b>The control.</b> The program really does convert to something, so the equalities above are
    /// not comparing two empty documents — which is exactly what the defect produced.
    /// </summary>
    [Fact]
    public void TheProgramReallyConvertsToADocument()
    {
        byte[] pdf = Convert(0);

        Assert.True(pdf.Length > 400, $"expected a real PDF, got {pdf.Length} bytes");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    private static byte[] Convert(int startPosition)
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(Program), writable: false);
        source.Position = startPosition;

        using var output = new MemoryStream();
        PostScriptConversion.ConvertDocument(source, output);
        return output.ToArray();
    }
}
