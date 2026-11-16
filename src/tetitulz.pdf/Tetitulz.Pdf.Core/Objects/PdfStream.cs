// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfStream : PdfDictionary
    {
        private byte[] _rawData;
        private byte[] _decodedData;

        /// <summary>
        /// The document's shared ceiling on retained decoded bytes, set by the parser on every stream
        /// it builds; null for a stream built outside the parser (a writer's), which always caches.
        /// Past the ceiling a filtered decode is handed back without being memoized, so a
        /// whole-document pass does not hold the sum of every stream's decoded content (F3).
        /// </summary>
        internal Util.DecodeMemoBudget MemoBudget { get; set; }

        public byte[] RawData
        {
            get => _rawData;
            set
            {
                _rawData = value;
                _decodedData = null; // invalidate cache
            }
        }

        public long StreamOffset { get; set; }

        public byte[] GetDecodedData() => GetDecodedDataUnder(null);

        /// <summary>
        /// Whether this stream holds its decoded bytes: a caller keeping them beside the stream then
        /// retains nothing new, and one keeping them when this is false is their only owner.
        /// </summary>
        internal bool IsDecodedDataMemoized => _decodedData != null;

        /// <summary>
        /// <see cref="GetDecodedData"/> with each filter stage handed <paramref name="work"/> as
        /// <see cref="StreamFilter.Work"/> (the parser's budget for its own streams); null is the
        /// parameterless form. The decoded bytes are cached the same way.
        /// </summary>
        internal byte[] GetDecodedDataUnder(Util.WorkBudget work)
        {
            if (_decodedData != null) return _decodedData;
            if (_rawData == null) return Array.Empty<byte>();

            var filterObj = this[PdfName.Filter];
            if (filterObj == null)
            {
                _decodedData = _rawData;
                return _decodedData;
            }

            var decodeParms = this[PdfName.DecodeParms];
            byte[] decoded = FilterPipeline.Decode(_rawData, filterObj, decodeParms, Constants.PdfLimits.MaxDecompressedBytes, work);
            // Memoize only while the document is under its ceiling; past it, hand the decode back
            // uncached so a whole-document pass does not retain the sum of every stream's content
            // (F3). An unbudgeted stream (a writer's) always caches, as before.
            if (MemoBudget == null || MemoBudget.TryRetain(decoded.LongLength)) _decodedData = decoded;
            return decoded;
        }

        /// <summary>
        /// Decodes this stream, refusing rather than allocating once the output passes
        /// <paramref name="maxBytes"/>. Returns <c>false</c> — with <paramref name="reason"/> set —
        /// when the ceiling was hit or the stream would not decode.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> A caller with a size limit of its own could otherwise only
        /// enforce it <i>after</i> the fact: a filtered stream that declares no <c>/DL</c>
        /// cannot be sized without inflating it, so a 4 MiB ceiling still paid for up to Core's
        /// 512 MiB. The accessibility tagger and the PDF/X validator both describe this in
        /// GAPS.md §5c and §12c as "a bounded transient, not an unbounded one" — this makes the
        /// bound the caller's.</para>
        ///
        /// <para><b>A declared <c>/DL</c> is read first.</b> A producer that declares the decoded
        /// length (ISO 32000-2 Table 5) has said what the inflate would find, so a declaration over
        /// the ceiling is refused without inflating a byte, with a reason that names it. One at or
        /// under the ceiling changes nothing — it is a declaration, not a measurement, and the bound
        /// still applies during the inflate.</para>
        ///
        /// <para><b>A refusal yields no bytes, never a prefix.</b> Handing back what had been
        /// decoded so far would be indistinguishable from a short stream that decoded completely,
        /// which is the failure mode <c>ContentScanResult.LimitExceeded</c> exists to avoid.</para>
        ///
        /// <para><b>A refused decode is not cached</b>, so a later unbounded
        /// <see cref="GetDecodedData"/> still returns the whole stream. Only a complete decode
        /// populates the memo — and it populates the same one, so a bounded call that succeeds is
        /// not wasted work.</para>
        /// </remarks>
        public bool TryGetDecodedData(long maxBytes, out byte[] data, out string reason)
            => TryDecode(maxBytes, remember: true, out data, out reason);

        /// <summary>
        /// <see cref="TryGetDecodedData(long, out byte[], out string)"/> without populating the
        /// memo: the decoded bytes belong to the caller alone and are collectable once it lets go.
        /// </summary>
        /// <remarks>
        /// <para><b>For a whole-document pass that decodes many streams once each.</b> The memo
        /// keeps every decode alive as long as its stream, so a pass over a document's images held
        /// the sum of their samples — each up to the caller's ceiling — rather than the largest one.
        /// A memo already present is returned as it is: it costs nothing more to hand over.</para>
        /// </remarks>
        public bool TryGetDecodedDataUncached(long maxBytes, out byte[] data, out string reason)
            => TryDecode(maxBytes, remember: false, out data, out reason);

        private bool TryDecode(long maxBytes, bool remember, out byte[] data, out string reason)
        {
            reason = null;

            if (_decodedData != null)
            {
                // Already decoded: the ceiling is about work not yet done, and there is none.
                data = _decodedData;
                return true;
            }

            if (_rawData == null) { data = Array.Empty<byte>(); return true; }

            var filterObj = this[PdfName.Filter];
            if (filterObj == null)
            {
                // Unfiltered: the decoded length *is* the raw length, knowable for free.
                if (_rawData.LongLength > maxBytes)
                {
                    data = null;
                    reason = $"the stream is {_rawData.LongLength} bytes and the limit is {maxBytes}; " +
                             "it is unfiltered, so this was known without decoding it";
                    return false;
                }
                if (remember) _decodedData = _rawData;
                data = _rawData;
                return true;
            }

            // A ceiling of nothing is a budget spent, the usual way to reach zero here; the pipeline
            // reads a number below one as "its own default", which would be the largest decode of all.
            if (maxBytes <= 0 && _rawData.Length > 0)
            {
                data = null;
                reason = $"the limit is {maxBytes} bytes, so nothing may be decoded";
                return false;
            }

            // ISO 32000-2 Table 5: /DL is the decoded length as the producer declared it. Over the
            // ceiling it settles the refusal before a byte is inflated — the declaration is exactly
            // the information the refusal needs. At or under the ceiling it settles nothing: it is
            // a declaration, not a measurement, so the bound stays on the inflate below and a /DL
            // that lies low buys no allocation past the limit. A /DL that is not a direct number
            // (an indirect reference, which a stream cannot resolve on its own) says nothing.
            if (this[PdfName.DL] is PdfNumber declared && declared.DoubleValue > maxBytes)
            {
                data = null;
                reason = $"the stream declares /DL {declared.DoubleValue.ToString(CultureInfo.InvariantCulture)} " +
                         $"bytes and the limit is {maxBytes}; it was refused on that declaration " +
                         "without decoding it";
                return false;
            }

            try
            {
                byte[] decoded = FilterPipeline.Decode(
                    _rawData, filterObj, this[PdfName.DecodeParms], maxBytes);
                // As in GetDecodedDataUnder: retain only under the document's memo ceiling (F3).
                if (remember && (MemoBudget == null || MemoBudget.TryRetain(decoded.LongLength))) _decodedData = decoded;
                data = decoded;
                return true;
            }
            catch (Exception ex)
            {
                data = null;
                reason = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// <see cref="TryGetDecodedData(long, out byte[], out string)"/> without the reason.
        /// </summary>
        public bool TryGetDecodedData(long maxBytes, out byte[] data)
            => TryGetDecodedData(maxBytes, out data, out _);

        public override string ToString()
        {
            var typeName = GetTypeName();
            int len = _rawData?.Length ?? 0;
            return typeName != null
                ? $"<< /Type /{typeName.Value} stream({len} bytes) >>"
                : $"<< stream({len} bytes) >>";
        }
    }
}
