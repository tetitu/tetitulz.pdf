// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Objects
{
    /// <summary>
    /// Determines how many bytes of sample data an inline image (<c>BI … ID … EI</c>) carries.
    ///
    /// <para>
    /// <b>Why this is worth getting exactly right.</b> The payload between <c>ID</c> and <c>EI</c>
    /// is arbitrary binary and routinely contains the byte pair "EI", so every parser falls back
    /// to a heuristic: find a whitespace-delimited <c>EI</c> that is followed by printable ASCII.
    /// That heuristic is provably wrong on real files — a <em>dark</em> grayscale or 1-bit image
    /// has no byte above 0x7F anywhere, and a line-wrapped ASCII85 payload is all-ASCII by
    /// construction, so either can contain a byte sequence satisfying every condition it tests.
    /// The image then ends early and the parser tokenises pixel data as operators: text extraction
    /// invents operators that were never in the document, and a content-stream editor splices at
    /// offsets that fall inside the image samples, corrupting what the page renders.
    /// </para>
    ///
    /// <para>
    /// Three cases need no heuristic at all, and this type covers all three: a declared <c>/L</c>;
    /// an <em>unfiltered</em> image, whose byte count follows from its geometry alone; and a
    /// <em>filtered</em> image whose outermost filter carries its own end-of-data marker
    /// (<c>/AHx</c>, <c>/A85</c>, <c>/RL</c>, <c>/DCT</c>), where the count is readable from the
    /// payload itself.
    /// </para>
    ///
    /// <para>
    /// Shared by <see cref="Tetitulz.Pdf.Core.Text.ContentStreamParser"/> and the accessibility
    /// feature's content-stream scanner so the two can never disagree about where an image ends —
    /// a disagreement is exactly what the editor's cross-check exists to detect, and it is useless
    /// if both sides implement the same mistake.
    /// </para>
    /// </summary>
    internal static class InlineImageLength
    {
        /// <summary>
        /// The payload length declared by <c>/L</c> (or its PDF 2.0 long form <c>/Length</c>), or
        /// -1 when the image declares none.
        /// </summary>
        public static int GetDeclared(PdfDictionary imageDict)
        {
            if (imageDict == null) return -1;

            PdfObject value = imageDict[PdfName.L] ?? imageDict[PdfName.Length];
            if (value is PdfNumber n)
            {
                double d = n.DoubleValue;
                if (d >= 0 && d <= int.MaxValue) return SaturatingCast.ToInt(d);
            }
            return -1;
        }

        /// <summary>
        /// Exact payload length of an <em>unfiltered</em> inline image, or -1 when it cannot be
        /// computed — a filter is present, a geometry key is missing or not a direct number, or
        /// the colour space is a named resource that only the page's <c>/Resources</c> could
        /// resolve.
        ///
        /// <para>
        /// Per ISO 32000-1 §8.9.5.2 the samples are packed rows, each padded to a byte boundary:
        /// <c>ceil(Width · components · BitsPerComponent / 8) · Height</c>.
        /// </para>
        /// </summary>
        public static int ComputeUnfiltered(PdfDictionary imageDict)
        {
            if (imageDict == null) return -1;

            // Any filter makes the ENCODED byte count independent of the geometry. The decoded
            // count still follows from it, which is what ComputeByDecoding uses.
            if (imageDict[PdfName.F] != null || imageDict[PdfName.Filter] != null) return -1;

            return ExpectedSampleBytes(imageDict);
        }

        /// <summary>
        /// Bytes the image's samples occupy once decoded:
        /// <c>ceil(Width · components · BitsPerComponent / 8) · Height</c> (§8.9.5.2), or -1 when
        /// the geometry is missing or unusable.
        /// </summary>
        /// <remarks>
        /// Shared by <see cref="ComputeUnfiltered"/>, where it is also the <em>encoded</em> length
        /// because there is no filter, and by <see cref="ComputeByDecoding"/>, where it is the
        /// yardstick a candidate payload has to decode to. One implementation, because the two are
        /// the same question about the same image and a second copy is where they would start
        /// answering it differently.
        /// </remarks>
        private static int ExpectedSampleBytes(PdfDictionary imageDict)
        {
            int width  = GetInt(imageDict, PdfName.W, PdfName.Width);
            int height = GetInt(imageDict, PdfName.H, PdfName.Height);
            if (width <= 0 || height <= 0) return -1;

            int bpc, components;

            // An image mask is 1 bit per sample by definition; /BPC is optional there and, when
            // present, must be 1 (ISO 32000-1 Table 91).
            if (GetBool(imageDict, PdfName.IM, PdfName.ImageMask) == true)
            {
                bpc = 1;
                components = 1;
            }
            else
            {
                bpc = GetInt(imageDict, PdfName.BPC, PdfName.BitsPerComponent);
                if (bpc != 1 && bpc != 2 && bpc != 4 && bpc != 8 && bpc != 16) return -1;

                components = ColourComponents(imageDict);
                if (components <= 0) return -1;
            }

            // Guard the arithmetic: a hostile /W and /H can multiply past int range.
            long rowBits  = (long)width * components * bpc;
            long rowBytes = (rowBits + 7) / 8;
            long total    = rowBytes * height;
            if (total < 0 || total > int.MaxValue) return -1;

            return (int)total;
        }

        /// <summary>
        /// Exact payload length of an inline image whose <em>outermost</em> filter carries its own
        /// end-of-data marker — <c>/AHx</c>, <c>/A85</c>, <c>/RL</c> or <c>/DCT</c> — or -1 when the
        /// filter is not one of those, the payload does not obey it, or the marker never arrives
        /// before the end of <paramref name="data"/>. The length <em>includes</em> the marker, which
        /// is part of the encoded stream.
        ///
        /// <para>
        /// This is the case <see cref="ComputeUnfiltered"/> cannot reach: the moment a filter is
        /// present the encoded byte count stops following from the geometry, which would send every
        /// filtered image without a declared <c>/L</c> to the heuristic. Four of the seven
        /// inline-image filters are nevertheless self-delimiting (ISO 32000-1 §7.4), and one of them
        /// is the case the heuristic is worst at: an ASCII85 payload is printable ASCII by
        /// construction, so a line-wrapped one can contain a whitespace-delimited <c>EI</c>
        /// satisfying every condition the heuristic tests.
        /// </para>
        ///
        /// <para>
        /// <c>/Fl</c>, <c>/LZW</c> and <c>/CCF</c> are deliberately absent. Flate and LZW do have end
        /// markers, but reaching one means running the decompressor — a decode, not a measurement,
        /// and a decompression bomb's worth of work for a boundary. CCITT's EOFB is <em>optional</em>
        /// (§7.4.6, <c>/EndOfBlock</c>), so a CCITT payload genuinely is not self-delimiting.
        /// </para>
        ///
        /// <para>
        /// The window is always "<paramref name="start"/> to the end of <paramref name="data"/>"
        /// rather than a caller-supplied length. Both call sites hand this the whole decoded content
        /// stream, and a window each computes for itself is one more thing they could compute
        /// differently — which is the disagreement this type exists to make impossible.
        /// </para>
        ///
        /// <para>
        /// <b>The answer is a candidate, not a verdict.</b> Both callers still verify that an
        /// <c>EI</c> really follows the payload before believing the length, because a marker found
        /// inside what is really a longer payload ends the image early — the exact corruption this
        /// whole subsystem exists to prevent. A length the data contradicts is worse than none.
        /// </para>
        ///
        /// <para>
        /// The scan is a single allocation-free pass bounded by <paramref name="data"/>, so it adds
        /// no ceiling of its own: the caller has already bounded the stream it hands over.
        /// </para>
        /// </summary>
        /// <param name="imageDict">The image's parameter dictionary, as parsed from <c>BI … ID</c>.</param>
        /// <param name="data">The decoded content stream the image sits in.</param>
        /// <param name="start">Index in <paramref name="data"/> of the payload's first byte.</param>
        public static int ComputeSelfDelimiting(PdfDictionary imageDict, byte[] data, int start)
        {
            if (imageDict == null || data == null) return -1;
            if (start < 0 || start >= data.Length) return -1;

            PdfName filter = OutermostFilter(imageDict);
            if (filter == null) return -1;

            if (IsFilter(filter, PdfName.AHx, PdfName.ASCIIHexDecode))  return ScanAsciiHexEnd(data, start);
            if (IsFilter(filter, PdfName.A85, PdfName.ASCII85Decode))   return ScanAscii85End(data, start);
            if (IsFilter(filter, PdfName.RL,  PdfName.RunLengthDecode)) return ScanRunLengthEnd(data, start);
            if (IsFilter(filter, PdfName.DCT, PdfName.DCTDecode))       return ScanJpegEnd(data, start);
            return -1;
        }

        /// <summary>
        /// Exact payload length of an inline image whose outermost filter is <c>/Fl</c> or
        /// <c>/LZW</c>, found by <b>decoding</b> candidate payloads through the whole filter chain
        /// and keeping the one that yields exactly the sample count the image's geometry requires —
        /// or -1 when nothing verifies.
        ///
        /// <para>
        /// This is the case the other three cannot reach. A declared <c>/L</c> settles it;
        /// <see cref="ComputeSelfDelimiting"/> handles the filters that carry an end-of-data marker;
        /// <see cref="ComputeUnfiltered"/> handles no filter at all. Flate and LZW have neither a
        /// marker nor a geometry-derived encoded size, so without this the only answer would be the
        /// "whitespace-delimited <c>EI</c> followed by printable ASCII" heuristic — which is
        /// provably wrong on real input and is why a page carrying one cannot be spliced.
        /// </para>
        ///
        /// <para>
        /// <b>It is a verification, not a scan, and that is what makes it exact.</b> The candidate
        /// ends come from the same weak heuristic; what is new is that each one is <em>checked</em>
        /// by decoding it and comparing against <c>ceil(W · components · BPC / 8) · H</c>. A
        /// candidate that ends the payload early truncates the compressed stream, so it decodes to
        /// fewer bytes than the geometry demands — or not at all. Guessing right by accident would
        /// need a truncation that is both valid Flate and exactly the right length.
        /// </para>
        ///
        /// <para>
        /// <b>Decode failures are the normal case here, not an error.</b> Most candidates are
        /// wrong, and a wrong one throws or returns the wrong size; both mean "keep looking".
        /// Nothing is reported, because a rejected candidate is not a fact about the document.
        /// </para>
        ///
        /// <para>
        /// <b>A chain of filters is measured the same way, by running all of it.</b> The sample count
        /// is not what the <i>outermost</i> decode produces, but it is what the <i>last</i> one
        /// produces, so running the chain answers the same question. The chain must
        /// be general-purpose throughout; see <see cref="TryReadVerifiableChain"/> for which filters
        /// qualify and why an image codec anywhere in it does not.
        /// </para>
        /// </summary>
        /// <param name="imageDict">The image's parameter dictionary, as parsed from <c>BI … ID</c>.</param>
        /// <param name="data">The decoded content stream the image sits in.</param>
        /// <param name="start">Index in <paramref name="data"/> of the payload's first byte.</param>
        public static int ComputeByDecoding(PdfDictionary imageDict, byte[] data, int start)
        {
            if (imageDict == null || data == null) return -1;
            if (start < 0 || start >= data.Length) return -1;

            var chain = new System.Collections.Generic.List<PdfName>();
            if (!TryReadVerifiableChain(imageDict, chain)) return -1;

            int expected = ExpectedSampleBytes(imageDict);
            if (expected <= 0) return -1;

            var parms = new System.Collections.Generic.List<PdfDictionary>();
            ReadDecodeParms(imageDict, chain.Count, parms);

            // The outermost stage says where it ends — a zlib stream's final block and trailer, an
            // LZW stream's EOD code — so one decode settles the candidate, and one more verifies it
            // through the whole chain. The EI scan below is for a payload whose end its data does
            // not state, and is bounded: each candidate is a decode, and a stream of images whose
            // payloads never verify would otherwise try every later EI for every image.
            int end = OutermostStageEnd(data, start, chain[0], parms.Count > 0 ? parms[0] : null, expected);
            if (end > start && end <= data.Length && TerminatorFollows(data, end))
            {
                int length = end - start;
                if (DecodesToExactly(data, start, length, chain, parms, expected)) return length;
            }

            int candidates = 0;
            for (int p = start; p + 1 < data.Length; p++)
            {
                if (data[p] != (byte)'E' || data[p + 1] != (byte)'I') continue;
                if (p == start || !PdfConstants.IsWhitespace(data[p - 1])) continue;
                if (p + 2 < data.Length && !PdfConstants.IsEndOfToken(data[p + 2])) continue;

                // The payload ends at the whitespace before EI, which is a delimiter rather than
                // part of the encoded data.
                int length = p - 1 - start;
                if (length <= 0) continue;

                if (++candidates > MaxDecodeCandidates) return -1;
                if (DecodesToExactly(data, start, length, chain, parms, expected)) return length;
            }
            return -1;
        }

        /// <summary>
        /// The most <c>EI</c> candidates one image's scan decodes when its outermost stage did not
        /// say where it ends. A bound on work, not a rule of the format: a payload that verifies at
        /// none of them is unmeasurable here and goes to the heuristic, which reports itself as such.
        /// </summary>
        internal const int MaxDecodeCandidates = 16;

        /// <summary>
        /// Candidate decodes <see cref="ComputeByDecoding"/> has run on this thread — the work a test
        /// measures, since the answer cannot show how many were tried.
        /// </summary>
        [System.ThreadStatic]
        internal static long CandidateDecodes;

        /// <summary>
        /// Where the outermost stage's data ends, read from the data itself: past a Flate stream's
        /// final block and trailer, past an LZW stream's EOD code. −1 when the data does not say, or
        /// the stage decodes past the ceiling a candidate is allowed.
        /// </summary>
        private static int OutermostStageEnd(byte[] data, int start, PdfName filter, PdfDictionary parms, int expected)
        {
            long ceiling = (long)expected * StageOutputSlack + MinimumStageCeiling;
            int count = data.Length - start;
            try
            {
                int end;
                if (IsFilter(filter, PdfName.Fl, PdfName.FlateDecode))
                    IO.Filters.FlateDecodeFilter.DecodeEmbedded(data, start, count, ceiling, out end);
                else
                {
                    int earlyChange = (parms?[PdfName.EarlyChange] as PdfNumber)?.IntValue ?? 1;
                    IO.Filters.LzwDecodeFilter.DecodeEmbedded(data, start, count, earlyChange, ceiling, out end);
                }
                return end;
            }
            catch (System.Exception)
            {
                return -1;
            }
        }

        /// <summary>
        /// Whether <c>EI</c>, at a token boundary, follows <paramref name="at"/> after any white
        /// space — the shape the parser takes an exact payload by.
        /// </summary>
        private static bool TerminatorFollows(byte[] data, int at)
        {
            int p = at;
            while (p < data.Length && PdfConstants.IsWhitespace(data[p])) p++;
            if (p + 1 >= data.Length || data[p] != (byte)'E' || data[p + 1] != (byte)'I') return false;
            return p + 2 >= data.Length || PdfConstants.IsEndOfToken(data[p + 2]);
        }

        /// <summary>
        /// Reads the image's filter chain into <paramref name="chain"/>, or answers
        /// <see langword="false"/> when decoding it could not settle the payload length.
        /// </summary>
        /// <remarks>
        /// <para><b>Two conditions, and each rules out a different way of being wrong.</b> The
        /// outermost filter must be <c>/Fl</c> or <c>/LZW</c>, because that is what makes a wrong
        /// candidate <i>fail</i>: truncating a compressed stream leaves it invalid or short, whereas
        /// truncating (say) a run-length payload usually still decodes to something. And every
        /// filter in the chain must be one whose output is bytes the next stage consumes — the five
        /// general-purpose ones — because the comparison is against the raster's byte count, which
        /// only the last stage of such a chain produces.</para>
        ///
        /// <para><b>An image codec anywhere in the chain disqualifies it.</b> <c>/DCT</c> and
        /// <c>/JPX</c> are passthrough stages here, so the "decoded" length would be the codestream's
        /// and would match nothing; <c>/CCF</c> does produce a raster, but a truncated CCITT payload
        /// can decode to a full-sized buffer of wrong rows rather than failing, which would let a
        /// wrong candidate verify. Turning "uncertain" into a confidently wrong boundary is worse
        /// than leaving it uncertain, and the heuristic path already reports itself as uncertain.</para>
        ///
        /// <para><b>This is a fast path, and a mutation confirmed it cannot be anything else.</b>
        /// Deleting the whitelist loop leaves every test green, because <see cref="MakeFilter"/> is
        /// the one place the rule lives and rejecting a chain here or failing to build a decoder
        /// there both end as -1. What the loop buys is that an unmeasurable chain costs nothing
        /// rather than a full scan of the content stream with a failed decode at every candidate —
        /// a cost, which no assertion about the answer can see.</para>
        /// </remarks>
        private static bool TryReadVerifiableChain(
            PdfDictionary imageDict, System.Collections.Generic.List<PdfName> chain)
        {
            PdfObject filter = imageDict[PdfName.F] ?? imageDict[PdfName.Filter];

            if (filter is PdfName single) chain.Add(single);
            else if (filter is PdfArray array)
            {
                foreach (PdfObject element in array)
                {
                    if (!(element is PdfName name)) return false;
                    chain.Add(name);
                }
            }
            else return false;

            if (chain.Count == 0) return false;

            if (!IsFilter(chain[0], PdfName.Fl, PdfName.FlateDecode) &&
                !IsFilter(chain[0], PdfName.LZW, PdfName.LZWDecode))
                return false;

            foreach (PdfName name in chain)
                if (MakeFilter(name) == null) return false;

            return true;
        }

        /// <summary>
        /// Reads <c>/DP</c> or <c>/DecodeParms</c> positionally, padding to
        /// <paramref name="stages"/> entries.
        /// </summary>
        /// <remarks>
        /// Positional alignment is the whole point: a <c>/Predictor</c> belongs to one stage, and an
        /// array shifted by one applies it to the wrong one — which produces plausible bytes of the
        /// wrong length, so it would show up here as "no candidate verifies" rather than as an error.
        /// </remarks>
        private static void ReadDecodeParms(
            PdfDictionary imageDict, int stages, System.Collections.Generic.List<PdfDictionary> into)
        {
            PdfObject parms = imageDict[PdfName.DP] ?? imageDict[PdfName.DecodeParms];

            if (parms is PdfArray array)
                for (int i = 0; i < array.Count; i++) into.Add(array[i] as PdfDictionary);
            else if (parms is PdfDictionary dict)
                into.Add(dict);

            while (into.Count < stages) into.Add(null);
        }

        /// <summary>
        /// A decoder for one of the five general-purpose filters, under either spelling, or
        /// <see langword="null"/> for anything else.
        /// </summary>
        private static IO.Filters.StreamFilter MakeFilter(PdfName name)
        {
            if (IsFilter(name, PdfName.Fl, PdfName.FlateDecode)) return new IO.Filters.FlateDecodeFilter();
            if (IsFilter(name, PdfName.LZW, PdfName.LZWDecode)) return new IO.Filters.LzwDecodeFilter();
            if (IsFilter(name, PdfName.AHx, PdfName.ASCIIHexDecode)) return new IO.Filters.AsciiHexDecodeFilter();
            if (IsFilter(name, PdfName.A85, PdfName.ASCII85Decode)) return new IO.Filters.Ascii85DecodeFilter();
            if (IsFilter(name, PdfName.RL, PdfName.RunLengthDecode)) return new IO.Filters.RunLengthDecodeFilter();
            return null;
        }

        /// <summary>
        /// Whether <paramref name="length"/> bytes from <paramref name="start"/> decode through the
        /// whole filter chain to exactly <paramref name="expected"/> bytes.
        /// </summary>
        /// <remarks>
        /// <para><b>Each stage is capped, because this runs once per candidate on attacker-supplied
        /// bytes.</b> An inline image with no <c>/L</c> can offer dozens of <c>EI</c>-shaped
        /// positions, and inflating every one of them under the 512 MB default would let a single
        /// Flate bomb buy dozens of half-gigabyte allocations from a scan that is only
        /// looking for a boundary. The ceiling is derived from the answer being sought rather than
        /// guessed: a candidate that decodes to more than the raster needs is already wrong, and the
        /// slack covers the one legitimate reason an <i>intermediate</i> stage is larger — an
        /// ASCIIHex stage carries two characters per byte.</para>
        ///
        /// <para>A <c>/Predictor</c> is passed through, because it changes what "decoded" means: a
        /// PNG predictor adds a filter-type byte per row, so the raw inflate is larger than the
        /// raster and only the de-filtered result matches the geometry.</para>
        /// </remarks>
        private static bool DecodesToExactly(
            byte[] data, int start, int length,
            System.Collections.Generic.List<PdfName> chain,
            System.Collections.Generic.List<PdfDictionary> parms, int expected)
        {
            CandidateDecodes++;
            var slice = new byte[length];
            System.Buffer.BlockCopy(data, start, slice, 0, length);

            long ceiling = (long)expected * StageOutputSlack + MinimumStageCeiling;

            try
            {
                byte[] current = slice;
                for (int i = 0; i < chain.Count; i++)
                {
                    // Never null: TryReadVerifiableChain refused the chain otherwise, and it is the
                    // only caller. MakeFilter is the one place that decides which filters qualify;
                    // re-testing its answer here would be a second decision point that can only
                    // ever agree, and a null would arrive as an exception the catch below already
                    // treats as "this candidate is wrong".
                    IO.Filters.StreamFilter filter = MakeFilter(chain[i]);

                    filter.MaxOutputBytes = ceiling;
                    current = filter.Decode(current, i < parms.Count ? parms[i] : null);
                    if (current == null) return false;
                }

                return current.Length == expected;
            }
            catch (System.Exception)
            {
                // A wrong candidate truncates the compressed stream. Throwing is one of the two
                // ways that shows up; the other is a wrong size, tested above. A candidate that
                // trips the ceiling throws too, and is wrong for the same reason.
                return false;
            }
        }

        /// <summary>
        /// How much larger than the raster a single decode stage may be before the candidate is
        /// refused.
        /// </summary>
        /// <remarks>
        /// Four, which covers the largest legitimate intermediate — an ASCIIHex stage is two
        /// characters per byte, and ASCII85 is five per four — with room for line breaks. This is a
        /// bound on work, not a specification rule, so it lives here rather than in
        /// <c>PdfLimits</c>: the only thing that can exceed it is a candidate that was already
        /// going to be rejected.
        /// </remarks>
        private const int StageOutputSlack = 4;

        /// <summary>Floor for the per-stage ceiling, so a tiny image is not bounded absurdly.</summary>
        private const int MinimumStageCeiling = 64 * 1024;

        /// <summary>
        /// The filter whose end-of-data marker terminates the bytes as they sit in the file, or
        /// <c>null</c> when the image is unfiltered or names its filter as something other than a
        /// direct name.
        ///
        /// <para>
        /// §7.4.1: a <c>/Filter</c> array lists the filters in the order they were <em>applied</em>,
        /// so decoding runs left to right and element <b>0</b> is the outermost encoding — the only
        /// one whose marker is visible in the stored bytes. Taking the last element instead reads
        /// the <em>innermost</em> filter, which describes bytes that do not exist until the outer
        /// one has been decoded, and bounds nothing.
        /// </para>
        /// </summary>
        private static PdfName OutermostFilter(PdfDictionary imageDict)
        {
            PdfObject filter = imageDict[PdfName.F] ?? imageDict[PdfName.Filter];
            if (filter is PdfName name) return name;
            if (filter is PdfArray array && array.Count > 0) return array[0] as PdfName;
            return null;
        }

        /// <summary>Matches a filter name against its inline abbreviation and its full form (Table 93).</summary>
        private static bool IsFilter(PdfName actual, PdfName abbreviation, PdfName full)
            => abbreviation.Equals(actual) || full.Equals(actual);

        /// <summary>
        /// Length of an <c>ASCIIHexDecode</c> payload, including its <c>&gt;</c> end-of-data marker
        /// (§7.4.2).
        ///
        /// <para>
        /// Anything that is neither a hex digit, whitespace nor the marker means the bytes are not
        /// an ASCIIHex payload at all — a mislabelled filter, or a scan that has already run past
        /// the image — so the length is refused rather than guessed at. That strictness is also what
        /// makes an early <c>&gt;</c> impossible: a legal payload cannot contain one before its end.
        /// </para>
        /// </summary>
        private static int ScanAsciiHexEnd(byte[] data, int start)
        {
            for (int i = start; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == GreaterThan) return i - start + 1;
                if (!PdfConstants.IsHexDigit(b) && !PdfConstants.IsWhitespace(b)) return -1;
            }
            return -1;
        }

        /// <summary>
        /// Length of an <c>ASCII85Decode</c> payload, including its <c>~&gt;</c> end-of-data marker
        /// (§7.4.3). Bytes outside the base-85 alphabet (<c>!</c>–<c>u</c>, plus <c>z</c> and
        /// whitespace) refuse the length for the same reason as in <see cref="ScanAsciiHexEnd"/>.
        ///
        /// <para>
        /// <c>z</c> (0x7A) sits <em>above</em> <c>u</c> and so needs its own test: §7.4.3 gives it to
        /// the shorthand for four zero bytes, which is common in real output — a run of transparent
        /// or black samples encodes as nothing else — so folding it into the range test would refuse
        /// exactly the payloads most worth measuring.
        /// </para>
        /// </summary>
        private static int ScanAscii85End(byte[] data, int start)
        {
            for (int i = start; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == Tilde)
                    return i + 1 < data.Length && data[i + 1] == GreaterThan ? i - start + 2 : -1;
                if (b == Ascii85Zero || PdfConstants.IsWhitespace(b)) continue;
                if (b < Ascii85Low || b > Ascii85High) return -1;
            }
            return -1;
        }

        /// <summary>
        /// Length of a <c>RunLengthDecode</c> payload, including its <c>128</c> end-of-data byte
        /// (§7.4.5): the filter's own walk over the runs (<see cref="IO.Filters.RunLengthDecodeFilter.FindEnd"/>),
        /// which the PostScript plugin's <c>currentfile /RunLengthDecode filter</c> source reads
        /// through as well — one reading of §7.4.5's control bytes for every embedded stream.
        /// </summary>
        /// <remarks>
        /// The runs are <em>walked</em> rather than searched for the byte 128, and the difference is
        /// the whole point: a literal run's data may contain 128 anywhere, so a search would end the
        /// image inside the samples. Each length byte says how far the next one is — 0–127 means
        /// <c>length + 1</c> literal bytes follow, 129–255 a single byte repeated — so the walk is
        /// strictly forwards and terminates.
        /// </remarks>
        private static int ScanRunLengthEnd(byte[] data, int start)
        {
            int end = IO.Filters.RunLengthDecodeFilter.FindEnd(data, start, data.Length - start);
            return end < 0 ? -1 : end - start;   // ran off the end with no EOD — a truncated payload bounds nothing
        }

        // The two ASCII85 alphabet bounds. Named because a bare byte in a loop reads as an
        // arbitrary threshold rather than as the spec's.
        private const byte Ascii85Low          = (byte)'!';
        private const byte Ascii85High         = (byte)'u';
        private const byte Ascii85Zero         = (byte)'z';   // §7.4.3 shorthand for four zero bytes
        private const byte GreaterThan         = (byte)'>';
        private const byte Tilde               = (byte)'~';

        /// <summary>
        /// Length of a <c>DCTDecode</c> payload: the JPEG codestream from <c>SOI</c> through its
        /// <c>EOI</c> (ISO/IEC 10918-1 §B.2, which §7.4.7 defers to) — the filter's own segment
        /// walk, <see cref="IO.Filters.DctDecodeFilter.FindEnd"/>, which the PostScript plugin's
        /// <c>currentfile /DCTDecode filter</c> source reads through as well. The walk's reasoning —
        /// why a search for <c>FF D9</c> ends the image inside a thumbnail — is documented there.
        /// </summary>
        private static int ScanJpegEnd(byte[] data, int start)
        {
            int end = IO.Filters.DctDecodeFilter.FindEnd(data, start, data.Length - start);
            return end < 0 ? -1 : end - start;
        }

        /// <summary>
        /// Number of colour components implied by <c>/CS</c>, or -1 when the colour space is not
        /// one of the abbreviations resolvable without the page's <c>/Resources</c>.
        /// </summary>
        private static int ColourComponents(PdfDictionary imageDict)
        {
            PdfObject cs = imageDict[PdfName.CS] ?? imageDict[PdfName.ColorSpace];

            // An /Indexed space stores one index per sample whatever its base space is.
            if (cs is PdfArray array)
            {
                string family = (array.Count > 0 ? array[0] as PdfName : null)?.Value;
                return family == PdfColorSpaceNames.IndexedAbbrev ||
                       family == PdfColorSpaceNames.Indexed ? 1 : -1;
            }

            // Deliberately NOT PdfColorSpaceNames.ComponentCount: that maps /Lab and /Separation
            // to a component count, but this branch only ever sees a *bare name*, where neither
            // is legal — and any space the page's /Resources would have to resolve must fall
            // through to -1 rather than be guessed at.
            switch ((cs as PdfName)?.Value)
            {
                case PdfColorSpaceNames.DeviceGrayAbbrev:
                case PdfColorSpaceNames.DeviceGray:
                case PdfColorSpaceNames.CalGray:
                    return PdfColorSpaceNames.GrayComponents;
                case PdfColorSpaceNames.DeviceRGBAbbrev:
                case PdfColorSpaceNames.DeviceRGB:
                case PdfColorSpaceNames.CalRGB:
                    return PdfColorSpaceNames.RgbComponents;
                case PdfColorSpaceNames.DeviceCMYKAbbrev:
                case PdfColorSpaceNames.DeviceCMYK:
                    return PdfColorSpaceNames.CmykComponents;
                default:
                    return -1;   // named resource, /Lab, /ICCBased, absent … not computable here
            }
        }

        private static int GetInt(PdfDictionary imageDict, PdfName abbreviation, PdfName full)
        {
            PdfObject value = imageDict[abbreviation] ?? imageDict[full];
            if (value is PdfNumber n)
            {
                double d = n.DoubleValue;
                if (d >= 0 && d <= int.MaxValue) return SaturatingCast.ToInt(d);
            }
            return -1;
        }

        private static bool? GetBool(PdfDictionary imageDict, PdfName abbreviation, PdfName full)
            => (imageDict[abbreviation] ?? imageDict[full]) is PdfBoolean b
                ? b.Value
                : (bool?)null;
    }
}
