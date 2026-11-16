// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// <c>save</c> and <c>restore</c>'s half that is VM (PLRM §3.7.3): every change to an array's or a
    /// dictionary's contents made since a <c>save</c> is undone by its <c>restore</c>. As interpreters
    /// do it: the first change to an object after a save copies what it held, and
    /// the restore puts the copies back, latest first; an object made since the save is not copied,
    /// since nothing older can reach it once the restore has undone what reached it. String contents
    /// are not restored — PLRM's exception, kept here: only reference slots are journalled.
    /// <c>systemdict</c> and <c>globaldict</c> are global VM, which no restore touches.
    ///
    /// <para><b>One per conversion, on its thread.</b> An array's or a dictionary's write cannot see
    /// the interpreter, and a pattern's or a glyph's child interpreter shares its parent's VM, so the
    /// conversion installs its VM as <see cref="Current"/> and every write asks it. Each copy is
    /// charged to the conversion's allocation budget at the price of making what it copies (an
    /// array's elements, a dictionary's entries); one that does not fit is a <c>VMerror</c>.</para>
    /// </summary>
    internal sealed class PsVm
    {
        [ThreadStatic] private static PsVm _current;

        /// <summary>The VM the running conversion's writes are journaled in; null outside one.</summary>
        public static PsVm Current { get => _current; set => _current = value; }

        /// <summary>How many saves are open — the level an object made now is stamped with.</summary>
        public static int CurrentLevel => _current?._levels.Count ?? 0;

        private readonly PsAllocationBudget _allocation;
        private readonly List<SaveLevel> _levels = new();
        private int _nextId = 1;

        public PsVm(PsAllocationBudget allocation) => _allocation = allocation;

        private sealed class SaveLevel
        {
            public int Id;
            public int GraphicsSave;
            public readonly List<Action> Undo = new();
        }

        /// <summary>
        /// A save: a new level, its id the save object's, <paramref name="graphicsSave"/> the engine's own.
        ///
        /// <para><b>Capped and charged here, whatever the engine.</b> The graphics engine caps its own
        /// stack at <see cref="PdfLimits.MaxGraphicsStateStackDepth"/> and <c>save</c> charges a
        /// graphics state only when an engine is attached; a tint transform runs in a child
        /// interpreter with no engine, which shares this VM, so its saves reached neither. A level is
        /// <c>limitcheck</c> past the same depth, and its bytes are charged to the allocation budget
        /// (<c>VMerror</c> when they do not fit).</para>
        /// </summary>
        public int Save(int graphicsSave)
        {
            if (_levels.Count >= PdfLimits.MaxGraphicsStateStackDepth)
                throw new PsErrorException(PsErrorKind.LimitCheck, "save",
                    $"more than {PdfLimits.MaxGraphicsStateStackDepth} saves are open");
            if (!_allocation.TryCharge(PsAllocationBudget.BytesPerSaveLevel))
                throw new PsErrorException(PsErrorKind.VmError, "save", "the save level exceeds the allocation budget");
            var level = new SaveLevel { Id = _nextId++, GraphicsSave = graphicsSave };
            _levels.Add(level);
            return level.Id;
        }

        /// <summary>How many levels the last <see cref="Restore"/> examined to find its save: the evidence that the search is logarithmic.</summary>
        internal int LastRestoreComparisons { get; private set; }

        /// <summary>
        /// The restore of save <paramref name="id"/>: every level from the newest down to it undone, newest
        /// change first; the engine's save it carried in <paramref name="graphicsSave"/>. False when the
        /// id names no open save (<c>invalidrestore</c>).
        ///
        /// <para>The ids ascend along <c>_levels</c> (a save appends the next id, a restore removes a
        /// tail), so the level is found by binary search: a stale save object restored in a
        /// <c>stopped</c> loop costs one tick, and a scan of every open level per tick would let a
        /// program multiply the two.</para>
        /// </summary>
        public bool Restore(int id, out int graphicsSave)
        {
            graphicsSave = 0;
            int index = -1, comparisons = 0;
            for (int lo = 0, hi = _levels.Count - 1; lo <= hi;)
            {
                int mid = lo + ((hi - lo) >> 1);
                comparisons++;
                int at = _levels[mid].Id;
                if (at == id) { index = mid; break; }
                if (at < id) lo = mid + 1; else hi = mid - 1;
            }
            LastRestoreComparisons = comparisons;
            if (index < 0) return false;
            graphicsSave = _levels[index].GraphicsSave;
            for (int k = _levels.Count - 1; k >= index; k--)
            {
                List<Action> undo = _levels[k].Undo;
                for (int u = undo.Count - 1; u >= 0; u--) undo[u]();
                _levels.RemoveAt(k);
            }
            return true;
        }

        /// <summary>
        /// Before a write into <paramref name="store"/>'s array: its contents copied once a level, unless
        /// it was made at this level (or is global).
        /// </summary>
        internal void BeforeWrite(PsArrayStore store, PsObject[] items)
        {
            int level = _levels.Count;
            if (level == 0 || store.CreatedLevel >= level || store.JournaledLevel >= level) return;
            Charge(items.Length * (long)PsAllocationBudget.BytesPerArrayElement);
            var copy = (PsObject[])items.Clone();
            int previous = store.JournaledLevel;
            store.JournaledLevel = level;
            _levels[level - 1].Undo.Add(() =>
            {
                Array.Copy(copy, items, copy.Length);
                store.JournaledLevel = previous;
            });
        }

        /// <summary>Before a change to <paramref name="dict"/>'s entries: they are copied once a level, unless it was made at this level or is global.</summary>
        internal void BeforeWrite(PsDict dict, Dictionary<PsObject, PsObject> entries)
        {
            int level = _levels.Count;
            if (level == 0 || dict.CreatedLevel >= level || dict.JournaledLevel >= level) return;
            Charge((entries.Count + 1) * (long)PsAllocationBudget.BytesPerDictEntry);
            var copy = new Dictionary<PsObject, PsObject>(entries);
            int previous = dict.JournaledLevel;
            dict.JournaledLevel = level;
            _levels[level - 1].Undo.Add(() =>
            {
                dict.RestoreEntries(copy);
                dict.JournaledLevel = previous;
            });
        }

        private void Charge(long bytes)
        {
            if (!_allocation.TryCharge(bytes))
                throw new PsErrorException(PsErrorKind.VmError, "save", "the copies restore needs exceed the allocation budget");
        }
    }

    /// <summary>
    /// What an array's storage carries for <see cref="PsVm"/>: the save level it was made at and the
    /// level its contents were last copied at. Shared by every view of the storage, as the storage is.
    /// </summary>
    internal sealed class PsArrayStore
    {
        public int CreatedLevel = PsVm.CurrentLevel;
        public int JournaledLevel;
    }
}
