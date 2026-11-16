// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// A data source that reads from the program's own bytes — what <c>currentfile</c> pushes and
    /// what <c>filter</c> wraps.
    ///
    /// <para><b>It reads lazily, and that is the whole design.</b> At the moment <c>filter</c>
    /// executes, the operator that will consume the data — <c>image</c> — has not been read yet, so
    /// reading eagerly swallows it. The block begins where the scanner is when the <i>consumer</i>
    /// pulls, which is exactly what makes this work at all and why the eexec pattern (capture at
    /// scan time) does not transfer: eexec's bytes start immediately after its own token, a
    /// filter's do not.</para>
    ///
    /// <para><b>The innermost filter decides where the block ends.</b>
    /// <c>currentfile /ASCII85Decode filter /FlateDecode filter</c> applies base-85 to the file
    /// bytes first, so <c>~&gt;</c> terminates it. A binary innermost filter —
    /// <c>currentfile /FlateDecode filter</c>, how a binary EPS embeds its images — has no marker in
    /// the byte stream: its block ends where its own data says (the EOD byte, the EOD code, the
    /// final block and trailer, the EOI marker), which for the compressed ones is known only by
    /// decoding. Core's filters decode and report that end in one pass
    /// (<see cref="FlateDecodeFilter.DecodeEmbedded"/> and its siblings), and the scanner's cursor
    /// lands exactly on the byte after it — never estimated, because a wrong extent feeds the rest
    /// of the program into the image <i>and</i> loses it from the program.</para>
    /// </summary>
    internal sealed class PsFile
    {
        /// <summary>The name <c>SubFileDecode</c> is given in a filter chain.</summary>
        internal const string SubFileDecode = "SubFileDecode";

        /// <summary>The name <c>ReusableStreamDecode</c> is given in a filter chain.</summary>
        internal const string ReusableStreamDecode = "ReusableStreamDecode";

        private readonly PsScanner _scanner;
        private readonly string _filterName;
        private readonly PsFile _inner;
        private readonly PsDict _parameters;

        // A ReusableStreamDecode file holds its whole data (PLRM §3.8.4: read from its source when it is
        // made) in _decoded, read from _decodedAt and repositioned by resetfile and setfileposition.
        private readonly bool _reusable;
        private readonly bool _reusableJpeg;

        private PsFile(PsScanner scanner, string filterName, PsFile inner, PsDict parameters = null)
        {
            _scanner    = scanner;
            _filterName = filterName;
            _inner      = inner;
            _parameters = parameters;
            _chainDepth = inner == null ? 0 : inner._chainDepth + 1;
        }

        // How many filters this file reads through: PsLimits.MaxFilterChainDepth bounds it, since each
        // is a pass over the data and a walk of the chain.
        private readonly int _chainDepth;

        private PsFile(byte[] data, bool jpeg)
        {
            _reusable     = true;
            _reusableJpeg = jpeg;
            _decoded      = data;
        }

        /// <summary><c>currentfile</c> — the raw program bytes from the current position.</summary>
        public static PsFile FromScanner(PsScanner scanner) => new(scanner, null, null);

        /// <summary>
        /// <c>ReusableStreamDecode</c>'s file over <paramref name="data"/>, read from its start — a JPEG
        /// codestream when <paramref name="jpeg"/>, carried as an image's data as a <c>/DCTDecode</c>
        /// chain's is.
        /// </summary>
        public static PsFile Reusable(byte[] data, bool jpeg) => new(data, jpeg);

        // A filter's data source that is a procedure (PLRM §3.13.1): called each time the filter needs
        // data, its answer a string, an empty one the end. Only the image operators run it, through
        // the step loop (PsImageOps.Gather); every other reader is refused by name.
        private readonly PsArray _procedure;

        private PsFile(PsArray procedure) => _procedure = procedure;

        /// <summary>A filter's data source that is a procedure: <c>{ … } /FlateDecode filter</c>, as a print driver feeds an image.</summary>
        public static PsFile FromProcedure(PsArray procedure) => new(procedure);

        /// <summary>The procedure at the bottom of this file's chain, or null when the chain reads bytes.</summary>
        public PsArray ProcedureSource
        {
            get
            {
                var file = this;
                while (file._inner != null) file = file._inner;
                return file._procedure;
            }
        }

        /// <summary>
        /// The most bytes a filter decodes from a partial chunk's tail that a whole chunk would decode
        /// otherwise: an ASCII85 group cut short fills out to four bytes, ASCIIHex an odd digit to one.
        /// The run-length, LZW and Flate filters decode a cut stream to a true prefix or not at all.
        /// </summary>
        private const int TrialTailPaddingBytes = 4;

        /// <summary>
        /// The bytes a procedure-sourced chain passes for <paramref name="raw"/> — what the procedure
        /// answered, in order — every filter applied, innermost first; null when a stage cannot decode
        /// it. A <c>/DCTDecode</c> stage is carried, not decoded: the chain then answers the JPEG
        /// codestream through its EOI marker, the bytes after it dropped — a driver's procedure cycles
        /// through its strings and never ends, so the codestream's own end is the data's — and
        /// <paramref name="jpeg"/> is set; null until the EOI has arrived, and a stage after it is
        /// refused. <paramref name="noJpeg"/> says the data will never be one — it does not begin with
        /// the SOI marker, or a stage follows the JPEG's — so a procedure that never ends need not be
        /// run further. Reported only when <paramref name="report"/>: a trial over part of the data says nothing.
        /// </summary>
        public byte[] DecodeProcedureData(byte[] raw, PsInterpreter i, bool report, out bool jpeg, out bool noJpeg)
        {
            jpeg = false;
            noJpeg = false;
            byte[] data = raw;
            var chain = FilterChain();
            for (int k = 0; k < chain.Count; k++)
            {
                var (name, parameters) = chain[k];
                if (name == PdfFilterNames.DctDecode)
                {
                    if (k < chain.Count - 1)
                    {
                        noJpeg = true;
                        return report ? RefuseStageOverJpeg(chain[k + 1].Name) : null;
                    }
                    // The first two bytes are the data's own when the JPEG stage is the first (the
                    // procedure's raw bytes), when the data is whole, or when the data runs past any
                    // tail a filter below pads a trial's partial chunk with — ASCIIHex an odd digit's
                    // byte, ASCII85 a short group's bytes, at most TrialTailPaddingBytes: padding
                    // standing where the marker is looked for refused a good JPEG.
                    bool headIsTheData = k == 0 || report || data.Length >= 2 + TrialTailPaddingBytes;
                    if (headIsTheData && data.Length >= 2 && (data[0] != 0xFF || data[1] != 0xD8))
                    {
                        noJpeg = true;
                        if (report)
                            PostScriptDiagnostics.Report("PsFile",
                                "the data a procedure fed a /DCTDecode filter does not begin with a JPEG's SOI marker; the image was not drawn");
                        return null;
                    }
                    int end = DctDecodeFilter.FindEnd(data, 0, data.Length);
                    if (end < 0)
                    {
                        if (report)
                            PostScriptDiagnostics.Report("PsFile",
                                "a JPEG a procedure fed ended before its EOI marker; the image was not drawn");
                        return null;
                    }
                    jpeg = true;
                    if (end == data.Length) return data;
                    var codestream = new byte[end];
                    Array.Copy(data, codestream, end);
                    return codestream;
                }
                data = Decode(i, name, parameters, data, report);
                if (data == null) return null;
            }
            return data;
        }

        /// <summary>
        /// <c>filter</c> — this file, decoded by <paramref name="filterName"/> with its parameter
        /// dictionary, if it was given one; a chain past <see cref="PsLimits.MaxFilterChainDepth"/> is a
        /// <c>limitcheck</c>.
        /// </summary>
        public PsFile WithFilter(string filterName, PsDict parameters = null)
        {
            if (_chainDepth >= PsLimits.MaxFilterChainDepth)
                throw new PsErrorException(PsErrorKind.LimitCheck, "filter",
                    $"a file read through more than {PsLimits.MaxFilterChainDepth} filters (PsLimits.MaxFilterChainDepth)");
            return new(_scanner, filterName, this, parameters);
        }

        /// <summary>Whether this is a <c>ReusableStreamDecode</c> file, whose position <c>resetfile</c> and <c>setfileposition</c> move.</summary>
        public bool IsReusable => _reusable;

        /// <summary>A reusable file's position, from its start.</summary>
        public int Position => _decodedAt;

        /// <summary>A reusable file's length.</summary>
        public int Length => _decoded?.Length ?? 0;

        /// <summary>Moves a reusable file to <paramref name="position"/>; false outside its data.</summary>
        public bool Seek(long position)
        {
            if (!_reusable || position < 0 || position > _decoded.Length) return false;
            _decodedAt = (int)position;
            return true;
        }

        /// <summary>The reusable file at the bottom of this file's chain, or null when the chain reads the program's own bytes.</summary>
        private PsFile ReusableRoot()
        {
            var file = this;
            while (file._inner != null) file = file._inner;
            return file._reusable ? file : null;
        }

        /// <summary>Whether the file has data to read: the program's bytes, a reusable file's, or a procedure's (which only an image reads).</summary>
        private bool HasSource => _scanner != null || ReusableRoot() != null || ProcedureSource != null;

        /// <summary>
        /// A <c>/CCITTFaxDecode</c> filter's parameters as the /DecodeParms Core reads: <c>/K</c>,
        /// <c>/Columns</c>, <c>/Rows</c>, <c>/BlackIs1</c>, <c>/EncodedByteAlign</c>, <c>/EndOfBlock</c>,
        /// <c>/EndOfLine</c> — the keys PLRM Table 3.21 and ISO 32000's Table 11 share.
        /// </summary>
        private static Core.Objects.PdfDictionary CcittParameters(PsDict parameters)
        {
            var pdf = new Core.Objects.PdfDictionary();
            if (parameters == null) return pdf;
            foreach (var key in new[] { Core.Objects.PdfName.K, Core.Objects.PdfName.Columns, Core.Objects.PdfName.Rows })
                if (parameters.TryGetName(key.Value, out PsObject number) && number.IsNumber)
                    pdf[key] = new Core.Objects.PdfNumber(number.AsInt());
            foreach (var key in new[] { Core.Objects.PdfName.BlackIs1, Core.Objects.PdfName.EncodedByteAlign,
                                        Core.Objects.PdfName.EndOfBlock, Core.Objects.PdfName.EndOfLine })
                if (parameters.TryGetName(key.Value, out PsObject flag) && flag.Type == PsType.Boolean)
                    pdf[key] = flag.AsBool() ? Core.Objects.PdfBoolean.True : Core.Objects.PdfBoolean.False;
            return pdf;
        }

        // A filtered file read a piece at a time — `{ f buf readstring pop } image` — decodes its
        // whole block at the first read and serves the rest from it, the block's end being in the
        // data (see ReadInlineBlock); null until then.
        private byte[] _decoded;
        private int _decodedAt;
        private bool _decodeFailed;

        /// <summary>
        /// Up to <paramref name="count"/> bytes into <paramref name="target"/> — <c>readstring</c>
        /// (<paramref name="hex"/> false) or <c>readhexstring</c> — the number read, fewer at the end
        /// of the data. Unfiltered, the program's own bytes from the cursor; filtered, the decoded
        /// block, which a JPEG's codestream is not, so a <c>/DCTDecode</c> chain reads as empty.
        /// </summary>
        /// <remarks>
        /// A hexadecimal read skips whatever is no digit, so its work is the bytes it passed over, which
        /// the caller's charge for the bytes it stored cannot see: a reusable file of none is walked whole
        /// at each read, for nothing stored. They are charged here.
        /// </remarks>
        public int Read(PsInterpreter i, byte[] target, int offset, int count, bool hex)
        {
            if (!HasSource) return 0;
            if (_filterName == null && !_reusable)
            {
                if (!hex) return _scanner.ReadRaw(target, offset, count);
                int from = _scanner.Position;
                int read = _scanner.ReadHex(target, offset, count);
                i.Tick((_scanner.Position - from) / BytesPerStep);
                return read;
            }

            if (!FillDecoded(i)) return 0;
            if (hex)
            {
                int from = _decodedAt;
                int read = PsScanner.ReadHexFrom(_decoded, ref _decodedAt, target, offset, count);
                i.Tick((_decodedAt - from) / BytesPerStep);
                return read;
            }
            int take = Math.Min(count, _decoded.Length - _decodedAt);
            Array.Copy(_decoded, _decodedAt, target, offset, take);
            _decodedAt += take;
            return take;
        }

        /// <summary><c>readline</c>: as <see cref="PsScanner.ReadLine"/>, from the decoded block for a filtered file.</summary>
        public int ReadLine(PsInterpreter i, byte[] target, int offset, int count, out bool ended, out bool overflow)
        {
            ended = overflow = false;
            if (!HasSource) return 0;
            if (_filterName == null && !_reusable) return _scanner.ReadLine(target, offset, count, out ended, out overflow);

            if (!FillDecoded(i)) return 0;
            return PsScanner.ReadLineFrom(_decoded, ref _decodedAt, target, offset, count, out ended, out overflow);
        }

        /// <summary>
        /// <c>file token</c>: the next object the file holds — the program's own next token(s), a
        /// procedure whole, through a parser over the scanner; or one from a filtered file's decoded
        /// block — false at the end of the data.
        /// </summary>
        public bool ReadToken(PsInterpreter i, out PsObject value) => ReadToken(i, out value, out _);

        /// <summary>
        /// <see cref="ReadToken(PsInterpreter, out PsObject)"/>, with the bytes the scan read for the
        /// caller to charge.
        /// </summary>
        /// <remarks>
        /// A filtered file's decoded block is scanned in place, from where the file stands. Every
        /// token copied the whole rest of the block into a fresh array first, for one tick: a
        /// megabyte block read a token at a time was a megabyte copied a token.
        /// </remarks>
        public bool ReadToken(PsInterpreter i, out PsObject value, out int scanned)
        {
            PsDictStack dicts = i.Dicts;
            PsAllocationBudget alloc = i.Allocation;
            value = PsObject.Null;
            scanned = 0;
            if (!HasSource) return false;
            if (_filterName == null && !_reusable)
            {
                int from = _scanner.Position;
                bool read = new PsParser(_scanner, alloc) { Dicts = dicts }.TryNext(out value);
                scanned = _scanner.Position - from;
                return read;
            }

            if (!FillDecoded(i) || _decodedAt >= _decoded.Length) return false;
            if (_decodedScanner == null || !ReferenceEquals(_decodedScannerData, _decoded))
            {
                _decodedScanner = new PsScanner(_decoded, alloc);
                _decodedScannerData = _decoded;
            }
            _decodedScanner.SeekTo(_decodedAt);
            bool found = new PsParser(_decodedScanner, alloc) { Dicts = dicts }.TryNext(out value);
            scanned = _decodedScanner.Position - _decodedAt;
            _decodedAt = _decodedScanner.Position;
            return found;
        }

        // One scanner over the decoded block, sought to the file's position for each token.
        private PsScanner _decodedScanner;
        private byte[] _decodedScannerData;

        private bool FillDecoded(PsInterpreter i)
        {
            if (_decoded != null && _reusableJpeg)
            {
                PostScriptDiagnostics.Report("PsFile",
                    "a reusable file holding a JPEG was read with readstring, readhexstring, readline or token; the samples " +
                    "of a JPEG are not decoded by this converter, so the read came back empty");
                return false;
            }
            if (_decoded != null) return true;
            if (_decodeFailed) return false;
            byte[] block = ReadInlineBlock(i, out bool jpeg);
            if (block == null || jpeg)
            {
                if (jpeg)
                    PostScriptDiagnostics.Report("PsFile",
                        "a /DCTDecode file was read with readstring or readhexstring; the samples of a JPEG are " +
                        "not decoded by this converter, so the read came back empty");
                _decodeFailed = true;
                return false;
            }
            _decoded = block;
            return true;
        }

        /// <summary>
        /// Whether <see cref="ReadInlineBlock"/> will answer with a JPEG codestream — a stage of the
        /// chain is <c>/DCTDecode</c> — for a caller that chooses how to carry the image before it reads.
        /// </summary>
        public bool CarriesJpeg
            => ReusableRoot() is { _reusableJpeg: true } || FilterChain().Exists(stage => stage.Name == PdfFilterNames.DctDecode);

        /// <summary>Every filter in the chain with its parameter dictionary, innermost first — above a reusable file, which is the chain's source.</summary>
        private System.Collections.Generic.List<(string Name, PsDict Parameters)> FilterChain()
        {
            var chain = new System.Collections.Generic.List<(string, PsDict)>();
            for (var file = this; file != null && !file._reusable; file = file._inner)
                if (file._filterName != null) chain.Insert(0, (file._filterName, file._parameters));
            return chain;
        }

        /// <summary>
        /// Reads and decodes the inline block, advancing the scanner exactly past it. Returns null
        /// when the chain cannot be terminated or decoded, having reported why — and having consumed
        /// the block, or as much of the program as the scan may reach, so that image data is never
        /// tokenised as PostScript.
        /// </summary>
        /// <param name="i">
        /// The interpreter whose budgets pay. Each stage decodes under the allocation's remaining
        /// headroom — Core's filters otherwise default to <c>PdfLimits.MaxDecompressedBytes</c> (512 MB),
        /// eight times this plugin's whole budget — and what it produced is charged here, the one place
        /// every reader comes through, so no caller charges a block again: a block held by a file, and
        /// one a loop decodes afresh from a reusable source for one step's charge, are both this
        /// budget's to refuse. A stage's output is charged too, though only the last survives: a large
        /// stage narrowed by the next is work all the same.
        /// </param>
        /// <param name="jpeg">
        /// True when the bytes returned are a JPEG codestream rather than samples: the chain ends in
        /// <c>/DCTDecode</c>, which is carried into the image as its <c>/Filter</c> instead of being
        /// decoded here — exactly as a PDF writer carries a JPEG. Any stage after it is refused.
        /// </param>
        public byte[] ReadInlineBlock(PsInterpreter i, out bool jpeg)
        {
            jpeg = false;
            long maxOutputBytes = i.Allocation.Remaining;
            if (ProcedureSource != null)
            {
                PostScriptDiagnostics.ReportUnimplemented("a filter whose data source is a procedure, read other than by an image operator");
                return null;
            }
            if (!HasSource) return null;

            // Already decoded by a read, or a reusable file: what the reads have not taken is the rest
            // of the block.
            if (_decoded != null)
            {
                jpeg = _reusableJpeg;
                if (!Charge(i, _decoded.Length - _decodedAt)) return null;
                var rest = new byte[_decoded.Length - _decodedAt];
                Array.Copy(_decoded, _decodedAt, rest, 0, rest.Length);
                _decodedAt = _decoded.Length;
                return rest;
            }

            var chain = FilterChain();
            PsFile reusable = ReusableRoot();
            if (chain.Count == 0 && reusable == null)
            {
                PostScriptDiagnostics.ReportUnimplemented("currentfile (unfiltered data source)");
                return null;
            }

            // The block is the file's whole data, taken or refused: a filter at its end of data stays
            // there (PLRM §3.8.4), so a later read — the discard of a shading or image refused after it
            // read, a second image on the same file — finds the end. It searched the program after the
            // block instead, and a JPEG's EOI search took the rest of the program with it.
            _decoded = Array.Empty<byte>();
            _decodedAt = 0;

            byte[] bytes;
            int next = 0;
            if (reusable != null)
            {
                bytes = reusable.ReadInlineBlock(i, out jpeg);
                if (bytes == null) return null;
            }
            else
            {
                if (!TryTakeInnermost(chain[0].Name, chain[0].Parameters, maxOutputBytes, out bytes, out next, out jpeg))
                    return null;
                if (next == 1 && !Charge(i, bytes.Length)) return null;
            }

            // Innermost first: the filters were applied to the file bytes in that order.
            for (int k = next; k < chain.Count; k++)
            {
                if (jpeg) return RefuseStageOverJpeg(chain[k].Name);

                if (chain[k].Name == PdfFilterNames.DctDecode)
                {
                    jpeg = true;
                    continue;
                }

                bytes = Decode(i, chain[k].Name, chain[k].Parameters, bytes);
                if (bytes == null || !Charge(i, bytes.Length)) return null;
            }
            return bytes;
        }

        /// <summary>Charges a block's bytes to the allocation budget; false, said, when it refuses.</summary>
        private static bool Charge(PsInterpreter i, long bytes)
        {
            if (i.Allocation.TryCharge(bytes)) return true;
            PostScriptDiagnostics.Report("PsFile",
                "the composite-allocation budget is exhausted; a data source's block was not read");
            return false;
        }

        /// <summary>
        /// The bytes of a filter's input or output one interpreter step pays for: the scale the
        /// operators charge a copy at. Decoding is dearer than that (about 4 ns a byte against 29 a step),
        /// and it is the allocation budget that bounds what is kept; this bounds what is thrown away.
        /// </summary>
        private const int BytesPerStep = 64;

        /// <summary>
        /// The most output one byte of a filter's input can have produced: an LZW code of nine bits or more
        /// stands for at most 4 096 bytes. What a decode that failed is charged for, since it leaves no count.
        /// </summary>
        private const int MaxExpansionPerInputByte = 4096;

        /// <summary>
        /// Takes the raw bytes of the block, advancing the scanner past it. For a marker-delimited
        /// filter they are the bytes before the marker, still to be decoded (<paramref name="next"/>
        /// is 0); for a binary one, Core decodes them in the same pass that finds the end, so the
        /// stage is done (<paramref name="next"/> is 1), and a JPEG is handed on as it is.
        /// </summary>
        private bool TryTakeInnermost(string innermost, PsDict innermostParameters, long maxOutputBytes,
                                      out byte[] bytes, out int next, out bool jpeg)
        {
            bytes = null;
            next  = 0;
            jpeg  = false;
            bool found;

            switch (innermost)
            {
                case SubFileDecode:
                {
                    // Its end is its byte count, or the EOD string's occurrence after EODCount others,
                    // the string consumed — the block is what the filter passes.
                    next = 1;
                    var (eodCount, eodString) = SubFileParameters(innermostParameters);
                    return TakeMeasured(innermost, eodString.Length > 0 ? "its end-of-data string" : "its byte count",
                        (byte[] data, int start, int count, out int end) =>
                            SubFile(data, start, count, eodCount, eodString, start + count == data.Length, out end),
                        out bytes);
                }

                case PdfFilterNames.AsciiHexDecode:
                    bytes = _scanner.TakeInlineBlock(new[] { (byte)'>' }, out found);
                    return found || ReportUnterminated(innermost, "'>'");

                case PdfFilterNames.Ascii85Decode:
                    bytes = _scanner.TakeInlineBlock(new[] { (byte)'~', (byte)'>' }, out found);
                    return found || ReportUnterminated(innermost, "'~>'");

                case PdfFilterNames.RunLengthDecode:
                    next = 1;
                    return TakeMeasured(innermost, "the EOD byte 128",
                        (byte[] data, int start, int count, out int end) =>
                            RunLengthDecodeFilter.DecodeEmbedded(data, start, count, maxOutputBytes, out end),
                        out bytes);

                case PdfFilterNames.LzwDecode:
                    next = 1;
                    return TakeMeasured(innermost, "the EOD code 257",
                        (byte[] data, int start, int count, out int end) =>
                            LzwDecodeFilter.DecodeEmbedded(data, start, count, 1, maxOutputBytes, out end),
                        out bytes)
                        && ReportTruncation(innermost, LzwDecodeFilter.LastTruncationReason);

                case PdfFilterNames.FlateDecode:
                    next = 1;
                    return TakeMeasured(innermost, "the final deflate block and its Adler-32 trailer",
                        (byte[] data, int start, int count, out int end) =>
                            FlateDecodeFilter.DecodeEmbedded(data, start, count, maxOutputBytes, out end),
                        out bytes)
                        && ReportTruncation(innermost, FlateDecodeFilter.LastTruncationReason);

                case PdfFilterNames.CcittFaxDecode:
                {
                    // Its end is where /Rows rows have been decoded, or its EOFB or RTC: Core decodes and
                    // measures in one pass. With neither /Rows nor /EndOfBlock the data does not say.
                    next = 1;
                    Core.Objects.PdfDictionary parameters = CcittParameters(innermostParameters);
                    if (!(parameters[Core.Objects.PdfName.Rows] is Core.Objects.PdfNumber { IntValue: > 0 })
                        && parameters[Core.Objects.PdfName.EndOfBlock] is Core.Objects.PdfBoolean { Value: false })
                    {
                        PostScriptDiagnostics.ReportUnimplemented(
                            "currentfile /CCITTFaxDecode filter (no /Rows and /EndOfBlock false: the data does not say where it ends)");
                        return false;
                    }
                    return TakeMeasured(innermost, "its /Rows rows or its end-of-block code",
                        (byte[] data, int start, int count, out int end) =>
                            CcittFaxDecodeFilter.DecodeEmbedded(data, start, count, parameters, maxOutputBytes, out end),
                        out bytes);
                }

                case PdfFilterNames.DctDecode:
                    next = 1;
                    jpeg = true;
                    return TakeMeasured(innermost, "the EOI marker",
                        (byte[] data, int start, int count, out int end) =>
                        {
                            end = DctDecodeFilter.FindEnd(data, start, count);
                            if (end < 0) return null;
                            var codestream = new byte[end - start];
                            Array.Copy(data, start, codestream, 0, codestream.Length);
                            return codestream;
                        },
                        out bytes);

                default:
                    // A filter whose end this converter cannot find is refused by name rather than
                    // guessed at: a wrong extent would feed the rest of the program into the image AND
                    // lose it from the program.
                    PostScriptDiagnostics.ReportUnimplemented(
                        $"currentfile /{innermost} filter (no end-of-data marker in the byte stream)");
                    return false;
            }
        }

        /// <summary>
        /// Takes a block whose end the data itself states, through the scanner. A block the data
        /// never ends is reported and skipped, as an unterminated marker is; one the filter cannot
        /// decode (a ceiling crossed, corrupt data) is reported and skipped too, and the scanner has
        /// consumed as far as it may reach.
        /// </summary>
        private bool TakeMeasured(string filterName, string ending, PsScanner.InlineBlockDecoder decoder,
                                  out byte[] bytes)
        {
            try
            {
                bytes = _scanner.TakeInlineBlock(decoder, out bool found);
                if (found) return true;

                bytes = null;
                return ReportUnterminated(filterName, ending);
            }
            catch (Exception ex)
            {
                // Caught HERE rather than allowed to escape: PostScriptConversion's catch-all
                // reports "no content was produced" and discards every mark already emitted, so an
                // unreadable image would cost the whole fragment instead of itself.
                bytes = null;
                PostScriptDiagnostics.Report("PsFile",
                    $"a /{filterName} data source could not be decoded; the image was skipped, and the " +
                    "program's bytes were consumed as far as a binary block may reach", ex);
                return false;
            }
        }

        private static bool ReportUnterminated(string filterName, string marker)
        {
            PostScriptDiagnostics.Report("PsFile",
                $"a /{filterName} data source reached the end of the program without its end-of-data " +
                $"marker ({marker}); the image was skipped");
            return false;
        }

        /// <summary>
        /// LzwDecodeFilter and FlateDecodeFilter truncate SILENTLY by design; their only signal is
        /// the reason slot. Reported, and the image still drawn from what was decoded. Always true,
        /// so a take can chain it.
        /// </summary>
        private static bool ReportTruncation(string filterName, string reason)
        {
            if (reason != null)
                PostScriptDiagnostics.Report("PsFile",
                    $"a /{filterName} data source was truncated: " + reason);
            return true;
        }

        private static byte[] RefuseStageOverJpeg(string stage)
        {
            // A JPEG is carried, not decoded, so nothing can be applied to its samples here.
            PostScriptDiagnostics.ReportUnimplemented($"filter /{stage} (over a /DCTDecode data source)");
            return null;
        }

        /// <summary>A <c>SubFileDecode</c> stage's <c>/EODCount</c> (0 when absent, as other interpreters take it) and <c>/EODString</c> (empty when absent).</summary>
        private static (int Count, byte[] Eod) SubFileParameters(PsDict parameters)
        {
            int count = 0;
            byte[] eod = Array.Empty<byte>();
            if (parameters != null && parameters.TryGetName("EODCount", out PsObject n) && n.Type == PsType.Integer)
                count = Math.Max(0, n.AsInt());
            if (parameters != null && parameters.TryGetName("EODString", out PsObject s) && s.Type == PsType.String)
                eod = s.AsString().ToArray();
            return (count, eod);
        }

        /// <summary>
        /// What <c>SubFileDecode</c> passes from <paramref name="data"/>[<paramref name="start"/>, +<paramref name="count"/>)
        /// (PLRM §3.8.4): with an EOD string, the
        /// bytes before its occurrence after <paramref name="eodCount"/> others — those passed through —
        /// <paramref name="end"/> past the string, which is consumed; with none, <paramref name="eodCount"/>
        /// bytes, or every byte to the source's end for a count of 0 (only when
        /// <paramref name="sourceEndsHere"/>). Null, <paramref name="end"/> −1, when the data runs out
        /// first. The string is found by Knuth–Morris–Pratt: linear in the data whatever the string.
        /// </summary>
        internal static byte[] SubFile(byte[] data, int start, int count, int eodCount, byte[] eod,
                                       bool sourceEndsHere, out int end)
        {
            end = -1;
            if (eod.Length == 0)
            {
                if (eodCount == 0 ? !sourceEndsHere : eodCount > count) return null;
                end = start + (eodCount == 0 ? count : eodCount);
                return Slice(data, start, end - start);
            }

            var failure = new int[eod.Length];
            for (int k = 1, m = 0; k < eod.Length; k++)
            {
                while (m > 0 && eod[k] != eod[m]) m = failure[m - 1];
                if (eod[k] == eod[m]) m++;
                failure[k] = m;
            }

            int seen = 0, matched = 0;
            for (int at = start; at < start + count; at++)
            {
                while (matched > 0 && data[at] != eod[matched])
                {
                    matched = failure[matched - 1];
                    SubFileFallbacks++;
                }
                if (data[at] == eod[matched]) matched++;
                if (matched < eod.Length) continue;

                if (seen++ == eodCount)
                {
                    end = at + 1;
                    return Slice(data, start, end - eod.Length - start);
                }
                matched = 0;   // occurrences do not overlap: the next is sought after this one
            }
            return null;
        }

        /// <summary>
        /// The times <see cref="SubFile"/> has fallen back along the string on the calling thread — at
        /// most one a byte scanned, which is the evidence that the search is linear.
        /// </summary>
        [ThreadStatic] internal static long SubFileFallbacks;

        private static byte[] Slice(byte[] data, int start, int length)
        {
            var slice = new byte[length];
            Array.Copy(data, start, slice, 0, length);
            return slice;
        }

        /// <summary>
        /// Decodes one stage through <b>Core's</b> filter, never a private copy — stream filter
        /// decoding lives in <c>Core.IO.Filters</c> and nowhere else.
        ///
        /// <para>Instantiated directly rather than routed through <c>FilterPipeline</c>, whose
        /// <c>StrictUnknownFilters</c> is a mutable <i>process-wide</i> static: setting it here
        /// would change the host parser's leniency for every concurrent document.</para>
        /// </summary>
        /// <remarks>
        /// Each decode is charged to the work budget, whatever becomes of its output — a trial over part of a
        /// procedure's data, a refused or corrupt stream — and a failed one at the most its input can have
        /// produced under the ceiling, since a filter that throws says nothing of how far it got.
        /// </remarks>
        internal static byte[] Decode(PsInterpreter i, string filterName, PsDict parameters, byte[] data, bool report = true)
        {
            long maxOutputBytes = i.Allocation.Remaining;
            i.Tick(1 + data.Length / BytesPerStep);

            // Over another filter's output a SubFileDecode stage passes what its count or its string
            // allows; a source that ends first ends it there.
            if (filterName == SubFileDecode)
            {
                var (eodCount, eodString) = SubFileParameters(parameters);
                i.Tick(eodString.Length / BytesPerStep);
                return SubFile(data, 0, data.Length, eodCount, eodString, sourceEndsHere: true, out _) ?? data;
            }

            StreamFilter filter = filterName switch
            {
                PdfFilterNames.AsciiHexDecode  => new AsciiHexDecodeFilter(),
                PdfFilterNames.Ascii85Decode   => new Ascii85DecodeFilter(),
                PdfFilterNames.RunLengthDecode => new RunLengthDecodeFilter(),
                PdfFilterNames.FlateDecode     => new FlateDecodeFilter(),
                PdfFilterNames.LzwDecode       => new LzwDecodeFilter(),
                _                              => null
            };

            if (filter == null)
            {
                if (report) PostScriptDiagnostics.ReportUnimplemented($"filter /{filterName}");
                return null;
            }

            filter.MaxOutputBytes = Math.Max(0, maxOutputBytes);

            try
            {
                byte[] decoded = filter.Decode(data, null);
                i.Tick(decoded.Length / BytesPerStep);

                if (report && filterName == PdfFilterNames.LzwDecode)
                    ReportTruncation(filterName, LzwDecodeFilter.LastTruncationReason);
                if (report && filterName == PdfFilterNames.FlateDecode)
                    ReportTruncation(filterName, FlateDecodeFilter.LastTruncationReason);

                return decoded;
            }
            catch (Exception ex) when (ex is not PsBudgetExhaustedException)
            {
                i.Tick(Math.Min(maxOutputBytes, (long)data.Length * MaxExpansionPerInputByte) / BytesPerStep);

                // Caught HERE rather than allowed to escape: PostScriptConversion's catch-all
                // reports "no content was produced" and discards every mark already emitted, so an
                // unreadable image would cost the whole fragment instead of itself.
                if (report)
                    PostScriptDiagnostics.Report("PsFile",
                        $"a /{filterName} data source could not be decoded; the image was skipped", ex);
                return null;
            }
        }
    }
}
