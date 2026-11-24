// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Functions
{
    /// <summary>
    /// Evaluates PDF function dictionaries (types 0, 2, 3, 4).
    /// PDF Reference 1.7 §3.9.
    /// </summary>
    internal static class PdfFunctionEvaluator
    {
        /// <summary>
        /// Names of any Type 4 (PostScript) operators that the evaluator did not
        /// recognise during the most recent <see cref="Evaluate(PdfDictionary,float[])"/> call.  Reset
        /// at the start of every call.  Useful for diagnosing wrong-output PDFs
        /// without having to step through with a debugger.  An unknown operator is evaluated as the
        /// number 0, as other readers read one.  A <c>}</c> that closes nothing is listed as
        /// <c>}</c>, and skipped.
        /// </summary>
        [ThreadStatic]
        private static List<string> _unknownPsOps;

        /// <summary>
        /// The names in <see cref="_unknownPsOps"/>, which keeps them distinct: a search of the list
        /// on each unknown operator executed made one cost the number of distinct unknowns, so a
        /// program of four thousand in a loop spent six seconds reaching its two-million-operation
        /// budget — a unit the per-page budget prices as constant.
        /// </summary>
        [ThreadStatic]
        private static HashSet<string> _unknownPsOpSet;

        /// <summary>How many distinct unknown operators the set holds — every one, while it is the set that decides.</summary>
        internal static int UnknownPostScriptOpsSeen => _unknownPsOpSet?.Count ?? 0;
        public static IReadOnlyList<string> LastUnknownPostScriptOps =>
            (IReadOnlyList<string>)_unknownPsOps ?? Array.Empty<string>();

        /// <summary>
        /// Reason the most recent Type 0 (sampled) function evaluation produced a sentinel
        /// <c>[0f]</c> output <b>or fell back to a lower-fidelity path</b>, or <c>null</c> if it
        /// evaluated exactly. Read it on the thread that called <see cref="Evaluate(PdfDictionary,
        /// float[])"/>, after the call.
        ///
        /// <para>Cleared at the start of every top-level <see cref="Evaluate(PdfDictionary,
        /// float[])"/> — not only where a Type 0 function writes it. A slot that is written on one
        /// code path and read after any call must be reset on <i>every</i> path, or the previous
        /// document's reason is still sitting in it (the mistake
        /// <c>PdfDecryptor.LastPermsValidation</c> made). Nested evaluation, such as a Type 3
        /// stitching function's sub-functions, does <i>not</i> clear it, so a failure inside a
        /// sub-function still reaches the caller.</para>
        /// </summary>
        [ThreadStatic]
        private static string _lastType0Failure;
        public static string LastType0Failure => _lastType0Failure;

        /// <summary>
        /// Evaluates a PDF function dictionary at the given input values.
        /// Clamps inputs to Domain and outputs to Range when present.
        /// Returns an array of output values (float[]).
        /// </summary>
        public static float[] Evaluate(PdfDictionary fn, float[] inputs)
            => Evaluate(fn, inputs, null);

        /// <summary>
        /// Evaluates a <c>/Function</c> <b>entry</b>, which the spec allows to be either a single
        /// <i>n</i>-out function <b>or an array of <i>n</i> 1-out functions</b> — one per colour
        /// component. §8.7.4.5.2 (shadings), §8.6.6.4 (DeviceN/Separation alternates) and §10.4
        /// (transfer functions) all take that shape.
        /// </summary>
        /// <remarks>
        /// <para><b>This exists because narrowing an entry to <c>PdfDictionary</c> is a resolve and a
        /// filter, and the filter is the half that goes wrong.</b> <c>ResolveObject(sh[/Function]) as
        /// PdfDictionary</c>, then <c>return null</c>, would make a Type 1 shading written with the
        /// array form draw <b>nothing at all</b>, in the raster path and the SVG export alike — one gap
        /// in both renderers, so the answer is a helper they share.</para>
        ///
        /// <para>Their axial/radial paths call here too, so the four sites cannot drift on what a
        /// <c>/Function</c> entry may legally be.</para>
        ///
        /// <para>Entries that do not resolve to a function are <b>skipped</b>, not written as zero:
        /// a zero component is a colour, and an absent one is a shorter vector the caller can test.
        /// Returns <see langword="null"/> when nothing evaluated — the "no usable colour" signal
        /// every caller already had.</para>
        ///
        /// <para><c>PdfStream</c> derives from <c>PdfDictionary</c>, so Type 0 and Type 4 functions
        /// need no separate case here.</para>
        /// </remarks>
        /// <param name="function">The raw <c>/Function</c> value: a function, an array of them, or
        /// an indirect reference to either.</param>
        /// <param name="inputs">The input vector — 1 value for axial/radial, 2 for Type 1.</param>
        /// <param name="resolve">Typically <c>PdfDocument.ResolveObject</c>. May be null.</param>
        public static float[] EvaluateEntry(PdfObject function, float[] inputs,
            Func<PdfObject, PdfObject> resolve)
        {
            PdfObject fn = Deref(function, resolve);

            if (fn is PdfArray arr)
            {
                // One float[] sized at the array's own count, rather than a List<float> plus a
                // ToArray copy: this runs once per sample of a Type 1 shading grid (up to 512x512)
                // and per pixel of a DeviceN image, so the second allocation was never free.
                // Entries that do not resolve to a function are skipped, so the filled count can
                // be short — the array is trimmed only in that (rare) case.
                if (arr.Count == 0) return null;
                float[] outputs = new float[arr.Count];
                int filled = 0;
                foreach (PdfObject item in arr)
                {
                    if (!(Deref(item, resolve) is PdfDictionary sub)) continue;
                    float[] r = Evaluate(sub, inputs, resolve);
                    if (r != null && r.Length > 0) outputs[filled++] = r[0];
                }
                if (filled == 0) return null;
                if (filled == outputs.Length) return outputs;
                var trimmed = new float[filled];
                Array.Copy(outputs, trimmed, filled);
                return trimmed;
            }

            return fn is PdfDictionary dict ? Evaluate(dict, inputs, resolve) : null;
        }

        /// <summary>
        /// True when <paramref name="function"/> is a shape <see cref="EvaluateEntry"/> can evaluate.
        /// Lets a caller decline before doing the setup work an evaluation needs.
        /// </summary>
        public static bool IsFunctionEntry(PdfObject function, Func<PdfObject, PdfObject> resolve)
        {
            PdfObject fn = Deref(function, resolve);
            return fn is PdfDictionary || fn is PdfArray;
        }

        private static PdfObject Deref(PdfObject obj, Func<PdfObject, PdfObject> resolve)
            => resolve != null && obj is PdfReference ? resolve(obj) : obj;

        /// <summary>
        /// Overload accepting a <paramref name="resolve"/> callback (typically
        /// <c>PdfDocument.ResolveObject</c>) so a Type 3 stitching function whose
        /// <c>/Functions</c> entries are INDIRECT object references can resolve them.
        /// Without it those sub-functions evaluate to <c>0</c> — e.g. a multi-segment
        /// (stitching) shading gradient renders as a flat black ramp.
        /// </summary>
        public static float[] Evaluate(PdfDictionary fn, float[] inputs, Func<PdfObject, PdfObject> resolve)
        {
            // Reset the caller-visible diagnostic at the START of the operation it describes, on
            // EVERY path including the two early returns below; see LastType0Failure. Depth 0
            // only, so a nested sub-function's reason survives to the caller rather than being
            // cleared by the next sibling.
            if (_evalDepth == 0) _lastType0Failure = null;

            if (fn == null || inputs == null) return new float[] { 0f };

            // Recursion guard: a Type 3 (stitching) function's /Functions entries may be
            // INDIRECT references, so a crafted pair (A → [B], B → [A]) recurses through
            // Evaluate→EvalType3→Evaluate forever → uncatchable StackOverflow. The parser's
            // nesting-depth cap does not cover this (each object is shallow). Guard here, the
            // single recursion entry point. ThreadStatic so concurrent renders are independent.
            if (_evalDepth >= MaxEvalDepth) return new float[] { 0f };
            _evalDepth++;
            try
            {
                // Every key this evaluation needs, parsed once per function dictionary — see
                // FnParams. A /FunctionType of -1 (absent, or indirect with no resolver) falls to
                // `default:` and returns {0f} BEFORE any EvalType runs, so LastType0Failure stays
                // null too: a shading whose /FunctionType is indirect evaluates to black everywhere
                // and reports nothing.
                FnParams p = GetParams(fn, resolve);

                // Clamp inputs to Domain
                inputs = ClampToDomain(inputs, p.Domain);

                float[] result;
                switch (p.Type)
                {
                    case 0: result = EvalType0(fn, inputs, p); break;
                    case 2: result = EvalType2(inputs, p); break;
                    case 3: result = EvalType3(inputs, p, resolve); break;
                    case 4: result = EvalType4(fn, inputs, p); break;
                    default: return new float[] { 0f };
                }

                // Clamp outputs to Range if present. In place: every `result` above is an array
                // this call has just allocated (or, for Type 3, the one its sub-function allocated
                // and handed back), so nothing else can be holding it.
                ClampToRange(result, p.Range);
                return result;
            }
            finally { _evalDepth--; }
        }

        [ThreadStatic] private static int _evalDepth;
        private const int MaxEvalDepth = 100;

        // ----------------------------------------------------------------
        //  Per-depth scratch buffers
        // ----------------------------------------------------------------
        //
        // Every intermediate float[] an evaluation needs — the domain-clamped inputs, the encoded
        // coordinates, the sample corners a Type 0 interpolation reads, and the one-element input
        // vector a Type 3 hands its sub-function — is dead by the time Evaluate returns. Allocating
        // them per call cost 3-5 arrays on a path a Type 1 shading drives up to 262 144 times and a
        // DeviceN image up to width x height times: a Type 0 evaluation allocated 256 bytes, and
        // now allocates 40 — the returned vector, which the contract requires.
        //
        // ~5.6 KB of jagged-array slots per thread that ever evaluates a function, allocated on
        // first use. The alternative — one buffer set with no depth index — is what a stitching
        // function would corrupt.
        //
        // Indexed BY DEPTH, because a Type 3 stitching function calls Evaluate recursively.
        // `_evalDepth` is 1-based inside Evaluate, so depth d owns slots
        // [(d-1) * ScratchSlots, d * ScratchSlots).
        //
        // BE HONEST ABOUT WHAT THE INDEX BUYS: as the code stands today a single shared set would
        // also be correct, and mutating the index away reddens nothing — no path reads a scratch
        // buffer after a nested Evaluate has returned (EvalType3 copies its input to a local before
        // it recurses, and returns the sub-function's array untouched). The index is here so that
        // stays true by construction rather than by re-derivation: a later edit that blended two
        // sub-functions, or read `inputs` after the recursive call, would silently read the child's
        // values instead of its own. That is the failure mode this file already has a history of,
        // and one multiply-add per buffer is what it costs to make it unreachable.
        //
        // Sized EXACTLY, never "at least": ClampToDomain's result is read back through its own
        // `.Length` by EvalType0 (`int m = inputs.Length`), so a buffer that is merely long enough
        // would silently change the input arity. The length is stable across a shading loop, so the
        // exact-size rule costs one allocation per function rather than one per evaluation.
        private const int ScratchSlots = 7;
        private const int ScratchDomain = 0;   // ClampToDomain output, length = inputs.Length
        private const int ScratchEncoded = 1;  // EvalType0 encoded coordinates, length = m
        private const int ScratchSample0 = 2;  // EvalType0 sample corners, length = n (4 of them)
        private const int ScratchSubInput = 6; // EvalType3 sub-function input vector, length = 1

        [ThreadStatic] private static float[][] _floatScratch;
        [ThreadStatic] private static int[][] _intScratch;

        /// <summary>
        /// The current depth's scratch buffer for <paramref name="slot"/>, exactly
        /// <paramref name="length"/> long. Contents are undefined; every caller fills what it reads.
        /// </summary>
        private static float[] Scratch(int slot, int length)
        {
            var all = _floatScratch ??= new float[MaxEvalDepth * ScratchSlots][];
            int i = (_evalDepth - 1) * ScratchSlots + slot;
            var buf = all[i];
            if (buf == null || buf.Length != length) all[i] = buf = new float[length];
            return buf;
        }

        /// <summary>Integer counterpart of <see cref="Scratch"/>; one slot per depth (Type 0 /Size).</summary>
        private static int[] IntScratch(int length)
        {
            var all = _intScratch ??= new int[MaxEvalDepth][];
            var buf = all[_evalDepth - 1];
            if (buf == null || buf.Length != length) all[_evalDepth - 1] = buf = new int[length];
            return buf;
        }

        // ----------------------------------------------------------------
        //  Reference-following key reads
        // ----------------------------------------------------------------
        //
        // Every value in a function dictionary may legally be written as an indirect
        // reference. Reading them as `fn[key] as PdfArray` silently yielded null — i.e.
        // "absent" — so a /C0, /C1, /Size, /Encode, /Decode, /Bounds, /Domain or /Range
        // written as `6 0 R` was dropped and the function fell back to its defaults. A
        // shading whose /C1 was indirect faded to white instead of its end colour.
        //
        // `resolve` is null on the overload that has no document (behaviour unchanged there).

        // ----------------------------------------------------------------
        //  Parsed parameters, memoised per function dictionary
        // ----------------------------------------------------------------

        /// <summary>
        /// Every loop-invariant value of one function dictionary, parsed once: the key reads, the
        /// indirect-reference resolution and the <c>PdfNumber</c> unboxing that would otherwise happen
        /// on every evaluation — measured at <b>43-44% of a Type 0 evaluation</b>,
        /// on a path a Type 1 shading drives up to 262 144 times per paint and a DeviceN image
        /// drives once per pixel.
        /// </summary>
        /// <remarks>
        /// <para><b>Keyed on the dictionary instance, and that is the one thing to understand
        /// before touching this.</b> <see cref="_programTokens"/> ten screens down keys on the
        /// decoded <i>byte array</i> instead, and its doc comment explains why: a
        /// <see cref="PdfStream"/>'s bytes have an invalidation channel — <c>RawData</c>'s setter
        /// drops the decode memo, so mutated content yields a different array and misses the cache.
        /// <b>A dictionary's entries have no such channel</b>: <c>Set</c> / <c>Remove</c> would
        /// leave this memo holding the old parameters, and the function would keep evaluating
        /// under them. The two caches therefore rest on different arguments and are not evidence for
        /// each other.</para>
        /// <para><b>The invariant this relies on: a function dictionary's parameters are not
        /// written after it is parsed.</b> That holds across this repository — the only code that
        /// writes <c>/FunctionType</c>, <c>/Domain</c>, <c>/Bounds</c>, <c>/C0</c>, <c>/C1</c> or
        /// <c>/Encode</c> is <c>PdfContentStreamBuilder</c>, which is <i>constructing</i> new
        /// function dictionaries, and every <c>/Size</c> write in the tree is an xref trailer size
        /// while every <c>/Decode</c> write is on an image XObject. If you ever add a pass that
        /// rewrites a live function dictionary in place — an optimizer normalising a tint transform,
        /// say — it must not reuse the dictionary instance, or this memo must go.</para>
        /// <para>The <see cref="Resolve"/> field closes the other way this could hand back a wrong
        /// answer: the two-argument <c>Evaluate</c> passes no resolver, so an indirect <c>/Domain</c>
        /// reads as <i>absent</i> there and as its value under a resolver. An entry parsed with a
        /// different resolver is rebuilt and <b>not</b> stored, so neither call can serve the other
        /// one's answer. Delegate equality compares target and method, so the fresh
        /// <c>_doc.ResolveObject</c> delegate each call site creates still matches.</para>
        /// <para>A <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
        /// for the same reason <see cref="_programTokens"/> is one: the entry dies with the
        /// dictionary rather than pinning every function a long-lived process ever evaluated, and
        /// it is safe for concurrent renders.</para>
        /// </remarks>
        private sealed class FnParams
        {
            internal Func<PdfObject, PdfObject> Resolve;
            internal int Type;
            internal float[] Domain;    // null when the key is absent — the callers test for that
            internal float[] Range;
            // Type 0
            internal int[] Size;
            internal float[] Encode;
            internal float[] Decode;    // already defaulted to /Range per ISO 32000-1 Table 38
            internal int Order;
            internal int Bps;
            // Type 2
            internal float N;
            internal float[] C0;
            internal float[] C1;
            // Type 3 (Encode is shared with Type 0 above)
            internal PdfObject[] Functions;  // raw entries; resolution of the chosen one stays per call
            internal float[] Bounds;         // the computed k+1 boundary table, not the raw /Bounds
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, FnParams>
            _fnParams = new System.Runtime.CompilerServices.ConditionalWeakTable<PdfDictionary, FnParams>();

        private static FnParams GetParams(PdfDictionary fn, Func<PdfObject, PdfObject> resolve)
        {
            if (_fnParams.TryGetValue(fn, out var cached))
                // Built under a different resolver: parse fresh and DO NOT store, so the two
                // answers cannot displace each other. Never happens on a rendering path, where a
                // document's dictionaries are only ever seen with that document's resolver.
                return cached.Resolve == resolve ? cached : BuildParams(fn, resolve);

            return BuildAndStore(fn, resolve);
        }

        /// <summary>
        /// The cold half of <see cref="GetParams"/>, and it is a separate method for a reason worth
        /// keeping: the <c>GetValue</c> callback below captures a local, so the C# compiler
        /// allocates its closure at the <b>top of whatever method declares that local</b> — not at
        /// the lambda. Inlined into <see cref="GetParams"/> it therefore cost <b>24 bytes on every
        /// evaluation</b>, cache hit included, which was measurable (64 B/eval against 40 B) and
        /// silently ate a third of what the memo saved.
        /// </summary>
        private static FnParams BuildAndStore(PdfDictionary fn, Func<PdfObject, PdfObject> resolve)
        {
            var built = BuildParams(fn, resolve);
            // GetValue rather than Add: a concurrent evaluation of the same function may have won
            // the race, and Add would throw on the duplicate key.
            var winner = _fnParams.GetValue(fn, _ => built);
            return winner.Resolve == resolve ? winner : built;
        }

        private static FnParams BuildParams(PdfDictionary fn, Func<PdfObject, PdfObject> resolve)
        {
            var p = new FnParams
            {
                Resolve = resolve,
                Type    = ResolvedNumber(fn, PdfName.FunctionType, resolve) is PdfNumber ft ? ft.IntValue : -1,
                Domain  = Floats(ResolvedArray(fn, PdfName.Domain, resolve)),
                Range   = Floats(ResolvedArray(fn, PdfName.Range,  resolve)),
            };

            switch (p.Type)
            {
                case 0:
                    p.Size   = SizeInts(ResolvedArray(fn, PdfName.Size, resolve));
                    p.Encode = Floats(ResolvedArray(fn, PdfName.Encode, resolve));
                    // ISO 32000-1 Table 38: /Decode DEFAULTS TO /Range. Without this the samples
                    // stay normalised to [0,1] and ClampToRange can only clamp them, so a tint
                    // transform into a space whose range is not [0,1] collapsed — a Separation into
                    // Lab (/Range [0 100 -128 127 -128 127]) returned L ~ 0..1 and rendered
                    // near-black. For the common /Range [0 1 ...] the substitution is the identity,
                    // which is why it went unseen.
                    p.Decode = Floats(ResolvedArray(fn, PdfName.Decode, resolve)) ?? p.Range;
                    p.Order  = ResolvedNumber(fn, PdfName.Order,         resolve) is PdfNumber ord ? ord.IntValue : 1;
                    p.Bps    = ResolvedNumber(fn, PdfName.BitsPerSample, resolve) is PdfNumber b   ? b.IntValue   : 8;
                    break;

                case 2:
                    p.N  = ResolvedNumber(fn, PdfName.N, resolve) is PdfNumber nv ? (float)nv.DoubleValue : 1f;
                    p.C0 = Floats(ResolvedArray(fn, PdfName.C0, resolve));
                    p.C1 = Floats(ResolvedArray(fn, PdfName.C1, resolve));
                    break;

                case 3:
                    var funcsArr = ResolvedArray(fn, PdfName.Functions, resolve);
                    if (funcsArr != null && funcsArr.Count > 0)
                    {
                        var entries = new PdfObject[funcsArr.Count];
                        for (int i = 0; i < entries.Length; i++) entries[i] = funcsArr[i];
                        p.Functions = entries;

                        // The k+1 boundary table, which is a pure function of /Domain, /Bounds and
                        // the sub-function count — so it is built here rather than rebuilt per call.
                        var boundsArr = ResolvedArray(fn, PdfName.Bounds, resolve);
                        int k = entries.Length;
                        float domMin = p.Domain != null && p.Domain.Length >= 2 ? p.Domain[0] : 0f;
                        float domMax = p.Domain != null && p.Domain.Length >= 2 ? p.Domain[1] : 1f;
                        var bounds = new float[k + 1];
                        bounds[0] = domMin;
                        bounds[k] = domMax;
                        // /Bounds has k-1 elements separating k sub-functions.
                        for (int i = 0; i < k - 1; i++)
                            bounds[i + 1] = boundsArr != null && i < boundsArr.Count
                                ? GetArrFloat(boundsArr, i) : (domMin + (domMax - domMin) * (i + 1) / k);
                        p.Bounds = bounds;
                    }
                    p.Encode = Floats(ResolvedArray(fn, PdfName.Encode, resolve));
                    break;
            }

            return p;
        }

        /// <summary>
        /// A numeric array as <c>float</c>s, or <c>null</c> when the key was absent — the state
        /// every caller already tested for. An element that is not a <see cref="PdfNumber"/>
        /// (including an unresolved indirect reference) becomes <c>0</c>, which is exactly what
        /// <see cref="GetArrFloat"/> returned for it.
        /// </summary>
        private static float[] Floats(PdfArray arr)
        {
            if (arr == null) return null;
            var v = new float[arr.Count];
            for (int i = 0; i < v.Length; i++) v[i] = (float)((arr[i] as PdfNumber)?.DoubleValue ?? 0.0);
            return v;
        }

        /// <summary>
        /// <c>/Size</c> as integers. A non-numeric entry becomes <b>1</b>, not 0 — matching the
        /// per-axis default the sampled-function path applies, and a 0 would make the table
        /// unaddressable.
        /// </summary>
        private static int[] SizeInts(PdfArray arr)
        {
            if (arr == null) return null;
            var v = new int[arr.Count];
            for (int i = 0; i < v.Length; i++) v[i] = arr[i] is PdfNumber n ? n.IntValue : 1;
            return v;
        }

        /// <summary>Element <paramref name="i"/>, or 0 past the end — <see cref="GetArrFloat"/>'s rule.</summary>
        private static float At(float[] a, int i) => i < a.Length ? a[i] : 0f;

        // Both call the resolver only for a value that IS a PdfReference, as `Deref` above does.
        // `PdfParser.ResolveObject` returns a non-reference unchanged, so the result is identical
        // — it just saves a delegate invocation and a type test for every direct value on a path
        // that reads ten keys per evaluation.

        private static PdfArray ResolvedArray(PdfDictionary fn, PdfName key,
            Func<PdfObject, PdfObject> resolve)
        {
            var v = fn?[key];
            if (v == null) return null;
            if (resolve != null && v is PdfReference) v = resolve(v) ?? v;
            return v as PdfArray;
        }

        private static PdfNumber ResolvedNumber(PdfDictionary fn, PdfName key,
            Func<PdfObject, PdfObject> resolve)
        {
            var v = fn?[key];
            if (v == null) return null;
            if (resolve != null && v is PdfReference) v = resolve(v) ?? v;
            return v as PdfNumber;
        }

        // ----------------------------------------------------------------
        //  Type 0 — Sampled function (multi-linear interpolation table)
        // ----------------------------------------------------------------

        private static float[] EvalType0(PdfDictionary fn, float[] inputs, FnParams p)
        {
            // Every key below is read once, in BuildParams, including the /Decode-defaults-to-/Range
            // substitution (ISO 32000-1 Table 38): read on every evaluation they cost 43-44% of a
            // Type 0 evaluation.
            int[] sizeArr    = p.Size;
            float[] encodeArr = p.Encode;
            float[] decodeArr = p.Decode;
            float[] rangeArr  = p.Range;
            float[] domainArr = p.Domain;
            int order = p.Order;
            int bps   = p.Bps;

            // /BitsPerSample is restricted to this set by ISO 32000-1 Table 38. Validate it:
            // the value drives a per-bit loop, so an unvalidated 2,000,000,000 turns one sample
            // read into a two-billion-iteration loop — and ContentStreamInterpreter's function
            // shader calls Evaluate up to 512x512 times per shading, with an early-out that only
            // catches exceptions, never a merely slow loop.
            // It also removes the `1L << bps` shift-count masking below: with bps == 64 the shift
            // count masks to 0, so sampleMax became 0 and non-zero samples produced +Infinity.
            if (bps != 1 && bps != 2 && bps != 4 && bps != 8 &&
                bps != 12 && bps != 16 && bps != 24 && bps != 32)
            {
                _lastType0Failure =
                    $"/BitsPerSample {bps} is not one of the values permitted by ISO 32000-1 Table 38 " +
                    "(1, 2, 4, 8, 12, 16, 24, 32).";
                return new float[] { 0f };
            }

            if (sizeArr == null) return new float[] { 0f };

            int m = inputs.Length;
            int[] sizes = IntScratch(m);
            for (int i = 0; i < m; i++)
                sizes[i] = sizeArr.Length > i ? sizeArr[i] : 1;

            // Encode: map each input from Domain[2i..2i+1] to [0, Size[i]-1]
            float[] encoded = Scratch(ScratchEncoded, m);
            for (int i = 0; i < m; i++)
            {
                float emin = 0f, emax = sizes[i] - 1f;
                if (encodeArr != null && encodeArr.Length >= (i + 1) * 2)
                {
                    emin = encodeArr[i * 2];
                    emax = encodeArr[i * 2 + 1];
                }
                // Domain for input i. Read through the resolver like every sibling key — this was
                // the one raw `fn[...] as PdfArray` left in the type-0 path, so an indirect
                // /Domain silently reverted to [0 1] in the encode step alone (ClampToDomain, which
                // does resolve it, then disagreed).
                float dmin = 0f, dmax = 1f;
                if (domainArr != null && domainArr.Length >= (i + 1) * 2)
                {
                    dmin = domainArr[i * 2];
                    dmax = domainArr[i * 2 + 1];
                }
                float t = dmax == dmin ? 0f : (inputs[i] - dmin) / (dmax - dmin);
                encoded[i] = Clamp(emin + t * (emax - emin), emin, emax);
            }

            // Get sample stream data.  Track failures via LastType0Failure so callers
            // can diagnose "shading went black" without stepping through the
            // PostScript evaluator.
            var stream = fn as PdfStream;
            if (stream == null)
            {
                _lastType0Failure = "Type 0 function dictionary is not a stream (sample table missing)";
                return new float[] { 0f };
            }

            byte[] sampleData;
            // No `= null` on success: the slot is cleared once, at the start of the top-level
            // Evaluate. Clearing it here would let a later sub-function of one stitching call
            // erase the reason an earlier one recorded.
            try { sampleData = stream.GetDecodedData(); }
            catch (Exception ex)
            {
                _lastType0Failure = "Type 0 sample stream decode failed: " + ex.GetType().Name + ": " + ex.Message;
                return new float[] { 0f };
            }

            // Determine output count from Range
            int n = rangeArr != null ? rangeArr.Length / 2 : 1;
            float sampleMax = (float)((1L << bps) - 1);

            // Multi-linear interpolation (order 1) over the n-dimensional sample table.
            // 1D: linear (order 1) or Catmull-Rom cubic (order 3) per PDF §7.10.2.
            // 2D: bilinear.  3D+: multilinear over the 2^m corners, up to
            // PdfLimits.MaxSampledFunctionInterpolationInputs inputs.
            if (m == 1)
            {
                int i0 = SaturatingCast.ToInt(Math.Floor(encoded[0]));
                int i1 = Math.Min(i0 + 1, sizes[0] - 1);
                float frac = encoded[0] - i0;
                i0 = Math.Max(0, Math.Min(i0, sizes[0] - 1));

                float[] result = new float[n];

                if (order == 3 && sizes[0] >= 2)
                {
                    // Catmull-Rom cubic: needs 4 samples (i-1, i, i+1, i+2).
                    int im1 = Math.Max(0, i0 - 1);
                    int ip2 = Math.Min(sizes[0] - 1, i0 + 2);
                    float[] sm1 = Scratch(ScratchSample0,     n);
                    float[] s0c = Scratch(ScratchSample0 + 1, n);
                    float[] s1c = Scratch(ScratchSample0 + 2, n);
                    float[] sp2 = Scratch(ScratchSample0 + 3, n);
                    ReadSamplesInto(sampleData, im1, n, bps, sampleMax, sm1);
                    ReadSamplesInto(sampleData, i0,  n, bps, sampleMax, s0c);
                    ReadSamplesInto(sampleData, i1,  n, bps, sampleMax, s1c);
                    ReadSamplesInto(sampleData, ip2, n, bps, sampleMax, sp2);
                    float t = frac;
                    float t2 = t * t, t3 = t2 * t;
                    for (int j = 0; j < n; j++)
                    {
                        result[j] = 0.5f * (
                            (2f * s0c[j]) +
                            (-sm1[j] + s1c[j]) * t +
                            (2f * sm1[j] - 5f * s0c[j] + 4f * s1c[j] - sp2[j]) * t2 +
                            (-sm1[j] + 3f * s0c[j] - 3f * s1c[j] + sp2[j]) * t3);
                    }
                }
                else
                {
                    float[] s0 = Scratch(ScratchSample0,     n);
                    float[] s1 = Scratch(ScratchSample0 + 1, n);
                    ReadSamplesInto(sampleData, i0, n, bps, sampleMax, s0);
                    ReadSamplesInto(sampleData, i1, n, bps, sampleMax, s1);
                    for (int j = 0; j < n; j++)
                        result[j] = s0[j] + frac * (s1[j] - s0[j]);
                }

                ApplyDecode(decodeArr, result, n);
                return result;
            }
            else if (m == 2)
            {
                // Bilinear interpolation for 2D functions (PostScript spec requires
                // linear interpolation for Order=1, which is the default).
                int x0 = SaturatingCast.ToInt(Math.Floor(encoded[0]));
                int y0 = SaturatingCast.ToInt(Math.Floor(encoded[1]));
                int x1 = Math.Min(x0 + 1, sizes[0] - 1);
                int y1 = Math.Min(y0 + 1, sizes[1] - 1);
                float fx = encoded[0] - x0;
                float fy = encoded[1] - y0;
                x0 = Math.Max(0, Math.Min(x0, sizes[0] - 1));
                y0 = Math.Max(0, Math.Min(y0, sizes[1] - 1));

                float[] s00 = Scratch(ScratchSample0,     n);
                float[] s10 = Scratch(ScratchSample0 + 1, n);
                float[] s01 = Scratch(ScratchSample0 + 2, n);
                float[] s11 = Scratch(ScratchSample0 + 3, n);
                ReadSamplesInto(sampleData, y0 * sizes[0] + x0, n, bps, sampleMax, s00);
                ReadSamplesInto(sampleData, y0 * sizes[0] + x1, n, bps, sampleMax, s10);
                ReadSamplesInto(sampleData, y1 * sizes[0] + x0, n, bps, sampleMax, s01);
                ReadSamplesInto(sampleData, y1 * sizes[0] + x1, n, bps, sampleMax, s11);

                float[] result = new float[n];
                for (int j = 0; j < n; j++)
                {
                    float top    = s00[j] + fx * (s10[j] - s00[j]);
                    float bottom = s01[j] + fx * (s11[j] - s01[j]);
                    result[j] = top + fy * (bottom - top);
                }

                ApplyDecode(decodeArr, result, n);
                return result;
            }
            else
            {
                // 3 or more inputs: multilinear over the 2^m corners of the surrounding cell —
                // the same interpolation the 1D and 2D branches above do, generalised. This used
                // to take the nearest sample, which quantises a 4-ink DeviceN→CMYK transform to
                // its /Size grid and bands every gradient built on one.
                //
                // The corner count is exponential in m, and m is the colorant count the FILE
                // chooses (up to 32), so it is capped: past the cap the nearest sample is used —
                // the previous behaviour, still a usable colour — and the reason is recorded.
                //
                // First, addressability. A /Size whose product exceeds what a sample index can
                // hold names entries no reader can reach, and multiplying the mixed-radix strides
                // out would wrap and name a valid-looking WRONG one. Every read would be out of
                // range anyway, so the samples are zero — say why instead of returning a silent
                // black, and let both walks below assume their arithmetic cannot overflow.
                long total = 1;
                for (int i = 0; i < m && total <= int.MaxValue; i++) total *= Math.Max(1, sizes[i]);

                float[] s;
                if (total > int.MaxValue)
                {
                    s = new float[n];
                    _lastType0Failure =
                        "Type 0 function /Size declares more samples than an index can address " +
                        $"({string.Join("x", sizes)}); no sample is reachable, so it evaluated as 0.";
                }
                else if (m <= PdfLimits.MaxSampledFunctionInterpolationInputs)
                {
                    s = InterpolateMultilinear(sampleData, encoded, sizes, m, n, bps, sampleMax);
                }
                else
                {
                    s = NearestSample(sampleData, encoded, sizes, m, n, bps, sampleMax);
                    _lastType0Failure =
                        $"Type 0 function has {m} inputs; multilinear interpolation is capped at " +
                        $"{PdfLimits.MaxSampledFunctionInterpolationInputs} " +
                        "(PdfLimits.MaxSampledFunctionInterpolationInputs) because it visits 2^m " +
                        "samples. Nearest-sample interpolation was used instead.";
                }

                ApplyDecode(decodeArr, s, n);
                return s;
            }
        }

        /// <summary>
        /// §7.10.2's <c>/Decode</c> step: maps each interpolated sample from [0, 1] onto its
        /// <c>/Decode</c> interval, in place. A null <paramref name="decodeArr"/> leaves the values
        /// alone (Table 38's default is <c>/Range</c>, substituted once in <c>BuildParams</c>, so a
        /// null here means the function declared neither).
        /// </summary>
        /// <remarks>
        /// <b>One implementation for all three interpolation branches</b> — one input, two, three or
        /// more. Nothing about <c>/Decode</c> depends
        /// on how many <i>inputs</i> a sampled function takes, so three copies could only ever
        /// agree or be a defect; and the shape of that defect is a colour that changes when a
        /// producer adds an axis, which no assertion about a single function would show.
        /// </remarks>
        private static void ApplyDecode(float[] decodeArr, float[] values, int n)
        {
            if (decodeArr == null) return;

            for (int j = 0; j < n; j++)
            {
                float dmin = At(decodeArr, j * 2);
                float dmax = At(decodeArr, j * 2 + 1);
                values[j] = dmin + values[j] * (dmax - dmin);
            }
        }

        /// <summary>
        /// Multilinear interpolation of an m-dimensional sample table (m &gt;= 3): the weighted
        /// average of the 2^m corners of the cell containing <paramref name="encoded"/>, each
        /// corner weighted by the product of the per-axis fractions.
        /// </summary>
        private static float[] InterpolateMultilinear(byte[] data, float[] encoded, int[] sizes,
            int m, int n, int bps, float sampleMax)
        {
            var lo   = new int[m];
            var frac = new float[m];
            for (int i = 0; i < m; i++)
            {
                int size = Math.Max(1, sizes[i]);
                int floor = SaturatingCast.ToInt(Math.Floor(encoded[i]));
                lo[i]   = Math.Max(0, Math.Min(floor, size - 1));
                // Zero fraction at (and past) the last sample, so the clamped upper corner
                // contributes nothing rather than duplicating the lower one with weight.
                frac[i] = floor == lo[i] && lo[i] < size - 1 ? encoded[i] - floor : 0f;
            }

            var result = new float[n];
            var corner = new float[n];
            int corners = 1 << m;
            for (int mask = 0; mask < corners; mask++)
            {
                // Every index is clamped to its axis and the caller has already established that
                // the axes multiply to at most int.MaxValue entries, so the mixed-radix sum below
                // cannot exceed the table's own entry count and cannot overflow.
                float weight = 1f;
                long flat = 0, stride = 1;
                for (int i = 0; i < m; i++)
                {
                    bool upper = (mask & (1 << i)) != 0;
                    weight *= upper ? frac[i] : 1f - frac[i];
                    if (weight == 0f) break;
                    int size = Math.Max(1, sizes[i]);
                    flat   += (long)(upper ? Math.Min(lo[i] + 1, size - 1) : lo[i]) * stride;
                    stride *= size;
                }
                // A zero-weight corner cannot change the result, and skipping it is what keeps the
                // common case (an input landing exactly on a sample) at one read rather than 2^m.
                if (weight == 0f) continue;

                ReadSamplesInto(data, (int)flat, n, bps, sampleMax, corner);
                for (int j = 0; j < n; j++) result[j] += weight * corner[j];
            }
            return result;
        }

        /// <summary>Nearest sample in an m-dimensional table — the fallback past the interpolation cap.</summary>
        private static float[] NearestSample(byte[] data, float[] encoded, int[] sizes,
            int m, int n, int bps, float sampleMax)
        {
            // Bounded like the multilinear path above: clamped indices over axes whose product
            // the caller has already checked, so the sum stays inside the table.
            long flat = 0, stride = 1;
            for (int i = 0; i < m; i++)
            {
                int size = Math.Max(1, sizes[i]);
                int idx  = SaturatingCast.ToInt(Math.Round(encoded[i]));
                flat   += (long)Math.Max(0, Math.Min(idx, size - 1)) * stride;
                stride *= size;
            }
            return GetSamples(data, (int)flat, n, bps, sampleMax);
        }

        private static float[] GetSamples(byte[] data, int index, int n, int bps, float sampleMax)
        {
            float[] result = new float[n];
            ReadSamplesInto(data, index, n, bps, sampleMax, result);
            return result;
        }

        /// <summary>
        /// Reads the <paramref name="n"/> samples of one table entry into a caller-owned buffer.
        /// The multilinear path visits up to 2^m corners per evaluation and would otherwise
        /// allocate one array per corner.
        /// </summary>
        private static void ReadSamplesInto(byte[] data, int index, int n, int bps,
            float sampleMax, float[] dest)
        {
            // index is derived from attacker-controlled /Encode bounds and /Size;
            // a negative or overflowed bit offset must not index out of bounds.
            // Compute the offset in long to avoid int overflow, and treat an
            // out-of-range index as all-zero samples.
            if (index < 0)
            {
                Array.Clear(dest, 0, n);
                return;
            }
            long bitsOffset = (long)index * n * bps;
            for (int j = 0; j < n; j++)
            {
                long bitStart = bitsOffset + (long)j * bps;
                long rawVal = ReadBits(data, bitStart, bps);
                dest[j] = rawVal / sampleMax;
            }
        }

        // One implementation, in Core.IO.MsbBitReader — shared with LzwDecodeFilter, which carried
        // the same rule with an `int` accumulator. THIS side is the one that had to survive:
        // Table 38 lets /BitsPerSample reach 32, and an int accumulator returns a negative number
        // there.
        private static long ReadBits(byte[] data, long bitOffset, int bps)
            => IO.MsbBitReader.ReadBits(data, bitOffset, bps);

        // ----------------------------------------------------------------
        //  Type 2 — Exponential interpolation
        //  result[i] = C0[i] + t^N * (C1[i] - C0[i])
        // ----------------------------------------------------------------

        private static float[] EvalType2(float[] inputs, FnParams p)
        {
            float t = inputs.Length > 0 ? inputs[0] : 0f;
            float N = p.N;

            float[] c0Arr = p.C0;
            float[] c1Arr = p.C1;

            int n = Math.Max(
                c0Arr?.Length ?? 1,
                c1Arr?.Length ?? 1);

            float tN = (float)Math.Pow(t, N);
            float[] result = new float[n];
            for (int i = 0; i < n; i++)
            {
                float c0 = c0Arr != null && i < c0Arr.Length ? c0Arr[i] : 0f;
                float c1 = c1Arr != null && i < c1Arr.Length ? c1Arr[i] : 1f;
                result[i] = c0 + tN * (c1 - c0);
            }
            return result;
        }

        // ----------------------------------------------------------------
        //  Type 3 — Stitching function
        //  Selects sub-function by Bounds, encodes input
        // ----------------------------------------------------------------

        private static float[] EvalType3(float[] inputs, FnParams p, Func<PdfObject, PdfObject> resolve)
        {
            float t = inputs.Length > 0 ? inputs[0] : 0f;

            PdfObject[] funcsArr  = p.Functions;
            float[] encodeArr     = p.Encode;

            // BuildParams leaves both null together when /Functions is absent or empty.
            if (funcsArr == null) return new float[] { 0f };

            // The k+1 boundary table is a pure function of /Domain, /Bounds and k, so it is built
            // once in BuildParams rather than re-derived (and re-allocated) on every evaluation.
            float[] bounds = p.Bounds;
            int k = funcsArr.Length;

            // Find which sub-function applies
            int idx = k - 1;
            for (int i = 0; i < k - 1; i++)
            {
                if (t < bounds[i + 1]) { idx = i; break; }
            }

            // Encode the input into the sub-function's domain
            float emin = encodeArr != null && idx * 2 < encodeArr.Length ? encodeArr[idx * 2]     : 0f;
            float emax = encodeArr != null && idx * 2 + 1 < encodeArr.Length ? encodeArr[idx * 2 + 1] : 1f;

            float b0 = bounds[idx];
            float b1 = bounds[idx + 1];
            float subT = b1 == b0 ? emin : emin + (t - b0) / (b1 - b0) * (emax - emin);

            var subFn = funcsArr[idx];
            // subFn may be a direct dict/stream or an INDIRECT reference. When a resolver
            // is supplied (the rendering call sites pass PdfDocument.ResolveObject), resolve
            // it so indirect sub-functions work; otherwise an indirect ref yields 0.
            if (resolve != null) subFn = resolve(subFn) ?? subFn;

            // PdfStream derives from PdfDictionary, so a Type 0 / Type 4 sub-function (which IS a
            // stream) matches here too, one case for both.
            if (subFn is PdfDictionary subDict)
            {
                // This depth's own one-element buffer rather than a `new[] { subT }` per call: the
                // sub-function reads it, clamps into ITS OWN depth's buffer, and never keeps it.
                float[] subInput = Scratch(ScratchSubInput, 1);
                subInput[0] = subT;
                return Evaluate(subDict, subInput, resolve);
            }

            return new float[] { 0f };
        }

        // ----------------------------------------------------------------
        //  Type 4 — PostScript calculator function (stack machine)
        // ----------------------------------------------------------------

        /// <summary>
        /// Evaluates a PostScript Type 4 calculator program.
        /// Procedures ({...} blocks) are pushed onto a separate procedure stack and
        /// consumed by `if` and `ifelse` operators. This matches the PostScript
        /// execution model where procedures are first-class operands.
        /// </summary>
        private static float[] EvalType4(PdfDictionary fn, float[] inputs, FnParams p)
        {
            // Reset the unknown-operator log at the start of each Type 4 evaluation.
            // Must hold across EvalPs() — if that throws, the thread-static must
            // not leak partial state to a subsequent unrelated evaluation on the
            // same thread.  EvalPs catches its own arithmetic exceptions, but
            // a deeply-nested procedure with malformed braces can still bubble
            // up; finally-clear is the defensive backstop.
            _unknownPsOps = null;
            _unknownPsOpSet = null;

            var stream = fn as PdfStream;
            if (stream == null) return inputs.Length > 0 ? new[] { inputs[0] } : new float[] { 0f };

            byte[] data;
            try { data = stream.GetDecodedData(); }
            catch { return new float[] { 0f }; }

            List<string> tokens = GetProgramTokens(data);

            // One aggregate budget for this whole Type 4 evaluation; threaded through every
            // nested loop so the PRODUCT of nested-loop iterations is bounded (a single loop's
            // MaxPsLoopIterations cap cannot do that).
            //
            // DELIBERATELY NOT POOLED OR SHARED, unlike the two objects below it: this is the DoS
            // bound, and a budget that outlived its call would let one image's cost be paid by an
            // earlier one. One `new` per evaluation is what makes MaxPsTotalOps mean anything.
            // Its parent is the scope's (SpendFrom): a page evaluates a function once a pixel, so
            // the product of evaluations and their work is the page's, not the evaluation's.
            var budget = new WorkBudget(MaxPsTotalOps, "Type 4 PostScript function", false, _sharedWork);

            // The evaluation stack and the procedure-token cache ARE per-call state, but they are
            // per-call in the sense of "must start empty", not "must be a new object" — so they are
            // rented from a per-thread slot and returned in the finally. A Type 4 tint transform
            // runs once per pixel, and allocating a Stack (plus its backing array) and a Dictionary
            // on every one of those was pure garbage. A nested evaluation — which a Type 3
            // stitching function can start — finds the slot empty and allocates its own, so the
            // "starts empty" property holds at every depth.
            //
            // CLEARED ON RENT, NOT ON RETURN, and deliberately in one place: a budget refusal
            // unwinds out of EvalPs with the loop's residue still on the stack, and §7.10.5 reads
            // the TOPMOST n values — so a program that leaves fewer values than its /Range declares
            // would otherwise return a previous evaluation's leftovers as colour components.
            // Clearing here cleans the object however it got back into the slot.
            Stack<float> stack = _psStackPool;
            if (stack == null) stack = new Stack<float>();
            else { _psStackPool = null; stack.Clear(); }

            Dictionary<string, List<string>> procCache = _procCachePool;
            if (procCache == null) procCache = new Dictionary<string, List<string>>();
            else { _procCachePool = null; procCache.Clear(); }

            var savedProcCache = _procTokenCache;
            _procTokenCache = procCache; // tokenise each proc once
            try
            {
                // Push inputs onto stack (last input on top per PDF spec). Not through the capped
                // Push: the input count is the function's declared arity (a colour's components, a
                // handful), not program-controlled, so it is bounded already; the cap guards the
                // program's own pushes, where `copy` is the amplifier.
                foreach (float v in inputs) stack.Push(v);
                try
                {
                    EvalPs(tokens, 0, tokens.Count, stack, depth: 0, budget);
                }
                catch
                {
                    _unknownPsOps = null;
                    _unknownPsOpSet = null;
                    throw;
                }

                // /Range must be read through the resolver: `fn[PdfName.Range]` is the raw indexer, so
                // an indirect `/Range 9 0 R` yielded null and the output count silently became
                // stack.Count — handing a five-component colour to a four-component space.
                float[] rngArr = p.Range;
                int n = rngArr != null ? rngArr.Length / 2 : stack.Count;

                var result = new float[n];
                // §7.10.5: the function's outputs are the TOPMOST n values left on the stack, with the
                // last output on top. Taking the bottom n instead meant any value the program did not
                // consume displaced a real output: `{ 0 0 0 1 }` — a legal constant-black tint
                // transform that ignores its input — left (t, 0, 0, 0, 1) and returned
                // DeviceCMYK(t, 0, 0, 0), a mid cyan where the file asked for solid black.
                for (int i = n - 1; i >= 0; i--)
                    result[i] = stack.Count > 0 ? stack.Pop() : 0f;
                return result;
            }
            finally
            {
                _procTokenCache = savedProcCache;
                _procCachePool = procCache;
                _psStackPool = stack;
            }
        }

        /// <summary>
        /// The budget every Type 4 evaluation on this thread also spends while a <see cref="SpendFrom"/>
        /// scope is open, or null. Not a hand-off between operators: the scope is opened and closed
        /// around the whole operation that evaluates — a page's rendering, a colour conversion, a
        /// halftone screen — on the thread that runs it.
        /// </summary>
        [ThreadStatic] private static WorkBudget _sharedWork;

        /// <summary>
        /// Opens a scope in which every Type 4 evaluation on this thread also spends
        /// <paramref name="work"/> (<see cref="PdfLimits.MaxFunctionWorkPerPage"/> is the figure its
        /// openers use): a function evaluated once a pixel or a sample spends one budget, and past it
        /// each evaluation is refused at its first operation with a
        /// <see cref="WorkBudgetExceededException"/> whose <see cref="WorkBudgetExceededException.Budget"/>
        /// is <paramref name="work"/>. Null opens nothing. Disposing restores the enclosing scope.
        /// </summary>
        internal static WorkScope SpendFrom(WorkBudget work) => new WorkScope(work);

        /// <summary>A <see cref="SpendFrom"/> scope.</summary>
        internal readonly struct WorkScope : IDisposable
        {
            private readonly WorkBudget _previous;
            private readonly bool _opened;

            internal WorkScope(WorkBudget work)
            {
                _previous = _sharedWork;
                _opened = work != null;
                if (_opened) _sharedWork = work;
            }

            public void Dispose()
            {
                if (_opened) _sharedWork = _previous;
            }
        }

        /// <summary>Per-thread rental slots for the two per-call Type 4 objects; see EvalType4.</summary>
        [ThreadStatic] private static Stack<float> _psStackPool;
        [ThreadStatic] private static Dictionary<string, List<string>> _procCachePool;

        /// <summary>
        /// The tokenised body of every Type 4 program this process has evaluated, keyed on the
        /// <b>decoded byte array instance</b>.
        /// </summary>
        /// <remarks>
        /// <para><b>Keyed on the array, not on the stream, and that is what makes it safe.</b>
        /// <see cref="PdfStream.GetDecodedData"/> memoises its result and
        /// <see cref="PdfStream.RawData"/>'s setter discards that memo, so mutating a stream
        /// yields a <i>different</i> array instance and therefore misses this cache. Keying on
        /// the <c>PdfStream</c> would hand back tokens for content it no longer holds.</para>
        /// <para>A <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
        /// so the entry dies with the array rather than pinning every function program a
        /// long-lived process ever saw; it is also safe for the concurrent renders this library
        /// runs. Same mechanism <c>IccColorTransform</c> already uses for profiles.</para>
        /// <para>Only the <i>program</i> is cached. The evaluation stack, the
        /// <see cref="WorkBudget"/> and the procedure-token cache stay per call — the budget
        /// especially, since it is the DoS bound and sharing it would let one image's cost be
        /// paid by an earlier one.</para>
        /// </remarks>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], List<string>>
            _programTokens = new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], List<string>>();

        /// <summary>
        /// Decodes and tokenises a Type 4 program, or returns the tokens from a previous call.
        /// </summary>
        /// <remarks>
        /// Run once, not on <b>every evaluation</b>: an <c>Encoding.ASCII.GetString</c> over the whole
        /// program, a <c>Trim</c>, up to two <c>Substring</c>s and a full tokenise producing one
        /// string per token. A type 1 (function-based) shading drives up to
        /// 512x512 = 262 144 evaluations of the same program, and an image transfer function ran
        /// three per pixel, so the same handful of bytes was re-tokenised millions of times.
        /// </remarks>
        private static List<string> GetProgramTokens(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<string>();
            if (_programTokens.TryGetValue(data, out var cached)) return cached;

            // Comments go first, so every procedure body cut from this text is free of them too.
            // Trimmed by PDF's white-space set, which holds NUL: a program padded with NULs after its
            // closing brace loses the padding, not the brace.
            string program = TrimWhiteSpace(WithoutComments(Encoding.ASCII.GetString(data)));
            // Strip surrounding braces if present
            if (program.StartsWith("{")) program = program.Substring(1);
            if (program.EndsWith("}"))   program = program.Substring(0, program.Length - 1);

            var tokens = Tokenize(program);

            // GetValue rather than Add: a concurrent evaluation of the same function may have
            // won the race, and Add would throw on the duplicate key.
            return _programTokens.GetValue(data, _ => tokens);
        }

        /// <summary>
        /// Removes every comment from a Type 4 program.
        /// </summary>
        /// <remarks>
        /// <para><b>§7.10.5.1 admits comments into the subset</b>, and §7.2.4 defines one: it runs
        /// from <c>%</c> up to, but not including, the end of the line, and counts as a single
        /// white-space character. A Type 4 program has no strings, so every <c>%</c> starts one.
        /// Read as code, a comment's words executed whenever they spelled an operator, and a brace
        /// inside one opened or closed a procedure.</para>
        ///
        /// <para><b>The end-of-line stays</b>, and it is the white space that separates the tokens
        /// either side of the comment. A comment that ends the text has nothing after it to
        /// separate.</para>
        /// </remarks>
        private static string WithoutComments(string program)
        {
            if (program.IndexOf('%') < 0) return program;

            var sb = new StringBuilder(program.Length);
            int i = 0;
            while (i < program.Length)
            {
                if (program[i] != '%') { sb.Append(program[i++]); continue; }
                while (i < program.Length && program[i] != '\r' && program[i] != '\n') i++;
            }
            return sb.ToString();
        }

        private static string TrimWhiteSpace(string text)
        {
            int start = 0, end = text.Length;
            while (start < end && IsWhiteSpace(text[start])) start++;
            while (end > start && IsWhiteSpace(text[end - 1])) end--;
            return text.Substring(start, end - start);
        }

        // §7.2.3's white-space set. The program was decoded as ASCII, so every char fits in a byte.
        private static bool IsWhiteSpace(char c) => PdfConstants.IsWhitespace((byte)c);

        private static List<string> Tokenize(string prog)
        {
            var tokens = new List<string>();
            int i = 0;
            while (i < prog.Length)
            {
                if (IsWhiteSpace(prog[i])) { i++; continue; }
                if (prog[i] == '}')
                {
                    // A '}' that closes nothing. The scan below stops AT a brace, so without this it
                    // would add an empty token here without advancing, forever. Kept as a token, so every
                    // evaluation reports it through LastUnknownPostScriptOps.
                    tokens.Add("}");
                    i++;
                    continue;
                }
                if (prog[i] == '{')
                {
                    // find matching }
                    int depth = 1, j = i + 1;
                    while (j < prog.Length && depth > 0)
                    {
                        if (prog[j] == '{') depth++;
                        else if (prog[j] == '}') depth--;
                        j++;
                    }
                    tokens.Add(prog.Substring(i, j - i));
                    i = j;
                }
                else
                {
                    int j = i;
                    while (j < prog.Length && !IsWhiteSpace(prog[j]) && prog[j] != '{' && prog[j] != '}') j++;
                    tokens.Add(prog.Substring(i, j - i));
                    i = j;
                }
            }
            return tokens;
        }

        /// <summary>
        /// Maximum recursion depth across nested <c>{...}</c> procedures. The
        /// cycle detector here is a depth counter, not a visited-set, because
        /// each procedure literal is a unique string token; an adversarial
        /// program like <c>{ { { ... 10 000 levels ... } } }</c> would
        /// exhaust the CLR stack before any logical bound triggered.
        ///
        /// Capped at 100 (not 1000 like the struct-tree / text-extractor
        /// caps) because each recursive level consumes two CLR frames here
        /// — <c>EvalPs</c> calls <c>ExecProc</c> calls <c>EvalPs</c> — and
        /// each frame keeps a fresh <see cref="Stack{T}"/> + tokeniser
        /// list around. A larger cap risks tripping the CLR's hard
        /// 1 MB default thread stack before our managed throw fires,
        /// turning a controllable error into an uncatchable
        /// <see cref="StackOverflowException"/>. Real Type 4 functions in
        /// PDFs almost never exceed single-digit depth.
        /// </summary>
        private const int MaxPsProcedureDepth = 100;

        /// <summary>
        /// Hard cap on iterations executed by <c>repeat</c> / <c>for</c> per
        /// call — prevents an adversarial Type 4 function from spinning the
        /// evaluator with <c>1000000000 { } repeat</c>. Real shading /
        /// transfer functions never need anything close to a million
        /// iterations.
        /// </summary>
        private const int MaxPsLoopIterations = 1_000_000;

        /// <summary>
        /// Global per-evaluation operation budget. The per-loop cap (<see cref="MaxPsLoopIterations"/>)
        /// bounds a SINGLE loop, but NESTED loops multiply: "1e6 {1e6 {1e6 {…} repeat} repeat} repeat"
        /// is 1e18 body executions at proc-depth 3 (so the depth guard never fires) — an unbounded
        /// CPU-amplification DoS reachable through a shading/transfer Type 4 function. This counts
        /// every token executed across all nesting in one EvalType4 call and aborts past the ceiling.
        /// Real functions execute a few hundred ops. Enforced via a shared <see cref="WorkBudget"/>
        /// ticked once per token, once per loop iteration, AND by procedure length on each
        /// ExecProc (which re-tokenizes its body — the dominant cost a token-only count misses),
        /// so the aggregate bound is a single reusable object rather than ad-hoc counters.
        /// Kept modest because a renderer evaluates a shading/transfer function once PER SAMPLE
        /// (e.g. 64+ times), so the effective ceiling is this budget × sample-count.
        /// </summary>
        private const long MaxPsTotalOps = 2_000_000;

        /// <summary>Whether both operands are whole numbers an <c>int</c> holds, and those numbers.</summary>
        private static bool BothIntegers(float a, float b, out int ia, out int ib)
        {
            ia = ib = 0;
            if (a != Math.Floor(a) || b != Math.Floor(b)) return false;
            if (a < int.MinValue || a > int.MaxValue || b < int.MinValue || b > int.MaxValue) return false;
            ia = SaturatingCast.ToInt(a);
            ib = SaturatingCast.ToInt(b);
            return true;
        }

        private static void EvalPs(List<string> tokens, int start, int end, Stack<float> stack, int depth, WorkBudget budget)
        {
            if (depth >= MaxPsProcedureDepth)
                throw new InvalidOperationException(
                    "PostScript Type 4 procedure depth exceeds " +
                    MaxPsProcedureDepth + "; malformed or adversarial function.");

            // Procedure stack: stores brace-delimited blocks for if/ifelse consumption. Allocated
            // only if a `{...}` literal actually appears — EvalPs runs once per evaluation AND once
            // per ExecProc (so once per loop iteration), and most bodies contain no procedure at all.
            Stack<string> procStack = null;

            for (int i = start; i < end; i++)
            {
                // Charge each token to the aggregate budget — bounds the PRODUCT of nested loops
                // (which the per-loop MaxPsLoopIterations cap cannot) across all nesting.
                budget.Tick();

                string tok = tokens[i];

                // Numeric literal
                if (float.TryParse(tok, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float fval))
                {
                    Push(stack,fval);
                    continue;
                }

                // Boolean literals
                if (tok == "true")  { Push(stack,1f); continue; }
                if (tok == "false") { Push(stack,0f); continue; }

                // Procedure literal — push onto proc stack for later consumption by if/ifelse
                if (tok.StartsWith("{"))
                {
                    (procStack ??= new Stack<string>()).Push(tok);
                    continue;
                }

                switch (tok.ToLowerInvariant())
                {
                    // ---- Arithmetic (PostScript Language Reference §8.1) ----
                    case "add":  { float b = Pop(stack), a = Pop(stack); Push(stack,a + b); break; }
                    case "sub":  { float b = Pop(stack), a = Pop(stack); Push(stack,a - b); break; }
                    case "mul":  { float b = Pop(stack), a = Pop(stack); Push(stack,a * b); break; }
                    case "div":  { float b = Pop(stack), a = Pop(stack); Push(stack,b == 0 ? 0 : a / b); break; }
                    case "idiv": { float b = Pop(stack), a = Pop(stack); Push(stack,b == 0 ? 0 : (float)SaturatingCast.ToInt(a / b)); break; }
                    case "mod":  { float b = Pop(stack), a = Pop(stack); Push(stack,b == 0 ? 0 : a % b); break; }
                    case "neg":  { Push(stack,-Pop(stack)); break; }
                    case "abs":  { Push(stack,Math.Abs(Pop(stack))); break; }
                    case "ceiling": { Push(stack,(float)Math.Ceiling(Pop(stack))); break; }
                    case "floor":   { Push(stack,(float)Math.Floor(Pop(stack)));   break; }
                    // PostScript rounds a midpoint AWAY FROM ZERO; .NET's default is to even, so
                    // `0.5 round` gave 0 instead of 1 and `2.5 round` gave 2 instead of 3.
                    case "round":   { Push(stack,(float)Math.Round(Pop(stack), MidpointRounding.AwayFromZero)); break; }
                    case "truncate":{ Push(stack,(float)Math.Truncate(Pop(stack))); break; }
                    case "sqrt":    { Push(stack,(float)Math.Sqrt(Math.Max(0, Pop(stack)))); break; }
                    case "sin":     { Push(stack,(float)Math.Sin(Pop(stack) * Math.PI / 180.0)); break; }
                    case "cos":     { Push(stack,(float)Math.Cos(Pop(stack) * Math.PI / 180.0)); break; }
                    // `num den atan` — den is on top, so it pops FIRST; named the other way round, the
                    // operands would reach Atan2 swapped and compute atan(den/num). The result must also be folded into [0, 360) per
                    // §7.10.5 and the PostScript reference; Atan2 returns (-180, 180].
                    case "atan":
                    {
                        float den = Pop(stack), num = Pop(stack);
                        double deg = Math.Atan2(num, den) * 180.0 / Math.PI;
                        if (deg < 0.0) deg += 360.0;
                        Push(stack,(float)deg);
                        break;
                    }
                    case "exp":     { float e = Pop(stack), b2 = Pop(stack); Push(stack,(float)Math.Pow(b2, e)); break; }
                    case "ln":      { Push(stack,(float)Math.Log(Math.Max(float.Epsilon, Pop(stack)))); break; }
                    case "log":     { Push(stack,(float)Math.Log10(Math.Max(float.Epsilon, Pop(stack)))); break; }

                    // ---- Type conversion (PostScript §8.1) ----
                    case "cvi": { Push(stack,(float)SaturatingCast.ToInt(Pop(stack))); break; }
                    case "cvr": break; // already float — no-op in our representation

                    // ---- Comparison ----
                    case "eq": { float b = Pop(stack), a = Pop(stack); Push(stack,a == b ? 1f : 0f); break; }
                    case "ne": { float b = Pop(stack), a = Pop(stack); Push(stack,a != b ? 1f : 0f); break; }
                    case "gt": { float b = Pop(stack), a = Pop(stack); Push(stack,a >  b ? 1f : 0f); break; }
                    case "ge": { float b = Pop(stack), a = Pop(stack); Push(stack,a >= b ? 1f : 0f); break; }
                    case "lt": { float b = Pop(stack), a = Pop(stack); Push(stack,a <  b ? 1f : 0f); break; }
                    case "le": { float b = Pop(stack), a = Pop(stack); Push(stack,a <= b ? 1f : 0f); break; }

                    // ---- Logical / bitwise ----
                    // PLRM: on integers and/or/xor/not are bitwise, on booleans logical. This stack
                    // holds floats with no type, and a boolean is 1 or 0, on which the bitwise and,
                    // or and xor agree with the logical ones — so integers take the bitwise reading.
                    // `not` is the one that differs; 0 and 1 are read as booleans (`5 not` is -6).
                    case "and":  { float b = Pop(stack), a = Pop(stack); Push(stack,BothIntegers(a, b, out int ia, out int ib) ? ia & ib : (a != 0 && b != 0) ? 1f : 0f); break; }
                    case "or":   { float b = Pop(stack), a = Pop(stack); Push(stack,BothIntegers(a, b, out int ia, out int ib) ? ia | ib : (a != 0 || b != 0) ? 1f : 0f); break; }
                    case "xor":  { float b = Pop(stack), a = Pop(stack); Push(stack,BothIntegers(a, b, out int ia, out int ib) ? ia ^ ib : ((a != 0) ^ (b != 0)) ? 1f : 0f); break; }
                    case "not":
                    {
                        float a = Pop(stack);
                        Push(stack, a == 0 ? 1f : a == 1 ? 0f : BothIntegers(a, a, out int ia, out _) ? ~ia : 0f);
                        break;
                    }
                    // PLRM bitshift: a 32-bit integer shifted left, or right with zeros coming in (a
                    // logical shift); a count of 32 or more either way shifts everything out. A long
                    // shift masked the count to six bits, so `1 64 bitshift` was 1.
                    case "bitshift":
                    {
                        float shift = Pop(stack), val = Pop(stack);
                        int si = SaturatingCast.ToInt(shift), vi = SaturatingCast.ToInt(val);
                        int shifted = si >= 32 || si <= -32 ? 0 : si >= 0 ? vi << si : (int)((uint)vi >> -si);
                        Push(stack, shifted);
                        break;
                    }

                    // ---- Stack manipulation ----
                    case "dup":  { float a = Pop(stack); Push(stack,a); Push(stack,a); break; }
                    case "pop":  { Pop(stack); break; }
                    case "exch": { float b = Pop(stack), a = Pop(stack); Push(stack,b); Push(stack,a); break; }
                    case "copy": {
                        int cn = SaturatingCast.ToInt(Pop(stack));
                        var items = new List<float>(stack);
                        for (int j = Math.Min(cn, items.Count) - 1; j >= 0; j--) Push(stack,items[j]);
                        break;
                    }
                    case "index": {
                        int ni = SaturatingCast.ToInt(Pop(stack));
                        var items = new List<float>(stack);
                        Push(stack,ni >= 0 && ni < items.Count ? items[ni] : 0f);
                        break;
                    }
                    case "roll": {
                        // PostScript spells this `n j roll` (§7.10.5 / PLRM "roll"), so j is pushed
                        // LAST and therefore pops FIRST. This was written as
                        // `int rn = (int)Pop(stack), rj = (int)Pop(stack);` — C# evaluates
                        // declarators left to right, so rn received j and rj received n, and the
                        // two were then used as the count and the shift respectively.
                        //
                        // The direction was inverted too: positive j moves each element of the
                        // segment one place TOWARD THE TOP, with the top element wrapping round to
                        // the bottom. The reference cases, hand-computed from the operator
                        // definition: `1 2 3  3 1 roll` → `3 1 2`, `1 2 3  3 -1 roll` → `2 3 1`.
                        // Plugins.PostScript's PsStackOps.Roll implements the same two.
                        int rj = SaturatingCast.ToInt(Pop(stack));
                        int rn = SaturatingCast.ToInt(Pop(stack));
                        if (rn > 0 && stack.Count >= rn)
                        {
                            // seg[0] is the TOP of the segment (Pop order); seg[rn-1] its bottom.
                            var seg = new List<float>();
                            for (int x = 0; x < rn; x++) seg.Add(Pop(stack));
                            int r = ((rj % rn) + rn) % rn;
                            // "One place toward the top, top wraps to the bottom" is a LEFT
                            // rotation of this top-first list.
                            for (int x = 0; x < r; x++) { seg.Add(seg[0]); seg.RemoveAt(0); }
                            for (int x = rn - 1; x >= 0; x--) Push(stack,seg[x]);
                        }
                        break;
                    }

                    // ---- Control flow (PostScript §8.3) ----
                    // In PostScript: bool {proc} if
                    //                bool {trueProc} {falseProc} ifelse
                    // Procedures were already pushed onto procStack above.
                    case "if": {
                        string proc = procStack != null && procStack.Count > 0 ? procStack.Pop() : null;
                        float cond = Pop(stack);
                        if (cond != 0 && proc != null)
                            ExecProc(proc, stack, depth + 1, budget);
                        break;
                    }
                    case "ifelse": {
                        string falseProc = procStack != null && procStack.Count > 0 ? procStack.Pop() : null;
                        string trueProc  = procStack != null && procStack.Count > 0 ? procStack.Pop() : null;
                        float cond = Pop(stack);
                        string chosen = cond != 0 ? trueProc : falseProc;
                        if (chosen != null)
                            ExecProc(chosen, stack, depth + 1, budget);
                        break;
                    }

                    // ---- Iteration (PostScript §8.2) ----
                    // PostScript: count {proc} repeat
                    //             init incr limit {proc} for
                    case "repeat": {
                        string proc = procStack != null && procStack.Count > 0 ? procStack.Pop() : null;
                        int count = SaturatingCast.ToInt(Pop(stack));
                        if (proc != null && count > 0)
                        {
                            int cap = count > MaxPsLoopIterations ? MaxPsLoopIterations : count;
                            for (int n = 0; n < cap; n++)
                            {
                                // Charge each iteration so an empty-body innermost loop (0 tokens)
                                // still counts toward the aggregate nested-loop bound.
                                budget.Tick();
                                ExecProc(proc, stack, depth + 1, budget);
                            }
                        }
                        break;
                    }
                    case "for": {
                        string proc = procStack != null && procStack.Count > 0 ? procStack.Pop() : null;
                        float limit = Pop(stack);
                        float incr  = Pop(stack);
                        float init  = Pop(stack);
                        if (proc != null && incr != 0)
                        {
                            int iterations = 0;
                            for (float v = init;
                                 incr > 0 ? v <= limit : v >= limit;
                                 v += incr)
                            {
                                if (++iterations > MaxPsLoopIterations) break;
                                budget.Tick();
                                Push(stack,v);
                                ExecProc(proc, stack, depth + 1, budget);
                            }
                        }
                        break;
                    }
                    case "bind":
                        // PostScript "bind" substitutes operator references
                        // with their operator values inside a procedure as a
                        // performance optimisation.  Our evaluator dispatches
                        // by name at exec time, so this is effectively a
                        // no-op; we recognise the token explicitly to keep
                        // it out of the unknown-operator diagnostic.
                        break;

                    default:
                        // Track the unknown operator so callers can audit which
                        // PostScript instructions the function relied on that the
                        // evaluator does not have.
                        if (_unknownPsOps == null) { _unknownPsOps = new List<string>(); _unknownPsOpSet = new HashSet<string>(StringComparer.Ordinal); }
                        if (_unknownPsOpSet.Add(tok)) _unknownPsOps.Add(tok);
                        // A word that is no operator is the number 0, as other readers' lexers read it
                        // and push it; skipped, the stack unchanged, a program's later operators
                        // would take operands the producer did not mean. A `}`
                        // closing nothing is structure, not a word, and stays skipped.
                        if (tok != "}") Push(stack,0f);
                        break;
                }
            }
        }

        // Per-evaluation cache of tokenised procedure blocks. ExecProc is the hot path under
        // nested loops and re-Tokenizes its body on EVERY call, so an if-recursion inside a 1e6
        // repeat re-tokenizes large bodies millions of times — a super-linear cost that a simple
        // op count cannot bound and that a length charge cannot bound without also tripping on
        // deeply-nested (non-loop) input. Caching makes each distinct proc tokenize once, so the
        // WorkBudget's per-token / per-iteration ticks then bound total work reliably.
        [ThreadStatic] private static Dictionary<string, List<string>> _procTokenCache;

        /// <summary>
        /// Maximum number of distinct procedure bodies retained by <see cref="_procTokenCache"/>.
        /// The number of distinct <c>{...}</c> literals is already bounded by the program text,
        /// so this is a defensive ceiling on cache memory rather than a correctness bound: past
        /// it, further procedures still execute, they are just re-tokenised each time.
        /// </summary>
        private const int MaxCachedProcedures = 8192;

        /// <summary>Strips braces from a procedure block and evaluates it.</summary>
        private static void ExecProc(string proc, Stack<float> stack, int depth, WorkBudget budget)
        {
            var cache = _procTokenCache;
            List<string> inner;
            if (cache != null && cache.TryGetValue(proc, out inner)) { /* reuse cached tokens */ }
            else
            {
                string body = proc.Trim();
                if (body.StartsWith("{")) body = body.Substring(1);
                if (body.EndsWith("}"))   body = body.Substring(0, body.Length - 1);
                inner = Tokenize(body);
                // Bound the cache by entry count (distinct procs ≤ program size, but cap defensively).
                if (cache != null && cache.Count < MaxCachedProcedures) cache[proc] = inner;
            }
            EvalPs(inner, 0, inner.Count, stack, depth, budget);
        }

        /// <summary>
        /// ISO 32000-1 §7.10.5 caps a Type 4 function's operand stack at 100 entries. <c>copy</c>
        /// pushes the top n values again and <c>for</c> pushes a value an iteration, so without a
        /// bound a few dozen <c>copy</c> tokens grow the stack exponentially — 2^30 floats from a few
        /// hundred bytes, one budget tick a token (the per-token budget cannot see a single token
        /// that allocates 2^k). A push past the cap is the §7.10.5 <c>rangecheck</c>: the evaluation
        /// is refused as the same resource-exhaustion failure the work budget raises, so the caller
        /// reports it and the function fails, as any other Type 4 error does.
        /// </summary>
        private const int MaxPsOperandStackDepth = 100;

        /// <summary>Pushes onto the operand stack, refusing past <see cref="MaxPsOperandStackDepth"/>.</summary>
        private static void Push(Stack<float> s, float value)
        {
            if (s.Count >= MaxPsOperandStackDepth)
                throw new WorkBudgetExceededException(
                    "PostScript Type 4 operand stack exceeds " + MaxPsOperandStackDepth +
                    " entries (ISO 32000-1 §7.10.5); malformed or adversarial function.");
            s.Push(value);
        }

        private static float Pop(Stack<float> s) => s.Count > 0 ? s.Pop() : 0f;

        // ----------------------------------------------------------------
        //  Domain / Range clamping helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// The inputs clamped to <c>/Domain</c>, in this depth's scratch buffer — <b>never</b> the
        /// caller's array, which the render loops reuse across a whole grid and must not see
        /// modified. Returns <paramref name="inputs"/> itself when there is no <c>/Domain</c>,
        /// exactly as before.
        /// </summary>
        private static float[] ClampToDomain(float[] inputs, float[] domArr)
        {
            if (domArr == null) return inputs;
            float[] result = Scratch(ScratchDomain, inputs.Length);
            for (int i = 0; i < inputs.Length; i++)
            {
                float dmin = At(domArr, i * 2);
                float dmax = domArr.Length > i * 2 + 1 ? domArr[i * 2 + 1] : 1f;
                result[i] = Clamp(inputs[i], dmin, dmax);
            }
            return result;
        }

        /// <summary>
        /// Clamps <paramref name="outputs"/> to <c>/Range</c> <b>in place</b>. Safe because the only
        /// caller passes an array that same <c>Evaluate</c> call has just produced.
        /// </summary>
        private static void ClampToRange(float[] outputs, float[] rngArr)
        {
            if (rngArr == null) return;
            for (int i = 0; i < outputs.Length; i++)
            {
                float rmin = rngArr.Length > i * 2     ? rngArr[i * 2]     : 0f;
                float rmax = rngArr.Length > i * 2 + 1 ? rngArr[i * 2 + 1] : 1f;
                outputs[i] = Clamp(outputs[i], rmin, rmax);
            }
        }

        private static float GetArrFloat(PdfArray arr, int idx)
            => idx < arr.Count ? (float)((arr[idx] as PdfNumber)?.DoubleValue ?? 0.0) : 0f;

        /// <summary>
        /// Clamps to [<paramref name="min"/>, <paramref name="max"/>], substituting
        /// <paramref name="min"/> for NaN and recording the substitution.
        /// </summary>
        /// <remarks>
        /// `v &lt; min ? min : v > max ? max : v` returns v unchanged for NaN, because BOTH
        /// comparisons are false — and infinity is handled correctly by the same expression, which
        /// is what made the gap easy to miss. ClampToRange is the last thing every Evaluate path
        /// runs, so a NaN escaped the evaluator despite a /Range being declared, reached
        /// PdfColor.Channel, and Math.Round(NaN) cast to 0: a black band indistinguishable from a
        /// legitimately black stop. `{ -8 0.5 exp }` and a Type 2 function with a fractional /N at
        /// a negative t both produce one; sqrt, ln and log are guarded two lines apart, exp and the
        /// Type 2 exponent were not.
        /// </remarks>
        private static float Clamp(float v, float min, float max)
        {
            if (float.IsNaN(v))
            {
                _lastType0Failure ??=
                    "A function produced NaN (an out-of-domain exp or a fractional /N at a " +
                    "negative input); the /Range minimum was substituted.";
                return min;
            }
            return v < min ? min : v > max ? max : v;
        }
    }
}
