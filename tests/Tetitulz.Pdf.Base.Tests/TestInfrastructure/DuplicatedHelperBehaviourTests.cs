// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Skips — <b>visibly</b> — on a host whose local time is already UTC, where "converted rather
/// than relabelled" is unobservable because both answers are the same string.
///
/// <para>This is the shape CLAUDE.md prescribes for a host-dependent assertion: a visible skip, not
/// a looser assertion that passes everywhere by checking less. It is also the reason the defect
/// survived — a UTC build agent cannot see it.</para>
/// </summary>
public sealed class NonUtcHostFactAttribute : FactAttribute
{
    public NonUtcHostFactAttribute()
    {
        if (TimeZoneInfo.Local.BaseUtcOffset == TimeSpan.Zero &&
            !TimeZoneInfo.Local.SupportsDaylightSavingTime)
            Skip = "This host's local time is UTC, so converting to UTC and relabelling as UTC " +
                   "produce the same string and the distinction cannot be observed here.";
    }
}

/// <summary>
/// Pins the <b>current</b> behaviour of the helper clusters that exist in several drifted copies,
/// so that consolidating them produces a visible, reviewable diff rather than a silent change.
///
/// <para><b>Several of these assertions encode defects on purpose.</b> A literal string written by
/// the weaker escapers keeps a raw carriage return, which ISO 32000-1 §7.3.4.2 says a reader shall
/// treat as a line feed — so the text does not round-trip. That is pinned here as it stands today.
/// When the cluster is consolidated, the expectation flips in the same commit as the fix, and the
/// diff to this file is the record of exactly what changed. Pinning a bug is only honest if the
/// test says it is pinning a bug, which is why each one is labelled below.</para>
///
/// <para>Most of the duplicated helpers are <c>private</c>, and this assembly has no
/// <c>InternalsVisibleTo</c>, so the pins go through public entry points. That is the better test
/// anyway: it measures what a caller can observe rather than what a private method returns.</para>
/// </summary>
public class DuplicatedHelperBehaviourTests
{
    // ==================================================================
    //  Cluster 1 — PDF literal-string escaping (8 copies, 4 of them weak)
    // ==================================================================

    // The tagged-document builder was one of the eight copies, and three tests here pinned its
    // literal strings: a carriage return written as \r, a NUL as \000, and ( ) \ escaped. It has
    // left the cluster rather than been consolidated into it — it now draws through an embedded font
    // as glyph ids in a hex string, which has nothing to escape — so the pins went with it, and its
    // text round trip is asserted in PdfTaggedDocumentBuilderTests.

    /// <summary>
    /// The claim that actually matters to a caller: text survives the write. This is the test the
    /// consolidation exists for — every control character the weak escapers passed through raw is
    /// read back as itself rather than as something else.
    /// </summary>
    [Theory]
    [InlineData("carriage\rreturn")]
    [InlineData("line\nfeed")]
    [InlineData("tab\there")]
    [InlineData("nul\0byte")]
    [InlineData("delchar")]
    [InlineData(@"parens (and) a \ backslash")]
    public void Text_written_through_the_canonical_escaper_round_trips(string original)
    {
        string escaped = PdfStringEscape.EscapeLiteral(original);
        Assert.Equal(original, UnescapeLiteral(escaped));
    }

