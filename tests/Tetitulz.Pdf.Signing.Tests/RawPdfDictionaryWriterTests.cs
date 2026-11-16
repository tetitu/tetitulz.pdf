// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <c>RawPdfDictionaryWriter</c> refuses to emit the same key twice, and is otherwise byte-for-byte
/// what the hand-written <c>WriteRaw</c> calls it replaced produced.
///
/// <para><b>Both halves matter and they check different things.</b>
/// <c>SignatureDictionaryKeyUniquenessTests</c> asserts the OUTPUT of a real signing run has unique
/// keys — the evidence. This asserts the MECHANISM refuses, which the output test cannot: a signer
/// that happens never to write a duplicate satisfies the output test with no guard present at
/// all.</para>
///
/// <para>The byte-equality tests exist because this type was introduced by refactoring a live
/// signing path whose <c>/ByteRange</c> and <c>/Contents</c> placeholders are patched by
/// <b>absolute stream offset</b>. A single byte of drift there does not produce a malformed file —
/// it produces a file whose signature silently fails to verify.</para>
/// </summary>
public class RawPdfDictionaryWriterTests
{
    private static (RawPdfDictionaryWriter Dict, PdfWriter Writer, MemoryStream Stream) NewWriter()
    {
        var ms = new MemoryStream();
        var w = new PdfWriter(ms);
        return (new RawPdfDictionaryWriter(w, "the test dictionary"), w, ms);
    }

    private static string Text(MemoryStream ms) => Encoding.ASCII.GetString(ms.ToArray());

    /// <summary>
    /// The whole point: a repeated key is refused, not written. Emitting it is what ISO 32000-1
    /// §7.3.7 leaves undefined.
    /// </summary>
    [Fact]
    public void WritingTheSameKeyTwiceIsRefused()
    {
        var (dict, _, ms) = NewWriter();
        dict.Entry("Location", "(somewhere)");

        var ex = Assert.Throws<PdfSigningException>(() => dict.Entry("Location", "(elsewhere)"));

        Assert.Contains("/Location", ex.Message, System.StringComparison.Ordinal);
        // And nothing of the second entry reached the stream — a guard that throws after writing
        // would leave exactly the malformed dictionary it exists to prevent.
        Assert.Equal(" /Location (somewhere)", Text(ms));
    }

    /// <summary>
    /// The refusal spans the three ways an entry can be claimed, because the signer uses all
    /// three: a value entry, a placeholder whose value the caller writes itself
    /// (<c>/ByteRange</c>, <c>/Contents</c>), and a key copied verbatim from an adopted field.
    /// </summary>
    [Theory]
    [InlineData("Entry", "BeginEntry")]
    [InlineData("BeginEntry", "Entry")]
    [InlineData("ClaimOnly", "Entry")]
    [InlineData("Entry", "ClaimOnly")]
    [InlineData("BeginEntry", "ClaimOnly")]
    public void TheRefusalHoldsAcrossEveryWayOfClaimingAKey(string first, string second)
    {
        var (dict, _, _) = NewWriter();

        void Claim(string how)
        {
            switch (how)
            {
                case "Entry":      dict.Entry("V", "1 0 R"); break;
                case "BeginEntry": dict.BeginEntry("V");     break;
                case "ClaimOnly":  dict.ClaimOnly("V");      break;
            }
        }

        Claim(first);
        Assert.Throws<PdfSigningException>(() => Claim(second));
    }

    /// <summary>
    /// Different keys are of course fine — without this the theory above would be satisfied by a
    /// writer that refuses everything.
    /// </summary>
    [Fact]
    public void DistinctKeysAreAllWritten()
    {
        var (dict, _, ms) = NewWriter();

        dict.Entry("Type", "/Sig");
        dict.Entry("Filter", "/Adobe.PPKLite");
        dict.Entry("SubFilter", "/adbe.pkcs7.detached");

        Assert.Equal(" /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached", Text(ms));
    }

    /// <summary>
    /// <c>Entry</c> emits exactly <c>" /Name value"</c> — the byte sequence the
    /// <c>WriteRaw($" /Type /Sig")</c> calls it replaced produced.
    /// </summary>
    [Fact]
    public void EntryIsByteForByteWhatTheRawWriteProduced()
    {
        var (dict, _, ms) = NewWriter();
        dict.Entry("Reason", "(a stated reason)");

        Assert.Equal(" /Reason (a stated reason)", Text(ms));
    }

    /// <summary>
    /// <c>BeginEntry</c> emits <c>" /Name "</c> — key and ONE trailing space, nothing more — so the
    /// caller's next write lands at the same offset it did before. This is the one that would
    /// silently break signing: <c>/Contents</c>' hex placeholder is patched by absolute offset, so
    /// an extra or missing byte here moves the patch target and the signature stops verifying while
    /// the file still parses.
    /// </summary>
    [Fact]
    public void BeginEntryEmitsTheKeyAndExactlyOneTrailingSpace()
    {
        var (dict, writer, ms) = NewWriter();

        dict.BeginEntry("Contents");
        long valueStart = ms.Position;
        writer.WriteRaw("<0000>");

        Assert.Equal(" /Contents ", Text(ms).Substring(0, (int)valueStart));
        Assert.Equal(" /Contents <0000>", Text(ms));
    }

    /// <summary>
    /// <c>ClaimOnly</c> reserves the name and writes nothing at all — the adopted-field case, where
    /// the key/value pair is emitted through <c>PdfWriter.WriteObject</c> rather than as text.
    /// </summary>
    [Fact]
    public void ClaimOnlyWritesNothing()
    {
        var (dict, _, ms) = NewWriter();

        dict.ClaimOnly("Rect");
        dict.ClaimOnly("DA");

        Assert.Equal(string.Empty, Text(ms));
    }
}
