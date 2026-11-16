// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Array, string and packed-array operators (PLRM §8.6), plus the shared
    /// <c>get</c>/<c>put</c>/<c>length</c>/<c>getinterval</c>/<c>putinterval</c> family that works
    /// across arrays, strings and dictionaries.
    /// </summary>
    internal static class PsArrayStringOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("array", Array);
            op("string", String);
            op("packedarray", PackedArray);
            op("]", EndArray);
            op("astore", AStore);
            op("aload", ALoad);

            op("length", Length);
            op("get", Get);
            op("put", Put);
            op("getinterval", GetInterval);
            op("putinterval", PutInterval);

            op("search", Search);
            op("anchorsearch", AnchorSearch);
        }

        // ------------------------------------------------------------------
        //  Construction
        // ------------------------------------------------------------------

        private static void Array(PsInterpreter i)
        {
            int n = i.Operands.PopInt("array");
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "array");
            if (n > PsLimits.MaxSingleArrayLength)
                throw new PsErrorException(PsErrorKind.LimitCheck, "array",
                    $"length {n} exceeds {PsLimits.MaxSingleArrayLength}");

            // Charged BEFORE allocating — the whole point of the cap is that
            // `1000000000 array` never reaches `new PsObject[n]`.
            i.ChargeAllocation((long)n * PsAllocationBudget.BytesPerArrayElement, "array");

            var items = new PsObject[n];
            for (int k = 0; k < n; k++) items[k] = PsObject.Null;
            i.Operands.Push(PsObject.Arr(new PsArray(items)));
        }

        private static void String(PsInterpreter i)
        {
            int n = i.Operands.PopInt("string");
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "string");
            if (n > PsLimits.MaxSingleStringLength)
                throw new PsErrorException(PsErrorKind.LimitCheck, "string",
                    $"length {n} exceeds {PsLimits.MaxSingleStringLength}");

            i.ChargeAllocation(n, "string");
            i.Operands.Push(PsObject.Str(new PsString(new byte[n])));
        }

        /// <summary>
        /// <c>packedarray</c> builds an ordinary array here. Packed arrays exist to save VM in a
        /// Level 2 printer; they are read-only and otherwise indistinguishable, and this
        /// interpreter has no VM pressure to relieve.
        /// </summary>
        private static void PackedArray(PsInterpreter i)
        {
            int n = i.Operands.PopInt("packedarray");
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "packedarray");
            if (n > i.Operands.Count) throw new PsErrorException(PsErrorKind.StackUnderflow, "packedarray");

            i.ChargeAllocation((long)n * PsAllocationBudget.BytesPerArrayElement, "packedarray");
            i.Tick(n);

            var items = new PsObject[n];
            for (int k = n - 1; k >= 0; k--) items[k] = i.Operands.Pop();
            i.Operands.Push(PsObject.Arr(new PsArray(items)));
        }

        /// <summary><c>]</c> — collect everything back to the mark into an array.</summary>
        private static void EndArray(PsInterpreter i)
        {
            int mark = PsStackOps.FindMark(i);
            if (mark < 0) throw new PsErrorException(PsErrorKind.UnmatchedMark, "]");

            i.ChargeAllocation((long)mark * PsAllocationBudget.BytesPerArrayElement, "]");
            i.Tick(mark);

            var items = new PsObject[mark];
            for (int k = mark - 1; k >= 0; k--) items[k] = i.Operands.Pop();
            i.Operands.Pop();   // the mark
            i.Operands.Push(PsObject.Arr(new PsArray(items)));
        }

        private static void AStore(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("astore");
            if (arr.Length > i.Operands.Count)
                throw new PsErrorException(PsErrorKind.StackUnderflow, "astore");

            i.Tick(arr.Length);
            for (int k = arr.Length - 1; k >= 0; k--) arr[k] = i.Operands.Pop();
            i.Operands.Push(PsObject.Arr(arr));
        }

        private static void ALoad(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("aload");
            i.Tick(arr.Length);
            for (int k = 0; k < arr.Length; k++) i.Operands.Push(arr[k]);
            i.Operands.Push(PsObject.Arr(arr));
        }

        // ------------------------------------------------------------------
        //  Shared accessors
        // ------------------------------------------------------------------

        private static void Length(PsInterpreter i)
        {
            var o = i.Operands.Pop();
            int n = o.Type switch
            {
                PsType.Array      => o.AsArray().Length,
                PsType.String     => o.AsString().Length,
                PsType.Dictionary => o.AsDict().Count,
                PsType.Name       => o.AsName().Length,
                _ => throw new PsErrorException(PsErrorKind.TypeCheck, "length")
            };
            i.Operands.Push(PsObject.Int(n));
        }

        private static void Get(PsInterpreter i)
        {
            var index = i.Operands.Pop();
            var target = i.Operands.Pop();

            switch (target.Type)
            {
                case PsType.Array:
                {
                    int k = IntIndex(index, "get");
                    var a = target.AsArray();
                    if (k < 0 || k >= a.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "get");
                    i.Operands.Push(a[k]);
                    return;
                }
                case PsType.String:
                {
                    int k = IntIndex(index, "get");
                    var s = target.AsString();
                    if (k < 0 || k >= s.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "get");
                    i.Operands.Push(PsObject.Int(s[k]));
                    return;
                }
                case PsType.Dictionary:
                {
                    var key = index.Type == PsType.Name && index.IsExecutable
                        ? index.WithExecutable(false) : index;
                    i.TickKey(key);
                    if (!target.AsDict().TryGet(key, out var v))
                        throw new PsErrorException(PsErrorKind.Undefined, "get");
                    i.Operands.Push(v);
                    return;
                }
                default:
                    throw new PsErrorException(PsErrorKind.TypeCheck, "get");
            }
        }

        private static void Put(PsInterpreter i)
        {
            var value  = i.Operands.Pop();
            var index  = i.Operands.Pop();
            var target = i.Operands.Pop();

            switch (target.Type)
            {
                case PsType.Array:
                {
                    int k = IntIndex(index, "put");
                    var a = target.AsArray();
                    if (k < 0 || k >= a.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "put");
                    a[k] = value;
                    return;
                }
                case PsType.String:
                {
                    int k = IntIndex(index, "put");
                    var s = target.AsString();
                    if (k < 0 || k >= s.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "put");
                    if (value.Type != PsType.Integer) throw new PsErrorException(PsErrorKind.TypeCheck, "put");

                    // PLRM: the value is an integer in 0..255, rangecheck otherwise. Masking to a
                    // byte stored 0 for 256 and told a program probing the bound with `stopped`
                    // that the store had succeeded.
                    int v = value.AsInt();
                    if (v < 0 || v > 255)
                        throw new PsErrorException(PsErrorKind.RangeCheck, "put",
                            $"{v} is not a byte value");
                    s[k] = (byte)v;
                    return;
                }
                case PsType.Dictionary:
                {
                    var key = index.Type == PsType.Name && index.IsExecutable
                        ? index.WithExecutable(false) : index;
                    i.TickKey(key);
                    i.ChargeAllocation(PsAllocationBudget.BytesPerDictEntry, "put");
                    target.AsDict().Put(key, value);
                    return;
                }
                default:
                    throw new PsErrorException(PsErrorKind.TypeCheck, "put");
            }
        }

        /// <summary>
        /// <c>getinterval</c> returns a view that <b>shares storage</b> with its parent (PLRM
        /// §3.3.2), so a later <c>put</c> through either is visible through the other. Copying would
        /// be simpler and would silently break programs that rely on the aliasing.
        /// </summary>
        private static void GetInterval(PsInterpreter i)
        {
            int count = i.Operands.PopInt("getinterval");
            int index = i.Operands.PopInt("getinterval");
            var target = i.Operands.Pop();

            switch (target.Type)
            {
                case PsType.Array:
                {
                    var view = target.AsArray().Interval(index, count);
                    if (view == null) throw new PsErrorException(PsErrorKind.RangeCheck, "getinterval");
                    i.Operands.Push(PsObject.Arr(view));
                    return;
                }
                case PsType.String:
                {
                    var view = target.AsString().Interval(index, count);
                    if (view == null) throw new PsErrorException(PsErrorKind.RangeCheck, "getinterval");
                    i.Operands.Push(PsObject.Str(view));
                    return;
                }
                default:
                    throw new PsErrorException(PsErrorKind.TypeCheck, "getinterval");
            }
        }

        private static void PutInterval(PsInterpreter i)
        {
            var source = i.Operands.Pop();
            int index  = i.Operands.PopInt("putinterval");
            var target = i.Operands.Pop();

            if (target.Type == PsType.Array && source.Type == PsType.Array)
            {
                var dst = target.AsArray();
                var src = source.AsArray();

                // 64-bit for the same reason PsArray.Interval is: `index` saturates at int.MaxValue
                // rather than throwing, so a 32-bit sum wraps negative and passes a check it should
                // fail, leaving the CLR's bounds check — an exception this interpreter does not
                // catch — as the only thing between a hostile file and the whole conversion.
                if (index < 0 || (long)index + src.Length > dst.Length)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "putinterval");

                i.Tick(src.Length);
                for (int k = 0; k < src.Length; k++) dst[index + k] = src[k];
                return;
            }

            if (target.Type == PsType.String && source.Type == PsType.String)
            {
                var dst = target.AsString();
                var src = source.AsString();
                if (index < 0 || (long)index + src.Length > dst.Length)
                    throw new PsErrorException(PsErrorKind.RangeCheck, "putinterval");

                i.Tick(src.Length);
                for (int k = 0; k < src.Length; k++) dst[index + k] = src[k];
                return;
            }

            throw new PsErrorException(PsErrorKind.TypeCheck, "putinterval");
        }

        /// <summary>
        /// The composite form of <c>copy</c>: <c>src dst copy</c> copies the elements of src into
        /// the front of dst and pushes the populated prefix of dst.
        /// </summary>
        internal static void CopyComposite(PsInterpreter i)
        {
            var dst = i.Operands.Pop();
            var src = i.Operands.Pop();

            if (src.Type == PsType.Array && dst.Type == PsType.Array)
            {
                var s = src.AsArray();
                var d = dst.AsArray();
                if (s.Length > d.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "copy");

                i.Tick(s.Length);
                for (int k = 0; k < s.Length; k++) d[k] = s[k];
                i.Operands.Push(PsObject.Arr(d.Interval(0, s.Length)));
                return;
            }

            if (src.Type == PsType.String && dst.Type == PsType.String)
            {
                var s = src.AsString();
                var d = dst.AsString();
                if (s.Length > d.Length) throw new PsErrorException(PsErrorKind.RangeCheck, "copy");

                i.Tick(s.Length);
                for (int k = 0; k < s.Length; k++) d[k] = s[k];
                i.Operands.Push(PsObject.Str(d.Interval(0, s.Length)));
                return;
            }

            if (src.Type == PsType.Dictionary && dst.Type == PsType.Dictionary)
            {
                var s = src.AsDict();
                var d = dst.AsDict();
                i.Tick(s.Count);
                i.ChargeAllocation((long)s.Count * PsAllocationBudget.BytesPerDictEntry, "copy");
                foreach (var kv in s.Entries)
                {
                    i.TickKey(kv.Key);
                    d.Put(kv.Key, kv.Value);
                }
                i.Operands.Push(PsObject.Dict(d));
                return;
            }

            throw new PsErrorException(PsErrorKind.TypeCheck, "copy");
        }

        // ------------------------------------------------------------------
        //  Search
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>string seek search</c> → <c>post match pre true</c> on success, <c>string false</c>
        /// otherwise. All three results share the original's storage.
        /// </summary>
        private static void Search(PsInterpreter i)
        {
            var seek   = i.Operands.PopString("search");
            var target = i.Operands.PopString("search");

            // Charged for the WORK, not for the call. IndexOf is a naive nested scan, so this is
            // O(n·m) inside a single operator — 72 bytes of PostScript could buy ~2.5e11 byte
            // comparisons while the budget was charged one tick, and the step loop never regained
            // control. Every other bulk operator here (copy, roll, astore, putinterval) already
            // ticks its count; these two were the omission.
            i.Tick((long)target.Length * Math.Max(1, seek.Length));

            int at = IndexOf(target, seek, 0);
            if (at < 0)
            {
                i.Operands.Push(PsObject.Str(target));
                i.Operands.Push(PsObject.False);
                return;
            }

            i.Operands.Push(PsObject.Str(target.Interval(at + seek.Length, target.Length - at - seek.Length)));
            i.Operands.Push(PsObject.Str(target.Interval(at, seek.Length)));
            i.Operands.Push(PsObject.Str(target.Interval(0, at)));
            i.Operands.Push(PsObject.True);
        }

        private static void AnchorSearch(PsInterpreter i)
        {
            var seek   = i.Operands.PopString("anchorsearch");
            var target = i.Operands.PopString("anchorsearch");

            // A prefix test, not a search. `IndexOf(...) == 0` scanned the WHOLE haystack looking
            // for a match it would then reject unless it sat at 0 — O(n·m) to answer an O(m)
            // question.
            i.Tick(seek.Length);
            bool matches = StartsWith(target, seek);
            if (!matches)
            {
                i.Operands.Push(PsObject.Str(target));
                i.Operands.Push(PsObject.False);
                return;
            }

            i.Operands.Push(PsObject.Str(target.Interval(seek.Length, target.Length - seek.Length)));
            i.Operands.Push(PsObject.Str(target.Interval(0, seek.Length)));
            i.Operands.Push(PsObject.True);
        }

        private static bool StartsWith(PsString target, PsString prefix)
        {
            if (prefix.Length > target.Length) return false;
            for (int k = 0; k < prefix.Length; k++)
                if (target[k] != prefix[k]) return false;
            return true;
        }

        private static int IndexOf(PsString haystack, PsString needle, int from)
        {
            if (needle.Length == 0) return from <= haystack.Length ? from : -1;

            for (int start = from; start + needle.Length <= haystack.Length; start++)
            {
                int k = 0;
                while (k < needle.Length && haystack[start + k] == needle[k]) k++;
                if (k == needle.Length) return start;
            }
            return -1;
        }

        private static int IntIndex(PsObject o, string op)
        {
            if (o.Type != PsType.Integer) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsInt();
        }
    }
}
