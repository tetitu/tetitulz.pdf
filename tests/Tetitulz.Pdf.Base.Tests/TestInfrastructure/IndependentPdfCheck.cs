// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// A structural check of a PDF file that shares no code with this library: its own tokenizer, its own
/// cross-reference reader, its own decryption, written from ISO 32000-2 alone. A writer and the parser
/// it is tested with can share a misunderstanding — a <c>/Size</c> one short, a <c>/Prev</c> to the
/// wrong section, a stream <c>/Length</c> the reader never trusts — and every round trip passes; this
/// re-derives the file from its bytes and says what it had to forgive.
/// </summary>
/// <remarks>
/// <para><b>What is checked.</b> The header and the last <c>startxref</c>; every cross-reference section
/// on the <c>/Prev</c> chain (tables entry by entry, streams through their <c>/W</c>, <c>/Index</c> and
/// predictor; a hybrid file's <c>/XRefStm</c>); every in-use object at its offset, with the number and
/// generation the table gives; every stream's framing (<c>stream</c> and its end of line, the
/// <c>/Length</c>, <c>endstream</c>, <c>endobj</c>); every object stream's header against the entries that
/// point into it; <c>/Size</c> above every object number; the catalog and the page tree with its
/// <c>/Count</c>s; and every <c>/FlateDecode</c> stream decoding. An encrypted file is opened with the
/// password given — the standard security handler, revisions 2 to 6 — and its streams decrypted first.</para>
/// <para><b>What is not.</b> Content-stream syntax, fonts, filters other than Flate, linearization hint
/// tables, the public-key handler.</para>
/// </remarks>
internal static class IndependentPdfCheck
{
    internal sealed class Outcome
    {
        internal List<string> Problems { get; } = new();
        /// <summary>For an encrypted file, whether the password given opened it; true for a file without encryption.</summary>
        internal bool Opened { get; set; } = true;
        internal bool Clean => Opened && Problems.Count == 0;
        /// <summary>How many streams were decrypted, and how many inflated — the evidence the checks above ran.</summary>
        internal int StreamsDecrypted { get; set; }
        internal int StreamsInflated { get; set; }
        public override string ToString() => (Opened ? "" : "the password does not open the file; ") +
            (Problems.Count == 0 ? "no problems" : string.Join("; ", Problems.Take(20)) + (Problems.Count > 20 ? $"; and {Problems.Count - 20} more" : ""));
    }

    internal static Outcome Run(byte[] pdf, string? password = null)
    {
        var outcome = new Outcome();
        try
        {
            new Checker(pdf, outcome).Check(password ?? "");
        }
        catch (CheckAbort e)
        {
            outcome.Problems.Add(e.Message);
        }
        return outcome;
    }

    private sealed class CheckAbort : Exception
    {
        public CheckAbort(string message) : base(message) { }
    }

    // ------------------------------------------------------------------ the object model

    private sealed record Name(string Value);
    private sealed record Ref(int Number, int Generation);
    private sealed class Str { public byte[] Bytes = Array.Empty<byte>(); }
    private sealed class Dict : Dictionary<string, object?> { }
    private sealed class Arr : List<object?> { }
    private sealed class StreamObj { public Dict Dict = new(); public int DataStart; public int Length; }

    private readonly struct Entry
    {
        public Entry(int type, long field2, int field3) { Type = type; Field2 = field2; Field3 = field3; }
        public int Type { get; }      // 0 free, 1 in use at an offset, 2 in an object stream
        public long Field2 { get; }   // offset, or the object stream's number
        public int Field3 { get; }    // generation, or the index in the object stream
    }

    // ------------------------------------------------------------------ the checker

    private sealed class Checker
    {
        private readonly byte[] _b;
        private readonly Outcome _o;
        private readonly Dictionary<int, Entry> _entries = new();
        private readonly Dictionary<int, object?> _cache = new();
        private readonly Dictionary<int, Dictionary<int, object?>> _objectStreams = new();
        private Dict _trailer = new();
        private int _resolveDepth;

        // Encryption state.
        private bool _encrypted;
        private int _v, _r;
        private byte[] _fileKey = Array.Empty<byte>();
        private string _streamMethod = "None", _embeddedMethod = "None";
        private bool _encryptMetadata = true;
        private int _encryptObject = -1;

        public Checker(byte[] bytes, Outcome outcome) { _b = bytes; _o = outcome; }

        private void Problem(string message) => _o.Problems.Add(message);

        public void Check(string password)
        {
            CheckHeader();
            int startxref = FindStartXref();
            ReadXrefChain(startxref);

            int maxNumber = _entries.Keys.DefaultIfEmpty(0).Max();
            if (_trailer.TryGetValue("Size", out object? size) && size is long s)
            {
                if (s <= maxNumber) Problem($"/Size {s} does not exceed the highest object number {maxNumber}");
            }
            else Problem("the trailer has no integer /Size");

            if (_trailer.TryGetValue("Encrypt", out object? enc) && enc != null)
            {
                if (enc is Ref er) _encryptObject = er.Number;
                Dict? encrypt = Resolve(enc) as Dict;
                if (encrypt == null) throw new CheckAbort("/Encrypt is not a dictionary");
                if (!OpenEncryption(encrypt, password)) { _o.Opened = false; return; }
            }

            foreach (KeyValuePair<int, Entry> e in _entries.OrderBy(e => e.Key))
            {
                if (e.Value.Type == 1) CheckObjectAt(e.Key, e.Value);
                else if (e.Value.Type == 2) CheckCompressedObject(e.Key, e.Value);
            }

            CheckCatalog();
        }

