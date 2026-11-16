// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>The per-object cipher of a <see cref="TestPdfs.LegacyRc4.RevisionWriter"/> document.</summary>
public enum LegacyCipher
{
    /// <summary><c>/V 1 /R 2</c>: RC4 under the 40-bit file key.</summary>
    Rc4With40BitKey,

    /// <summary>
    /// <c>/V 4 /R 4</c> with a <c>/StdCF</c> of <c>/CFM /AESV2</c>: AES-128-CBC under per-object keys. The other
    /// handler whose object key mixes in the object identifier (ISO 32000-2 §7.6.3.2, Algorithm 1) — and the one where a
    /// wrong key fails PKCS#7 padding instead of decrypting to noise.
    /// </summary>
    Aes128,
}

internal static partial class TestPdfs
{
    public static partial class LegacyRc4
    {
        /// <summary>Bytes of the revision-4 file key: <c>/Length 128</c>.</summary>
        private const int Revision4KeyBytes = 16;

        /// <summary>
        /// Writes an encrypted document one revision at a time, each closed by its own cross-reference stream whose
        /// <c>/Prev</c> names the one before.
        /// </summary>
        /// <remarks>
        /// <para><b>Cross-reference streams, because only they can say an object lives in an object stream</b> — a
        /// classic <c>xref</c> table has no type-2 entry. That is the shape in which a later revision can rewrite, move or
        /// free an object an earlier revision stored inside an object stream.</para>
        /// <para><b>Every key is computed here</b>, from this class's own transcriptions of ISO 32000-2 §7.6 — Algorithms
        /// 2, 3 and 4 for revision 2 in <c>TestPdfs.cs</c>, Algorithms 2, 3 and 5 for revision 4 below, Algorithm 1 for
        /// both — so the decryptor is handed an independent vector, not a document this library encrypted for itself.
        /// The user password is empty and the owner password is "owner-secret".</para>
        /// <para><b>Strings are the caller's to encipher</b>, through <see cref="EncryptedString"/>: Algorithm 1 keys a
        /// string by the indirect object that contains it, and only the caller knows where it is putting one.</para>
        /// </remarks>
        public sealed class RevisionWriter
        {
            private const string OwnerPasswordValue = "owner-secret";

            private readonly LegacyCipher _cipher;
            private readonly byte[] _fileId;
            private readonly byte[] _fileKey;
            private readonly byte[] _ownerValue;
            private readonly byte[] _userValue;
            private readonly List<byte> _bytes = new List<byte>();

            /// <summary>This revision's cross-reference entries: type, second field, third field.</summary>
            private readonly SortedDictionary<int, (byte Type, long Field2, int Field3)> _entries =
                new SortedDictionary<int, (byte Type, long Field2, int Field3)>();

            private int _previousXrefOffset = -1;
            private int _highestObjectNumber;

            public RevisionWriter(byte[] fileId, LegacyCipher cipher = LegacyCipher.Rc4With40BitKey)
            {
                _fileId = fileId;
                _cipher = cipher;

                if (cipher == LegacyCipher.Rc4With40BitKey)
                {
                    _ownerValue = OwnerValue(OwnerPasswordValue, "");
                    _fileKey = FileKey("", _ownerValue, PValue, fileId);
                    _userValue = UserValue(_fileKey);
                }
                else
                {
                    _ownerValue = OwnerValueRevision4(OwnerPasswordValue, "");
                    _fileKey = FileKeyRevision4("", _ownerValue, PValue, fileId);
                    _userValue = UserValueRevision4(_fileKey, fileId);
                }

                Append(cipher == LegacyCipher.Aes128 ? "%PDF-1.6\n" : "%PDF-1.5\n");
            }

            /// <summary>
            /// The literal for a string inside indirect object (<paramref name="objectNumber"/>,
            /// <paramref name="generation"/>): <paramref name="plaintext"/> enciphered under that object's key, written
            /// as a hexadecimal string.
            /// </summary>
            public string EncryptedString(int objectNumber, int generation, string plaintext)
                => "<" + Hex(Encipher(objectNumber, generation, Encoding.ASCII.GetBytes(plaintext))) + ">";

            /// <summary>An ordinary indirect object. Any string in <paramref name="body"/> must already be enciphered.</summary>
            public void Object(int objectNumber, int generation, string body)
            {
                Entry(objectNumber, 1, _bytes.Count, generation);
                Append($"{objectNumber} {generation} obj\n{body}\nendobj\n");
            }

