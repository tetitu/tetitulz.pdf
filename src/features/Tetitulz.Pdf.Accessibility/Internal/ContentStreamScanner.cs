// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using PdfConstants = Tetitulz.Pdf.Core.PdfConstants;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// One operation in a content stream, together with the <em>byte range</em> it occupies
    /// in the raw (already decoded) stream.
    ///
    /// <para>
    /// <b>Why byte offsets matter.</b> Tagging an existing page means inserting
    /// <c>BDC</c>/<c>EMC</c> operators into a stream someone else produced. The only way to do
    /// that without changing a single visible pixel is to <em>splice</em> — copy the original
    /// bytes verbatim and insert new text at operation boundaries. Re-serialising a parsed
    /// operation list back to bytes is lossy: inline images (<c>BI…ID…EI</c>) carry raw binary
    /// payloads, string escapes and number formatting are producer-specific, and any operator
    /// the parser does not model would silently vanish. Splicing at byte offsets is the only
    /// safe transformation, and that is what this scanner exists to enable.
    /// </para>
    /// </summary>
    internal readonly struct ContentToken
    {
        internal static readonly IReadOnlyList<PdfObject> EmptyOperands = new PdfObject[0];

        /// <summary>The operator keyword, e.g. <c>"BT"</c>, <c>"Tj"</c>, <c>"TJ"</c>, <c>"'"</c>.</summary>
        public string Operator { get; }

        /// <summary>
        /// Byte offset of the first byte of this operation — the start of its first operand when
        /// it has operands, otherwise the start of the operator keyword itself. Inserting bytes
        /// <em>at</em> this offset places them immediately before the whole operation.
        /// </summary>
        public int StartOffset { get; }

        /// <summary>
        /// Exclusive byte offset one past the last byte of the operator keyword (for <c>BI</c>,
        /// one past the terminating <c>EI</c>). Inserting bytes <em>at</em> this offset places
        /// them immediately after the whole operation.
        /// </summary>
        public int EndOffset { get; }

        /// <summary>
        /// Operands that preceded the operator, in stream order. Never <c>null</c> — an
        /// operation with no operands carries an empty list.
        /// </summary>
        public IReadOnlyList<PdfObject> Operands { get; }

        /// <summary>For a <c>BI</c> token, the inline image's parameter dictionary as written (abbreviated keys kept); otherwise <c>null</c>.</summary>
        public PdfDictionary InlineImage { get; }

        public ContentToken(string op, int startOffset, int endOffset, IReadOnlyList<PdfObject> operands,
                            PdfDictionary inlineImage = null)
        {
            Operator    = op;
            StartOffset = startOffset;
            EndOffset   = endOffset;
            Operands    = operands ?? EmptyOperands;
            InlineImage = inlineImage;
        }

        public override string ToString()
            => Operator + "@[" + StartOffset.ToString(CultureInfo.InvariantCulture) + ","
             + EndOffset.ToString(CultureInfo.InvariantCulture) + ") ("
             + Operands.Count.ToString(CultureInfo.InvariantCulture) + " operands)";
    }

    /// <summary>
    /// Outcome of a <see cref="ContentStreamScanner"/> pass: the token list plus the handful of
    /// facts a caller needs in order to decide whether the stream is safe to modify.
    /// </summary>
    internal sealed class ContentScanResult
    {
        /// <summary>
        /// The distinct XObject names this scan invokes with <c>Do</c>, in first-use order.
        /// </summary>
        /// <remarks>
        /// <para>Two identical private copies existed — <c>ContentStreamTagger</c>'s
        /// <c>CollectInvokedXObjectNames</c> and <c>FormXObjectInventory</c>'s
        /// <c>InvokedNames</c> — differing only in whitespace and the name. It belongs on the scan
        /// result rather than in either caller: "which XObjects does this content invoke" is a
        /// question about the tokens, not about what the asker intends to do with them.</para>
        ///
        /// <para><b>The operand is read from the end, not the start.</b> A content stream is
        /// postfix, so <c>/Name Do</c> puts the name immediately before the operator — and a
        /// malformed stream that left extra operands on the stack would otherwise yield whichever
        /// stale value happened to be first. Both callers rely on this, so the reason is stated
        /// here, once.</para>
        /// </remarks>
        public IReadOnlyList<string> InvokedXObjectNames()
        {
            if (Tokens == null || Tokens.Count == 0) return System.Array.Empty<string>();

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < Tokens.Count; i++)
            {
                ContentToken token = Tokens[i];
                if (!string.Equals(token.Operator, PdfOperators.InvokeXObject, StringComparison.Ordinal))
                    continue;
                if (token.Operands.Count == 0) continue;

                if (token.Operands[token.Operands.Count - 1] is PdfName name && seen.Add(name.Value))
                    names.Add(name.Value);
            }
            return names;
        }

        /// <summary>
        /// Tokens found, in stream order. When <see cref="Error"/> is non-<c>null</c> this holds
        /// the tokens found <em>before</em> the failure and must not be used for splicing.
        /// </summary>
        public IReadOnlyList<ContentToken> Tokens { get; internal set; } = ContentStreamScanner.EmptyTokens;

        /// <summary>
        /// Non-<c>null</c> when the scanner hit a construct it could not bound with certainty
        /// (unterminated string / dictionary / array, an inline image with no locatable
        /// <c>EI</c>, PostScript calculator braces, excessive nesting) <em>or</em> when the stream
        /// exceeded one of the scanner's resource ceilings (see <see cref="LimitExceeded"/>).
        /// Callers must refuse to modify the stream in that case.
        /// </summary>
        public string Error { get; internal set; }

        /// <summary>
        /// True when <see cref="Error"/> is a <em>resource ceiling</em> refusal — the stream was too
        /// large, or produced too many tokens, for <see cref="ContentStreamScanner"/> to tokenise
        /// within a bounded amount of memory — rather than a malformed construct.
        ///
        /// <para>
        /// The distinction exists because the two call for different answers. A malformed stream is
        /// a defect in the document; an over-ceiling stream may be perfectly valid and simply larger
        /// than this caller is willing to pay for, and the fix is to raise the ceiling (see
        /// <see cref="ContentStreamScanner.DefaultMaxScanBytes"/> /
        /// <see cref="ContentStreamScanner.DefaultMaxTokens"/>) rather than to repair the file.
        /// Either way <see cref="IsSafe"/> is false and <see cref="Tokens"/> is empty: a refusal is
        /// never returned as a partial token list, because a caller cannot tell a truncated scan
        /// from a complete one and would splice, count or measure against a stream it only half saw.
        /// </para>
        /// </summary>
        public bool LimitExceeded { get; internal set; }

        /// <summary>True when the whole stream was scanned without hitting an unbounded construct.</summary>
        public bool IsSafe => Error == null;

        /// <summary>
        /// True when the stream already contains <c>BDC</c>, <c>BMC</c> or <c>EMC</c>. A tagger
        /// must not add a second, overlapping layer of marked content on top of an existing one.
        /// </summary>
        public bool HasMarkedContent { get; internal set; }

        /// <summary>True when the stream contains <c>MP</c>/<c>DP</c> marked-content <em>points</em>.</summary>
        public bool HasMarkedContentPoints { get; internal set; }

        /// <summary>
        /// True when the stream contains a <c>BX</c>/<c>EX</c> compatibility section. Operators
        /// inside such a section are by definition unknown to this scanner, so their operand
        /// syntax cannot be trusted; a conservative tagger refuses these pages.
        /// </summary>
        public bool HasCompatibilitySection { get; internal set; }

        /// <summary>True when at least one <c>BI…ID…EI</c> inline image was scanned.</summary>
        public bool HasInlineImage { get; internal set; }

        /// <summary>
        /// True when at least one inline image's payload length had to be <em>guessed</em> — i.e.
        /// none of a declared <c>/L</c>, a self-delimiting outermost filter's end-of-data marker and
        /// a computable unfiltered sample count settled it, so the end of the payload was located by
        /// the "whitespace-delimited <c>EI</c> followed by printable ASCII" heuristic.
        ///
        /// <para>
        /// <b>Why this is a separate flag from <see cref="Error"/>.</b> The heuristic is good enough
        /// for read-only inspection and is what every PDF reader does, so the scan is not a failure.
        /// But it is provably wrong on real inputs: a dark grayscale image (every sample byte
        /// &lt; 0x80) or a line-wrapped ASCII85 payload can contain a byte sequence that satisfies
        /// every condition the heuristic tests, which ends the image early and makes the scanner
        /// hallucinate operators inside pixel data. Splicing at those offsets writes
        /// <c>BDC</c>/<c>EMC</c> bytes <em>into the image samples</em> and corrupts what the page
        /// renders. <b>Any caller that intends to modify the stream must refuse when this is true</b>;
        /// <see cref="ContentStreamTagger"/> does.
        /// </para>
        /// </summary>
        public bool HasUncertainInlineImage { get; internal set; }

        /// <summary>Number of <c>Do</c> invocations. Text drawn inside a Form XObject lives in another stream.</summary>
        public int XObjectInvocations { get; internal set; }

        /// <summary>Number of <c>BT</c> operators seen.</summary>
        public int TextObjectCount { get; internal set; }
    }

    /// <summary>
    /// A byte-accurate lexical scanner over a decoded PDF content stream.
    ///
    /// <para>
    /// It answers exactly one question: <em>where does each operation begin and end?</em> That
    /// is the prerequisite for splicing <c>BDC</c>/<c>EMC</c> into an existing page without
    /// re-writing it. Everything else it produces (operand objects, the "already marked" flag)
    /// is a by-product.
    /// </para>
    ///
    /// <para><b>What it must never do</b> is report an offset that falls inside a construct where
    /// the bytes are not operators. The four traps, all handled here:</para>
    /// <list type="bullet">
    ///   <item><description><b>Literal strings</b> <c>( … )</c> — backslash escapes and
    ///     <em>nested balanced</em> parentheses. A naive "scan to the next <c>)</c>" ends the
    ///     string early on <c>(a (b) c)</c>.</description></item>
    ///   <item><description><b>Hex strings</b> <c>&lt; … &gt;</c> — must not be confused with the
    ///     <c>&lt;&lt; … &gt;&gt;</c> dictionaries used as <c>BDC</c>/<c>gs</c> operands.</description></item>
    ///   <item><description><b>Comments</b> <c>% … EOL</c> — a comment can legally contain the text
    ///     "BT", "ET" or an unbalanced parenthesis.</description></item>
    ///   <item><description><b>Inline images</b> <c>BI … ID &lt;binary&gt; EI</c> — the classic
    ///     content-stream corruption bug. The payload between <c>ID</c> and <c>EI</c> is arbitrary
    ///     binary and routinely contains the byte pairs "BT", "ET", "(" or "%". Any scanner that
    ///     tokenises straight through it will hallucinate operators inside image data and splice
    ///     there, destroying the page. This scanner jumps the payload wholesale: it prefers the
    ///     declared <c>/L</c> (<c>/Length</c>) when present, and otherwise looks for a
    ///     whitespace-delimited <c>EI</c> that is followed by a valid operator boundary and by
    ///     printable ASCII, refusing outright when neither works.
    ///     The <c>BI</c> is looked for <em>through</em> a whitespace-less operator run as well
    ///     (<c>"qBI"</c>, <c>"QBI"</c>): comparing the whole keyword against <c>"BI"</c> misses
    ///     those and drops the scan straight into the payload.</description></item>
    /// </list>
    ///
    /// <para>
    /// The scanner deliberately does <em>not</em> re-implement PDF semantics. It has no notion of
    /// colour spaces, fonts or paths — it only needs to know where tokens start and stop.
    /// </para>
    ///
    /// <para><b>Bounded work, whatever it is handed.</b> Tokenising allocates a
    /// <see cref="ContentToken"/> plus operand objects per operation, out of as few as two input
    /// bytes, and Core admits a single content stream of up to
    /// <c>PdfLimits.MaxDecompressedBytes</c> (512 MiB) inflated from a few kilobytes of Flate. The
    /// ceilings that bound this therefore live <em>here</em>, at the allocation
    /// (<see cref="DefaultMaxScanBytes"/>, <see cref="DefaultMaxTokens"/>, and the internal
    /// keyword/operand/array caps), not in the callers — enforced only by
    /// <see cref="ContentStreamTagger"/>, they would leave every other caller of
    /// <see cref="ScanDetailed(byte[])"/> exposed. Exceeding one is <em>refused</em>
    /// (<see cref="ContentScanResult.LimitExceeded"/>, empty <see cref="ContentScanResult.Tokens"/>),
    /// never silently truncated: a partial token list is indistinguishable from a complete one to
    /// the caller, which is how a half-seen stream ends up spliced or counted.</para>
    /// </summary>
    internal static class ContentStreamScanner
    {
        internal static readonly IReadOnlyList<ContentToken> EmptyTokens = new ContentToken[0];

        /// <summary>
        /// Reason the most recent <see cref="Scan(byte[])"/> / <see cref="ScanDetailed(byte[])"/>
        /// call reported an unsafe stream; <c>null</c> when the last scan succeeded.
        /// <c>[ThreadStatic]</c> so concurrent page scans on a server host cannot read each
        /// other's diagnostics.
        /// </summary>
        [ThreadStatic]
        private static string _lastScanError;

        /// <summary>Last scan failure on this thread, or <c>null</c>. See <see cref="ContentScanResult.Error"/>.</summary>
        internal static string LastScanError => _lastScanError;

        /// <summary>Guards against stack exhaustion from adversarial <c>[[[[[…</c> / <c>&lt;&lt;&lt;&lt;…</c> input.</summary>
        private const int MaxNestingDepth = 64;

        // ================================================================
        //  Resource ceilings
        //
        //  This scanner runs server-side on uploaded files, and every one of the
        //  ceilings below bounds an allocation that is a MULTIPLE of the input:
        //  one ContentToken (24 bytes) plus its operand objects per operation, out
        //  of as few as two input bytes. Core lets a single content stream inflate
        //  to PdfLimits.MaxDecompressedBytes (512 MiB) from a few kilobytes of
        //  Flate, so "bounded by the file size" is not a bound at all.
        //
        //  The ceilings live HERE, at the allocation, rather than in each caller.
        //  In ContentStreamTagger alone (PageTagOptions.MaxContentBytes) they would protect the
        //  tagging path and nothing else — PdfDeepAccessibilityChecker and
        //  PdfReadingOrderAnalyzer both call ScanDetailed directly. A guard one caller can
        //  forget is not a guard.
        // ================================================================

        /// <summary>
        /// Default ceiling, in bytes, on the decoded content stream
        /// <see cref="ScanDetailed(byte[])"/> will tokenise. Streams above it are refused
        /// <em>before a single byte is parsed or a single token allocated</em> — the only point at
        /// which refusing is free.
        ///
        /// <para>
        /// <b>Why 32 MiB.</b> It is the largest stream any caller in this assembly can currently
        /// hand the scanner on purpose (<c>PdfDeepAccessibilityChecker.MaxFormScanBytes</c> caps the
        /// audit's Form XObject tokenising at exactly this), so the default refuses nothing that was
        /// meant to be scanned while cutting Core's 512 MiB single-stream maximum — the size an
        /// attacker actually controls — by a factor of sixteen. Callers that want a tighter bound
        /// pass their own: the tagger's is 4 MiB, the audit's is
        /// <c>PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes</c>.
        /// </para>
        ///
        /// <para>Pass <c>0</c> or less to <see cref="ScanDetailed(byte[], int, int)"/> to disable
        /// it. That is only for a caller that has already bounded its input itself.</para>
        /// </summary>
        internal const int DefaultMaxScanBytes = 32 * 1024 * 1024;

        /// <summary>
        /// Default ceiling on the number of tokens one scan will accumulate.
        ///
        /// <para>
        /// <b>Why a byte ceiling is not enough.</b> The token list is the dominant allocation and it
        /// is not proportional to the stream in any bounded way: <c>"q q q q …"</c> yields one
        /// 24-byte <see cref="ContentToken"/> per two input bytes, so 32 MiB of operators is 16
        /// million tokens — around 400 MB of list, half of it copied again on the last doubling.
        /// Four million tokens caps that at roughly 96 MB while still admitting a legitimate stream
        /// at the byte ceiling (real content averages one operation per ~12 bytes, so a full-size
        /// 32 MiB stream is under 3 million).
        /// </para>
        ///
        /// <para>
        /// The ceiling is <em>hard</em>: it is tested every iteration, and
        /// <see cref="MaxKeywordLength"/> bounds how many tokens a single iteration can add, so the
        /// list cannot overshoot it by more than one operator run.
        /// </para>
        /// </summary>
        internal const int DefaultMaxTokens = 4_000_000;

        /// <summary>
        /// Cap on the token list's <em>initial</em> capacity.
        ///
        /// <para>
        /// Pre-sizing the list at <c>content.Length / 12</c> with no cap is a perfectly good
        /// estimate and a catastrophic allocation strategy: a 512 MiB stream would ask for a
        /// 42-million-entry list — about 1 GiB — on the Large Object Heap <em>before the first token
        /// was parsed</em>, so the process could die on a stream the scanner would refuse a
        /// microsecond later. Starting small and letting the list grow costs a handful of
        /// doublings on a large stream and nothing at all on a normal one (a 48 KiB page, which is
        /// already a dense page, fits in 4096 entries), and it makes the refusal path above genuinely
        /// allocation-free.
        /// </para>
        /// </summary>
        private const int MaxInitialTokenCapacity = 4096;

        /// <summary>
        /// Longest operator keyword the scanner will accept before declaring the stream unsafe.
        ///
        /// <para>
        /// No operator in ISO 32000-1 Table A.1 is longer than three characters. A longer run of
        /// non-delimiter bytes is either an unknown extension operator or — the case that matters —
        /// an adversarial <c>"qqqq…q"</c> with no whitespace at all, which is a single keyword that
        /// <see cref="SplitConcatenatedOperators"/> would decompose into one sub-operator per byte:
        /// a 32 MiB keyword becomes a 32-million-entry parts list plus 32 million tokens, from
        /// <em>one</em> iteration of the main loop, defeating the token ceiling entirely. Bounding
        /// the keyword bounds both, and 256 is two orders of magnitude past anything a real producer
        /// emits.
        /// </para>
        /// </summary>
        private const int MaxKeywordLength = 256;

        /// <summary>
        /// Ceiling on operands accumulated ahead of a single operator.
        ///
        /// <para>
        /// Operands are held until the operator that consumes them arrives and are then cleared, so
        /// a stream that is <em>nothing but</em> operands (<c>"1 1 1 1 …"</c>) never clears: one
        /// <c>PdfNumber</c> per two input bytes, retained, which is the same denial-of-service as the
        /// token list wearing different clothes. The most operands any real operation takes is six
        /// (<c>cm</c>, <c>d1</c>); 8192 admits even a pathological producer while bounding the
        /// pending list at a few hundred kilobytes.
        /// </para>
        /// </summary>
        private const int MaxPendingOperands = 8192;

        /// <summary>
        /// Ceiling on elements in a single array operand. A <c>TJ</c> array for a dense line of text
        /// runs to a few hundred entries and one for a whole page to a few thousand, so half a
        /// million is far past any real content while stopping <c>"[1 1 1 1 …]"</c> — one operand,
        /// and therefore invisible to <see cref="MaxPendingOperands"/> — from materialising an
        /// object per two input bytes.
        /// </summary>
        private const int MaxArrayElements = 500_000;

        // The operator set is Core's — Tetitulz.Pdf.Core.Text.ContentStreamOperators — because this
        // scanner and Core's ContentStreamParser run the same greedy longest-match split and must
        // agree about what an operator is, or ContentStreamTagger's cross-check fires on perfectly
        // good files.
        //
        // One set, so the two agree on ID and PS (which both renderers dispatch a handler for): a
        // comment asserting that two implementations agree is not a test.
        //
        // What is NOT shared is the failure policy below: this splitter is all-or-nothing on
        // purpose, while Core's yields an unrecognised tail verbatim. See SplitConcatenatedOperators.

        // ================================================================
        //  Public entry points
        // ================================================================

        /// <summary>
        /// Scans <paramref name="content"/> and returns the operator tokens in stream order.
        ///
        /// <para>
        /// <b>Callers that intend to splice must use <see cref="ScanDetailed(byte[])"/> instead</b>,
        /// or at minimum check <see cref="LastScanError"/>: this overload returns whatever it
        /// managed to tokenise before a failure, which is fine for inspection but not for editing.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<ContentToken> Scan(byte[] content) => ScanDetailed(content).Tokens;

        /// <summary>
        /// Scans <paramref name="content"/> and reports both the tokens and the safety facts a
        /// content-stream editor needs, under the default resource ceilings
        /// (<see cref="DefaultMaxScanBytes"/>, <see cref="DefaultMaxTokens"/>). Never throws: an
        /// unbounded construct is reported through <see cref="ContentScanResult.Error"/> (and
        /// <see cref="LastScanError"/>) rather than as an exception, because "this page cannot be
        /// tagged" is a normal outcome, not a fault.
        /// </summary>
        internal static ContentScanResult ScanDetailed(byte[] content)
            => ScanDetailed(content, DefaultMaxScanBytes, DefaultMaxTokens);

        /// <summary>
        /// The ceiling-aware overload of <see cref="ScanDetailed(byte[])"/>, for a caller that
        /// legitimately processes documents larger (or wants to be stricter) than the defaults.
        ///
        /// <para>
        /// Exceeding either ceiling is <b>refused, never truncated</b>: the result carries
        /// <see cref="ContentScanResult.Error"/> naming the limit and the actual size,
        /// <see cref="ContentScanResult.LimitExceeded"/> is true,
        /// <see cref="ContentScanResult.IsSafe"/> is false and
        /// <see cref="ContentScanResult.Tokens"/> is empty. Handing back the tokens found so far
        /// would be worse than useless — a caller cannot distinguish a truncated token list from a
        /// complete one, so it would splice into, or draw conclusions from, a stream it saw only
        /// part of.
        /// </para>
        /// </summary>
        /// <param name="content">The decoded content stream.</param>
        /// <param name="maxContentBytes">
        /// Byte ceiling; <c>0</c> or less disables it. See <see cref="DefaultMaxScanBytes"/>.
        /// </param>
        /// <param name="maxTokens">
        /// Token ceiling; <c>0</c> or less disables it. See <see cref="DefaultMaxTokens"/>.
        /// </param>
        internal static ContentScanResult ScanDetailed(byte[] content, int maxContentBytes, int maxTokens)
        {
            _lastScanError = null;
            var result = new ContentScanResult();

            if (content == null || content.Length == 0)
            {
                result.Tokens = EmptyTokens;
                return result;
            }

            // The byte ceiling is tested first and costs one comparison. Everything below it
            // allocates per operation, so this is the only place where saying no is free — which is
            // the entire point of moving the ceiling here from the tagger.
            if (maxContentBytes > 0 && content.Length > maxContentBytes)
            {
                return Refuse(result,
                    "Content stream is " + content.Length.ToString(CultureInfo.InvariantCulture) +
                    " bytes, past the " + maxContentBytes.ToString(CultureInfo.InvariantCulture) +
                    "-byte scanner limit; tokenising it would cost a large multiple of that in " +
                    "memory, so the stream was refused without being scanned.");
            }

            var tokens   = new List<ContentToken>(Math.Min(MaxInitialTokenCapacity,
                                                           Math.Max(16, content.Length / 12)));
            var operands = new List<PdfObject>(8);
            int operandStart = -1;
            string error = null;
            bool limitExceeded = false;
            int pos = 0;

            try
            {
                while (pos < content.Length)
                {
                    // Hard token ceiling. Tested per iteration, and MaxKeywordLength bounds how many
                    // tokens one iteration can add, so the list cannot run away between two tests.
                    if (maxTokens > 0 && tokens.Count >= maxTokens)
                    {
                        error = "Content stream produced " + tokens.Count.ToString(CultureInfo.InvariantCulture) +
                                " operator tokens at byte " + pos.ToString(CultureInfo.InvariantCulture) +
                                ", reaching the " + maxTokens.ToString(CultureInfo.InvariantCulture) +
                                "-token scanner limit; the scan was abandoned rather than continue " +
                                "allocating, and no partial token list is returned.";
                        limitExceeded = true;
                        break;
                    }

                    SkipWhitespaceAndComments(content, ref pos);
                    if (pos >= content.Length) break;

                    int tokenStart = pos;
                    byte b = content[pos];

                    // ---- operand? ------------------------------------------------
                    int operandKind = TryReadOperand(content, ref pos, 0, out PdfObject operand,
                                                     ref error, ref limitExceeded);
                    if (operandKind < 0) break;                 // unbounded construct — stop
                    if (operandKind > 0)
                    {
                        if (operands.Count == 0) operandStart = tokenStart;
                        operands.Add(operand);
                        if (operands.Count > MaxPendingOperands)
                        {
                            // Operands are only released when an operator consumes them, so a
                            // stream that never reaches one retains an object per operand. See
                            // MaxPendingOperands.
                            error = "More than " + MaxPendingOperands.ToString(CultureInfo.InvariantCulture) +
                                    " operands accumulated before any operator, at byte " +
                                    tokenStart.ToString(CultureInfo.InvariantCulture) +
                                    "; the stream was refused rather than continue allocating.";
                            limitExceeded = true;
                            break;
                        }
                        continue;
                    }

                    // ---- stray closing / unsupported delimiter -------------------
                    if (b == (byte)')' || b == (byte)']' || b == (byte)'>')
                    {
                        error = "Stray '" + (char)b + "' delimiter at byte " +
                                tokenStart.ToString(CultureInfo.InvariantCulture) + ".";
                        break;
                    }
                    if (b == (byte)'{' || b == (byte)'}')
                    {
                        // PostScript calculator braces. They belong to Type 4 function streams,
                        // never to a page content stream; seeing one means we do not understand
                        // this stream well enough to edit it.
                        error = "PostScript calculator brace '" + (char)b + "' at byte " +
                                tokenStart.ToString(CultureInfo.InvariantCulture) + ".";
                        break;
                    }

                    // ---- operator keyword ---------------------------------------
                    string keyword = ReadKeyword(content, ref pos);
                    if (keyword.Length == 0)
                    {
                        // Defensive: ReadKeyword always consumes at least one byte, so this is
                        // unreachable — but a zero-length token would spin the loop forever.
                        error = "Zero-length token at byte " + tokenStart.ToString(CultureInfo.InvariantCulture) + ".";
                        break;
                    }
                    if (keyword.Length > MaxKeywordLength)
                    {
                        // ReadKeyword stops one byte past the cap so this test can see the overrun.
                        // Refusing is mandatory rather than merely prudent: SplitConcatenatedOperators
                        // would turn a keyword this long into one token per byte, which is how a
                        // whitespace-free stream defeats the token ceiling. See MaxKeywordLength.
                        error = "Operator keyword at byte " + tokenStart.ToString(CultureInfo.InvariantCulture) +
                                " runs past " + MaxKeywordLength.ToString(CultureInfo.InvariantCulture) +
                                " bytes without a delimiter; no operator is that long, and decomposing " +
                                "it would allocate one token per byte, so the stream was refused.";
                        limitExceeded = true;
                        break;
                    }

                    // ---- inline image, including one glued to the operator before it ----
                    // This test MUST run before EmitOperator's whitespace-less split, not after.
                    // A producer that omits the space in "q BI" emits the single keyword "qBI",
                    // which is not equal to "BI"; comparing the whole keyword therefore misses the
                    // image, the scan walks straight into the payload, and sample bytes get
                    // tokenised as operators. Splicing at those offsets writes BDC/EMC *into the
                    // image samples* while the dictionary's /L still declares the original length —
                    // i.e. exactly the corruption this scanner exists to prevent, with
                    // HasInlineImage left false so the tagger's uncertain-image refusal never fires.
                    int biOffset = FindInlineImageStart(keyword, out List<KeyValuePair<string, int>> runParts);
                    if (biOffset == BiUndecomposable)
                    {
                        error = "Operator run '" + keyword + "' at byte " +
                                tokenStart.ToString(CultureInfo.InvariantCulture) +
                                " ends in BI but does not decompose into known operators, so the " +
                                "inline image's extent cannot be bounded.";
                        break;
                    }
                    if (biOffset >= 0)
                    {
                        int biStart;
                        PdfObject[] biOperands = null;
                        if (biOffset == 0)
                        {
                            // The keyword *is* the BI (possibly followed by glued header text such
                            // as "BIID"); the operation starts at its first operand, as before.
                            biStart = operandStart >= 0 ? operandStart : tokenStart;
                            biOperands = operands.Count > 0 ? operands.ToArray() : null;
                            operands.Clear();
                            operandStart = -1;
                        }
                        else
                        {
                            // Emit the real operators that precede the BI inside the same keyword
                            // ("q" of "qBI"); they own any pending operands. The image token then
                            // starts at the BI itself.
                            EmitOperatorRun(tokens, result, runParts, biOffset, tokenStart,
                                            operands, ref operandStart);
                            biStart = tokenStart + biOffset;
                        }

                        // Rewind: everything the keyword reader consumed past the BI belongs to the
                        // image header, not to the operator stream.
                        pos = tokenStart + biOffset + PdfOperators.BeginInlineImage.Length;

                        // Jump the entire BI…ID…EI run in one step. `pos` lands just past EI.
                        if (!TryReadInlineImage(content, ref pos, out bool exactLength, out PdfDictionary inlineImage,
                                                ref error, ref limitExceeded)) break;
                        result.HasInlineImage = true;
                        if (!exactLength) result.HasUncertainInlineImage = true;
                        tokens.Add(new ContentToken(PdfOperators.BeginInlineImage, biStart, pos, biOperands, inlineImage));
                        operands.Clear();
                        operandStart = -1;
                        continue;
                    }

                    EmitOperator(tokens, result, keyword, tokenStart, pos, operands, ref operandStart);
                }
            }
            catch (Exception ex)
            {
                // The scanner is written not to throw; if it ever does, degrade to "unsafe"
                // rather than letting the exception escape into a caller that would then have
                // no idea whether the stream was partially processed.
                error = "Unexpected scanner failure: " + ex.Message;
            }

            // A ceiling refusal drops the tokens found so far on purpose — see LimitExceeded. The
            // list is released here rather than handed back half-full, so a caller cannot mistake a
            // truncated scan for a complete one. (A *malformed* stream keeps its partial list: that
            // contract predates the ceilings, ContentScanResult.Tokens documents it, and
            // PdfReadingOrderAnalyzer relies on it to report the positions it did establish.)
            result.Tokens        = limitExceeded ? EmptyTokens : (IReadOnlyList<ContentToken>)tokens;
            result.Error         = error;
            result.LimitExceeded = limitExceeded;
            _lastScanError = error;

            // The [ThreadStatic] slot above is only visible to a caller that thinks to read it.
            // Mirror every failure into the assembly-wide diagnostics sink as well, so a host
            // subscribed to AccessibilityDiagnostics.OnEvent sees why a page was refused instead
            // of only observing that nothing changed.
            if (error != null)
                AccessibilityDiagnostics.Report("ContentStreamScanner.ScanDetailed", error);

            return result;
        }

        /// <summary>
        /// Completes a ceiling refusal that was decided <em>before</em> any parsing began: no token
        /// list, no operand list, no pass over the bytes — just the reason. Keeping this path
        /// allocation-free is the whole point of testing the byte ceiling first.
        /// </summary>
        private static ContentScanResult Refuse(ContentScanResult result, string reason)
        {
            result.Tokens        = EmptyTokens;
            result.Error         = reason;
            result.LimitExceeded = true;
            _lastScanError       = reason;
            AccessibilityDiagnostics.Report("ContentStreamScanner.ScanDetailed", reason);
            return result;
        }

        // ================================================================
        //  Operator emission (including whitespace-less operator runs)
        // ================================================================

        private static void EmitOperator(
            List<ContentToken> tokens,
            ContentScanResult result,
            string keyword,
            int keywordStart,
            int keywordEnd,
            List<PdfObject> operands,
            ref int operandStart)
        {
            PdfObject[] ops = operands.Count > 0 ? operands.ToArray() : null;
            int firstStart  = operandStart >= 0 ? operandStart : keywordStart;

            List<KeyValuePair<string, int>> parts = SplitConcatenatedOperators(keyword);
            if (parts == null)
            {
                tokens.Add(new ContentToken(keyword, firstStart, keywordEnd, ops));
                NoteOperator(result, keyword);
            }
            else
            {
                // "DoQQ" → Do @ +0, Q @ +2, Q @ +3.  The byte ranges stay exact, so a caller may
                // still splice between two glued operators (inserting "\nEMC\n" after the "ET" of
                // "ETQ" yields "ET\nEMC\nQ", which is correct).
                for (int i = 0; i < parts.Count; i++)
                {
                    string sub = parts[i].Key;
                    int offset = parts[i].Value;
                    int start  = i == 0 ? firstStart : keywordStart + offset;
                    int end    = keywordStart + offset + sub.Length;
                    tokens.Add(new ContentToken(sub, start, end, i == 0 ? ops : null));
                    NoteOperator(result, sub);
                }
            }

            operands.Clear();
            operandStart = -1;
        }

        /// <summary>
        /// Emits the leading sub-operators of a decomposed whitespace-less operator run — those
        /// whose offset inside the run is below <paramref name="stopOffset"/>. Used when the run
        /// contains a <c>BI</c>: the operators before it are ordinary operations and must be
        /// tokenised, while the <c>BI</c> itself is handed to the inline-image path so the binary
        /// payload is jumped rather than tokenised.
        /// </summary>
        private static void EmitOperatorRun(
            List<ContentToken> tokens,
            ContentScanResult result,
            List<KeyValuePair<string, int>> parts,
            int stopOffset,
            int keywordStart,
            List<PdfObject> operands,
            ref int operandStart)
        {
            PdfObject[] ops = operands.Count > 0 ? operands.ToArray() : null;
            int firstStart  = operandStart >= 0 ? operandStart : keywordStart;

            for (int i = 0; i < parts.Count; i++)
            {
                int offset = parts[i].Value;
                if (offset >= stopOffset) break;
                string sub = parts[i].Key;
                int start  = i == 0 ? firstStart : keywordStart + offset;
                tokens.Add(new ContentToken(sub, start, keywordStart + offset + sub.Length, i == 0 ? ops : null));
                NoteOperator(result, sub);
            }

            operands.Clear();
            operandStart = -1;
        }

        /// <summary>"No <c>BI</c> anywhere in this keyword" — the overwhelmingly common answer.</summary>
        private const int BiNotPresent = -1;

        /// <summary>
        /// "This keyword ends in <c>BI</c> but cannot be decomposed into known operators", so the
        /// scanner cannot tell where the image's header starts and must declare the stream unsafe.
        /// </summary>
        private const int BiUndecomposable = -2;

        /// <summary>
        /// Locates the <c>BI</c> that starts an inline image inside an operator keyword, allowing
        /// for a producer that glued it onto the preceding operator (<c>"qBI"</c>, <c>"QBI"</c>,
        /// <c>"nBI"</c> — the same class of input <see cref="SplitConcatenatedOperators"/> exists
        /// to handle).
        ///
        /// <para>
        /// Returns the offset of the <c>BI</c> inside <paramref name="keyword"/>,
        /// <see cref="BiNotPresent"/>, or <see cref="BiUndecomposable"/>. When a decomposition was
        /// computed it is handed back through <paramref name="parts"/> so the caller does not have
        /// to split the keyword twice.
        /// </para>
        ///
        /// <para>
        /// The <em>first</em> <c>BI</c> in the decomposition wins: everything after it is by
        /// definition image header bytes (<c>"BIID"</c> is <c>BI</c> immediately followed by the
        /// <c>ID</c> of a parameter-less image, not two operators), and re-reading them from the
        /// image reader is what bounds the payload correctly.
        /// </para>
        /// </summary>
        private static int FindInlineImageStart(string keyword, out List<KeyValuePair<string, int>> parts)
        {
            parts = null;
            if (string.Equals(keyword, PdfOperators.BeginInlineImage, StringComparison.Ordinal)) return 0;
            if (keyword.IndexOf(PdfOperators.BeginInlineImage, StringComparison.Ordinal) < 0) return BiNotPresent;

            parts = SplitConcatenatedOperators(keyword);
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                    if (string.Equals(parts[i].Key, PdfOperators.BeginInlineImage, StringComparison.Ordinal))
                        return parts[i].Value;
                // The "BI" text only straddled a sub-operator boundary — no inline image here.
                return BiNotPresent;
            }

            // Not decomposable. Only a *trailing* "BI" can actually open an inline image: the
            // image's parameter dictionary begins with '/' or whitespace, both of which terminate
            // the keyword, so a BI anywhere else is an accident inside some extension operator's
            // name and is left alone. A trailing one is a real risk of walking into a payload, and
            // "refuse the page" is the only honest answer — corrupting the image is not an option.
            return keyword.EndsWith(PdfOperators.BeginInlineImage, StringComparison.Ordinal)
                 ? BiUndecomposable
                 : BiNotPresent;
        }

        private static void NoteOperator(ContentScanResult result, string op)
        {
            switch (op)
            {
                case PdfOperators.BeginMarkedContentProps:
                case PdfOperators.BeginMarkedContent:
                case PdfOperators.EndMarkedContent:  result.HasMarkedContent = true;       break;
                case PdfOperators.MarkedPoint:
                case PdfOperators.MarkedPointProps:  result.HasMarkedContentPoints = true; break;
                case PdfOperators.BeginCompat:
                case PdfOperators.EndCompat:         result.HasCompatibilitySection = true; break;
                case PdfOperators.InvokeXObject:     result.XObjectInvocations++;          break;
                case PdfOperators.BeginText:         result.TextObjectCount++;             break;
            }
        }

        /// <summary>
        /// Splits a keyword that a producer emitted without separating whitespace into the
        /// sequence of known operators it is made of, returning each sub-operator with its offset
        /// inside the keyword. Returns <c>null</c> when the keyword is already a single known
        /// operator <em>or</em> when it cannot be split cleanly.
        ///
        /// <para>
        /// The all-or-nothing rule matters: a partial split would let an unrecognised extension
        /// operator such as <c>"BTx"</c> masquerade as a <c>BT</c>, and the tagger would then try
        /// to open a text object that does not exist. Requiring the entire keyword to decompose
        /// into known operators keeps that from happening.
        /// </para>
        /// </summary>
        private static List<KeyValuePair<string, int>> SplitConcatenatedOperators(string keyword)
        {
            if (ContentStreamOperators.Contains(keyword)) return null;
            if (keyword.Length < 2) return null;

            var parts = new List<KeyValuePair<string, int>>(4);
            int pos = 0;
            while (pos < keyword.Length)
            {
                bool matched = false;
                for (int len = Math.Min(ContentStreamOperators.MaxLength, keyword.Length - pos); len >= 1; len--)
                {
                    string candidate = keyword.Substring(pos, len);
                    if (ContentStreamOperators.Contains(candidate))
                    {
                        parts.Add(new KeyValuePair<string, int>(candidate, pos));
                        pos += len;
                        matched = true;
                        break;
                    }
                }
                if (!matched) return null;   // leftover garbage — treat the keyword as one opaque token
            }
            return parts.Count > 1 ? parts : null;
        }

        // ================================================================
        //  Lexical primitives
        // ================================================================

        private static void SkipWhitespaceAndComments(byte[] c, ref int pos)
        {
            while (pos < c.Length)
            {
                byte b = c[pos];
                if (PdfConstants.IsWhitespace(b)) { pos++; continue; }
                if (b == PdfConstants.Percent)
                {
                    // A comment runs to the next EOL. Its body is arbitrary text and may contain
                    // "BT", "(" or "EI" — none of which must ever be tokenised.
                    pos++;
                    while (pos < c.Length && c[pos] != PdfConstants.CarriageReturn && c[pos] != PdfConstants.LineFeed)
                        pos++;
                    continue;
                }
                break;
            }
        }

        /// <summary>
        /// Reads one operand at <paramref name="pos"/>.
        /// Returns <c>1</c> when an operand was read, <c>0</c> when the bytes are not an operand
        /// (the caller should treat them as an operator keyword or a stray delimiter), and
        /// <c>-1</c> when an operand started but could not be terminated — an unsafe stream.
        ///
        /// <para>
        /// <paramref name="limitExceeded"/> is set alongside <paramref name="error"/> when the
        /// refusal is a <em>resource ceiling</em> rather than a malformed construct, so
        /// <see cref="ContentScanResult.LimitExceeded"/> stays truthful for a failure raised this
        /// deep (an over-long array operand). It is only ever set, never cleared.
        /// </para>
        /// </summary>
        private static int TryReadOperand(byte[] c, ref int pos, int depth, out PdfObject obj,
                                          ref string error, ref bool limitExceeded)
        {
            obj = null;
            byte b = c[pos];

            if (b == PdfConstants.LeftParen)
            {
                int start = pos;
                PdfString s = ReadLiteralString(c, ref pos, out bool ok);
                if (!ok)
                {
                    error = "Unterminated literal string starting at byte " +
                            start.ToString(CultureInfo.InvariantCulture) + ".";
                    return -1;
                }
                obj = s;
                return 1;
            }

            if (b == PdfConstants.LessThan)
            {
                if (pos + 1 < c.Length && c[pos + 1] == PdfConstants.LessThan)
                {
                    int start = pos;
                    if (!TryReadDictionary(c, ref pos, depth, out PdfDictionary d, ref error, ref limitExceeded))
                    {
                        if (error == null)
                            error = "Unterminated dictionary starting at byte " +
                                    start.ToString(CultureInfo.InvariantCulture) + ".";
                        return -1;
                    }
                    obj = d;
                    return 1;
                }
                else
                {
                    int start = pos;
                    PdfString s = ReadHexString(c, ref pos, out bool ok);
                    if (!ok)
                    {
                        error = "Unterminated hex string starting at byte " +
                                start.ToString(CultureInfo.InvariantCulture) + ".";
                        return -1;
                    }
                    obj = s;
                    return 1;
                }
            }

            if (b == PdfConstants.LeftBracket)
            {
                int start = pos;
                if (!TryReadArray(c, ref pos, depth, out PdfArray a, ref error, ref limitExceeded))
                {
                    if (error == null)
                        error = "Unterminated array starting at byte " +
                                start.ToString(CultureInfo.InvariantCulture) + ".";
                    return -1;
                }
                obj = a;
                return 1;
            }

            if (b == PdfConstants.Solidus)
            {
                obj = ReadName(c, ref pos);
                return 1;
            }

            if (IsNumberStart(b))
            {
                obj = ReadNumber(c, ref pos);
                return 1;
            }

            // true / false / null are keywords that act as operands. Match them without
            // consuming so that a genuine operator keyword falls through to the caller.
            if (MatchesKeyword(c, pos, "true"))  { pos += 4; obj = PdfBoolean.True;  return 1; }
            if (MatchesKeyword(c, pos, "false")) { pos += 5; obj = PdfBoolean.False; return 1; }
            if (MatchesKeyword(c, pos, "null"))  { pos += 4; obj = PdfNull.Instance; return 1; }

            return 0;
        }

        private static bool MatchesKeyword(byte[] c, int pos, string keyword)
        {
            if (pos + keyword.Length > c.Length) return false;
            for (int i = 0; i < keyword.Length; i++)
                if (c[pos + i] != (byte)keyword[i]) return false;
            int after = pos + keyword.Length;
            return after >= c.Length || PdfConstants.IsEndOfToken(c[after]);
        }

        private static bool IsNumberStart(byte b)
            => PdfConstants.IsDigit(b) || b == (byte)'+' || b == (byte)'-' || b == (byte)'.';

        /// <summary>
        /// Reads a literal string, honouring backslash escapes and nested balanced parentheses.
        /// Sets <paramref name="ok"/> to false when the string never closes, which makes the whole
        /// stream unsafe to edit (we would not know where the next real operator begins).
        /// </summary>
        private static PdfString ReadLiteralString(byte[] c, ref int pos, out bool ok)
        {
            pos++;                       // consume '('
            int depth = 1;
            ok = false;
            var bytes = new List<byte>(32);

            while (pos < c.Length)
            {
                byte b = c[pos];

                if (b == PdfConstants.Backslash)
                {
                    pos++;
                    if (pos >= c.Length) break;   // trailing backslash — unterminated
                    byte e = c[pos];
                    switch ((char)e)
                    {
                        case 'n':  bytes.Add((byte)'\n'); pos++; break;
                        case 'r':  bytes.Add((byte)'\r'); pos++; break;
                        case 't':  bytes.Add((byte)'\t'); pos++; break;
                        case 'b':  bytes.Add(8);          pos++; break;
                        case 'f':  bytes.Add(12);         pos++; break;
                        case '(':  bytes.Add((byte)'(');  pos++; break;
                        case ')':  bytes.Add((byte)')');  pos++; break;
                        case '\\': bytes.Add((byte)'\\'); pos++; break;
                        case '\r':
                            // Line continuation: the EOL is not part of the string.
                            pos++;
                            if (pos < c.Length && c[pos] == PdfConstants.LineFeed) pos++;
                            break;
                        case '\n':
                            pos++;
                            break;
                        default:
                            if (e >= (byte)'0' && e <= (byte)'7')
                            {
                                int val = 0, digits = 0;
                                while (digits < 3 && pos < c.Length && c[pos] >= (byte)'0' && c[pos] <= (byte)'7')
                                {
                                    val = (val * 8) + (c[pos] - (byte)'0');
                                    pos++;
                                    digits++;
                                }
                                bytes.Add((byte)(val & 0xFF));
                            }
                            else
                            {
                                // Reverse solidus before any other character is ignored (7.3.4.2).
                                bytes.Add(e);
                                pos++;
                            }
                            break;
                    }
                    continue;
                }

                if (b == PdfConstants.LeftParen)
                {
                    depth++;
                    bytes.Add(b);
                    pos++;
                    continue;
                }

                if (b == PdfConstants.RightParen)
                {
                    depth--;
                    pos++;
                    if (depth == 0) { ok = true; break; }
                    bytes.Add(b);
                    continue;
                }

                bytes.Add(b);
                pos++;
            }

            return new PdfString(bytes.ToArray(), false);
        }

        private static PdfString ReadHexString(byte[] c, ref int pos, out bool ok)
        {
            pos++;                       // consume '<'
            ok = false;
            var bytes = new List<byte>(32);
            int pending = -1;

            while (pos < c.Length)
            {
                byte b = c[pos];
                if (b == PdfConstants.GreaterThan) { pos++; ok = true; break; }
                if (PdfConstants.IsHexDigit(b))
                {
                    int v = PdfConstants.HexToNibble(b);
                    if (pending < 0) pending = v;
                    else { bytes.Add((byte)((pending << 4) | v)); pending = -1; }
                }
                // Whitespace inside a hex string is ignored (7.3.4.3); so is any other junk byte.
                pos++;
            }

            if (pending >= 0) bytes.Add((byte)(pending << 4));   // odd digit count → pad with 0
            return new PdfString(bytes.ToArray(), true);
        }

        /// <summary>
        /// A name object, with its <c>#XX</c> escapes expanded.
        /// </summary>
        /// <remarks>
        /// <b>Decoded by <see cref="Tetitulz.Pdf.Core.Objects.PdfNameEscape.Decode(byte[], int)"/>, the same
        /// rule the document tokenizer uses.</b> This is the tree's second name reader - it exists
        /// because the scanner walks a <c>byte[]</c> with a cursor rather than a stream - and the
        /// two must agree, or a page's tag names would mean one thing to the parser and another to
        /// the tagger. Both accumulate bytes and decode once, because a UTF-8 sequence spans
        /// several bytes and a per-byte <c>(char)</c> cast is a Latin-1 reading.
        /// </remarks>
        private static PdfName ReadName(byte[] c, ref int pos)
        {
            pos++;                       // consume '/'
            byte[] buffer = new byte[16];
            int length = 0;

            void Add(byte value)
            {
                if (length == buffer.Length) Array.Resize(ref buffer, length * 2);
                buffer[length++] = value;
            }

            while (pos < c.Length)
            {
                byte b = c[pos];
                if (PdfConstants.IsEndOfToken(b)) break;
                if (b == PdfConstants.NumberSign && pos + 2 < c.Length
                    && PdfConstants.IsHexDigit(c[pos + 1]) && PdfConstants.IsHexDigit(c[pos + 2]))
                {
                    Add((byte)((PdfConstants.HexToNibble(c[pos + 1]) << 4) | PdfConstants.HexToNibble(c[pos + 2])));
                    pos += 3;
                    continue;
                }
                Add(b);
                pos++;
            }
            return new PdfName(Tetitulz.Pdf.Core.Objects.PdfNameEscape.Decode(buffer, length));
        }

        private static PdfNumber ReadNumber(byte[] c, ref int pos)
        {
            int start = pos;
            while (pos < c.Length)
            {
                byte b = c[pos];
                if (PdfConstants.IsDigit(b) || b == (byte)'+' || b == (byte)'-' || b == (byte)'.') pos++;
                else break;
            }
            string text = Encoding.ASCII.GetString(c, start, pos - start);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                return new PdfNumber(value);
            // A malformed number ("--3", "+") still occupies these bytes; keeping a placeholder
            // operand preserves operand counts so the operator that follows is unaffected.
            return new PdfNumber(0d);
        }

        private static bool TryReadArray(byte[] c, ref int pos, int depth, out PdfArray array,
                                         ref string error, ref bool limitExceeded)
        {
            array = new PdfArray();
            if (depth >= MaxNestingDepth)
            {
                error = "Array nesting exceeds " + MaxNestingDepth.ToString(CultureInfo.InvariantCulture) + " levels.";
                return false;
            }

            pos++;                       // consume '['
            while (true)
            {
                SkipWhitespaceAndComments(c, ref pos);
                if (pos >= c.Length) return false;                       // unterminated
                if (c[pos] == PdfConstants.RightBracket) { pos++; return true; }

                int kind = TryReadOperand(c, ref pos, depth + 1, out PdfObject item, ref error, ref limitExceeded);
                if (kind < 0) return false;
                if (kind == 0)
                {
                    // A bare keyword inside an array is malformed, but it is still bounded — skip
                    // it rather than declaring the whole page unsafe. The ']' still delimits us.
                    if (c[pos] == PdfConstants.RightParen || c[pos] == PdfConstants.GreaterThan
                        || c[pos] == PdfConstants.LeftBrace || c[pos] == PdfConstants.RightBrace)
                    {
                        error = "Unexpected '" + (char)c[pos] + "' inside array at byte " +
                                pos.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }
                    ReadKeyword(c, ref pos);
                    continue;
                }

                array.Add(item);
                if (array.Count > MaxArrayElements)
                {
                    // One array is one operand, so MaxPendingOperands cannot see it grow. See
                    // MaxArrayElements.
                    error = "Array operand starting before byte " + pos.ToString(CultureInfo.InvariantCulture) +
                            " holds more than " + MaxArrayElements.ToString(CultureInfo.InvariantCulture) +
                            " elements; the stream was refused rather than continue allocating.";
                    limitExceeded = true;
                    return false;
                }
            }
        }

        private static bool TryReadDictionary(byte[] c, ref int pos, int depth, out PdfDictionary dict,
                                              ref string error, ref bool limitExceeded)
        {
            dict = new PdfDictionary();
            if (depth >= MaxNestingDepth)
            {
                error = "Dictionary nesting exceeds " + MaxNestingDepth.ToString(CultureInfo.InvariantCulture) + " levels.";
                return false;
            }

            pos += 2;                    // consume '<<'
            while (true)
            {
                SkipWhitespaceAndComments(c, ref pos);
                if (pos >= c.Length) return false;                       // unterminated

                if (c[pos] == PdfConstants.GreaterThan)
                {
                    if (pos + 1 < c.Length && c[pos + 1] == PdfConstants.GreaterThan) { pos += 2; return true; }
                    error = "Malformed dictionary terminator at byte " + pos.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                if (c[pos] != PdfConstants.Solidus)
                {
                    error = "Dictionary key is not a name at byte " + pos.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }

                PdfName key = ReadName(c, ref pos);
                SkipWhitespaceAndComments(c, ref pos);
                if (pos >= c.Length) return false;

                int kind = TryReadOperand(c, ref pos, depth + 1, out PdfObject value, ref error, ref limitExceeded);
                if (kind < 0) return false;
                if (kind == 0)
                {
                    error = "Unsupported dictionary value at byte " + pos.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
                dict.Set(key, value);
            }
        }

        /// <summary>
        /// Reads an operator keyword. Mirrors <c>PdfTokenizer.ReadKeyword</c>: the token stops at
        /// whitespace, at a delimiter, and at a digit (so that producers who write <c>"BT0"</c>
        /// meaning <c>BT 0</c> still tokenise correctly), with <c>d0</c>/<c>d1</c> as the documented
        /// exception. Keeping the two in lockstep is what lets the tagger cross-check its token
        /// stream against the production parser.
        ///
        /// <para>
        /// It stops one byte past <see cref="MaxKeywordLength"/> rather than consuming an unbounded
        /// run, so the caller can see the overrun (<c>keyword.Length &gt; MaxKeywordLength</c>) and
        /// refuse. The over-long prefix is never used as an operator — the main loop refuses the
        /// whole stream on sight of it — so stopping early truncates nothing that is acted upon.
        /// </para>
        /// </summary>
        private static string ReadKeyword(byte[] c, ref int pos)
        {
            var sb = new StringBuilder(8);
            if (pos < c.Length) { sb.Append((char)c[pos]); pos++; }

            while (pos < c.Length)
            {
                byte b = c[pos];
                if (PdfConstants.IsWhitespace(b) || PdfConstants.IsDelimiter(b)) break;
                if (PdfConstants.IsDigit(b))
                {
                    if (sb.Length == 1 && sb[0] == 'd') { sb.Append((char)b); pos++; }
                    break;
                }
                if (sb.Length > MaxKeywordLength) break;
                sb.Append((char)b);
                pos++;
            }
            return sb.ToString();
        }

        // ================================================================
        //  Inline images — the construct that breaks naive scanners
        // ================================================================

        /// <summary>
        /// Consumes an inline image starting immediately after the <c>BI</c> keyword, leaving
        /// <paramref name="pos"/> one byte past the terminating <c>EI</c>.
        ///
        /// <para>
        /// Four strategies, in order of trustworthiness:
        /// </para>
        /// <list type="number">
        ///   <item><description>If the image dictionary declares <c>/L</c> (or <c>/Length</c>, PDF 2.0),
        ///     the payload length is known exactly — jump it and verify that <c>EI</c> really is
        ///     there. No heuristic involved.</description></item>
        ///   <item><description>Otherwise, if the outermost filter is <em>self-delimiting</em>
        ///     (<c>/AHx</c>, <c>/A85</c>, <c>/RL</c>, <c>/DCT</c>), the payload carries its own
        ///     end-of-data marker and the length is read from the bytes themselves
        ///     (§7.4). Verify <c>EI</c> the same way.</description></item>
        ///   <item><description>Otherwise, if the image is <em>unfiltered</em>, the payload length is
        ///     still known exactly: it is <c>ceil(/W · components · /BPC / 8) · /H</c>
        ///     (ISO 32000-1 §8.9.5.2). Jump it and verify <c>EI</c> the same way.</description></item>
        ///   <item><description>Otherwise scan for a whitespace-preceded <c>EI</c> that ends at a
        ///     token boundary and is followed by printable ASCII. The ASCII check is what stops a
        ///     JPEG marker pair that happens to read "EI" from ending the image early; it is the
        ///     same printable-ASCII guard.</description></item>
        /// </list>
        ///
        /// <para>
        /// If none locates a terminator the stream is declared unsafe. Guessing here is exactly
        /// how content streams get corrupted: an <c>EI</c> chosen one byte early leaves binary
        /// image data exposed as "operators", and every subsequent offset is meaningless.
        /// </para>
        /// </summary>
        /// <param name="c">The whole content stream.</param>
        /// <param name="pos">
        /// On entry, the byte just past <c>BI</c>. On success, one byte past the terminating
        /// <c>EI</c>; unchanged on failure.
        /// </param>
        /// <param name="exact">
        /// <c>true</c> when strategy 1 or 2 settled the payload length, <c>false</c> when the
        /// heuristic (strategy 3) had to guess it. A guess is fine for inspection but must never be
        /// spliced at — see <see cref="ContentScanResult.HasUncertainInlineImage"/>.
        /// </param>
        /// <param name="imageDict">The image's parameter dictionary as written, the keys up to <c>ID</c>.</param>
        /// <param name="error">Set to the reason when the image could not be bounded.</param>
        /// <param name="limitExceeded">
        /// Set alongside <paramref name="error"/> when the refusal came from a resource ceiling
        /// inside the image's parameter dictionary rather than from an unbounded construct.
        /// </param>
        private static bool TryReadInlineImage(byte[] c, ref int pos, out bool exact, out PdfDictionary imageDict,
                                               ref string error, ref bool limitExceeded)
        {
            exact = false;
            imageDict = new PdfDictionary();

            // ---- 1. key/value pairs up to the ID keyword -------------------------
            while (true)
            {
                SkipWhitespaceAndComments(c, ref pos);
                if (pos >= c.Length)
                {
                    error = "Inline image ended before its ID keyword.";
                    return false;
                }

                if (c[pos] == PdfConstants.Solidus)
                {
                    PdfName key = ReadName(c, ref pos);
                    SkipWhitespaceAndComments(c, ref pos);
                    if (pos >= c.Length)
                    {
                        error = "Inline image ended inside its parameter dictionary.";
                        return false;
                    }
                    int kind = TryReadOperand(c, ref pos, 1, out PdfObject value, ref error, ref limitExceeded);
                    if (kind < 0) return false;
                    if (kind == 0)
                    {
                        error = "Unsupported inline-image parameter value at byte " +
                                pos.ToString(CultureInfo.InvariantCulture) + ".";
                        return false;
                    }
                    imageDict.Set(key, value);
                    continue;
                }

                if (IsNumberStart(c[pos])) { ReadNumber(c, ref pos); continue; }   // stray operand, ignore

                string keyword = ReadKeyword(c, ref pos);
                if (keyword.Length == 0)
                {
                    error = "Malformed inline-image header at byte " + pos.ToString(CultureInfo.InvariantCulture) + ".";
                    return false;
                }
                if (string.Equals(keyword, PdfOperators.InlineImageData, StringComparison.Ordinal)) break;
                // Any other keyword before ID is junk from a broken producer; keep looking.
            }

            // ---- 2. exactly one whitespace byte separates ID from the payload ----
            if (pos < c.Length && PdfConstants.IsWhitespace(c[pos])) pos++;
            int dataStart = pos;

            // ---- 3a/3b/3c. exact length, when the image supplies one --------------
            // InlineImageLength lives in Core so this scanner and Core's ContentStreamParser
            // cannot disagree about where an image ends — a disagreement is what the tagger's
            // cross-check exists to detect, and the check is useless if both sides implement the
            // same mistake. A declared /L is authoritative; failing that, a self-delimiting
            // outermost filter (/AHx, /A85, /RL, /DCT) puts an end-of-data marker in the payload
            // itself; failing that, an unfiltered image's byte count follows from /W, /H, /BPC and
            // /CS alone. A length the data contradicts is worse than none, so every miss falls
            // through — and the three are tried here in the same order as in the parser.
            if (TryTerminatorAt(c, dataStart, InlineImageLength.GetDeclared(imageDict), ref pos))
            {
                exact = true;
                return true;
            }
            if (TryTerminatorAt(c, dataStart, InlineImageLength.ComputeSelfDelimiting(imageDict, c, dataStart), ref pos))
            {
                exact = true;
                return true;
            }
            if (TryTerminatorAt(c, dataStart, InlineImageLength.ComputeUnfiltered(imageDict), ref pos))
            {
                exact = true;
                return true;
            }
            // Fourth, and last of the exact answers: a Flate or LZW payload has neither an
            // end-of-data marker nor a geometry-derived ENCODED size, so the only way to be sure is
            // to decode a candidate and check it produced the sample count the geometry demands.
            // Without this, such an image would always fall to the heuristic below and make the page
            // unspliceable.
            if (TryTerminatorAt(c, dataStart, InlineImageLength.ComputeByDecoding(imageDict, c, dataStart), ref pos))
            {
                exact = true;
                return true;
            }

            // ---- 3c. heuristic scan (a guess — reported through `exact: false`) --
            for (int p = dataStart; p + 1 < c.Length; p++)
            {
                if (c[p] != (byte)'E' || c[p + 1] != (byte)'I') continue;
                if (p == 0 || !PdfConstants.IsWhitespace(c[p - 1])) continue;
                if (p + 2 < c.Length && !PdfConstants.IsEndOfToken(c[p + 2])) continue;

                bool looksBinary = false;
                int limit = Math.Min(c.Length, p + 6);
                for (int k = p + 2; k < limit; k++)
                {
                    if (c[k] > 0x7F) { looksBinary = true; break; }
                }
                if (looksBinary) continue;   // "EI" inside binary payload, not the terminator

                pos = p + 2;
                return true;
            }

            error = "Inline image starting at byte " + dataStart.ToString(CultureInfo.InvariantCulture) +
                    " has no locatable EI terminator.";
            return false;
        }

        /// <summary>
        /// Checks whether a payload of exactly <paramref name="length"/> bytes starting at
        /// <paramref name="dataStart"/> is followed by the <c>EI</c> terminator. On success
        /// <paramref name="pos"/> is advanced past <c>EI</c>. A negative length means "unknown".
        /// </summary>
        private static bool TryTerminatorAt(byte[] c, int dataStart, int length, ref int pos)
        {
            if (length < 0) return false;
            if (dataStart > c.Length - length) return false;   // overflow-safe bounds test

            int p = dataStart + length;
            while (p < c.Length && PdfConstants.IsWhitespace(c[p])) p++;

            if (p + 1 >= c.Length || c[p] != (byte)'E' || c[p + 1] != (byte)'I') return false;
            if (p + 2 < c.Length && !PdfConstants.IsEndOfToken(c[p + 2])) return false;

            pos = p + 2;
            return true;
        }

    }
}