        // ---------------------------------------------------------- header, startxref, cross-reference

        private void CheckHeader()
        {
            int limit = Math.Min(_b.Length, 1024);
            int at = IndexOf("%PDF-", 0, limit);
            if (at < 0) throw new CheckAbort("no %PDF- header in the first 1024 bytes");
            if (at != 0) Problem($"the header is at byte {at}, not 0");
            if (LastIndexOf("%%EOF", _b.Length) < _b.Length - 1024) Problem("no %%EOF in the last 1024 bytes");
        }

        private int FindStartXref()
        {
            int at = LastIndexOf("startxref", _b.Length);
            if (at < 0) throw new CheckAbort("no startxref");
            int pos = at + "startxref".Length;
            SkipWhite(ref pos);
            long offset = ReadInteger(ref pos);
            if (offset < 0 || offset >= _b.Length) throw new CheckAbort($"startxref {offset} is outside the file");
            return (int)offset;
        }

        private void ReadXrefChain(int offset)
        {
            var seen = new HashSet<int>();
            bool first = true;
            while (true)
            {
                if (!seen.Add(offset)) { Problem($"the /Prev chain loops at {offset}"); break; }
                Dict trailer = ReadXrefSection(offset);
                if (first) { _trailer = trailer; first = false; }
                if (trailer.TryGetValue("XRefStm", out object? xs) && xs is long xso)
                {
                    if (xso < 0 || xso >= _b.Length) Problem($"/XRefStm {xso} is outside the file");
                    else ReadXrefStream((int)xso);
                }
                if (!trailer.TryGetValue("Prev", out object? prev) || prev == null) break;
                if (prev is not long p || p < 0 || p >= _b.Length) { Problem("/Prev is not an offset in the file"); break; }
                offset = (int)p;
            }
        }

        private Dict ReadXrefSection(int offset)
        {
            int pos = offset;
            if (Matches("xref", pos)) return ReadXrefTable(pos + 4);
            if (!IsDigit(At(pos))) throw new CheckAbort($"startxref / Prev {offset} points at neither 'xref' nor an object");
            return ReadXrefStream(offset);
        }

        private Dict ReadXrefTable(int pos)
        {
            SkipWhite(ref pos);
            while (IsDigit(At(pos)))
            {
                long start = ReadInteger(ref pos);
                SkipSpaces(ref pos);
                long count = ReadInteger(ref pos);
                SkipWhite(ref pos);
                for (long i = 0; i < count; i++)
                {
                    if (pos + 20 > _b.Length) throw new CheckAbort("the cross-reference table runs past the end of the file");
                    string line = Encoding.ASCII.GetString(_b, pos, 20);
                    bool shaped = line.Length == 20 && line.Substring(0, 10).All(char.IsDigit) && line[10] == ' ' &&
                                  line.Substring(11, 5).All(char.IsDigit) && line[16] == ' ' && (line[17] == 'n' || line[17] == 'f') &&
                                  (line.Substring(18) == " \r" || line.Substring(18) == " \n" || line.Substring(18) == "\r\n");
                    if (!shaped) { Problem($"cross-reference entry {start + i} is not 20 bytes of the table's form"); }
                    int number = (int)(start + i);
                    if (!_entries.ContainsKey(number) && line.Length == 20)
                    {
                        long off = long.Parse(line.Substring(0, 10), CultureInfo.InvariantCulture);
                        int gen = int.Parse(line.Substring(11, 5), CultureInfo.InvariantCulture);
                        _entries[number] = new Entry(line[17] == 'n' ? 1 : 0, off, gen);
                    }
                    pos += 20;
                }
                SkipWhite(ref pos);
            }
            if (!Matches("trailer", pos)) throw new CheckAbort("the cross-reference table is not followed by 'trailer'");
            pos += "trailer".Length;
            return ParseObject(ref pos) as Dict ?? throw new CheckAbort("the trailer is not a dictionary");
        }

