// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Objects
{
    public class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<PdfName, PdfObject>>
    {
        // Keyed by the raw name string — avoids boxing a PdfName into every value slot.
        // PdfName objects are reconstructed from the string key only when enumerated.
        private readonly Dictionary<string, PdfObject> _entries;

        // The keys whose bytes in the file were not UTF-8 (PdfName.BytesNotUtf8), so a key rebuilt
        // from its string says so; null — the usual case — when there are none. Read only for a key
        // the dictionary holds, and every way a key is added writes it, so a removal leaves it be.
        private HashSet<string> _keysNotUtf8;

        public PdfDictionary() : this(4) { }

        // Parser-facing: supply the entry count so the backing dict never resizes.
        public PdfDictionary(int capacity)
        {
            _entries = new Dictionary<string, PdfObject>(capacity, StringComparer.Ordinal);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        public PdfObject this[PdfName key]
        {
            get
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                return _entries.TryGetValue(key.Value, out var v) ? v : null;
            }
            set => Set(key, value);
        }

        public int Count => _entries.Count;

        public ICollection<PdfName> Keys
        {
            get
            {
                var arr = new PdfName[_entries.Count];
                int i = 0;
                foreach (var k in _entries.Keys)
                    arr[i++] = KeyName(k);
                return arr;
            }
        }

        public void Set(PdfName key, PdfObject value)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            Set(key.Value, key.BytesNotUtf8, value);
        }

        /// <summary>
        /// <see cref="Set(PdfName, PdfObject)"/> from a key's string, for the parser: the dictionary
        /// keeps only the string, so a name built for each key read was garbage the moment it was stored.
        /// </summary>
        internal void Set(string key, bool keyBytesNotUtf8, PdfObject value)
        {
            _entries[key] = value;
            // A key set from a name read from non-UTF-8 bytes keeps saying so; set again from any
            // other name, it is that name's — the writer writes every name as UTF-8.
            if (keyBytesNotUtf8) (_keysNotUtf8 ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
            else _keysNotUtf8?.Remove(key);
        }

        /// <summary>The key <paramref name="key"/> as a name, saying whether its bytes were not UTF-8.</summary>
        private PdfName KeyName(string key)
            => _keysNotUtf8 != null && _keysNotUtf8.Contains(key) ? new PdfName(key, bytesNotUtf8: true) : new PdfName(key);

        private static void CopyKeyEncoding(PdfDictionary source, PdfDictionary clone)
        {
            if (source._keysNotUtf8 != null)
                clone._keysNotUtf8 = new HashSet<string>(source._keysNotUtf8, StringComparer.Ordinal);
        }

        /// <summary>
        /// A new dictionary holding the same entries. Values are shared <b>by reference</b>: only
        /// the container is new, which is what every caller in this repository wants — the writer
        /// expects object graphs to stay shared.
        ///
        /// <para><b>Why this lives here.</b> Fifteen private copies of this loop existed across
        /// five assemblies. They could not share <c>PdfHelpers.CloneDictionary</c> in Base because
        /// <c>Tetitulz.Pdf.Signing</c> may not reference Base; putting it on the type being cloned
        /// reaches every one of them and retires that exception.</para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
        /// <remarks>
        /// <para>The copies disagreed about a null source: fourteen threw
        /// <see cref="NullReferenceException"/> and <c>PdfHelpers</c> returned an empty dictionary.
        /// Both are wrong. An NRE loses the parameter name and is never a contract; an empty
        /// dictionary is worse, because it converts "you passed me nothing" into a valid-looking
        /// empty page or font dictionary that fails much later and somewhere else.</para>
        ///
        /// <para><b>A <see cref="PdfStream"/> source clones as a stream.</b> <c>PdfStream</c>
        /// derives from <c>PdfDictionary</c>, so passing one compiles — and copying only the
        /// entries would produce a dictionary carrying the stream's keys, a lying <c>/Length</c> and
        /// no data. <see cref="Writing.PdfWriter"/> would see a plain dictionary and write no
        /// <c>stream</c>/<c>endstream</c> at all, so an appearance stream or a Form XObject would
        /// vanish from the output with no exception and no diagnostic. <see cref="CloneValue"/>
        /// guards the nested case; this is the same guard at the entry point.</para>
        /// </remarks>
        public static PdfDictionary CloneShallow(PdfDictionary source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            PdfDictionary clone = NewLike(source);
            foreach (var kvp in source._entries)
                clone._entries[kvp.Key] = kvp.Value;
            CopyKeyEncoding(source, clone);
            return clone;
        }

        /// <summary>
        /// An empty container of the same kind as <paramref name="source"/>: a
        /// <see cref="PdfStream"/> for a stream — carrying its data and stream offset, shared by
        /// reference like every other value a clone copies — and a plain dictionary otherwise.
        /// </summary>
        private static PdfDictionary NewLike(PdfDictionary source)
        {
            // PdfStream must be tested first, for the reason CloneValue states: a stream IS a
            // dictionary here, so `is PdfDictionary` matches it too.
            if (source is PdfStream stream)
            {
                return new PdfStream
                {
                    RawData = stream.RawData,
                    StreamOffset = stream.StreamOffset,
                };
            }
            return new PdfDictionary();
        }

        /// <summary>
        /// A recursive clone: nested dictionaries and arrays are copied rather than shared, to
        /// <paramref name="maxDepth"/> levels. Beyond the limit the remaining values are shared,
        /// which bounds a self-referential graph instead of overflowing the stack.
        ///
        /// <para>A <see cref="PdfStream"/> source clones as a stream, for the reason
        /// <see cref="CloneShallow"/> records. Its data is shared by reference, exactly as
        /// <see cref="CloneValue"/> shares a nested stream.</para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <c>null</c>.</exception>
        public static PdfDictionary DeepClone(PdfDictionary source, int maxDepth = 64)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (maxDepth <= 0) return CloneShallow(source);

            PdfDictionary clone = NewLike(source);
            foreach (var kvp in source._entries)
                clone._entries[kvp.Key] = CloneValue(kvp.Value, maxDepth - 1);
            CopyKeyEncoding(source, clone);
            return clone;
        }

        private static PdfObject CloneValue(PdfObject value, int depth)
        {
            if (depth <= 0) return value;
            switch (value)
            {
                // PdfStream DERIVES from PdfDictionary, so it must be tested first: cloning one
                // as a plain dictionary would produce a dictionary without its data.
                case PdfStream: return value;      // streams carry data; sharing is intended
                case PdfDictionary d: return DeepClone(d, depth);
                case PdfArray a:
                {
                    var copy = new PdfArray();
                    foreach (var item in a) copy.Add(CloneValue(item, depth - 1));
                    return copy;
                }
                default: return value;             // scalars are immutable
            }
        }

        public bool ContainsKey(PdfName key)
        {
            if (key == null) return false;
            return _entries.ContainsKey(key.Value);
        }

        public bool Remove(PdfName key)
        {
            if (key == null) return false;
            return _entries.Remove(key.Value);
        }

        // ── Typed convenience getters ─────────────────────────────────────────────
        // Use TryGetValue directly — avoids the null-guard in the indexer and the
        // pattern-match going through a second virtual dispatch.

        public PdfObject Get(PdfName key) => this[key];

        public PdfName GetName(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) ? v as PdfName : null;

        public int? GetInteger(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) && v is PdfNumber n ? n.IntValue : null;

        public long? GetLong(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) && v is PdfNumber n ? n.LongValue : null;

        public double? GetDouble(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) && v is PdfNumber n ? n.DoubleValue : null;

        public PdfString GetString(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) ? v as PdfString : null;

        public PdfArray GetArray(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) ? v as PdfArray : null;

        public PdfDictionary GetDictionary(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) ? v as PdfDictionary : null;

        public PdfReference GetReference(PdfName key)
            => _entries.TryGetValue(key.Value, out var v) ? v as PdfReference : null;

        public PdfName GetTypeName() => GetName(PdfName.Type);

        // ── Internal zero-allocation API ──────────────────────────────────────────
        // These let Core/Base code call dict["SomeName"] without allocating a PdfName.
        // Only accessible to InternalsVisibleTo assemblies (Base, tests).

        internal PdfObject this[string key]
        {
            get => key != null && _entries.TryGetValue(key, out var v) ? v : null;
            set
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                _entries[key] = value;
                _keysNotUtf8?.Remove(key);
            }
        }

        internal bool TryGetValue(string key, out PdfObject value)
            => _entries.TryGetValue(key, out value);

        internal bool ContainsKey(string key)
            => key != null && _entries.ContainsKey(key);

        internal bool Remove(string key)
            => key != null && _entries.Remove(key);

        // ── Enumeration ───────────────────────────────────────────────────────────

        public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator()
        {
            foreach (var entry in _entries)
                yield return new KeyValuePair<PdfName, PdfObject>(KeyName(entry.Key), entry.Value);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString()
        {
            var typeName = GetTypeName();
            return typeName != null
                ? $"<< /Type /{typeName.Value} ... >>"
                : $"<< {Count} entries >>";
        }
    }
}
