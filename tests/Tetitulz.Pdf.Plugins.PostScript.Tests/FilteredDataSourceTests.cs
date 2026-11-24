// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// <c>currentfile … filter</c> as an image data source — the standard way an EPS embeds image
/// bytes.
///
/// <para>The data block is consumed by the filter: left in the token stream, it would be scanned
/// as PostScript, producing a storm of garbage names that also consume slots in the capped
/// diagnostic list.</para>
///
/// <para><b>Every assertion reads the decoded bytes back out of the emitted image XObject.</b> That
/// a <c>Do</c> was emitted proves nothing here: the failure mode being tested is bytes that are
/// present but wrong, as a hex scanner would produce for base-85 input.</para>
/// </summary>
public sealed class FilteredDataSourceTests
{
    private static PostScriptConversionResult Convert(string ps)
        => PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(ps));

    private static byte[] ImageBytes(PostScriptConversionResult r)
    {
        if (r.Resources?[PdfName.XObject] is not PdfDictionary xobjects) return null;
        foreach (var kv in xobjects)
            if (kv.Value is PdfStream stream) return stream.RawData;
        return null;
    }

    /// <summary><c>ASCIIHexDecode</c>, terminated by its own <c>&gt;</c>.</summary>
    [Fact]
    public void AsciiHexDataSource_DecodesToItsBytes()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCIIHexDecode filter image\n00FF7F40>\n");

        Assert.Equal(new byte[] { 0x00, 0xFF, 0x7F, 0x40 }, ImageBytes(r));
    }

    /// <summary>
    /// <c>ASCII85Decode</c>, terminated by <c>~&gt;</c>. This is the case the hex scanner would
    /// have silently mangled — base-85 uses the whole printable range.
    /// </summary>
    [Fact]
    public void Ascii85DataSource_DecodesToItsBytes()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCII85Decode filter image\n!<N?+~>\n");

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, ImageBytes(r));
    }

    /// <summary>
    /// <b>The scanner resumes after the block.</b> This is what the whole lazy-execution change
    /// buys, and getting it wrong means the program's remaining operators are read out of the image
    /// data.
    /// </summary>
    [Fact]
    public void TheProgramContinuesAfterTheDataBlock()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCIIHexDecode filter image\n00FF7F40>\n" +
                        "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill");

        var ops = ContentStreamAssert.Operators(r.Content);
        Assert.Contains("Do", ops);
        Assert.Contains("f", ops);
        Assert.True(r.IsClean, r.LastError);
    }

    // A binary innermost filter — Flate, LZW, RunLength, DCT — has no marker in the byte stream.
    // Its block ends where its own data says, found by Core's filters in the pass that decodes
    // it: UndelimitedFilterSourcesTests.

    /// <summary>
    /// An unterminated block is reported rather than drawn from whatever was scanned. The scanner
    /// still consumes it, so the rest of the program is not read out of the image data.
    /// </summary>
    [Fact]
    public void AnUnterminatedBlock_IsReported()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCIIHexDecode filter image\n00FF7F40");

        Assert.False(r.IsClean);
        Assert.Contains("end-of-data marker", r.LastError);
        ContentStreamAssert.DoesNotContainOperator(r.Content, "Do");
    }

    /// <summary>
    /// <b>A declined image must still consume its block.</b> Otherwise one option flag turns the
    /// image data into PostScript — the exact storm of garbage names the design exists to prevent.
    /// With <c>AllowImages</c> off, the fill that follows must still be the only thing drawn.
    /// </summary>
    [Fact]
    public void ADeclinedImage_StillConsumesItsDataBlock()
    {
        var options = new PostScriptConversionOptions { AllowImages = false };

        var r = PostScriptConversion.ConvertFragment(Encoding.Latin1.GetBytes(
            "4 1 8 [4 0 0 -1 0 1] currentfile /ASCIIHexDecode filter image\n00FF7F40>\n" +
            "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill"), options);

        // The drawing after the block ran…
        Assert.Contains("f", ContentStreamAssert.Operators(r.Content));

        // …and the block itself was never tokenised as PostScript.
        Assert.DoesNotContain("00FF7F40", r.UnimplementedOperators);
    }

    /// <summary>
    /// <c>currentfile</c> is a real file object, not a pushed null —
    /// <c>type</c> answers <c>filetype</c>.
    /// </summary>
    [Fact]
    public void CurrentFile_IsAFileObject()
    {
        var r = Convert("currentfile type /filetype eq { 0.25 } { 0.75 } ifelse setgray " +
                        "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill");

        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 3);
    }

    /// <summary>
    /// A filter over a file is still a file, so a chain composes. The <b>innermost</b> filter
    /// decides where the block ends — here base-85, whose <c>~&gt;</c> terminates it, which is why
    /// an outer Flate never has to be delimited.
    /// </summary>
    [Fact]
    public void AFilterOverAFile_IsStillAFile()
    {
        var r = Convert("currentfile /ASCII85Decode filter type /filetype eq " +
                        "{ 0.25 } { 0.75 } ifelse setgray " +
                        "newpath 0 0 moveto 9 0 lineto 9 9 lineto fill");

        Assert.Equal(0.25, ContentStreamAssert.FirstOperandsOf(r.Content, "g")[0], 3);
    }

    /// <summary>
    /// A <c>/FlateDecode</c> data source cut short — here a zlib stream missing its final block and
    /// trailer, carried in base-85 so the block is delimited — is drawn from the bytes it had
    /// <b>and reported</b> through <see cref="FlateDecodeFilter.LastTruncationReason"/>, as an
    /// LZW source is. A whole stream through the same chain reports nothing.
    /// </summary>
    [Fact]
    public void ATruncatedFlateDataSource_IsReported_AWholeOneIsNot()
    {
        byte[] payload = Enumerable.Range(0, 64).Select(i => (byte)(i * 3)).ToArray();
        byte[] whole = FlateEncodeFilter.Encode(payload, System.IO.Compression.CompressionLevel.NoCompression);
        byte[] cut = whole.Take(whole.Length - 8).ToArray();   // the trailer and the final block's tail

        var truncated = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCII85Decode filter /FlateDecode filter image\n" + Base85(cut) + "~>\n");
        var complete = Convert("4 1 8 [4 0 0 -1 0 1] currentfile /ASCII85Decode filter /FlateDecode filter image\n" + Base85(whole) + "~>\n");

        Assert.NotNull(truncated.LastError);
        Assert.Contains("a /FlateDecode data source was truncated", truncated.LastError);
        Assert.Contains("FlateDecode:", truncated.LastError);
        Assert.NotNull(ImageBytes(truncated));

        Assert.Equal(payload.Take(4).ToArray(), ImageBytes(complete).Take(4).ToArray());
        Assert.True(complete.IsClean, complete.LastError);
    }

    /// <summary>Adobe base-85 (no <c>z</c> shorthand), without its <c>~&gt;</c>.</summary>
    private static string Base85(byte[] data)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += 4)
        {
            int n = Math.Min(4, data.Length - i);
            uint v = 0;
            for (int k = 0; k < 4; k++) v = (v << 8) | (k < n ? data[i + k] : (byte)0);
            var digits = new char[5];
            for (int k = 4; k >= 0; k--) { digits[k] = (char)('!' + v % 85); v /= 85; }
            sb.Append(digits, 0, n + 1);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The string data source is untouched — it is what older EPS uses, and it must not regress.
    /// </summary>
    [Fact]
    public void AStringDataSource_StillWorks()
    {
        var r = Convert("4 1 8 [4 0 0 -1 0 1] <00FF7F40> image");

        Assert.Equal(new byte[] { 0x00, 0xFF, 0x7F, 0x40 }, ImageBytes(r));
    }
}
