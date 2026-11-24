// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// A WOFF 2.0 container whose Brotli stream expands far past the size its own directory declares is
/// refused <b>without inflating it</b>.
/// </summary>
/// <remarks>
/// <para><b>Refusing is not the property under test, and asserting it alone would prove nothing.</b>
/// A decoder materialising the whole expansion and only then comparing the length refuses this
/// container too — answer identical, work unbounded: a 200-byte file could make the process
/// allocate gigabytes on its way to <see langword="null"/>. So the discriminating assertion here
/// is on <b>allocation</b>.</para>
///
/// <para><b>Why the fixture declares 64 bytes rather than approaching the 64 MiB ceiling.</b> The
/// bound the decoder applies is the directory's summed lengths (§4.1), not
/// <c>MaxSfntBytes</c> — so a container can be made to prove the point at any scale, and a small
/// declared total keeps the expansion needed to overrun it small enough to build in a test.</para>
///
/// <para><b>What this does not claim.</b> Brotli sizes its ring buffer from the stream's
/// <c>WBITS</c>, inside the package, so the decoder's window is allocated whatever the caller does;
/// the fixture is compressed with the smallest legal window (<c>lgwin: 10</c>) so that window is
/// not what the ceiling below is measuring. The bounded quantity is the <i>output</i>, which is the
/// half a small input can make arbitrarily large.</para>
///
/// <para><b>The acceptance control is in <c>Woff2DecoderTests</c></b> — eight production fonts
/// against an independent font tool, and the two checked-in fixtures — because a read loop that refused everything
/// would pass every assertion in this file. <see cref="AnExactlyDeclaredBlockStillDecodes"/> is the
/// local half of that pairing: it proves the loop can fill its buffer and accept.</para>
/// </remarks>
public class Woff2BrotliBombTests
{
    /// <summary>The table block this container's directory declares — one small 'cmap'.</summary>
    private const int DeclaredBlockBytes = 64;

    /// <summary>
    /// What the bomb actually expands to: far past the declared total, and far past the ceiling
    /// asserted below, so the two readings cannot be confused for each other.
    /// </summary>
    private const int BombExpandedBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The allocation the decode is allowed. Comfortably above the declared 64 bytes plus the
    /// decoder's own state, and an order of magnitude below <see cref="BombExpandedBytes"/>, so
    /// neither a modest change in Brotli's internals nor measurement noise can move the verdict.
    /// </summary>
    private const long AllocationCeiling = 8L * 1024 * 1024;

