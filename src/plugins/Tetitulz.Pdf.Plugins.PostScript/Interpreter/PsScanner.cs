// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Document;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// The raw bytes of a Type 1 font program and the three section lengths a PDF
    /// <c>/FontFile</c> stream must declare.
    /// </summary>
    internal sealed class PsEmbeddedFontBytes
    {
        public byte[] Program { get; init; } = System.Array.Empty<byte>();
        public int Length1 { get; init; }
        public int Length2 { get; init; }
        public int Length3 { get; init; }

        /// <summary>
        /// True when the scan reached a real end of the encrypted section — the 512-zero trailer,
        /// or <c>cleartomark</c> — rather than simply running out of buffer.
        ///
        /// <para><b>Without this the two cases are indistinguishable, and the truncated one passes
        /// the gate that exists to reject it.</b> When the scan runs off the end, <c>trailerStart</c>
        /// stays -1, <c>encryptedEnd</c> becomes <c>_pos</c>, the <c>cleartomark</c> loop cannot
        /// advance, and <c>Length3</c> comes out 0 — which
        /// <c>PsEmbeddedFont.IsWellFormed</c> accepts, because <c>Length3 == 0</c> is
        /// <i>legal</i> per Table 127 when the fixed-content portion is genuinely absent. A
        /// truncated download therefore embedded a <c>/FontFile</c> cut mid-charstring and reported
        /// it as "embedded verbatim", which is the outcome the sibling diagnostic promises is
        /// avoided.</para>
        /// </summary>
        public bool TrailerFound { get; init; }
    }

    /// <summary>
    /// Turns PostScript bytes into <see cref="PsToken"/>s.
    ///
    /// <para>This is where a naive tokenizer goes wrong, and the three ways it does are worth
    /// naming because each silently corrupts everything downstream rather than failing:</para>
    /// <list type="number">
    ///   <item><b>Delimiters terminate tokens without whitespace.</b> <c>abc/def</c> is two tokens
    ///     and <c>1 2 add}</c> ends a procedure. A scanner that splits only on whitespace merges
    ///     them into nonsense names.</item>
    ///   <item><b>Braces inside strings are not braces.</b> <c>(a { b)</c> contains no procedure.
    ///     <c>Core.Functions.PdfFunctionEvaluator.Tokenize</c> brace-matches without knowing about
    ///     strings — correct for Type 4, which has no string literals, and fatal here.</item>
    ///   <item><b>Comments are everywhere.</b> A real <c>.ps</c> file is a third comments, and DSC
    ///     lines carry the page geometry, so they are observed on the way past rather than simply
    ///     dropped.</item>
    /// </list>
    ///
    /// <para>Binary sections — an <c>eexec</c>-encrypted font program, a <c>%%BeginBinary</c>
    /// payload — are <b>skipped by byte count or terminator</b>, never tokenised. Feeding encrypted
    /// font bytes to a tokenizer produces a storm of garbage names, and among them, eventually, a
    /// byte that looks like <c>{</c>.</para>
    /// </summary>
    internal sealed class PsScanner
    {
        private readonly byte[] _data;
        private readonly PsAllocationBudget _alloc;
        private readonly DscScanner _dsc;
        private int _pos;

        public PsScanner(byte[] data, PsAllocationBudget alloc = null, DscScanner dsc = null)
        {
            _data  = data ?? Array.Empty<byte>();
            _alloc = alloc ?? new PsAllocationBudget();
            _dsc   = dsc;
        }

        /// <summary>Byte offset of the next unread byte — for diagnostics.</summary>
        public int Position => _pos;

        /// <summary>Moves the next unread byte to <paramref name="position"/>, within the data — a file read by other operators between tokens.</summary>
        public void SeekTo(int position) => _pos = Math.Max(0, Math.Min(position, _data.Length));

        /// <summary>True once <c>eexec</c> or a binary block was skipped rather than interpreted.</summary>
        public bool SkippedBinarySection { get; private set; }

        /// <summary>
        /// Where the current font program's clear-text header began, from the DSC scanner, or -1.
        /// </summary>
        public int FontProgramStart => _dsc?.LastFontProgramStart ?? -1;

        private readonly List<PsEmbeddedFontBytes> _capturedFonts = new();

        /// <summary>
        /// Type 1 font programs captured from <c>eexec</c> sections, in source order.
        ///
        /// <para><b>Captured during scanning, not during execution</b>, because the whole input is
        /// tokenised before the interpreter runs a single object — by the time an <c>eexec</c>
        /// operator executed, the bytes it names would already be behind the scanner. The operator
        /// still exists, to consume its <c>currentfile</c> operand; the bytes come from here.</para>
        /// </summary>
        public IReadOnlyList<PsEmbeddedFontBytes> CapturedFontPrograms => _capturedFonts;

        /// <summary>PLRM §3.1: the characters that end a token without whitespace.</summary>
        // PLRM §3.1's character classes, NOT ISO 32000-1 §7.2.3's — and deliberately not routed
        // through Core.Constants.PdfSyntax, which the two PDF-scanning plugins share. The two
        // sets are byte-for-byte identical today, and identical by coincidence of two
        // specifications rather than because one defines the other; binding a PostScript scanner to
        // a constant named for the PDF spec would make a change to one silently change the other.
        private static bool IsDelimiter(byte b) =>
            b == (byte)'(' || b == (byte)')' ||
            b == (byte)'<' || b == (byte)'>' ||
            b == (byte)'[' || b == (byte)']' ||
            b == (byte)'{' || b == (byte)'}' ||
            b == (byte)'/' || b == (byte)'%';

        /// <summary>PLRM §3.1 white space, which includes NUL and form feed.</summary>
        internal static bool IsWhite(byte b) =>
            b == 0 || b == 9 || b == 10 || b == 12 || b == 13 || b == 32;

        // ------------------------------------------------------------------
        //  Per-scan diagnostics
        // ------------------------------------------------------------------

        /// <summary>
        /// The scanning anomalies a file can contain an unbounded number of.
        ///
        /// <para>Each is reported <b>once</b>, with a count in the summary at end of input, for the
        /// reason <c>PostScriptDiagnostics.ReportOversizedNumericToken</c> already states:
        /// <c>Report</c> is neither deduplicated nor capped, so it composes a message, sets
        /// <c>LastError</c> and invokes every <c>OnEvent</c> subscriber every time it is called. An
        /// 8 MiB <c>/Subtype /PS</c> stream of <c>'&gt;'</c> bytes produced ~8 000 000 formatted
        /// strings and ~8 000 000 callbacks <b>before a single PostScript object executed</b> —
        /// nothing ticks a budget during scanning — so a host that subscribed its logger, which is
        /// the documented use, logged eight million lines from one XObject. The diagnostics were
        /// the attack.</para>
        /// </summary>
        private enum ScanIssue
        {
            StrayAngleBracket,
            UnbalancedCloseParen,
            NonHexCharacter,
            BinarySection,
            OversizedLiteral,
            AllocationExhausted,
            Base85Character
        }

        private readonly int[] _issueCounts = new int[7];
        private bool _issueSummaryReported;

        /// <summary>Reports the first occurrence of an anomaly and counts the rest.</summary>
        private void Note(ScanIssue issue, string detail)
        {
            if (_issueCounts[(int)issue]++ == 0)
                PostScriptDiagnostics.Report("PsScanner", detail);
        }

        /// <summary>
        /// Reports one summary line per repeated anomaly, at end of input.
        ///
        /// <para>The count is the point: "one stray '&gt;' was ignored" and "8 000 000 stray '&gt;'
        /// bytes were ignored" describe very different files, and suppressing everything after the
        /// first would have hidden the difference. Bounded by the number of enum members, and
        /// emitted once — <see cref="TryNext"/> can be called again after it has returned false.</para>
        /// </summary>
        private void ReportIssueSummary()
        {
            if (_issueSummaryReported) return;
            _issueSummaryReported = true;

            Summarise(ScanIssue.StrayAngleBracket,    "stray '>' byte(s) outside a dictionary");
            Summarise(ScanIssue.UnbalancedCloseParen, "unbalanced ')' byte(s) outside a string");
            Summarise(ScanIssue.NonHexCharacter,      "non-hex character(s) inside hex strings");
            Summarise(ScanIssue.BinarySection,        "binary section(s) skipped without interpreting");
            Summarise(ScanIssue.OversizedLiteral,     "over-long string literal(s) truncated");
            Summarise(ScanIssue.AllocationExhausted,  "string(s) scanned past the allocation budget");
            Summarise(ScanIssue.Base85Character,       "invalid character(s) inside base-85 strings");
        }

        private void Summarise(ScanIssue issue, string what)
        {
            int count = _issueCounts[(int)issue];
            if (count < 2) return;

            PostScriptDiagnostics.Report("PsScanner",
                $"{count} {what} were seen while scanning; the first was reported above and the " +
                "rest were counted rather than reported individually");
        }

        /// <summary>Reads the next token. Returns false at end of input.</summary>
        public bool TryNext(out PsToken token)
        {
            bool read = TryNextToken(out token);
            if (read) _tokenEnd = _pos;
            return read;
        }

        /// <summary>
        /// Where the last token ended — the byte its delimiter is, which the scanner has not taken —
        /// or -1 once a raw read has moved past it. PLRM §3.8.2: the one white-space character
        /// that ends a token belongs to it (CR LF counting as one), so the first byte a raw read
        /// returns is the one after it; any later read starts exactly where the last one stopped.
        /// </summary>
        private int _tokenEnd = -1;

        private void SkipTokenDelimiter()
        {
            if (_pos != _tokenEnd) return;
            _tokenEnd = -1;
            if (_pos >= _data.Length) return;
            if (_data[_pos] == 13)
            {
                _pos++;
                if (_pos < _data.Length && _data[_pos] == 10) _pos++;
            }
            else if (IsWhite(_data[_pos])) _pos++;
        }

        /// <summary>
        /// <c>readstring</c>'s read of the program's own bytes: up to <paramref name="count"/> of them
        /// from the cursor into <paramref name="target"/>, as they are; the number read, fewer only at
        /// the end of the program.
        /// </summary>
        internal int ReadRaw(byte[] target, int offset, int count)
        {
            SkipTokenDelimiter();
            int n = Math.Max(0, Math.Min(count, _data.Length - _pos));
            Array.Copy(_data, _pos, target, offset, n);
            _pos += n;
            return n;
        }

        /// <summary>
        /// <c>readhexstring</c>'s read: hexadecimal digit pairs from the cursor, every other character
        /// skipped (PLRM §8.2), up to <paramref name="count"/> bytes; the number read, fewer only at the
        /// end of the program — where a lone last digit is its byte's high nibble.
        /// </summary>
        internal int ReadHex(byte[] target, int offset, int count)
        {
            SkipTokenDelimiter();
            return ReadHexFrom(_data, ref _pos, target, offset, count);
        }

        /// <summary>The hex read itself, over any bytes — the program's, or a filtered file's decoded block.</summary>
        internal static int ReadHexFrom(byte[] source, ref int at, byte[] target, int offset, int count)
        {
            int n = 0, high = -1;
            while (n < count && at < source.Length)
            {
                int digit = HexValue(source[at++]);
                if (digit < 0) continue;
                if (high < 0) { high = digit; continue; }
                target[offset + n++] = (byte)(high << 4 | digit);
                high = -1;
            }
            if (high >= 0 && n < count) target[offset + n++] = (byte)(high << 4);
            return n;
        }

        /// <summary>
        /// <c>readline</c>'s read: bytes up to the next end of line — CR, LF or CR LF, not stored — into
        /// <paramref name="target"/> from <paramref name="offset"/>, at most <paramref name="count"/>.
        /// <paramref name="ended"/> is true when an end of line was read, false at the end of the
        /// program; <paramref name="overflow"/> when the line is longer than the room.
        /// </summary>
        internal int ReadLine(byte[] target, int offset, int count, out bool ended, out bool overflow)
        {
            SkipTokenDelimiter();
            return ReadLineFrom(_data, ref _pos, target, offset, count, out ended, out overflow);
        }

        /// <summary>The line read itself, over any bytes — the program's, or a filtered file's decoded block.</summary>
        internal static int ReadLineFrom(byte[] source, ref int at, byte[] target, int offset, int count,
            out bool ended, out bool overflow)
        {
            int n = 0;
            ended = overflow = false;
            while (at < source.Length)
            {
                byte b = source[at];
                if (b == 13 || b == 10)
                {
                    at++;
                    if (b == 13 && at < source.Length && source[at] == 10) at++;
                    ended = true;
                    return n;
                }
                if (n >= count) { overflow = true; return n; }
                target[offset + n++] = b;
                at++;
            }
            return n;
        }

        private bool TryNextToken(out PsToken token)
        {
            // Loop rather than recurse: a file of stray '>' characters would otherwise recurse once
            // per character and overflow the stack.
            while (true)
            {
                SkipWhiteAndComments();
                if (_pos >= _data.Length)
                {
                    ReportIssueSummary();
                    token = default;
                    return false;
                }

                byte c = _data[_pos];
                switch (c)
                {
                    case (byte)'{': _pos++; token = PsToken.ProcOpen;   return true;
                    case (byte)'}': _pos++; token = PsToken.ProcClose;  return true;
                    case (byte)'[': _pos++; token = PsToken.ArrayOpen;  return true;
                    case (byte)']': _pos++; token = PsToken.ArrayClose; return true;

                    case (byte)'(':
                        return ScanLiteralString(out token);

                    case (byte)'<':
                        if (_pos + 1 < _data.Length && _data[_pos + 1] == (byte)'<')
                        { _pos += 2; token = PsToken.DictOpen; return true; }

                        // `<~ … ~>` is an ASCII base-85 string (PLRM §3.2.2), not a hex string, and
                        // falling through to ScanHexString does not merely fail — it SUCCEEDS with
                        // the wrong bytes. Base-85 uses the whole printable range, so the hex
                        // scanner keeps whichever characters happen to look hexadecimal and drops
                        // the rest: `<~87cURD]j7BEbo80~>` ("Hello") came back as five unrelated
                        // bytes, with only a "non-hex character" note to say anything was wrong.
                        if (_pos + 1 < _data.Length && _data[_pos + 1] == (byte)'~')
                            return ScanBase85String(out token);

                        return ScanHexString(out token);

                    case (byte)'>':
                        if (_pos + 1 < _data.Length && _data[_pos + 1] == (byte)'>')
                        { _pos += 2; token = PsToken.DictClose; return true; }
                        _pos++;   // stray '>' — report the first, count the rest, and resync
                        Note(ScanIssue.StrayAngleBracket, $"stray '>' at byte {_pos - 1} ignored");
                        continue;

                    case (byte)'/':
                        return ScanName(out token);

                    case (byte)')':
                        _pos++;   // unbalanced ')' outside a string
                        Note(ScanIssue.UnbalancedCloseParen,
                             $"unbalanced ')' at byte {_pos - 1} ignored");
                        continue;

                    default:
                        return ScanNumberOrName(out token);
                }
            }
        }

        /// <summary>
        /// Takes the inline data block that begins at the read cursor, up to and including
        /// <paramref name="terminator"/>, and advances past it.
        ///
        /// <para>This is what <c>currentfile … filter</c> reads, and the reason execution is
        /// streamed: the cursor is exactly where the program's own bytes continue, because the
        /// consuming operator has just been executed and nothing beyond it has been tokenised.</para>
        ///
        /// <para>The returned bytes <b>exclude</b> the terminator, which the ASCII filters would
        /// stop at anyway.</para>
        /// </summary>
        /// <param name="terminator">The end-of-data marker, e.g. <c>&gt;</c> or <c>~&gt;</c>.</param>
        /// <param name="found">False when the input ended before the marker did.</param>
        internal byte[] TakeInlineBlock(byte[] terminator, out bool found)
        {
            found = false;

            int start = SkipBlockLead();
            int limit = Math.Min(_data.Length, start + PsLimits.MaxBinarySkipBytes);

            for (int at = start; at + terminator.Length <= limit; at++)
            {
                bool match = true;
                for (int k = 0; k < terminator.Length && match; k++)
                    if (_data[at + k] != terminator[k]) match = false;

                if (!match) continue;

                found = true;
                var block = new byte[at - start];
                Array.Copy(_data, start, block, 0, block.Length);

                // Advance PAST the terminator. Leaving the cursor before it would tokenise the
                // marker as a stray '>' — and leaving it behind `start` at all would rewind the
                // scanner and re-execute the same tokens forever.
                _pos = at + terminator.Length;
                return block;
            }

            // Unterminated: consume what was scanned rather than leaving it to be tokenised as
            // PostScript, which is what turns image data into a storm of garbage names.
            _pos = limit;
            var partial = new byte[limit - start];
            Array.Copy(_data, start, partial, 0, partial.Length);
            return partial;
        }

        /// <summary>
        /// Reads an inline data block whose end is in the data's own structure rather than in a
        /// marker: given the program bytes, the block's first index and how many bytes it may
        /// occupy, returns the decoded bytes (or the block itself) and, through <c>end</c>, the
        /// index just past the block — or -1 when the data ran out before the block ended.
        /// </summary>
        internal delegate byte[] InlineBlockDecoder(byte[] data, int start, int count, out int end);

        /// <summary>
        /// Takes the inline data block that begins at the read cursor and ends where
        /// <paramref name="decoder"/> says — a RunLength stream's EOD byte, an LZW stream's EOD code,
        /// a zlib stream's final block and trailer, a JPEG's EOI — and advances <b>exactly</b> past
        /// it: the byte after the block is the program's next byte, and nothing of the block is left
        /// to be tokenised. The decoding and the measuring are one pass, because for the compressed
        /// filters the end is known only by decoding.
        /// </summary>
        /// <param name="decoder">
        /// The filter's own reader, handed the program bytes, the block's first index and how many
        /// bytes it may occupy — the lesser of what is left and <see cref="PsLimits.MaxBinarySkipBytes"/>.
        /// </param>
        /// <param name="found">
        /// False when the data ran out — or <see cref="PsLimits.MaxBinarySkipBytes"/> was reached —
        /// before the block ended. The cursor is then at that limit, as for an unterminated marker.
        /// </param>
        /// <exception cref="Exception">
        /// Whatever the decoder throws (a ceiling crossed, corrupt data). The cursor is at the limit
        /// when it does: a block that cannot be measured is consumed as far as the scan may reach,
        /// never left for the tokeniser.
        /// </exception>
        internal byte[] TakeInlineBlock(InlineBlockDecoder decoder, out bool found)
        {
            found = false;

            int start = SkipBlockLead();
            int limit = Math.Min(_data.Length, start + PsLimits.MaxBinarySkipBytes);

            byte[] decoded;
            int end;
            try
            {
                decoded = decoder(_data, start, limit - start, out end);
            }
            catch
            {
                _pos = limit;
                throw;
            }

            if (end < start || end > limit)
            {
                _pos = limit;
                return decoded;
            }

            found = true;
            _pos = end;
            return decoded;
        }

        /// <summary>
        /// Where an inline block's bytes begin: just past the consuming operator's token, by the one
        /// white-space character that ended it (PLRM §3.8.2 — CR LF counting as one), as the raw readers
        /// take it; once a raw read has moved past the token, at the line's end, as before. Taking only
        /// CR or LF began a block after a space with that space, one byte early.
        /// </summary>
        private int SkipBlockLead()
        {
            if (_pos == _tokenEnd) { SkipTokenDelimiter(); return _pos; }
            return SkipPayloadLineEnd();
        }

        /// <summary>
        /// Steps past the end-of-line that ended the operator's line, if there is one: the payload
        /// of an inline block or a binary section starts on the next line. Returns the cursor.
        /// </summary>
        private int SkipPayloadLineEnd()
        {
            if (_pos < _data.Length && _data[_pos] == 13) _pos++;
            if (_pos < _data.Length && _data[_pos] == 10) _pos++;
            return _pos;
        }

        /// <summary>Reads every remaining token. Bounded by the input length, so it terminates.</summary>
        public List<PsToken> ReadAll()
        {
            var tokens = new List<PsToken>();
            while (TryNext(out var t)) tokens.Add(t);
            return tokens;
        }

        // ------------------------------------------------------------------
        //  White space and comments
        // ------------------------------------------------------------------

        private void SkipWhiteAndComments()
        {
            while (_pos < _data.Length)
            {
                byte b = _data[_pos];
                if (IsWhite(b)) { _pos++; continue; }
                if (b != (byte)'%') return;

                // A comment runs to end of line. DSC comments ("%%…", "%!…") are handed to the
                // document scanner BEFORE being discarded — that is where %%BoundingBox comes
                // from — and %%BeginBinary / %%BeginData declare a byte count that must be
                // skipped wholesale.
                int lineStart = _pos;
                while (_pos < _data.Length && _data[_pos] != 10 && _data[_pos] != 13) _pos++;
                int lineLength = _pos - lineStart;

                if (_dsc != null)
                {
                    int skip = _dsc.Observe(_data, lineStart, lineLength, out bool inLines);
                    if (skip > 0) SkipBinary(skip, inLines);
                }
            }
        }

        /// <summary>
        /// Discards a declared-length binary payload — <paramref name="declared"/> bytes, or that
        /// many lines when <paramref name="inLines"/> is set (DSC 3.0 §5.7's <c>Lines</c> unit) —
        /// clamped to what is left and to <see cref="PsLimits.MaxBinarySkipBytes"/>.
        /// </summary>
        private void SkipBinary(int declared, bool inLines)
        {
            // Step past the end-of-line that terminated the %%BeginBinary comment: the payload
            // starts on the next line.
            SkipPayloadLineEnd();

            int available = Math.Min(_data.Length - _pos, PsLimits.MaxBinarySkipBytes);
            int skip;
            if (inLines)
            {
                // A line is ended by LF, CR, or CR LF — counted as one each way.
                int at = 0, lines = 0;
                while (at < available && lines < declared)
                {
                    byte b = _data[_pos + at++];
                    if (b == 10) lines++;
                    else if (b == 13)
                    {
                        lines++;
                        if (at < available && _data[_pos + at] == 10) at++;
                    }
                }
                skip = at;
            }
            else
            {
                skip = Math.Min(declared, available);
            }
            _pos += skip;
            SkippedBinarySection = true;

            // Counted rather than reported per section: a declared length of 0 is legal, so
            // "%%BeginData: 0" repeated is a one-report-per-15-input-bytes amplifier.
            Note(ScanIssue.BinarySection,
                 $"skipped a {skip}-byte binary section without interpreting it");
        }

        /// <summary>
        /// Consumes an <c>eexec</c> section and, when the clear-text header can be located, returns
        /// the whole Type 1 font program so it can be embedded verbatim.
        ///
        /// <para><b>The encrypted bytes are never decrypted.</b> A PDF <c>/FontFile</c> stream <i>is</i>
        /// a Type 1 font program — header, encrypted section, zero trailer — so passing the bytes
        /// through gives the reader the document's own glyphs without this interpreter understanding
        /// any of them. Tokenising them, by contrast, would produce a storm of garbage names and
        /// eventually a byte that looks like <c>{</c>, which is why the section is consumed as a
        /// unit either way.</para>
        /// </summary>
        /// <param name="headerStart">
        /// Byte offset of the font program's clear-text start — the <c>%!PS-AdobeFont</c> or
        /// <c>%!FontType1</c> line the DSC scanner recorded. Pass -1 when it is unknown, in which
        /// case the section is discarded as before: a <c>/FontFile</c> without its header is not a
        /// font program and a reader would reject it.
        /// </param>
        public PsEmbeddedFontBytes CaptureEexecSection(int headerStart)
        {
            // The encrypted section begins after the whitespace that follows `eexec`.
            while (_pos < _data.Length && IsWhite(_data[_pos])) _pos++;
            int encryptedStart = _pos;

            int limit = Math.Min(_data.Length, encryptedStart + PsLimits.MaxBinarySkipBytes);

            // The trailer is 512 ASCII '0' characters (often line-wrapped) then "cleartomark".
            // Scanning for a run of 64 zeros is enough to find it without being fooled by binary
            // data that happens to contain a few 0x30 bytes.
            const int ZeroRunNeeded = 64;
            int zeroRun = 0;
            int trailerStart = -1;

            while (_pos < limit)
            {
                byte b = _data[_pos++];
                if (b == (byte)'0')
                {
                    if (++zeroRun == 1) trailerStart = _pos - 1;
                    if (zeroRun >= ZeroRunNeeded) break;
                }
                else if (!IsWhite(b))
                {
                    zeroRun = 0;
                    trailerStart = -1;
                }
            }

            // Consume the rest of the trailer through `cleartomark`, so the token stream resumes at
            // real PostScript rather than in the middle of the zeros.
            int encryptedEnd = trailerStart >= 0 ? trailerStart : _pos;
            bool clearToMarkFound = false;
            while (_pos < limit && _pos < _data.Length)
            {
                if (_data[_pos] == (byte)'c' && Matches("cleartomark"))
                {
                    _pos += 11;
                    clearToMarkFound = true;
                    break;
                }
                _pos++;
            }

            // Either terminator proves the program ended where the file said it did. Neither means
            // the scan simply hit the end of the buffer (or MaxBinarySkipBytes) mid-charstring —
            // see PsEmbeddedFontBytes.TrailerFound.
            bool trailerFound = trailerStart >= 0 || clearToMarkFound;

            SkippedBinarySection = true;

            // The header belongs to the program just consumed. Forgetting it here is what keeps
            // capture linear in the input: otherwise a file with one header and many eexec sections
            // copies an ever-longer prefix for each one. See DscScanner.ClearFontProgramStart.
            _dsc?.ClearFontProgramStart();

            if (headerStart < 0 || headerStart >= encryptedStart)
            {
                PostScriptDiagnostics.Report("PsScanner",
                    $"consumed a {_pos - encryptedStart}-byte eexec section whose clear-text header " +
                    "could not be located, so the font program was not embedded; glyphs it defines " +
                    "will be missing");
                return null;
            }

            if (_capturedFonts.Count >= PsLimits.MaxCapturedFontPrograms)
            {
                PostScriptDiagnostics.Report("PsScanner",
                    $"the {PsLimits.MaxCapturedFontPrograms}-font capture cap was reached; this " +
                    "font program was consumed but not embedded");
                return null;
            }

            int total = _pos - headerStart;

            // Capture is the one scanning step that copies input instead of pointing into it, so it
            // is charged like any other composite allocation rather than being invisible to the
            // budget the rest of the interpreter answers to.
            if (!_alloc.TryCharge(total))
            {
                PostScriptDiagnostics.Report("PsScanner",
                    $"the allocation budget cannot afford a {total}-byte font program; it was " +
                    "consumed but not embedded");
                return null;
            }

            var program = new byte[total];
            Array.Copy(_data, headerStart, program, 0, total);

            return new PsEmbeddedFontBytes
            {
                Program = program,
                Length1 = encryptedStart - headerStart,
                Length2 = encryptedEnd - encryptedStart,
                Length3 = _pos - encryptedEnd,
                TrailerFound = trailerFound
            };
        }

        private bool Matches(string literal)
        {
            if (_pos + literal.Length > _data.Length) return false;
            for (int i = 0; i < literal.Length; i++)
                if (_data[_pos + i] != (byte)literal[i]) return false;
            return true;
        }

        // ------------------------------------------------------------------
        //  Strings
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>(…)</c> with nested parentheses, the escape set from PLRM §3.2.2, and backslash
        /// line continuation.
        /// </summary>
        private bool ScanLiteralString(out PsToken token)
        {
            _pos++;                          // consume '('
            int depth = 1;
            var buf = new List<byte>(64);
            bool truncated = false;

            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];

                if (b == (byte)'\\')
                {
                    if (_pos >= _data.Length) break;
                    byte e = _data[_pos++];
                    switch (e)
                    {
                        case (byte)'n': buf.Add(10); break;
                        case (byte)'r': buf.Add(13); break;
                        case (byte)'t': buf.Add(9);  break;
                        case (byte)'b': buf.Add(8);  break;
                        case (byte)'f': buf.Add(12); break;
                        case 10: break;                                     // line continuation
                        case 13:
                            if (_pos < _data.Length && _data[_pos] == 10) _pos++;
                            break;                                          // CRLF continuation
                        default:
                            if (e >= (byte)'0' && e <= (byte)'7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && _pos < _data.Length
                                                     && _data[_pos] >= (byte)'0'
                                                     && _data[_pos] <= (byte)'7'; k++)
                                    v = v * 8 + (_data[_pos++] - '0');
                                buf.Add((byte)v);
                            }
                            else buf.Add(e);          // \( \) \\ and "any other" per the spec
                            break;
                    }
                }
                else if (b == (byte)'(') { depth++; buf.Add(b); }
                else if (b == (byte)')') { if (--depth == 0) { depth = 0; goto done; } buf.Add(b); }
                else buf.Add(b);

                // Charge as it grows. An unterminated '(' followed by a 512 MB tail would
                // otherwise become one 512 MB List<byte> before anyone noticed.
                if (buf.Count > PsLimits.MaxSingleStringLength)
                {
                    truncated = true;
                    Note(ScanIssue.OversizedLiteral,
                         $"string literal exceeds {PsLimits.MaxSingleStringLength} bytes; truncated");
                    // Consume the rest of the literal so the token stream stays in sync.
                    SkipRestOfLiteralString(ref depth);
                    goto done;
                }
            }

            if (depth != 0 && !truncated)
                PostScriptDiagnostics.Report("PsScanner",
                    "unterminated string literal at end of input");

        done:
            // Counted, not reported per literal: once the budget is exhausted every subsequent
            // charge fails, so a file of a million `()` after that point is a million reports.
            if (!_alloc.TryCharge(buf.Count))
                Note(ScanIssue.AllocationExhausted,
                     "allocation budget exhausted while scanning a string literal");

            token = PsToken.String(buf.ToArray());
            return true;
        }

        /// <summary>Consumes the tail of an over-long literal without storing it.</summary>
        private void SkipRestOfLiteralString(ref int depth)
        {
            while (_pos < _data.Length && depth > 0)
            {
                byte b = _data[_pos++];
                if (b == (byte)'\\') { if (_pos < _data.Length) _pos++; }
                else if (b == (byte)'(') depth++;
                else if (b == (byte)')') depth--;
            }
        }

        /// <summary>
        /// <c>&lt;…&gt;</c> hex string: whitespace is ignored inside, a non-hex character ends it,
        /// and an odd final digit is padded with <c>0</c> (PLRM §3.2.2).
        /// </summary>
        private bool ScanHexString(out PsToken token)
        {
            _pos++;                          // consume '<'
            var buf = new List<byte>(32);
            int hi = -1;
            bool closed = false;

            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];
                if (b == (byte)'>') { closed = true; break; }
                if (IsWhite(b)) continue;

                int v = HexValue(b);
                if (v < 0)
                {
                    Note(ScanIssue.NonHexCharacter,
                         $"non-hex character '{(char)b}' in a hex string; ignored");
                    continue;
                }

                if (hi < 0) hi = v;
                else { buf.Add((byte)((hi << 4) | v)); hi = -1; }

                if (buf.Count > PsLimits.MaxSingleStringLength)
                {
                    Note(ScanIssue.OversizedLiteral,
                         $"hex string exceeds {PsLimits.MaxSingleStringLength} bytes; truncated");
                    while (_pos < _data.Length && _data[_pos] != (byte)'>') _pos++;
                    if (_pos < _data.Length) { _pos++; closed = true; }
                    break;
                }
            }

            if (hi >= 0) buf.Add((byte)(hi << 4));   // odd digit count → pad with 0
            if (!closed)
                PostScriptDiagnostics.Report("PsScanner", "unterminated hex string at end of input");

            if (!_alloc.TryCharge(buf.Count))
                Note(ScanIssue.AllocationExhausted,
                     "allocation budget exhausted while scanning a hex string");

            token = PsToken.String(buf.ToArray());
            return true;
        }

        /// <summary>
        /// <c>&lt;~ … ~&gt;</c> — an ASCII base-85 string (PLRM §3.2.2).
        ///
        /// <para>Five characters from <c>!</c>..<c>u</c> encode four bytes as a base-85 number,
        /// most significant digit first; <c>z</c> abbreviates a whole group of four zero bytes; and
        /// a final short group of <i>k</i> characters encodes <i>k</i>−1 bytes, padded with the
        /// highest digit. Whitespace is ignored anywhere, which is the whole point of the encoding —
        /// it survives line-wrapping by mail and print spoolers.</para>
        ///
        /// <para>Bounded exactly as <see cref="ScanHexString"/> is: the same length cap, the same
        /// allocation charge, and anomalies noted-and-counted rather than reported per character, so
        /// a hostile file cannot make the diagnostics the attack.</para>
        /// </summary>
        private bool ScanBase85String(out PsToken token)
        {
            _pos += 2;                       // consume '<~'
            var buf = new List<byte>(32);

            uint group = 0;
            int digits = 0;
            bool closed = false;
            bool truncated = false;

            bool groupOverflowed = false;

            void Flush(int count)
            {
                // A short final group is padded with 'u' (84), the highest digit, so the bytes that
                // ARE present round to themselves rather than downwards.
                //
                // In ulong, for the same reason the accumulate loop below is — and this line was
                // NOT. It was plain uint and it WRAPPED, on the very path that loop's comment
                // claims is protected: `<~uuuu~>` reaches 50,529,027 after four digits, pads once
                // to 4,437,053,124, wraps to 142,085,828, and emitted 08 78 0E — three bytes of
                // plausible-looking nonsense rather than a refusal.
                ulong g = group;
                for (int k = digits; k < 5; k++) g = g * 85 + 84;

                // A group that does not fit in 32 bits is DROPPED, matching
                // Core.IO.Filters.Ascii85DecodeFilter.DecodeGroup, so the same payload decodes one way
                // as a `<~…~>` literal and through `currentfile /ASCII85Decode filter` — which this
                // plugin runs through that Core filter (PsFile). Clamping to FF FF FF FF here would
                // give one assembly two answers for one byte sequence, chosen by syntax.
                if (groupOverflowed || g > uint.MaxValue)
                {
                    Note(ScanIssue.Base85Character,
                         "a base-85 group encodes a value larger than 32 bits; the group was dropped");
                    groupOverflowed = false;
                    return;
                }

                buf.Add((byte)(g >> 24));
                if (count > 1) buf.Add((byte)(g >> 16));
                if (count > 2) buf.Add((byte)(g >> 8));
                if (count > 3) buf.Add((byte)g);
            }

            while (_pos < _data.Length)
            {
                byte b = _data[_pos++];

                if (b == (byte)'~')
                {
                    if (_pos < _data.Length && _data[_pos] == (byte)'>') _pos++;
                    closed = true;
                    break;
                }

                if (IsWhite(b)) continue;

                if (b == (byte)'z' && digits == 0)
                {
                    buf.Add(0); buf.Add(0); buf.Add(0); buf.Add(0);
                }
                else if (b >= (byte)'!' && b <= (byte)'u')
                {
                    // 85^5 exceeds 2^32, so a group of five maximal digits overflows. Accumulating
                    // in ulong and refusing is what keeps a malformed group from wrapping into a
                    // plausible-looking value.
                    ulong next = (ulong)group * 85 + (ulong)(b - (byte)'!');
                    if (next > uint.MaxValue)
                    {
                        // Remembered rather than clamped here: Flush is the one place that decides
                        // what an out-of-range group produces, so both overflow routes — the
                        // accumulate above and the 'u'-padding below — give the same answer.
                        groupOverflowed = true;
                        next = uint.MaxValue;
                    }

                    group = (uint)next;
                    if (++digits == 5) { Flush(4); group = 0; digits = 0; groupOverflowed = false; }
                }
                else
                {
                    Note(ScanIssue.Base85Character,
                         $"invalid base-85 character '{(char)b}'; ignored");
                    continue;
                }

                if (buf.Count > PsLimits.MaxSingleStringLength)
                {
                    Note(ScanIssue.OversizedLiteral,
                         $"base-85 string exceeds {PsLimits.MaxSingleStringLength} bytes; truncated");
                    truncated = true;
                    break;
                }
            }

            if (truncated)
            {
                // Resync on the real terminator so the rest of the program still scans.
                while (_pos < _data.Length && _data[_pos] != (byte)'~') _pos++;
                if (_pos < _data.Length) _pos++;
                if (_pos < _data.Length && _data[_pos] == (byte)'>') _pos++;
                closed = true;
            }
            else if (digits > 0)
            {
                // A single leftover digit encodes nothing — k−1 bytes, and k is 1.
                if (digits == 1)
                    Note(ScanIssue.Base85Character,
                         "a base-85 string ended on a single trailing digit, which encodes no byte");
                else
                    Flush(digits - 1);
            }

            if (!closed)
                PostScriptDiagnostics.Report("PsScanner",
                    "unterminated base-85 string at end of input");

            if (!_alloc.TryCharge(buf.Count))
                Note(ScanIssue.AllocationExhausted,
                     "allocation budget exhausted while scanning a base-85 string");

            token = PsToken.String(buf.ToArray());
            return true;
        }

        private static int HexValue(byte b)
        {
            if (b >= (byte)'0' && b <= (byte)'9') return b - '0';
            if (b >= (byte)'a' && b <= (byte)'f') return b - 'a' + 10;
            if (b >= (byte)'A' && b <= (byte)'F') return b - 'A' + 10;
            return -1;
        }

        // ------------------------------------------------------------------
        //  Names and numbers
        // ------------------------------------------------------------------

        /// <summary><c>/name</c>, or <c>//name</c> (immediately evaluated — recognised, not run).</summary>
        private bool ScanName(out PsToken token)
        {
            _pos++;                                     // consume '/'
            bool immediate = _pos < _data.Length && _data[_pos] == (byte)'/';
            if (immediate) _pos++;

            int start = _pos;
            while (_pos < _data.Length && !IsWhite(_data[_pos]) && !IsDelimiter(_data[_pos])) _pos++;

            string text = InternName(start, CappedNameLength(_pos - start));
            token = immediate ? PsToken.ImmediateName(text) : PsToken.Name(text, literal: true);
            return true;
        }

        /// <summary>
        /// A run of non-delimiter, non-white characters: a number if it parses as one, an
        /// executable name otherwise.
        /// </summary>
        private bool ScanNumberOrName(out PsToken token)
        {
            int start = _pos;
            while (_pos < _data.Length && !IsWhite(_data[_pos]) && !IsDelimiter(_data[_pos])) _pos++;

            int length = _pos - start;
            if (length == 0)
            {
                // Defensive: TryNext only reaches here on a non-delimiter, non-white byte, so a
                // zero-length run would be an infinite loop. Consume the byte and carry on.
                _pos++;
                token = PsToken.Name("", literal: false);
                return true;
            }

            var span = new ReadOnlySpan<byte>(_data, start, length);
            if (PsNumberParser.TryParse(span, out bool isInt, out double v))
            {
                token = isInt ? PsToken.Integer(SaturatingCast.ToLong(v)) : PsToken.Real(v);
                return true;
            }

            string name = InternName(start, CappedNameLength(length));

            // `eexec` introduces an encrypted font program. It has to be consumed HERE rather than
            // when the operator executes: the whole input is tokenised before execution begins, so
            // an operator would find the bytes already behind it — and tokenising ciphertext
            // produces a storm of garbage names and, eventually, a byte that looks like '{'.
            if (name == "eexec")
            {
                var captured = CaptureEexecSection(FontProgramStart);
                if (captured != null) _capturedFonts.Add(captured);
            }

            token = PsToken.Name(name, literal: false);
            return true;
        }

        /// <summary>A name token's characters, no more than <see cref="PsLimits.MaxNameChars"/>; a longer one is cut there, said once.</summary>
        private int CappedNameLength(int length)
        {
            if (length <= PsLimits.MaxNameChars) return length;
            Note(ScanIssue.OversizedLiteral, $"a name of {length} characters was cut to {PsLimits.MaxNameChars} (PsLimits.MaxNameChars)");
            return PsLimits.MaxNameChars;
        }

        // One implementation, in Core.Text.Latin1Text — six byte-identical copies existed.
        private string Latin1(int start, int length)
            => Core.Text.Latin1Text.Decode(_data, start, length);

        // ------------------------------------------------------------------
        //  Name interning
        // ------------------------------------------------------------------
        //
        // A name token allocated a fresh string every time it was scanned, and a PostScript program
        // is overwhelmingly the SAME few names repeated: `moveto`, `lineto`, `stroke`, `add`, `def`.
        // A 2000-iteration arithmetic program scans ~14 000 name tokens drawing on about seven
        // distinct names, so ~13 993 of those strings were identical to one already made.
        //
        // Direct-mapped cache, compared on the raw bytes. A collision costs an allocation - exactly
        // what happened before - so correctness never depends on the cache hitting; only the saving
        // does. Sharing the instance is safe because strings are immutable, and it makes the ordinal
        // comparison in PsObject.Equals hit its reference-equality fast path as a side effect.
        //
        // MEASURE BEFORE BELIEVING THIS HELPED: plan 16 §40 records the dictionary-stack hypothesis,
        // which was just as well argued from the code and bought nothing.

        /// <summary>Power of two: the mask below depends on it.</summary>
        private const int InternSlots = 1024;

        private readonly string[] _internedNames = new string[InternSlots];

        /// <summary>
        /// Name tokens served from <see cref="_internedNames"/> without allocating, and those that
        /// had to allocate. <b>Liveness instrumentation, not statistics.</b>
        /// </summary>
        /// <remarks>
        /// <para>The cache is invisible in the output: a hit and a miss produce equal strings, so
        /// every test in the tree passes whether it works or is disabled outright. That is the
        /// blind spot CLAUDE.md records under "an optimisation whose fast and slow paths agree
        /// cannot be pinned by asserting the answer" — the same reason
        /// <c>CMapCodec.UsesCidRangeIndex</c> exists.</para>
        ///
        /// <para>So <c>NameInternHits</c> is asserted directly: a program repeating a handful of
        /// operators must serve nearly all of its name tokens from the cache. Without that, a
        /// future edit that neutralises the lookup — a wrong mask, a hash that never repeats, an
        /// accidental per-token reset — ships green and silently restores ~14 000 allocations.</para>
        /// </remarks>
        internal int NameInternHits { get; private set; }

        /// <inheritdoc cref="NameInternHits"/>
        internal int NameInternMisses { get; private set; }

        /// <summary>
        /// Returns a shared instance for the name at <paramref name="start"/>, allocating only when
        /// this exact byte sequence is not already in the slot its hash selects.
        /// </summary>
        private string InternName(int start, int length)
        {
            // FNV-1a. Chosen for being cheap over the two-to-ten bytes a name usually is; the table
            // is a cache, so a poor distribution costs allocations, never correctness.
            uint hash = 2166136261u;
            for (int i = 0; i < length; i++)
            {
                hash ^= _data[start + i];
                hash *= 16777619u;
            }

            int slot = (int)(hash & (InternSlots - 1));
            string cached = _internedNames[slot];
            if (cached != null && MatchesBytes(cached, start, length))
            {
                NameInternHits++;
                return cached;
            }

            NameInternMisses++;
            string made = Latin1(start, length);
            _internedNames[slot] = made;    // direct-mapped: the newcomer evicts the incumbent
            return made;
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is the Latin-1 decoding of the bytes at
        /// <paramref name="start"/> — the check that makes a hash collision harmless.
        /// </summary>
        /// <remarks>
        /// Latin-1 maps each byte to the code point of the same value, so comparing
        /// <c>(char)byte</c> to the character is exactly the decode this avoids performing.
        /// </remarks>
        private bool MatchesBytes(string candidate, int start, int length)
        {
            if (candidate.Length != length) return false;
            for (int i = 0; i < length; i++)
                if (candidate[i] != (char)_data[start + i]) return false;
            return true;
        }
    }
}