            /// <summary>A stream whose data is <paramref name="plaintextData"/> enciphered under the stream's identifier.</summary>
            public void Stream(int objectNumber, int generation, string plaintextData, string extraEntries = "")
            {
                byte[] data = Encipher(objectNumber, generation, Encoding.ASCII.GetBytes(plaintextData));
                string extra = extraEntries.Length > 0 ? " " + extraEntries : "";

                Entry(objectNumber, 1, _bytes.Count, generation);
                Append($"{objectNumber} {generation} obj\n<< /Length {data.Length}{extra} >>\nstream\n");
                _bytes.AddRange(data);
                Append("\nendstream\nendobj\n");
            }

            /// <summary>
            /// An object stream holding <paramref name="members"/> in order, each given a type-2 entry naming this stream
            /// and its own index.
            /// </summary>
            public void ObjectStream(int objectNumber, params (int ObjectNumber, string Body)[] members)
            {
                ObjectStreamWithoutEntries(objectNumber, "", members);
                for (int index = 0; index < members.Length; index++)
                    Compressed(members[index].ObjectNumber, objectNumber, index);
            }

            /// <summary>
            /// An object stream whose members get no cross-reference entries, for a caller stating them itself with
            /// <see cref="Compressed"/>, and which may carry <paramref name="extraEntries"/> in its dictionary.
            /// </summary>
            /// <remarks>
            /// Members are stored as plain object values — ISO 32000-2 §7.5.7: "strings occurring anywhere in an object
            /// stream shall not be separately encrypted" — and the stream is enciphered as a whole, under its own
            /// identifier at generation 0 ("The generation number of an object stream and of any compressed object
            /// shall be zero").
            /// </remarks>
            public void ObjectStreamWithoutEntries(int objectNumber, string extraEntries,
                params (int ObjectNumber, string Body)[] members)
                => WriteObjectStream(objectNumber, extraEntries, members.Length, members);

            /// <summary>
            /// An object stream whose <c>/N</c> is <paramref name="declaredN"/> rather than the truth — the shape
            /// <c>PdfDecryptor.ExtractObjectsFromStream</c> refuses on the count alone. Members get no entries.
            /// </summary>
            public void ObjectStreamDeclaring(int objectNumber, int declaredN, params (int ObjectNumber, string Body)[] members)
                => WriteObjectStream(objectNumber, "", declaredN, members);

            /// <summary>
            /// An object stream whose index is written exactly as given, before <paramref name="bodies"/> — for a
            /// malformed one, such as two members at one offset. Members get no entries; state them with
            /// <see cref="Compressed"/>.
            /// </summary>
            public void ObjectStreamWithIndex(int objectNumber, int declaredN, string index, string bodies)
            {
                byte[] data = Encipher(objectNumber, 0, Encoding.ASCII.GetBytes(index + bodies));
                Entry(objectNumber, 1, _bytes.Count, 0);
                Append($"{objectNumber} 0 obj\n<< /Type /ObjStm /N {declaredN} /First {index.Length} " +
                       $"/Length {data.Length} >>\nstream\n");
                _bytes.AddRange(data);
                Append("\nendstream\nendobj\n");
            }

            private void WriteObjectStream(int objectNumber, string extraEntries, int declaredN,
                (int ObjectNumber, string Body)[] members)
            {
                var index = new StringBuilder();
                var bodies = new StringBuilder();
                foreach (var (number, body) in members)
                {
                    index.Append(number).Append(' ').Append(bodies.Length).Append(' ');
                    bodies.Append(body).Append('\n');
                }

                byte[] data = Encipher(objectNumber, 0, Encoding.ASCII.GetBytes(index.ToString() + bodies));
                string extra = extraEntries.Length > 0 ? " " + extraEntries : "";

                Entry(objectNumber, 1, _bytes.Count, 0);
                Append($"{objectNumber} 0 obj\n<< /Type /ObjStm /N {declaredN} /First {index.Length}{extra} " +
                       $"/Length {data.Length} >>\nstream\n");
                _bytes.AddRange(data);
                Append("\nendstream\nendobj\n");
            }

