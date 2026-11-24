// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// A PostScript array — and, when the owning <see cref="PsObject"/> is executable, a procedure.
    ///
    /// <para><b>Storage is shared, not copied.</b> <c>getinterval</c> returns a subarray that
    /// <i>aliases</i> the parent (PLRM §3.3.2: "the result shares the same storage"), so writing
    /// through one must be visible through the other. Copying would be simpler and wrong.</para>
    /// </summary>
    internal sealed class PsArray
    {
        public readonly PsObject[] Items;
        public readonly int Offset;
        public readonly int Length;

        /// <summary>What <see cref="PsVm"/> knows of the storage; every view of it shares this.</summary>
        internal readonly PsArrayStore Store;

        public PsArray(PsObject[] items) : this(items, 0, items?.Length ?? 0) { }

        public PsArray(PsObject[] items, int offset, int length) : this(items, offset, length, new PsArrayStore()) { }

        private PsArray(PsObject[] items, int offset, int length, PsArrayStore store)
        {
            Items  = items ?? Array.Empty<PsObject>();
            Offset = offset;
            Length = length;
            Store  = store;
        }

        public PsObject this[int i]
        {
            get => Items[Offset + i];
            set
            {
                // A save's restore undoes this write (PLRM §3.7.3): the contents copied first.
                PsVm.Current?.BeforeWrite(Store, Items);
                Items[Offset + i] = value;
            }
        }

        /// <summary>
        /// An aliasing subarray, or null when the range is out of bounds.
        ///
        /// <para><b>The sum is widened to 64 bits, and that is the whole guard.</b> <c>index</c>
        /// comes straight out of the file through <c>PsObject.AsInt()</c>, which <i>saturates</i> at
        /// <c>int.MaxValue</c> rather than throwing — so <c>2147483647 1 getinterval</c> made
        /// <c>index + count</c> wrap to <c>int.MinValue</c>, which is not <c>&gt; Length</c>. The
        /// range check passed, the view carried an <c>Offset</c> far outside the backing array, and
        /// the CLR's own bounds check was all that was left: <c>IndexOutOfRangeException</c> is
        /// neither a <c>PsErrorException</c> nor a <c>PsBudgetExhaustedException</c>, so it escaped
        /// <c>PsInterpreter.Run</c> and cost the caller <b>every</b> mark the program had already
        /// emitted, where a conforming <c>rangecheck</c> costs one operator and is catchable by
        /// <c>stopped</c>.</para>
        /// </summary>
        public PsArray Interval(int index, int count)
            => index < 0 || count < 0 || (long)index + count > Length
                ? null
                : new PsArray(Items, Offset + index, count, Store);
    }

    /// <summary>
    /// A PostScript string: a mutable byte sequence, <b>not</b> text. Shares storage for the same
    /// reason <see cref="PsArray"/> does.
    /// </summary>
    internal sealed class PsString
    {
        public readonly byte[] Bytes;
        public readonly int Offset;
        public readonly int Length;

        public PsString(byte[] bytes) : this(bytes, 0, bytes?.Length ?? 0) { }

        public PsString(byte[] bytes, int offset, int length)
        {
            Bytes  = bytes ?? Array.Empty<byte>();
            Offset = offset;
            Length = length;
        }

        public byte this[int i]
        {
            get => Bytes[Offset + i];
            set => Bytes[Offset + i] = value;
        }

        /// <summary>
        /// An aliasing substring, or null when the range is out of bounds. 64-bit for the reason
        /// <see cref="PsArray.Interval"/> spells out.
        /// </summary>
        public PsString Interval(int index, int count)
            => index < 0 || count < 0 || (long)index + count > Length
                ? null
                : new PsString(Bytes, Offset + index, count);

        /// <summary>
        /// The bytes as Latin-1 text. PostScript strings are bytes, so this is only ever right for
        /// operator names and ASCII content — never for arbitrary <c>show</c> operands, which go to
        /// the emitter as bytes.
        /// </summary>
        public string AsLatin1() => Core.Text.Latin1Text.Decode(Bytes, Offset, Length);

        public byte[] ToArray()
        {
            var copy = new byte[Length];
            Array.Copy(Bytes, Offset, copy, 0, Length);
            return copy;
        }

        /// <summary><c>eq</c> compares strings by characters, not by identity (PLRM §3.3).</summary>
        public bool ContentEquals(PsString other)
        {
            if (other == null || other.Length != Length) return false;
            for (int i = 0; i < Length; i++)
                if (Bytes[Offset + i] != other.Bytes[other.Offset + i]) return false;
            return true;
        }

        /// <summary>Whether the bytes are <paramref name="name"/>'s characters, a name being Latin-1 text.</summary>
        public bool ContentEquals(string name)
        {
            if (name == null || name.Length != Length) return false;
            for (int i = 0; i < Length; i++)
                if (Bytes[Offset + i] != name[i]) return false;
            return true;
        }
    }

    /// <summary>
    /// A PostScript dictionary.
    ///
    /// <para>Keys are <see cref="PsObject"/>s, so <c>eq</c> semantics decide identity: names and
    /// strings match by characters, numbers across the integer/real divide, everything else by
    /// reference. Nearly every real key is a name, but a program may legally use an integer, and
    /// silently coercing keys to strings would break <c>known</c> for those.</para>
    /// </summary>
    internal sealed class PsDict
    {
        private Dictionary<PsObject, PsObject> _entries;

        /// <summary>The save level the dictionary was made at, <see cref="int.MaxValue"/> for global VM; see <see cref="PsVm"/>.</summary>
        internal int CreatedLevel = PsVm.CurrentLevel;

        /// <summary>The save level the dictionary's entries were last copied at; see <see cref="PsVm"/>.</summary>
        internal int JournaledLevel;

        /// <summary>Marks the dictionary as global VM, which no <c>restore</c> rolls back (<c>systemdict</c>, <c>globaldict</c>).</summary>
        internal void MarkGlobal() => CreatedLevel = int.MaxValue;

        /// <summary>
        /// The capacity the program asked <c>dict</c> for. PostScript Level 1 enforced it; Level 2
        /// dictionaries grow, so this is kept only because <c>maxlength</c> reports it.
        /// </summary>
        public int DeclaredCapacity { get; }

        public PsDict(int declaredCapacity = 0)
        {
            DeclaredCapacity = declaredCapacity;
            _entries = new Dictionary<PsObject, PsObject>();
        }

        public int Count => _entries.Count;

        /// <summary>A restore's copy of the entries, taken as they are: filling the live table from it would hash every key whole again.</summary>
        internal void RestoreEntries(Dictionary<PsObject, PsObject> saved) => _entries = saved;

        // A string key is its name (PsObject.AsKey), at every door, so it is one key with the name.
        public bool TryGet(PsObject key, out PsObject value) => _entries.TryGetValue(key.AsKey(), out value);
        public bool Contains(PsObject key)                   => _entries.ContainsKey(key.AsKey());
        public bool Remove(PsObject key)
        {
            PsVm.Current?.BeforeWrite(this, _entries);
            return _entries.Remove(key.AsKey());
        }

        public void Put(PsObject key, PsObject value)
        {
            PsVm.Current?.BeforeWrite(this, _entries);
            _entries[key.AsKey()] = value;
        }

        public IEnumerable<KeyValuePair<PsObject, PsObject>> Entries => _entries;

        // Convenience for the operator tables, which key on names constantly.
        public void PutName(string name, PsObject value) => Put(PsObject.LitName(name), value);
        public bool TryGetName(string name, out PsObject value)
            => TryGet(PsObject.LitName(name), out value);
    }

    /// <summary>
    /// A built-in operator: a name bound to the code that implements it.
    ///
    /// <para>The delegate is typed <see cref="object"/> rather than the interpreter so this file
    /// stays independent of the execution engine; the operator tables cast it back. Every
    /// construction site is inside the plugin, so the cast cannot fail from outside.</para>
    /// </summary>
    internal sealed class PsOperator
    {
        public readonly string Name;
        public readonly Action<object> Run;

        public PsOperator(string name, Action<object> run)
        {
            Name = name;
            Run  = run;
        }

        public override string ToString() => "--" + Name + "--";
    }
}
