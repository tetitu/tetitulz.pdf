// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>currentfile &lt;&lt;…&gt;&gt; /CCITTFaxDecode filter</c> as an image's data source: the filter's
/// parameter dictionary taken (PLRM §3.8.4 — it was read as the source, so the filter was refused),
/// and the block's end found where the coded data says, after its <c>/Rows</c> rows or at its EOFB
/// or RTC (Core's <see cref="CcittFaxDecodeFilter.DecodeEmbedded"/>), the program resuming after it.
/// The end was "a bit pattern inside the coded rows … refused by name".
/// </summary>
public sealed class CcittInlineTests
{
    private const string Marker = " newpath 0 0 moveto 1 0 lineto 1 1 lineto closepath fill";

    private static readonly byte[] Rows = { 0xF0, 0x0F };

    private static PostScriptConversionResult Convert(string head, byte[] block, string tail = "\n0.25 setgray" + Marker)
        => PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes(head).Concat(block).Concat(Encoding.ASCII.GetBytes(tail)).ToArray());

    private static PdfStream OnlyImage(PostScriptConversionResult r)
        => r.Resources?[PdfName.XObject] is PdfDictionary x && x.Count == 1 ? (PdfStream)x.Single().Value : null;

    private static string[] Ops(PostScriptConversionResult r)
        => ContentStreamAssert.Parse(r.Content).Select(o => o.ToString()).ToArray();

    /// <summary>Group 4 with <c>/Rows</c>: the rows decoded, the block ended after them, the program resuming.</summary>
    [Fact]
    public void AGroup4Block_EndsAfterItsRows()
    {
        var r = Convert("8 2 1 [8 0 0 -2 0 2] currentfile << /K -1 /Columns 8 /Rows 2 >> /CCITTFaxDecode filter image\n",
                        CcittFaxEncoder.Encode(Rows, 8, 2));
        Assert.Equal(Rows, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>Group 4 with no <c>/Rows</c>: the block ends at its EOFB.</summary>
    [Fact]
    public void AGroup4Block_EndsAtItsEofb()
    {
        var r = Convert("8 2 1 [8 0 0 -2 0 2] currentfile << /K -1 /Columns 8 >> /CCITTFaxDecode filter image\n",
                        CcittFaxEncoder.Encode(Rows, 8, 2));
        Assert.Equal(Rows, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>
    /// Group 3: one all-white row of eight (the white run code <c>10011</c>, padded) — white, which the
    /// default <c>/BlackIs1</c> false answers as 1s — the block a byte long.
    /// </summary>
    [Fact]
    public void AGroup3Block_EndsAfterItsRows()
    {
        var r = Convert("8 1 1 [8 0 0 -1 0 1] currentfile << /K 0 /Columns 8 /Rows 1 >> /CCITTFaxDecode filter image\n",
                        new byte[] { 0x98 });
        Assert.Equal(new byte[] { 0xFF }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>
    /// Group 4 with no EOFB, ended by its <c>/Rows</c>: two white rows of eight, each the vertical code
    /// <c>1</c> against the white reference line, are the one byte <c>C0</c>, and the program follows it.
    /// </summary>
    [Fact]
    public void AGroup4BlockWithNoEofb_EndsAfterItsRows()
    {
        var r = Convert("8 2 1 [8 0 0 -2 0 2] currentfile << /K -1 /Columns 8 /Rows 2 >> /CCITTFaxDecode filter image\n",
                        new byte[] { 0xC0 });
        Assert.Equal(new byte[] { 0xFF, 0xFF }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }

    /// <summary>The RTC after a Group 3 block's last row belongs to it: the program resumes after the six EOLs.</summary>
    [Fact]
    public void AGroup3BlocksRtc_IsConsumedWithIt()
    {
        // 10011 (eight white) then six 000000000001, padded: 77 bits in ten bytes.
        string bits = "10011" + string.Concat(Enumerable.Repeat("000000000001", 6));
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        byte[] block = Enumerable.Range(0, bits.Length / 8).Select(k => System.Convert.ToByte(bits.Substring(k * 8, 8), 2)).ToArray();
        var r = Convert("8 1 1 [8 0 0 -1 0 1] currentfile << /K 0 /Columns 8 /Rows 1 >> /CCITTFaxDecode filter image\n", block);
        Assert.Equal(new byte[] { 0xFF }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
        Assert.True(r.IsClean, r.LastError);
    }

    /// <summary>
    /// Group 4 data that only runs out — no EOFB and no <c>/Rows</c>, eight white rows in one byte and
    /// the program ending there — never said where it ends: refused, not taken as an image of eight rows.
    /// </summary>
    [Fact]
    public void Group4DataThatOnlyRunsOut_IsNotTaken()
    {
        var r = Convert("8 8 1 [8 0 0 -8 0 8] currentfile << /K -1 /Columns 8 >> /CCITTFaxDecode filter image\n",
                        new byte[] { 0xFF }, tail: "");
        Assert.Null(OnlyImage(r));
        Assert.Contains("without its end-of-data", r.LastError ?? "", System.StringComparison.Ordinal);
    }

    /// <summary><c>/BlackIs1</c> is taken from the dictionary: the same white row is 0s.</summary>
    [Fact]
    public void BlackIs1_IsTaken()
    {
        var r = Convert("8 1 1 [8 0 0 -1 0 1] currentfile << /K 0 /Columns 8 /Rows 1 /BlackIs1 true >> /CCITTFaxDecode filter image\n",
                        new byte[] { 0x98 });
        Assert.Equal(new byte[] { 0x00 }, OnlyImage(r).RawData);
    }

    /// <summary>With neither <c>/Rows</c> nor an end-of-block code allowed, the data does not say where it ends: refused by name.</summary>
    [Fact]
    public void ACcittBlockThatDoesNotSayWhereItEnds_IsRefusedByName()
    {
        var r = Convert("8 1 1 [8 0 0 -1 0 1] currentfile << /K 0 /Columns 8 /EndOfBlock false >> /CCITTFaxDecode filter image\n",
                        new byte[] { 0x98 });
        Assert.Null(OnlyImage(r));
        Assert.Contains(r.UnimplementedOperators, u => u.Contains("does not say where it ends", System.StringComparison.Ordinal));
    }

    /// <summary>A parameter dictionary is taken by any filter, not read as its source: an ASCIIHex block with an empty one still reads.</summary>
    [Fact]
    public void AFilterParameterDictionary_IsNotItsSource()
    {
        var r = Convert("1 1 8 [1 0 0 -1 0 1] currentfile << >> /ASCIIHexDecode filter image\n", Encoding.ASCII.GetBytes("7F>"));
        Assert.Equal(new byte[] { 0x7F }, OnlyImage(r).RawData);
        Assert.Contains("0.25 g", Ops(r));
    }
}
