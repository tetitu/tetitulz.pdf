// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    public sealed class PdfWriter
    {
        /// <summary>
        /// Largest value the 5-digit generation field of a cross-reference entry can hold
        /// (PDF 32000-1:2008, 7.5.4). The head of the free list — object 0 — is also required
        /// to carry exactly this generation.
        /// </summary>
        private const int MaxGenerationNumber = Constants.PdfXrefFormat.MaxGenerationNumber;

        /// <summary>
        /// Largest byte offset the 10-digit fixed-width cross-reference field can express,
        /// i.e. 9 999 999 999 bytes (~9.31&#160;GiB). A larger file needs a cross-reference
        /// stream (PDF 1.5+) instead of a classic xref table.
        /// </summary>
        private const long MaxXrefOffset = 9_999_999_999L;

        private readonly Stream _output;
        private readonly Dictionary<int, long> _objectOffsets = new Dictionary<int, long>();

        public PdfWriter(Stream output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public long Position => _output.Position;

        /// <summary>
        /// How many <see cref="PdfStream"/>s were serialised as a <b>nested</b> value and therefore
        /// written without their data. Zero for a well-formed write.
        ///
        /// <para><b>Why this counter exists.</b> §7.3.8 requires a stream to be an indirect object,
        /// so <see cref="WriteObject"/> can only write a nested one's dictionary entries —
        /// <see cref="PdfStream.RawData"/> has nowhere to go. It did that <i>silently</i>, and the
        /// PostScript document writer lost every embedded font program that way: the output carried
        /// a <c>/FontFile</c> whose stream had no bytes, and the conversion reported success.</para>
        ///
        /// <para>Writing the data inline instead would be worse — it is unparseable, so a detectable
        /// omission would become a corrupt file — and throwing is not available either, since this
        /// class serialises straight to the output and an exception mid-object leaves a half-written
        /// one. Reporting is the honest option: a caller that builds its own object graph must
        /// promote its streams (see <c>ResourcePromoter</c> in the PostScript plugin) and can check
        /// this afterwards to know whether it missed one.</para>
        ///
        /// <para><b>An instance property, deliberately not a <c>[ThreadStatic]</c> <c>Last*</c>
        /// slot.</b> A writer is constructed per output stream, so the natural scope of "did this
        /// write lose data?" is the object itself. That is inherently per-call, needs no reset
        /// discipline, and cannot be overwritten by the next object the way a shared slot would —
        /// sidestepping the per-thread-is-not-per-document trap CLAUDE.md records.</para>
        /// </summary>
        public int NestedStreamsWrittenWithoutData { get; private set; }

        /// <summary>
        /// How many <see cref="PdfNumber"/>s this writer had to coerce to <c>0</c> because their
        /// value was <see cref="double.NaN"/> or an infinity. Zero for a well-formed write.
        ///
        /// <para><b>Why coerce rather than throw.</b> ISO 32000-1 §7.3.3 defines no syntax for a
        /// non-finite number, and the fixed-point format this writer requires turns one into the
        /// bare keyword <c>NaN</c> or <c>Infinity</c> — a token that stops every parser at the
        /// object containing it. Emitting <c>0</c> yields a readable file whose one number is
        /// wrong; emitting the keyword yields no readable file at all. Throwing is not available
        /// for the same reason it is not available to
        /// <see cref="NestedStreamsWrittenWithoutData"/>: this class serialises straight to the
        /// output, so an exception mid-object leaves a half-written one in the caller's
        /// stream.</para>
        ///
        /// <para>An instance property for the same reason as
        /// <see cref="NestedStreamsWrittenWithoutData"/> — per-output-stream scope needs no reset
        /// discipline and cannot be overwritten by the next object.</para>
        /// </summary>
        public int NonFiniteNumbersCoercedToZero { get; private set; }

        public void WriteRaw(byte[] data)
        {
            _output.Write(data, 0, data.Length);
        }

        public void WriteRaw(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            _output.Write(bytes, 0, bytes.Length);
        }

        public void WriteLine(string text = "")
        {
            WriteRaw(text + "\n");
        }

        public void WriteHeader(Document.PdfVersion version)
        {
            WriteRaw($"%PDF-{version.Major}.{version.Minor}\n");
            // Binary comment per PDF spec recommendation
            WriteRaw(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
        }

        /// <summary>
        /// Encrypts the strings and stream data of every object written through
        /// <see cref="WriteIndirectObject(int, int, PdfObject)"/>, for a revision appended to an
        /// already-encrypted document. <see langword="null"/> (the default) writes clear text,
        /// which is correct only for an unencrypted file.
        ///
        /// <para>Supplied by <c>Tetitulz.Pdf.Encryption</c>; see <see cref="IPdfObjectEncryptor"/>
        /// for why Core declares the operation but cannot implement it.</para>
        /// </summary>
        public IPdfObjectEncryptor ObjectEncryptor { get; set; }

        /// <summary>The object currently being written, for per-object key derivation.</summary>
        private int _currentObjectNumber;
        private int _currentGenerationNumber;

        /// <summary>
        /// Whether strings written right now are inside an object that gets encrypted. False both
        /// when there is no encryptor and while writing one of the objects §7.6.2 excludes.
        /// </summary>
        private bool _encryptCurrentObject;

        public long WriteIndirectObject(int objectNumber, int generation, PdfObject obj)
            => WriteIndirectObject(objectNumber, generation, obj, encrypt: true);

        /// <summary>
        /// Writes one indirect object, optionally exempting it from
        /// <see cref="ObjectEncryptor"/>.
        /// </summary>
        /// <param name="encrypt">
        /// <see langword="false"/> for the objects ISO 32000-1 §7.6.2 excludes from encryption —
        /// in practice the cross-reference stream, whose dictionary also carries the trailer's
        /// unencrypted <c>/ID</c>. Ignored when no encryptor is set.
        /// </param>
        /// <param name="objectNumber">The object's number.</param>
        /// <param name="generation">The object's generation number.</param>
        /// <param name="obj">The object to write.</param>
        /// <returns>The byte offset the object was written at, for the cross-reference entry.</returns>
        public long WriteIndirectObject(int objectNumber, int generation, PdfObject obj, bool encrypt)
        {
            long offset = _output.Position;
            _objectOffsets[objectNumber] = offset;

            var encryptor = encrypt ? ObjectEncryptor : null;
            _currentObjectNumber = objectNumber;
            _currentGenerationNumber = generation;
            _encryptCurrentObject = encryptor != null && encryptor.EncryptsStrings;

            try
            {
                // For a stream, force /Length to match the raw data BEFORE the dictionary is
                // serialised — WriteObject writes the dict, so setting /Length afterwards (as this
                // method used to) left a stream with no/stale /Length, which strict readers reject.
                // Encryption happens first for the same reason: AES prepends an IV and pads, so
                // the ciphertext is longer than the plaintext and /Length must describe what is
                // actually written between `stream` and `endstream`.
                byte[] streamRaw = null;
                if (obj is PdfStream stream)
                {
                    streamRaw = stream.RawData ?? Array.Empty<byte>();
                    // §7.4.10: a stream whose own /Crypt filter names /Identity is in the clear in an
                    // encrypted document, whatever /StmF or /EFF say, and a reader takes it as it is.
                    if (IO.Filters.StreamCryptFilter.TryGetName(stream, out PdfName own) && own == PdfName.Identity) { }
                    // An embedded file stream takes the document's /EFF crypt filter (§7.6.5) where
                    // the encryptor knows it — not /StmF, which a reader does not decrypt it under.
                    else if (encryptor is IPdfEmbeddedFileEncryptor files && stream.GetName(PdfName.Type) == PdfName.EmbeddedFile)
                    {
                        if (files.EncryptsEmbeddedFiles)
                            streamRaw = files.EncryptEmbeddedFile(streamRaw, objectNumber, generation);
                    }
                    else if (ShouldEncryptStreamData(encryptor, stream, objectNumber))
                        streamRaw = encryptor.EncryptStream(streamRaw, objectNumber, generation);
                    stream.Set(PdfName.Length, new PdfNumber(streamRaw.Length));
                }

                WriteRaw($"{objectNumber} {generation} obj\n");
                WriteObject(obj);
                WriteRaw("\n");

                if (streamRaw != null)
                {
                    WriteRaw("stream\n");
                    _output.Write(streamRaw, 0, streamRaw.Length);
                    WriteRaw("\nendstream\n");
                }

                WriteRaw("endobj\n");
                return offset;
            }
            finally
            {
                // Strings written outside an indirect object — the trailer's /ID, above all — are
                // never encrypted, so the context must not leak past this object.
                _encryptCurrentObject = false;
                _currentObjectNumber = 0;
                _currentGenerationNumber = 0;
            }
        }

        /// <summary>
        /// The document-level metadata stream is exempt when the document declares
        /// <c>/EncryptMetadata false</c>: the XMP packet is deliberately readable there, and
        /// encrypting a newly written one would contradict the document's own declaration. Only that
        /// stream (Table 20): an encryptor that names it (<see cref="IPdfDocumentMetadataEncryptor"/>)
        /// has every other <c>/Type /Metadata</c> stream encrypted; one that does not leaves each in clear.
        /// </summary>
        private static bool ShouldEncryptStreamData(IPdfObjectEncryptor encryptor, PdfStream stream, int objectNumber)
        {
            if (encryptor == null || !encryptor.EncryptsStreams) return false;
            if (encryptor.EncryptsMetadataStreams) return true;
            if (stream.GetName(PdfName.Type) != PdfName.Metadata) return true;
            return encryptor is IPdfDocumentMetadataEncryptor document
                   && document.DocumentMetadataObjectNumber > 0
                   && document.DocumentMetadataObjectNumber != objectNumber;
        }

        /// <summary>
        /// Serialises <paramref name="obj"/> at the current position.
        ///
        /// <para>Nesting is tracked so <see cref="NestedStreamsWrittenWithoutData"/> can tell a
        /// stream written <i>as</i> an object — which <see cref="WriteIndirectObject(int,int,PdfObject)"/> follows with
        /// its data, and which <c>PdfOptimizer.ComputeObjectHash</c> deliberately calls directly —
        /// from one buried inside another object, where the data has nowhere to go.</para>
        /// </summary>
        public void WriteObject(PdfObject obj)
        {
            _writeDepth++;
            try { WriteObjectCore(obj); }
            finally { _writeDepth--; }
        }

        private int _writeDepth;

        private void WriteObjectCore(PdfObject obj)
        {
            if (obj == null || obj is PdfNull)
            {
                WriteRaw("null");
                return;
            }

            if (obj is PdfBoolean boolObj)
            {
                WriteRaw(boolObj.Value ? "true" : "false");
                return;
            }

            if (obj is PdfNumber numObj)
            {
                if (numObj.IsInteger)
                {
                    WriteRaw(numObj.LongValue.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    // PDF spec requires fixed-point notation; scientific notation (e.g. "1E-06")
                    // is not allowed. Limit to 6 decimal places to avoid bloat, and strip trailing
                    // zeros for compact output.
                    double v = numObj.DoubleValue;

                    // §7.3.3 has no syntax for a non-finite number, and "F6" formats one as the
                    // bare keyword `NaN` / `Infinity` / `-Infinity` — a token no reader accepts,
                    // written with no exception. A layout that divides by zero can reach here, and
                    // `/MediaBox [0 0 NaN 842]` would ship as a corrupt file that parses nowhere;
                    // the value is written as 0 and counted.
                    // PdfRealFormat is the rule; the count is this writer's, because the caller
                    // wants to know per output stream how many coercions it paid for.
                    if (!PdfRealFormat.TryFormat(v, out string s)) NonFiniteNumbersCoercedToZero++;
                    WriteRaw(s);
                }
                return;
            }

            if (obj is PdfString strObj)
            {
                // In an encrypted document every string is ciphertext; the escaping below then
                // applies to the ciphertext bytes, which is what a reader unescapes before
                // decrypting.
                byte[] bytes = _encryptCurrentObject
                    ? ObjectEncryptor.EncryptString(strObj.RawBytes, _currentObjectNumber, _currentGenerationNumber)
                    : strObj.RawBytes;

                if (strObj.IsHex)
                {
                    WriteRaw("<");
                    foreach (byte b in bytes)
                        WriteRaw(b.ToString("X2"));
                    WriteRaw(">");
                }
                else
                {
                    WriteRaw("(");
                    foreach (byte b in bytes)
                    {
                        switch (b)
                        {
                            case (byte)'(': WriteRaw("\\("); break;
                            case (byte)')': WriteRaw("\\)"); break;
                            case (byte)'\\': WriteRaw("\\\\"); break;
                            case (byte)'\r': WriteRaw("\\r"); break;
                            case (byte)'\n': WriteRaw("\\n"); break;
                            default:
                                _output.WriteByte(b);
                                break;
                        }
                    }
                    WriteRaw(")");
                }
                return;
            }

            if (obj is PdfName nameObj)
            {
                // PDF §7.3.5: a name's bytes are UTF-8, each byte outside the regular printable range
                // hex-escaped as #XX — and a name read from bytes that are not UTF-8 is written from
                // those bytes, since a content stream copied byte for byte still names it by them.
                // One implementation, in PdfNameEscape. The escaped form is pure printable ASCII by
                // construction, so writing it through the ASCII WriteRaw is byte-identical to a
                // per-byte loop.
                WriteRaw("/" + PdfNameEscape.Escape(nameObj));
                return;
            }

            if (obj is PdfReference refObj)
            {
                WriteRaw($"{refObj.ReferenceObjectNumber} {refObj.ReferenceGenerationNumber} R");
                return;
            }

            if (obj is PdfArray arrObj)
            {
                WriteRaw("[");
                for (int i = 0; i < arrObj.Count; i++)
                {
                    if (i > 0) WriteRaw(" ");
                    WriteObject(arrObj[i]);
                }
                WriteRaw("]");
                return;
            }

            if (obj is PdfStream streamObj)
            {
                // Reached only for a stream nested inside another object, which §7.3.8 forbids:
                // WriteIndirectObject handles a top-level one and writes its data. Only the
                // dictionary can be written here, so any RawData is lost — counted rather than
                // dropped in silence. See NestedStreamsWrittenWithoutData.
                if (_writeDepth > 1 && streamObj.RawData is { Length: > 0 })
                    NestedStreamsWrittenWithoutData++;

                WriteDictionaryEntries(streamObj);
                return;
            }

            if (obj is PdfDictionary dictObj)
            {
                WriteDictionaryEntries(dictObj);
                return;
            }
        }

        private void WriteDictionaryEntries(PdfDictionary dict)
        {
            // ISO 32000-2 §7.6.2 exempts from encryption "any hexadecimal strings representing the
            // value of the Contents key in a Signature dictionary": the hole a /ByteRange skips IS
            // that string, so the CMS a verifier reads must be the bytes stored, not ciphertext.
            // Decided once per dictionary, by the one classification the decryptor also uses.
            bool signature = _encryptCurrentObject
                && Document.SignatureDictionaryShape.IsSignatureValue(dict, Document.SignatureDictionaryShape.Direct);

            WriteRaw("<<");
            foreach (var kvp in dict)
            {
                WriteRaw(" ");
                WriteObject(kvp.Key);
                WriteRaw(" ");
                if (signature && Document.SignatureDictionaryShape.IsUnencryptedEntry(dict, kvp.Key))
                    WriteUnencrypted(kvp.Value);
                else
                    WriteObject(kvp.Value);
            }
            WriteRaw(" >>");
        }

        /// <summary>
        /// Writes one value with string encryption suspended, inside an object that is otherwise
        /// encrypted — the §7.6.2 <c>/Contents</c> exemption. Restored on every path, so a throwing
        /// write cannot leave the next entry in the clear.
        /// </summary>
        private void WriteUnencrypted(PdfObject value)
        {
            bool saved = _encryptCurrentObject;
            _encryptCurrentObject = false;
            try { WriteObject(value); }
            finally { _encryptCurrentObject = saved; }
        }

        /// <summary>
        /// Writes an xref table. For incremental updates, set <paramref name="includeObject0"/>
        /// to false — object 0 was already declared in the original xref.
        /// </summary>
        /// <remarks>
        /// This overload assumes generation 0 for every entry. Full-write paths (the
        /// Builder / encryptors / optimizer) only ever emit gen-0 objects, so they use
        /// this overload. For incremental updates that may carry gen&gt;0 objects, use the
        /// <see cref="WriteXrefTable(List{ValueTuple{int,int,long}},int,bool)"/> overload
        /// so the xref generation matches the object header.
        /// </remarks>
        public long WriteXrefTable(List<(int objNum, long offset)> entries,
            int startObjNum = 0, bool includeObject0 = true)
        {
            // Promote to the generation-aware overload with gen 0 for every entry.
            var withGen = new List<(int objNum, int gen, long offset)>(entries.Count);
            foreach (var e in entries)
                withGen.Add((e.objNum, 0, e.offset));
            return WriteXrefTable(withGen, startObjNum, includeObject0);
        }

        /// <summary>
        /// Writes an xref table whose entries carry an explicit generation number.
        /// Incremental updates that revise a gen&gt;0 object MUST use this overload so the
        /// xref-entry generation matches the object header written by
        /// <see cref="WriteIndirectObject(int,int,PdfObject)"/> — otherwise strict readers treat the entry as
        /// stale and silently drop the update.
        /// </summary>
        public long WriteXrefTable(List<(int objNum, int gen, long offset)> entries,
            int startObjNum = 0, bool includeObject0 = true)
        {
            long xrefOffset = _output.Position;
            WriteRaw("xref\n");

            // Work on a copy to avoid mutating the caller's list.
            var sorted = new List<(int objNum, int gen, long offset)>(entries);
            sorted.Sort((a, b) => a.objNum.CompareTo(b.objNum));

            if (includeObject0)
            {
                bool hasObj0 = sorted.Count > 0 && sorted[0].objNum == 0;
                if (!hasObj0)
                    sorted.Insert(0, (0, 0, 0));
            }

            // Use sorted copy from here on.
            entries = sorted;

            // Group into subsections
            int i = 0;
            while (i < entries.Count)
            {
                int subStart = entries[i].objNum;
                int subEnd = subStart;

                // Find contiguous range
                int j = i + 1;
                while (j < entries.Count && entries[j].objNum == subEnd + 1)
                {
                    subEnd = entries[j].objNum;
                    j++;
                }

                int count = j - i;
                WriteRaw($"{subStart} {count}\n");

                for (int k = i; k < j; k++)
                {
                    if (entries[k].objNum == 0)
                    {
                        // Object 0 must always be free with the maximum generation number.
                        WriteRaw(Constants.PdfXrefFormat.FreeListHead);
                    }
                    else
                    {
                        // PDF spec §7.5.4: xref offset is a 10-digit decimal,
                        // capping the file at 9 999 999 999 bytes (~9.31 GiB).
                        // Files larger than this would silently truncate via
                        // string.Format wrap-around — emit a clear failure
                        // instead, since the resulting xref would be unreadable
                        // and the whole document corrupted.
                        if (entries[k].offset > MaxXrefOffset)
                            throw new InvalidOperationException(
                                $"PDF xref offset {entries[k].offset} exceeds the 10-digit " +
                                "fixed-width xref format limit (~9.31 GiB).  Switch to a " +
                                "cross-reference stream (PDF 1.5+) for files this large.");
                        // Generation is a 5-digit field (0–65535).  Clamp defensively so a
                        // bogus value can never overflow the fixed-width slot.
                        int gen = entries[k].gen;
                        if (gen < 0) gen = 0;
                        if (gen > MaxGenerationNumber) gen = MaxGenerationNumber;
                        WriteRaw(PdfXrefFormat.Entry(entries[k].offset, gen));
                    }
                }

                i = j;
            }

            return xrefOffset;
        }

        public void WriteTrailer(PdfDictionary trailer, long xrefOffset)
        {
            WriteRaw("trailer\n");
            WriteObject(trailer);
            WriteRaw($"\nstartxref\n{xrefOffset}\n%%EOF\n");
        }

        public Dictionary<int, long> ObjectOffsets => _objectOffsets;
    }
}
