// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Control-flow operators (PLRM §8.4).
    ///
    /// <para>None of these recurses: each pushes a <see cref="PsFrame"/> and lets the interpreter's
    /// step loop drive it. That is what lets <c>exit</c> and <c>stop</c> unwind by popping frames,
    /// and what keeps <c>/f { f } def f</c> inside a heap limit instead of the CLR stack.</para>
    /// </summary>
    internal static class PsControlOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("exec", Exec);
            op("if", If);
            op("ifelse", IfElse);
            op("for", For);
            op("repeat", Repeat);
            op("loop", Loop);
            op("forall", ForAll);
            op("exit", i => i.ExitRequested = true);
            op("stop", i => i.StopRequested = true);
            op("stopped", Stopped);
            op("countexecstack", i => i.Operands.Push(PsObject.Int(i.ExecStack.Count)));
            op("execstack", ExecStack);

            // `packedarray` builds an ordinary array here (see PsArrayStringOps), so the packing
            // MODE is a flag with no observable consequence — but a prologue that sets it and reads
            // it back must see what it wrote, or it takes the wrong branch.
            op("setpacking", i => i.PackingMode = i.Operands.PopBool("setpacking"));
            op("currentpacking", i => i.Operands.Push(PsObject.Bool(i.PackingMode)));

            // `quit` ends execution. In a fragment it means "the program is done", not "exit the
            // interpreter", so it is a clean stop rather than an error.
            op("quit", i => i.QuitRequested = true);

            // `bind` resolves operator names inside a procedure at bind time — which IS observable
            // here. See Bind.
            op("bind", Bind);
        }

        /// <summary>
        /// <c>exec</c> is one of only two ways a procedure actually runs (the other is being the
        /// value an executable name resolves to) — everywhere else a procedure literal is pushed.
        /// </summary>
        private static void Exec(PsInterpreter i)
        {
            var o = i.Operands.Pop();

            // `exec` on a literal just pushes it back: executing a literal IS pushing it.
            if (!o.IsExecutable) { i.Operands.Push(o); return; }

            // An executable name reaching exec must be looked up, which Execute does.
            if (o.Type == PsType.Name) { i.Execute(o); return; }

            i.ExecuteResolvedValue(o);
        }

        private static void If(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("if");
            bool condition = i.Operands.PopBool("if");
            if (condition) i.PushProcedureFrame(proc.AsArray());
        }

        private static void IfElse(PsInterpreter i)
        {
            var elseProc = i.Operands.PopProcedure("ifelse");
            var thenProc = i.Operands.PopProcedure("ifelse");
            bool condition = i.Operands.PopBool("ifelse");
            i.PushProcedureFrame((condition ? thenProc : elseProc).AsArray());
        }

        /// <summary><c>initial increment limit proc for</c>.</summary>
        private static void For(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("for");
            double limit = i.Operands.PopNumber("for");

            // The OPERAND types decide whether the control value is an integer (PLRM), so they are
            // read here and carried on the frame. Re-deriving it downstream from the running value
            // cannot tell 1.0 from 1.
            var incrementObj = i.Operands.Peek();
            double increment = i.Operands.PopNumber("for");
            var initialObj   = i.Operands.Peek();
            double initial   = i.Operands.PopNumber("for");

            bool integerControl = initialObj.Type == PsType.Integer
                                  && incrementObj.Type == PsType.Integer;

            // A zero increment with a reachable limit never terminates on its own. The op budget
            // would eventually stop it, but burning 20 million steps on an obvious typo is worse
            // than saying so.
            if (increment == 0)
            {
                PostScriptDiagnostics.Report("PsControlOps.For",
                    "'for' with a zero increment would never terminate; it was skipped");
                return;
            }

            i.PushForFrame(initial, increment, limit, proc.AsArray(), integerControl);
        }

        private static void Repeat(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("repeat");
            int count = i.Operands.PopInt("repeat");

            if (count < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "repeat");
            if (count == 0) return;

            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Repeat,
                Body = proc.AsArray(),
                Index = 0,
                RemainingIterations = count
            });
        }

        private static void Loop(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("loop");
            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Loop,
                Body = proc.AsArray(),
                Index = 0
            });
        }

        /// <summary>
        /// <c>forall</c> over an array, a string or a dictionary. The dictionary form snapshots its
        /// entries first: the body may legally <c>put</c> into the dictionary it is walking, and
        /// enumerating a live collection while it mutates throws in .NET.
        /// </summary>
        private static void ForAll(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("forall");
            var target = i.Operands.Pop();

            switch (target.Type)
            {
                case PsType.Array:
                    i.ExecStack.Push(new PsFrame
                    {
                        Kind = PsFrameKind.ForAllArray,
                        Body = proc.AsArray(),
                        Index = int.MaxValue,      // force the first iteration to push an element
                        IterArray = target.AsArray(),
                        IterIndex = 0
                    });
                    return;

                case PsType.String:
                    i.ExecStack.Push(new PsFrame
                    {
                        Kind = PsFrameKind.ForAllString,
                        Body = proc.AsArray(),
                        Index = int.MaxValue,
                        IterString = target.AsString(),
                        IterIndex = 0
                    });
                    return;

                case PsType.Dictionary:
                {
                    var snapshot = new List<KeyValuePair<PsObject, PsObject>>(target.AsDict().Count);
                    foreach (var kv in target.AsDict().Entries) snapshot.Add(kv);
                    i.Tick(snapshot.Count);

                    i.ExecStack.Push(new PsFrame
                    {
                        Kind = PsFrameKind.ForAllDict,
                        Body = proc.AsArray(),
                        Index = int.MaxValue,
                        IterDict = snapshot,
                        IterIndex = 0
                    });
                    return;
                }

                default:
                    throw new PsErrorException(PsErrorKind.TypeCheck, "forall");
            }
        }

        /// <summary>
        /// <c>proc stopped bool</c> — runs proc, pushing false if it completed and true if a
        /// <c>stop</c> or an error unwound out of it.
        ///
        /// <para>The false is pushed by a sentinel appended after the body, so it happens when the
        /// frame finishes normally and not when the frame is discarded by an unwind. Recording the
        /// stack depths lets the unwind restore them, which is what makes
        /// <c>{ 1 2 3 badop } stopped</c> leave the stack as the author would expect.</para>
        /// </summary>
        private static void Stopped(PsInterpreter i)
        {
            var proc = i.Operands.PopProcedure("stopped");
            var body = proc.AsArray();

            // Body + a trailing operator that pushes false. Building one array keeps the frame
            // kinds simple: the step loop needs no special "finished normally" hook.
            var items = new PsObject[body.Length + 1];
            for (int k = 0; k < body.Length; k++) items[k] = body[k];
            items[body.Length] = PsObject.Op(PushFalseOperator);

            i.ChargeAllocation(items.Length * PsAllocationBudget.BytesPerArrayElement, "stopped");

            i.ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Stopped,
                Body = new PsArray(items),
                Index = 0,
                OperandDepthOnEntry = i.Operands.Count,
                DictDepthOnEntry = i.Dicts.Count
            });
        }

        private static readonly PsOperator PushFalseOperator =
            new("--stopped-completed--", o => ((PsInterpreter)o).Operands.Push(PsObject.False));

        /// <summary>
        /// <c>array execstack subarray</c> — stores the execution stack bottom-first into the
        /// caller's array and pushes the populated prefix.
        ///
        /// <para><b>A <c>stopped</c> frame's body is deliberately NOT handed out.</b>
        /// <see cref="Stopped"/> builds that frame's body as the program's procedure plus a private
        /// sentinel operator that pushes <c>false</c>. Exposing the array would let a program run
        /// the sentinel (<c>execstack 0 get exec</c> pushing a spurious <c>false</c>) or
        /// <c>put</c> through the alias and corrupt a live frame. Those frames report as null,
        /// which is also closer to the truth: what is executing there is not the array the program
        /// wrote.</para>
        /// </summary>
        private static void ExecStack(PsInterpreter i)
        {
            var target = i.Operands.PopArray("execstack");
            int depth = i.ExecStack.Count;

            if (target.Length < depth)
                throw new PsErrorException(PsErrorKind.RangeCheck, "execstack",
                    $"the {target.Length}-element array cannot hold {depth} frames");

            i.Tick(depth);
            for (int k = 0; k < depth; k++)
            {
                var frame = i.ExecStack[k];
                target[k] = frame.Kind == PsFrameKind.Stopped || frame.Body == null
                    ? PsObject.Null
                    : PsObject.Proc(frame.Body);
            }

            i.Operands.Push(PsObject.Arr(target.Interval(0, depth)));
        }

        /// <summary>
        /// <c>bind</c> — replaces every executable name in the procedure that currently resolves to
        /// an <b>operator</b> with the operator object itself, in place, recursing into nested
        /// procedures. The procedure stays on the stack: <c>bind</c>'s contract is proc → proc.
        ///
        /// <para><b>It is observable, so it is not a no-op.</b> <c>PsDictOps.Def</c> writes into
        /// <c>Dicts.Current</c> — userdict, or whatever <c>begin</c> pushed — and
        /// <c>PsDictStack.TryLookup</c> walks the stack top-down, so a <c>def</c> of an operator's
        /// name shadows systemdict for every later lookup. Unbound, a procedure would pick the
        /// redefinition up, where a conforming interpreter keeps the operator it bound:</para>
        ///
        /// <code>/draw { moveto } bind def  /moveto { pop pop } def  100 100 draw
        ///                                   200 200 lineto stroke</code>
        ///
        /// <para>would draw nothing at all — <c>draw</c> would resolve <c>moveto</c> at call time to
        /// <c>{ pop pop }</c>, so no current point would be established and <c>lineto</c> would raise
        /// <c>undefined</c>, abandoning the whole top-level object. Shadowing a painting operator
        /// and reaching the original through a bound procedure is a common idiom in generated
        /// PostScript.</para>
        ///
        /// <para>Only names resolving to an <b>operator</b> are substituted, per PLRM: a name
        /// resolving to a procedure stays a name, so a program that redefines one of its own
        /// procedures after binding still sees the new definition — which is also what PostScript
        /// does. A name that resolves to nothing stays a name too, so <c>bind</c> never turns a
        /// forward reference into an error at bind time.</para>
        ///
        /// <para>Bounded like every other walk in this interpreter: <c>Tick(body.Length)</c> per
        /// procedure charges the traversal, and the nesting cap stops a procedure that reaches
        /// itself through <c>getinterval</c> aliasing from recursing forever.</para>
        /// </summary>
        private static void Bind(PsInterpreter i)
        {
            var o = i.Operands.Peek();
            if (o.Type != PsType.Array) throw new PsErrorException(PsErrorKind.TypeCheck, "bind");

            BindProcedure(i, o.AsArray(), depth: 0);
        }

        private static void BindProcedure(PsInterpreter i, PsArray body, int depth)
        {
            if (body == null) return;

            if (depth >= PsLimits.MaxParseNestingDepth)
            {
                // Not silent: past this depth the inner procedures keep their names, so a later
                // redefinition WILL reach them — the behaviour this operator exists to prevent.
                // One-shot, because the guard fires once per branch of a wide adversarial graph.
                if (!_bindDepthReported)
                {
                    _bindDepthReported = true;
                    PostScriptDiagnostics.Report("PsControlOps",
                        $"'bind' stopped at {PsLimits.MaxParseNestingDepth} levels of nesting; " +
                        "procedures below that keep their operator names and will see a later " +
                        "redefinition. A procedure nested this deeply is normally one that " +
                        "contains itself. Reported once per thread.");
                }
                return;
            }

            // Charged before the walk, as PushProcedure does: a deeply nested procedure literal
            // must cost the budget its whole size, not one tick per level reached.
            i.Tick(body.Length);

            for (int k = 0; k < body.Length; k++)
            {
                var item = body[k];

                if (item.Type == PsType.Array && item.IsExecutable)
                {
                    BindProcedure(i, item.AsArray(), depth + 1);
                    continue;
                }

                if (item.Type != PsType.Name || !item.IsExecutable) continue;

                i.TickKey(item, i.Dicts.Count);
                if (i.Dicts.TryLookup(PsObject.LitName(item.AsName()), out var value) &&
                    value.Type == PsType.Operator)
                    body[k] = value;
            }
        }

        /// <summary>
        /// The bind-depth one-shot. <c>[ThreadStatic]</c> as this repo requires of every such slot,
        /// and cleared per conversion by <see cref="ResetPerConversionState"/> — per-thread storage
        /// is not per-document scope, so without the reset the second conversion on a thread would
        /// silently drop a report the first one had already made.
        /// </summary>
        [ThreadStatic] private static bool _bindDepthReported;

        internal static void ResetPerConversionState() => _bindDepthReported = false;
    }
}