        private Dict ReadXrefStream(int offset)
        {
            int pos = offset;
            (int number, int generation) = ReadObjectHeader(ref pos, -1, -1);
            object? value = ParseObjectBody(ref pos, number);
            if (value is not StreamObj stream) throw new CheckAbort($"the cross-reference stream at {offset} is not a stream");
            if (!(stream.Dict.TryGetValue("Type", out object? t) && t is Name { Value: "XRef" }))
                Problem($"the cross-reference stream {number} {generation} is not /Type /XRef");
            byte[] data = Decode(stream, number, generation, decrypt: false, $"cross-reference stream {number}");
            Arr? w = stream.Dict.TryGetValue("W", out object? wv) ? wv as Arr : null;
            if (w == null || w.Count != 3 || w.Any(x => x is not long)) throw new CheckAbort($"cross-reference stream {number} has no /W of three integers");
            int[] widths = w.Select(x => (int)(long)x!).ToArray();
            int rowLength = widths.Sum();
            long size = stream.Dict.TryGetValue("Size", out object? sv) && sv is long sl ? sl : 0;
            var index = stream.Dict.TryGetValue("Index", out object? iv) && iv is Arr ia ? ia.Select(x => x is long l ? l : -1).ToList() : new List<long> { 0, size };
            if (index.Count % 2 != 0) Problem($"cross-reference stream {number}'s /Index has an odd length");
            int row = 0;
            for (int k = 0; k + 1 < index.Count; k += 2)
                for (long i = 0; i < index[k + 1]; i++, row++)
                {
                    int at = row * rowLength;
                    if (at + rowLength > data.Length) { Problem($"cross-reference stream {number} holds fewer rows than its /Index"); return stream.Dict; }
                    long f1 = widths[0] == 0 ? 1 : Field(data, at, widths[0]);
                    long f2 = Field(data, at + widths[0], widths[1]);
                    long f3 = Field(data, at + widths[0] + widths[1], widths[2]);
                    int n = (int)(index[k] + i);
                    if (f1 > 2) continue;   // reserved types are null references
                    if (!_entries.ContainsKey(n)) _entries[n] = new Entry((int)f1, f2, (int)f3);
                }
            if (row * rowLength != data.Length) Problem($"cross-reference stream {number} holds {data.Length} bytes for {row} rows of {rowLength}");
            return stream.Dict;
        }

        private static long Field(byte[] data, int at, int width)
        {
            long v = 0;
            for (int i = 0; i < width; i++) v = (v << 8) | data[at + i];
            return v;
        }

        // ---------------------------------------------------------- objects

        private void CheckObjectAt(int number, Entry e)
        {
            if (e.Field2 <= 0 || e.Field2 >= _b.Length) { Problem($"object {number}'s offset {e.Field2} is outside the file"); return; }
            int pos = (int)e.Field2;
            try
            {
                ReadObjectHeader(ref pos, number, e.Field3);
                object? value = ParseObjectBody(ref pos, number);
                if (value is StreamObj stream) CheckStream(number, e.Field3, stream);
            }
            catch (CheckAbort a) { Problem($"object {number}: {a.Message}"); }
        }

        private void CheckStream(int number, int generation, StreamObj stream)
        {
            if (stream.Dict.TryGetValue("Type", out object? t) && t is Name { Value: "XRef" }) return;   // read with the chain
            var filters = FilterNames(stream.Dict);
            if (filters.All(f => f == "FlateDecode" || f == "Fl" || f == "Crypt"))
            {
                Decode(stream, number, generation, decrypt: true, $"stream {number}");
                return;
            }
            // A filter not read here: the stream is still decrypted, which for AES checks its padding.
            if (_encrypted && ShouldDecrypt(stream.Dict, number, filters))
            {
                byte[] data = new byte[stream.Length];
                Buffer.BlockCopy(_b, stream.DataStart, data, 0, stream.Length);
                DecryptStream(data, number, generation, stream.Dict, $"stream {number}");
                _o.StreamsDecrypted++;
            }
        }

        private void CheckCompressedObject(int number, Entry e)
        {
            int container = (int)e.Field2;
            if (!_entries.TryGetValue(container, out Entry c) || c.Type != 1)
            {
                Problem($"object {number} is in object stream {container}, which is not an in-use uncompressed object");
                return;
            }
            Dictionary<int, object?>? members = ObjectStreamMembers(container);
            if (members == null) return;
            if (!members.ContainsKey(number)) Problem($"object {number} is not in object stream {container}'s header");
        }

        private Dictionary<int, object?>? ObjectStreamMembers(int container)
        {
            if (_objectStreams.TryGetValue(container, out var cached)) return cached;
            _objectStreams[container] = null!;
            Entry c = _entries[container];
            int pos = (int)c.Field2;
            try
            {
                ReadObjectHeader(ref pos, container, c.Field3);
                if (ParseObjectBody(ref pos, container) is not StreamObj stream) { Problem($"object stream {container} is not a stream"); return null; }
                if (!(stream.Dict.TryGetValue("Type", out object? t) && t is Name { Value: "ObjStm" })) Problem($"object stream {container} is not /Type /ObjStm");
                long n = stream.Dict.TryGetValue("N", out object? nv) && nv is long nl ? nl : -1;
                long first = stream.Dict.TryGetValue("First", out object? fv) && fv is long fl ? fl : -1;
                if (n < 0 || first < 0) { Problem($"object stream {container} has no /N or /First"); return null; }
                byte[] data = Decode(stream, container, c.Field3, decrypt: true, $"object stream {container}");
                var members = new Dictionary<int, object?>();
                var parser = new Checker(data, _o);
                int hp = 0;
                var header = new List<(int Number, long Offset)>();
                for (long i = 0; i < n; i++)
                {
                    parser.SkipWhite(ref hp);
                    long num = parser.ReadInteger(ref hp);
                    parser.SkipWhite(ref hp);
                    long off = parser.ReadInteger(ref hp);
                    header.Add(((int)num, off));
                }
                foreach (var (num, off) in header)
                {
                    int p = (int)(first + off);
                    if (p >= data.Length) { Problem($"object stream {container}: object {num} is placed past the stream's end"); continue; }
                    members[num] = parser.ParseObject(ref p);
                }
                foreach (var kv in _entries.Where(kv => kv.Value.Type == 2 && kv.Value.Field2 == container))
                    if (kv.Value.Field3 >= n) Problem($"object {kv.Key}'s index {kv.Value.Field3} is past object stream {container}'s /N {n}");
                _objectStreams[container] = members;
                return members;
            }
            catch (CheckAbort a) { Problem($"object stream {container}: {a.Message}"); return null; }
        }

