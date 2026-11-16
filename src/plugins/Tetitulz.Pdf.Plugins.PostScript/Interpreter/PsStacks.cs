// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// The operand stack.
    ///
    /// <para><b>Underflow raises a catchable PostScript error, it does not return zero.</b>
    /// <c>Core.Functions.PdfFunctionEvaluator.Pop</c> yields <c>0f</c> on an empty stack, which is
    /// right for the closed Type 4 subset — a malformed function should still produce a colour. It
    /// is wrong here: <c>{ pop } stopped</c> is how a real program probes what it may do, and
    /// silently substituting 0 turns a broken program into a differently-broken drawing rather than
    /// one the author's own error handling can see.</para>
    /// </summary>
    internal sealed class PsOperandStack
    {
        private readonly List<PsObject> _items = new();
        private readonly int _limit;

        public PsOperandStack(int limit = -1) => _limit = limit < 0 ? PsLimits.MaxOperandStackDepth : limit;

        public int Count => _items.Count;

        public void Push(PsObject o)
        {
            if (_items.Count >= _limit)
                throw new PsErrorException(PsErrorKind.StackOverflow, detail:
                    $"operand stack exceeded {_limit} entries");
            _items.Add(o);
        }

        public PsObject Pop()
        {
            if (_items.Count == 0)
                throw new PsErrorException(PsErrorKind.StackUnderflow);
            var o = _items[_items.Count - 1];
            _items.RemoveAt(_items.Count - 1);
            return o;
        }

        /// <summary>The object <paramref name="fromTop"/> below the top; 0 is the top itself.</summary>
        public PsObject Peek(int fromTop = 0)
        {
            if (fromTop < 0 || fromTop >= _items.Count)
                throw new PsErrorException(PsErrorKind.StackUnderflow);
            return _items[_items.Count - 1 - fromTop];
        }

        public void Set(int fromTop, PsObject value)
        {
            if (fromTop < 0 || fromTop >= _items.Count)
                throw new PsErrorException(PsErrorKind.StackUnderflow);
            _items[_items.Count - 1 - fromTop] = value;
        }

        public void Clear() => _items.Clear();

        /// <summary>
        /// Discards down to <paramref name="depth"/>. Used by the top-level error recovery to put
        /// the stack back where it was before the object that failed.
        /// </summary>
        public void TruncateTo(int depth)
        {
            if (depth < 0) depth = 0;
            if (depth < _items.Count) _items.RemoveRange(depth, _items.Count - depth);
        }

        // ---- typed accessors: one place where a wrong type becomes a typecheck ----

        public double PopNumber(string op)
        {
            var o = Pop();
            if (!o.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.Num;
        }

        public int PopInt(string op)
        {
            var o = Pop();
            // PLRM: an operator specified to take an integer accepts only an integer. `5.0 2 idiv`
            // is a typecheck, and programs rely on that to distinguish the two.
            if (o.Type != PsType.Integer) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsInt();
        }

        public bool PopBool(string op)
        {
            var o = Pop();
            if (o.Type != PsType.Boolean) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsBool();
        }

        public PsArray PopArray(string op)
        {
            var o = Pop();
            if (o.Type != PsType.Array) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsArray();
        }

        public PsObject PopProcedure(string op)
        {
            var o = Pop();
            if (o.Type != PsType.Array || !o.IsExecutable)
                throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o;
        }

        public PsString PopString(string op)
        {
            var o = Pop();
            if (o.Type != PsType.String) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsString();
        }

        public PsDict PopDict(string op)
        {
            var o = Pop();
            if (o.Type != PsType.Dictionary) throw new PsErrorException(PsErrorKind.TypeCheck, op);
            return o.AsDict();
        }

        /// <summary>Snapshot for diagnostics and for <c>count</c>-style operators.</summary>
        public IReadOnlyList<PsObject> Items => _items;
    }

    /// <summary>
    /// The dictionary stack: <c>systemdict</c> at the bottom, then <c>globaldict</c> and
    /// <c>userdict</c>, then whatever the program pushed with <c>begin</c>.
    ///
    /// <para>Name lookup walks it top-down, which is the entire mechanism by which an EPS prologue's
    /// two hundred <c>/xyz {…} def</c> definitions become callable. Without a dictionary stack
    /// nothing after the prologue executes at all — the reason
    /// <c>Core.Functions.PdfFunctionEvaluator</c> cannot be extended into a PostScript
    /// interpreter.</para>
    /// </summary>
    internal sealed class PsDictStack
    {
        private readonly List<PsDict> _stack = new();
        private readonly int _limit;

        public PsDictStack(PsDict systemDict, PsDict globalDict, PsDict userDict,
                           int limit = -1)
        {
            _limit = limit < 0 ? PsLimits.MaxDictStackDepth : limit;
            SystemDict = systemDict;
            GlobalDict = globalDict;
            UserDict   = userDict;
            _stack.Add(systemDict);
            _stack.Add(globalDict);
            _stack.Add(userDict);
        }

        public PsDict SystemDict { get; }
        public PsDict GlobalDict { get; }
        public PsDict UserDict { get; }

        /// <summary>
        /// The resources the program has defined, a dictionary of instances a category (PLRM §3.9),
        /// kept with the dictionaries so a child interpreter that shares them shares these.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, PsDict> Resources { get; } = new(System.StringComparer.Ordinal);

        public int Count => _stack.Count;

        /// <summary>The dictionary <c>def</c> writes into: the top of the stack.</summary>
        public PsDict Current => _stack[_stack.Count - 1];

        public void Begin(PsDict d)
        {
            if (_stack.Count >= _limit)
                throw new PsErrorException(PsErrorKind.DictStackOverflow, "begin");
            _stack.Add(d);
        }

        public void End()
        {
            // The bottom three are permanent; `end` past them is an error, not a silent no-op.
            if (_stack.Count <= 3)
                throw new PsErrorException(PsErrorKind.DictStackUnderflow, "end");
            _stack.RemoveAt(_stack.Count - 1);
        }

        /// <summary>Truncates to <paramref name="depth"/>, never below the permanent three.</summary>
        public void TruncateTo(int depth)
        {
            if (depth < 3) depth = 3;
            if (depth < _stack.Count) _stack.RemoveRange(depth, _stack.Count - depth);
        }

        /// <summary>The dictionary at <paramref name="index"/>, counting from the bottom (<c>dictstack</c>).</summary>
        public PsDict this[int index] => _stack[index];

        /// <summary>Top-down lookup — the innermost definition of a name wins.</summary>
        public bool TryLookup(PsObject key, out PsObject value)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
                if (_stack[i].TryGet(key, out value)) return true;
            value = PsObject.Null;
            return false;
        }

        /// <summary>The dictionary in which <paramref name="key"/> is defined, or null (<c>where</c>).</summary>
        public PsDict Where(PsObject key)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
                if (_stack[i].Contains(key)) return _stack[i];
            return null;
        }
    }

    /// <summary>What an execution-stack frame is doing.</summary>
    internal enum PsFrameKind
    {
        /// <summary>Executing the elements of a procedure in order.</summary>
        Procedure,

        /// <summary><c>for</c>: push the control variable, run the body, step.</summary>
        For,

        /// <summary><c>repeat</c>: run the body a fixed number of times.</summary>
        Repeat,

        /// <summary><c>loop</c>: run the body until <c>exit</c>.</summary>
        Loop,

        /// <summary><c>forall</c> over an array.</summary>
        ForAllArray,

        /// <summary><c>forall</c> over a string.</summary>
        ForAllString,

        /// <summary><c>forall</c> over a dictionary.</summary>
        ForAllDict,

        /// <summary><c>stopped</c>: catches a <c>stop</c> or a raised error.</summary>
        Stopped,

        /// <summary>
        /// An operator that calls a procedure per item — <c>pathforall</c>, <c>kshow</c>,
        /// <c>cshow</c>: <see cref="PsFrame.Advance"/> does the item's own work and names the
        /// procedure to run for it, or null when there are no more. A loop context, as PLRM makes
        /// each of them for <c>exit</c>.
        /// </summary>
        Callback
    }

    /// <summary>
    /// One execution-stack frame.
    ///
    /// <para>These live on the <b>heap</b>, not the CLR stack. That is the whole design: it means
    /// <see cref="PsLimits.MaxExecStackDepth"/> bounds memory rather than being pinned to whatever
    /// the thread's 1 MB stack allows, and it means <c>exit</c> / <c>stop</c> unwind by popping
    /// frames in a loop instead of throwing through CLR frames.</para>
    /// </summary>
    internal sealed class PsFrame
    {
        public PsFrameKind Kind;

        /// <summary>The procedure being executed, for every kind except the forall-over-dict.</summary>
        public PsArray Body;

        /// <summary>Index of the next element of <see cref="Body"/> to execute.</summary>
        public int Index;

        // --- loop control ---
        public double ControlValue;      // for: current value
        public double Increment;         // for: step
        public double Limit;             // for: inclusive limit
        public int RemainingIterations;  // repeat

        /// <summary>
        /// True when the <c>for</c> control value must be an integer — i.e. the INITIAL VALUE and
        /// the INCREMENT operands both were. Carried from the operator rather than re-derived from
        /// the running value, which cannot distinguish 1.0 from 1.
        /// </summary>
        public bool IntegerControl;

        // --- forall ---
        public PsArray  IterArray;
        public PsString IterString;
        public List<KeyValuePair<PsObject, PsObject>> IterDict;
        public int IterIndex;
        public PsObject IterProc;

        /// <summary>
        /// A <see cref="PsFrameKind.Callback"/> frame's next item: run when the previous procedure has
        /// finished (and at the start), it does the operator's own work for the item — pushing a
        /// segment's coordinates, showing a glyph — and answers the procedure to run, or null to end.
        /// The step loop calls it, so a procedure runs as any body does and nothing re-enters the loop.
        /// </summary>
        public Func<PsInterpreter, PsArray> Advance;

        // --- stopped ---
        /// <summary>Operand-stack depth on entry, so an error unwind restores it.</summary>
        public int OperandDepthOnEntry;

        /// <summary>Dictionary-stack depth on entry, so an error unwind restores it.</summary>
        public int DictDepthOnEntry;
    }

    /// <summary>The execution stack.</summary>
    internal sealed class PsExecStack
    {
        private readonly List<PsFrame> _frames = new();
        private readonly int _limit;

        public PsExecStack(int limit = -1) => _limit = limit < 0 ? PsLimits.MaxExecStackDepth : limit;

        public int Count => _frames.Count;
        public PsFrame Top => _frames[_frames.Count - 1];
        public PsFrame this[int i] => _frames[i];

        public void Push(PsFrame f)
        {
            if (_frames.Count >= _limit)
                throw new PsErrorException(PsErrorKind.ExecStackOverflow, detail:
                    $"execution stack exceeded {_limit} frames; a procedure is probably recursive");
            _frames.Add(f);
        }

        public void Pop() => _frames.RemoveAt(_frames.Count - 1);

        public void Clear() => _frames.Clear();

        /// <summary>
        /// Index of the innermost frame of one of the loop kinds, or -1 — <c>exit</c>'s target.
        /// </summary>
        public int FindInnermostLoop()
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                switch (_frames[i].Kind)
                {
                    case PsFrameKind.For:
                    case PsFrameKind.Repeat:
                    case PsFrameKind.Loop:
                    case PsFrameKind.ForAllArray:
                    case PsFrameKind.ForAllString:
                    case PsFrameKind.ForAllDict:
                    case PsFrameKind.Callback:
                        return i;
                    case PsFrameKind.Stopped:
                        // PLRM: `exit` may not escape a `stopped` context.
                        return -1;
                }
            }
            return -1;
        }

        /// <summary>Index of the innermost <see cref="PsFrameKind.Stopped"/> frame, or -1.</summary>
        public int FindInnermostStopped()
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
                if (_frames[i].Kind == PsFrameKind.Stopped) return i;
            return -1;
        }

        /// <summary>Discards every frame from <paramref name="index"/> upward, inclusive.</summary>
        public void TruncateFrom(int index)
        {
            if (index < 0) index = 0;
            if (index < _frames.Count) _frames.RemoveRange(index, _frames.Count - index);
        }
    }
}