    [Fact]
    public void ABrotliBombIsRefusedWithoutBeingInflated()
    {
        byte[] container = Container(Bomb(BombExpandedBytes), DeclaredBlockBytes);

        // Warm the decoder's static state and JIT the path, so the measurement below is the
        // decode's own allocation rather than one-time initialisation on this thread.
        Woff2Decoder.Decode(container);

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] sfnt = Woff2Decoder.Decode(container);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(sfnt);
        Assert.True(
            allocated < AllocationCeiling,
            $"the decode allocated {allocated:N0} bytes for a container declaring " +
            $"{DeclaredBlockBytes} — the {BombExpandedBytes:N0}-byte expansion was materialised " +
            "before being refused, which is the defect this test exists for");
    }

    /// <summary>
    /// A stream that stops short of the declared total is refused too — the same equality, from the
    /// other side.
    /// </summary>
    /// <remarks>
    /// §4.1 makes the directory's summed lengths equal the decompressed size, so a short block is
    /// as much a contradiction as an over-long one. It matters that this is a *separate* case:
    /// filling the buffer and testing for leftovers catches the bomb, and only the loop's own
    /// <c>read &lt;= 0</c> exit catches this.
    /// </remarks>
    [Fact]
    public void ABlockShorterThanTheDirectoryDeclaresIsRefused()
    {
        byte[] container = Container(Bomb(DeclaredBlockBytes - 1), DeclaredBlockBytes);

        Assert.Null(Woff2Decoder.Decode(container));
    }

    /// <summary>
    /// The acceptance control: a block of exactly the declared size is read through and reaches
    /// reassembly, so the refusals above are the guard talking and not the loop failing.
    /// </summary>
    /// <remarks>
    /// The assertion is that the decode gets <b>past</b> <c>BrotliInflate</c>, which is observable
    /// because a single 'cmap' table reassembles into a well-formed sfnt: the four-byte version,
    /// then a table count of one. A decoder that refused every stream would return null here.
    /// </remarks>
    [Fact]
    public void AnExactlyDeclaredBlockStillDecodes()
    {
        byte[] container = Container(Bomb(DeclaredBlockBytes), DeclaredBlockBytes);

        byte[] sfnt = Woff2Decoder.Decode(container);

        Assert.NotNull(sfnt);
        Assert.Equal(1, (sfnt[4] << 8) | sfnt[5]);      // numTables in the sfnt offset table
    }

    // ── the other attack: a container that LIES about its size, rather than one that expands ──

    /// <summary>
    /// <b>A header claiming a font larger than the ceiling is refused.</b> <c>totalSfntSize</c> is read,
    /// tested against <c>MaxSfntBytes</c>, and never used again — so this guard changes the
    /// <i>verdict</i>, not the memory: without it, a container whose header claims an absurd font
    /// but whose tables are small decodes as though the claim were never made.
    /// </summary>
    /// <remarks>
    /// The same container with an honest header is <see cref="AnExactlyDeclaredBlockStillDecodes"/>,
    /// so the only thing this test changes is the four bytes of the claim.
    /// </remarks>
    [Fact]
    public void AHeaderClaimingAnOversizedFontIsRefused()
    {
        byte[] container = Container(Bomb(DeclaredBlockBytes), DeclaredBlockBytes);
        Woff2Bytes.WriteU32(container, 16, uint.MaxValue);          // totalSfntSize: past any ceiling

        Assert.Null(Woff2Decoder.Decode(container));
    }

    /// <summary>
    /// <b>Adversarial: a directory whose tables each pass the ceiling but whose SUM does not.</b> The
    /// sum is what sizes the buffer the stream is inflated into, so it must be refused before that
    /// buffer is allocated — the attack a tiny file makes by declaring, rather than expanding.
    /// </summary>
    /// <remarks>
    /// <para><b>One guard stands on this: <c>BrotliInflate</c>'s, at the allocation.</b> There used to
    /// be three — the summing loop refused as the total crossed <c>MaxSfntBytes</c>, and the
    /// directory tested each <c>transformLength</c> too — and deleting any one left another to
    /// refuse, so none of them could be pinned. The two copies were deleted; mutating the survivor
    /// out is what this test now kills on its own.</para>
    ///
    /// <para><b>With it gone, the verdict still does not change</b>, which is why this asserts
    /// allocation exactly as the bomb test above does: the stream is short of the declared total, so
    /// the read loop refuses it too — after <c>new byte[sum]</c> has taken tens of megabytes. Refusing
    /// is the answer either way; only the work differs.</para>
    ///
    /// <para>The ceiling is read from the decoder rather than written here, so raising it cannot quietly
    /// turn this fixture into one that no longer exceeds it — at which point the <i>correct</i> code
    /// would allocate the sum and fail the assertion for a reason unrelated to any defect.</para>
    /// </remarks>
    [Fact]
    public void ADirectoryWhoseTablesSumPastTheCeilingIsRefusedWithoutAllocatingIt()
    {
        long ceiling = (long)typeof(Woff2Decoder)
            .GetField("MaxSfntBytes", System.Reflection.BindingFlags.NonPublic |
                                      System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        // Each table is just over half the ceiling: under it on its own, so the per-entry check
        // passes, and over it together, so only the sum can refuse.
        uint each = (uint)(ceiling / 2 + 1);
        byte[] container = TwoTableContainer(Bomb(DeclaredBlockBytes), each, each);

        Woff2Decoder.Decode(container);                  // warm, as above

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] sfnt = Woff2Decoder.Decode(container);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(sfnt);
        Assert.True(
            allocated < AllocationCeiling,
            $"the decode allocated {allocated:N0} bytes for a directory declaring {2L * each:N0} — " +
            "the buffer was sized from the declared sum before the sum was refused");
    }

    /// <summary>
    /// A two-entry container — known tags 0 (<c>cmap</c>) and 5 (<c>name</c>), both untransformed and
    /// neither with a fixed size a validator could reject — declaring <paramref name="first"/> and
    /// <paramref name="second"/> bytes, over <paramref name="block"/>. <c>totalSfntSize</c> stays
    /// small, so the header check does not refuse it before the directory is summed.
    /// </summary>
    private static byte[] TwoTableContainer(byte[] block, uint first, uint second)
    {
        const int HeaderSize = 48;
        byte[] a = Woff2Bytes.UIntBase128(first), b = Woff2Bytes.UIntBase128(second);
        int directory = 1 + a.Length + 1 + b.Length;

        var file = new byte[HeaderSize + directory + block.Length];
        file[0] = 0x77; file[1] = 0x4F; file[2] = 0x46; file[3] = 0x32;   // 'wOF2'
        Woff2Bytes.WriteU32(file, 4, 0x00010000);                                    // flavor: TrueType sfnt
        Woff2Bytes.WriteU32(file, 8, (uint)file.Length);                             // length
        Woff2Bytes.WriteU16(file, 12, 2);                                            // numTables
        Woff2Bytes.WriteU16(file, 14, 0);                                            // reserved
        Woff2Bytes.WriteU32(file, 16, 1024);                                         // totalSfntSize: honest-sized
        Woff2Bytes.WriteU32(file, 20, (uint)block.Length);                           // totalCompressedSize

        int at = HeaderSize;
        file[at++] = 0x00;                                                // 'cmap', transform 0
        Buffer.BlockCopy(a, 0, file, at, a.Length); at += a.Length;
        file[at++] = 0x05;                                                // 'name', transform 0
        Buffer.BlockCopy(b, 0, file, at, b.Length); at += b.Length;

        Buffer.BlockCopy(block, 0, file, at, block.Length);
        return file;
    }

    /// <summary>Brotli of <paramref name="plainBytes"/> zero bytes, in the smallest legal window.</summary>
    private static byte[] Bomb(int plainBytes)
        => RuntimeBrotli(new byte[plainBytes], 5, 10);

    /// <summary>
    /// The smallest WOFF 2.0 container that reaches the decompressor: a valid header, a one-entry
    /// directory naming a 'cmap' of <paramref name="declaredLength"/> bytes with no transform, and
    /// <paramref name="block"/> as the compressed table block.
    /// </summary>
    /// <remarks>
    /// Hand-built rather than patched out of a real font, so that what the directory declares and
    /// what the stream produces can be set independently — which is the entire fixture. Known-tag
    /// index 0 is 'cmap'; flags 0x00 is that index with transform version 0, which for a table
    /// other than glyf/loca means untransformed, so a single UIntBase128 length follows.
    /// </remarks>
    private static byte[] Container(byte[] block, uint declaredLength)
    {
        const int HeaderSize = 48;
        Assert.InRange(declaredLength, 1u, 127u);       // one UIntBase128 byte, no continuation

        var file = new byte[HeaderSize + 2 + block.Length];

        file[0] = 0x77; file[1] = 0x4F; file[2] = 0x46; file[3] = 0x32;   // 'wOF2'
        Woff2Bytes.WriteU32(file, 4, 0x00010000);                                    // flavor: TrueType sfnt
        Woff2Bytes.WriteU32(file, 8, (uint)file.Length);                             // length: the whole file
        Woff2Bytes.WriteU16(file, 12, 1);                                            // numTables
        Woff2Bytes.WriteU16(file, 14, 0);                                            // reserved
        Woff2Bytes.WriteU32(file, 16, 1024);                                         // totalSfntSize
        Woff2Bytes.WriteU32(file, 20, (uint)block.Length);                           // totalCompressedSize
        // majorVersion, minorVersion and the five metadata/private fields stay zero: none of them
        // affects the glyphs and the decoder reads past all of them.

        file[HeaderSize] = 0x00;                        // known tag 0 ('cmap'), transform version 0
        file[HeaderSize + 1] = (byte)declaredLength;    // origLength, UIntBase128 in one byte

        Buffer.BlockCopy(block, 0, file, HeaderSize + 2, block.Length);
        return file;
    }

    /// <summary>The runtime's Brotli encoder (Google's, inside .NET) — an encoder the decoder under test shares nothing with.</summary>
    private static byte[] RuntimeBrotli(byte[] plain, int quality, int window)
    {
        var destination = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(plain.Length)];
        if (!System.IO.Compression.BrotliEncoder.TryCompress(plain, destination, out int written, quality, window))
            throw new System.InvalidOperationException("the runtime's Brotli encoder declined the fixture");
        return destination.AsSpan(0, written).ToArray();
    }
}