            /// <summary>
            /// A type-2 entry: <paramref name="objectNumber"/> is member <paramref name="index"/> of object stream
            /// <paramref name="objectStreamNumber"/>.
            /// </summary>
            public void Compressed(int objectNumber, int objectStreamNumber, int index)
                => Entry(objectNumber, 2, objectStreamNumber, index);

            /// <summary>
            /// A type-0 entry: the object is deleted, and <paramref name="nextGeneration"/> is the generation a reuse of
            /// its number would take (§7.5.4).
            /// </summary>
            public void Free(int objectNumber, int nextGeneration) => Entry(objectNumber, 0, 0, nextGeneration);

            /// <summary>The <c>/Encrypt</c> dictionary, whose own strings are not encrypted (§7.6.2).</summary>
            public void EncryptDictionary(int objectNumber)
                => Object(objectNumber, 0, _cipher == LegacyCipher.Rc4With40BitKey
                    ? $"<< /Filter /Standard /V 1 /R 2 /O <{Hex(_ownerValue)}> /U <{Hex(_userValue)}> /P {PValue} >>"
                    : "<< /Filter /Standard /V 4 /R 4 /Length 128" +
                      " /CF << /StdCF << /CFM /AESV2 /AuthEvent /DocOpen /Length 16 >> >> /StmF /StdCF /StrF /StdCF" +
                      $" /O <{Hex(_ownerValue)}> /U <{Hex(_userValue)}> /P {PValue} >>");

            /// <summary>
            /// Closes the revision: a cross-reference stream holding this revision's entries and its own, carrying the
            /// trailer entries every added trailer repeats (§7.5.6) and, after the first revision, a <c>/Prev</c>.
            /// </summary>
            public void EndRevision(int xrefStreamObjectNumber, int rootObjectNumber, int encryptObjectNumber)
            {
                int xrefOffset = _bytes.Count;
                if (_previousXrefOffset < 0)
                    Entry(0, 0, 0, 0xFFFF);   // the head of the free list, in the first section only
                Entry(xrefStreamObjectNumber, 1, xrefOffset, 0);

                var subsections = new StringBuilder();
                var rows = new List<byte>();
                int runStart = 0, runLength = 0;
                foreach (var entry in _entries)
                {
                    if (runLength > 0 && entry.Key != runStart + runLength)
                    {
                        subsections.Append(runStart).Append(' ').Append(runLength).Append(' ');
                        runLength = 0;
                    }
                    if (runLength == 0) runStart = entry.Key;
                    runLength++;

                    rows.Add(entry.Value.Type);
                    for (int shift = 24; shift >= 0; shift -= 8)
                        rows.Add((byte)(entry.Value.Field2 >> shift));
                    rows.Add((byte)(entry.Value.Field3 >> 8));
                    rows.Add((byte)entry.Value.Field3);
                }
                subsections.Append(runStart).Append(' ').Append(runLength);

                string prev = _previousXrefOffset >= 0 ? $" /Prev {_previousXrefOffset}" : "";

                // A cross-reference stream is never encrypted (§7.5.8.2) and is written unfiltered, so the parser reads
                // the rows above verbatim.
                Append($"{xrefStreamObjectNumber} 0 obj\n<< /Type /XRef /Size {_highestObjectNumber + 1} /W [1 4 2]" +
                       $" /Index [{subsections}] /Root {rootObjectNumber} 0 R /Encrypt {encryptObjectNumber} 0 R" +
                       $" /ID [<{Hex(_fileId)}> <{Hex(_fileId)}>]{prev} /Length {rows.Count} >>\nstream\n");
                _bytes.AddRange(rows);
                Append($"\nendstream\nendobj\nstartxref\n{xrefOffset}\n%%EOF\n");

                _previousXrefOffset = xrefOffset;
                _entries.Clear();
            }

            /// <summary>The document as written so far.</summary>
            public byte[] ToArray() => _bytes.ToArray();

            private void Entry(int objectNumber, byte type, long field2, int field3)
            {
                _entries[objectNumber] = (type, field2, field3);
                _highestObjectNumber = Math.Max(_highestObjectNumber, objectNumber);
            }

            private void Append(string text) => _bytes.AddRange(Encoding.ASCII.GetBytes(text));

            private byte[] Encipher(int objectNumber, int generation, byte[] plaintext)
                => _cipher == LegacyCipher.Rc4With40BitKey
                    ? Rc4(ObjectKey(_fileKey, objectNumber, generation), plaintext)
                    : AesCbc(AesObjectKey(_fileKey, objectNumber, generation), plaintext, objectNumber, generation);
        }

