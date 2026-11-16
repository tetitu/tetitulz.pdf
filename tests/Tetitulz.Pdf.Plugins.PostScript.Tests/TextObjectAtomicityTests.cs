// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A text object is emitted all-or-nothing: the byte cap can refuse a whole <c>BT … ET</c>, but it
/// can never split one, so a truncated stream never ends inside an unterminated text object.
/// </summary>
/// <remarks>
/// <para><b>This property was documented in the emitter and defended by nothing.</b>
/// <c>ContentStreamEmitter.ShowText</c>'s own comment records the defect it exists to prevent —
/// "a cap reached anywhere after <c>BT</c> swallowed the rest, including <c>ET</c>, and
/// CloseAllSaves only writes <c>Q</c>, so the truncated stream ended inside an unterminated text
/// object. q/Q balance was asserted; BT/ET was not." The second half of that sentence was still
/// true when it was written, and stayed true afterwards.</para>
///
/// <para><b>Why it needed a test now.</b> The guarantee originally came from building the text
/// object in a private <c>StringBuilder</c> so its length could be tested before a byte was
/// written. That buffer was removed (plan 16 §41): the object is appended straight into the output
/// and discarded back to a saved offset if it does not fit. The <i>property</i> is unchanged and
/// the <i>mechanism</i> is completely different — which is precisely the situation where an
/// untested invariant silently stops holding.</para>
///
/// <para><b>Why unbalanced output is worse than truncated output.</b> A reader meeting <c>ET</c>
/// with no open text object, or reaching end-of-stream inside one, may reject the entire content
/// stream — so the "valid truncated stream" the cap exists to produce becomes no drawing at all,
/// which is the failure the cap was added to avoid.</para>
/// </remarks>
public class TextObjectAtomicityTests
{
    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>Shows enough text that any small cap must land partway through the run.</summary>
    private static byte[] Program(int shows)
    {
        var sb = new StringBuilder("/Helvetica findfont 12 scalefont setfont\n");
        for (int i = 0; i < shows; i++)
            sb.Append(40).Append(' ').Append(700 - (i % 60) * 11)
              .Append(" moveto (Line of sample text ").Append(i).Append(") show\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Across a range of caps — so the refusal lands at many different points inside the emitted
    /// text — every <c>BT</c> is matched by an <c>ET</c>.
    /// </summary>
    /// <remarks>
    /// The sweep matters more than any single cap. A cap is only interesting when it falls
    /// <i>inside</i> a text object, and which byte that is depends on the coordinates, the font
    /// name and the string length. One hand-picked cap can easily land on a boundary and pass
    /// while the split case is broken.
    /// </remarks>
    [Theory]
    [InlineData(200)]
    [InlineData(317)]
    [InlineData(500)]
    [InlineData(613)]
    [InlineData(800)]
    [InlineData(1024)]
    [InlineData(1500)]
    [InlineData(2048)]
    [InlineData(3000)]
    [InlineData(4096)]
    public void ACapLandingInsideATextObject_NeverSplitsIt(int maxBytes)
    {
        var options = PostScriptConversionOptions.Default;
        options.MaxEmittedContentBytes = maxBytes;

        var result = PostScriptConversion.ConvertFragment(Program(200), options);
        string content = Encoding.ASCII.GetString(result.Content);

        int bt = Count(content, "BT\n");
        int et = Count(content, "ET");

        Assert.True(bt == et,
            $"cap {maxBytes}: {bt} BT against {et} ET — the cap split a text object, so the stream "
            + "ends inside one and a reader may reject the whole thing rather than draw the part "
            + "that fitted.\n---\n" + Tail(content));

        // …and the cap itself still holds, so this is not passing by emitting nothing at all.
        Assert.True(result.Content.Length <= maxBytes,
            $"cap {maxBytes} exceeded: {result.Content.Length} bytes");
    }

    private static string Tail(string s) => s.Length <= 300 ? s : s.Substring(s.Length - 300);

    /// <summary>
    /// The floor for the sweep above: at a cap this tight the emitter really is refusing text, so
    /// the balance assertions are examining truncated output rather than a stream that fitted whole.
    /// </summary>
    /// <remarks>
    /// Without this, every case above would still pass if the cap were never reached — balanced
    /// because nothing was cut, which is the vacuous pass this file exists to avoid.
    /// </remarks>
    [Fact]
    public void TheSweepReallyDoesTruncate()
    {
        var options = PostScriptConversionOptions.Default;
        options.MaxEmittedContentBytes = 500;

        var result = PostScriptConversion.ConvertFragment(Program(200), options);

        Assert.True(result.ContentTruncated,
            "the 500-byte cap did not truncate a 200-show program, so the atomicity sweep is "
            + "asserting balance on output that was never cut");
    }

    /// <summary>
    /// An uncapped run of the same program emits every text object, so a balanced-but-empty result
    /// cannot be mistaken for success.
    /// </summary>
    [Fact]
    public void WithoutACapEveryShowIsEmitted()
    {
        var result = PostScriptConversion.ConvertFragment(Program(200));
        string content = Encoding.ASCII.GetString(result.Content);

        Assert.False(result.ContentTruncated);
        Assert.Equal(200, Count(content, "BT\n"));
        Assert.Equal(200, Count(content, "ET"));
    }
}
