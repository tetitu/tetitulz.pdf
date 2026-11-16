// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Operators;

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// The PostScript execution engine.
    ///
    /// <para><b>The step loop is iterative.</b> Executing a procedure pushes a
    /// <see cref="PsFrame"/> rather than recursing, so:</para>
    /// <list type="bullet">
    ///   <item>nesting depth is bounded by <see cref="PsLimits.MaxExecStackDepth"/> — a heap limit
    ///     — instead of by the thread's stack, so <c>/f { f } def f</c> raises a catchable
    ///     <c>execstackoverflow</c> rather than an uncatchable <see cref="StackOverflowException"/>
    ///     that takes the process down;</item>
    ///   <item><c>exit</c> and <c>stop</c> unwind by popping frames in a loop, which is what they
    ///     actually mean, instead of by throwing through CLR frames.</item>
    /// </list>
    ///
    /// <para><b>Errors are the one thing that throws</b> (<see cref="PsErrorException"/>), because
    /// they originate deep inside operator implementations where a status code would have to be
    /// threaded through every one. <see cref="Run"/> catches them at exactly one place and converts
    /// the throw into the PostScript-level unwind: to the innermost <c>stopped</c> if there is one,
    /// otherwise abandon the current top-level object, restore the stacks and carry on with the
    /// next. <b>A raised error never escapes this class</b> — the caller is a renderer with a
    /// placeholder, and losing a page to one bad prologue line would be worse than losing the
    /// line.</para>
    /// </summary>
    internal sealed class PsInterpreter
    {
        private readonly WorkBudget _budget;

        internal PsInterpreter(PostScriptConversionOptions options = null,
                               PsAllocationBudget allocation = null)
            : this(options, allocation, null, null)
        {
        }

        /// <summary>
        /// A <b>child</b> interpreter, for running a Type 3 glyph procedure.
        ///
        /// <para><b>What it shares is the whole point.</b> The operand and exec stacks stay
        /// per-instance — that is what keeps a glyph's <c>stop</c> from unwinding into the outer
        /// program's <c>stopped</c>, and leaves the parent's <c>ExitRequested</c> /
        /// <c>StopRequested</c> untouched. But the <see cref="WorkBudget"/> and the
        /// <see cref="PsAllocationBudget"/> <b>must</b> be the parent's: a child built through the
        /// ordinary constructor gets a fresh budget, which is a straight bypass of
        /// <see cref="PsLimits.MaxTotalOps"/> — twenty million steps per glyph, times a thousand
        /// glyphs. The dictionary stack is shared because PLRM runs a glyph procedure with the font
        /// dictionary pushed onto the <i>same</i> stack the program is using.</para>
        ///
        /// <para>This is what makes running a glyph procedure possible without re-entering the step
        /// loop: <c>DrainExecStack</c> is called from exactly one site, inside <see cref="Run"/>,
        /// so an operator cannot run a procedure and observe its result within its own invocation —
        /// the same reason <c>pathforall</c>, <c>kshow</c> and <c>cshow</c> are arity-table entries.
        /// A second interpreter has its own <see cref="Run"/>.</para>
        /// </summary>
        internal PsInterpreter(PostScriptConversionOptions options, PsAllocationBudget allocation,
                               WorkBudget sharedBudget, PsDictStack sharedDicts)
        {
            Options    = options ?? PostScriptConversionOptions.Default;
            Allocation = allocation ?? new PsAllocationBudget(Options.MaxCompositeAllocationBytes);

            _budget = sharedBudget ?? new WorkBudget(Options.MaxTotalOps, "PostScript interpreter");

            Operands = new PsOperandStack();
            ExecStack = new PsExecStack();

            if (sharedDicts != null) { Dicts = sharedDicts; return; }

            var systemDict = PsSystemDict.Build();
            Dicts = new PsDictStack(systemDict, new PsDict(), new PsDict());

            // systemdict must be able to name itself: `systemdict /foo known` is the standard
            // feature probe, and an EPS prologue that cannot run it takes the wrong branch.
            systemDict.PutName("systemdict", PsObject.Dict(systemDict));
            systemDict.PutName("globaldict", PsObject.Dict(Dicts.GlobalDict));

            // Global VM, which no restore rolls back (PLRM §3.7.2).
            systemDict.MarkGlobal();
            Dicts.GlobalDict.MarkGlobal();
            systemDict.PutName("userdict",   PsObject.Dict(Dicts.UserDict));

            // The two Appendix E encoding vectors. See PsEncodings for why a program naming one is
            // the ordinary case rather than a probe, and why they are built per interpreter.
            systemDict.PutName(PsEncodings.StandardEncodingName,
                               PsObject.Arr(PsEncodings.Build(PsEncodings.StandardEncodingName)));
            systemDict.PutName(PsEncodings.IsoLatin1EncodingName,
                               PsObject.Arr(PsEncodings.Build(PsEncodings.IsoLatin1EncodingName)));

            // FontDirectory is read unconditionally by the standard "define it once" guard —
            // `FontDirectory /Foo known not { … definefont … } if` — so it has to be a real
            // dictionary that `definefont` keeps up to date, not a stub. PsTextOps maintains it.
            FontDirectory = new PsDict();
            systemDict.PutName("FontDirectory", PsObject.Dict(FontDirectory));

            // GlobalFontDirectory is the same dictionary, not a second one: PLRM separates local
            // from global VM, and this interpreter has one VM (see `restore`, which rolls back the
            // graphics and dictionary state but not VM). Two directories would mean a font defined
            // through one being invisible through the other, which is a worse lie than sharing.
            systemDict.PutName("GlobalFontDirectory", PsObject.Dict(FontDirectory));

            // errordict and $error exist so that a prologue installing its own error handler —
            // `errordict /handleerror { … } put`, `$error /newerror false put` — runs instead of
            // costing its whole top-level object to `undefined`. errordict's procedures are never
            // INVOKED: this interpreter reports an error and abandons the object (PsErrorException),
            // it does not execute PostScript error procedures. $error IS written, by RecordError,
            // with what PLRM §3.11.2's handler records — newerror, errorname, command — because
            // `stopped { $error /newerror get { … } if } if` is how a program tells an error from
            // a plain `stop`, and an empty dictionary cost that idiom its object at the `get`.
            //
            // statusdict and internaldict are deliberately NOT defined, on PsSystemDict's own
            // rule. Both are printer-private and everything reached through them is a device
            // operator this interpreter does not have, so defining them buys nothing: the object
            // dies on `setpapertray` instead of on `statusdict`, one token later. Leaving them
            // undefined additionally lets `/statusdict where { … } if` take its fallback branch.
            var errorDict = new PsDict();
            errorDict.PutName("newerror", PsObject.False);
            systemDict.PutName("errordict", PsObject.Dict(new PsDict()));
            systemDict.PutName("$error",    PsObject.Dict(errorDict));
        }

        /// <summary>
        /// Writes the error into <c>$error</c> as PLRM §3.11.2's handler does — <c>newerror</c>,
        /// <c>errorname</c>, <c>command</c> — so a program that catches it with <c>stopped</c> can
        /// read what happened. Looked up through <c>systemdict</c> each time rather than held,
        /// because a program may replace the dictionary; one it removed records nothing.
        /// </summary>
        private void RecordError(PsErrorException ex)
        {
            if (!Dicts.SystemDict.TryGetName("$error", out var slot) || slot.Type != PsType.Dictionary)
                return;

            var error = slot.AsDict();
            error.PutName("newerror",  PsObject.True);
            error.PutName("errorname", PsObject.LitName(ex.ErrorName));
            error.PutName("command",   ex.Operator != null ? PsObject.LitName(ex.Operator) : PsObject.Null);
        }

        /// <summary>
        /// <c>FontDirectory</c> — every font the program has defined, by name. Maintained by
        /// <c>PsTextOps.DefineFont</c> / <c>UndefineFont</c>.
        /// </summary>
        internal PsDict FontDirectory { get; }

        public PostScriptConversionOptions Options { get; }
        public PsAllocationBudget Allocation { get; }
        public PsOperandStack Operands { get; }
        public PsDictStack Dicts { get; }
        public PsExecStack ExecStack { get; }

        /// <summary>True once the operation or allocation budget ran out.</summary>
        public bool BudgetExhausted { get; private set; }

        /// <summary>
        /// Set by <c>stop</c>; cleared when a <c>stopped</c> frame catches it. Exposed so the
        /// operators can raise it without knowing how the loop unwinds.
        /// </summary>
        internal bool StopRequested { get; set; }

        /// <summary>Set by <c>exit</c>, consumed by the step loop on the next iteration.</summary>
        internal bool ExitRequested { get; set; }

        /// <summary>
        /// Set by <c>quit</c> / <c>showpage</c> in fragment mode when execution should simply end.
        /// </summary>
        internal bool QuitRequested { get; set; }

        /// <summary>
        /// The graphics engine, or null for an interpreter that runs the language without drawing.
        /// The graphics operators consume their operands and report in that case rather than
        /// throwing, so a program still executes to completion and its diagnostics stay useful.
        /// An engine set here charges its own work and memory to this interpreter's budgets.
        /// </summary>
        internal Graphics.PsGraphicsEngine Graphics
        {
            get => _graphics;
            set
            {
                _graphics = value;
                if (value == null) return;
                value.Work = units => Tick(units);
                value.Allocate = (bytes, op) => ChargeAllocation(bytes, op);
            }
        }

        private Graphics.PsGraphicsEngine _graphics;

        /// <summary>
        /// Where <c>showpage</c> deposits a finished page in document mode; null in fragment mode,
        /// which is what makes <c>showpage</c> mean "reset" there instead.
        /// </summary>
        internal Document.PostScriptPageCollector PageSink { get; set; }

        // The font is part of the graphics state (PLRM §4.2): the properties below are views onto the
        // current state's PsFontState, so gsave keeps the font and grestore puts it back. An
        // interpreter with no engine — a tint transform's child — keeps its own.
        private global::Tetitulz.Pdf.Plugins.PostScript.Graphics.PsFontState _detachedFont =
            global::Tetitulz.Pdf.Plugins.PostScript.Graphics.PsFontState.Initial;

        private global::Tetitulz.Pdf.Plugins.PostScript.Graphics.PsFontState Font
        {
            get => Graphics?.Gs.Font ?? _detachedFont;
            set { if (Graphics != null) Graphics.Gs.Font = value; else _detachedFont = value; }
        }

        /// <summary>
        /// The font matrix set by <c>scalefont</c> / <c>makefont</c>.
        ///
        /// <para>Kept whole rather than reduced to a size, because <c>makefont</c> can skew or
        /// non-uniformly scale text and PDF can only express that through the text matrix.</para>
        /// </summary>
        // Fully qualified: the `Graphics` property above shadows the namespace of the same name, so
        // `Graphics.PsMatrix.Identity` binds to the property and does not compile.
        internal Graphics.PsMatrix CurrentFontMatrix
        {
            get => Font.Matrix;
            set => Font = Font with { Matrix = value };
        }

        /// <summary>
        /// The current font <c>/Encoding</c> as code to glyph name, or null for plain WinAnsi. Set
        /// by <c>setfont</c> from a font the program built with <c>definefont</c>.
        /// </summary>
        internal IReadOnlyDictionary<int, string> CurrentFontEncoding
        {
            get => Font.Encoding;
            set => Font = Font with { Encoding = value };
        }

        /// <summary>
        /// The predefined CMap name of the current font when it is a CJK composite, else null. Set
        /// by <c>setfont</c>; it is what makes <c>show</c> take the Type0 path.
        /// </summary>
        internal string CurrentFontCMap
        {
            get => Font.CMap;
            set => Font = Font with { CMap = value };
        }

        /// <summary>
        /// The font program the current font carries, or null. Set by <c>setfont</c> when the
        /// program's own <c>definefont</c> registered one.
        /// </summary>
        internal Emit.PsEmbeddedFont CurrentEmbeddedFont
        {
            get => Font.Embedded;
            set => Font = Font with { Embedded = value };
        }

        /// <summary>
        /// True when the font <c>setfont</c> last established is a Type 3 (user-defined) one, whose
        /// glyphs are PostScript procedures rather than a font program.
        ///
        /// <para>It exists so a show operator can say <i>why</i> it is substituting. Such a font has
        /// no standard-14 base by design, so it reaches the same fallback as a missing
        /// <c>setfont</c> — and reporting "ran with no font set" there sends a reader looking for a
        /// missing <c>setfont</c> that is not the problem.</para>
        ///
        /// <para>Recorded rather than derived at show time: the show operators see only the resolved
        /// base-font name.</para>
        /// </summary>
        internal bool CurrentFontIsType3
        {
            get => Font.IsType3;
            set => Font = Font with { IsType3 = value };
        }

        /// <summary>The recognised Type 3 font setfont established, or null.</summary>
        internal Emit.PsType3Font CurrentType3Font
        {
            get => Font.Type3;
            set => Font = Font with { Type3 = value };
        }

        /// <summary>
        /// The font dictionary <c>setfont</c> last established — what <c>currentfont</c> answers
        /// with (PLRM §8.2), so a font rescaled through it keeps its encoding, its Type 3
        /// procedures, its CMap and its embedded program. Null until a font is set.
        /// </summary>
        internal PsDict CurrentFontDict
        {
            get => Font.Dict;
            set => Font = Font with { Dict = value };
        }

        /// <summary>The character a composite font's <c>cshow</c> selected while its procedure runs, or null.</summary>
        internal Operators.PsCShowSelection CShowSelection { get; set; }

        /// <summary>The work budget, so a child interpreter can be given the parent's.</summary>
        internal WorkBudget SharedBudget => _budget;

        /// <summary>Whether a separation painted in its alternate space has been reported in this conversion — once is the fact.</summary>
        internal bool SpotColourApproximationReported { get; set; }

        /// <summary>The <c>shfill</c>s this conversion has run, painted or refused, against <see cref="PsLimits.MaxShadings"/>.</summary>
        internal int ShadingFills { get; set; }

        /// <summary>
        /// Non-null while this interpreter is running a Type 3 glyph procedure. It is the slot
        /// <c>setcachedevice</c> / <c>setcharwidth</c> write the declared width into, and what the
        /// page-level operators test in order to refuse: a glyph that calls <c>showpage</c> or
        /// <c>initclip</c> is a malformed font, not a page break, and honouring it would clear the
        /// glyph mid-build.
        /// </summary>
        internal Emit.PsType3Build GlyphBuild { get; set; }

        /// <summary>
        /// The <c>setpacking</c> flag. Nothing observes it but <c>currentpacking</c>: this
        /// interpreter builds an ordinary array for <c>packedarray</c> either way, since packed
        /// arrays exist to save VM in a printer. It is carried so a prologue that sets the mode and
        /// reads it back sees what it wrote rather than taking the wrong branch.
        /// </summary>
        internal bool PackingMode { get; set; }

        /// <summary>
        /// How many numeric operands <c>setcolor</c> consumes: the component count of the space
        /// <c>setcolorspace</c> (or <c>setgray</c> / <c>setrgbcolor</c> / <c>setcmykcolor</c>) last
        /// established, or -1 when it named one this interpreter cannot size.
        ///
        /// <para><b>Stored on the graphics state, not here</b> — PLRM §4.8 makes the current colour
        /// space part of the graphics state, so <c>gsave</c> has to save it and <c>grestore</c> has
        /// to put it back. See <c>PsGraphicsState.ColorComponents</c> for what a count that outlived
        /// its <c>gsave</c> did to the operand stack. This property is the accessor the operators
        /// use, so no call site had to learn where it lives.</para>
        ///
        /// <para>An interpreter with no graphics engine — the language-only mode the graphics
        /// operators tolerate — falls back to a field here, so <c>setcolorspace</c>/<c>setcolor</c>
        /// still keep their arity agreement in a unit test that drives the interpreter directly.
        /// Instance state either way, not a <c>[ThreadStatic]</c> slot: it lives and dies with the
        /// interpreter, so it needs no reset hook and cannot leak into the next conversion.</para>
        /// </summary>
        internal int CurrentColorComponents
        {
            get => Graphics?.Gs.ColorComponents ?? _detachedColorComponents;
            set
            {
                if (Graphics != null) Graphics.Gs.ColorComponents = value;
                else _detachedColorComponents = value;
            }
        }

        private int _detachedColorComponents = 1;

        /// <summary>
        /// The scanner, so the <c>eexec</c> operator can hand it the clear-text start offset and
        /// take back the captured font program. Set by the facade; null in a unit test that drives
        /// the interpreter directly.
        /// </summary>
        internal PsScanner Scanner { get; set; }

        /// <summary>The one file <c>currentfile</c> pushes, made at its first use.</summary>
        internal PsFile CurrentFile { get; set; }

        /// <summary>Font programs captured from <c>eexec</c> sections, by declared /FontName.</summary>
        internal Dictionary<string, Emit.PsEmbeddedFont> EmbeddedFonts { get; } =
            new Dictionary<string, Emit.PsEmbeddedFont>(StringComparer.Ordinal);

        /// <summary>The most recently captured program, for a font defined without a /FontName.</summary>
        internal Emit.PsEmbeddedFont LastEmbeddedFont { get; set; }

        // ------------------------------------------------------------------
        //  Running
        // ------------------------------------------------------------------

        /// <summary>
        /// Executes <paramref name="program"/>, one top-level object at a time.
        ///
        /// <para><b>An <see cref="IEnumerable{T}"/>, deliberately, not a list.</b> The facade hands
        /// this a <i>lazy</i> sequence pulled from <c>PsParser</c>, so the scanner is only as far
        /// ahead as the object being executed. That is what makes <c>currentfile</c> meaningful: a
        /// filtered data source begins at the scanner's <b>current</b> position, and pre-tokenising
        /// the whole program — which is what <c>ReadAll</c> did — puts the scanner at end of input
        /// before the first object runs, with the image data already shredded into garbage names.
        /// Nothing else here depends on the sequence being materialised.</para>
        /// </summary>
        public void Run(IEnumerable<PsObject> program)
        {
            if (program == null) return;

            foreach (var next in program)
            {
                if (QuitRequested) break;
                int operandDepth = Operands.Count;
                int dictDepth    = Dicts.Count;

                try
                {
                    Execute(next);
                    DrainExecStack();
                    ClearUnconsumedUnwindFlags();
                }
                catch (PsErrorException ex)
                {
                    // No enclosing `stopped` — DrainExecStack would have handled it otherwise.

                    // An `undefined` that reached here cost the program a whole top-level object
                    // because this interpreter has no such name. That is precisely what
                    // UnimplementedOperators is for — every such name, not only the operators
                    // PsSystemDict knows it lacks, so a name nobody had thought of (clippath,
                    // strokepath, …) cannot leave the list EMPTY while IsClean goes false. Recorded
                    // here rather than at the lookup that
                    // raises it, because a name inside `{ … } stopped` is a deliberate feature
                    // probe answering "no" and must not be reported as a gap.
                    //
                    // The list is deduplicated and capped, so this cannot become the attack.
                    if (ex.IsUndefinedName) PostScriptDiagnostics.ReportUndefinedName(ex.Operator);

                    RecordError(ex);
                    PostScriptDiagnostics.Report("PsInterpreter",
                        $"{ex.ErrorName}: {ex.Message}; abandoned the current top-level object " +
                        "and continued with the next");
                    Recover(operandDepth, dictDepth);
                }
                catch (PsBudgetExhaustedException)
                {
                    BudgetExhausted = true;
                    PostScriptDiagnostics.Report("PsInterpreter",
                        "the operation budget was exhausted; execution stopped");
                    Recover(operandDepth, dictDepth);
                    return;
                }
            }
        }

        /// <summary>
        /// Drops an <c>exit</c> / <c>stop</c> that no frame consumed, so it cannot cross into the
        /// next top-level object.
        ///
        /// <para><b>Both flags are only ever read inside <see cref="DrainExecStack"/>'s
        /// <c>while (ExecStack.Count &gt; 0)</c> body.</b> A <b>top-level</b> <c>exit</c> — a shape
        /// generated prologues really do emit as a defensive guard — therefore set the flag, found
        /// an empty exec stack, and left it set: the next object that pushed a frame consumed it, so
        /// <c>exit /n 0 def 3 { /n n 1 add def } repeat</c> ran the body <b>zero</b> times, with no
        /// diagnostic at all, because <c>HandleExit</c> reports only when it finds no loop. Only
        /// <see cref="Recover"/> cleared them, and that runs on the exception path.</para>
        ///
        /// <para>The report is the <c>invalidexit</c> / <c>invalidstop</c> a conforming interpreter
        /// raises here, reported rather than thrown for the same reason <see cref="HandleExit"/>
        /// gives: a prologue that does it defensively still runs.</para>
        /// </summary>
        private void ClearUnconsumedUnwindFlags()
        {
            if (ExitRequested)
            {
                ExitRequested = false;
                PostScriptDiagnostics.Report("PsInterpreter",
                    "a top-level 'exit' outside any loop was ignored (invalidexit); it no longer " +
                    "unwinds the next loop the program enters");
            }

            if (StopRequested)
            {
                StopRequested = false;
                PostScriptDiagnostics.Report("PsInterpreter",
                    "a top-level 'stop' with no enclosing 'stopped' was ignored; it no longer " +
                    "ends the next procedure the program runs");
            }
        }

        /// <summary>Restores the stacks after an unhandled error.</summary>
        private void Recover(int operandDepth, int dictDepth)
        {
            ExecStack.Clear();
            Operands.TruncateTo(operandDepth);
            Dicts.TruncateTo(dictDepth);
            StopRequested = false;
            ExitRequested = false;
        }

        /// <summary>
        /// Executes one object <b>encountered in the input stream</b> — a top-level object, or an
        /// element of a procedure that is running (PLRM §3.5.5).
        ///
        /// <para><b>A procedure literal here is PUSHED, not run.</b> That is the rule the whole
        /// language turns on: <c>{ 2 mul }</c> in <c>/double { 2 mul } def</c> has to reach
        /// <c>def</c> as an operand, and <c>{ 1 } { 2 } ifelse</c> has to reach <c>ifelse</c> as two
        /// operands. A procedure runs only when it is the operand of <c>exec</c> or a control
        /// operator, or when it is the value an executable <i>name</i> resolves to — which is what
        /// <see cref="ExecuteResolvedValue"/> handles.</para>
        /// </summary>
        internal void Execute(PsObject o)
        {
            Tick();

            if (!o.IsExecutable)
            {
                Operands.Push(o);
                return;
            }

            switch (o.Type)
            {
                case PsType.Name:
                    ExecuteName(o.AsName());
                    return;

                case PsType.Operator:
                    o.AsOperator().Run(this);
                    return;

                default:
                    // Procedure literal, executable string, executable number: all push.
                    Operands.Push(o);
                    return;
            }
        }

        /// <summary>
        /// Looks a name up the dictionary stack and executes what it finds.
        ///
        /// <para>Written as a loop rather than by recursing, because a name may resolve to another
        /// executable name (<c>/a /b cvx def</c>). Recursing would put one CLR frame on the stack
        /// per link, so a long enough chain would overflow it — the failure mode this interpreter
        /// is built to avoid.</para>
        /// </summary>
        private void ExecuteName(string name)
        {
            for (int hop = 0; hop < PsLimits.MaxExecStackDepth; hop++)
            {
                // Each dictionary the lookup asks hashes the name whole.
                Tick((long)name.Length * Dicts.Count / KeyCharsPerStep);
                if (!Dicts.TryLookup(PsObject.LitName(name), out var value))
                    throw PsErrorException.UndefinedName(name);

                if (value.Type == PsType.Name && value.IsExecutable)
                {
                    Tick();
                    name = value.AsName();
                    continue;   // follow the alias
                }

                ExecuteResolvedValue(value);
                return;
            }

            throw new PsErrorException(PsErrorKind.ExecStackOverflow, name,
                "an executable-name alias chain did not terminate");
        }

        /// <summary>
        /// Executes the value a name resolved to, or the operand of <c>exec</c>. This is the one
        /// path on which an executable array is <b>run</b> rather than pushed.
        /// </summary>
        internal void ExecuteResolvedValue(PsObject value)
        {
            if (value.Type == PsType.Array && value.IsExecutable)
            {
                PushProcedureFrame(value.AsArray());
                return;
            }

            if (value.Type == PsType.Operator)
            {
                value.AsOperator().Run(this);
                return;
            }

            // Anything else — including a literal array — is simply pushed.
            Operands.Push(value);
        }

        internal void PushProcedureFrame(PsArray body)
        {
            if (body == null || body.Length == 0) return;

            // Charge the procedure's length up front. Otherwise a program that calls a
            // 100 000-element procedure a million times ticks the budget only per element it
            // actually reaches, and the pathological cases are the ones that never finish.
            Tick(body.Length);

            ExecStack.Push(new PsFrame
            {
                Kind = PsFrameKind.Procedure,
                Body = body,
                Index = 0
            });
        }

        /// <summary>
        /// Runs the execution stack until it is empty. Every construct that needs to run a
        /// procedure — loops, <c>forall</c>, <c>stopped</c>, a plain call — pushes a frame and lets
        /// this drive it.
        /// </summary>
        internal void DrainExecStack()
        {
            while (ExecStack.Count > 0)
            {
                if (QuitRequested) { ExecStack.Clear(); return; }

                Tick();

                if (ExitRequested) { HandleExit(); continue; }
                if (StopRequested) { if (!HandleStop()) return; continue; }

                var frame = ExecStack.Top;

                try
                {
                    if (!StepFrame(frame)) ExecStack.Pop();
                }
                catch (PsErrorException ex)
                {
                    // Unwind to the innermost `stopped`, if any. Otherwise rethrow and let Run
                    // abandon the whole top-level object.
                    int stopped = ExecStack.FindInnermostStopped();
                    if (stopped < 0) throw;

                    RecordError(ex);
                    var f = ExecStack[stopped];
                    Operands.TruncateTo(f.OperandDepthOnEntry);
                    Dicts.TruncateTo(f.DictDepthOnEntry);
                    ExecStack.TruncateFrom(stopped);
                    Operands.Push(PsObject.True);   // `stopped` pushes true when it caught something

                    PostScriptDiagnostics.Report("PsInterpreter",
                        $"{ex.ErrorName} caught by an enclosing 'stopped'");
                }
            }
        }

        /// <summary>
        /// Advances one frame by one step. Returns false when the frame is finished and should be
        /// popped.
        /// </summary>
        private bool StepFrame(PsFrame frame)
        {
            switch (frame.Kind)
            {
                case PsFrameKind.Procedure:
                case PsFrameKind.Stopped:
                    if (frame.Index >= frame.Body.Length) return false;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                case PsFrameKind.Loop:
                    if (frame.Index >= frame.Body.Length) frame.Index = 0;   // wrap: loop forever
                    if (frame.Body.Length == 0)
                    {
                        // `{ } loop` — no body to tick through, so tick explicitly or it spins
                        // without ever charging the budget.
                        Tick();
                        return true;
                    }
                    Execute(frame.Body[frame.Index++]);
                    return true;

                case PsFrameKind.Repeat:
                    if (frame.Index >= frame.Body.Length)
                    {
                        if (--frame.RemainingIterations <= 0) return false;
                        frame.Index = 0;
                    }
                    if (frame.Body.Length == 0) return false;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                // The four iterating kinds below share one shape, and one rule that is easy to get
                // backwards: an EMPTY body still iterates. `[1 2 3] { } forall` pushes all three
                // elements and `1 1 3 { } for` pushes all three control values — the procedure runs
                // zero operators each time round, which is not the same as the loop running once.
                // Returning false after the first push would swallow the rest.
                case PsFrameKind.For:
                    if (frame.Index >= frame.Body.Length)
                    {
                        frame.ControlValue += frame.Increment;
                        frame.Index = 0;
                        if (!ForContinues(frame)) return false;
                        Operands.Push(MakeForValue(frame));
                    }
                    if (frame.Body.Length == 0) return true;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                case PsFrameKind.ForAllArray:
                    if (frame.Index >= frame.Body.Length)
                    {
                        if (frame.IterIndex >= frame.IterArray.Length) return false;
                        Operands.Push(frame.IterArray[frame.IterIndex++]);
                        frame.Index = 0;
                    }
                    if (frame.Body.Length == 0) return true;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                case PsFrameKind.ForAllString:
                    if (frame.Index >= frame.Body.Length)
                    {
                        if (frame.IterIndex >= frame.IterString.Length) return false;
                        Operands.Push(PsObject.Int(frame.IterString[frame.IterIndex++]));
                        frame.Index = 0;
                    }
                    if (frame.Body.Length == 0) return true;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                case PsFrameKind.ForAllDict:
                    if (frame.Index >= frame.Body.Length)
                    {
                        if (frame.IterIndex >= frame.IterDict.Count) return false;
                        var kv = frame.IterDict[frame.IterIndex++];
                        Operands.Push(kv.Key);
                        Operands.Push(kv.Value);
                        frame.Index = 0;
                    }
                    if (frame.Body.Length == 0) return true;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                // Ticked per item, as forall is per element: an empty procedure still costs the
                // item's own work, which Advance does.
                case PsFrameKind.Callback:
                    if (frame.Body == null || frame.Index >= frame.Body.Length)
                    {
                        Tick();
                        PsArray next = frame.Advance(this);
                        if (next == null) return false;
                        frame.Body = next;
                        frame.Index = 0;
                    }
                    if (frame.Body.Length == 0) return true;
                    Execute(frame.Body[frame.Index++]);
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The <c>for</c> control value keeps the integer/real distinction of its <b>operands</b>.
        ///
        /// <para>PLRM: the value is an integer only if the initial value and the increment both
        /// were. Re-deriving that from the value at hand — <c>x == Math.Floor(x)</c> — would make
        /// <c>1.0 1 1 { } for</c> push <c>integertype</c>, so a body doing <c>idiv</c> would succeed
        /// where a conforming interpreter raises <c>typecheck</c>. The operand types are known at
        /// <c>for</c>, so they are carried on the frame instead of guessed from the
        /// arithmetic.</para>
        /// </summary>
        private static PsObject MakeForValue(PsFrame f) =>
            f.IntegerControl && Math.Abs(f.ControlValue) < int.MaxValue
                ? PsObject.Int(SaturatingCast.ToLong(f.ControlValue))
                : PsObject.Real(f.ControlValue);

        private static bool ForContinues(PsFrame f) =>
            f.Increment >= 0 ? f.ControlValue <= f.Limit : f.ControlValue >= f.Limit;

        /// <summary>Starts a <c>for</c> frame, pushing the first control value if it runs at all.</summary>
        internal void PushForFrame(double initial, double increment, double limit, PsArray body,
                                   bool integerControl)
        {
            var frame = new PsFrame
            {
                Kind = PsFrameKind.For,
                Body = body,
                Index = 0,
                ControlValue = initial,
                Increment = increment,
                Limit = limit,
                IntegerControl = integerControl
            };

            if (!ForContinues(frame)) return;      // `1 1 0 { } for` runs zero times
            ExecStack.Push(frame);
            Operands.Push(MakeForValue(frame));
        }

        /// <summary><c>exit</c>: discard frames up to and including the innermost loop.</summary>
        private void HandleExit()
        {
            ExitRequested = false;
            int loop = ExecStack.FindInnermostLoop();
            if (loop < 0)
            {
                // PLRM: `exit` outside a loop is an invalidexit error. Reported rather than thrown
                // so a prologue that does it defensively still runs.
                PostScriptDiagnostics.Report("PsInterpreter",
                    "'exit' outside any loop was ignored (invalidexit)");
                return;
            }
            ExecStack.TruncateFrom(loop);
        }

        /// <summary>
        /// <c>stop</c>: unwind to the innermost <c>stopped</c> and push true. Returns false when
        /// there is none, which ends this top-level object.
        ///
        /// <para><b>The operand stack is left alone here, where the error path in
        /// <see cref="DrainExecStack"/> truncates it. The asymmetry is deliberate.</b> The PLRM
        /// restores neither, so an explicit <c>stop</c> — which the program issued on purpose, with
        /// the stack in a state it chose — follows the spec exactly and keeps whatever partial
        /// results it meant to leave. A <i>raised error</i> is the opposite case: it fires from
        /// wherever an operator happened to fail, so the depth is arbitrary and unknowable to the
        /// author, and truncating to the <c>stopped</c> frame's entry depth is what makes
        /// <c>{ 1 2 3 badop } stopped</c> land somewhere predictable instead of leaving the caller to
        /// guess. Making the two agree would mean giving up one or the other property; they are not
        /// the same situation.</para>
        /// </summary>
        private bool HandleStop()
        {
            int stopped = ExecStack.FindInnermostStopped();
            if (stopped < 0)
            {
                StopRequested = false;
                ExecStack.Clear();
                PostScriptDiagnostics.Report("PsInterpreter",
                    "'stop' with no enclosing 'stopped' ended the current top-level object");
                return false;
            }

            StopRequested = false;
            var f = ExecStack[stopped];
            Dicts.TruncateTo(f.DictDepthOnEntry);
            ExecStack.TruncateFrom(stopped);
            Operands.Push(PsObject.True);
            return true;
        }

        // ------------------------------------------------------------------
        //  Budget
        // ------------------------------------------------------------------

        /// <summary>
        /// Charges <paramref name="units"/> of interpreter work (one step by default). Every path
        /// that can loop calls this, which is what makes <c>{ } loop</c> terminate.
        ///
        /// <para><see cref="WorkBudget.Tick"/> throws <see cref="WorkBudgetExceededException"/>,
        /// which is translated here so the step loop has one budget exception to catch and so the
        /// message names PostScript rather than the generic helper.</para>
        /// </summary>
        internal void Tick(long units = 1)
        {
            try { _budget.Tick(units); }
            catch (WorkBudgetExceededException ex) { throw new PsBudgetExhaustedException(ex); }
        }

        /// <summary>The characters of a dictionary key one step pays for: hashing is about 1 ns a character against 29 a step.</summary>
        private const int KeyCharsPerStep = 32;

        /// <summary>
        /// Charges a dictionary operation for its key: a name or a string is hashed whole, and compared
        /// whole where it is found, for the one step the operator costs — the shape <c>eq</c> is charged for too.
        /// </summary>
        internal void TickKey(PsObject key, int dictionaries = 1)
        {
            int length = key.Type == PsType.Name ? key.AsName().Length
                : key.Type == PsType.String ? key.AsString().Length
                : 0;
            Tick((long)length * dictionaries / KeyCharsPerStep);
        }

        /// <summary>
        /// A PostScript string as .NET text, <b>charged</b> for what the conversion retains.
        ///
        /// <para>Every <c>AsLatin1()</c> on an operand-derived string allocates a <c>char[]</c> and
        /// a <c>string</c> — two bytes of UTF-16 per input byte — and none of the call sites charged
        /// for it. <c>cvn</c> was the worst: <c>{ s cvn pop } loop</c> over a 500 KB string retained
        /// 597 MB of interned names against a 64 MiB budget that never saw a byte of it. Routing
        /// the conversions through here means a new call site cannot forget.</para>
        /// </summary>
        internal string LatinFromOperand(PsString s, string op)
        {
            if (s == null) return string.Empty;
            ChargeAllocation(2L * s.Length, op);
            return s.AsLatin1();
        }

        /// <summary>Raises <c>limitcheck</c> when the allocation budget refuses a request.</summary>
        internal void ChargeAllocation(long bytes, string op)
        {
            if (!Allocation.TryCharge(bytes))
                throw new PsErrorException(PsErrorKind.LimitCheck, op,
                    "the composite-allocation budget is exhausted");
        }
    }

    /// <summary>
    /// Thrown when the operation budget runs out. Distinct from <see cref="PsErrorException"/>
    /// because it is <b>not</b> a PostScript error: a program must not be able to catch it with
    /// <c>stopped</c> and keep going, which would defeat the guard entirely.
    /// </summary>
    internal sealed class PsBudgetExhaustedException : Exception
    {
        public PsBudgetExhaustedException(Exception inner = null)
            : base("The PostScript interpreter's operation budget was exhausted.", inner) { }
    }
}