        // ---------------------------------------------- ISO 32000-2 §7.6.3.2 and §7.6.4, revision 4, by hand

        /// <summary>
        /// Algorithm 2 for revision 4, steps (a)-(i): MD5 over the padded password, <c>/O</c>, <c>/P</c> low-order byte
        /// first and the file identifier, then fifty re-hashes of the first n bytes. Metadata is encrypted, so step (f)
        /// adds nothing.
        /// </summary>
        private static byte[] FileKeyRevision4(string userPassword, byte[] oValue, int pValue, byte[] fileId)
        {
            byte[] hash = Digest(Padded(userPassword), oValue, LowOrderBytesFirst(pValue), fileId);
            for (int i = 0; i < 50; i++)
                hash = Digest(First(hash, Revision4KeyBytes));
            return First(hash, Revision4KeyBytes);
        }

        /// <summary>
        /// Algorithm 3 for revision 4: RC4 the padded user password under the first n bytes of MD5(padded owner password)
        /// re-hashed fifty times, then nineteen further passes under that key XORed with the pass counter.
        /// </summary>
        private static byte[] OwnerValueRevision4(string ownerPassword, string userPassword)
        {
            byte[] hash = Digest(Padded(ownerPassword));
            for (int i = 0; i < 50; i++)
                hash = Digest(hash);

            byte[] key = First(hash, Revision4KeyBytes);
            byte[] value = Rc4(key, Padded(userPassword));
            for (int counter = 1; counter <= 19; counter++)
                value = Rc4(XorEach(key, counter), value);
            return value;
        }

        /// <summary>
        /// Algorithm 5 (revision 3 or 4): RC4 MD5(padding string || file identifier) under the file key, nineteen further
        /// passes, then 16 bytes of arbitrary padding.
        /// </summary>
        private static byte[] UserValueRevision4(byte[] fileKey, byte[] fileId)
        {
            byte[] value = Rc4(fileKey, Digest(PadString, fileId));
            for (int counter = 1; counter <= 19; counter++)
                value = Rc4(XorEach(fileKey, counter), value);

            var u = new byte[32];
            Array.Copy(value, u, value.Length);
            return u;
        }

        /// <summary>
        /// Algorithm 1 for AES: MD5(file key || low 3 bytes of the object number || low 2 of the generation || "sAlT"),
        /// first min(n + 5, 16) bytes.
        /// </summary>
        private static byte[] AesObjectKey(byte[] fileKey, int objectNumber, int generation)
        {
            byte[] suffix =
            {
                (byte)(objectNumber         & 0xFF),
                (byte)((objectNumber >>  8) & 0xFF),
                (byte)((objectNumber >> 16) & 0xFF),
                (byte)(generation         & 0xFF),
                (byte)((generation >>  8) & 0xFF),
                0x73, 0x41, 0x6C, 0x54,             // "sAlT"
            };
            return First(Digest(fileKey, suffix), Math.Min(fileKey.Length + 5, 16));
        }

        /// <summary>
        /// AES-CBC with PKCS#7 padding and the IV stored as the first 16 bytes (Algorithm 1 step (d)). The IV is derived
        /// from the identifier rather than drawn at random, so a fixture is the same bytes on every run.
        /// </summary>
        private static byte[] AesCbc(byte[] key, byte[] plaintext, int objectNumber, int generation)
        {
            byte[] iv = Digest(Encoding.ASCII.GetBytes($"iv {objectNumber} {generation}"));

            using var aes = Aes.Create();
            aes.Key = key;
            byte[] ciphertext = aes.EncryptCbc(plaintext, iv, PaddingMode.PKCS7);

            var result = new byte[iv.Length + ciphertext.Length];
            Array.Copy(iv, result, iv.Length);
            Array.Copy(ciphertext, 0, result, iv.Length, ciphertext.Length);
            return result;
        }

        private static byte[] LowOrderBytesFirst(int value) =>
            new[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

        private static byte[] XorEach(byte[] key, int counter)
        {
            var result = new byte[key.Length];
            for (int i = 0; i < key.Length; i++) result[i] = (byte)(key[i] ^ counter);
            return result;
        }
    }
}