        private object? Resolve(object? value)
        {
            if (value is not Ref r) return value;
            if (_cache.TryGetValue(r.Number, out object? hit)) return hit;
            if (++_resolveDepth > 64) throw new CheckAbort("references nest more than 64 deep");
            try
            {
                if (!_entries.TryGetValue(r.Number, out Entry e) || e.Type == 0) return _cache[r.Number] = null;
                if (e.Type == 2)
                {
                    var members = ObjectStreamMembers((int)e.Field2);
                    return _cache[r.Number] = members != null && members.TryGetValue(r.Number, out object? m) ? m : null;
                }
                int pos = (int)e.Field2;
                ReadObjectHeader(ref pos, r.Number, e.Field3);
                return _cache[r.Number] = ParseObjectBody(ref pos, r.Number);
            }
            finally { _resolveDepth--; }
        }

        private void CheckCatalog()
        {
            if (!_trailer.TryGetValue("Root", out object? root) || Resolve(root) is not Dict catalog) { Problem("/Root is not a dictionary"); return; }
            if (!(catalog.TryGetValue("Type", out object? t) && t is Name { Value: "Catalog" })) Problem("the catalog is not /Type /Catalog");
            if (!catalog.TryGetValue("Pages", out object? pages) || pages is not Ref) { Problem("the catalog's /Pages is not an indirect reference"); return; }
            var visited = new HashSet<int>();
            CountPages(pages, visited, 0);
        }

        private long CountPages(object? node, HashSet<int> visited, int depth)
        {
            if (node is not Ref r) { Problem("a page-tree /Kids entry is not an indirect reference"); return 0; }
            if (!visited.Add(r.Number)) { Problem($"page-tree node {r.Number} is reached twice"); return 0; }
            if (depth > 64) { Problem("the page tree is more than 64 deep"); return 0; }
            if (Resolve(node) is not Dict d) { Problem($"page-tree node {r.Number} is not a dictionary"); return 0; }
            string type = d.TryGetValue("Type", out object? t) && t is Name n ? n.Value : "";
            if (type == "Page") return 1;
            if (type != "Pages") { Problem($"page-tree node {r.Number} is neither /Page nor /Pages"); return 0; }
            long sum = 0;
            if (d.TryGetValue("Kids", out object? kids) && Resolve(kids) is Arr ka)
                foreach (object? kid in ka) sum += CountPages(kid, visited, depth + 1);
            else Problem($"page-tree node {r.Number} has no /Kids array");
            long count = d.TryGetValue("Count", out object? c) && Resolve(c) is long cl ? cl : -1;
            if (count != sum) Problem($"page-tree node {r.Number} says /Count {count} over {sum} pages");
            return sum;
        }

        // ---------------------------------------------------------- streams and filters

        private static List<string> FilterNames(Dict dict)
        {
            var names = new List<string>();
            if (!dict.TryGetValue("Filter", out object? f) || f == null) return names;
            if (f is Name n) names.Add(n.Value);
            else if (f is Arr a) names.AddRange(a.OfType<Name>().Select(x => x.Value));
            return names;
        }

        private byte[] Decode(StreamObj stream, int number, int generation, bool decrypt, string what)
        {
            byte[] data = new byte[stream.Length];
            Buffer.BlockCopy(_b, stream.DataStart, data, 0, stream.Length);
            List<string> filters = FilterNames(stream.Dict);
            if (decrypt && _encrypted && ShouldDecrypt(stream.Dict, number, filters))
            {
                data = DecryptStream(data, number, generation, stream.Dict, what);
                _o.StreamsDecrypted++;
            }
            object? parms = stream.Dict.TryGetValue("DecodeParms", out object? dp) ? Resolve(dp) : null;
            for (int i = 0; i < filters.Count; i++)
            {
                string filter = filters[i];
                if (filter == "Crypt") continue;
                if (filter != "FlateDecode" && filter != "Fl") throw new CheckAbort($"{what}: the filter /{filter} is not read here");
                try
                {
                    using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                    using var o = new MemoryStream();
                    z.CopyTo(o);
                    data = o.ToArray();
                    _o.StreamsInflated++;
                }
                catch (InvalidDataException e) { throw new CheckAbort($"{what}: its FlateDecode data does not decode ({e.Message})"); }
                Dict? p = parms is Arr pa ? (i < pa.Count ? Resolve(pa[i]) as Dict : null) : parms as Dict;
                if (p != null) data = UndoPredictor(data, p, what);
            }
            return data;
        }

