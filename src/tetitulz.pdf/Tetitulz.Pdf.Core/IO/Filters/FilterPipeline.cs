// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// A filter chain refused: a filter refused its data as corrupt or past a ceiling, the chain or
    /// its cumulative output is past its limit, or <see cref="FilterPipeline.StrictUnknownFilters"/>
    /// is set and a <c>/Filter</c> entry names a filter the pipeline does not recognise.
    /// </summary>
    public sealed class PdfFilterException : PdfException
    {
        public PdfFilterException(string message) : base(message) { }
    }

    public static class FilterPipeline
    {
        // [ThreadStatic] so concurrent decodes on a server host cannot read each other's
        // diagnostic (see the Diagnostics Pattern section of CLAUDE.md).
        [System.ThreadStatic]
        private static string _lastUnknownFilterName;

        /// <summary>
        /// Name of the most recently encountered <c>/Filter</c> that the
        /// pipeline did not recognise <b>on the calling thread</b>, or <c>null</c>
        /// when the last decode on this thread used only known filters.  Pre-fix,
        /// unknown filters silently returned an empty byte array; this property lets
        /// callers detect (and log) the missing decoder before downstream consumers
        /// see garbled output.
        /// </summary>
        /// <remarks>
        /// <para>Backed by a <c>[ThreadStatic]</c> field: read it on the same thread that ran
        /// the decode.</para>
        /// <para>Cleared at the <b>start</b> of every <see cref="Decode(byte[], PdfObject, PdfObject, long)"/>
        /// call, before the no-filter early return, so a value read after a decode always belongs
        /// to that decode, never the *previous* stream's filter name: per-thread storage is not
        /// per-decode scope.</para>
        /// </remarks>
        public static string LastUnknownFilterName
        {
            get => _lastUnknownFilterName;
            private set => _lastUnknownFilterName = value;
        }

        /// <summary>Resets <see cref="LastUnknownFilterName"/> to null (for use in tests).</summary>
        public static void ResetLastUnknownFilterName() => LastUnknownFilterName = null;

        /// <summary>
        /// When <c>true</c>, an unrecognised <c>/Filter</c> name throws
        /// <see cref="PdfFilterException"/> instead of passing the encoded
        /// bytes through unchanged.  Default <c>false</c> preserves lenient
        /// lenient behaviour.
        /// </summary>
        public static bool StrictUnknownFilters { get; set; }

        // The cumulative ceiling across a chain's stages is PdfLimits.MaxCumulativeOutputBytes, read
        // where it is applied. A static field here copied it at type initialisation, so a host that
        // set the limit after the first stream was decoded — the ordinary order, since opening a
        // document decodes its cross-reference stream — was not honoured.

        // Keyed by the canonical long form AND the inline-image abbreviation (PDF 32000-1:2008,
        // Table 93), which are the same decoder under two spellings.
        private static readonly Dictionary<string, Func<StreamFilter>> FilterMap = new Dictionary<string, Func<StreamFilter>>
        {
            { PdfName.FlateDecode.Value,     () => new FlateDecodeFilter() },
            { PdfName.Fl.Value,              () => new FlateDecodeFilter() },
            { PdfName.LZWDecode.Value,       () => new LzwDecodeFilter() },
            { PdfName.LZW.Value,             () => new LzwDecodeFilter() },
            // The PDF Association's proposed PDF 2.0 filter; no inline-image abbreviation is defined.
            { PdfName.BrotliDecode.Value,    () => new BrotliDecodeFilter() },
            { PdfName.ASCII85Decode.Value,   () => new Ascii85DecodeFilter() },
            { PdfName.A85.Value,             () => new Ascii85DecodeFilter() },
            { PdfName.ASCIIHexDecode.Value,  () => new AsciiHexDecodeFilter() },
            { PdfName.AHx.Value,             () => new AsciiHexDecodeFilter() },
            // Image codecs — passthrough: raw bitstream returned for external decoders
            { PdfName.DCTDecode.Value,       () => new DctDecodeFilter() },
            { PdfName.DCT.Value,             () => new DctDecodeFilter() },
            { PdfName.RunLengthDecode.Value, () => new RunLengthDecodeFilter() },
            { PdfName.RL.Value,              () => new RunLengthDecodeFilter() },
            { PdfName.CCITTFaxDecode.Value,  () => new CcittFaxDecodeFilter() },
            { PdfName.CCF.Value,             () => new CcittFaxDecodeFilter() },
            { PdfName.JPXDecode.Value,       () => new JpxDecodeFilter() },
            { PdfName.JBIG2Decode.Value,     () => new Jbig2DecodeFilter() },
            // Per-stream crypt filter: Identity / named filters pass through.
            { PdfName.Crypt.Value,           () => new CryptDecodeFilter() },
        };

        public static byte[] Decode(byte[] data, PdfObject filterObj, PdfObject decodeParmsObj)
            => Decode(data, filterObj, decodeParmsObj, PdfLimits.MaxDecompressedBytes);

        /// <summary>
        /// Decodes with a caller-supplied per-stage output ceiling.
        /// </summary>
        /// <param name="maxOutputBytes">
        /// The most any single filter stage may produce. Passing
        /// <see cref="PdfLimits.MaxDecompressedBytes"/> is byte-for-byte the three-argument
        /// overload, which is what every existing caller gets.
        /// </param>
        /// <remarks>
        /// <para><b>What this is for.</b> A caller that will refuse anything over <i>n</i> bytes
        /// otherwise has to inflate the whole stream to discover its size — a filtered stream
        /// declaring no <c>/DL</c> cannot be measured any other way, because that is what a
        /// compression filter is. Threading the ceiling down makes the transient allocation the
        /// caller's number rather than Core's 512 MB one.</para>
        /// <para>The cumulative cap across stages is unchanged and still applies: this bounds each
        /// stage, that bounds the chain.</para>
        /// </remarks>
        /// <param name="data">The raw, still-encoded stream bytes.</param>
        /// <param name="filterObj">
        /// The stream's <c>/Filter</c> — a name or an array of them, applied in order.
        /// </param>
        /// <param name="decodeParmsObj">
        /// The matching <c>/DecodeParms</c>, positionally aligned with <paramref name="filterObj"/>.
        /// </param>
        public static byte[] Decode(byte[] data, PdfObject filterObj, PdfObject decodeParmsObj,
            long maxOutputBytes)
            => Decode(data, filterObj, decodeParmsObj, maxOutputBytes, null);

        /// <summary>
        /// <see cref="Decode(byte[], PdfObject, PdfObject, long)"/> under the caller's
        /// <paramref name="work"/> budget as well: each stage is handed it as
        /// <see cref="StreamFilter.Work"/>, and a filter whose own budget is sized by the stream's
        /// parameters (JBIG2) spends it too. Null is the four-argument overload.
        /// </summary>
        public static byte[] Decode(byte[] data, PdfObject filterObj, PdfObject decodeParmsObj,
            long maxOutputBytes, Util.WorkBudget work)
        {
            // Reset BEFORE the no-filter early return: a stream with no /Filter is a decode that
            // used only known filters, so it must not report the previous stream's unknown one.
            // This is the whole operation — FilterPipeline.Decode is never called re-entrantly
            // (no filter implementation calls back into it), so there is no outermost-call guard
            // to write here.
            LastUnknownFilterName = null;

            if (maxOutputBytes <= 0) maxOutputBytes = PdfLimits.MaxDecompressedBytes;
            if (filterObj == null) return WithinCeiling(data, maxOutputBytes, "the stream has no /Filter", decoded: false);
            if (!(filterObj is PdfName) && !(filterObj is PdfArray))
            {
                // /Filter is neither a name nor an array — an unresolved indirect reference, a
                // number, a string. Nothing can be applied, so the raw bytes pass through; but
                // they are still bytes the caller has to hold, so the ceiling still applies.
                return WithinCeiling(
                    data, maxOutputBytes,
                    $"/Filter is {filterObj.GetType().Name}, not a name or an array", decoded: false);
            }

            var filters = new List<string>();
            var paramsList = new List<PdfDictionary>();
            PairStages(filterObj, decodeParmsObj, filters, paramsList, (slot, strictMessage) =>
            {
                LastUnknownFilterName = slot;
                if (StrictUnknownFilters) throw new PdfFilterException(strictMessage);
            });

            // Cap the filter-chain length. Each expand filter (Flate/LZW/RunLength) caps its
            // own output at 512 MB, but a long /Filter array (e.g. 1000× FlateDecode, or
            // alternating expanders) multiplies CPU/GC work by the array length while every
            // per-stage cap still "passes" — a decompression-amplification DoS. No legitimate
            // PDF chains more than a few filters.
            int MaxFilterChainLength = PdfLimits.MaxFilterChainLength;
            if (filters.Count > MaxFilterChainLength)
                throw new PdfFilterException(
                    $"Filter chain length {filters.Count} exceeds the {MaxFilterChainLength}-filter limit.");

            long cumulativeBytes = 0L;
            byte[] result = DecodeStages(data, filters, paramsList, filters.Count, maxOutputBytes, work,
                ref cumulativeBytes, out bool applied);
            return WithinCeiling(result, maxOutputBytes, "the decoded stream", decoded: applied);
        }

        /// <summary>
        /// The stages <see cref="Decode(byte[], PdfObject, PdfObject, long, Util.WorkBudget)"/> would
        /// run for <paramref name="filterObj"/> and <paramref name="decodeParmsObj"/>, each name with
        /// the parameter dictionary it pairs with it — for a caller that runs some of them itself
        /// (<see cref="DecodeStages"/>) and decodes the rest its own way. <c>false</c> wherever
        /// Decode would report or refuse instead: a parameter dictionary or a <c>/Filter</c> element
        /// it cannot apply, a name it does not know, no stage at all, a chain past
        /// <see cref="PdfLimits.MaxFilterChainLength"/>.
        /// </summary>
        internal static bool TryGetStages(PdfObject filterObj, PdfObject decodeParmsObj,
            out List<string> filters, out List<PdfDictionary> parameters)
        {
            filters = new List<string>();
            parameters = new List<PdfDictionary>();
            if (!(filterObj is PdfName) && !(filterObj is PdfArray)) return false;
            bool clean = true;
            PairStages(filterObj, decodeParmsObj, filters, parameters, (_, _) => clean = false);
            if (!clean || filters.Count == 0 || filters.Count > PdfLimits.MaxFilterChainLength) return false;
            foreach (string name in filters)
                if (!FilterMap.ContainsKey(name)) return false;
            return true;
        }

        /// <summary>
        /// The first <paramref name="count"/> of the stages run in order, as
        /// <see cref="Decode(byte[], PdfObject, PdfObject, long, Util.WorkBudget)"/> runs them: each
        /// under <paramref name="maxOutputBytes"/> and <paramref name="work"/>, its output counted into
        /// <paramref name="cumulativeBytes"/> against <see cref="PdfLimits.MaxCumulativeOutputBytes"/>,
        /// an unknown name passed through and reported. <paramref name="applied"/> says whether any
        /// filter ran.
        /// </summary>
        internal static byte[] DecodeStages(byte[] data, List<string> filters, List<PdfDictionary> parameters, int count,
            long maxOutputBytes, Util.WorkBudget work, ref long cumulativeBytes, out bool applied)
        {
            // Cap cumulative output across all stages. Each per-stage filter caps at 512 MB,
            // but a 16-stage chain could accumulate up to 16 × 512 MB = 8 GB of intermediate
            // allocations before the last-stage cap fires. Tracking total bytes produced
            // across stages closes the multi-stage amplification window.
            byte[] result = data;
            applied = false;
            for (int i = 0; i < count; i++)
            {
                string filterName = filters[i];
                PdfDictionary parms = i < parameters.Count ? parameters[i] : null;

                if (FilterMap.TryGetValue(filterName, out var factory))
                {
                    var filter = factory();
                    filter.MaxOutputBytes = maxOutputBytes;
                    filter.Work = work;
                    try
                    {
                        result = filter.Decode(result, parms);
                    }
                    catch (System.IO.InvalidDataException ex)
                    {
                        // Several filters refuse corrupt data or a ceiling as InvalidDataException;
                        // every public door catches PdfException, so the refusal is one of those.
                        throw new PdfFilterException("/" + filterName + ": " + ex.Message);
                    }
                    applied = true;
                    cumulativeBytes += result.LongLength;
                    long cumulativeCeiling = PdfLimits.MaxCumulativeOutputBytes;
                    if (cumulativeBytes > cumulativeCeiling)
                        throw new PdfFilterException(
                            $"Cumulative filter output after stage {i + 1} ({cumulativeBytes / (1024 * 1024)} MB) " +
                            $"exceeds the {cumulativeCeiling / (1024 * 1024)}-MB limit.");
                }
                else
                {
                    // Unknown filter — surface the name to callers and choose
                    // the policy.  Pre-fix, the branch silently set
                    // `result = new byte[0]`, which was *worse* than no
                    // decoding because every byte was lost without diagnostic.
                    // Now we record the unknown name and pass the data through
                    // by default (lenient) so downstream
                    // consumers — image decoders, content-stream parsers — at
                    // least see the raw bytes.  Strict callers opt in via
                    // <see cref="StrictUnknownFilters"/>.
                    LastUnknownFilterName = filterName;
                    if (StrictUnknownFilters)
                    {
                        throw new PdfFilterException(
                            "Unknown stream filter '" + filterName + "'.");
                    }
                    // else: leave `result` unchanged (passthrough)
                }
            }
            return result;
        }

        /// <summary>
        /// A <c>/Filter</c> name or array and its <c>/DecodeParms</c> as stages: each name with the
        /// parameter dictionary paired with it (§7.3.8.2), and every stage or dictionary that cannot
        /// be applied handed to <paramref name="report"/> — the text for
        /// <see cref="LastUnknownFilterName"/>, then the strict mode's message.
        /// </summary>
        private static void PairStages(PdfObject filterObj, PdfObject decodeParmsObj, List<string> filters,
            List<PdfDictionary> paramsList, Action<string, string> report)
        {
            if (filterObj is PdfName singleFilter)
            {
                filters.Add(singleFilter.Value);
                paramsList.Add(decodeParmsObj as PdfDictionary);
            }
            else if (filterObj is PdfArray filterArray)
            {
                var parmsArray = decodeParmsObj as PdfArray;

                // §7.3.8.2: "If there is only one filter and that filter has parameters, DecodeParms
                // shall be set to the filter's parameter dictionary" — and a one-element /Filter
                // array is one filter. Only an array was paired with an array here, so
                // /Filter [/FlateDecode] /DecodeParms << /Predictor 12 … >> dropped the dictionary
                // without a report and decoded with no predictor: the PNG filter-type byte stayed
                // at the head of every row.
                var onlyFiltersParms = filterArray.Count == 1 ? decodeParmsObj as PdfDictionary : null;

                // Beside SEVERAL filters a lone dictionary names no stage — §7.3.8.2 requires an
                // array there — so it cannot be applied, and is reported rather than dropped without a
                // word; an empty one loses nothing and is
                // not reported.
                if (filterArray.Count > 1 && decodeParmsObj is PdfDictionary strayParms && strayParms.Count > 0)
                {
                    string stray = $"/DecodeParms is a dictionary but /Filter names {filterArray.Count} filters";
                    report(stray, stray + "; no stage's parameters can be applied.");
                }

                for (int i = 0; i < filterArray.Count; i++)
                {
                    if (filterArray[i] is PdfName name)
                    {
                        filters.Add(name.Value);
                        // The two lists are indexed together further down, so the parms entry
                        // must be added ONLY when a filter was. Adding it unconditionally meant a
                        // single non-name element (an unresolved indirect reference, a number)
                        // shifted every subsequent /DecodeParms by one — silently applying the
                        // wrong predictor to the wrong stage, which corrupts the decoded output
                        // rather than failing.
                        PdfDictionary parms = onlyFiltersParms;
                        if (parmsArray != null && i < parmsArray.Count)
                        {
                            var element = parmsArray[i];
                            parms = element as PdfDictionary;
                            // A /DecodeParms element written as an indirect reference — legal, and
                            // what `/DecodeParms [null 12 0 R]` looks like — cast to null, which is
                            // indistinguishable from "this stage has no parameters". The stage then
                            // decoded with predictor 1 and the PNG filter-type byte stayed at the
                            // head of every row, so the image rendered as diagonal smear with no
                            // Last* slot anywhere recording that a parameter dictionary was
                            // dropped. The adjacent /Filter branch already reports its equivalent.
                            if (parms == null && element != null && !(element is PdfNull))
                            {
                                report($"/DecodeParms[{i}] is {element.GetType().Name}, not a dictionary",
                                    $"/DecodeParms array element {i} is {element.GetType().Name}, " +
                                    "not a dictionary; the stage's predictor cannot be applied.");
                            }
                        }
                        paramsList.Add(parms);
                    }
                    else
                    {
                        // A /Filter element that is not a name cannot be applied. Report it the
                        // same way an unrecognised filter name is reported, so a dropped stage can
                        // never be mistaken for a successful decode.
                        string what = filterArray[i]?.GetType().Name ?? "null";
                        report(what, $"/Filter array element {i} is {what}, not a name; " +
                            "the stream cannot be decoded.");
                    }
                }
            }
        }

        /// <summary>
        /// Enforces the caller's ceiling on bytes about to be handed back, whether they were
        /// decoded or passed through.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the passthrough paths need this at all.</b> Every recognised filter is
        /// given <c>MaxOutputBytes</c> and caps itself, so for a known filter chain this is a
        /// no-op. But three paths return bytes <i>without</i> any filter running: a
        /// <c>/Filter</c> that is not a name or an array, an unrecognised filter <i>name</i>
        /// (which passes through by design, see <see cref="StrictUnknownFilters"/>), and no
        /// <c>/Filter</c> at all. On those, <c>maxOutputBytes</c> is honoured too — accepted and
        /// ignored, <c>PdfStream.TryGetDecodedData(4 MiB, …)</c> on a 500 MB stream whose
        /// <c>/Filter</c> is <c>/FooDecode</c> would return <b>true</b> and 500 MB.</para>
        ///
        /// <para><b>The tell was that the same library already refused the byte-identical
        /// case.</b> <c>PdfStream.TryGetDecodedData</c> checks the ceiling itself when
        /// <c>/Filter</c> is absent, because "the decoded length is the raw length, knowable for
        /// free" — which is equally true of a filter nobody can apply. One spelling of "hand back
        /// the raw bytes" was bounded and the other was not.</para>
        ///
        /// <para>Throwing rather than truncating matches every other guard here: a refusal yields
        /// no bytes, never a prefix, because a prefix is indistinguishable from a short stream
        /// that decoded completely.</para>
        ///
        /// <para><b>The message says whether a filter ran.</b> The last check also guards filters
        /// that do not cap themselves (ASCIIHexDecode), where "no filter was applied, so this was
        /// known without decoding" would be untrue.</para>
        /// </remarks>
        private static byte[] WithinCeiling(byte[] bytes, long maxOutputBytes, string what, bool decoded)
        {
            if (bytes != null && bytes.LongLength > maxOutputBytes)
                throw new PdfFilterException(
                    $"Output is {bytes.LongLength} bytes and the caller's limit is {maxOutputBytes} " +
                    $"({what}); " + (decoded
                        ? "the filter chain decoded it in full before the limit was checked."
                        : "no filter was applied, so this was known without decoding."));
            return bytes;
        }
    }
}
