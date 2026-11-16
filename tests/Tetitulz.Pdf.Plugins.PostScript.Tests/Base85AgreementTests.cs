// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The plugin decodes ASCII base-85 twice — once for a <c>&lt;~ … ~&gt;</c> literal in
/// <c>PsScanner</c>, and once through <c>Core.IO.Filters.Ascii85DecodeFilter</c> for
/// <c>currentfile /ASCII85Decode filter</c>. The two must produce the same bytes.
/// </summary>
/// <remarks>
/// <para><b>This is one assembly giving two answers for one byte sequence, chosen by syntax.</b>
/// <c>PsFile</c> constructs the Core filter itself, so both decoders are reachable from the same
/// program.</para>
///
/// <para>They differed twice. The scanner <b>clamped</b> an out-of-range group to
/// <c>0xFFFFFFFF</c> and emitted <c>FF FF FF FF</c> where the filter drops the group and emits
/// nothing. And its <c>'u'</c>-padding line was plain <c>uint</c> and <b>wrapped</b> — on the very
/// path the accumulate loop's own comment claims is protected by <c>ulong</c> arithmetic. For
/// <c>&lt;~uuuu~&gt;</c> that produced <c>08 78 0E</c>: three bytes of plausible-looking nonsense
/// rather than a refusal.</para>
/// </remarks>
public class Base85AgreementTests
{
    /// <summary>
    /// <b>The defect.</b> Groups that overflow 32 bits — by accumulation or by <c>'u'</c>-padding —
    /// must decode identically through both paths.
    /// </summary>
    [Theory]
    [InlineData("uuuuu")]   // 85^5-1 = 4,437,053,124: overflows while accumulating
    [InlineData("uuuu")]    // overflows only when 'u'-padded to five — the wrapping case
    [InlineData("uuu")]
    [InlineData("uuuuuuuuuu")]
    [InlineData("uuuuu!!!!")]
    public void AnOverflowingGroupDecodesTheSameThroughBothPaths(string payload)
        => Assert.Equal(ViaFilter(payload), ViaScanner(payload));

    /// <summary>
    /// <b>The control.</b> Ordinary payloads must decode identically too — otherwise the theory
    /// above could be satisfied by both paths having become "return nothing".
    /// </summary>
    [Theory]
    [InlineData("87cURD]i,\"Ebo80")]
    [InlineData("z")]
    [InlineData("<+oue")]
    [InlineData("!!!!!")]
    [InlineData("@:E_WAKYr")]
    public void AnOrdinaryPayloadDecodesTheSameThroughBothPaths(string payload)
    {
        byte[] viaFilter = ViaFilter(payload);

        Assert.NotEmpty(viaFilter);
        Assert.Equal(viaFilter, ViaScanner(payload));
    }

    /// <summary>
    /// The wrapping case, named directly: <c>&lt;~uuuu~&gt;</c> must not yield the three bytes the
    /// uint arithmetic used to produce.
    /// </summary>
    [Fact]
    public void ThePaddingOverflowDoesNotWrapIntoPlausibleBytes()
    {
        byte[] bytes = ViaScanner("uuuu");

        Assert.NotEqual(new byte[] { 0x08, 0x78, 0x0E }, bytes);
        Assert.Empty(bytes);
    }

    // ------------------------------------------------------------------

    /// <summary>Decodes through Core's filter — the same object <c>PsFile</c> constructs.</summary>
    private static byte[] ViaFilter(string payload)
        => new Ascii85DecodeFilter().Decode(Encoding.ASCII.GetBytes(payload + "~>"), null);

    /// <summary>Decodes through the scanner's <c>&lt;~ … ~&gt;</c> literal path.</summary>
    private static byte[] ViaScanner(string payload)
    {
        var scanner = new PsScanner(Encoding.ASCII.GetBytes("<~" + payload + "~>"));

        Assert.True(scanner.TryNext(out PsToken token), "the scanner produced no token");
        Assert.Equal(PsTokenKind.String, token.Kind);
        return token.Bytes ?? Array.Empty<byte>();
    }
}