        private static byte[] UndoPredictor(byte[] data, Dict p, string what)
        {
            long predictor = p.TryGetValue("Predictor", out object? pv) && pv is long pl ? pl : 1;
            if (predictor < 10) { if (predictor != 1) throw new CheckAbort($"{what}: predictor {predictor} is not read here"); return data; }
            long columns = p.TryGetValue("Columns", out object? cv) && cv is long cl ? cl : 1;
            long colors = p.TryGetValue("Colors", out object? kv) && kv is long kl ? kl : 1;
            long bpc = p.TryGetValue("BitsPerComponent", out object? bv) && bv is long bl ? bl : 8;
            int bpp = (int)Math.Max(1, colors * bpc / 8);
            int rowBytes = (int)((columns * colors * bpc + 7) / 8);
            if (data.Length % (rowBytes + 1) != 0) throw new CheckAbort($"{what}: {data.Length} bytes are not whole PNG rows of {rowBytes + 1}");
            int rows = data.Length / (rowBytes + 1);
            var output = new byte[rows * rowBytes];
            var prior = new byte[rowBytes];
            for (int r = 0; r < rows; r++)
            {
                int type = data[r * (rowBytes + 1)];
                var cur = new byte[rowBytes];
                Buffer.BlockCopy(data, r * (rowBytes + 1) + 1, cur, 0, rowBytes);
                for (int i = 0; i < rowBytes; i++)
                {
                    int left = i >= bpp ? cur[i - bpp] : 0, up = prior[i], upLeft = i >= bpp ? prior[i - bpp] : 0;
                    cur[i] = type switch
                    {
                        0 => cur[i],
                        1 => (byte)(cur[i] + left),
                        2 => (byte)(cur[i] + up),
                        3 => (byte)(cur[i] + (left + up) / 2),
                        4 => (byte)(cur[i] + Paeth(left, up, upLeft)),
                        _ => throw new CheckAbort($"{what}: PNG row filter {type}"),
                    };
                }
                Buffer.BlockCopy(cur, 0, output, r * rowBytes, rowBytes);
                prior = cur;
            }
            return output;
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        // ---------------------------------------------------------- encryption (the standard handler)

        private bool ShouldDecrypt(Dict dict, int number, List<string> filters)
        {
            if (number == _encryptObject) return false;
            if (dict.TryGetValue("Type", out object? t) && t is Name { Value: "XRef" }) return false;
            if (filters.Count > 0 && filters[0] == "Crypt")
            {
                // §7.4.10: the stream names its own crypt filter; /Identity (the default) leaves it as it is.
                return false;
            }
            if (!_encryptMetadata && t is Name { Value: "Metadata" }) return false;
            string method = t is Name { Value: "EmbeddedFile" } ? _embeddedMethod : _streamMethod;
            return method != "None";
        }

        private byte[] DecryptStream(byte[] data, int number, int generation, Dict dict, string what)
        {
            bool embedded = dict.TryGetValue("Type", out object? t) && t is Name { Value: "EmbeddedFile" };
            string method = embedded ? _embeddedMethod : _streamMethod;
            if (method == "AESV3") return AesCbc(_fileKey, data, what);
            byte[] objectKey = ObjectKey(number, generation, method == "AESV2");
            return method == "AESV2" ? AesCbc(objectKey, data, what) : Rc4(objectKey, data);
        }

        private byte[] ObjectKey(int number, int generation, bool aes)
        {
            var input = new List<byte>(_fileKey)
            {
                (byte)number, (byte)(number >> 8), (byte)(number >> 16), (byte)generation, (byte)(generation >> 8),
            };
            if (aes) input.AddRange(new byte[] { 0x73, 0x41, 0x6C, 0x54 });   // "sAlT"
            byte[] digest = MD5.HashData(input.ToArray());
            return digest.Take(Math.Min(_fileKey.Length + 5, 16)).ToArray();
        }

        private static byte[] AesCbc(byte[] key, byte[] data, string what)
        {
            if (data.Length < 16 || data.Length % 16 != 0) throw new CheckAbort($"{what}: {data.Length} bytes are not an IV and whole AES blocks");
            using Aes aes = Aes.Create();
            aes.Key = key;
            try { return aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16), PaddingMode.PKCS7); }
            catch (CryptographicException) { throw new CheckAbort($"{what}: its AES padding is wrong — the key or the data is"); }
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }
            var output = new byte[data.Length];
            for (int k = 0, i = 0, j = 0; k < data.Length; k++)
            {
                i = (i + 1) & 0xFF;
                j = (j + s[i]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
                output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
            }
            return output;
        }

