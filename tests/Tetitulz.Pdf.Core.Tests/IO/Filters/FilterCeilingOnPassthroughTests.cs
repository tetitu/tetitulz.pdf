// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters
{
    /// <summary>
    /// <see cref="PdfStream.TryGetDecodedData(long, out byte[], out string)"/> promises to refuse
    /// "rather than allocating once the output passes <c>maxBytes</c>". Three paths hand bytes
    /// back with <b>no filter applied</b> — a <c>/Filter</c> that is not a name or an array, an
    /// unrecognised filter <i>name</i> (which passes through by design), and no <c>/Filter</c> at
    /// all — and the ceiling holds on all three.
    ///
    /// <para><c>PdfStream</c> checks the ceiling itself when <c>/Filter</c> is absent, because the
    /// decoded length is knowable for free. That is equally true of a filter nobody can apply, so
    /// every spelling of "hand back the raw bytes" is bounded. These tests pin all three to the same
    /// answer.</para>
    /// </summary>
    public class FilterCeilingOnPassthroughTests
    {
        private const int Payload = 4096;
        private const int Ceiling = 1024;

        private static byte[] Bytes(int n)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++) b[i] = (byte)(i & 0xFF);
            return b;
        }

        private static PdfStream StreamWith(PdfObject filter, int size)
        {
            var s = new PdfStream { RawData = Bytes(size) };
            if (filter != null) s.Set(PdfName.Filter, filter);
            return s;
        }

        [Fact]
        public void AnUnrecognisedFilterNameIsRefusedByTheCallersCeilingRatherThanPassedThrough()
        {
            var s = StreamWith(new PdfName("FooDecode"), Payload);

            bool ok = s.TryGetDecodedData(Ceiling, out byte[] data, out string reason);

            Assert.False(ok);
            Assert.Null(data);                       // a refusal yields no bytes, never a prefix
            Assert.Contains(Ceiling.ToString(), reason);
        }

        [Fact]
        public void AFilterThatIsNeitherNameNorArrayIsRefusedByTheCallersCeiling()
        {
            // What an unresolved indirect /Filter, or a malformed numeric one, looks like.
            var s = StreamWith(new PdfNumber(7), Payload);

            bool ok = s.TryGetDecodedData(Ceiling, out byte[] data, out string reason);

            Assert.False(ok);
            Assert.Null(data);
            Assert.Contains(Ceiling.ToString(), reason);
        }

        /// <summary>
        /// The refusal says whether a filter ran. ASCIIHexDecode does not cap itself, so the pipeline's
        /// final check is what refuses it — after decoding, which the old fixed wording ("no filter was
        /// applied, so this was known without decoding") denied. A passthrough still says the latter.
        /// </summary>
        [Fact]
        public void TheRefusalSaysWhetherTheFilterChainRan()
        {
            var hex = new PdfStream { RawData = Encoding.ASCII.GetBytes(new string('4', 2 * Payload) + ">") };
            hex.Set(PdfName.Filter, PdfName.ASCIIHexDecode);

            Assert.False(hex.TryGetDecodedData(Ceiling, out _, out string decodedReason));
            Assert.Contains("decoded it in full", decodedReason);
            Assert.DoesNotContain("no filter was applied", decodedReason);

            var unknown = StreamWith(new PdfName("FooDecode"), Payload);
            Assert.False(unknown.TryGetDecodedData(Ceiling, out _, out string passthroughReason));
            Assert.Contains("no filter was applied", passthroughReason);
        }

        /// <summary>
        /// The case that already worked, asserted alongside so the three stay in step. Without it
        /// nothing would notice if a future change bounded the two above and unbounded this one.
        /// </summary>
        [Fact]
        public void AnUnfilteredStreamIsStillRefusedByTheCallersCeiling()
        {
            var s = StreamWith(null, Payload);

            Assert.False(s.TryGetDecodedData(Ceiling, out byte[] data, out _));
            Assert.Null(data);
        }

        /// <summary>
        /// Stops the vacuous fix. Refusing <i>everything</i> would satisfy all three tests above,
        /// so this pins that a passthrough stream comfortably under the ceiling still succeeds —
        /// and returns its actual bytes, not an empty array.
        /// </summary>
        [Fact]
        public void AnUnrecognisedFilterUnderTheCeilingStillPassesItsBytesThrough()
        {
            var s = StreamWith(new PdfName("FooDecode"), Ceiling / 2);

            bool ok = s.TryGetDecodedData(Ceiling, out byte[] data, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(Ceiling / 2, data.Length);
            Assert.Equal(Bytes(Ceiling / 2), data);
        }

        /// <summary>
        /// A recognised filter must be unaffected: it already capped itself through
        /// <c>IStreamFilter.MaxOutputBytes</c>, and the new check must not have changed where that
        /// boundary sits.
        /// </summary>
        [Fact]
        public void ARecognisedFilterUnderTheCeilingIsUnaffected()
        {
            byte[] raw = Encoding.ASCII.GetBytes("48656C6C6F>");   // "Hello" in ASCIIHex
            var s = new PdfStream { RawData = raw };
            s.Set(PdfName.Filter, PdfName.ASCIIHexDecode);

            Assert.True(s.TryGetDecodedData(Ceiling, out byte[] data, out _));
            Assert.Equal("Hello", Encoding.ASCII.GetString(data));
        }
    }
}
