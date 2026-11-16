// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;

namespace Tetitulz.Pdf.Core.IO
{
    public sealed class PdfTokenizer
    {
        private readonly Stream _stream;
        private readonly byte[] _singleByte = new byte[1];

        // Array mode: the content-stream parser holds its data as an array, and reading it through a
        // MemoryStream cost a virtual Read call (and a copy into _singleByte) per byte. With an array
        // the position is an index; every other member behaves as it does over a stream.
        private readonly byte[] _data;
        private readonly int _dataLength;
        private long _pos;

        public PdfTokenizer(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>A tokenizer over <paramref name="data"/> itself, positioned at its start.</summary>
        internal PdfTokenizer(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            _dataLength = data.Length;
        }

        /// <summary>One position back — the byte <see cref="ReadByte"/> just returned is unread.</summary>
        private void Back()
        {
            if (_data != null) _pos--;
            else _stream.Position--;
        }

        // ---------------------------------------------------------------- the lexeme

        /// <summary>
        /// A token as the content-stream parser takes it: a number's value rather than its digits, a
        /// string's bytes rather than their Latin-1 reading, and no object. <see cref="NextToken"/> is
        /// the same lexing spelled out as a <see cref="PdfToken"/>, so the two cannot read a byte
        /// differently.
        /// </summary>
        /// <remarks>
        /// The content-stream parser built a <see cref="PdfToken"/> for every token, spelled every
        /// number as a string and parsed it back, and decoded every string to text and encoded it to
        /// bytes again — 106 MB of a 209 MB parse of a hundred thousand path lines (GAPS.md §13b).
        /// </remarks>
        internal struct Lexeme
        {
            internal PdfTokenType Type;
            internal long Position;
            /// <summary>A name's, keyword's, boolean's or null's text; a number's or string's only when spelled.</summary>
            internal string Text;
            internal bool NameIsNotUtf8;
            /// <summary>A number with a decimal point (<see cref="Real"/>), else an integer (<see cref="Integer"/>).</summary>
            internal bool IsReal;
            internal long Integer;
            internal double Real;
            /// <summary>A literal or hex string's bytes, when not spelled.</summary>
            internal byte[] Bytes;
        }

        private static Lexeme Lexed(PdfTokenType type, string text, long position)
            => new Lexeme { Type = type, Text = text, Position = position };

        // ---------------------------------------------------------------- the token text

        /// <summary>
        /// The text of the token being read, one builder for every token rather than one each: a
        /// page of a million operations allocated a <see cref="StringBuilder"/> and its buffer per
        /// token, 284 MB of a 706 MB parse (GAPS.md §13b). A token's own string is still its own.
        /// </summary>
        private readonly StringBuilder _text = new StringBuilder(64);

        /// <summary>A name's bytes, reused as the text builder is.</summary>
        private byte[] _nameBytes = new byte[32];

        /// <summary>Past this a buffer one long token grew is let go, so a tokenizer does not hold a large string's worth for its life.</summary>
        private const int MaxRetainedTextChars = 4096;

        private StringBuilder BeginText()
        {
            _text.Clear();
            return _text;
        }

        private string EndText()
        {
            string text = _text.ToString();
            Release();
            return text;
        }

        private void Release()
        {
            if (_text.Capacity <= MaxRetainedTextChars) return;
            _text.Clear();
            _text.Capacity = 64;
        }

        /// <summary>
        /// A keyword's text: the one string of a known keyword — every content-stream operator,
        /// <c>true</c>, <c>false</c>, <c>null</c> and the file structure's own — rather than a new
        /// copy for each, and a new string for any other.
        /// </summary>
        private string EndKeyword()
        {
            if (KnownKeywords.TryGetValue(KeywordKey(_text), out string known)) return known;
            return EndText();
        }

        /// <summary>The capacity the text builder keeps between tokens — for the tests of its release.</summary>
        internal int RetainedTextCapacity => _text.Capacity;

        /// <summary>The length of the name buffer kept between tokens — for the tests of its reuse.</summary>
        internal int RetainedNameCapacity => _nameBytes.Length;

        /// <summary>
        /// The keyword's characters packed seven bits each, or −1 when it cannot be a known one
        /// (longer than nine, or a character above 127). A NUL is white space (§7.2.3), never in a
        /// keyword, so the packing needs no length: a shorter keyword's leading bits are zero.
        /// </summary>
        private static long KeywordKey(StringBuilder text)
        {
            if (text.Length == 0 || text.Length > 9) return -1;
            long key = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c > 127) return -1;
                key = (key << 7) | c;
            }
            return key;
        }

