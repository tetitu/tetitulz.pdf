// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Parsing
{
    public sealed class PdfParser
    {
        private readonly Stream _stream;
        private readonly PdfTokenizer _tokenizer;
        private readonly Dictionary<int, PdfObject> _objectCache = new Dictionary<int, PdfObject>();

        // One per-document ceiling on the decoded bytes this document's streams keep memoized, read
        // once here at parse time (PdfLimits is process-global, set at startup); shared by every
        // stream the parser builds so their retained decodes sum under it (F3).
        private readonly Util.DecodeMemoBudget _memoBudget =
            new Util.DecodeMemoBudget(Constants.PdfLimits.MaxMemoizedDecodedBytesPerDocument);
        private CrossReferenceTable _xrefTable;
        private PdfDictionary _trailer;

        /// <summary>
        /// One entry per object stream this parser has read from, keyed by the
        /// <c>/ObjStm</c>'s own object number. See <see cref="ObjectStreamReader"/> for why
        /// this exists.
        /// </summary>
        /// <remarks>
        /// Same lifetime and same thread-affinity as <see cref="_objectCache"/>: per parser
        /// instance, and a <c>PdfParser</c> is not thread-safe (each <c>PdfDocument</c> owns
        /// its own). Adding this changes neither property.
        /// <para>Least recently used first out, by <see cref="_objStmRecency"/>: a reader that does
        /// not fit evicts the oldest until it does. A cache that stops admitting readers once full
        /// would make every member of a ninth index-heavy stream rebuild that stream's whole index —
        /// one rebuild per member, with no bound but the cross-reference table.</para>
        /// </remarks>
        private Dictionary<int, LinkedListNode<(int Stream, ObjectStreamReader Reader, long Bytes)>> _objStmReaders;

        /// <summary>The cached readers, least recently used first.</summary>
        private LinkedList<(int Stream, ObjectStreamReader Reader, long Bytes)> _objStmRecency;

        /// <summary>
        /// The last reader too large for the cache on its own, kept so consecutive members of that
        /// one stream do not rebuild it each. A single reader, so it bounds nothing new.
        /// </summary>
        private (int Stream, ObjectStreamReader Reader) _objStmUncached;

        /// <summary>How many object-stream indexes this parser has read — a test's evidence that a stream is not re-read per member.</summary>
        internal int ObjectStreamIndexReads { get; private set; }

        /// <summary>Index entries read so far by this parser, charged against <see cref="PdfLimits.MaxObjStmIndexEntriesRead"/>.</summary>
        private long _objStmIndexEntriesRead;

        /// <summary>
        /// The decoded body of one object stream, together with its parsed offset index and the
        /// tokenizer/parser positioned over it.
        /// <para>
        /// <b>Why this is cached.</b> §7.5.7's index is a run of <c>/N</c> integer pairs at the
        /// front of the stream, and re-read in full for <i>every</i> object fetched, resolving all
        /// <c>N</c> objects of one stream would cost <c>N²</c> integer
        /// tokenizations, plus a throwaway <c>MemoryStream</c>, <c>PdfTokenizer</c>,
        /// <c>PdfParser</c> and <c>List</c> per object. A PDF 1.5+ producer puts nearly every
        /// non-stream object in an <c>/ObjStm</c>, so that was on the critical path of
        /// essentially every modern file: measured at <b>2.25× the time and 2.42× the
        /// allocation</b> of the same 50-page document written with a classic xref table.
        /// </para>
        /// </summary>
        private sealed class ObjectStreamReader
        {
            /// <summary>
            /// Bytes one <see cref="Entries"/> element occupies: two <c>int</c>s in a value tuple,
            /// which the runtime lays out without padding. Named rather than inlined because it is
            /// what the cache budget is counted in.
            /// </summary>
            public const int EntryByteSize = 2 * sizeof(int);

            /// <summary>Object number and byte offset (relative to <c>/First</c>) per contained object.</summary>
            public (int objNum, int offset)[] Entries;

            /// <summary>
            /// Indexes of entries whose offset an earlier entry already names, or null when there
            /// are none. ISO 32000-2 §7.5.7: "The byte offsets shall be in increasing order."
            /// </summary>
            public HashSet<int> SharedOffsets;
            /// <summary>Value of the stream's <c>/First</c> entry — where object bodies begin.</summary>
            public int First;
            /// <summary>Length of the decoded stream, for bounds-checking a computed offset.</summary>
            public int DecodedLength;
            public PdfTokenizer Tokenizer;
            public PdfParser Parser;
        }

        // [ThreadStatic] so a value read after one parse cannot belong to another thread's
        // parse (see the Diagnostics Pattern section of CLAUDE.md).
        [System.ThreadStatic]
        private static string _lastObjectParseError;

        /// <summary>
        /// The most recent error recorded when an individual object failed to parse
        /// <b>on the calling thread</b>.
        /// <c>null</c> when the last <see cref="GetObject"/> call on this thread succeeded.
        /// </summary>
        /// <remarks>
        /// <para>Backed by a <c>[ThreadStatic]</c> field: read it on the same thread that ran the
        /// parse. Concurrent parses on other threads do not overwrite this value.</para>
        ///
        /// <para><b>This is the only thing separating "the file has no such object" from "this file
        /// is malformed and I could not produce it".</b> Both come back as <c>PdfNull</c>, because
        /// <see cref="GetObject"/> and <see cref="ResolveObject"/> are called millions of times
        /// during a render and cannot throw on a file a reader is expected to tolerate. So the
        /// invariant is: <b>every <c>PdfNull</c> returned for a reason other than genuine absence
        /// sets this slot.</b> Exactly two paths return <c>PdfNull</c> and leave it null, and both
        /// mean absent — no cross-reference entry for the object number, and a free entry.</para>
        ///
        /// <para>Four malformed shapes set it too, rather than reading as an ordinary missing
        /// object: an object-stream index outside the stream's own entry table, an entry whose
        /// offset lies outside the decoded stream, a reference chain past
        /// <c>PdfLimits.MaxIndirectReferenceChain</c>, and a reference chain that loops.</para>
        ///
        /// <para><b>Read it immediately.</b> <see cref="GetObject"/> and
        /// <see cref="ResolveObject"/> both clear it at the start of every call, so the value
        /// belongs to the most recent one and to nothing earlier.</para>
        /// </remarks>
        public static string LastObjectParseError
        {
            get => _lastObjectParseError;
            private set => _lastObjectParseError = value;
        }

        // [ThreadStatic] for the same reason as _lastObjectParseError.
        [System.ThreadStatic]
        private static string _lastRootRecovery;

        /// <summary>
        /// How the most recent open <b>on the calling thread</b> found its catalog when the
        /// trailer's <c>/Root</c> did not name one (<see cref="TryRecoverRoot"/>), or <c>null</c>
        /// when the trailer's <c>/Root</c> resolved to a dictionary — the ordinary file — or no
        /// recovery was attempted.
        /// </summary>
        /// <remarks>
        /// <para>Six of the eight public-corpus files that would not open stopped at "Cannot find
        /// document catalog": a cross-reference table that read cleanly and a trailer whose
        /// <c>/Root</c> was missing or named an object that was not there (GAPS §7c). Other readers
        /// re-index the file and take the last <c>/Type /Catalog</c> object; this parser did so
        /// only when the cross-reference table itself was unreadable. The scan rebuilds the table
        /// from the objects' own headers — the latest copy of each in the file wins, as §7.5.6
        /// has it — so a document opened this way may read a different revision of an object than
        /// its cross-reference table named, which is what this slot says.</para>
        /// <para>Reset at the start of <see cref="ParseCrossReferences"/>, on every path.</para>
        /// </remarks>
        public static string LastRootRecovery
        {
            get => _lastRootRecovery;
            private set => _lastRootRecovery = value;
        }

        // [ThreadStatic] for the same reason as _lastObjectParseError.
        [System.ThreadStatic]
        private static string _lastXrefSectionError;

        /// <summary>
        /// Why a cross-reference section was skipped during the most recent
        /// <see cref="ParseCrossReferences"/> call <b>on the calling thread</b>, or <c>null</c>
        /// when every section named by the file was read.
        /// </summary>
        /// <remarks>
        /// A document still opens when a section is skipped — that is the documented behaviour, and
        /// the remaining sections (or a full scan) supply what they can. This is what tells a caller
        /// that the cross-reference table it is reading is incomplete, and why: a corrupt
        /// cross-reference stream, an offset outside the file, or the
        /// <see cref="PdfLimits.MaxXrefEntries"/> refusal.
        /// Reset at the start of <see cref="ParseCrossReferences"/>, so it never describes an
        /// earlier document.
        /// </remarks>
        public static string LastXrefSectionError
        {
            get => _lastXrefSectionError;
            private set => _lastXrefSectionError = value;
        }

        /// <summary>
        /// Cross-reference entries admitted so far by THIS parser, charged across every section of
        /// the <c>/Prev</c> / <c>/XRefStm</c> walk.
        /// </summary>
        /// <remarks>
        /// The cap is the parser's, not one section's: a cap per section would let the chain multiply
        /// it without limit — a measured 4 290-byte file minting 1 200 000 entries and 97 MB of heap by chaining sections whose <c>/Index</c> claimed disjoint object-number
        /// ranges, which the first-wins table cannot deduplicate.
        /// </remarks>
        private int _xrefEntriesParsed;

        /// <summary>
        /// Ceiling on <see cref="_xrefEntriesParsed"/>. Defaults to
        /// <see cref="PdfLimits.MaxXrefEntries"/>; settable only from inside the assembly so a
        /// guard test can exercise the refusal without building a file with millions of entries in
        /// it.
        /// </summary>
        internal int MaxCrossReferenceEntries { get; set; } = PdfLimits.MaxXrefEntries;

        /// <summary>
        /// The codec work the document's syntax streams may spend over its whole life — every object
        /// stream and cross-reference stream this parser reads, and every page's content
        /// (<c>PdfPage.GetContentStreamData</c>), each decode charging it as its parent
        /// (<see cref="PdfLimits.MaxSyntaxStreamDecodeWork"/>).
        /// </summary>
        /// <remarks>
        /// A JBIG2 stream's own budget is sized by its parameters, so a 212-byte <c>/Type /ObjStm</c>
        /// filtered through <c>/JBIG2Decode</c> spent the whole of it — three and a half seconds —
        /// and a damaged file whose headers let four object numbers parse into that one stream
        /// decoded it four times in each of the two recovery scans <c>PdfDocument.Open</c> runs:
        /// 1 108 bytes would open in 27 s (the coverage-guided fuzzer's <c>open</c> target).
        /// Each stream decoded is a distinct object, so a memo of failures would not have seen them
        /// as one; the budget counts them all. An object stream or cross-reference stream whose
        /// <c>/Filter</c> names an image codec is refused by name before any decode
        /// (<see cref="SyntaxStreamFilters"/>): samples are never syntax, and asking the codec cost
        /// this whole budget once for every open of the document. The budget bounds what remains —
        /// page content, which is syntax too, and any codec a syntax stream may carry.
        /// </remarks>
        private Util.WorkBudget _syntaxStreamWork;

        /// <summary>The budget above, made on first use of <see cref="MaxSyntaxStreamDecodeWork"/>.</summary>
        internal Util.WorkBudget SyntaxStreamWork => _syntaxStreamWork ??= new Util.WorkBudget(
            MaxSyntaxStreamDecodeWork,
            "decoding this document's object, cross-reference and content streams (PdfLimits.MaxSyntaxStreamDecodeWork)");

        /// <summary>
        /// Ceiling of <see cref="SyntaxStreamWork"/>. Defaults to
        /// <see cref="PdfLimits.MaxSyntaxStreamDecodeWork"/>; settable from inside the assembly, before
        /// the first decode, so a test can spend it with a small stream.
        /// </summary>
        internal long MaxSyntaxStreamDecodeWork { get; set; } = PdfLimits.MaxSyntaxStreamDecodeWork;

        public PdfParser(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _tokenizer = new PdfTokenizer(stream);
        }

        public CrossReferenceTable CrossReferenceTable => _xrefTable;
        public PdfDictionary Trailer => _trailer;

        /// <summary>
        /// The byte offset of the most-recent xref table/stream, as read from the
        /// <c>startxref</c> keyword.  Valid after <see cref="ParseCrossReferences"/> has run.
        /// </summary>
        /// <remarks>
        /// The value as the file wrote it, which ISO 32000-2 §7.5.2 measures from the header's
        /// percent sign rather than from byte 0 of the stream — so it is also the value an appended
        /// revision's <c>/Prev</c> has to carry. The two differ only for a file with bytes in front of
        /// its header; see <see cref="HeaderOffset"/>.
        /// </remarks>
        public long StartXrefOffset { get; private set; }

        /// <summary>
        /// Stream position of the <c>%</c> that begins the <c>%PDF-</c> header, or 0 when the header
        /// search window holds none. Located by <see cref="ParseCrossReferences"/> through
        /// <see cref="PdfHeaderLocator"/>, the one search this parser shares with the incremental
        /// writer.
        /// </summary>
        /// <remarks>
        /// <para>ISO 32000-2 §7.5.2: "The PDF file begins with the 5 characters “%PDF–” and byte offsets
        /// shall be calculated from the PERCENT SIGN (25h)." NOTE 1: "This provision allows for
        /// arbitrary bytes preceding the %PDF- without impacting the viability of the PDF file and its
        /// byte offsets." So <c>startxref</c>, <c>/Prev</c>, <c>/XRefStm</c> and every in-use
        /// cross-reference entry count from here; a <see cref="CrossReferenceEntry.Offset"/> is stored
        /// as the stream position it names, because that is what every consumer indexing raw bytes
        /// with it needs.</para>
        /// <para>ISO 32000-1 §7.5.2 made the header "the first line of a PDF file", so for a conforming
        /// PDF 1.x file this is 0 and nothing moves.</para>
        /// </remarks>
        internal long HeaderOffset { get; private set; }

        /// <summary>
        /// How many cross-reference sections the most recent <see cref="ParseCrossReferences"/> found
        /// only at the byte-0 reading of their offset — the fallback <see cref="ParseXrefSection"/>
        /// takes when a file's header is not at byte 0 and a section is not where §7.5.2 says. Zero
        /// when every section was where the file's own header said it would be.
        /// </summary>
        /// <remarks>
        /// The fallback succeeds silently — nothing else distinguishes a file read through it from
        /// one read as written — so this is what lets a test of a writer say that its output needed
        /// no fallback, and a test of the fallback say that it was exercised at all.
        /// </remarks>
        internal int SectionsReadFromByteZero { get; private set; }

        private readonly List<long> _sectionPositions = new List<long>();

        /// <summary>
        /// Stream position of every cross-reference section the most recent
        /// <see cref="ParseCrossReferences"/> read, in the order the chain was followed: newest
        /// first, a classic section's <c>/XRefStm</c> stream before its <c>/Prev</c>. Empty after
        /// scan recovery, and a section that could not be read is not in it.
        /// </summary>
        /// <remarks>
        /// Positions, not the offsets as written: the two differ by <see cref="HeaderOffset"/> for a
        /// section found where §7.5.2 says, and not at all for one found at the byte-0 reading — and
        /// a caller after revision boundaries wants positions. It exists so that a host need not
        /// re-parse the document once per <c>/Prev</c> hop to learn what one walk already knew.
        /// </remarks>
        internal IReadOnlyList<long> CrossReferenceSectionPositions => _sectionPositions;

        private readonly List<long> _revisionPositions = new List<long>();

        /// <summary>
        /// <see cref="CrossReferenceSectionPositions"/> without the hybrid <c>/XRefStm</c> streams:
        /// the stream position of the section <c>startxref</c> names and of every section a
        /// <c>/Prev</c> reached, newest first — one per revision, which is what a caller counting
        /// revisions wants. §7.5.8.4 makes an <c>/XRefStm</c> stream part of the revision whose
        /// classic section names it, not a revision of its own.
        /// </summary>
        internal IReadOnlyList<long> RevisionSectionPositions => _revisionPositions;

        // [ThreadStatic] for the same reason as _lastObjectParseError.
        [System.ThreadStatic]
        private static string _lastHeaderRecovery;

        /// <summary>
        /// What the most recent <see cref="ParseHeader"/> <b>on the calling thread</b> could not
        /// read and what it assumed — no <c>%PDF-</c> marker in the first 1024 bytes, or a version
        /// after it that does not parse, each taken as <see cref="AssumedVersion"/> — or <c>null</c>
        /// when the header read as written.
        /// </summary>
        /// <remarks>
        /// <para>Two of the eight public-corpus files that would not open stopped at the header
        /// (GAPS §7c): one with no <c>%PDF-</c> at all, one whose version would not parse. Other readers
        /// open both; the header decides nothing but the version this library reports and
        /// gates PDF/X's X12 on, and offsets already count from byte 0 when there is no marker
        /// (<see cref="PdfHeaderLocator.OriginOf(Stream)"/>). So the parse goes on, with the
        /// assumption named here. A file that is not a PDF at all gets no further than the
        /// catalog, where <c>PdfDocument</c> refuses it and says the header was missing too.</para>
        /// <para>Reset at the start of <see cref="ParseHeader"/>, on every path.</para>
        /// </remarks>
        public static string LastHeaderRecovery
        {
            get => _lastHeaderRecovery;
            private set => _lastHeaderRecovery = value;
        }

        /// <summary>The version a file whose header says none readable is taken to be.</summary>
        internal static readonly PdfVersion AssumedVersion = new PdfVersion(1, 4);

        public PdfVersion ParseHeader()
        {
            LastHeaderRecovery = null;

            // PDF spec §7.5.2: the header must appear within the first 1024 bytes.
            // Some PDFs have garbage before %PDF- (e.g., HTML preamble from web servers).
            byte[] window = PdfHeaderLocator.ReadSearchWindow(_stream, out int read);

            int pdfIndex = PdfHeaderLocator.IndexOf(window, read);
            if (pdfIndex < 0)
            {
                LastHeaderRecovery = $"no %PDF- header in the first {PdfHeaderLocator.SearchWindowBytes} bytes; " +
                                     $"the version is taken as {AssumedVersion} and offsets count from byte 0.";
                return AssumedVersion;
            }

            // One char per byte, so the locator's index is an index into the text.
            var header = Encoding.ASCII.GetString(window, 0, read);
            int versionStart = pdfIndex + PdfHeaderLocator.Marker.Length;
            int versionEnd = versionStart;
            while (versionEnd < header.Length && (char.IsDigit(header[versionEnd]) || header[versionEnd] == '.'))
                versionEnd++;
            string written = header.Substring(versionStart, versionEnd - versionStart);

            // A dot at either end leaves an empty half, which TryParse refuses; no bounds check beside it.
            int dotIndex = written.IndexOf('.');
            if (dotIndex >= 0
                && int.TryParse(written.Substring(0, dotIndex), out int major)
                && int.TryParse(written.Substring(dotIndex + 1), out int minor))
                return new PdfVersion(major, minor);

            LastHeaderRecovery = $"the header's version '{written}' does not parse as <major>.<minor>; " +
                                 $"it is taken as {AssumedVersion}.";
            return AssumedVersion;
        }

        /// <summary>The trailer keyword whose operand is the offset of the last xref section.</summary>
        private const string StartXrefKeyword = "startxref";

        /// <summary>
        /// How many bytes at the END of the file <see cref="FindStartXref"/> scans for
        /// <see cref="StartXrefKeyword"/>. Distinct from <see cref="PdfHeaderLocator.SearchWindowBytes"/>,
        /// which is a window at the START of the file; the two happen to be the same size but
        /// are governed by different parts of the spec and may be tuned independently.
        /// </summary>
        private const int StartXrefSearchWindowBytes = 1024;

        public long FindStartXref()
        {
            // Search from the end of the file for "startxref"
            long searchStart = Math.Max(0, _stream.Length - StartXrefSearchWindowBytes);
            _stream.Position = searchStart;

            var buffer = new byte[_stream.Length - searchStart];
            // A single Read may underfill the buffer; fill until full or EOF.
            int read = StreamBytes.Fill(_stream, buffer, 0, buffer.Length);
            string tail = Encoding.ASCII.GetString(buffer, 0, read);

            int idx = tail.LastIndexOf(StartXrefKeyword, StringComparison.Ordinal);
            if (idx < 0)
                throw new PdfParseException("Cannot find 'startxref' keyword.");

            // Parse the offset after "startxref".
            //
            // §7.2.3 Table 1 defines SIX whitespace bytes, and this accepted three of them: NUL,
            // TAB and FORM FEED were missing, so "startxref\t123" — perfectly conforming — found no
            // digits, threw, and sent the whole document down RecoverXrefByScanning with
            // StartXrefOffset left at 0. PdfConstants.IsWhitespace is the predicate this same file
            // already uses in three other places; the inline set was the odd one out.
            int numStart = idx + StartXrefKeyword.Length;
            while (numStart < tail.Length && PdfConstants.IsWhitespace((byte)tail[numStart]))
                numStart++;

            int numEnd = numStart;
            while (numEnd < tail.Length && char.IsDigit(tail[numEnd]))
                numEnd++;

            string offsetStr = tail.Substring(numStart, numEnd - numStart);
            if (!long.TryParse(offsetStr, out long offset))
                throw new PdfParseException("Cannot parse startxref offset.");

            return offset;
        }

        // ISO-8859-1 (Latin-1) maps every byte 0x00-0xFF 1:1 to the same code point, which is
        // how PDF literal strings round-trip raw bytes through a string.  Resolving the encoding
        // via Encoding.GetEncoding does a registry/provider lookup, so cache one instance rather
        // than re-resolving it per literal-string token.
        // Superseded by Text.Latin1Text.Encode, which is a loop: there is no provider lookup to
        // cache and no type initializer on a type the parser touches on its first string token.

        public void ParseCrossReferences()
        {
            // Cleared at the START of the operation that writes it: LastXrefSectionError describes
            // THIS document's cross-reference walk, so a refusal recorded while opening an earlier
            // one must not be readable after a later clean open (CLAUDE.md, Diagnostics Pattern).
            LastXrefSectionError = null;
            LastRootRecovery = null;
            _xrefEntriesParsed = 0;
            _xrefTable = new CrossReferenceTable();

            // The origin every offset below is counted from (§7.5.2). Located here rather than left to
            // ParseHeader, which PdfRepair and PdfIncrementalSaveValidator never call first.
            HeaderOffset = PdfHeaderLocator.OriginOf(_stream);
            SectionsReadFromByteZero = 0;
            _sectionPositions.Clear();
            _revisionPositions.Clear();

            long xrefOffset;
            try
            {
                xrefOffset = FindStartXref();
            }
            catch (PdfParseException)
            {
                // startxref missing or corrupt — attempt full recovery.
                RecoverXrefByScanning();
                return;
            }
            StartXrefOffset = xrefOffset;

            // Iterative traversal of the /Prev chain (and /XRefStm for hybrid PDFs).
            // Using a queue avoids stack-overflow on deeply incremental documents.
            var queue = new Queue<XrefSectionRef>();
            var visited = new HashSet<long>();
            queue.Enqueue(new XrefSectionRef(xrefOffset, replaceableFreeEntries: null));
            _trailer = new PdfDictionary();
            bool anyParsed = false;
            bool anySkipped = false;

            while (queue.Count > 0)
            {
                XrefSectionRef section = queue.Dequeue();
                long offset = section.Offset;
                if (!visited.Add(offset))
                    continue;

                PdfDictionary sectionTrailer;
                HashSet<int> installedFreeEntries;
                try
                {
                    sectionTrailer = ParseXrefSection(section, out installedFreeEntries);
                    anyParsed = true;
                }
                catch (Exception ex)
                {
                    // Skip an unreadable xref section and keep going — but say why. Catching only
                    // PdfParseException would miss the decode path, which throws neither
                    // (FlateDecodeFilter throws the base PdfException, FilterPipeline throws
                    // PdfFilterException, which derives from Exception directly), so a corrupt
                    // xref STREAM would escape both this skip and the scan-recovery fallback below and
                    // made an otherwise recoverable file unopenable.
                    // The wording carries both halves deliberately: the offset immediately after
                    // "at" locates the section, and "was skipped" is what tells a caller the
                    // document it is now reading came from a recovered or older state rather than
                    // from the section the file pointed at.
                    LastXrefSectionError =
                        $"xref section at {offset.ToString(CultureInfo.InvariantCulture)} " +
                        $"was skipped: {ex.Message}";

                    // A spent entry budget is a refusal, not a damaged section: every remaining
                    // section would fail the same way, and reaching them still costs a decode each.
                    // Nor does it ask for the gap scan below, which would admit the entries the
                    // budget just refused.
                    if (_xrefEntriesParsed >= MaxCrossReferenceEntries)
                        break;
                    anySkipped = true;
                    continue;
                }

                // Merge: entries already in _trailer (from newer xref sections) take priority.
                foreach (var kvp in sectionTrailer)
                {
                    if (!_trailer.ContainsKey(kvp.Key))
                        _trailer.Set(kvp.Key, kvp.Value);
                }

                // Hybrid PDF (§7.5.8.4): a traditional xref table may carry /XRefStm pointing at an
                // xref stream that belongs to THIS revision. It is enqueued before /Prev because
                // CrossReferenceTable is first-wins: the contemporary stream has to be merged ahead
                // of the older section, or an object superseded in this revision resolves to the
                // previous revision's copy — a stale form value, a pre-redaction content stream.
                //
                // It carries the free entries THIS section's table installed, which are the only ones
                // its entries may displace. §7.5.8.4 ranks the stream below its own section's table
                // and above the previous section, and it lets the stream look past "the free entry in
                // the previous section" — never past one a NEWER section wrote, which the walk installed
                // first and which is a deletion. Letting it displace any free entry brought back an
                // object a later classic update had deleted.
                var xrefStm = sectionTrailer.GetLong(PdfName.XRefStm);
                if (xrefStm.HasValue && IsUsableSectionOffset(xrefStm.Value))
                    queue.Enqueue(new XrefSectionRef(xrefStm.Value, replaceableFreeEntries: installedFreeEntries, hybrid: true));

                // Older xref section
                var prev = sectionTrailer.GetLong(PdfName.Prev);
                if (prev.HasValue && IsUsableSectionOffset(prev.Value))
                    queue.Enqueue(new XrefSectionRef(prev.Value, replaceableFreeEntries: null));
            }

            // If no xref section could be parsed, try full recovery.
            if (!anyParsed)
                RecoverXrefByScanning();
            // If one was skipped while others parsed, the objects only the skipped one listed are
            // missing — and nothing else would notice. A damaged table that is the file's only
            // section takes the full recovery above, and the document reads; append one intact
            // update (any incremental save) and that section is now an OLDER one, skipped on the
            // walk while the new one parses, so the objects it alone named vanished. A page among
            // them made the saved file one page shorter than the file it was saved from (public
            // corpus, filled-background.pdf).
            else if (anySkipped)
                FillGapsByScanning();
        }

        /// <summary>
        /// Adds, for every object the parsed sections do not mention at all, the last
        /// <c>N G obj</c> header for it the file holds — what a skipped section would have named.
        /// </summary>
        /// <remarks>
        /// Gaps only: an entry a parsed section wrote, a free entry included, stays — a newer
        /// section's word on an object outranks its position in the file, and a free entry is a
        /// deletion. Among several headers for one missing object the last wins, as in
        /// <see cref="RecoverXrefByScanning"/>: §7.5.6 appends updates at the end.
        /// </remarks>
        private void FillGapsByScanning()
        {
            // AddEntry is the table's first-wins rule, and it does not replace a free entry: what the
            // parsed sections said, a deletion included, stands, and only the gaps are filled.
            foreach (CrossReferenceEntry entry in LastHeaders().Values)
                _xrefTable.AddEntry(entry);
        }

        /// <summary>
        /// For every object number the file holds a <c>N G obj</c> header for, the entry the last such
        /// header makes — what <see cref="FillGapsByScanning"/> adds for the objects no parsed section
        /// names, and what <see cref="GetObject"/> falls back to for an object a section misplaces
        /// (GAPS §19h item 5). Built once, on first need, over the first
        /// <see cref="MaxRecoveryScanBytes"/> of the file; among several headers for one object the
        /// last wins, as in <see cref="RecoverXrefByScanning"/>, since §7.5.6 appends updates at the end.
        /// </summary>
        private Dictionary<int, CrossReferenceEntry> _lastHeaders;

        /// <summary>Every header found for each object number, in file order — what <see cref="FindHeaderEntry"/> searches below a bound.</summary>
        private Dictionary<int, List<CrossReferenceEntry>> _allHeaders;

        private Dictionary<int, CrossReferenceEntry> LastHeaders()
        {
            if (_lastHeaders != null) return _lastHeaders;

            var lastHeader = new Dictionary<int, CrossReferenceEntry>();
            var allHeaders = new Dictionary<int, List<CrossReferenceEntry>>();
            ScanObjectHeaders(found =>
            {
                lastHeader[found.ObjectNumber] = found;
                if (!allHeaders.TryGetValue(found.ObjectNumber, out var list))
                    allHeaders[found.ObjectNumber] = list = new List<CrossReferenceEntry>(1);
                list.Add(found);
            }, out _, out _);
            _allHeaders = allHeaders;
            _lastHeaders = lastHeader;
            return lastHeader;
        }

        /// <summary>
        /// Bytes two consecutive recovery windows share when no header was taken in the first, so a
        /// header the window's end cuts through is read whole by the next.
        /// </summary>
        private const int RecoveryWindowOverlapBytes = 4096;

        /// <summary>Where the bytes <see cref="_recoveryContent"/> transcribes begin in the file: 0 unless the file is longer than one window, when it is the last window's start.</summary>
        private long _recoveryContentStart;

        /// <summary>
        /// Every <c>N G obj</c> header in the file, in file order, each handed to <paramref name="onHeader"/>
        /// as the entry it makes — read in windows of <see cref="MaxRecoveryScanBytes"/>, so a file longer
        /// than one window is scanned to its end rather than to the window's (otherwise a newer copy of
        /// an object appended past the first 64 MB would lose to an older one inside it, and the
        /// trailer at the end of such a file would never be read). Each window steps over stream data as
        /// <see cref="ObjectHeaderScanner.ResumeAfter"/> does. The next window begins at the last header
        /// this one took, so an object the window's end cut through is read whole — its header found
        /// again and not taken twice, its stream data stepped over; a window that took none begins the
        /// next <see cref="RecoveryWindowOverlapBytes"/> before its end. The work is linear in the file:
        /// a boundary costs at most one window read again. Returns the last window's bytes, with how many
        /// were read and where they begin, for the trailer and catalog searches over the file's tail.
        /// </summary>
        private byte[] ScanObjectHeaders(Action<CrossReferenceEntry> onHeader, out int lastWindowRead, out long lastWindowStart)
        {
            long length = _stream.Length;
            int window = (int)Math.Max(0, Math.Min(Math.Min(length, MaxRecoveryScanBytes), int.MaxValue / 2));
            var buf = new byte[window];
            long windowStart = 0;
            long lastAccepted = -1;
            lastWindowRead = 0;
            lastWindowStart = 0;
            while (true)
            {
                _stream.Position = windowStart;
                int read = StreamBytes.Fill(_stream, buf, 0, buf.Length);
                lastWindowRead = read;
                lastWindowStart = windowStart;

                long lastAcceptedHere = -1;
                int scanPos = 0;
                bool budgetSpent = false;
                var resume = new ObjectHeaderScanner.ResumeCursor();
                while (ObjectHeaderScanner.TryFindNext(buf, read, scanPos, out var header))
                {
                    // Past the object's stream data: a header inside it is the attachment's, not this file's.
                    scanPos = ObjectHeaderScanner.ResumeAfter(buf, read, header.KeywordEnd, resume);
                    long offset = windowStart + header.Offset;
                    if (offset <= lastAccepted) continue;
                    // A generation that will not parse skips the object: an entry whose generation
                    // contradicts the header is treated as stale by a strict reader, so it is worse
                    // than a missing one. (PdfRepair defaults it to 0 instead, and is right to —
                    // it copies the header verbatim.)
                    if (!ObjectHeaderScanner.TryParseDigits(
                            buf, header.GenerationStart, header.GenerationLength, out int genNum))
                        continue;
                    // Charged against the document's cross-reference budget like every parsed entry
                    // and every recovered object-stream member: the scan reads the whole file, and
                    // uncharged it rebuilt a table of one entry per eight bytes of a file of bare
                    // headers, with nothing bounding it but the file.
                    if (_xrefEntriesParsed >= MaxCrossReferenceEntries)
                    {
                        LastXrefSectionError = RecoveryStoppedAtBudget;
                        budgetSpent = true;
                        break;
                    }
                    _xrefEntriesParsed++;
                    lastAccepted = lastAcceptedHere = offset;
                    onHeader(new CrossReferenceEntry
                    {
                        ObjectNumber = header.ObjectNumber,
                        GenerationNumber = genNum,
                        Offset = offset,
                        Type = CrossReferenceEntry.EntryType.Normal,
                    });
                }

                long windowEnd = windowStart + read;
                if (budgetSpent || read == 0 || windowEnd >= length) break;
                long next = lastAcceptedHere > windowStart ? lastAcceptedHere : windowEnd - RecoveryWindowOverlapBytes;
                if (next <= windowStart) next = windowEnd;
                windowStart = next;
            }
            return buf;
        }

        /// <summary>One entry of the <c>/Prev</c> / <c>/XRefStm</c> walk queue.</summary>
        private readonly struct XrefSectionRef
        {
            internal XrefSectionRef(long offset, HashSet<int> replaceableFreeEntries, bool hybrid = false)
            {
                Offset = offset;
                ReplaceableFreeEntries = replaceableFreeEntries;
                Hybrid = hybrid;
            }

            /// <summary>The offset as the file wrote it, counted from the header (§7.5.2).</summary>
            internal long Offset { get; }

            /// <summary>
            /// Named by a classic trailer's <c>/XRefStm</c> — part of that section's revision
            /// (§7.5.8.4), so recorded among the sections read but not among the revisions.
            /// </summary>
            internal bool Hybrid { get; }

            /// <summary>
            /// For a section named by a classic trailer's <c>/XRefStm</c>: the object numbers whose
            /// <b>free</b> entry the table of that same section installed — the only installed entries
            /// this section's may displace (§7.5.8.4). <c>null</c> for every other section, and for
            /// an <c>/XRefStm</c> whose section installed no free entry.
            /// </summary>
            internal HashSet<int> ReplaceableFreeEntries { get; }
        }

        /// <summary>
        /// Reads the section <paramref name="section"/> names — at the position §7.5.2 gives its offset,
        /// and, only when that fails and the header does not start at byte 0, at the position the same
        /// number names counted from byte 0 of the stream.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the §7.5.2 reading.</b> ISO 32000-2 §7.5.2: "byte offsets shall be calculated
        /// from the PERCENT SIGN (25h)" of the header, "This provision allows for arbitrary bytes
        /// preceding the %PDF- without impacting the viability of the PDF file and its byte offsets."
        /// Sought as a stream position, every offset of a file with bytes in front of its header would
        /// lose the chain, in one of two ways decided by whatever the misaligned bytes held: the
        /// section failing and the file rebuilt by scanning — which serves an object its newest
        /// revision deleted — or a fragment happening to parse as a section, and every object only an
        /// older revision defines was unreachable.</para>
        /// <para><b>Why the byte-0 fallback, and why per section.</b> A writer that counts from byte 0
        /// of an output already holding bytes writes it, and it is a prefixed shape this parser reads. <c>IncrementalUpdateWriter</c>'s six-argument
        /// <c>WriteUpdate</c> — the one 25 of the 28 <c>Base.Operations</c> writers use — does exactly
        /// that when handed a prefixed original: it records the revision it appends at positions in its
        /// output, while the <c>/Prev</c> it writes is the caller's <see cref="StartXrefOffset"/>,
        /// counted from the header. One origin for a whole document cannot follow that. The fallback
        /// never outranks the §7.5.2 reading: when both positions hold a section, the §7.5.2 one is the
        /// file's.</para>
        /// <para>When both readings fail, the §7.5.2 reading's failure is the one rethrown — it is the
        /// reading a conforming file is judged by.</para>
        /// </remarks>
        private PdfDictionary ParseXrefSection(XrefSectionRef section, out HashSet<int> installedFreeEntries)
        {
            long origin = HeaderOffset;
            try
            {
                PdfDictionary trailer = ParseXrefSectionAt(section.Offset + origin, origin,
                    section.ReplaceableFreeEntries, out installedFreeEntries);
                RecordSection(section, section.Offset + origin);
                return trailer;
            }
            catch (Exception) when (origin > 0)
            {
                // A spent entry budget needs no filter here: the second reading is refused at its first
                // entry, before it can change the table, and the walk stops on the rethrown refusal.
                PdfDictionary byteZeroTrailer = null;
                installedFreeEntries = null;
                try
                {
                    byteZeroTrailer = ParseXrefSectionAt(section.Offset, 0, section.ReplaceableFreeEntries,
                        out installedFreeEntries);
                }
                catch (Exception)
                {
                    // Dropped on purpose: the §7.5.2 reading's failure is rethrown below.
                }

                if (byteZeroTrailer != null)
                {
                    SectionsReadFromByteZero++;
                    RecordSection(section, section.Offset);
                    return byteZeroTrailer;
                }
                throw;
            }
        }

        /// <summary>
        /// Records a section the walk read, at the position it was found, in both lists — the
        /// revision list only for a section <c>startxref</c> or a <c>/Prev</c> named.
        /// </summary>
        private void RecordSection(XrefSectionRef section, long position)
        {
            _sectionPositions.Add(position);
            if (!section.Hybrid) _revisionPositions.Add(position);
        }

        /// <summary>
        /// Whether a <c>/Prev</c> or <c>/XRefStm</c> value can name a cross-reference section at
        /// all. A negative one reaches <see cref="Stream.Position"/> and throws
        /// <see cref="ArgumentOutOfRangeException"/> — not a <see cref="PdfException"/> — straight
        /// out of the document open; offset 0 is the <c>%PDF</c> header, and an offset at or past
        /// EOF names nothing. Dropping the value leaves the rest of the chain intact.
        /// </summary>
        /// <remarks>
        /// Judged on the value as written, which is inside the file under at least one of the two
        /// readings <see cref="ParseXrefSection"/> tries; the position either reading lands on is
        /// range-checked where it is sought.
        /// </remarks>
        private bool IsUsableSectionOffset(long offset)
        {
            if (offset > 0 && offset < _stream.Length) return true;

            LastXrefSectionError =
                $"cross-reference offset {offset.ToString(CultureInfo.InvariantCulture)} is outside the file; the section was skipped.";
            return false;
        }

        /// <summary>
        /// Ceiling on the buffer <see cref="RecoverXrefByScanning"/> reads the file into. The
        /// rationale lives with the value in <see cref="PdfLimits.MaxXrefRecoveryScanBytes"/>: this
        /// is a DoS cap, and CLAUDE.md makes <c>PdfLimits</c> the one place a maintainer hardening
        /// the library has to look.
        /// </summary>
        private static long MaxRecoveryScanBytes => PdfLimits.MaxXrefRecoveryScanBytes;

        /// <summary>
        /// The transcription the last <see cref="RecoverXrefByScanning"/> searched, held only
        /// between that scan and <see cref="TryRecoverRoot"/>'s second look at the catalog.
        /// </summary>
        private string _recoveryContent;

        /// <summary>
        /// Recovery mode: scan the entire stream for "N G obj" patterns to rebuild
        /// the cross-reference table from scratch.
        /// Also searches for a trailer dictionary so /Root and /Info are available.
        /// </summary>
        private void RecoverXrefByScanning()
        {
            _trailer = _trailer ?? new PdfDictionary();
            _xrefTable = _xrefTable ?? new CrossReferenceTable();

            // What failed was judged against the table this scan replaces: an object an entry placed
            // inside another's dictionary is readable at the offset its own header gives. So the
            // failures, and the null answers they cached, go.
            _failedObjects?.Clear();
            _failedObjectStreams?.Clear();
            var unreadable = new List<int>();
            foreach (var kv in _objectCache)
                if (kv.Value == null || kv.Value is PdfNull) unreadable.Add(kv.Key);
            foreach (int number in unreadable) _objectCache.Remove(number);

            // Scan for "N G obj" patterns, over the whole file in windows (ScanObjectHeaders).
            //
            // Header recognition itself lives in ObjectHeaderScanner, over the raw bytes rather
            // than the transcription below, shared with Base's PdfRepair: a second, STRICTER copy
            // — a literal " obj", a digit demanded immediately before it — would drop three legal
            // spellings (`12 0\nobj`, `12 0 obj<<…`, `12 0  obj`) this recovers, and write an empty
            // file with
            // an empty error list. One rule, two callers. Stepping over stream data is the scanner's
            // too: a `3 0 obj` inside an uncompressed embedded file replaced the real object 3 — the
            // hazard RawObjectScanner documents for the repair scan.
            //
            // Every header the scan accepts, in file order — what RecoverTrailer needs to tell a
            // `trailer` keyword inside an object from the file's own.
            var headerPositions = new List<long>();
            byte[] tail = ScanObjectHeaders(entry =>
            {
                // Recovery scans the file front-to-back, so the LAST "N G obj" we
                // see for a given object number is the most recent revision in an
                // incrementally-updated PDF.  Use SetEntry (last-wins) rather than
                // AddEntry (first-wins, which would pin the OLDEST revision).
                _xrefTable.SetEntry(entry);
                headerPositions.Add(entry.Offset);
            }, out int tailRead, out long tailStart);

            // The trailer and catalog searches below still work over a transcription — of the last
            // window, which is the whole file when it fits one and the file's tail when it does not:
            // where §7.5.6 puts every later trailer and the latest copy of what changed. Kept for
            // TryRecoverRoot, which looks again after a recovered trailer has named a /Root that
            // resolves to nothing; it lets it go.
            string content = Encoding.ASCII.GetString(tail, 0, tailRead);
            _recoveryContent = content;
            _recoveryContentStart = tailStart;

            // Every stage below settles a conflict the same way the scan above does: the copy written
            // LATEST in the file wins. §7.5.6 appends every update to the end of the file ("changes
            // shall be appended to the end of the file") and requires that "the most recent copy of
            // each object shall be the one accessed", and once the cross-reference data is unusable,
            // position is the only evidence of recency left.
            RecoverTrailer(content, headerPositions);
            RecoverCompressedObjects(content);
            RecoverRootIfMissing(content);
        }

        /// <summary>
        /// The dictionary keys a cross-reference stream carries to describe its own section rather
        /// than the document — what <see cref="RecoverTrailer"/> leaves out of a recovered trailer.
        /// </summary>
        private static bool IsCrossReferenceStreamPlumbing(PdfName key) =>
            key == PdfName.Type || key == PdfName.W || key == PdfName.Index || key == PdfName.Prev ||
            key == PdfName.Filter || key == PdfName.Length || key == PdfName.DecodeParms;

        /// <summary>
        /// Rebuilds the trailer from every trailer dictionary the scan can see — the one after the last
        /// literal <c>trailer</c> keyword, and each recovered <c>/Type /XRef</c> stream's — newest in
        /// the file first.
        /// </summary>
        /// <remarks>
        /// <para>A cross-reference-stream section has no <c>trailer</c> keyword: §7.5.8.2 has the stream
        /// dictionary carry the "entries common to all streams" and those of "trailer dictionaries
        /// ("Table 15 — Entries in the file trailer dictionary")". Taking the keyword's dictionary
        /// first and consulting a stream's only when no <c>/Root</c> has turned up, a later revision
        /// written with a cross-reference stream alone — a new <c>/Info</c>, a new <c>/Root</c> —
        /// would lose to the older classic trailer before it.</para>
        /// <para>First-wins in newest-first order is the merge the <c>/Prev</c> walk performs: a later
        /// revision's entry outranks an earlier one, and an entry only an earlier revision states
        /// still fills in. Every <c>trailer</c> keyword is read that way too, newest first and at
        /// most <see cref="PdfLimits.MaxRecoveredTrailerDictionaries"/> of them: only the last used to
        /// be, and §7.5.6 does make each added trailer carry every entry of the one before it — but
        /// the files recovery exists for are the ones that broke that, a tail cut off inside the last
        /// trailer dictionary or an update whose producer dropped <c>/Info</c>. An earlier keyword's
        /// dictionary ranks below every later source and fills in only what they lack.</para>
        /// <para>Reading more than the last keyword reaches ones the last never was: a
        /// <c>trailer</c> inside an uncompressed embedded PDF, the tail of a longer word. The two
        /// rules <c>Base.Operations.Documents.PdfRepair</c> applies to the same keywords apply here
        /// — see <see cref="IsFileTrailerKeyword"/> — and a rejected candidate still counts against
        /// the cap, because judging it costs a scan back to the previous <c>endobj</c>.</para>
        /// </remarks>
        private void RecoverTrailer(string content, List<long> headerPositions)
        {
            var sources = new List<(long position, PdfDictionary dictionary, bool isStream)>();

            // Bounded, because each keyword costs a dictionary parse — one that never closes is
            // parsed to the end of the scanned region — and a hostile file can hold the seven-byte
            // keyword every seven bytes.
            int consulted = 0;
            for (int keyword = LastIndexBefore(content, TrailerKeyword, content.Length);
                 keyword >= 0 && consulted < PdfLimits.MaxRecoveredTrailerDictionaries;
                 keyword = LastIndexBefore(content, TrailerKeyword, keyword))
            {
                consulted++;
                if (!IsFileTrailerKeyword(content, _recoveryContentStart, keyword, headerPositions)) continue;
                try
                {
                    _tokenizer.Seek(_recoveryContentStart + keyword + TrailerKeyword.Length);
                    _tokenizer.SkipWhitespaceAndComments();
                    if (ParseDirectObject() is PdfDictionary dict)
                        sources.Add((_recoveryContentStart + keyword, dict, false));
                }
                catch
                {
                    // Recovery is best-effort.
                }
            }

            var xrefStreams = new List<CrossReferenceEntry>();
            foreach (var entry in _xrefTable.AllEntries)
            {
                if (RecoveredObjectMentions(content, entry, "/XRef")) xrefStreams.Add(entry);
            }

            foreach (var entry in xrefStreams)
            {
                if (!(GetObject(entry.ObjectNumber) is PdfStream stream)) continue;
                if (stream.GetName(PdfName.Type) != PdfName.XRef) continue;
                sources.Add((entry.Offset, stream, true));
            }

            sources.Sort((a, b) => b.position.CompareTo(a.position));

            foreach (var (_, dictionary, isStream) in sources)
            {
                foreach (var kvp in dictionary)
                {
                    // The cross-reference plumbing describes the section this file no longer has a
                    // usable pointer to; only the trailer half of the dictionary is meaningful here.
                    if (isStream && IsCrossReferenceStreamPlumbing(kvp.Key)) continue;
                    if (!_trailer.ContainsKey(kvp.Key))
                        _trailer.Set(kvp.Key, kvp.Value);
                }
            }
        }

        /// <summary>The keyword that introduces a classic section's trailer dictionary (§7.5.5).</summary>
        private const string TrailerKeyword = "trailer";

        /// <summary>
        /// Whether the <c>trailer</c> at <paramref name="keyword"/> is the file's: it starts a token
        /// rather than ending a longer word (<c>Xtrailer</c>), and it lies outside every object the
        /// scan found — an uncompressed embedded PDF carries a trailer of its own. The same two
        /// rules <c>Base.Operations.Documents.PdfRepair</c> applies before adopting the parser's
        /// trailer, which is how a keyword this accepted and that rejected would reach its output.
        /// </summary>
        private static bool IsFileTrailerKeyword(string content, long contentStart, int keyword, List<long> headerPositions)
        {
            // The start of a token. What follows needs no such test: anything glued on is parsed as
            // the dictionary, and is not one.
            if (keyword > 0 && !PdfConstants.IsEndOfToken((byte)content[keyword - 1])) return false;
            return !InsideAnObject(content, contentStart, headerPositions, keyword);
        }

        /// <summary>
        /// Whether <paramref name="position"/> (an index into <paramref name="content"/>, which
        /// transcribes the file from <paramref name="contentStart"/>) lies inside an object the scan
        /// found: the last <c>N G obj</c> header before it has no <c>endobj</c> between the two. The
        /// repair tool answers this from object extents; the scan has only headers, so an object ends
        /// at the keyword that closes it — a stream whose data spells <c>endobj</c> can fool this, and
        /// the cost is one trailer source too many, capped like the rest. A header before the
        /// transcription's start with no <c>endobj</c> inside it counts as open.
        /// </summary>
        private static bool InsideAnObject(string content, long contentStart, List<long> headerPositions, int position)
        {
            long absolute = contentStart + position;
            int lo = 0, hi = headerPositions.Count - 1, last = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (headerPositions[mid] < absolute) { last = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (last < 0) return false;

            int endobj = LastIndexBefore(content, "endobj", position);
            return endobj < 0 || contentStart + endobj < headerPositions[last];
        }

        /// <summary>
        /// The last occurrence of <paramref name="value"/> in <paramref name="content"/> that begins
        /// before index <paramref name="before"/>, or -1. Spelled out rather than left to
        /// <c>string.LastIndexOf(value, startIndex)</c>, whose treatment of a match straddling
        /// <c>startIndex</c> is easy to misread; walking the keywords backwards needs exactly
        /// "begins before".
        /// </summary>
        private static int LastIndexBefore(string content, string value, int before)
        {
            for (int i = Math.Min(before - 1, content.Length - value.Length); i >= 0; i--)
            {
                if (string.CompareOrdinal(content, i, value, 0, value.Length) == 0) return i;
            }

            return -1;
        }

        /// <summary>
        /// How much of a recovered object is searched for the marker that says it is worth parsing
        /// (<c>/ObjStm</c>, <c>/XRef</c>, <c>/Catalog</c>). An object's dictionary is written first,
        /// so this covers it; the alternative — parsing every object the scan found — would
        /// materialise every image in the file into the object cache to answer a question about
        /// three of them.
        /// </summary>
        private const int RecoveryDictionaryProbeBytes = 4096;

        /// <summary>
        /// Whether the recovered object <paramref name="entry"/> names looks like it carries
        /// <paramref name="marker"/> in its dictionary, judged from the scanned bytes in
        /// <paramref name="content"/> rather than by parsing it.
        /// </summary>
        private bool RecoveredObjectMentions(string content, CrossReferenceEntry entry, string marker)
        {
            if (entry == null || entry.Type != CrossReferenceEntry.EntryType.Normal) return false;
            // The transcription is the file's tail when the file is longer than one window; an object
            // before it cannot be probed and is not taken for one of the three.
            long index = entry.Offset - _recoveryContentStart;
            if (index < 0 || index >= content.Length) return false;

            int start = (int)index;
            int length = Math.Min(RecoveryDictionaryProbeBytes, content.Length - start);
            return content.IndexOf(marker, start, length, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Adds a type-2 entry for every object living inside a recovered <c>/Type /ObjStm</c>
        /// stream (§7.5.7), unless the scan found a copy of that object written later in the file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scan can only find objects written as <c>N G obj</c> in the file, and a PDF 1.5+
        /// producer puts nearly every non-stream object — the catalog and the whole page tree
        /// included — inside an object stream instead. Recovery therefore rebuilt a table that
        /// named the containers and none of their contents, so damaging only the <c>startxref</c>
        /// of a modern file made it unopenable while the byte-identical damage to a classic PDF 1.4
        /// file recovered cleanly.
        /// </para>
        /// <para>
        /// <b>Which copy.</b> A member's position is its container's, and the latest copy wins — against
        /// a member of another container and against a top-level copy alike, which is the rule the scan
        /// already applies to top-level objects. Added first-wins, in the order their containers first
        /// appeared and never over a top-level copy, an object an update had rewritten into a new
        /// object stream would come back as the revision it replaced. Within one
        /// container the first index naming a number keeps it.
        /// </para>
        /// <para>
        /// <b>Collected before installed</b>, so indexing a later container never reads a table an
        /// earlier one has already changed. The member count comes from the file, so each member is
        /// charged against the parser's cross-reference budget as it is collected; what was collected
        /// before a refusal is still installed.
        /// </para>
        /// </remarks>
        private void RecoverCompressedObjects(string content)
        {
            var containers = new List<CrossReferenceEntry>();
            foreach (var entry in _xrefTable.AllEntries)
            {
                if (RecoveredObjectMentions(content, entry, "/ObjStm"))
                    containers.Add(entry);
            }

            var newestMembers = new Dictionary<int, (CrossReferenceEntry entry, long position)>();
            foreach (var container in containers)
            {
                ObjectStreamReader reader;
                try
                {
                    if (!(GetObject(container.ObjectNumber) is PdfStream stream) ||
                        stream.GetName(PdfName.Type) != PdfName.ObjStm)
                        continue;
                    reader = GetObjectStreamReader(container.ObjectNumber);
                }
                catch (Exception ex)
                {
                    LastXrefSectionError =
                        $"recovered object stream {container.ObjectNumber.ToString(CultureInfo.InvariantCulture)} " +
                        $"could not be indexed: {ex.Message}";
                    continue;
                }

                if (!CollectNewestMembers(reader, container, newestMembers)) break;
            }

            foreach (var member in newestMembers)
            {
                // A top-level copy the scan found later in the file than this member's container is the
                // newer one. Anything else the table holds for the number — an older top-level copy, or
                // a leftover of a section that failed to parse — is not.
                CrossReferenceEntry existing = _xrefTable.GetEntry(member.Key);
                if (existing != null &&
                    existing.Type == CrossReferenceEntry.EntryType.Normal &&
                    existing.Offset >= member.Value.position)
                    continue;

                _xrefTable.SetEntry(member.Value.entry);

                // Opening a container resolves its indirect /Length, /Filter and /DecodeParms through
                // GetObject, which caches what it read: the older copy of an object this member
                // supersedes may already sit there under the same number.
                _objectCache.Remove(member.Key);
            }
        }

        /// <summary>
        /// Records each member of <paramref name="reader"/> in <paramref name="newestMembers"/> unless
        /// a candidate from this container or a later one already holds its number, charging each
        /// against the cross-reference budget. Containers may arrive in any order: a candidate is
        /// replaced only by one from a container written later.
        /// </summary>
        /// <returns><c>false</c> once the budget is spent, with the refusal recorded.</returns>
        private bool CollectNewestMembers(ObjectStreamReader reader, CrossReferenceEntry container,
            Dictionary<int, (CrossReferenceEntry entry, long position)> newestMembers)
        {
            for (int i = 0; i < reader.Entries.Length; i++)
            {
                if (_xrefEntriesParsed >= MaxCrossReferenceEntries)
                {
                    LastXrefSectionError = RecoveryStoppedAtBudget;
                    return false;
                }
                _xrefEntriesParsed++;

                int objectNumber = reader.Entries[i].objNum;
                if (newestMembers.TryGetValue(objectNumber, out var held) && held.position >= container.Offset)
                    continue;

                newestMembers[objectNumber] = (new CrossReferenceEntry
                {
                    ObjectNumber = objectNumber,
                    GenerationNumber = 0,
                    ObjectStreamNumber = container.ObjectNumber,
                    IndexInObjectStream = i,
                    Type = CrossReferenceEntry.EntryType.Compressed,
                }, container.Offset);
            }

            return true;
        }

        /// <summary>What the slot says when a recovery — the header scan or a container's members — stops at the entry budget.</summary>
        private string RecoveryStoppedAtBudget =>
            $"recovery stopped at the {MaxCrossReferenceEntries.ToString(CultureInfo.InvariantCulture)}-entry " +
            "cross-reference limit; the rebuilt table is incomplete.";

        /// <summary>
        /// Supplies the trailer's <c>/Root</c> when no recovered trailer dictionary did.
        /// </summary>
        /// <remarks>
        /// <para>A cross-reference-stream file has no <c>trailer</c> keyword at all — its trailer IS the
        /// xref stream's dictionary (§7.5.8.2) — and every recovered one has already been merged by
        /// <see cref="RecoverTrailer"/>. What is left is a file where none of them names a
        /// <c>/Root</c>, and there the catalog is identified by its own <c>/Type /Catalog</c>, which is
        /// what <c>Base.PdfRepair.FindRootObjectNumber</c> already does one layer up.</para>
        /// <para><b>Latest position wins, whichever kind of entry holds it.</b> A top-level candidate is
        /// where its bytes sit; an object-stream member is where its container sits — its own bytes are
        /// compressed, so it cannot be probed, and it is where a PDF 1.5+ file keeps its catalog. With
        /// every top-level candidate outranking every member, a "save as" that renumbered the catalog
        /// into an object stream would hand back the older top-level one. Within one container the member written later ranks higher, for the
        /// same reason.</para>
        /// </remarks>
        private void RecoverRootIfMissing(string content)
        {
            if (_trailer.ContainsKey(PdfName.Root)) return;

            var candidates = new List<(CrossReferenceEntry entry, long position, int order)>();
            foreach (var entry in _xrefTable.AllEntries)
            {
                if (entry.Type == CrossReferenceEntry.EntryType.Normal)
                {
                    if (RecoveredObjectMentions(content, entry, "/Catalog"))
                        candidates.Add((entry, entry.Offset, 0));
                }
                else if (entry.Type == CrossReferenceEntry.EntryType.Compressed)
                {
                    // Members are small by construction (§7.5.7 forbids a stream inside an object
                    // stream), so parsing one to look at its /Type is cheap.
                    CrossReferenceEntry container = _xrefTable.GetEntry(entry.ObjectStreamNumber);
                    if (container == null || container.Type != CrossReferenceEntry.EntryType.Normal) continue;
                    candidates.Add((entry, container.Offset, entry.IndexInObjectStream + 1));
                }
            }

            candidates.Sort((a, b) =>
            {
                int byPosition = b.position.CompareTo(a.position);
                return byPosition != 0 ? byPosition : b.order.CompareTo(a.order);
            });

            foreach (var (entry, _, _) in candidates)
            {
                var obj = GetObject(entry.ObjectNumber);
                if (obj is PdfDictionary dict && !(obj is PdfStream) &&
                    dict.GetName(PdfName.Type) == PdfName.Catalog)
                {
                    _trailer.Set(PdfName.Root, new PdfReference(entry.ObjectNumber, 0));
                    return;
                }
            }
        }

        /// <summary>
        /// Finds the catalog when the trailer's <c>/Root</c> does not name one — missing, or naming an
        /// object that is not a dictionary — by the scan recovery a broken cross-reference table
        /// takes (<see cref="RecoverXrefByScanning"/>): the table is rebuilt from the objects' own
        /// headers and the last <c>/Type /Catalog</c> object in the file becomes <c>/Root</c>, as
        /// other readers re-index such a file. Returns whether the trailer now names a dictionary.
        /// <see cref="LastRootRecovery"/> says what was done, or that no catalog exists.
        /// </summary>
        /// <remarks>
        /// A <c>/Root</c> that resolves to a dictionary is never second-guessed here — a dictionary
        /// with no <c>/Type</c> is a catalog many producers write — so an ordinary file is untouched
        /// by this method, which <c>PdfDocument</c> calls only when the trailer's <c>/Root</c> fails.
        /// </remarks>
        internal bool TryRecoverRoot()
        {
            PdfObject named = _trailer?[PdfName.Root];
            string was = named == null ? "the trailer names no /Root"
                : named is PdfReference reference ? $"the trailer's /Root names object {reference.ReferenceObjectNumber}, which is not a dictionary"
                : "the trailer's /Root is not a reference to a dictionary";

            RecoverXrefByScanning();

            // The scan merges the file's own trailer dictionaries, so the /Root that named nothing
            // is back; RecoverRootIfMissing looks only when there is none. Drop it and look.
            if (!(ResolveObject(_trailer[PdfName.Root]) is PdfDictionary) && _recoveryContent != null)
            {
                _trailer.Remove(PdfName.Root);
                RecoverRootIfMissing(_recoveryContent);
            }
            _recoveryContent = null;

            // RecoverRootIfMissing names only a non-stream /Type /Catalog dictionary, so a dictionary
            // here is a catalog; a second stream check was a guard nothing could reach.
            if (ResolveObject(_trailer[PdfName.Root]) is PdfDictionary)
            {
                int number = _trailer[PdfName.Root] is PdfReference r ? r.ReferenceObjectNumber : -1;
                LastRootRecovery = was + "; the file was re-indexed from its objects' headers and the " +
                                   $"last /Type /Catalog object, {number}, is the catalog.";
                return true;
            }

            LastRootRecovery = was + "; the file was re-indexed from its objects' headers and no " +
                               "/Type /Catalog object was found.";
            return false;
        }

        // The recovery scan reads bytes through ObjectHeaderScanner, which classifies with
        // PdfConstants directly.

        /// <summary>
        /// Reads the cross-reference section at stream position <paramref name="position"/> and
        /// returns its trailer.
        /// </summary>
        /// <param name="position">Where the section starts in the stream.</param>
        /// <param name="origin">
        /// The stream position the section's byte offsets count from — the header's (§7.5.2), or 0 for
        /// a section written from byte 0; see <see cref="ParseXrefSection"/>.
        /// </param>
        /// <param name="replaceableFreeEntries">
        /// For an <c>/XRefStm</c> target, the free entries its entries may displace (§7.5.8.4); see
        /// <see cref="XrefSectionRef.ReplaceableFreeEntries"/> and
        /// <see cref="CrossReferenceTable.AddEntry(CrossReferenceEntry, bool)"/>.
        /// </param>
        /// <param name="installedFreeEntries">
        /// For a classic section, the object numbers whose free entry its table installed; otherwise
        /// <c>null</c>.
        /// </param>
        private PdfDictionary ParseXrefSectionAt(long position, long origin, HashSet<int> replaceableFreeEntries,
            out HashSet<int> installedFreeEntries)
        {
            installedFreeEntries = null;

            // Validate before seeking. startxref, /Prev and /XRefStm are all file-controlled
            // longs that reach Stream.Position unchecked; a negative one (`/Prev -1`) threw
            // ArgumentOutOfRangeException — not a PdfException — straight out of
            // PdfDocument.Open. Raising PdfParseException instead routes an unusable offset into
            // the section-skip and scan-recovery the caller already implements.
            if (position < 0 || position >= _stream.Length)
                throw new PdfParseException(
                    $"Cross-reference offset {position.ToString(CultureInfo.InvariantCulture)} is " +
                    $"outside the file (length {_stream.Length.ToString(CultureInfo.InvariantCulture)}).",
                    position);

            _tokenizer.Seek(position);
            var token = _tokenizer.NextToken();

            _sectionBeingParsed = position;
            try
            {
                if (token.Type == PdfTokenType.Keyword && token.Value == "xref")
                {
                    installedFreeEntries = ParseTraditionalXrefTable(origin);
                    return ParseTrailerDictionary();
                }

                if (token.Type == PdfTokenType.Number)
                {
                    // Cross-reference stream (PDF 1.5+): objNum genNum obj << … >> stream …
                    _tokenizer.Seek(position);
                    var obj = ParseIndirectObject();
                    if (obj is PdfStream xrefStream)
                        return ParseXrefStream(xrefStream, origin, replaceableFreeEntries);
                    throw new PdfParseException("Expected xref stream object.", position);
                }
            }
            finally
            {
                _sectionBeingParsed = -1;
            }

            throw new PdfParseException(
                $"Expected 'xref' keyword or xref stream, got {token.Type}: '{token.Value}'", position);
        }

        /// <summary>The position of the section <see cref="ParseXrefSectionAt"/> is reading, stamped on each entry it installs; -1 outside one.</summary>
        private long _sectionBeingParsed = -1;

        /// <summary>
        /// Installs a classic section's entries and returns the object numbers whose <b>free</b> entry
        /// it installed (<c>null</c> when none) — what the section's own <c>/XRefStm</c> may look past.
        /// </summary>
        /// <param name="origin">The stream position this section's byte offsets count from.</param>
        private HashSet<int> ParseTraditionalXrefTable(long origin)
        {
            HashSet<int> installedFreeEntries = null;
            bool isFirstSubsection = true;
            while (true)
            {
                _tokenizer.SkipWhitespaceAndComments();
                var token = _tokenizer.PeekToken();
                if (token.Type == PdfTokenType.Keyword && token.Value == "trailer")
                    break;
                if (token.Type != PdfTokenType.Number)
                    break;

                // Read as the file wrote them, not saturated to int: a subsection start of
                // 4294967296 saturated to int.MaxValue, and the entries counted on from it wrapped to
                // negative object numbers the table then held (public corpus,
                // a fuzzed file; GAPS §19h item 5). A start or count outside what an
                // object number can be is a malformed section, and the scan recovery takes over as it
                // does for any other malformed entry.
                // Each bounded before the sum, which two values near long.MaxValue overflowed past the
                // check; and parsed here rather than through LongValue, which reads a token beyond long
                // as 0 — a subsection starting at object 0.
                string firstText = _tokenizer.NextToken().Value;
                string countText = _tokenizer.NextToken().Value;
                if (!long.TryParse(firstText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long firstObjNumRead)
                    || !long.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long countRead)
                    || firstObjNumRead < 0 || countRead < 0
                    || firstObjNumRead > int.MaxValue || countRead > int.MaxValue
                    || firstObjNumRead + countRead > int.MaxValue)
                    throw new PdfParseException(
                        $"Malformed traditional xref subsection header {firstText} {countText}: it names object " +
                        "numbers outside the range an object number can have.");
                int firstObjNum = (int)firstObjNumRead;
                int count = (int)countRead;
                bool subsectionStartsAtZero = false;

                for (int i = 0; i < count; i++)
                {
                    _tokenizer.SkipWhitespace();

                    // Read 20-byte xref entry: "OOOOOOOOOO GGGGG N \n"
                    // Use TryParse and raise PdfParseException (not the bare
                    // Format/Overflow exceptions that long.Parse/int.Parse throw)
                    // so a malformed entry triggers the scan-based xref recovery
                    // in ParseCrossReferences instead of crashing the open.
                    if (!long.TryParse(ReadFixedToken(10), NumberStyles.Integer, CultureInfo.InvariantCulture, out long entryOffset))
                        throw new PdfParseException("Malformed traditional xref entry offset.");
                    _tokenizer.SkipWhitespace();
                    if (!int.TryParse(ReadFixedToken(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gen))
                        throw new PdfParseException("Malformed traditional xref entry generation.");
                    _tokenizer.SkipWhitespace();
                    string flag = ReadFixedToken(1);

                    // Some PDFs label the first subsection starting at object 1 with a free entry
                    // that is really the object-0 free-list head: the header is off by one, so every
                    // entry of the subsection belongs one number lower, as lenient readers take it.
                    // Moving the head alone left object 1 with no entry and gave each later object
                    // its predecessor's offset, which the header recovery then hid behind a report.
                    if (isFirstSubsection && i == 0 && flag == "f" && firstObjNum == 1)
                        subsectionStartsAtZero = true;
                    int objectNumber = subsectionStartsAtZero ? firstObjNum + i - 1 : firstObjNum + i;

                    bool free = flag == "f";

                    // NumberStyles.Integer accepts a leading sign, so "-000000001 00000 n " parses
                    // to Offset = -1 — which reaches Stream.Position and throws
                    // ArgumentOutOfRangeException (not a PdfException) out of GetObject, breaking
                    // its never-throw contract. A free entry's field is a next-free object number
                    // rather than an offset, so only the in-use ones are checked.
                    if (!free && entryOffset < 0)
                        throw new PdfParseException(
                            $"Negative offset {entryOffset.ToString(CultureInfo.InvariantCulture)} in traditional xref entry for object {objectNumber.ToString(CultureInfo.InvariantCulture)}.");

                    ChargeXrefEntry();

                    var entry = new CrossReferenceEntry
                    {
                        ObjectNumber = objectNumber,
                        // An in-use entry's field is "the number of bytes from the beginning of the PDF
                        // file to the beginning of the object" (§7.5.4), counted from the header
                        // (§7.5.2), and is stored as the stream position it names. A free entry's is
                        // "the 10-digit object number of the next free object", which no origin moves.
                        Offset = free ? entryOffset : entryOffset + origin,
                        GenerationNumber = gen,
                        Type = free
                            ? CrossReferenceEntry.EntryType.Free
                            : CrossReferenceEntry.EntryType.Normal,
                        SectionOffset = _sectionBeingParsed,
                    };

                    // Recorded only when THIS section installed the free entry. One a newer section
                    // installed first is that revision's deletion, which the /XRefStm must not undo.
                    if (_xrefTable.AddEntry(entry, allowReplacingFree: false) && free)
                        (installedFreeEntries ??= new HashSet<int>()).Add(objectNumber);
                }
                isFirstSubsection = false;
            }

            return installedFreeEntries;
        }

        private string ReadFixedToken(int length)
        {
            var buffer = new byte[length];
            int totalRead = StreamBytes.Fill(_stream, buffer, 0, length);
            return Encoding.ASCII.GetString(buffer, 0, totalRead);
        }

        private PdfDictionary ParseTrailerDictionary()
        {
            var token = _tokenizer.NextToken();
            if (token.Type != PdfTokenType.Keyword || token.Value != "trailer")
                throw new PdfParseException($"Expected 'trailer' keyword, got {token.Type}: '{token.Value}'", token.Position);

            _tokenizer.SkipWhitespaceAndComments();
            var obj = ParseDirectObject();
            if (obj is PdfDictionary dict)
                return dict;
            throw new PdfParseException("Expected dictionary after 'trailer' keyword.");
        }

        /// <summary>Installs a cross-reference stream's entries and returns its dictionary, which is the section's trailer.</summary>
        /// <param name="stream">The parsed <c>/Type /XRef</c> stream.</param>
        /// <param name="origin">The stream position this section's type-1 offsets count from.</param>
        /// <param name="replaceableFreeEntries">
        /// The installed free entries this section's entries may displace — non-null only for an
        /// <c>/XRefStm</c> target; see <see cref="XrefSectionRef.ReplaceableFreeEntries"/>.
        /// </param>
        private PdfDictionary ParseXrefStream(PdfStream stream, long origin, HashSet<int> replaceableFreeEntries)
        {
            string codec = SyntaxStreamFilters.ImageCodecOf(stream);
            if (codec != null)
                throw new PdfParseException(
                    $"the cross-reference stream's /Filter names /{codec}, an image codec, whose output is " +
                    "samples and never cross-reference entries; it was not decoded.");

            byte[] data = stream.GetDecodedDataUnder(SyntaxStreamWork);

            var wArray = stream.GetArray(PdfName.W);
            if (wArray == null || wArray.Count != 3)
                throw new PdfParseException("Invalid /W array in xref stream.");

            // /W elements must be numbers.  GetInteger throws InvalidOperationException
            // for a non-number element (e.g. /W [/Foo 1 0]); rewrap as PdfParseException
            // so ParseCrossReferences' catch engages the scan-based recovery path instead
            // of hard-failing the whole document open.
            int w0 = RequireXrefInt(wArray, 0, "W");
            int w1 = RequireXrefInt(wArray, 1, "W");
            int w2 = RequireXrefInt(wArray, 2, "W");
            // PDF §7.5.8.3: each /W field is a non-negative integer, max 8 bytes
            // (long-sized).  Adversarial PDFs with -1 / 1_000_000 / etc. can otherwise
            // produce infinite loops (entrySize underflow) or silent xref truncation.
            if (w0 < 0 || w0 > 8 || w1 < 0 || w1 > 8 || w2 < 0 || w2 > 8)
                throw new PdfParseException(
                    $"Invalid xref-stream /W field widths: [{w0} {w1} {w2}]; each must be in [0,8].");
            int entrySize = w0 + w1 + w2;
            if (entrySize <= 0)
                throw new PdfParseException(
                    $"Invalid xref-stream /W: total entry size must be > 0 (got {entrySize}).");

            // Determine subsections from /Index array or default
            var indexArray = stream.GetArray(PdfName.Index);
            var subsections = new List<(int start, int count)>();

            if (indexArray != null)
            {
                // Malformed PDFs can have an odd-length /Index array; in that
                // case the trailing number is meaningless.  Guard the pair-wise
                // read so we don't walk past the end.
                for (int i = 0; i + 1 < indexArray.Count; i += 2)
                {
                    // Same defence as /W: a non-number /Index element (e.g. an indirect
                    // ref "[0 2 0 R]") must trigger recovery, not an uncaught throw.
                    int start = RequireXrefInt(indexArray, i, "Index");
                    int count = RequireXrefInt(indexArray, i + 1, "Index");
                    // Reject negatives and overflow on (start + count) per PDF §7.5.8.4.
                    if (start < 0 || count < 0 || (long)start + count > int.MaxValue)
                        throw new PdfParseException(
                            $"Invalid xref-stream /Index entry: start={start} count={count}.");
                    subsections.Add((start, count));
                }
            }
            else
            {
                int size = stream.GetInteger(PdfName.Size) ?? 0;
                if (size < 0)
                    throw new PdfParseException($"Invalid xref-stream /Size: {size}.");
                subsections.Add((0, size));
            }

            int dataPos = 0;
            foreach (var (start, count) in subsections)
            {
                for (int i = 0; i < count; i++)
                {
                    if (dataPos + entrySize > data.Length)
                        break;

                    // Charged against the WHOLE document's budget, not this section's: see
                    // _xrefEntriesParsed.
                    ChargeXrefEntry();

                    long field0 = ReadFieldValue(data, dataPos, w0, 1); // default type 1
                    long field1 = ReadFieldValue(data, dataPos + w0, w1, 0);
                    long field2 = ReadFieldValue(data, dataPos + w0 + w1, w2, 0);
                    dataPos += entrySize;

                    int objNum = start + i;
                    var entry = new CrossReferenceEntry { ObjectNumber = objNum, SectionOffset = _sectionBeingParsed };

                    switch ((int)field0)
                    {
                        case 0:
                            entry.Type = CrossReferenceEntry.EntryType.Free;
                            entry.Offset = field1;
                            entry.GenerationNumber = (int)field2;
                            break;
                        case 1:
                            // /W [1 8 1] with the high bit set in the offset field yields a
                            // negative long, which would reach Stream.Position and throw
                            // ArgumentOutOfRangeException out of GetObject. Such an entry names no
                            // byte of the file, so drop it and keep the rest of the section.
                            if (field1 < 0)
                            {
                                LastXrefSectionError =
                                    $"obj {objNum.ToString(CultureInfo.InvariantCulture)}: negative xref-stream offset {field1.ToString(CultureInfo.InvariantCulture)}; the entry was dropped.";
                                continue;
                            }
                            entry.Type = CrossReferenceEntry.EntryType.Normal;
                            // "The byte offset of the object, starting from the beginning of the PDF
                            // file" (§7.5.8.3, Table 18) — counted from the header (§7.5.2) and stored
                            // as the stream position it names. An offset within `origin` of
                            // long.MaxValue wraps negative, which GetObject's range check refuses.
                            entry.Offset = field1 + origin;
                            entry.GenerationNumber = (int)field2;
                            break;
                        case 2:
                            entry.Type = CrossReferenceEntry.EntryType.Compressed;
                            entry.ObjectStreamNumber = (int)field1;
                            entry.IndexInObjectStream = (int)field2;
                            break;
                    }

                    _xrefTable.AddEntry(entry,
                        allowReplacingFree: replaceableFreeEntries != null && replaceableFreeEntries.Contains(objNum));
                }
            }

            // The xref stream dictionary IS the trailer
            return stream;
        }

        /// <summary>
        /// Reads element <paramref name="index"/> of an xref-stream <c>/W</c> or
        /// <c>/Index</c> array as an integer, raising a <see cref="PdfParseException"/>
        /// (not the <see cref="InvalidOperationException"/> that <see cref="PdfArray.GetInteger"/>
        /// throws) when the element is not a number.  Only <see cref="PdfParseException"/> is
        /// caught by <see cref="ParseCrossReferences"/>, so this keeps a malformed /W or /Index
        /// from hard-failing the document open instead of falling into scan-recovery.
        /// </summary>
        private static int RequireXrefInt(PdfArray array, int index, string arrayName)
        {
            if (index < 0 || index >= array.Count || !(array[index] is PdfNumber num))
                throw new PdfParseException(
                    $"Invalid xref-stream /{arrayName} array: element {index} is not a number.");
            return num.IntValue;
        }

        /// <summary>
        /// Charges one cross-reference entry against this parser's whole-document budget, throwing
        /// a <see cref="PdfParseException"/> once it is spent.
        /// </summary>
        /// <remarks>
        /// The budget is per <b>parser</b>, not per section. With a fresh one for every section of a
        /// <c>/Prev</c> chain, <c>/Index [start count]</c> would let each claim a disjoint
        /// object-number range so the first-wins table cannot deduplicate them — 4 290 crafted bytes
        /// buying 1 200 000 entries and 97 MB of heap.
        /// </remarks>
        private void ChargeXrefEntry()
        {
            if (_xrefEntriesParsed >= MaxCrossReferenceEntries)
                throw new PdfParseException(
                    $"Cross-reference entries exceed the {MaxCrossReferenceEntries.ToString(CultureInfo.InvariantCulture)}-entry limit for one document; refusing to continue.");
            _xrefEntriesParsed++;
        }

        private static long ReadFieldValue(byte[] data, int offset, int width, long defaultValue)
        {
            if (width == 0) return defaultValue;
            long value = 0;
            for (int i = 0; i < width; i++)
            {
                value = (value << 8) | data[offset + i];
            }
            return value;
        }

        public PdfObject ParseIndirectObject() => ParseIndirectObject(out _, out _);

        /// <summary>
        /// <see cref="ParseIndirectObject()"/>, also handing back the object number and generation
        /// the <c>N G obj</c> header named — the identity of what was parsed, whether or not the
        /// object carries it: the shared <c>true</c>, <c>false</c> and <c>null</c> instances are never
        /// stamped, so a caller checking that an offset holds the object it asked for reads these,
        /// not the object.
        /// </summary>
        internal PdfObject ParseIndirectObject(out int objectNumber, out int generation)
        {
            var objNumToken = _tokenizer.NextToken();
            if (objNumToken.Type != PdfTokenType.Number)
                throw new PdfParseException($"Expected object number, got {objNumToken.Type}", objNumToken.Position);

            var genNumToken = _tokenizer.NextToken();
            if (genNumToken.Type != PdfTokenType.Number)
                throw new PdfParseException($"Expected generation number, got {genNumToken.Type}", genNumToken.Position);

            var objToken = _tokenizer.NextToken();
            if (objToken.Type != PdfTokenType.Keyword || objToken.Value != "obj")
                throw new PdfParseException($"Expected 'obj' keyword, got {objToken.Type}: '{objToken.Value}'", objToken.Position);

            int objNum = objNumToken.IntValue;
            int genNum = genNumToken.IntValue;
            objectNumber = objNum;
            generation = genNum;

            _tokenizer.SkipWhitespaceAndComments();
            var obj = ParseDirectObject();

            // Check if this is a stream
            _tokenizer.SkipWhitespaceAndComments();
            var nextToken = _tokenizer.PeekToken();

            if (nextToken.Type == PdfTokenType.Keyword && nextToken.Value == "stream")
            {
                _tokenizer.NextToken(); // consume "stream"

                if (!(obj is PdfDictionary dict))
                    throw new PdfParseException("Stream must be preceded by a dictionary.", nextToken.Position);

                // Save position right after "stream" keyword BEFORE resolving length,
                // because ResolveLength may seek away if /Length is an indirect reference.
                long posAfterStreamKeyword = _tokenizer.Position;
                int declaredLength = ResolveLength(dict);
                _tokenizer.Seek(posAfterStreamKeyword);

                // Skip the EOL that must follow "stream", record where the raw bytes begin.
                long streamDataStart = _tokenizer.SkipStreamEol();
                int actualLength = declaredLength;

                // Why this is not assigned to LastObjectParseError here: the /Filter and
                // /DecodeParms resolution below calls GetObject, which clears the slot on entry —
                // so a message written now would be wiped by any stream whose /Filter is indirect.
                // It is recorded once, immediately before this object is returned.
                string lengthProblem = null;

                // If /Length resolution fell back (missing or unresolvable indirect ref),
                // skip the declared-length validation entirely and go straight to the
                // endstream scan with the tokenizer rewound to the start of stream data.
                if (LastStreamLengthWasFallback)
                {
                    int scanned = FindEndStreamFrom(streamDataStart);
                    if (scanned >= 0)
                        actualLength = scanned;
                    else
                        // Both the declared length and the scan have failed, so `actualLength` is
                        // still the fallback 0 and the stream materialises as Array.Empty<byte>().
                        // That is the one answer guaranteed to be wrong, and unreported it would be
                        // indistinguishable from a stream that was always empty: a 9 MB image whose
                        // /Length names a missing object rendering as nothing while
                        // GetDecodedData() returned zero bytes and every diagnostic said the parse
                        // succeeded.
                        lengthProblem =
                            $"obj {objNum}: /Length is missing or unresolvable and no 'endstream' " +
                            $"was found within the {FindEndStreamWindowBytes.ToString(CultureInfo.InvariantCulture)}-byte " +
                            "scan window; the stream was read as empty.";
                }
                // Validate declared /Length: seek ahead and look for "endstream".
                // If the declared length is wrong (common in real-world PDFs), scan for the
                // real boundary — the usual stream-length fallback. A declared
                // length that runs past EOF is always wrong (and, if trusted, would force a
                // multi-GB allocation from a tiny crafted file — a DoS), so it also scans.
                else
                {
                    bool declaredOk = false;
                    if (streamDataStart + (long)declaredLength <= _stream.Length)
                    {
                        _stream.Position = streamDataStart + declaredLength;
                        _tokenizer.SkipWhitespace();
                        var check = _tokenizer.PeekToken();
                        declaredOk = check.Type == PdfTokenType.Keyword && check.Value == "endstream";
                    }
                    if (!declaredOk)
                    {
                        int scanned = FindEndStreamFrom(streamDataStart);
                        if (scanned >= 0)
                            actualLength = scanned;
                        else
                            // The symmetric silent case: the declared length is known to be wrong
                            // (no 'endstream' sits where it says) and the scan could not find the
                            // real boundary either, so the known-wrong number is used and the
                            // stream is truncated or over-read to whatever it says.
                            lengthProblem =
                                $"obj {objNum}: no 'endstream' at the declared /Length " +
                                $"{declaredLength.ToString(CultureInfo.InvariantCulture)} and none found " +
                                $"within the {FindEndStreamWindowBytes.ToString(CultureInfo.InvariantCulture)}-byte " +
                                "scan window; the declared length was used unverified.";
                    }
                }

                // Hard clamp: never allocate/read past the end of the file regardless of the
                // declared or scanned length (defends against attacker-controlled /Length).
                _stream.Position = streamDataStart;
                long remaining = Math.Max(0, _stream.Length - streamDataStart);
                if (actualLength < 0 || actualLength > remaining)
                    actualLength = (int)Math.Min(remaining, int.MaxValue);
                byte[] streamData = _tokenizer.ReadStreamBytesRaw(actualLength);

                var pdfStream = new PdfStream();
                // Every stream the parser builds shares one per-document memo budget, so the decoded
                // bytes the document keeps cached across all its streams are bounded (F3).
                pdfStream.MemoBudget = _memoBudget;
                foreach (var kvp in dict)
                    pdfStream.Set(kvp.Key, kvp.Value);
                pdfStream.RawData = streamData;
                pdfStream.ObjectNumber = objNum;
                pdfStream.GenerationNumber = genNum;

                // Resolve /Filter and /DecodeParms if they are indirect references so that
                // PdfStream.GetDecodedData() never receives a PdfReference it cannot resolve. One
                // that resolves to nothing — a missing object, a cycle, or a member of an object
                // stream this parser cannot decrypt — stays a reference, as the array elements below
                // do: FilterPipeline reads a reference as absent, as it reads null, while a null
                // written in its place loses the reference for every writer of the graph (the
                // decryptor wrote `/DecodeParms null` for an image whose parameters sat in an
                // encrypted object stream, and the image lost its /JBIG2Globals).
                if (pdfStream[PdfName.Filter] is PdfReference filterRef)
                {
                    var resolvedFilter = GetObject(filterRef.ReferenceObjectNumber);
                    if (resolvedFilter != null && !(resolvedFilter is PdfNull)) pdfStream.Set(PdfName.Filter, resolvedFilter);
                }

                // The names inside a /Filter array too: §7.3.10 covers array elements, and a filter
                // cannot resolve one. /Filter [/ASCII85Decode 9 0 R] reached FilterPipeline with a
                // reference where a name belongs, and that stage was dropped — reported, but the
                // stream came back undecoded. Written back into the array, as the /DecodeParms
                // element fix below does; a missing object or a cycle stays a reference.
                if (pdfStream[PdfName.Filter] is PdfArray filterArray)
                {
                    for (int i = 0; i < filterArray.Count; i++)
                    {
                        if (!(filterArray[i] is PdfReference elementRef)) continue;
                        var resolvedFilter = GetObject(elementRef.ReferenceObjectNumber);
                        if (resolvedFilter != null && !(resolvedFilter is PdfNull)) filterArray[i] = resolvedFilter;
                    }
                }

                if (pdfStream[PdfName.DecodeParms] is PdfReference dpRef)
                {
                    var resolvedParms = GetObject(dpRef.ReferenceObjectNumber);
                    if (resolvedParms != null && !(resolvedParms is PdfNull)) pdfStream.Set(PdfName.DecodeParms, resolvedParms);
                }

                // …and the entries INSIDE /DecodeParms, for the same reason one level down. A filter
                // runs in Core with no document to resolve against, and PdfDictionary's indexer does
                // not follow a reference — so a decode parameter written as a reference, whether a
                // /JBIG2Globals stream or a /Columns number, can only ever reach a filter pre-resolved.
                ResolveDecodeParmsStreamValues(pdfStream[PdfName.DecodeParms]);

                // Skip "endstream"
                _tokenizer.SkipWhitespaceAndComments();
                _tokenizer.NextToken(); // lenient: consume whatever is there
                // Some PDFs omit endstream or have extra whitespace — just advance past it.

                // Skip "endobj"
                _tokenizer.SkipWhitespaceAndComments();
                var endObjToken = _tokenizer.PeekToken();
                if (endObjToken.Type == PdfTokenType.Keyword && endObjToken.Value == "endobj")
                    _tokenizer.NextToken();

                // Last, so nothing this method calls afterwards can clear it (see `lengthProblem`).
                if (lengthProblem != null)
                    LastObjectParseError = lengthProblem;

                return pdfStream;
            }

            StampIdentity(obj, objNum, genNum);

            // Skip "endobj"
            _tokenizer.SkipWhitespaceAndComments();
            var endObj = _tokenizer.PeekToken();
            if (endObj.Type == PdfTokenType.Keyword && endObj.Value == "endobj")
                _tokenizer.NextToken();

            return obj;
        }

        /// <summary>
        /// Gives a parsed object the identity of the indirect object it came from — unless it is one
        /// of the three shared instances the parser hands out for <c>true</c>, <c>false</c> and
        /// <c>null</c>, which belong to every document in the process and carry no identity.
        /// </summary>
        /// <remarks>
        /// A stamp on one of those made <c>PdfBoolean.True.ObjectNumber</c> read as whichever object
        /// last held a bare <c>true</c>, in any document, for the rest of the process — and a reader
        /// keying a value by the object it sits in, as the encryptor does, read noise. Both the
        /// top-level site and the object-stream member site go through here.
        /// </remarks>
        private static void StampIdentity(PdfObject obj, int objectNumber, int generation)
        {
            if (IsSharedInstance(obj)) return;
            obj.ObjectNumber = objectNumber;
            obj.GenerationNumber = generation;
        }

        /// <summary>Whether <paramref name="obj"/> is one of the shared <c>true</c>, <c>false</c> or <c>null</c> instances.</summary>
        internal static bool IsSharedInstance(PdfObject obj)
            => ReferenceEquals(obj, PdfBoolean.True) || ReferenceEquals(obj, PdfBoolean.False)
               || ReferenceEquals(obj, PdfNull.Instance);

        /// <summary>
        /// Resolves the indirect entries inside a resolved <c>/DecodeParms</c> — the stream-valued
        /// <c>/JBIG2Globals</c> (ISO 32000-1 Table 12) and every scalar a filter reads.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Without this, <c>/JBIG2Globals</c> could never be seen at all.</b> A PDF stream may
        /// only exist as an indirect object, so a conforming file always writes
        /// <c>/DecodeParms &lt;&lt; /JBIG2Globals 8 0 R &gt;&gt;</c>. <c>PdfDictionary</c>'s indexer
        /// does not follow a reference, and a filter runs in Core with no document to resolve
        /// against, so <c>Jbig2DecodeFilter</c>'s <c>parameters[JBIG2Globals] is PdfStream</c> test
        /// matched on no real file ever.
        /// </para>
        /// <para>
        /// The consequence was silent and total: with no globals the symbol dictionary is missing,
        /// so every text-region instance is skipped, the all-zero page bitmap is returned, the
        /// filter inverts it to white — and <c>LastFallbackReason</c> stays null. A scanned document
        /// in symbol mode (common encoders) rendered as **blank white pages while every
        /// honest-failure channel reported success**.
        /// </para>
        /// <para>
        /// Handled here rather than in the filter because this is the one place that has both the
        /// parsed dictionary and a way to resolve. An array-valued <c>/DecodeParms</c> is walked
        /// too; an element that is itself an indirect reference is resolved through
        /// <see cref="GetObject(int)"/> — <b>and written back into the array</b>, whose own cache is
        /// what keeps a hostile self-reference from recursing here.
        /// </para>
        /// <para>
        /// <b>The write-back is what matters.</b> With the array still holding the reference,
        /// <c>FilterPipeline</c>, which reads each element as <c>element as PdfDictionary</c>, would
        /// see <see langword="null"/> for that stage and the repair be thrown away.
        /// </para>
        /// </remarks>
        private void ResolveDecodeParmsStreamValues(PdfObject decodeParms)
        {
            if (decodeParms is PdfDictionary dp)
            {
                // Every entry, not only /JBIG2Globals. The scalars are read by filters that have no
                // document either — /Predictor, /Columns, /Colors, /BitsPerComponent, /EarlyChange,
                // CCITT's /K, /Columns, /Rows, /BlackIs1 — so /Columns 6 0 R read as absent and the
                // predictor ran with its default of one column: a four-column row decoded to
                // garbage from the first byte, with nothing reported. Keys are gathered first
                // because Set replaces values in the dictionary being walked.
                List<PdfName> indirectKeys = null;
                foreach (var kvp in dp)
                    if (kvp.Value is PdfReference) (indirectKeys ??= new List<PdfName>()).Add(kvp.Key);
                if (indirectKeys == null) return;

                foreach (var key in indirectKeys)
                {
                    // A self-reference, a cycle or a missing object comes back as PdfNull, and the
                    // reference stays: the filter then reads the entry as absent, as it always did.
                    var resolved = GetObject(((PdfReference)dp[key]).ReferenceObjectNumber);
                    if (resolved != null && !(resolved is PdfNull)) dp.Set(key, resolved);
                }
                return;
            }

            if (decodeParms is PdfArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    PdfObject element = arr[i];
                    if (element is PdfReference eRef)
                    {
                        element = GetObject(eRef.ReferenceObjectNumber);

                        // Written BACK into the array, not merely into this local. The recursion
                        // below does fix up the referenced dictionary — but the array still held
                        // the reference, and FilterPipeline reads each element as
                        // `element as PdfDictionary`, which is null for one. So the parms were
                        // repaired and then thrown away: for /DecodeParms [12 0 R] the JBIG2Globals
                        // resolution this whole method exists for never reached the filter, and
                        // the outcome was the blank-page-with-no-diagnostic the remarks describe.
                        // The single-valued branch at the call site has always written back, and
                        // says why in as many words.
                        if (element != null) arr[i] = element;
                    }
                    if (element is PdfDictionary) ResolveDecodeParmsStreamValues(element);
                }
            }
        }

        /// <summary>
        /// Whether the most recent <see cref="ResolveLength"/> fell back to 0
        /// because the stream's <c>/Length</c> was missing or unresolvable.
        /// Callers that detect this can rely on the <c>endstream</c> scan but
        /// should know the declared-length validation step is meaningless.
        /// </summary>
        public bool LastStreamLengthWasFallback { get; private set; }

        private int ResolveLength(PdfDictionary dict)
        {
            LastStreamLengthWasFallback = false;
            var lengthObj = dict[PdfName.Length];
            if (lengthObj is PdfNumber num)
            {
                int v = num.IntValue;
                // Negative declared length is malformed — fall back to scan.
                if (v < 0) { LastStreamLengthWasFallback = true; return 0; }
                return v;
            }
            if (lengthObj is PdfReference reference)
            {
                var resolved = GetObject(reference.ReferenceObjectNumber);
                if (resolved is PdfNumber resolvedNum)
                {
                    int v = resolvedNum.IntValue;
                    if (v < 0) { LastStreamLengthWasFallback = true; return 0; }
                    return v;
                }
            }
            // /Length missing or unresolvable — return 0 so the endstream scan takes over.
            LastStreamLengthWasFallback = true;
            return 0;
        }

        public PdfObject ParseDirectObject()
        {
            var token = _tokenizer.NextToken();

            switch (token.Type)
            {
                case PdfTokenType.Boolean:
                    return token.Value == "true" ? PdfBoolean.True : PdfBoolean.False;

                case PdfTokenType.Number:
                    return ParseNumberOrReference(token);

                case PdfTokenType.LiteralString:
                    return new PdfString(Text.Latin1Text.Encode(token.Value));

                case PdfTokenType.HexString:
                    return new PdfString(HexToBytes(token.Value), true);

                case PdfTokenType.Name:
                    return new PdfName(token.Value, token.NameIsNotUtf8);

                case PdfTokenType.Null:
                    return PdfNull.Instance;

                case PdfTokenType.ArrayStart:
                    return ParseArray();

                case PdfTokenType.DictionaryStart:
                    return ParseDictionary();

                case PdfTokenType.Keyword:
                    // Could be an indirect reference continuation or unexpected keyword
                    if (token.Value == "null") return PdfNull.Instance;
                    if (token.Value == "true") return PdfBoolean.True;
                    if (token.Value == "false") return PdfBoolean.False;
                    // Return a PdfName for unrecognized keywords (lenient)
                    return new PdfName(token.Value);

                default:
                    throw new PdfParseException($"Unexpected token type {token.Type}: '{token.Value}'", token.Position);
            }
        }

        private PdfObject ParseNumberOrReference(PdfToken firstNumber)
        {
            // Could be:
            // - Just a number: 42
            // - An indirect reference: 42 0 R
            long savedPos = _stream.Position;
            var second = _tokenizer.PeekToken();

            if (second.Type == PdfTokenType.Number)
            {
                _tokenizer.NextToken(); // consume second number
                var third = _tokenizer.PeekToken();

                if (third.Type == PdfTokenType.Keyword && third.Value == "R")
                {
                    _tokenizer.NextToken(); // consume "R"
                    return new PdfReference(firstNumber.IntValue, second.IntValue);
                }

                // Not a reference, put back the second number
                _stream.Position = savedPos;
            }

            // Just a number
            if (firstNumber.Value.Contains("."))
                return new PdfNumber(firstNumber.DoubleValue);
            return new PdfNumber(firstNumber.LongValue);
        }

        // Nesting depth of array/dictionary parsing. ParseArray/ParseDictionary
        // recurse via ParseDirectObject; deeply nested direct objects
        // ([[[[...]]]] or <<<<...>>>>) in untrusted input would otherwise
        // stack-overflow (uncatchable in .NET) before any object is built.
        private int _parseDepth;
        private static int MaxParseDepth => PdfLimits.MaxObjectGraphDepth;

        private PdfArray ParseArray()
        {
            if (++_parseDepth > MaxParseDepth)
            {
                _parseDepth--;
                throw new PdfParseException($"PDF object nesting exceeds {MaxParseDepth} levels.");
            }
            try
            {
                var array = new PdfArray();
                while (true)
                {
                    _tokenizer.SkipWhitespaceAndComments();
                    var token = _tokenizer.PeekToken();
                    if (token.Type == PdfTokenType.ArrayEnd)
                    {
                        _tokenizer.NextToken();
                        break;
                    }
                    if (token.Type == PdfTokenType.Eof)
                        throw new PdfParseException("Unterminated array.", token.Position);

                    array.Add(ParseDirectObject());
                }
                return array;
            }
            finally { _parseDepth--; }
        }

        private PdfDictionary ParseDictionary()
        {
            if (++_parseDepth > MaxParseDepth)
            {
                _parseDepth--;
                throw new PdfParseException($"PDF object nesting exceeds {MaxParseDepth} levels.");
            }
            try
            {
            var dict = new PdfDictionary();
            while (true)
            {
                _tokenizer.SkipWhitespaceAndComments();
                var token = _tokenizer.PeekToken();
                if (token.Type == PdfTokenType.DictionaryEnd)
                {
                    _tokenizer.NextToken();
                    break;
                }
                if (token.Type == PdfTokenType.Eof)
                    throw new PdfParseException("Unterminated dictionary.", token.Position);

                // Key must be a name.  If it isn't, skip that token and try again —
                // lenient recovery, as other readers recover a dictionary:
                //   info("Malformed dictionary: key must be a name object");
                //   this.shift(); continue;
                var keyToken = _tokenizer.NextToken();
                if (keyToken.Type != PdfTokenType.Name)
                {
                    // If we hit ">>" or EOF while expecting a key, just close the dict.
                    if (keyToken.Type == PdfTokenType.DictionaryEnd || keyToken.Type == PdfTokenType.Eof)
                        break;
                    // Otherwise skip the bad token and try the next one as a key.
                    continue;
                }

                string key = keyToken.Value;

                // Value is any object
                _tokenizer.SkipWhitespaceAndComments();
                var value = ParseDirectObject();
                dict.Set(key, keyToken.NameIsNotUtf8, value);
            }
            return dict;
            }
            finally { _parseDepth--; }
        }

        // Object numbers currently being resolved, used to break object-stream
        // resolution cycles. A Compressed entry resolves its owning object
        // stream via GetObject, which re-enters before the object is cached;
        // an adversarial xref where ObjStm A is compressed-in-B and B is
        // compressed-in-A would otherwise stack-overflow (uncatchable in .NET).
        private readonly HashSet<int> _resolving = new HashSet<int>();

        public PdfObject GetObject(int objectNumber)
        {
            // Cleared at the START of every call, on every path - including the cache hit below.
            // LastObjectParseError documents itself as "null when the last GetObject call on this
            // thread succeeded", and that is what makes it usable: a caller handed PdfNull reads it
            // to tell "genuinely absent" from "failed to parse, here is why". Without this reset a
            // single earlier failure was reported against every later success, so the caller blamed
            // the wrong object. CLAUDE.md states the rule after LastPermsValidation was found to be
            // [ThreadStatic] and still wrong for exactly this reason.
            LastObjectParseError = null;
            LastObjectHeaderRecovery = null;

            if (_objectCache.TryGetValue(objectNumber, out var cached))
                return cached;

            // A failure that cannot change with the path — an entry misplaced with no header to read
            // the object from, bytes that do not parse — is answered from memory, with its reason. It
            // was re-parsed on every reference, so a stream whose /Filter named a failing object twice,
            // thirty levels deep, cost 2^30 parses.
            if (_failedObjects != null && _failedObjects.TryGetValue(objectNumber, out string remembered))
            {
                LastObjectParseError = remembered;
                return PdfNull.Instance;
            }

            var entry = _xrefTable?.GetEntry(objectNumber);
            if (entry == null)
                return PdfNull.Instance;

            if (!_resolving.Add(objectNumber))
            {
                LastObjectParseError = $"obj {objectNumber}: object-stream resolution cycle.";
                return PdfNull.Instance;
            }

            // Parsing one object resolves others — a stream's indirect /Length, an indirect /Filter —
            // and each of those parses here again. A chain of distinct objects passes the cycle set,
            // so a file whose every object named the next one as its /Length ran the stack out, four
            // frames an object. The nesting is bounded like the object
            // graph's depth; CLAUDE.md: a cycle guard is not a depth guard.
            if (_getObjectDepth >= PdfLimits.MaxObjectGraphDepth)
            {
                _resolving.Remove(objectNumber);
                LastObjectParseError =
                    $"obj {objectNumber}: resolving it nests more than {PdfLimits.MaxObjectGraphDepth.ToString(CultureInfo.InvariantCulture)} " +
                    "objects deep (each one's parse needing the next), so no object was returned.";
                return PdfNull.Instance;
            }
            _getObjectDepth++;

            try
            {
                PdfObject obj;
                switch (entry.Type)
                {
                    case CrossReferenceEntry.EntryType.Normal:
                        if (!TryParseAtOffset(objectNumber, entry.Offset, out obj, out string failure))
                        {
                            // The entry is wrong about where the object is — outside the file, at
                            // another object's body, at bytes that do not parse. The file may still
                            // hold the object under its own header, the last `N G obj` for it in the
                            // entry's own revision, where a stale or shifted table would have pointed
                            // (GAPS §19h item 5: a corpus file, its offsets shifted by one object,
                            // read as one page of two and saved as a damaged file). FillGapsByScanning
                            // finds an object only a skipped section named; this finds one a parsed
                            // section misplaced. An offset the table and the header agree on is not tried
                            // twice, a header of another generation is another object (§7.3.10), and an
                            // object with no header anywhere is still refused with the entry's own reason.
                            // The first attempt's nested parses may have written the slot; the second
                            // attempt starts clean, so a success leaves it null.
                            CrossReferenceEntry byHeader = FindHeaderEntry(objectNumber, entry.SectionOffset);
                            LastObjectParseError = null;
                            if (byHeader == null || byHeader.Offset == entry.Offset
                                || byHeader.GenerationNumber != entry.GenerationNumber
                                || !TryParseAtOffset(objectNumber, byHeader.Offset, out obj, out _))
                            {
                                LastObjectParseError = failure;
                                (_failedObjects ??= new Dictionary<int, string>())[objectNumber] = failure;
                                return PdfNull.Instance;
                            }
                            RecordHeaderRecovery(objectNumber, entry, byHeader, failure);
                        }
                        break;

                    case CrossReferenceEntry.EntryType.Compressed:
                        try
                        {
                            obj = ParseObjectFromStream(entry.ObjectStreamNumber, entry.IndexInObjectStream, objectNumber);
                        }
                        catch (Exception ex)
                        {
                            LastObjectParseError = $"obj {objectNumber} (ObjStm): {ex.Message}";
                            return PdfNull.Instance;
                        }
                        break;

                    default:
                        return PdfNull.Instance;
                }

                _objectCache[objectNumber] = obj;
                return obj;
            }
            finally
            {
                _getObjectDepth--;
                _resolving.Remove(objectNumber);
            }
        }

        /// <summary>Nesting of <see cref="GetObject"/> calls, one object's parse needing another; bounded by <see cref="PdfLimits.MaxObjectGraphDepth"/>.</summary>
        private int _getObjectDepth;

        /// <summary>Objects whose parse failed for a reason no later call changes, with that reason — answered from here on.</summary>
        private Dictionary<int, string> _failedObjects;

        /// <summary>How many objects this parser read from their own header because their entry misplaced them.</summary>
        internal int HeaderRecoveries { get; private set; }

        /// <summary>How many times this parser parsed an indirect object at an offset — a test's evidence that a failing object is not parsed again per reference.</summary>
        internal int ObjectParses { get; private set; }

        /// <summary>
        /// Records that <paramref name="objectNumber"/> was read from its header, in the slot and the
        /// count, and treats the table as damaged from then on: a table that misplaces one object may
        /// have no entry at all for another (a shifted table, which reads its root from the header rather than reaching
        /// <see cref="TryRecoverRoot"/>'s full re-index), so the gaps are
        /// filled from the headers once, as a skipped section's are.
        /// </summary>
        private void RecordHeaderRecovery(int objectNumber, CrossReferenceEntry entry, CrossReferenceEntry header, string failure)
        {
            HeaderRecoveries++;
            LastObjectHeaderRecovery =
                $"obj {objectNumber}: its cross-reference entry ({failure}) was wrong; the object was read from its own " +
                $"header at offset {header.Offset.ToString(CultureInfo.InvariantCulture)}, inside the revision whose section at " +
                $"{entry.SectionOffset.ToString(CultureInfo.InvariantCulture)} supplied the entry.";
            if (!_gapsFilledAfterRecovery)
            {
                _gapsFilledAfterRecovery = true;
                FillGapsByScanning();
            }
        }

        private bool _gapsFilledAfterRecovery;

        [System.ThreadStatic]
        private static string _lastObjectHeaderRecovery;

        /// <summary>
        /// How the most recent <see cref="GetObject"/> or <see cref="ResolveObject"/> call <b>on the
        /// calling thread</b> read an object whose cross-reference entry misplaced it — which object,
        /// what was wrong with the entry, and the <c>N G obj</c> header it was read from — or <c>null</c>
        /// when the call read its object where the entry said, or returned nothing. (The file's
        /// <c>%PDF-</c> header is <see cref="LastHeaderRecovery"/>'s.)
        /// </summary>
        /// <remarks>
        /// A recovery leaves a trace here, so a signer, validator or optimizer can learn that the table
        /// misplaced objects then read from scanned headers. Reset at the start of both calls, on every path; the per-parser count is
        /// <see cref="HeaderRecoveries"/>.
        /// </remarks>
        public static string LastObjectHeaderRecovery
        {
            get => _lastObjectHeaderRecovery;
            private set => _lastObjectHeaderRecovery = value;
        }

        /// <summary>
        /// Parses the indirect object at <paramref name="offset"/> and answers whether it is object
        /// <paramref name="objectNumber"/>; <c>false</c>, with the reason, for an offset outside the
        /// file, bytes that do not parse, or a header naming another object. Nothing is recorded —
        /// the caller decides what a failure means, since <see cref="GetObject"/> tries the object's
        /// own header next.
        /// </summary>
        /// <remarks>
        /// <para>An entry offset is file-supplied and names no byte of the file when it falls outside
        /// it. The section readers reject a NEGATIVE one before it reaches the table, but nothing
        /// bounds one that merely points PAST EOF — an offset the scan recovery can also mint from a
        /// truncated file — and seeking there yields whatever <c>ParseIndirectObject</c> makes of
        /// end-of-stream: a message about a token, not about the offset. So the range is checked
        /// first, and the Seek sits INSIDE the try: a bad position reaches <c>Stream.Position</c>,
        /// which throws <see cref="ArgumentOutOfRangeException"/> — not a <see cref="PdfException"/>.</para>
        /// <para>The <c>N G obj</c> header at the offset must name the object that was asked for:
        /// uncompared, a file whose offsets are stale or shifted (a web server prefixing bytes, a naive
        /// concatenation, an attacker aiming one object number at another object's body) would
        /// silently return a DIFFERENT object under the requested number, cache it there, and leave
        /// <see cref="LastObjectParseError"/> null: indistinguishable from a healthy
        /// read. Compared on the header as read, not on the number stamped on the result: the shared
        /// true/false/null instances are never stamped, and judging them by their stamp turned every
        /// top-level keyword object into a mismatch.</para>
        /// </remarks>
        private bool TryParseAtOffset(int objectNumber, long offset, out PdfObject obj, out string failure)
        {
            obj = null;
            failure = null;
            if (offset < 0 || offset >= _stream.Length)
            {
                failure =
                    $"obj {objectNumber.ToString(CultureInfo.InvariantCulture)}: cross-reference offset " +
                    $"{offset.ToString(CultureInfo.InvariantCulture)} is outside the file " +
                    $"(length {_stream.Length.ToString(CultureInfo.InvariantCulture)}).";
                return false;
            }

            int headerObjectNumber;
            ObjectParses++;
            try
            {
                _tokenizer.Seek(offset);
                obj = ParseIndirectObject(out headerObjectNumber, out _);
            }
            catch (Exception ex)
            {
                failure = $"obj {objectNumber}: {ex.Message}";
                return false;
            }

            if (obj != null && headerObjectNumber != objectNumber)
            {
                failure =
                    $"obj {objectNumber}: the cross-reference offset " +
                    $"{offset.ToString(CultureInfo.InvariantCulture)} holds obj " +
                    $"{headerObjectNumber.ToString(CultureInfo.InvariantCulture)}; the file's " +
                    "offsets do not match its objects, so no object was returned.";
                return false;
            }
            return true;
        }

        /// <summary>
        /// The entry the last <c>N G obj</c> header for <paramref name="objectNumber"/> before
        /// <paramref name="before"/> makes — every header when it is negative — or null when there is none.
        /// </summary>
        /// <remarks>
        /// <b>Bounded by the section that supplied the misplaced entry</b>, so the bytes read are the
        /// entry's own revision and the ones it builds on. Unbounded, the last header in the file won:
        /// an object appended after a signature, listed by no section, replaced the signed object a
        /// misplaced entry named, while the change analysis — which reads entries' offsets — saw
        /// nothing after the signature.
        /// </remarks>
        private CrossReferenceEntry FindHeaderEntry(int objectNumber, long before)
        {
            LastHeaders();
            if (!_allHeaders.TryGetValue(objectNumber, out var headers)) return null;
            for (int i = headers.Count - 1; i >= 0; i--)
                if (before < 0 || headers[i].Offset < before) return headers[i];
            return null;
        }

        public PdfObject ResolveObject(PdfObject obj)
        {
            // Reset at the START, on every path, exactly as GetObject does — this is a public entry
            // point, and resolving a DIRECT object never reaches GetObject, so without this the slot
            // still holds some earlier object's failure and the caller attributes it to this one.
            LastObjectParseError = null;
            LastObjectHeaderRecovery = null;

            // Follow chains of indirect references with a guard against cycles
            // (malicious or corrupt PDFs with self-referential indirections).
            //
            // The cycle set is not built until the SECOND hop. Almost every reference in a real
            // file resolves in one — `/Contents 6 0 R` and the like — and a single hop cannot
            // revisit anything, so allocating a HashSet for it was pure overhead on the millions
            // of resolves a render performs. The first object number is kept in a local and
            // seeded into the set only if a second hop actually happens, so the guard covers
            // exactly the same chains as before.
            int MaxChain = PdfLimits.MaxIndirectReferenceChain;
            HashSet<int> seen = null;
            int firstObjNumber = 0;
            int hops = 0;
            while (obj is PdfReference reference)
            {
                // Both refusals below say WHY, and both are written after the previous hop's
                // GetObject — which resets the slot — so the reason that survives is this one's.
                // Without them a chain that is too long and a chain that loops are handed back as
                // the same PdfNull a genuinely absent object produces, which is the one distinction
                // a caller needs to tell a malformed file from an ordinary one.
                if (hops >= MaxChain)
                {
                    LastObjectParseError =
                        $"obj {reference.ReferenceObjectNumber}: the indirect reference chain is " +
                        $"longer than {MaxChain} hops, so no object was returned.";
                    return PdfNull.Instance;
                }
                int objNumber = reference.ReferenceObjectNumber;

                if (hops == 0)
                {
                    firstObjNumber = objNumber;
                }
                else
                {
                    seen ??= new HashSet<int> { firstObjNumber };
                    if (!seen.Add(objNumber))
                    {
                        LastObjectParseError =
                            $"obj {objNumber}: the indirect reference chain returns to an object it " +
                            "has already visited, so no object was returned.";
                        return PdfNull.Instance;
                    }
                }

                hops++;
                obj = GetObject(objNumber);
                if (obj == null) return PdfNull.Instance;
            }
            return obj;
        }

        /// <summary>
        /// Index memory currently retained by <see cref="_objStmReaders"/>, in bytes, charged
        /// against <see cref="PdfLimits.MaxObjStmIndexCacheBytes"/>.
        /// </summary>
        private long _objStmIndexBytes;

        /// <summary>
        /// The bytes the cached object-stream readers retain — their indexes, and the decoded bodies
        /// their streams did not keep — the figure <see cref="PdfLimits.MaxObjStmIndexCacheBytes"/> bounds.
        /// </summary>
        internal long ObjectStreamCacheBytes => _objStmIndexBytes;

        private PdfObject ParseObjectFromStream(int streamObjNumber, int indexInStream, int targetObjNumber)
        {
            ObjectStreamReader reader = GetObjectStreamReader(streamObjNumber);

            // Corrupt ObjStm index — return null rather than throwing, and say so. The index comes
            // from the cross-reference entry and the length from the stream, so the two disagreeing
            // is a malformed file, not an absent object; reported for the same reason the /N
            // mismatch below is.
            if (indexInStream < 0 || indexInStream >= reader.Entries.Length)
            {
                LastObjectParseError =
                    $"obj {targetObjNumber} (ObjStm {streamObjNumber}): the cross-reference entry " +
                    $"names index {indexInStream}, and the stream holds {reader.Entries.Length} " +
                    "objects, so no object was returned.";
                return PdfNull.Instance;
            }

            // §7.5.7 stores an object NUMBER alongside each offset precisely so that a type-2
            // cross-reference entry can be checked against the stream's own index. Without this
            // test the number was parsed and discarded, and a shifted or damaged index made
            // ParseObjectFromStream seek to some other object's body, stamp it with the requested
            // number and hand it back as a structurally valid answer — the wrong font dictionary,
            // the wrong page content, with LastObjectParseError null.
            //
            // It also covers the lenient-tokenizer case: PdfTokenizer.ReadInteger returns 0 for a
            // non-number token, so a garbled index degrades to a run of (0, 0) pairs rather than
            // failing — and 0 is never a legitimate target, so every such entry is refused here.
            int indexedObjNum = reader.Entries[indexInStream].objNum;
            if (indexedObjNum != targetObjNumber)
            {
                LastObjectParseError =
                    $"obj {targetObjNumber} (ObjStm {streamObjNumber}): index {indexInStream} names " +
                    $"obj {indexedObjNum}; the object-stream index disagrees with the " +
                    "cross-reference entry, so no object was returned.";
                return PdfNull.Instance;
            }

            long objOffset = reader.First + reader.Entries[indexInStream].offset;
            // Validate the computed offset against the decoded stream length:
            // a corrupt entry offset would otherwise seek past EOF and yield a
            // misleading object instead of a clean null.
            if (objOffset < 0 || objOffset > reader.DecodedLength)
            {
                LastObjectParseError =
                    $"obj {targetObjNumber} (ObjStm {streamObjNumber}): its entry places the object " +
                    $"at offset {objOffset} in a stream of {reader.DecodedLength} decoded bytes, so " +
                    "no object was returned.";
                return PdfNull.Instance;
            }

            // Two members at one offset are the same bytes read as two objects, and each was parsed
            // and cached as its own copy: a stream of a few kilobytes naming its largest object at
            // offset 0 for two hundred members held two hundred copies of it. §7.5.7 has the offsets
            // increase, so the first member keeps the offset and a later one naming it is refused.
            if (reader.SharedOffsets != null && reader.SharedOffsets.Contains(indexInStream))
            {
                LastObjectParseError =
                    $"obj {targetObjNumber} (ObjStm {streamObjNumber}): index {indexInStream} places the " +
                    $"object at offset {reader.Entries[indexInStream].offset}, where an earlier member " +
                    "of the stream already is (ISO 32000-2 §7.5.7 has the offsets increase), so no " +
                    "object was returned.";
                return PdfNull.Instance;
            }

            // The tokenizer and the parser share one MemoryStream, so seeking the tokenizer
            // positions the parser too, which is what makes two of them over one stream work.
            reader.Tokenizer.Seek(objOffset);
            var obj = reader.Parser.ParseDirectObject();
            StampIdentity(obj, targetObjNumber, 0);   // §7.5.7: a member's generation is always 0
            return obj;
        }

        /// <summary>
        /// Returns the cached reader for one object stream, building it on first use.
        /// The <c>/N</c> and <c>/First</c> validation below runs on that first use; a stream
        /// that fails it, or whose decode fails, is remembered with its reason
        /// (<see cref="_failedObjectStreams"/>), and every later member is refused with that
        /// reason rather than decoding the container again, uncharged, once per member.
        /// </summary>
        private ObjectStreamReader GetObjectStreamReader(int streamObjNumber)
        {
            if (_failedObjectStreams != null && _failedObjectStreams.TryGetValue(streamObjNumber, out string failed))
                throw new PdfParseException(failed);
            try
            {
                return BuildObjectStreamReader(streamObjNumber);
            }
            catch (PdfException ex)
            {
                (_failedObjectStreams ??= new Dictionary<int, string>())[streamObjNumber] = ex.Message;
                throw;
            }
        }

        /// <summary>Object streams whose reader could not be built, with the reason, for this parser.</summary>
        private Dictionary<int, string> _failedObjectStreams;

        private ObjectStreamReader BuildObjectStreamReader(int streamObjNumber)
        {
            if (_objStmReaders != null &&
                _objStmReaders.TryGetValue(streamObjNumber, out var cached))
            {
                _objStmRecency.Remove(cached);
                _objStmRecency.AddLast(cached);
                return cached.Value.Reader;
            }
            if (_objStmUncached.Reader != null && _objStmUncached.Stream == streamObjNumber)
                return _objStmUncached.Reader;

            var streamObj = GetObject(streamObjNumber);
            if (!(streamObj is PdfStream objStream))
                throw new PdfParseException($"Object {streamObjNumber} is not an object stream.");

            string codec = SyntaxStreamFilters.ImageCodecOf(objStream);
            if (codec != null)
                throw new PdfParseException(
                    $"ObjStm {streamObjNumber}: /Filter names /{codec}, an image codec, whose output is " +
                    "samples and never object syntax; it was not decoded.");

            byte[] decoded = objStream.GetDecodedDataUnder(SyntaxStreamWork);
            int n = objStream.GetInteger(PdfName.N) ?? 0;
            int first = objStream.GetInteger(PdfName.First) ?? 0;

            // Bounds-check /N and /First before reading the index — adversarial
            // ObjStm streams can advertise huge entry counts to drive memory
            // exhaustion, or negative values to wrap on multiplication.
            if (n < 0 || n > PdfLimits.MaxObjStmEntries)
                throw new PdfParseException(
                    $"ObjStm {streamObjNumber}: invalid /N entry count {n}.");
            if (first < 0 || first > decoded.Length)
                throw new PdfParseException(
                    $"ObjStm {streamObjNumber}: invalid /First {first} (decoded length {decoded.Length}).");

            // …and cross-check /N against the bytes that would have to be there for it to be true.
            // The constant cap above bounds the count but not the AMPLIFICATION: an entry is two
            // integers and a separator, so N entries need at least 4N decoded bytes, and a stream
            // declaring /N 65536 over a three-object body buys a 512 KiB array for ~67 bytes of
            // file. Charging the declared count against the body the file actually supplied is what
            // closes that, and it cannot reject a real ObjStm — its index is physically present.
            if ((long)n * PdfLimits.MinObjStmIndexBytesPerEntry > decoded.Length)
                throw new PdfParseException(
                    $"ObjStm {streamObjNumber}: /N declares {n} entries, which needs at least " +
                    $"{(long)n * PdfLimits.MinObjStmIndexBytesPerEntry} bytes of index, but the " +
                    $"stream decodes to {decoded.Length}.");

            // Not disposed, and deliberately: the reader outlives this call. A MemoryStream over
            // a byte[] holds no unmanaged resource, so there is nothing to release — its Dispose
            // only flips a flag that would make every later read fail.
            var ms = new MemoryStream(decoded);
            var reader = new ObjectStreamReader
            {
                First         = first,
                DecodedLength = decoded.Length,
                Tokenizer     = new PdfTokenizer(ms),
                Parser        = new PdfParser(ms),
                Entries       = new (int objNum, int offset)[n],
            };
            reader.Parser._xrefTable = _xrefTable;

            // The parser's index budget (GAPS §7b): the cache bounds what is retained, and a cycle of
            // streams wider than it re-reads one whole index per member, so the work is charged here,
            // on every read, and refused past the budget with the counts the refusal is derived from.
            if (_objStmIndexEntriesRead + n > PdfLimits.MaxObjStmIndexEntriesRead)
                throw new PdfParseException(
                    $"ObjStm {streamObjNumber}: reading its {n}-entry index would take this document past the " +
                    $"{PdfLimits.MaxObjStmIndexEntriesRead} index entries one parser reads ({_objStmIndexEntriesRead} read so far); " +
                    "its object streams cycle through more indexes than the cache holds.");
            _objStmIndexEntriesRead += n;

            // Read the index once: pairs of (objNum, offset) repeated N times.
            ObjectStreamIndexReads++;
            HashSet<int> offsetsSeen = null;
            for (int i = 0; i < n; i++)
            {
                int objNum = reader.Tokenizer.ReadInteger();
                int offset = reader.Tokenizer.ReadInteger();
                reader.Entries[i] = (objNum, offset);
                if (!(offsetsSeen ??= new HashSet<int>()).Add(offset))
                    (reader.SharedOffsets ??= new HashSet<int>()).Add(i);
            }

            // Cache within the retained index budget, evicting the least recently used readers to
            // make room. A reader larger than the whole budget is not cached, and is kept alone as
            // the last uncached one: the next member of the same stream reuses it rather than
            // reading the index again. Refusing to *parse* would be the wrong trade: the document is
            // legitimate, only the cache is full.
            //
            // A reader's cost is its index, and its decoded body too when the stream did not keep
            // that body — past the document's memo ceiling the reader is the body's only owner, and
            // counted by its index alone a /N 1 stream of a megabyte cost eight bytes.
            long entryBytes = (long)n * ObjectStreamReader.EntryByteSize
                              + (objStream.IsDecodedDataMemoized ? 0 : decoded.LongLength);
            if (entryBytes > PdfLimits.MaxObjStmIndexCacheBytes)
            {
                _objStmUncached = (streamObjNumber, reader);
                return reader;
            }
            _objStmReaders ??= new Dictionary<int, LinkedListNode<(int Stream, ObjectStreamReader Reader, long Bytes)>>();
            _objStmRecency ??= new LinkedList<(int Stream, ObjectStreamReader Reader, long Bytes)>();
            while (_objStmIndexBytes + entryBytes > PdfLimits.MaxObjStmIndexCacheBytes && _objStmRecency.First != null)
            {
                var oldest = _objStmRecency.First;
                _objStmRecency.RemoveFirst();
                _objStmReaders.Remove(oldest.Value.Stream);
                _objStmIndexBytes -= oldest.Value.Bytes;
            }
            _objStmReaders[streamObjNumber] = _objStmRecency.AddLast((streamObjNumber, reader, entryBytes));
            _objStmIndexBytes += entryBytes;
            return reader;
        }

        /// <summary>
        /// Enumerates all objects in the cross-reference table.
        /// </summary>
        public IEnumerable<KeyValuePair<int, PdfObject>> EnumerateAllObjects()
            => EnumerateAllObjects(null);

        /// <summary>
        /// Enumerates all objects in the cross-reference table.
        /// Objects that cannot be parsed are skipped; <paramref name="onError"/> is invoked
        /// for each such object so callers can detect and report partial parses.
        /// </summary>
        /// <param name="onError">
        /// Optional callback invoked with the object number and the exception when an
        /// individual object fails to parse. May be <c>null</c>.
        /// </param>
        public IEnumerable<KeyValuePair<int, PdfObject>> EnumerateAllObjects(Action<int, Exception> onError)
        {
            if (_xrefTable == null)
                yield break;

            foreach (var entry in _xrefTable.AllEntries)
            {
                if (entry.Type == CrossReferenceEntry.EntryType.Free)
                    continue;

                PdfObject obj = null;
                string errBefore = LastObjectParseError;
                try
                {
                    obj = GetObject(entry.ObjectNumber);
                }
                catch (Exception ex)
                {
                    onError?.Invoke(entry.ObjectNumber, ex);
                    continue;
                }

                // GetObject catches internal parse errors and returns PdfNull rather than
                // throwing.  When LastObjectParseError changed, fire onError so callers
                // still get diagnostic visibility.
                if (obj is PdfNull && LastObjectParseError != errBefore)
                {
                    onError?.Invoke(entry.ObjectNumber,
                        new PdfParseException(LastObjectParseError ?? "Object parse failed."));
                    continue;
                }

                if (obj != null && !(obj is PdfNull))
                    yield return new KeyValuePair<int, PdfObject>(entry.ObjectNumber, obj);
            }
        }

        /// <summary>
        /// How far past the start of a stream's data <see cref="FindEndStreamFrom"/> looks for the
        /// <c>endstream</c> keyword. Named because the refusal is reported to the caller and the
        /// message states the window that was searched; the value is
        /// <see cref="PdfLimits.MaxEndStreamSearchBytes"/>, where every DoS cap belongs.
        /// </summary>
        private static long FindEndStreamWindowBytes => PdfLimits.MaxEndStreamSearchBytes;

        /// <summary>
        /// Scans from <paramref name="dataStart"/> forward for the <c>endstream</c> keyword
        /// and returns the number of stream-data bytes before it (stripping the preceding EOL).
        /// Returns -1 if not found within <see cref="FindEndStreamWindowBytes"/>.
        /// The usual stream-length search.
        /// </summary>
        /// <remarks>
        /// <para><b>Read a chunk at a time, stopping at the first match</b>, so a search costs what
        /// the stream's real length does: reading the whole window first made every stream with an
        /// unusable <c>/Length</c> cost the rest of the file, up to the window, and a file of many
        /// such streams quadratic in its size.</para>
        /// <para><b>A span searched without a match is remembered</b>
        /// (<see cref="_endstreamAbsentFrom"/>): a later search starting inside it begins where it
        /// ended, so many streams with no <c>endstream</c> at all read the file about once, not
        /// once each.</para>
        /// </remarks>
        private int FindEndStreamFrom(long dataStart)
        {
            long limit = Math.Min(_stream.Length - dataStart, FindEndStreamWindowBytes);
            if (limit <= 0) return -1;
            long end = dataStart + limit;

            // Where this search may start reading: past a span an earlier search found empty, less
            // the bytes a match straddling its end needs.
            long readFrom = dataStart;
            if (dataStart >= _endstreamAbsentFrom && dataStart < _endstreamAbsentTo)
            {
                if (end <= _endstreamAbsentTo) return -1;
                readFrom = Math.Max(dataStart, _endstreamAbsentTo - EndStreamCarry);
            }

            const int Chunk = 64 * 1024;
            int step = 256;   // the first read, doubled each time up to Chunk: a search costs about twice the stream
            int maxBuffer = (int)Math.Min(end - readFrom, Chunk) + EndStreamCarry;
            var buf = new byte[Math.Min(maxBuffer, step + EndStreamCarry)];
            long bufStart = readFrom;   // the file offset of buf[0]
            int filled = 0, scanFrom = 0;
            long nextRead = readFrom;
            while (true)
            {
                if (buf.Length - filled < step && buf.Length < maxBuffer)
                    Array.Resize(ref buf, Math.Min(maxBuffer, Math.Max(buf.Length * 2, filled + step)));
                int want = (int)Math.Min(Math.Min(buf.Length - filled, step), end - nextRead);
                step = Math.Min(step * 2, Chunk);
                int got = 0;
                if (want > 0)
                {
                    _stream.Position = nextRead;
                    got = StreamBytes.Fill(_stream, buf, filled, want);
                }
                filled += got;
                nextRead += got;
                bool atEnd = got < want || nextRead >= end;
                bool bufferFull = filled == buf.Length && buf.Length == maxBuffer;

                // A match needs the byte after it unless the window ends there.
                int searchEnd = atEnd ? filled - 9 : filled - 10;
                for (int i = scanFrom; i <= searchEnd; i++)
                {
                    if (!IsEndStreamAt(buf, i)) continue;
                    if (i + 9 < filled && !PdfConstants.IsWhitespace(buf[i + 9])) continue;

                    // Strip the EOL that precedes the keyword (spec §7.3.8.1).
                    long keyword = bufStart + i;
                    long length = keyword - dataStart;
                    if (length > 0 && i > 0 && buf[i - 1] == '\n') { length--; i--; }
                    if (length > 0 && i > 0 && buf[i - 1] == '\r') length--;
                    return (int)length;
                }
                if (atEnd)
                {
                    _endstreamAbsentFrom = dataStart;
                    _endstreamAbsentTo = end;
                    return -1;
                }

                if (!bufferFull)
                {
                    // Room left: the next read appends, and the scan resumes where it stopped.
                    scanFrom = Math.Max(scanFrom, searchEnd + 1);
                    continue;
                }

                // Keep the last bytes, so a keyword across the chunk boundary, and the two EOL
                // bytes before it, are still seen.
                int keepFrom = filled - EndStreamCarry;
                Buffer.BlockCopy(buf, keepFrom, buf, 0, EndStreamCarry);
                bufStart += keepFrom;
                scanFrom = Math.Max(0, searchEnd + 1 - keepFrom);
                filled = EndStreamCarry;
            }
        }

        /// <summary>The two EOL bytes before <c>endstream</c>, its nine, and the byte after it.</summary>
        private const int EndStreamCarry = 12;

        /// <summary>The span <see cref="FindEndStreamFrom"/> last searched without finding the keyword.</summary>
        private long _endstreamAbsentFrom = -1, _endstreamAbsentTo = -1;

        private static bool IsEndStreamAt(byte[] buf, int i)
            => buf[i] == (byte)'e' && buf[i + 1] == (byte)'n' && buf[i + 2] == (byte)'d'
               && buf[i + 3] == (byte)'s' && buf[i + 4] == (byte)'t' && buf[i + 5] == (byte)'r'
               && buf[i + 6] == (byte)'e' && buf[i + 7] == (byte)'a' && buf[i + 8] == (byte)'m';
        // §7.3.4.3's odd-digit rule and the packing under it are PdfConstants.HexStringToBytes,
        // shared with ContentStreamParser. The two carried byte-identical copies while both already
        // called PdfConstants.HexToNibble — a shared lookup under two copies of the spec rule.
        private static byte[] HexToBytes(string hex) => PdfConstants.HexStringToBytes(hex);
    }
}
