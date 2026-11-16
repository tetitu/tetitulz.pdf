// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// §8.9.7's <c>BI … ID … EI</c> boundary when the sample data itself contains the bytes
/// <c>whitespace E I</c> — the false positive the scanner's follow-byte check exists for.
/// </summary>
/// <remarks>
/// <para><b>This had no test.</b> Neutering the check — so any whitespace-delimited <c>EI</c> ends
/// the image — left all 2 194 Core tests green. The consequence is not a small one: the image stops
/// early and <i>every remaining sample byte is tokenised as operators</i>, which is the failure the
/// scanner's own comment describes and the reason it mirrors another reader's
/// <c>findDefaultInlineStreamEnd</c>.</para>
///
/// <para><b>Reaching the heuristic at all takes some care</b>, and that is most of why the case was
/// never covered. `ParseInlineImageOp` settles the boundary three exact ways first — a declared
/// <c>/L</c>, a self-delimiting outermost filter (<c>/AHx</c>, <c>/A85</c>, <c>/RL</c>,
/// <c>/DCT</c>), and an unfiltered image's computable byte count — and only then scans. A fixture
/// that supplies any of those measures the exact path and says nothing about this one. So this
/// image declares <c>/F /Fl</c> and no <c>/L</c>: flate is not self-delimiting and its presence
/// stops the unfiltered computation, which is the narrow gap the heuristic serves.</para>
///
/// <para><b>The payload is not real flate</b>, and does not need to be: nothing in the parse path
/// decodes an inline image, and this test is about where the image <i>ends</i>. Making it valid
/// deflate would mean hand-assembling a stored block to place the literal bytes, which would test
/// the same scanner through more machinery.</para>
/// </remarks>
public class InlineImageEiInBinaryDataTests
{
    /// <summary>
    /// A <c>space E I</c> followed by a byte above 0x7F is sample data, not the terminator.
    /// </summary>
    [Fact]
    public void EiFollowedByAHighByteDoesNotEndTheImage()
    {
        // "AB", then a decoy " EI" whose next byte is 0xFF, then "CD", then the real " EI".
        byte[] payload = { (byte)'A', (byte)'B', 0x20, (byte)'E', (byte)'I', 0xFF, (byte)'C', (byte)'D' };

        var ops = ContentStreamParser.Parse(InlineImageStream(payload));

        var images = ops.Where(o => o.Operator == PdfOperators.BeginInlineImage).ToList();
        Assert.Single(images);

        var stream = Assert.IsType<PdfStream>(images[0].Operands[0]);
        // The decoy must be inside the payload. Terminating there instead yields 2 bytes.
        Assert.Equal(payload.Length, stream.RawData.Length);
        Assert.Equal(payload, stream.RawData);
    }

    /// <summary>
    /// The control: the same shape with a <b>printable</b> byte after the decoy really is the
    /// terminator, so the scanner must stop at it. Without this, the test above would pass on an
    /// implementation that simply never terminates on <c>EI</c>.
    /// </summary>
    [Fact]
    public void EiFollowedByPrintableBytesDoesEndTheImage()
    {
        // Same bytes, but 'Z' where the 0xFF was — now the first " EI" is a genuine terminator.
        byte[] payload = { (byte)'A', (byte)'B', 0x20, (byte)'E', (byte)'I', (byte)'Z', (byte)'C', (byte)'D' };

        var ops = ContentStreamParser.Parse(InlineImageStream(payload));
        var images = ops.Where(o => o.Operator == PdfOperators.BeginInlineImage).ToList();
        Assert.Single(images);

        var stream = Assert.IsType<PdfStream>(images[0].Operands[0]);
        // Stops at the first EI: "AB" only, the space before E being dropped.
        Assert.Equal(2, stream.RawData.Length);
    }

    /// <summary>
    /// <c>BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID &lt;payload&gt; EI Q</c>.
    /// <c>/F /Fl</c> with no <c>/L</c> is what forces the scanning path.
    /// </summary>
    private static byte[] InlineImageStream(byte[] payload)
    {
        var ms = new MemoryStream();
        void Ascii(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        Ascii("BI /W 2 /H 2 /BPC 8 /CS /G /F /Fl ID ");
        ms.Write(payload, 0, payload.Length);
        Ascii(" EI Q\n");
        return ms.ToArray();
    }
}