        private static long KeywordKey(string text)
        {
            var sb = new StringBuilder(text);
            return KeywordKey(sb);
        }

        private static readonly System.Collections.Generic.Dictionary<long, string> KnownKeywords = BuildKnownKeywords();

        private static System.Collections.Generic.Dictionary<long, string> BuildKnownKeywords()
        {
            var table = new System.Collections.Generic.Dictionary<long, string>();
            void Add(string keyword)
            {
                long key = KeywordKey(keyword);
                if (key >= 0) table[key] = keyword;
            }
            foreach (string op in Text.ContentStreamOperators.All) Add(op);
            foreach (string keyword in new[] { "true", "false", "null", "obj", "endobj", "stream", "endstream", "R", "xref", "trailer", "startxref", "n", "f" })
                Add(keyword);
            return table;
        }

        public long Position
        {
            get => _data != null ? _pos : _stream.Position;
            set
            {
                if (_data == null) { _stream.Position = value; return; }
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));   // as MemoryStream refuses
                _pos = value;
            }
        }

        public long Length => _data != null ? _dataLength : _stream.Length;

        public void Seek(long position)
        {
            Position = position;
        }

        private int ReadByte()
        {
            if (_data != null) return _pos < _dataLength ? _data[_pos++] : -1;
            int read = _stream.Read(_singleByte, 0, 1);
            return read == 0 ? -1 : _singleByte[0];
        }

        private int PeekByte()
        {
            if (_data != null) return _pos < _dataLength ? _data[_pos] : -1;
            int b = ReadByte();
            if (b != -1)
                Back();
            return b;
        }

        public void SkipWhitespace()
        {
            while (true)
            {
                int b = ReadByte();
                if (b == -1) return;
                if (!PdfConstants.IsWhitespace((byte)b))
                {
                    Back();
                    return;
                }
            }
        }

        public void SkipWhitespaceAndComments()
        {
            while (true)
            {
                SkipWhitespace();
                int b = PeekByte();
                if (b == PdfConstants.Percent)
                {
                    SkipLine();
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipLine()
        {
            while (true)
            {
                int b = ReadByte();
                if (b == -1 || b == PdfConstants.LineFeed) return;
                if (b == PdfConstants.CarriageReturn)
                {
                    int next = PeekByte();
                    if (next == PdfConstants.LineFeed)
                        ReadByte();
                    return;
                }
            }
        }

        public PdfToken NextToken()
        {
            Lexeme l = Lex(spell: true);
            return new PdfToken(l.Type, l.Text, l.Position, l.NameIsNotUtf8);
        }

        /// <summary>
        /// The next token. <paramref name="spell"/> gives every token its text, as
        /// <see cref="NextToken"/> needs; without it a number carries its value and a string its bytes,
        /// which is all the content-stream parser takes.
        /// </summary>
        internal Lexeme Lex(bool spell = false)
        {
            SkipWhitespaceAndComments();

            long pos = Position;
            int b = ReadByte();
            if (b == -1)
                return Lexed(PdfTokenType.Eof, string.Empty, pos);

            byte ch = (byte)b;

            // Name object
            if (ch == PdfConstants.Solidus)
                return ReadName(pos);

            // Literal string
            if (ch == PdfConstants.LeftParen)
                return ReadLiteralString(pos, spell);

            // Hex string or dictionary start
            if (ch == PdfConstants.LessThan)
            {
                int next = PeekByte();
                if (next == PdfConstants.LessThan)
                {
                    ReadByte();
                    return Lexed(PdfTokenType.DictionaryStart, "<<", pos);
                }
                return ReadHexString(pos, spell);
            }

            // Dictionary end
            if (ch == PdfConstants.GreaterThan)
            {
                int next = PeekByte();
                if (next == PdfConstants.GreaterThan)
                {
                    ReadByte();
                    return Lexed(PdfTokenType.DictionaryEnd, ">>", pos);
                }
                // Lone '>' is malformed; treat as an empty hex string end (lenient).
                var empty = Lexed(PdfTokenType.HexString, string.Empty, pos);
                empty.Bytes = Array.Empty<byte>();
                return empty;
            }

            // Array start
            if (ch == PdfConstants.LeftBracket)
                return Lexed(PdfTokenType.ArrayStart, "[", pos);

            // Array end
            if (ch == PdfConstants.RightBracket)
                return Lexed(PdfTokenType.ArrayEnd, "]", pos);

            // Number or keyword
            if (PdfConstants.IsDigit(ch) || ch == (byte)'+' || ch == (byte)'-' || ch == (byte)'.')
                return ReadNumber(pos, ch, spell);

            // Regular keyword (true, false, null, obj, endobj, R, stream, endstream, xref, trailer, startxref, etc.)
            return ReadKeyword(pos, ch);
        }

        public PdfToken PeekToken()
        {
            long savedPos = Position;
            var token = NextToken();
            Position = savedPos;
            return token;
        }

        /// <summary>
        /// A name object (&#167;7.3.5), with its <c>#XX</c> escapes expanded.
        /// </summary>
        /// <remarks>
        /// <b>The bytes are accumulated as bytes and decoded once, by
        /// <see cref="Objects.PdfNameEscape.Decode(byte[], int)"/>.</b> Appending <c>(char)b</c> per byte is a
        /// Latin-1 reading, and the writer escapes from UTF-8 bytes, so a non-ASCII name would not
        /// survive being written and read back. Decoding per byte cannot work in place: a UTF-8 sequence spans several of them, so the decision belongs after the loop.
        /// </remarks>
        private Lexeme ReadName(long startPos)
        {
            byte[] buffer = _nameBytes;
            int length = 0;

            void Add(byte value)
            {
                if (length == buffer.Length) { System.Array.Resize(ref buffer, length * 2); if (buffer.Length <= MaxRetainedTextChars) _nameBytes = buffer; }
                buffer[length++] = value;
            }
            while (true)
            {
                int b = PeekByte();
                if (b == -1 || PdfConstants.IsWhitespace((byte)b) || PdfConstants.IsDelimiter((byte)b))
                    break;

                ReadByte();
                if (b == PdfConstants.NumberSign)
                {
                    int h1 = PeekByte();
                    if (h1 == -1 || PdfConstants.IsWhitespace((byte)h1) || PdfConstants.IsDelimiter((byte)h1))
                    {
                        // Bare '#' not followed by hex digits - emit literally (lenient).
                        Add((byte)'#');
                        break;
                    }
                    ReadByte();
                    int x1 = PdfConstants.HexToNibble((byte)h1);
                    if (x1 == -1)
                    {
                        // Invalid first hex digit - emit '#' + the char and continue.
                        Add((byte)'#');
                        Add((byte)h1);
                        continue;
                    }
                    int h2 = PeekByte();
                    int x2 = (h2 >= 0) ? PdfConstants.HexToNibble((byte)h2) : -1;
                    if (x2 == -1)
                    {
                        // Invalid second hex digit - emit '#' + first char, don't consume second.
                        Add((byte)'#');
                        Add((byte)h1);
                        continue;
                    }
                    ReadByte();
                    Add((byte)((x1 << 4) | x2));
                }
                else
                {
                    Add((byte)b);
                }
            }
            string value = Objects.PdfNameEscape.Decode(buffer, length, out bool notUtf8);
            var name = Lexed(PdfTokenType.Name, value, startPos);
            name.NameIsNotUtf8 = notUtf8;
            return name;
        }

        // ---------------------------------------------------------------- a string's bytes

        /// <summary>A literal or hex string's bytes, reused between strings as the text builder is.</summary>
        private byte[] _bytes = new byte[64];
        private int _byteCount;

        private void AddByte(int value)
        {
            if (_byteCount == _bytes.Length) Array.Resize(ref _bytes, _byteCount * 2);
            _bytes[_byteCount++] = (byte)value;
        }

        /// <summary>
        /// The string's bytes as the token wants them: their Latin-1 reading when spelled (which is
        /// what appending each byte as a <c>char</c> made), or a copy.
        /// </summary>
        private void FinishBytes(ref Lexeme lexeme, bool spell, bool hex)
        {
            if (spell)
                lexeme.Text = Text.Latin1Text.Decode(_bytes, 0, _byteCount);
            else if (hex)
                lexeme.Bytes = PdfConstants.HexDigitsToBytes(_bytes, _byteCount);
            else
            {
                var copy = new byte[_byteCount];
                Buffer.BlockCopy(_bytes, 0, copy, 0, _byteCount);
                lexeme.Bytes = copy;
            }
            if (_bytes.Length > MaxRetainedTextChars) _bytes = new byte[64];
        }

        private Lexeme ReadLiteralString(long startPos, bool spell)
        {
            _byteCount = 0;
            int depth = 1;
            while (depth > 0)
            {
                int b = ReadByte();
                if (b == -1)
                    break; // Unterminated string: return what we have (lenient).

                if (b == PdfConstants.LeftParen)
                {
                    depth++;
                    AddByte(b);
                }
                else if (b == PdfConstants.RightParen)
                {
                    depth--;
                    if (depth > 0)
                        AddByte(b);
                }
                else if (b == PdfConstants.Backslash)
                {
                    int escaped = ReadByte();
                    if (escaped == -1)
                        throw new PdfParseException("Unterminated escape in literal string", startPos);
                    switch (escaped)
                    {
                        case 'n': AddByte('\n'); break;
                        case 'r': AddByte('\r'); break;
                        case 't': AddByte('\t'); break;
                        case 'b': AddByte('\b'); break;
                        case 'f': AddByte('\f'); break;
                        case '(': AddByte('('); break;
                        case ')': AddByte(')'); break;
                        case '\\': AddByte('\\'); break;
                        case '\r':
                            // line continuation: \<CR> or \<CR><LF>
                            if (PeekByte() == PdfConstants.LineFeed)
                                ReadByte();
                            break;
                        case '\n':
                            // line continuation: \<LF>
                            break;
                        default:
                            // Octal escape
                            if (escaped >= '0' && escaped <= '7')
                            {
                                int octal = escaped - '0';
                                int next = PeekByte();
                                if (next >= '0' && next <= '7')
                                {
                                    ReadByte();
                                    octal = (octal << 3) | (next - '0');
                                    next = PeekByte();
                                    if (next >= '0' && next <= '7')
                                    {
                                        ReadByte();
                                        octal = (octal << 3) | (next - '0');
                                    }
                                }
                                AddByte(octal & 0xFF);
                            }
                            else
                            {
                                AddByte(escaped);
                            }
                            break;
                    }
                }
                else
                {
                    AddByte(b);
                }
            }
            var literal = Lexed(PdfTokenType.LiteralString, null, startPos);
            FinishBytes(ref literal, spell, hex: false);
            return literal;
        }

        private Lexeme ReadHexString(long startPos, bool spell)
        {
            _byteCount = 0;
            while (true)
            {
                int b = ReadByte();
                if (b == -1)
                    break; // unterminated — return what we have (lenient)
                if (b == PdfConstants.GreaterThan)
                    break;
                if (PdfConstants.IsWhitespace((byte)b))
                    continue;
                if (!PdfConstants.IsHexDigit((byte)b))
                    continue; // skip invalid bytes rather than throwing (lenient)
                AddByte(b);
            }
            var hex = Lexed(PdfTokenType.HexString, null, startPos);
            FinishBytes(ref hex, spell, hex: true);
            return hex;
        }

        private Lexeme ReadNumber(long startPos, byte firstByte, bool spell)
        {
            var sb = BeginText();
            // Lenient readers tolerate "--42" (double-minus) by treating it as a single minus.
            if (firstByte == (byte)'-' && PeekByte() == (byte)'-')
                ReadByte(); // consume the extra '-'
            sb.Append((char)firstByte);
            bool hasDot = firstByte == (byte)'.';

            while (true)
            {
                int b = PeekByte();
                if (b == -1) break;
                byte ch = (byte)b;

                if (PdfConstants.IsDigit(ch))
                {
                    ReadByte();
                    sb.Append((char)ch);
                }
                else if (ch == (byte)'.' && !hasDot)
                {
                    ReadByte();
                    sb.Append('.');
                    hasDot = true;
                }
                else
                {
                    break;
                }
            }

            // Handle edge cases like just '+' or '-' which are really keywords
            if (sb.Length == 1 && (sb[0] == '+' || sb[0] == '-' || sb[0] == '.'))
                return Lexed(PdfTokenType.Keyword, EndText(), startPos);

            if (spell)
                return Lexed(PdfTokenType.Number, EndText(), startPos);

            var number = Lexed(PdfTokenType.Number, null, startPos);
            number.IsReal = hasDot;
            if (!TryValueOf(sb, hasDot, out number.Integer, out number.Real))
            {
                // Outside the exact reading: through the token's own parse of its text.
                var spelled = new PdfToken(PdfTokenType.Number, EndText(), startPos);
                if (hasDot) number.Real = spelled.DoubleValue;
                else number.Integer = spelled.LongValue;
                return number;
            }
            Release();
            return number;
        }

        /// <summary>
        /// The powers of ten a real's point can stand for: no more digits after it than the fifteen a
        /// real may have, and every one exact in a <see cref="double"/> (which holds them to 10²²).
        /// </summary>
        private static readonly double[] ExactPowersOfTen =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11, 1e12, 1e13, 1e14, 1e15,
        };

        /// <summary>
        /// A number's value from its digits, exactly what <see cref="PdfToken.LongValue"/> and
        /// <see cref="PdfToken.DoubleValue"/> read from its text — or false where that cannot be
        /// promised without the text, which the caller then spells and parses.
        /// </summary>
        /// <remarks>
        /// An integer of up to eighteen digits fits a <see cref="long"/> as it is. A real whose digits,
        /// read as one integer, number fifteen or fewer — so at most fifteen after the point — is that
        /// integer (below 2⁵³, so exact) divided by a power of ten (exact up to 10²²): one IEEE
        /// division of two exact values, rounded once, which is the correctly rounded decimal
        /// (Clinger's fast path), the value a correct parse returns. A real with no digits is the 0 a
        /// failed parse returns. A negative zero is left to the text: .NET Framework's parse returns
        /// +0 for "-0.0" and .NET Core's −0.
        /// </remarks>
        private static bool TryValueOf(StringBuilder text, bool isReal, out long integer, out double real)
        {
            integer = 0; real = 0;
            int i = 0;
            bool negative = false;
            if (text[0] == '-' || text[0] == '+') { negative = text[0] == '-'; i = 1; }

            long mantissa = 0;
            int digits = 0, afterPoint = 0;
            bool seenPoint = false;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '.') { seenPoint = true; continue; }
                if (++digits > (isReal ? 15 : 18)) return false;
                mantissa = mantissa * 10 + (c - '0');
                if (seenPoint) afterPoint++;
            }

            if (!isReal)
            {
                integer = negative ? -mantissa : mantissa;
                return true;
            }
            if (digits == 0) return true;                       // "-.", "+.": no digits, 0
            if (mantissa == 0 && negative) return false;         // −0: the text decides
            double value = mantissa / ExactPowersOfTen[afterPoint];
            real = negative ? -value : value;
            return true;
        }

        private Lexeme ReadKeyword(long startPos, byte firstByte)
        {
            var sb = BeginText();
            sb.Append((char)firstByte);

            while (true)
            {
                int b = PeekByte();
                if (b == -1) break;
                byte ch = (byte)b;
                if (PdfConstants.IsWhitespace(ch) || PdfConstants.IsDelimiter(ch))
                    break;
                // Stop at a digit — standard PDF content-stream operators do not contain
                // digits (the only exceptions are d0 and d1, handled below).  Some PDF
                // generators (e.g. ISIS AfpToPdf) omit whitespace between an operator and
                // its following numeric operand (e.g. "g1" = "g" + 1, "BT0" = "BT" + 0).
                // Stopping here lets the digit be read as a separate Number token so the
                // downstream operator receives the correct number of arguments.
                if (PdfConstants.IsDigit(ch))
                {
                    // d0 and d1 are legitimate Type 3 glyph-width operators — include digit.
                    if (sb.Length == 1 && sb[0] == 'd') { ReadByte(); sb.Append((char)ch); }
                    break;
                }
                ReadByte();
                sb.Append((char)ch);
            }

            string keyword = EndKeyword();

            if (keyword == "true" || keyword == "false")
                return Lexed(PdfTokenType.Boolean, keyword, startPos);
            if (keyword == "null")
                return Lexed(PdfTokenType.Null, keyword, startPos);

            return Lexed(PdfTokenType.Keyword, keyword, startPos);
        }

        public int ReadInteger()
        {
            var token = NextToken();
            if (token.Type != PdfTokenType.Number)
                return 0; // Non-number in integer context: return 0 (lenient).
            return token.IntValue;
        }

        /// <summary>
        /// Skips the single end-of-line sequence (CR, LF, or CR+LF) that the PDF spec
        /// requires to follow the "stream" keyword, and returns the stream position of
        /// the first byte of actual stream data.
        /// </summary>
        /// <remarks>
        /// Spaces before the end of line are skipped too, and a byte that is not an end of line is
        /// left where it is, as other readers read it. This consumed whatever byte followed the keyword:
        /// on <c>stream␠CR LF</c> the space went and the CR LF became the data's first two bytes, so
        /// a Flate stream lost its header; with no end of line at all the data lost its first byte.
        /// </remarks>
        public long SkipStreamEol()
        {
            int b = ReadByte();
            while (b == PdfConstants.Space) b = ReadByte();
            if (b == PdfConstants.CarriageReturn)
            {
                if (PeekByte() == PdfConstants.LineFeed) ReadByte();
            }
            else if (b != PdfConstants.LineFeed && b != -1)
            {
                Back();
            }
            return Position;
        }

        /// <summary>Up to <paramref name="count"/> bytes from the current position, the position moved past them.</summary>
        private int ReadInto(byte[] buffer, int count)
        {
            if (_data == null) return StreamBytes.Fill(_stream, buffer, 0, count);
            long available = Math.Max(0, _dataLength - _pos);
            int n = (int)Math.Min(count, available);
            if (n > 0) Buffer.BlockCopy(_data, (int)_pos, buffer, 0, n);
            _pos += n;
            return n;
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes from the current stream position
        /// without skipping any EOL prefix.  Used after <see cref="SkipStreamEol"/>.
        /// </summary>
        public byte[] ReadStreamBytesRaw(int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            var data = new byte[count];
            // Truncated stream: return partial data (lenient).
            int totalRead = ReadInto(data, count);
            if (totalRead < count)
                Array.Resize(ref data, totalRead);
            return data;
        }

        public byte[] ReadStreamData(int length)
        {
            SkipStreamEol();

            var data = new byte[length];
            // Truncated stream: return partial data (lenient).
            int totalRead = ReadInto(data, length);
            if (totalRead < length)
                Array.Resize(ref data, totalRead);
            return data;
        }

        public byte[] ReadRawBytes(long offset, int count)
        {
            long saved = Position;
            Position = offset;
            var buffer = new byte[count];
            int totalRead = ReadInto(buffer, count);
            Position = saved;
            if (totalRead < count)
                Array.Resize(ref buffer, totalRead);
            return buffer;
        }
    }
}