        private static readonly byte[] Pad =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
        };

        private bool OpenEncryption(Dict encrypt, string password)
        {
            _encrypted = true;
            if (!(encrypt.TryGetValue("Filter", out object? f) && f is Name { Value: "Standard" }))
                throw new CheckAbort("only the standard security handler is read here");
            _v = encrypt.TryGetValue("V", out object? v) && v is long vl ? (int)vl : 0;
            _r = encrypt.TryGetValue("R", out object? r) && r is long rl ? (int)rl : 0;
            byte[] o = (Resolve(encrypt.GetValueOrDefault("O")) as Str)?.Bytes ?? throw new CheckAbort("/Encrypt has no /O string");
            byte[] u = (Resolve(encrypt.GetValueOrDefault("U")) as Str)?.Bytes ?? throw new CheckAbort("/Encrypt has no /U string");
            long p = encrypt.TryGetValue("P", out object? pv) && pv is long pl ? pl : 0;
            _encryptMetadata = !(encrypt.TryGetValue("EncryptMetadata", out object? em) && em is false);

            if (_v == 4 || _v == 5)
            {
                Dict? cf = Resolve(encrypt.GetValueOrDefault("CF")) as Dict;
                string Method(string key)
                {
                    string name = encrypt.TryGetValue(key, out object? n) && n is Name nn ? nn.Value : "Identity";
                    if (name == "Identity") return "None";
                    Dict? filter = cf != null ? Resolve(cf.GetValueOrDefault(name)) as Dict : null;
                    return filter != null && filter.TryGetValue("CFM", out object? cfm) && cfm is Name cn ? (cn.Value == "V2" ? "RC4" : cn.Value) : "None";
                }
                _streamMethod = Method("StmF");
                _embeddedMethod = encrypt.ContainsKey("EFF") ? Method("EFF") : _streamMethod;
            }
            else
            {
                _streamMethod = _embeddedMethod = "RC4";
            }

            if (_r >= 5)
            {
                byte[] oe = (Resolve(encrypt.GetValueOrDefault("OE")) as Str)?.Bytes ?? throw new CheckAbort("/Encrypt has no /OE");
                byte[] ue = (Resolve(encrypt.GetValueOrDefault("UE")) as Str)?.Bytes ?? throw new CheckAbort("/Encrypt has no /UE");
                byte[] pw = Encoding.UTF8.GetBytes(password);
                if (pw.Length > 127) pw = pw.Take(127).ToArray();
                if (o.Length < 48 || u.Length < 48) throw new CheckAbort("/O or /U is shorter than 48 bytes");
                byte[] uBytes48 = u.Take(48).ToArray();
                if (Hash(pw, u.AsSpan(32, 8).ToArray(), Array.Empty<byte>()).AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32)))
                {
                    _fileKey = AesNoIv(Hash(pw, u.AsSpan(40, 8).ToArray(), Array.Empty<byte>()), ue);
                    return true;
                }
                if (Hash(pw, o.AsSpan(32, 8).ToArray(), uBytes48).AsSpan(0, 32).SequenceEqual(o.AsSpan(0, 32)))
                {
                    _fileKey = AesNoIv(Hash(pw, o.AsSpan(40, 8).ToArray(), uBytes48), oe);
                    return true;
                }
                return false;
            }

            int length = _v == 1 ? 40 : encrypt.TryGetValue("Length", out object? lv) && lv is long ll ? (int)ll : 40;
            int n = _r == 2 ? 5 : length / 8;
            byte[] id = Resolve(_trailer.GetValueOrDefault("ID")) is Arr ida && ida.Count > 0 && Resolve(ida[0]) is Str ids ? ids.Bytes : Array.Empty<byte>();
            byte[] Latin1(string s) => s.Select(ch => (byte)ch).Take(32).ToArray();

            byte[] UserKey(byte[] pw)
            {
                var input = new List<byte>(Padded(pw));
                input.AddRange(o.Take(32));
                input.AddRange(new[] { (byte)p, (byte)(p >> 8), (byte)(p >> 16), (byte)(p >> 24) });
                input.AddRange(id);
                if (_r >= 4 && !_encryptMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
                byte[] key = MD5.HashData(input.ToArray());
                if (_r >= 3) for (int i = 0; i < 50; i++) key = MD5.HashData(key.Take(n).ToArray());
                return key.Take(n).ToArray();
            }

            bool UserMatches(byte[] key)
            {
                if (_r == 2) return Rc4(key, Pad).AsSpan().SequenceEqual(u.AsSpan(0, 32));
                byte[] x = Rc4(key, MD5.HashData(Pad.Concat(id).ToArray()));
                for (int i = 1; i <= 19; i++) x = Rc4(key.Select(b => (byte)(b ^ i)).ToArray(), x);
                return x.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
            }

            byte[] userKey = UserKey(Latin1(password));
            if (UserMatches(userKey)) { _fileKey = userKey; return true; }

            // Algorithm 7: the owner password decrypts /O to the user password.
            byte[] ownerKey = MD5.HashData(Padded(Latin1(password)));
            if (_r >= 3) for (int i = 0; i < 50; i++) ownerKey = MD5.HashData(ownerKey.Take(n).ToArray());
            ownerKey = ownerKey.Take(n).ToArray();
            byte[] user = o.Take(32).ToArray();
            if (_r == 2) user = Rc4(ownerKey, user);
            else for (int i = 19; i >= 0; i--) user = Rc4(ownerKey.Select(b => (byte)(b ^ i)).ToArray(), user);
            byte[] recovered = UserKey(user);
            if (UserMatches(recovered)) { _fileKey = recovered; return true; }
            return false;
        }

        private static byte[] Padded(byte[] pw) => pw.Concat(Pad).Take(32).ToArray();

        private static byte[] AesNoIv(byte[] key, byte[] data)
        {
            using Aes aes = Aes.Create();
            aes.Key = key.Take(32).ToArray();
            return aes.DecryptCbc(data.Take(32).ToArray(), new byte[16], PaddingMode.None);
        }

        /// <summary>Algorithm 2.B (revision 6), or plain SHA-256 for revision 5.</summary>
        private byte[] Hash(byte[] password, byte[] salt, byte[] userKey)
        {
            byte[] k = SHA256.HashData(password.Concat(salt).Concat(userKey).ToArray());
            if (_r == 5) return k;
            for (int round = 0; ; round++)
            {
                byte[] block = password.Concat(k).Concat(userKey).ToArray();
                var k1 = new byte[block.Length * 64];
                for (int i = 0; i < 64; i++) Buffer.BlockCopy(block, 0, k1, i * block.Length, block.Length);
                byte[] e;
                using (Aes aes = Aes.Create())
                {
                    aes.Key = k.Take(16).ToArray();
                    e = aes.EncryptCbc(k1, k.Skip(16).Take(16).ToArray(), PaddingMode.None);
                }
                int mod = 0;
                for (int i = 0; i < 16; i++) mod += e[i];
                k = (mod % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
                // "the last byte of E is no greater than the round number minus 32", the round number
                // counting from 1: with a 0-based round, E[last] <= round + 1 - 32.
                if (round >= 63 && e[e.Length - 1] <= round - 31) break;
            }
            return k.Take(32).ToArray();
        }

        // ---------------------------------------------------------- the tokenizer and parser

        private (int Number, int Generation) ReadObjectHeader(ref int pos, int expectNumber, int expectGeneration)
        {
            SkipWhite(ref pos);
            int at = pos;
            long number = ReadInteger(ref pos);
            SkipWhite(ref pos);
            long generation = ReadInteger(ref pos);
            SkipWhite(ref pos);
            if (!Matches("obj", pos)) throw new CheckAbort($"no 'N G obj' at offset {at}");
            pos += 3;
            if (expectNumber >= 0 && (number != expectNumber || generation != expectGeneration))
                throw new CheckAbort($"the offset {at} holds {number} {generation} obj, not {expectNumber} {expectGeneration}");
            return ((int)number, (int)generation);
        }

        private object? ParseObjectBody(ref int pos, int number)
        {
            object? value = ParseObject(ref pos);
            SkipWhite(ref pos);
            if (value is Dict dict && Matches("stream", pos))
            {
                pos += "stream".Length;
                if (At(pos) == '\r' && At(pos + 1) == '\n') pos += 2;
                else if (At(pos) == '\n') pos += 1;
                else throw new CheckAbort("'stream' is not followed by CRLF or LF");
                object? lengthValue = dict.GetValueOrDefault("Length");
                if (lengthValue is Ref lr && lr.Number == number) throw new CheckAbort("the stream's /Length refers to the stream itself");
                if (Resolve(lengthValue) is not long length || length < 0) throw new CheckAbort("the stream has no non-negative integer /Length");
                if (pos + length > _b.Length) throw new CheckAbort($"the stream's /Length {length} runs past the end of the file");
                var stream = new StreamObj { Dict = dict, DataStart = pos, Length = (int)length };
                pos += (int)length;
                if (At(pos) == '\r' && At(pos + 1) == '\n') pos += 2;
                else if (At(pos) == '\n' || At(pos) == '\r') pos += 1;
                if (!Matches("endstream", pos)) throw new CheckAbort($"'endstream' does not follow the stream's /Length {length}");
                pos += "endstream".Length;
                SkipWhite(ref pos);
                if (!Matches("endobj", pos)) throw new CheckAbort("'endobj' does not follow 'endstream'");
                return stream;
            }
            if (!Matches("endobj", pos)) throw new CheckAbort("'endobj' does not follow the object");
            return value;
        }

        private int _parseDepth;

        /// <summary>One object; arrays and dictionaries nest at most 100 deep, since a stack overflow cannot be caught.</summary>
        public object? ParseObject(ref int pos)
        {
            if (++_parseDepth > 100) { _parseDepth = 0; throw new CheckAbort("objects nest more than 100 deep"); }
            try { return ParseObjectAt(ref pos); }
            finally { if (_parseDepth > 0) _parseDepth--; }
        }

        private object? ParseObjectAt(ref int pos)
        {
            SkipWhite(ref pos);
            if (pos >= _b.Length) throw new CheckAbort("an object runs past the end of the data");
            byte c = _b[pos];
            if (c == '<' && At(pos + 1) == '<')
            {
                pos += 2;
                var dict = new Dict();
                while (true)
                {
                    SkipWhite(ref pos);
                    if (At(pos) == '>' && At(pos + 1) == '>') { pos += 2; return dict; }
                    if (ParseObject(ref pos) is not Name key) throw new CheckAbort("a dictionary key is not a name");
                    if (dict.ContainsKey(key.Value)) throw new CheckAbort($"the dictionary holds /{key.Value} twice");
                    dict[key.Value] = ParseObject(ref pos);
                }
            }
            if (c == '[')
            {
                pos++;
                var arr = new Arr();
                while (true)
                {
                    SkipWhite(ref pos);
                    if (At(pos) == ']') { pos++; return arr; }
                    arr.Add(ParseObject(ref pos));
                }
            }
            if (c == '(') return ParseLiteral(ref pos);
            if (c == '<') return ParseHex(ref pos);
            if (c == '/')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < _b.Length && !IsWhite(_b[pos]) && !IsDelimiter(_b[pos]))
                {
                    if (_b[pos] == '#' && pos + 2 < _b.Length && IsHex(_b[pos + 1]) && IsHex(_b[pos + 2]))
                    {
                        sb.Append((char)Convert.ToByte(Encoding.ASCII.GetString(_b, pos + 1, 2), 16));
                        pos += 3;
                    }
                    else sb.Append((char)_b[pos++]);
                }
                return new Name(sb.ToString());
            }
            if (IsDigit(c) || c == '-' || c == '+' || c == '.')
            {
                int start = pos;
                pos++;
                while (pos < _b.Length && (IsDigit(_b[pos]) || _b[pos] == '.')) pos++;
                string text = Encoding.ASCII.GetString(_b, start, pos - start);
                if (!text.Contains('.') && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
                {
                    // An integer, or the first of "N G R".
                    int save = pos;
                    SkipWhite(ref pos);
                    int genStart = pos;
                    while (pos < _b.Length && IsDigit(_b[pos])) pos++;
                    if (pos > genStart)
                    {
                        long gen = long.Parse(Encoding.ASCII.GetString(_b, genStart, pos - genStart), CultureInfo.InvariantCulture);
                        SkipWhite(ref pos);
                        if (At(pos) == 'R' && (pos + 1 >= _b.Length || IsWhite(_b[pos + 1]) || IsDelimiter(_b[pos + 1])))
                        {
                            pos++;
                            return new Ref((int)integer, (int)gen);
                        }
                    }
                    pos = save;
                    return integer;
                }
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real)) return real;
                throw new CheckAbort($"'{text}' is not a number");
            }
            if (Matches("true", pos)) { pos += 4; return true; }
            if (Matches("false", pos)) { pos += 5; return false; }
            if (Matches("null", pos)) { pos += 4; return null; }
            throw new CheckAbort($"an unexpected byte 0x{c:X2} at {pos}");
        }

        private Str ParseLiteral(ref int pos)
        {
            pos++;
            var bytes = new List<byte>();
            int depth = 1;
            while (true)
            {
                if (pos >= _b.Length) throw new CheckAbort("a literal string runs past the end of the data");
                byte c = _b[pos++];
                if (c == '\\')
                {
                    if (pos >= _b.Length) break;
                    byte e = _b[pos++];
                    switch (e)
                    {
                        case (byte)'n': bytes.Add(10); break;
                        case (byte)'r': bytes.Add(13); break;
                        case (byte)'t': bytes.Add(9); break;
                        case (byte)'b': bytes.Add(8); break;
                        case (byte)'f': bytes.Add(12); break;
                        case (byte)'\r': if (At(pos) == '\n') pos++; break;
                        case (byte)'\n': break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && At(pos) >= '0' && At(pos) <= '7'; k++) v = v * 8 + (_b[pos++] - '0');
                                bytes.Add((byte)v);
                            }
                            else bytes.Add(e);
                            break;
                    }
                    continue;
                }
                if (c == '(') depth++;
                else if (c == ')' && --depth == 0) break;
                bytes.Add(c);
            }
            return new Str { Bytes = bytes.ToArray() };
        }

        private Str ParseHex(ref int pos)
        {
            pos++;
            var digits = new StringBuilder();
            while (true)
            {
                if (pos >= _b.Length) throw new CheckAbort("a hexadecimal string runs past the end of the data");
                byte c = _b[pos++];
                if (c == '>') break;
                if (IsWhite(c)) continue;
                if (!IsHex(c)) throw new CheckAbort($"a hexadecimal string holds 0x{c:X2}");
                digits.Append((char)c);
            }
            if (digits.Length % 2 == 1) digits.Append('0');
            return new Str { Bytes = Convert.FromHexString(digits.ToString()) };
        }

        public long ReadInteger(ref int pos)
        {
            int start = pos;
            if (At(pos) == '-' || At(pos) == '+') pos++;
            while (pos < _b.Length && IsDigit(_b[pos])) pos++;
            if (pos == start) throw new CheckAbort($"no integer at {start}");
            return long.Parse(Encoding.ASCII.GetString(_b, start, pos - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        public void SkipWhite(ref int pos)
        {
            while (pos < _b.Length)
            {
                if (IsWhite(_b[pos])) pos++;
                else if (_b[pos] == '%') { while (pos < _b.Length && _b[pos] != '\r' && _b[pos] != '\n') pos++; }
                else break;
            }
        }

        private void SkipSpaces(ref int pos) { while (pos < _b.Length && (_b[pos] == ' ' || _b[pos] == '\t')) pos++; }

        private int At(int pos) => pos >= 0 && pos < _b.Length ? _b[pos] : -1;

        private bool Matches(string keyword, int pos)
        {
            if (pos < 0 || pos + keyword.Length > _b.Length) return false;
            for (int i = 0; i < keyword.Length; i++) if (_b[pos + i] != keyword[i]) return false;
            int after = pos + keyword.Length;
            return after >= _b.Length || IsWhite(_b[after]) || IsDelimiter(_b[after]);
        }

        private int IndexOf(string text, int from, int to)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i = from; i + needle.Length <= to; i++)
                if (_b.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }

        private int LastIndexOf(string text, int before)
        {
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i = before - needle.Length; i >= 0; i--)
                if (_b.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
            return -1;
        }

        private static bool IsWhite(int c) => c == 0 || c == 9 || c == 10 || c == 12 || c == 13 || c == 32;
        private static bool IsDelimiter(int c) => c == '(' || c == ')' || c == '<' || c == '>' || c == '[' || c == ']' || c == '{' || c == '}' || c == '/' || c == '%';
        private static bool IsDigit(int c) => c >= '0' && c <= '9';
        private static bool IsHex(int c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
