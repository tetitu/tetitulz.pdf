// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// Turns tokens into <see cref="PsObject"/>s, assembling <c>{procedures}</c> as it goes.
    ///
    /// <para><b>Iterative, with an explicit builder stack.</b> Recursive descent would put a CLR
    /// frame on the stack per nesting level, so a file of 100 000 <c>{</c> characters would
    /// overflow it — an uncatchable failure that takes the whole process down.
    /// <see cref="PsLimits.MaxParseNestingDepth"/> then bounds the builder list itself.</para>
    ///
    /// <para><b>Procedures are parsed once</b> into a <see cref="PsArray"/> and executed from that
    /// form for ever after. <c>Core.Functions.PdfFunctionEvaluator</c> keeps procedures as
    /// <i>strings</i> and re-tokenises them on every call, which is why it needs a
    /// <c>[ThreadStatic]</c> procedure cache; here that cost does not arise, so neither does the
    /// cache.</para>
    ///
    /// <para><c>[</c> and <c>]</c> are deliberately <b>not</b> handled here. In PostScript they are
    /// not syntax: <c>[</c> is an operator that pushes a mark and <c>]</c> is one that collects to
    /// it, and a program may call either by name or leave them unbalanced across a procedure
    /// boundary. They are emitted as executable names and the interpreter runs them.
    /// <c>&lt;&lt;</c> and <c>&gt;&gt;</c> are the same story.</para>
    /// </summary>
    internal sealed class PsParser
    {
        private readonly PsScanner _scanner;
        private readonly PsAllocationBudget _alloc;

        public PsParser(PsScanner scanner, PsAllocationBudget alloc = null)
        {
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _alloc   = alloc ?? new PsAllocationBudget();
        }

        /// <summary>
        /// The dictionary stack a <c>//name</c> is looked up in when it is scanned (PLRM §3.12.2) —
        /// the running interpreter's, which execution being streamed makes current at that moment.
        /// Null for a parser over a program nothing runs, where the name stays a name.
        /// </summary>
        public PsDictStack Dicts { get; set; }

        /// <summary>
        /// Reads the next top-level object. Returns false at end of input.
        /// </summary>
        public bool TryNext(out PsObject obj)
        {
            if (!_scanner.TryNext(out var token)) { obj = PsObject.Null; return false; }
            obj = Build(token);
            return true;
        }

        /// <summary>Reads every remaining top-level object.</summary>
        public List<PsObject> ReadAll()
        {
            var objects = new List<PsObject>();
            while (TryNext(out var o)) objects.Add(o);
            return objects;
        }

        /// <summary>
        /// Builds one object from <paramref name="token"/>, consuming further tokens when it opens
        /// a procedure.
        /// </summary>
        private PsObject Build(PsToken token)
        {
            if (token.Kind != PsTokenKind.ProcOpen) return Simple(token);

            // Builder stack: one List<PsObject> per open '{'.
            var stack = new List<List<PsObject>> { new List<PsObject>() };

            while (true)
            {
                if (!_scanner.TryNext(out var t))
                {
                    PostScriptDiagnostics.Report("PsParser",
                        $"{stack.Count} unclosed '{{' at end of input; closed implicitly");
                    break;
                }

                if (t.Kind == PsTokenKind.ProcOpen)
                {
                    if (stack.Count >= PsLimits.MaxParseNestingDepth)
                    {
                        PostScriptDiagnostics.Report("PsParser",
                            $"procedure nesting exceeds {PsLimits.MaxParseNestingDepth}; " +
                            "the rest of this procedure is discarded");

                        // The builders open, plus the '{' just read that opened no builder: every
                        // one of them has a '}' to skip. Counting the builders alone stopped one
                        // brace short, and that brace reached the interpreter as an unbalanced '}'
                        // — a spurious report and a stray null operand.
                        SkipToMatchingClose(stack.Count + 1);
                        break;
                    }
                    stack.Add(new List<PsObject>());
                    continue;
                }

                if (t.Kind == PsTokenKind.ProcClose)
                {
                    var finished = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);

                    var proc = PsObject.Proc(MakeArray(finished));
                    if (stack.Count == 0) return proc;
                    stack[stack.Count - 1].Add(proc);
                    continue;
                }

                stack[stack.Count - 1].Add(Simple(t));
            }

            // Unclosed input: fold the open builders outward so nothing already parsed is lost.
            while (stack.Count > 1)
            {
                var finished = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                stack[stack.Count - 1].Add(PsObject.Proc(MakeArray(finished)));
            }
            return PsObject.Proc(MakeArray(stack[0]));
        }

        /// <summary>
        /// Discards tokens until the brace depth returns to zero, so parsing can resume at the next
        /// top-level object rather than mid-procedure.
        /// </summary>
        private void SkipToMatchingClose(int openDepth)
        {
            while (openDepth > 0 && _scanner.TryNext(out var t))
            {
                if (t.Kind == PsTokenKind.ProcOpen) openDepth++;
                else if (t.Kind == PsTokenKind.ProcClose) openDepth--;
            }
        }

        private PsArray MakeArray(List<PsObject> items)
        {
            if (!_alloc.TryChargeArray(items.Count))
                PostScriptDiagnostics.Report("PsParser",
                    "allocation budget exhausted while building a procedure");
            return new PsArray(items.ToArray());
        }

        /// <summary>Maps a non-procedure token to its object.</summary>
        private PsObject Simple(PsToken token) => token.Kind switch
        {
            PsTokenKind.Integer        => PsObject.Int(SaturatingCast.ToLong(token.Number)),
            PsTokenKind.Real           => PsObject.Real(token.Number),
            PsTokenKind.LiteralName    => PsObject.LitName(token.Text),
            PsTokenKind.ExecutableName => PsObject.ExecName(token.Text),

            // //name is "immediately evaluated": the value bound to the name AT SCAN TIME takes its
            // place. Not a nicety — `/moveto { Xform //moveto } def` captures the operator before
            // redefining the name, and an ordinary name there calls the new procedure from itself
            // until the execution stack overflows (a public sample program did).
            PsTokenKind.ImmediateName  => ImmediateName(token.Text),

            PsTokenKind.String         => MakeString(token.Bytes),

            // ']' '>>' and a stray '[' reach here as syntax tokens; hand them to the interpreter as
            // the operator names they actually are.
            PsTokenKind.ArrayOpen      => PsObject.ExecName("["),
            PsTokenKind.ArrayClose     => PsObject.ExecName("]"),
            PsTokenKind.DictOpen       => PsObject.ExecName("<<"),
            PsTokenKind.DictClose      => PsObject.ExecName(">>"),

            // A '}' with no matching '{' — the scanner already consumed it.
            PsTokenKind.ProcClose      => Unbalanced(),

            _                          => PsObject.Null
        };

        private PsObject ImmediateName(string text)
        {
            if (Dicts != null && Dicts.TryLookup(PsObject.LitName(text), out PsObject value)) return value;
            PostScriptDiagnostics.Report("PsParser",
                Dicts == null
                    ? $"immediately-evaluated name '//{text}' treated as an ordinary executable name: nothing is running to look it up in"
                    : $"immediately-evaluated name '//{text}' is not defined where it is scanned; it was kept as an executable name");
            return PsObject.ExecName(text);
        }

        private static PsObject Unbalanced()
        {
            PostScriptDiagnostics.Report("PsParser", "unbalanced '}' ignored");
            return PsObject.Null;
        }

        /// <summary>
        /// Wraps the scanner's bytes, <b>uncharged</b>.
        ///
        /// <para><b>The scanner already charged them, on the same budget instance.</b>
        /// <c>PostScriptConversion</c> hands one <c>PsAllocationBudget</c> to both, and
        /// <c>PsScanner.ScanLiteralString</c> / <c>ScanHexString</c> each charge the finished
        /// <c>buf.Count</c> — so charging <c>bytes.Length</c> here billed every string literal twice
        /// and halved the effective ceiling for string-heavy input. Because <c>Exhausted</c> is
        /// sticky, the first over-budget moment poisoned the whole conversion: a legitimate 40 MB
        /// <c>.ps</c> carrying 33 MB of hex image data charged 66 MB against the documented 64 MiB,
        /// and the very first <c>def</c> in the prologue then raised <c>limitcheck</c>.</para>
        ///
        /// <para>The scanner is the right owner — it is where the bytes are materialised, and where
        /// an unterminated literal has to be stopped mid-growth. <c>MakeArray</c>'s charge stays,
        /// because the scanner does not build procedures and so cannot duplicate it.</para>
        /// </summary>
        private static PsObject MakeString(byte[] bytes)
            => PsObject.Str(new PsString(bytes ?? Array.Empty<byte>()));
    }
}
