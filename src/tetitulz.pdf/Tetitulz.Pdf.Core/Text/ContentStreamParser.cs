// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Tokenises a decoded content stream into <see cref="ContentStreamOperation"/>s, including
    /// correct inline-image boundary handling (see the comment on the <c>BI</c> case).
    ///
    /// <para><b>Public because a plugin needs it.</b> Content-level conformance checks — PDF/X
    /// colour rules, for one — cannot be answered from dictionaries alone, and a plugin assembly
    /// cannot see Core's internals. The alternatives were an <c>InternalsVisibleTo</c> for a plugin
    /// (which would establish "plugins reach into Core internals") or a fourth hand-rolled
    /// tokenizer, and inline-image boundary detection is exactly the code that must not be
    /// reimplemented per caller. Same reasoning, and same precedent, as
    /// <see cref="Tetitulz.Pdf.Core.Fonts.Standard14Metrics"/>.</para>
    ///
    /// <para><b>Unbounded by design — the caller must bound the input.</b> <see cref="Parse"/>
    /// materialises one operation per operator, so a caller feeding it an arbitrarily large
    /// decoded stream pays for it in memory. Check the byte count before calling. This is stated
    /// rather than enforced because Base's existing callers rely on the current behaviour, and a
    /// cap here would change it silently.</para>
    /// </summary>
    public sealed class ContentStreamParser
    {
        /// <summary>The default of <see cref="MaxInlineImageBytes"/>: 256 MiB.</summary>
        internal const int DefaultMaxInlineImageBytes = 256 * 1024 * 1024;

        /// <summary>
        /// The most bytes an inline image's data is scanned for its <c>EI</c> before the parse is
        /// refused with a <see cref="PdfParseException"/>. Process-global, read at each parse; a test
        /// that lowers it restores <see cref="DefaultMaxInlineImageBytes"/> in a <c>finally</c>.
        /// </summary>
        internal static int MaxInlineImageBytes { get; set; } = DefaultMaxInlineImageBytes;

        // ISO-8859-1 (Latin-1) round-trips raw PDF string bytes 1:1.  Cache one instance —
        // Encoding.GetEncoding does a provider lookup, and a content stream can hold many
        // literal-string operands, so re-resolving it per token is wasteful.
        // Superseded by Latin1Text.Encode, which is a loop: nothing to look up, nothing to cache,
        // and no type initializer on a type a content stream touches per literal-string operand.

        // The operator set lives in ContentStreamOperators, shared with the accessibility feature's
        // scanner, which ran the same greedy split against its own hand-maintained copy.
        //
        // This copy was missing ' and " until they were found: an undecomposable run is emitted
        // verbatim, so a producer that glues an inline image onto one ("'BI") yielded the single
        // unknown operator "'BI", the BI was never recognised, and the image's samples were
        // tokenised as operators.  It was still missing ID, and BOTH copies were still missing PS —
        // which both renderers dispatch a handler for.  Neither gap was visible from inside either
        // file, because neither copy had ever been compared against PdfOperators.  It is now, by a
        // test, which is the only thing that keeps a hand-maintained list honest.

        /// <summary>
        /// If <paramref name="token"/> is not a known operator, tries to split it
        /// greedily into a sequence of known operators (longest match from left).
        /// Returns the sequence (single element if no split is needed or possible).
        ///
        /// <para><b>Partial splits are allowed here on purpose</b>, and that is the one way this
        /// differs from the accessibility scanner's all-or-nothing splitter: an unrecognised tail
        /// is yielded verbatim rather than abandoning the whole token, so the operators that
        /// <i>were</i> recognised still reach the renderer. Both call the same
        /// <see cref="ContentStreamOperators"/> set; only the failure policy differs.</para>
        /// </summary>
        /// <remarks>
        /// Into a list the caller reuses for every keyword, not an iterator each: a known operator —
        /// nearly every keyword — is the list of itself, and an iterator per operation was 60 MB of a
        /// million-operation parse (GAPS.md §13b).
        /// </remarks>
        private static void SplitOps(string token, List<string> into)
        {
            into.Clear();
            if (ContentStreamOperators.Contains(token)) { into.Add(token); return; }

            int pos = 0;
            while (pos < token.Length)
            {
                // Longest match wins, starting from the longest operator that exists.
                bool found = false;
                for (int len = System.Math.Min(ContentStreamOperators.MaxLength, token.Length - pos); len >= 1; len--)
                {
                    string sub = token.Substring(pos, len);
                    if (ContentStreamOperators.Contains(sub))
                    {
                        into.Add(sub);
                        pos += len;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Cannot split further — keep the remaining unrecognised part.
                    into.Add(token.Substring(pos));
                    return;
                }
            }
        }
        public static List<ContentStreamOperation> Parse(byte[] data)
        {
            var operations = new List<ContentStreamOperation>();
            if (data == null || data.Length == 0)
                return operations;

            // Over the array itself, and through the tokenizer's own lexing without a PdfToken per
            // token: a number arrives as its value, a string as its bytes (GAPS.md §13b).
            {
                var tokenizer = new PdfTokenizer(data);
                var operands = new List<PdfObject>();
                var subOps = new List<string>(4);

                while (true)
                {
                    tokenizer.SkipWhitespaceAndComments();
                    if (tokenizer.Position >= tokenizer.Length)
                        break;

                    PdfTokenizer.Lexeme token;
                    try
                    {
                        token = tokenizer.Lex();
                    }
                    catch
                    {
                        break;
                    }

                    if (token.Type == PdfTokenType.Eof)
                        break;

                    switch (token.Type)
                    {
                        // The eight token types that are OPERANDS, each converted by the one
                        // token → PdfObject switch — no copy of that conversion here (see the
                        // remarks on ParseInlineValue).
                        //
                        // Which types are operands stays HERE on purpose: that is a different
                        // question from what object a token becomes, and the answer differs.
                        // Comment, ArrayEnd, DictionaryEnd and StreamData reach this switch and are
                        // ignored, where ParseInlineValue's `default` would make them PdfNull —
                        // right for a value position, wrong for an operand list, where a stray `]`
                        // would otherwise become an extra operand and shift every index after it.
                        case PdfTokenType.Number:
                        case PdfTokenType.Name:
                        case PdfTokenType.LiteralString:
                        case PdfTokenType.HexString:
                        case PdfTokenType.Boolean:
                        case PdfTokenType.Null:
                        case PdfTokenType.ArrayStart:
                        case PdfTokenType.DictionaryStart:
                            operands.Add(ParseInlineValue(tokenizer, token));
                            break;

                        case PdfTokenType.Keyword:
                            // Some PDF generators omit whitespace between operators
                            // (e.g. "DoQQ" for "Do", "Q", "Q").  Split concatenated
                            // operator tokens into individual known operators.
                            // The first sub-operator receives the pending operands;
                            // subsequent sub-operators get no operands (they are
                            // zero-operand operators like Q, q, etc.).
                            //
                            // The inline-image test MUST happen per sub-operator, not on the
                            // whole keyword: "BI" is glued to its predecessor just as often as
                            // any other operator ("qBI"), and comparing token.Value to "BI"
                            // misses that entirely.  The parser would then tokenise the image's
                            // raw sample bytes as operands and operators — inventing text that
                            // the page never draws and executing operators the document never
                            // contained.  This mirrors ContentStreamScanner.FindInlineImageStart in
                            // the accessibility feature; the two must agree about where an image
                            // begins, because the tagger's cross-check is worthless if both sides
                            // make the same mistake.
                            bool firstSubOp = true;
                            int subOpOffset = 0;   // byte offset of subOp inside the keyword
                            SplitOps(token.Text, subOps);
                            foreach (string subOp in subOps)
                            {
                                if (subOp == PdfOperators.BeginInlineImage)
                                {
                                    // Rewind to just past this BI: the keyword reader may have
                                    // consumed bytes that belong to the image header rather than
                                    // to the operator stream ("BIID" is BI followed by the ID of a
                                    // parameter-less image, not the operators BI and ID).  Keyword
                                    // bytes are one byte per char, so the char offset is the byte
                                    // offset, and token.Position is the keyword's first byte.
                                    tokenizer.Position = token.Position + subOpOffset
                                                       + PdfOperators.BeginInlineImage.Length;

                                    // Everything from here to EI is image data, so no later
                                    // sub-operator of this keyword can be a real operator.
                                    var inlineOp = ParseInlineImageOp(tokenizer, data);
                                    if (inlineOp != null) operations.Add(inlineOp);
                                    break;
                                }

                                var op = firstSubOp
                                    ? new ContentStreamOperation(subOp, operands.ToArray())
                                    : new ContentStreamOperation(subOp, System.Array.Empty<PdfObject>());
                                operations.Add(op);
                                firstSubOp = false;
                                subOpOffset += subOp.Length;
                            }
                            operands.Clear();
                            break;
                    }
                }
            }

            return operations;
        }

        /// <summary>
        /// One already-consumed token → one PDF object, for <b>every</b> value position a content
        /// stream has: an inline image's parameter dictionary, a marked-content property
        /// dictionary, and the elements of either's arrays. <paramref name="token"/> must already
        /// have been taken from <paramref name="tokenizer"/>, which is left positioned after the
        /// value — past the matching <c>]</c> or <c>&gt;&gt;</c> for a composite one.
        /// </summary>
        /// <remarks>
        /// <para><b>One switch for the operand loop, arrays and inline images</b>, because copies each
        /// miss something different and a defect fixed in two stays live in the third. An array copy
        /// with no <c>Boolean</c> case loses an element at <c>[true]</c> and shifts every later index.
        /// An inline-image copy with no <c>DictionaryStart</c> stores <c>/DP</c> — a
        /// <i>dictionary</i> — as <c>PdfNull</c> while the token stream desynchronises and dumps
        /// <c>/K</c>, <c>/Columns</c>, <c>/Rows</c>, <c>/Predictor</c> into the <b>image</b>
        /// dictionary as top-level keys; a CCITT inline image then decodes at the filter's own
        /// 1728-column default instead of its <c>/Columns</c>, which fails, and
        /// <c>CcittFaxDecodeFilter</c> hands back its compressed input for the 1-bpc path to paint
        /// as samples.</para>
        ///
        /// <para>The dictionary copy is the one that was complete, and its history is why: the
        /// string and composite cases were added to it after `/Span &lt;&lt;/ActualText(fi)&gt;&gt;
        /// BDC` was found losing its text and `/OC &lt;&lt;/Type/OCMD/OCGs[5 0 R]&gt;&gt; BDC` was
        /// found drawing hidden content — a value token consumed with its body left in the stream
        /// desynchronises everything after it, so the operator arrives with no properties at all.
        /// Both fixes landed in one copy of three.</para>
        ///
        /// <para>The operand loop of <c>Parse</c> calls this too. The loop keeps its own list of <i>which</i> token types are
        /// operands, because that is a different question from what object a token becomes: the
        /// <c>default</c> below maps an unrecognised token to <c>PdfNull</c>, which is right in a
        /// value position and wrong in an operand list, where a stray <c>]</c> would shift every
        /// index after it.</para>
        ///
        /// <para><b>Two of this switch's decisions are invisible to every present consumer, and a
        /// mutation removing either survives on purpose.</b> Recorded so a later run reads them as
        /// this rather than as missing coverage:</para>
        /// <list type="bullet">
        ///   <item><description>The <c>Contains(".")</c> integer/real split is <b>equivalent</b> for
        ///   every value in range: <c>PdfNumber(double)</c> recomputes <c>IsInteger</c> from the
        ///   value itself, so <c>72</c> and <c>72.0</c> produce the same object, the same
        ///   <c>IntValue</c> and the same <c>ToString</c>. The two readings can differ only for an
        ///   integer token too large for a <c>long</c>.</description></item>
        ///   <item><description><c>PdfString.IsHex</c> is read only by <c>PdfWriter</c>, to choose
        ///   <c>&lt;…&gt;</c> over <c>(…)</c>, and a content-stream operand is never written back
        ///   through it. The flag is carried for fidelity, not consumed.</description></item>
        /// </list>
        /// </remarks>
        /// <param name="tokenizer">The content stream, positioned after <paramref name="token"/>.</param>
        /// <param name="token">The token that starts the value.</param>
        /// <param name="depth">
        /// How many arrays and dictionaries enclose the value. A <c>[</c> or <c>&lt;&lt;</c> met at
        /// <see cref="PdfLimits.MaxObjectGraphDepth"/> is read as <see cref="PdfNull"/> and its
        /// contents become the enclosing container's items: the recursion below was unbounded, and a
        /// content stream of a few hundred thousand <c>[</c> — a few hundred kilobytes, deflated to
        /// nothing — overflowed the stack, which no <c>catch</c> in this library or its host sees.
        /// The same cap bounds the object parser's nesting, so a content stream cannot nest deeper
        /// than the file around it.
        /// </param>
        private static PdfObject ParseInlineValue(PdfTokenizer tokenizer, PdfTokenizer.Lexeme token, int depth = 0)
        {
            switch (token.Type)
            {
                case PdfTokenType.Number:
                    // The lexeme's value is the token's own reading of its text (PdfTokenizer.Lex):
                    // a decimal point makes a real, anything else an integer.
                    return token.IsReal
                        ? new PdfNumber(token.Real)
                        : (PdfObject)new PdfNumber(token.Integer);
                case PdfTokenType.Name:
                    return new PdfName(token.Text, token.NameIsNotUtf8);
                case PdfTokenType.Boolean:
                    return token.Text == "true" ? PdfBoolean.True : PdfBoolean.False;
                case PdfTokenType.LiteralString:
                    return new PdfString(token.Bytes);
                case PdfTokenType.HexString:
                    return new PdfString(token.Bytes, true);
                case PdfTokenType.ArrayStart:
                    return depth >= PdfLimits.MaxObjectGraphDepth ? PdfNull.Instance : ParseInlineArray(tokenizer, depth + 1);
                case PdfTokenType.DictionaryStart:
                    return depth >= PdfLimits.MaxObjectGraphDepth ? PdfNull.Instance : ParseInlineDictionary(tokenizer, depth + 1);
                default:
                    // Includes PdfTokenType.Null, which maps to the same instance the unknown
                    // cases fall back to.
                    return PdfNull.Instance;
            }
        }

        private static PdfArray ParseInlineArray(PdfTokenizer tokenizer, int depth)
        {
            // Each element read once. A peek followed by a read lexed every element twice; the peek
            // decided only what the read then confirms, so the read decides it here.
            var array = new PdfArray();
            while (true)
            {
                var token = tokenizer.Lex();
                if (token.Type == PdfTokenType.ArrayEnd || token.Type == PdfTokenType.Eof)
                    break;

                array.Add(ParseInlineValue(tokenizer, token, depth));
            }
            return array;
        }

        private static PdfDictionary ParseInlineDictionary(PdfTokenizer tokenizer, int depth)
        {
            var dict = new PdfDictionary();
            while (true)
            {
                var keyToken = tokenizer.Lex();
                if (keyToken.Type == PdfTokenType.DictionaryEnd || keyToken.Type == PdfTokenType.Eof) break;
                if (keyToken.Type != PdfTokenType.Name) break;

                dict.Set(new PdfName(keyToken.Text, keyToken.NameIsNotUtf8), ParseInlineValue(tokenizer, tokenizer.Lex(), depth));
            }
            return dict;
        }

        /// <summary>
        /// Parses a BI...ID...EI inline image block starting after the BI keyword.
        /// Returns a "BI" operation whose single operand is a PdfStream containing
        /// the image-parameter dictionary entries and the raw pixel bytes.
        /// </summary>
        /// <param name="tokenizer">Positioned just past the <c>BI</c> keyword.</param>
        /// <param name="data">
        /// The array the tokenizer's <see cref="MemoryStream"/> wraps, so a tokenizer position is an
        /// index into it. Passed through only so a self-delimiting filter's end-of-data marker can be
        /// located without reading the payload back out of the stream a byte at a time.
        /// </param>
        private static ContentStreamOperation ParseInlineImageOp(PdfTokenizer tokenizer, byte[] data)
        {
            // 1. Parse key/value pairs (abbreviated or full names) until "ID" keyword.
            var imgDict = new PdfDictionary();
            while (true)
            {
                tokenizer.SkipWhitespaceAndComments();
                var keyToken = tokenizer.Lex();
                if (keyToken.Type == PdfTokenType.Eof) return null;
                if (keyToken.Type == PdfTokenType.Keyword && keyToken.Text == PdfOperators.InlineImageData) break;
                if (keyToken.Type != PdfTokenType.Name) continue;

                // Through the one value parser — /DP is a dictionary and this copy had no case for
                // one, so decode parameters were dropped and their keys leaked into the image.
                imgDict.Set(new PdfName(keyToken.Text, keyToken.NameIsNotUtf8),
                            ParseInlineValue(tokenizer, tokenizer.Lex()));
            }

            // 2. Skip the single whitespace byte after "ID".
            {
                long pos = tokenizer.Position;
                if (pos < tokenizer.Length)
                {
                    byte[] b = tokenizer.ReadRawBytes(pos, 1);
                    if (b.Length > 0 && PdfConstants.IsWhitespace(b[0]))
                        tokenizer.Position = pos + 1;
                }
            }

            // 3a. Exact payload length, when the image supplies one.
            //
            // Three ways to know rather than guess, and they cover disjoint files: a declared /L is
            // authoritative; an UNFILTERED image's byte count follows from /W, /H, /BPC and /CS
            // alone; and a filtered one whose outermost filter is self-delimiting (/AHx, /A85, /RL,
            // /DCT) carries its own end-of-data marker.  Each settles the boundary with no guessing,
            // which matters because the heuristic below is provably wrong on real files: a dark
            // grayscale image has no byte above 0x7F anywhere, and an ASCII85 payload is printable
            // ASCII by construction, so either can contain a whitespace-delimited "EI" followed by
            // printable ASCII and end the image early — after which every byte of the remaining
            // samples is tokenised as operators.
            //
            // All four go through Core's InlineImageLength, which the accessibility feature's
            // ContentStreamScanner calls in the same order.  The two MUST agree about where an image
            // ends: the tagger cross-checks this parser against that scanner, and the check is
            // worthless if one side knows a boundary the other has to guess at.
            {
                int exact = InlineImageLength.GetDeclared(imgDict);
                if (!TryTakeExactPayload(tokenizer, imgDict, exact, out PdfStream exactStream))
                {
                    exact = InlineImageLength.ComputeSelfDelimiting(imgDict, data, (int)tokenizer.Position);
                    if (!TryTakeExactPayload(tokenizer, imgDict, exact, out exactStream))
                    {
                        exact = InlineImageLength.ComputeUnfiltered(imgDict);
                        if (!TryTakeExactPayload(tokenizer, imgDict, exact, out exactStream))
                        {
                            // Fourth: a Flate or LZW payload, verified by decoding a candidate and
                            // checking it yields the sample count the geometry demands. Added on
                            // BOTH sides in the same change, for the reason stated above - a
                            // boundary one side knows and the other guesses at is exactly what the
                            // tagger's cross-check reports as a disagreement.
                            exact = InlineImageLength.ComputeByDecoding(
                                imgDict, data, (int)tokenizer.Position);
                            TryTakeExactPayload(tokenizer, imgDict, exact, out exactStream);
                        }
                    }
                }

                if (exactStream != null)
                    return new ContentStreamOperation(PdfOperators.BeginInlineImage, new PdfObject[] { exactStream });
            }

            // 3b. Read raw pixel bytes until the sequence (whitespace)(E)(I), at most
            // MaxInlineImageBytes of them: a PDF content stream with BI ... ID ... (no valid EI
            // marker) and gigabytes of binary data would otherwise drive the scanner until EOF,
            // pinning memory in the process.
            // Scanned over `data` directly rather than one tokenizer read per byte.
            //
            // `tokenizer.ReadRawBytes(pos, 1)` per byte would allocate a `byte[1]` AND save, seek
            // and restore the stream position for each call — a 4 MB inline image costing four
            // million one-byte arrays to read bytes the caller already has in hand.
            //
            // `data` IS the tokenizer's stream: `Parse` — the only caller — builds it as
            // `new MemoryStream(data)`, so the position indexes this array exactly. Step 3a above
            // already relies on the same identity when it hands `data` and `tokenizer.Position`
            // together to `InlineImageLength.ComputeSelfDelimiting`.
            //
            // The state machine is unchanged: bytes are accumulated with a one-byte lag, a
            // candidate `EI` is rejected when a byte above 0x7F follows within four (that is
            // binary sample data, not the operator), and a genuine one drops the whitespace that
            // preceded the `E`. Tracking the payload's END INDEX rather than copying into a list
            // is what removes the per-byte work; the slice is taken once at the end.
            int scanStart = (int)tokenizer.Position;
            int limit = data.Length;
            int payloadEnd = -1;     // exclusive; -1 until an EI settles it
            int prevPrev = -1, prev = -1;
            int scan = scanStart;

            for (; scan < limit; scan++)
            {
                // A content stream with BI ... ID ... and no valid EI marker would otherwise scan to
                // EOF. A refusal of the file's own content, so a PdfException, as every caller expects.
                if (scan - scanStart > MaxInlineImageBytes)
                    throw new PdfParseException(
                        $"Inline image exceeds {MaxInlineImageBytes} bytes without EI terminator");

                int cur = data[scan];

                if (prev == 'E' && cur == 'I' && prevPrev >= 0 && PdfConstants.IsWhitespace((byte)prevPrev))
                {
                    // The bytes following EI must be printable ASCII. A high byte immediately
                    // after means this is EI embedded in binary image data (a JPEG SOF marker,
                    // say), not the operator terminator — keep scanning, as lenient readers do
                    // Parser.findDefaultInlineStreamEnd()'s followingBytes check.
                    bool isBinaryContext = false;
                    int peekEnd = System.Math.Min(scan + 1 + 4, limit);
                    for (int k = scan + 1; k < peekEnd; k++)
                    {
                        if (data[k] > 0x7F) { isBinaryContext = true; break; }
                    }

                    if (!isBinaryContext)
                    {
                        // Genuine EI. The payload runs to the byte before the whitespace that
                        // precedes the E, so the exclusive end is two back from the I.
                        payloadEnd = scan - 2;
                        if (payloadEnd < scanStart) payloadEnd = scanStart;
                        scan++;                       // leave the position just past the I
                        break;
                    }
                    // False positive: fall through so the E is kept and I becomes the new prev.
                }

                prevPrev = prev;
                prev = cur;
            }

            if (payloadEnd < 0)
            {
                // Ran to EOF with no terminator. The payload stops one short of the end, kept so
                // rather than quietly corrected: an unterminated inline image is already malformed and
                // changing where it ends would move every caller's samples by one.
                payloadEnd = limit - 1;
                if (payloadEnd < scanStart) payloadEnd = scanStart;
                scan = limit;
            }

            tokenizer.Position = scan;

            var imageBytes = new byte[payloadEnd - scanStart];
            if (imageBytes.Length > 0)
                System.Buffer.BlockCopy(data, scanStart, imageBytes, 0, imageBytes.Length);

            // 4. Build PdfStream from dict entries + raw bytes.
            var stream = new PdfStream();
            foreach (var kvp in imgDict) stream[kvp.Key] = kvp.Value;
            stream.RawData = imageBytes;

            return new ContentStreamOperation(PdfOperators.BeginInlineImage, new PdfObject[] { stream });
        }

        /// <summary>
        /// Consumes exactly <paramref name="length"/> payload bytes and the <c>EI</c> that must
        /// follow them, building the image stream. Returns false — leaving the tokenizer position
        /// untouched — when the length is unknown, runs past the end of the stream, or is not
        /// followed by <c>EI</c>; a length the data contradicts is worse than no length at all,
        /// so the caller falls through to the heuristic rather than trusting it.
        /// </summary>
        private static bool TryTakeExactPayload(
            PdfTokenizer tokenizer, PdfDictionary imgDict, int length, out PdfStream stream)
        {
            stream = null;
            if (length < 0) return false;

            long dataStart = tokenizer.Position;
            if (length > tokenizer.Length - dataStart) return false;

            long p = dataStart + length;

            // Skip the whitespace that separates the samples from EI.
            while (p < tokenizer.Length)
            {
                byte[] one = tokenizer.ReadRawBytes(p, 1);
                if (one.Length == 0 || !PdfConstants.IsWhitespace(one[0])) break;
                p++;
            }

            byte[] terminator = tokenizer.ReadRawBytes(p, 2);
            if (terminator.Length < 2 || terminator[0] != (byte)'E' || terminator[1] != (byte)'I') return false;

            // "EI" must end at a token boundary, or this is "EIx" inside the data.
            if (p + 2 < tokenizer.Length)
            {
                byte[] after = tokenizer.ReadRawBytes(p + 2, 1);
                if (after.Length > 0 && !PdfConstants.IsEndOfToken(after[0])) return false;
            }

            stream = new PdfStream();
            foreach (var kvp in imgDict) stream[kvp.Key] = kvp.Value;
            stream.RawData = length == 0
                ? System.Array.Empty<byte>()
                : tokenizer.ReadRawBytes(dataStart, length);

            tokenizer.Position = p + 2;
            return true;
        }
    }
}