    /// <summary>A minimal §7.3.4.2 reader, so the round-trip is checked against the spec's rules rather than against the writer's own.</summary>
    private static string UnescapeLiteral(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\') { sb.Append(s[i]); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case '(': case ')': case '\\': sb.Append(n); break;
                default:
                    if (n >= '0' && n <= '7')
                    {
                        int v = 0, digits = 0;
                        while (digits < 3 && i < s.Length && s[i] >= '0' && s[i] <= '7')
                        { v = v * 8 + (s[i] - '0'); i++; digits++; }
                        i--;
                        sb.Append((char)v);
                    }
                    else sb.Append(n);
                    break;
            }
        }
        return sb.ToString();
    }

    // ==================================================================
    //  Cluster 2 — CloneDictionary (15 copies, divergent on null)
    // ==================================================================

    /// <summary>
    /// <b>Was pinning a divergence; now pins the unified contract.</b> The two public clones used
    /// to disagree about a null source — <c>PdfHelpers</c> manufactured an empty dictionary,
    /// <c>PdfPageContentAppender</c> threw <see cref="NullReferenceException"/> — and thirteen
    /// private copies split between the two. Both were wrong: an NRE loses the parameter name and
    /// is never a contract, and an empty dictionary converts "you passed me nothing" into a
    /// valid-looking empty page or font dictionary that fails much later and somewhere else.
    ///
    /// <para>Two call sites <i>do</i> want the empty-dictionary behaviour, and they now say so
    /// locally instead of inheriting it: the font remediator staging a font that has none yet, and
    /// <c>PdfAccessibility</c> building a <c>/MarkInfo</c> for a document that has none. That is
    /// the distinction this consolidation is for — a tolerance a caller has decided about, rather
    /// than one fourteen callers inherited without knowing.</para>
    /// </summary>
    [Fact]
    public void Both_public_dictionary_clones_reject_a_null_source()
    {
        Assert.Throws<ArgumentNullException>(() => PdfHelpers.CloneDictionary(null!));
        Assert.Throws<ArgumentNullException>(() => PdfPageContentAppender.CloneDictionary(null!));
        Assert.Throws<ArgumentNullException>(() => PdfDictionary.CloneShallow(null!));
        Assert.Throws<ArgumentNullException>(() => PdfDictionary.DeepClone(null!));
    }

    /// <summary>
    /// A deep clone must not treat a stream as a plain dictionary. <see cref="PdfStream"/> derives
    /// from <see cref="PdfDictionary"/>, and the copy this replaced tested the base type first —
    /// so it rebuilt streams as dictionaries and dropped their data, contradicting its own
    /// documentation that streams are shallow-copied.
    /// </summary>
    [Fact]
    public void A_deep_clone_shares_streams_rather_than_rebuilding_them_as_dictionaries()
    {
        var stream = new PdfStream();
        stream.RawData = new byte[] { 1, 2, 3 };
        var source = new PdfDictionary();
        source.Set(PdfName.Contents, stream);

        var clone = PdfDictionary.DeepClone(source);

        Assert.Same(stream, clone[PdfName.Contents]);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((PdfStream)clone[PdfName.Contents]).RawData);
    }

    /// <summary>
    /// A self-referential dictionary must terminate at the depth limit rather than overflow the
    /// stack — the adversarial-input test this repository requires for any recursion guard.
    /// </summary>
    [Fact]
    public void Adversarial_SelfReferentialDictionary_DeepClonesWithoutOverflowing()
    {
        var cycle = new PdfDictionary();
        cycle.Set(PdfName.Parent, cycle);

        var clone = PdfDictionary.DeepClone(cycle, maxDepth: 8);

        Assert.NotSame(cycle, clone);
        Assert.NotNull(clone[PdfName.Parent]);
    }

    /// <summary>
    /// The behaviour that must survive consolidation: a clone is <b>shallow</b>. Values are shared
    /// by reference and only the container is new — anything else would silently deep-copy object
    /// graphs that the writer expects to be shared.
    /// </summary>
    [Fact]
    public void A_clone_is_shallow_and_shares_its_values_by_reference()
    {
        var inner = new PdfArray();
        var source = new PdfDictionary();
        source.Set(PdfName.Kids, inner);

        foreach (var clone in new[] { PdfHelpers.CloneDictionary(source),
                                      PdfPageContentAppender.CloneDictionary(source) })
        {
            Assert.NotSame(source, clone);
            Assert.Same(inner, clone[PdfName.Kids]);
        }
    }

    // ==================================================================
    //  Cluster 3 — stream seek normalisation (9 copies + a third contract)
    // ==================================================================

    /// <summary>
    /// <b>Pins a genuine contract difference</b>, not a duplicate — this pair must NOT be merged.
    /// <see cref="PdfHelpers.EnsureSeekable"/> rewinds and returns the caller's own stream, so
    /// the caller can keep using it; <c>PdfMetadataWriter.EnsureSeekable</c> always returns a
    /// fresh <see cref="MemoryStream"/>, because the incremental-update writer reads the source
    /// bytes more than once and must not be reading a stream the caller can advance underneath it.
    /// The colliding <i>name</i> is what makes them look mergeable.
    /// </summary>
    [Fact]
    public void The_two_seek_helpers_have_different_contracts_for_a_seekable_stream()
    {
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4 }) { Position = 2 };

        Stream sameInstance = PdfHelpers.EnsureSeekable(source);
        Assert.Same(source, sameInstance);
        Assert.Equal(0, sameInstance.Position);

        source.Position = 2;
        Stream copy = PdfMetadataWriter.EnsureSeekable(source);
        Assert.IsType<MemoryStream>(copy);
        Assert.Equal(0, copy.Position);
        Assert.Equal(4, copy.Length);
    }

    /// <summary>A non-seekable source is read exactly once, and fully, by the shared helper.</summary>
    [Fact]
    public void A_non_seekable_stream_is_buffered_once_and_completely()
    {
        var source = new CountingForwardOnlyStream(new byte[] { 9, 8, 7, 6, 5 });

        Stream buffered = PdfHelpers.EnsureSeekable(source);

        Assert.NotSame(source, buffered);
        Assert.True(buffered.CanSeek);
        Assert.Equal(0, buffered.Position);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, ((MemoryStream)buffered).ToArray());
        Assert.Equal(1, source.FullReadsServed);
    }

    /// <summary>A forward-only stream that records how many times it served its whole payload.</summary>
    private sealed class CountingForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public CountingForwardOnlyStream(byte[] data) => _data = data;

        public int FullReadsServed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _position);
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            if (n == 0) return 0;
            if (_position == _data.Length) FullReadsServed++;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ==================================================================
    //  Cluster 4 — image magic-byte sniffing (6 copies that disagree)
    // ==================================================================

    /// <summary>
    /// The Core inspector is the strictest of the six, and its thresholds are what the five
    /// scattered copies are expected to adopt: the <b>full four-byte</b> PNG signature.
    /// </summary>
    [Fact]
    public void The_core_inspector_reads_a_png_header()
    {
        Assert.True(RasterImageInspector.TryInspect(MinimalPngHeader(), out var info));
        Assert.Equal(RasterImageFormat.Png, info.Format);
        Assert.Equal(4, info.Width);
        Assert.Equal(3, info.Height);
    }

    /// <summary>
    /// <b>This is the divergence, made observable.</b> Corrupting the <i>third</i> signature byte
    /// leaves the first two intact. The Core inspector checks four bytes and rejects it; the copy
    /// at the SVG renderer's image path checks only two and would accept it, then hand the bytes
    /// to a decoder that cannot read them. That is the "one path fixed, the other silently not"
    /// shape, and consolidating the six sniffers is expected to remove it.
    /// </summary>
    [Fact]
    public void A_png_signature_corrupted_past_its_second_byte_is_rejected()
    {
        byte[] png = MinimalPngHeader();
        png[2] = 0x00;   // 'N' of "PNG" — invisible to any two-byte check
        Assert.False(RasterImageInspector.TryInspect(png, out _));
    }

    /// <summary>
    /// <b>The divergence, now closed.</b> The SVG renderer tested <c>89 'P'</c> — two bytes —
    /// while the raster renderer tested all four. Both now call the same predicate, so a header
    /// that is right for two bytes and wrong for the third is rejected identically by each. They
    /// used to agree by accident; they agree by construction.
    /// </summary>
    [Fact]
    public void Both_renderers_use_one_predicate_for_a_png_header()
    {
        byte[] good = MinimalPngHeader();
        byte[] truncated = MinimalPngHeader();
        truncated[2] = 0x00;                     // invisible to any two-byte check

        Assert.True(RasterImageInspector.IsPng(good));
        Assert.False(RasterImageInspector.IsPng(truncated));
    }

    /// <summary>
    /// A JPEG's SOI is always followed by a marker. Five of the six copies checked only
    /// <c>FF D8</c>, which accepts a two-byte prefix of anything.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, true)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x00 }, false)]
    [InlineData(new byte[] { 0xFF, 0xD8 }, false)]
    public void The_jpeg_predicate_requires_a_marker_after_the_start_of_image(byte[] header, bool expected)
        => Assert.Equal(expected, RasterImageInspector.IsJpeg(header));

    [Fact]
    public void An_unrecognised_or_truncated_header_is_declined_rather_than_guessed()
    {
        Assert.False(RasterImageInspector.TryInspect(new byte[64], out _));
        Assert.False(RasterImageInspector.TryInspect(Array.Empty<byte>(), out _));
        Assert.False(RasterImageInspector.TryInspect(null!, out _));
    }

    // ==================================================================
    //  Cluster 5 — PDF date formatting (2 copies, one lying about UTC)
    // ==================================================================

    /// <summary>
    /// <b>Pins a fix.</b> A PDF date ending in <c>Z</c> claims UTC (§7.9.4). One of the two
    /// formatters converted before stamping it; the other formatted whatever
    /// <see cref="DateTimeKind"/> it was handed and appended <c>Z</c> anyway, so on any host not
    /// already on UTC a signature carried a local time labelled as universal — a discrepancy a
    /// verifier cannot detect, because the string is well-formed either way.
    /// </summary>
    [NonUtcHostFact]
    public void A_local_time_is_converted_to_utc_rather_than_relabelled()
    {
        // A fixed instant, expressed as local time. Whatever this machine's offset is, the
        // formatted result must be the UTC rendering of the same instant.
        var utc = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc);
        DateTime local = utc.ToLocalTime();

        Assert.Equal("D:20260314150926Z", PdfDate.FormatUtc(utc));
        Assert.Equal("D:20260314150926Z", PdfDate.FormatUtc(local));
    }

    /// <summary>
    /// The failure mode in isolation: a <see cref="DateTimeKind.Unspecified"/> value must not be
    /// stamped verbatim. This is the one that made the defect invisible on a UTC build agent.
    /// </summary>
    [NonUtcHostFact]
    public void An_unspecified_kind_is_treated_as_local_and_converted()
    {
        var unspecified = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Unspecified);
        string formatted = PdfDate.FormatUtc(unspecified);

        Assert.StartsWith("D:", formatted, StringComparison.Ordinal);
        Assert.EndsWith("Z", formatted, StringComparison.Ordinal);
        string expected = "D:" + TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZoneInfo.Local)
            .ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "Z";
        Assert.Equal(expected, formatted);
    }

    /// <summary>
    /// A structurally valid 4x3 RGB PNG header: the 8-byte signature followed by the IHDR chunk.
    /// The inspector reads headers rather than decoding, so no pixel data and no valid CRC are
    /// needed — and building it by hand keeps the fixture independent of any encoder under test.
    /// </summary>
    private static byte[] MinimalPngHeader()
    {
        var b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[11] = 13;                                    // IHDR chunk length
        System.Text.Encoding.ASCII.GetBytes("IHDR").CopyTo(b, 12);
        b[19] = 4;                                     // width  (big-endian)
        b[23] = 3;                                     // height (big-endian)
        b[24] = 8;                                     // bit depth
        b[25] = 2;                                     // colour type: truecolour
        return b;                                      // compression/filter/interlace = 0
    }
}
