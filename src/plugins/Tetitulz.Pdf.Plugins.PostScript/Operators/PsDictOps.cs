// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Dictionary operators (PLRM §8.5).
    ///
    /// <para>This family is what makes a real EPS run at all: a prologue is two hundred
    /// <c>/name {…} def</c> lines, and every one of them is a <c>def</c> into the current
    /// dictionary followed by name lookups down the dictionary stack.</para>
    /// </summary>
    internal static class PsDictOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("dict", Dict);
            op("begin", Begin);
            op("end", i => i.Dicts.End());
            op("def", Def);
            op("load", Load);
            op("store", Store);
            op("where", Where);
            op("known", Known);
            op("undef", Undef);
            op("currentdict", i => i.Operands.Push(PsObject.Dict(i.Dicts.Current)));
            op("countdictstack", i => i.Operands.Push(PsObject.Int(i.Dicts.Count)));

            // `cleardictstack` pops back to the permanent dictionaries. TruncateTo already refuses
            // to go below the bottom three, so this cannot unseat systemdict.
            op("cleardictstack", i => i.Dicts.TruncateTo(3));

            op("dictstack", DictStack);
            op("maxlength", MaxLength);

            // '<<' is the mark operator under another name; '>>' collects to it.
            op("<<", i => i.Operands.Push(PsObject.Mark));
            op(">>", EndDict);
        }

        private static void Dict(PsInterpreter i)
        {
            int capacity = i.Operands.PopInt("dict");
            if (capacity < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "dict");

            // Charge the DECLARED capacity, not the current size: `1000000000 dict` must be refused
            // before it can be filled, and charging on growth would let a loop of large empty dicts
            // through.
            i.ChargeAllocation((long)capacity * PsAllocationBudget.BytesPerDictEntry, "dict");
            i.Operands.Push(PsObject.Dict(new PsDict(capacity)));
        }

        private static void Begin(PsInterpreter i)
        {
            var d = i.Operands.PopDict("begin");
            i.Dicts.Begin(d);
        }

        private static void Def(PsInterpreter i)
        {
            var value = i.Operands.Pop();
            var key   = i.Operands.Pop();
            if (key.Type == PsType.Null) throw new PsErrorException(PsErrorKind.TypeCheck, "def");

            i.TickKey(key);
            i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "def");
            i.Dicts.Current.Put(key, value);
        }

        /// <summary>
        /// <c>key load value</c> — the value bound to a key anywhere on the dictionary stack.
        /// Unlike executing the name, this pushes the value <b>without</b> running it, which is how
        /// a program gets hold of a procedure to pass around.
        /// </summary>
        private static void Load(PsInterpreter i)
        {
            var key = Literal(i.Operands.Pop());
            i.TickKey(key, i.Dicts.Count);
            if (!i.Dicts.TryLookup(key, out var value))
                throw new PsErrorException(PsErrorKind.Undefined, "load",
                    key.Type == PsType.Name ? key.AsName() : null);
            i.Operands.Push(value);
        }

        /// <summary>
        /// <c>key value store</c> — replaces the definition wherever it already exists on the
        /// dictionary stack, falling back to <c>def</c> semantics when it exists nowhere.
        /// </summary>
        private static void Store(PsInterpreter i)
        {
            var value = i.Operands.Pop();
            var key   = Literal(i.Operands.Pop());
            i.TickKey(key, i.Dicts.Count);

            var owner = i.Dicts.Where(key);
            if (owner != null) { owner.Put(key, value); return; }

            i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "store");
            i.Dicts.Current.Put(key, value);
        }

        /// <summary>
        /// <c>key where</c> → <c>dict true</c> or <c>false</c>. This is the standard feature probe,
        /// so getting it wrong makes prologues take the wrong branch.
        /// </summary>
        private static void Where(PsInterpreter i)
        {
            var key = Literal(i.Operands.Pop());
            i.TickKey(key, i.Dicts.Count);
            var owner = i.Dicts.Where(key);

            if (owner == null) { i.Operands.Push(PsObject.False); return; }
            i.Operands.Push(PsObject.Dict(owner));
            i.Operands.Push(PsObject.True);
        }

        private static void Known(PsInterpreter i)
        {
            var key = Literal(i.Operands.Pop());
            var d   = i.Operands.PopDict("known");
            i.TickKey(key);
            i.Operands.Push(PsObject.Bool(d.Contains(key)));
        }

        private static void Undef(PsInterpreter i)
        {
            var key = Literal(i.Operands.Pop());
            var d   = i.Operands.PopDict("undef");
            i.TickKey(key);
            d.Remove(key);
        }

        private static void MaxLength(PsInterpreter i)
        {
            var d = i.Operands.PopDict("maxlength");
            // Level 2 dictionaries grow, so the useful answer is at least the current size.
            i.Operands.Push(PsObject.Int(d.DeclaredCapacity > d.Count ? d.DeclaredCapacity : d.Count));
        }

        /// <summary>
        /// <c>&gt;&gt;</c> — collect <c>key value …</c> pairs back to the mark into a dictionary.
        /// </summary>
        private static void EndDict(PsInterpreter i)
        {
            int mark = PsStackOps.FindMark(i);
            if (mark < 0) throw new PsErrorException(PsErrorKind.UnmatchedMark, ">>");
            if ((mark % 2) != 0)
                throw new PsErrorException(PsErrorKind.RangeCheck, ">>",
                    "an odd number of objects between << and >>");

            int pairs = mark / 2;
            i.ChargeAllocation((long)pairs * PsAllocationBudget.BytesPerDictEntry, ">>");
            i.Tick(pairs);

            var items = new PsObject[mark];
            for (int k = mark - 1; k >= 0; k--) items[k] = i.Operands.Pop();
            i.Operands.Pop();   // the mark

            var d = new PsDict(pairs);
            for (int k = 0; k < mark; k += 2)
            {
                i.TickKey(items[k]);
                d.Put(items[k], items[k + 1]);
            }
            i.Operands.Push(PsObject.Dict(d));
        }

        /// <summary>
        /// <c>array dictstack subarray</c> — stores the dictionary stack bottom-first into the
        /// caller's array and pushes the populated prefix, which shares its storage.
        /// </summary>
        private static void DictStack(PsInterpreter i)
        {
            var target = i.Operands.PopArray("dictstack");
            if (target.Length < i.Dicts.Count)
                throw new PsErrorException(PsErrorKind.RangeCheck, "dictstack",
                    $"the {target.Length}-element array cannot hold {i.Dicts.Count} dictionaries");

            i.Tick(i.Dicts.Count);
            for (int k = 0; k < i.Dicts.Count; k++) target[k] = PsObject.Dict(i.Dicts[k]);
            i.Operands.Push(PsObject.Arr(target.Interval(0, i.Dicts.Count)));
        }

        /// <summary>
        /// A key is compared literally. An executable name reaching here — from <c>/foo load</c>
        /// written as <c>foo load</c> — must still match the literal it was defined under.
        /// </summary>
        private static PsObject Literal(PsObject key) =>
            key.Type == PsType.Name && key.IsExecutable ? key.WithExecutable(false) : key;
    }
}
