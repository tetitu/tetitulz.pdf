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
    /// Builds <c>systemdict</c>: every operator this interpreter implements, plus the arity table
    /// for the ones it deliberately does not.
    /// </summary>
    internal static class PsSystemDict
    {
        /// <summary>Registers one operator under <paramref name="name"/>.</summary>
        internal delegate void Registrar(string name, Action<PsInterpreter> run);

        /// <summary>
        /// A fresh <c>systemdict</c>. Built per interpreter rather than cached in a static: the
        /// operators close over nothing, but a shared dictionary would let one conversion's
        /// <c>systemdict /foo put</c> leak into the next, across threads.
        /// </summary>
        public static PsDict Build()
        {
            var dict = new PsDict();

            void Op(string name, Action<PsInterpreter> run) =>
                dict.PutName(name, PsObject.Op(new PsOperator(name, o => run((PsInterpreter)o))));

            PsStackOps.Register(Op);
            PsArithmeticOps.Register(Op);
            PsRelationalOps.Register(Op);
            PsControlOps.Register(Op);
            PsDictOps.Register(Op);
            PsArrayStringOps.Register(Op);
            PsTypeOps.Register(Op);
            PsGraphicsOps.Register(Op);
            PsTextOps.Register(Op);
            PsCallbackOps.Register(Op);
            PsFileOps.Register(Op);
            PsImageOps.Register(Op);
            PsPatternOps.Register(Op);
            PsFormOps.Register(Op);
            PsResourceOps.Register(Op);
            PsTransferOps.Register(Op);
            PsDeviceOps.Register(Op);
            PsMiscOps.Register(Op);

            RegisterKnownUnimplemented(Op);

            return dict;
        }

        /// <summary>
        /// Operators this interpreter recognises but does not implement, with the number of
        /// operands each consumes and pushes.
        ///
        /// <para><b>Why an arity table rather than leaving them undefined.</b> There are two very
        /// different failure modes and conflating them is a bug:</para>
        /// <list type="number">
        ///   <item>A <b>known-but-unimplemented</b> operator must consume exactly the operands the
        ///     PLRM says it does. Leaving them on the stack desynchronises everything after it —
        ///     the next <c>fill</c> reads a leftover operand as a coordinate and draws
        ///     garbage.</item>
        ///   <item>A <b>genuinely undefined</b> name must raise <c>undefined</c>. Prologues probe
        ///     for features with <c>systemdict /setcmykcolor known { … } if</c> and
        ///     <c>{ … } stopped</c>; swallowing <c>undefined</c> makes those probes answer "yes"
        ///     and take a branch this interpreter cannot execute.</item>
        /// </list>
        ///
        /// <para>Each entry is reported once through
        /// <see cref="PostScriptDiagnostics.ReportUnimplemented"/>, so a caller can see exactly
        /// what the drawing is missing rather than inferring it from the result.</para>
        ///
        /// <para><b>Which side of that line an operator belongs on is decided by how programs
        /// reach it, and adding everything missing to this table would be a mistake.</b> The table
        /// is for operators a program calls <i>unconditionally</i> — there, consuming the operands
        /// keeps the stack sane and the rest of the drawing survives. An operator a program
        /// <i>probes</i> for must stay undefined, because that is what makes the probe answer "no"
        /// and take its own fallback path, which this interpreter can execute.</para>
        ///
        /// <para>So the <b>user-path family</b> (<c>upath</c>, <c>uappend</c>, <c>ufill</c>,
        /// <c>ueofill</c>, <c>ustroke</c>, <c>ustrokepath</c>, <c>ucache</c>, <c>ucachestatus</c>,
        /// <c>setucacheparams</c>, <c>currentucacheparams</c>) and the <b>hit-test family</b>
        /// (<c>infill</c>, <c>ineofill</c>, <c>inufill</c>, <c>inueofill</c>) are deliberately
        /// absent and must stay absent. A prologue writes
        /// <c>/ufill where { user-path branch } { ordinary branch } ifelse</c>; leaving them
        /// undefined takes the ordinary branch, which <i>draws correctly</i>. Registering them —
        /// even as faithful arity entries — makes the probe say yes and select a branch whose
        /// painting operators this interpreter would then consume and discard, turning a correct
        /// drawing into a blank one. Registering only <i>part</i> of a family is worse still: the
        /// probe succeeds and execution then dies on the unregistered member, losing the whole
        /// top-level object.</para>
        /// </summary>
        private static void RegisterKnownUnimplemented(Registrar op)
        {
            // name, operands consumed, results pushed (as nulls unless noted)
            var table = new (string Name, int Pops, int Pushes)[]
            {
                // --- halftone and device: parsed and ignored ---
                //     Every set*/current* pair here must be COMPLETE. A program that sets a
                //     parameter and reads it back is doing the most ordinary thing there is, and a
                //     missing current* half would cost it the whole top-level object.
                ("setscreen",        3, 0),
                ("currentscreen",    0, 3),
                ("setcolorscreen",  12, 0),
                ("currentcolorscreen", 0, 12),
                //     settransfer, setcolortransfer and their current* halves are NOT here:
                //     PsTransferOps writes them as /TR. Nor are setoverprint, setflat,
                //     setsmoothness, setstrokeadjust, setblackgeneration, setundercolorremoval and
                //     their current* halves: PsDeviceOps writes them as an ExtGState.
                ("sethalftone",      1, 0),
                ("currenthalftone",  0, 1),
                ("nulldevice",       0, 0),
                ("banddevice",       4, 0),
                ("framedevice",      4, 0),
                ("renderbands",      1, 0),

                // --- patterns and shading are NOT here: PsPatternOps carries makepattern and
                //     setpattern, PsImageOps shfill, and an entry here would shadow them, Build
                //     registering this table last.

                // --- operators that need machinery this interpreter does not have ---
                //     Each is called unconditionally by real programs, so the operands MUST be
                //     consumed; leaving them undefined costs the whole top-level object.
                //
                //     pathforall / kshow / cshow are NOT here: each calls a procedure per item, which
                //     PsCallbackOps does through a step-loop frame, and an entry here would shadow it.
                //     token is NOT here: PsFileOps scans a string or a file with the program's own
                //     scanner and parser.
                //     setbbox declares the extent of a user path, and the user-path family below is
                //     deliberately absent — so there is nothing for it to describe.
                ("setbbox",          4, 0),

                // --- colour spaces beyond DeviceGray/RGB/CMYK ---
                //     setcolorspace, setcolor and their current* halves are NOT here: none has a
                //     fixed arity, so all four are registered below where the component count can be
                //     tracked between them.
                ("setcolorrendering",1, 0),
                ("currentcolorrendering", 0, 1),

                // --- font programs: only INTERPRETING one is out of scope. definefont,
                //     undefinefont and eexec are all implemented (PsTextOps and PsMiscOps) — the
                //     first two carry the /Encoding array every legacy non-Latin document depends
                //     on, and a captured eexec section is embedded verbatim as a /FontFile. All of
                //     them are registered elsewhere and are deliberately absent from this table.
                //     What remains here is the glyph CACHE, which has no meaning without an
                //     interpreter that renders charstrings.
                //     setcachedevice / setcachedevice2 / setcharwidth are deliberately NOT here.
                //     They are real operators in PsTextOps, because a Type 3 glyph procedure
                //     declares its advance through them. Re-adding them would SHADOW those — Build
                //     registers PsTextOps before this table and PsDict.Put overwrites — which
                //     silently zeroes every Type 3 width and piles every glyph on one point.
                ("setcachelimit",    1, 0),
                ("currentcachelimit",0, 1),
                ("currentcacheparams", 0, 2),
                ("cachestatus",      0, 7),
                //     cachestatus pushes SEVEN values (bsize bmax msize mmax csize cmax blimit).
                //     A prologue reads them to decide whether to cache its own glyphs, so getting
                //     the count wrong desynchronises the stack just as leaving it undefined loses
                //     the object.
                

                // --- gstate objects and clipsave / cliprestore are NOT here: PsGraphicsOps carries
                //     them over the engine's clip history (PsClip), which widens a clip by closing
                //     the emitted levels it was taken under and taking the rest again.

                // --- forms are NOT here: PsFormOps carries execform as a form XObject.

                // --- interpreter parameters (Level 2). Called unconditionally by print-driver
                //     output, which sets them and moves on.
                ("setuserparams",       1, 0),
                ("currentuserparams",   0, 1),
                ("setsystemparams",     1, 0),
                ("currentsystemparams", 0, 1),
                ("setdevparams",        2, 0),
                ("currentdevparams",    1, 1),
                ("setobjectformat",     1, 0),
                ("currentobjectformat", 0, 1),

                // --- resources are NOT here: PsResourceOps carries the five over a dictionary of
                //     instances a category.

                // --- file and I/O: deliberately absent, a sandbox boundary ---
                ("file",             2, 1),
                ("closefile",        1, 0),
                ("write",            2, 0),
                //     `=` and `==` write to stdout, which there is none of here. Consuming the
                //     operand is the whole contract as far as the drawing is concerned; a prologue
                //     that prints a progress line must not lose the object it prints from.
                ("=",                1, 0),
                ("==",               1, 0),
                ("writestring",      2, 0),
                ("writehexstring",   2, 0),
                ("bytesavailable",   1, 1),
                ("flushfile",        1, 0),
                //     resetfile, fileposition and setfileposition are NOT here: PsFileOps moves a
                //     ReusableStreamDecode file with them.
                ("filenameforall",   3, 0),
                ("deletefile",       1, 0),
                ("renamefile",       2, 0),
                ("printobject",      2, 0),
                ("writeobject",      3, 0),
                ("status",           1, 1),
                ("run",              1, 0),
                ("print",            1, 0),
                ("stack",            0, 0),
                ("pstack",           0, 0),
                ("flush",            0, 0),
                ("echo",             1, 0),

                // --- VM ---
                ("vmstatus",         0, 3),
                ("vmreclaim",        1, 0),
                ("setvmthreshold",   1, 0),
                ("currentvmthreshold", 0, 1),
                ("gcheck",           1, 1),
                ("setglobal",        1, 0),
                ("currentglobal",    0, 1),
                ("startjob",         2, 1),
                ("usertime",         0, 1),
                ("realtime",         0, 1),
                ("serialnumber",     0, 1),
                ("languagelevel",    0, 1),
                ("product",          0, 1),
                ("revision",         0, 1),
                ("version",          0, 1),
            };

            foreach (var (name, pops, pushes) in table)
            {
                string opName = name;
                int consume = pops;
                int produce = pushes;

                op(opName, i =>
                {
                    PostScriptDiagnostics.ReportUnimplemented(opName);

                    // Consume what the PLRM says, so the stack stays balanced. Fewer operands than
                    // declared is the program's bug, not ours — raise stackunderflow rather than
                    // silently emptying the stack.
                    for (int k = 0; k < consume; k++) i.Operands.Pop();
                    for (int k = 0; k < produce; k++) i.Operands.Push(PsObject.Null);
                });
            }

            // A few of these have a result a program actually branches on, so pushing null would
            // be worse than pushing the honest answer. Registered after the table so they win.
            op("languagelevel", i => i.Operands.Push(PsObject.Int(2)));
            op("currentglobal", i => i.Operands.Push(PsObject.False));

            // The numeric readbacks added with their set* partners. A null here would be worse than
            // the undefined they replace: `currentcachelimit 2 div` on a null raises typecheck and
            // loses the same top-level object, one operator later. Each pushes the PLRM default.
            op("currentcachelimit",   i => i.Operands.Push(PsObject.Int(0)));
            op("currentvmthreshold",  i => i.Operands.Push(PsObject.Int(0)));
            op("currentobjectformat", i => i.Operands.Push(PsObject.Int(0)));   // 0 = not binary

            // cachestatus: bsize bmax msize mmax csize cmax blimit. Zeros with a plausible blimit —
            // this interpreter caches no glyphs, and saying the cache is empty is the truth.
            op("cachestatus", i =>
            {
                PostScriptDiagnostics.ReportUnimplemented("cachestatus");
                for (int k = 0; k < 6; k++) i.Operands.Push(PsObject.Int(0));
                i.Operands.Push(PsObject.Int(int.MaxValue));
            });

            // vmstatus pushes level, used, maximum. A prologue that checks free VM before
            // allocating must see a plausible amount, or it takes its low-memory branch.
            op("vmstatus", i =>
            {
                i.Operands.Push(PsObject.Int(0));
                i.Operands.Push(PsObject.Int(0));
                i.Operands.Push(PsObject.Int(int.MaxValue));
            });

            // usertime/realtime must be monotonic but MUST NOT read the clock: a conversion has to
            // be byte-reproducible or no test can assert on its output. Both return a constant.
            op("usertime", i => i.Operands.Push(PsObject.Int(0)));
            op("realtime", i => i.Operands.Push(PsObject.Int(0)));

            op("product",  i => i.Operands.Push(PsObject.Str(Latin1("Tetitulz.Pdf PostScript"))));
            op("revision", i => i.Operands.Push(PsObject.Int(1)));
            op("version",  i => i.Operands.Push(PsObject.Str(Latin1("3.0"))));

            // ----------------------------------------------------------------
            //  Variable-arity unimplemented operators
            // ----------------------------------------------------------------
            //
            // Both were in the fixed-arity table as consuming NOTHING — ("filter", 0, 1) and
            // ("setcacheparams", 0, 0) — which is the precise desynchronisation that table's own
            // doc says it exists to prevent. They were declared arity-free because their arity is
            // variable and the table cannot express that, and 0 is the worst of the three available
            // answers. `currentfile /ASCII85Decode filter` is a routine prologue line: currentfile
            // pushed a null, the name pushed a literal, filter popped nothing and pushed a third
            // object, so the stack held three where the program expected one file — and the next
            // `]`, counttomark, stopped unwind or `image` read the strays as its own operands.
            //
            // Registered by hand instead, both still reported through ReportUnimplemented. Removing
            // them from systemdict entirely was the other legal option and is rejected here: unlike
            // the user-path and hit-test families, neither is a feature a prologue probes for with
            // `where`, so leaving them undefined buys no fallback branch and costs the top-level
            // object an `undefined` instead.

            // PLRM: `datasrc dict|name filter file`, with any number of leading parameters. The
            // parameter dictionary or name is mandatory and the data source is below it; leading
            // numeric parameters are popped the way SetColor's fallback loop does, stopping at the
            // first operand that is not one so an unrelated object below is left alone.
            op("filter", i =>
            {
                // The /Name or parameter dictionary names the filter.
                string filterName = null;
                if (i.Operands.Count > 0)
                {
                    var spec = i.Operands.Pop();
                    if (spec.Type == PsType.Name) filterName = spec.AsName();
                }

                // `source dict /Name filter` (PLRM §3.8.4): the parameter dictionary between the
                // source and the name. Read as the source, a CCITT filter,
                // which always has one, would be refused. SubFileDecode's count and string come
                // above it: `source [dict] EODCount EODString /SubFileDecode filter`.
                PsDict parameters = filterName == PsFile.SubFileDecode ? SubFileOperands(i) : null;
                if (parameters == null && i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Dictionary)
                    parameters = i.Operands.Pop().AsDict();

                while (i.Operands.Count > 0 && i.Operands.Peek().IsNumber) i.Operands.Pop();

                var source = i.Operands.Count > 0 ? i.Operands.Pop() : PsObject.Null;

                if (filterName == PsFile.ReusableStreamDecode)
                {
                    i.Operands.Push(Reusable(i, source, parameters));
                    return;
                }

                // A filter over the program's own bytes is the shape that matters: it is how an EPS
                // embeds image data. Anything else is consumed and reported.
                if (filterName != null && source.Type == PsType.File)
                {
                    i.ChargeAllocation(PsAllocationBudget.BytesPerFile, "filter");
                    i.Operands.Push(PsObject.File(source.AsFile().WithFilter(filterName, parameters)));
                    return;
                }

                // A string or a procedure is a data source too (PLRM §3.13.1): the string's bytes, read
                // once; the procedure called for each piece, an empty string its end — how the
                // Microsoft PS Class Driver feeds an image, `{ … get … } /FlateDecode filter`.
                if (filterName != null && source.Type == PsType.String)
                {
                    byte[] bytes = source.AsString().ToArray();
                    i.ChargeAllocation(bytes.Length + 2L * PsAllocationBudget.BytesPerFile, "filter");
                    i.Operands.Push(PsObject.File(PsFile.Reusable(bytes, jpeg: false).WithFilter(filterName, parameters)));
                    return;
                }
                if (filterName != null && source.Type == PsType.Array && source.IsExecutable)
                {
                    i.ChargeAllocation(2L * PsAllocationBudget.BytesPerFile, "filter");
                    i.Operands.Push(PsObject.File(PsFile.FromProcedure(source.AsArray()).WithFilter(filterName, parameters)));
                    return;
                }

                PostScriptDiagnostics.ReportUnimplemented("filter");
                i.Operands.Push(PsObject.Null);
            });

            // PLRM: `mark size lower upper … setcacheparams` — it consumes everything back to the
            // topmost mark, so it is a cleartomark with a different name. With no mark on the stack
            // a conforming interpreter raises unmatchedmark, and raising it here is what stops the
            // operator from silently clearing an entire stack that never had one.
            op("setcacheparams", i =>
            {
                PostScriptDiagnostics.ReportUnimplemented("setcacheparams");

                int mark = PsStackOps.FindMark(i);
                if (mark < 0)
                    throw new PsErrorException(PsErrorKind.UnmatchedMark, "setcacheparams");

                for (int k = 0; k <= mark; k++) i.Operands.Pop();
            });

            // setcolorspace establishes how many operands setcolor will take, so the pair has to be
            // registered together and outside the fixed-arity table.
            op("setcolorspace", i =>
            {
                var operand = i.Operands.Pop();

                // ONE classifier answers both questions; a second switch would disagree with it
                // (PsImageColorSpace's remarks give the CalRGB case).
                //
                // Resolved EAGERLY, because a PsArray aliases the program's own storage and an
                // /Indexed palette can be written to after the space is established. Reported
                // lazily, at the image, for the reason PsImageColorSpace records.
                var resolved = Graphics.PsImageColorSpace.Resolve(operand, i);

                i.CurrentColorComponents = resolved?.SetColorArity ?? -1;
                if (i.Graphics != null)
                {
                    var gs = i.Graphics.Gs;
                    gs.ImageSpace = resolved;
                    gs.ColorSpaceOperand = new[] { operand };
                    gs.ColorOperands = resolved == null ? null
                        : resolved.Family == "Pattern" ? new[] { PsObject.Null }    // the initial colour: no pattern
                        : Reals(Graphics.PsSpaceColor.Initial(resolved));
                }

                // The space's initial colour becomes the current one (PLRM §4.8): black, index 0, a
                // full tint. A space whose colour cannot be painted leaves the colour as it was, said.
                // A Pattern space's initial colour is a null pattern, which this leaves to the
                // setcolor or setpattern that follows.
                if (resolved?.Family == "Pattern") return;
                if (resolved == null || !ApplySpaceColor(i, resolved, Graphics.PsSpaceColor.Initial(resolved)))
                    PostScriptDiagnostics.ReportUnimplemented("setcolorspace");
            });
            // currentfile is a real operator now: it is the handle a filtered data source reads
            // the program's own bytes through. Removed from the arity table above, which pushed a
            // null that image then correctly reported as an unusable source.
            // One file object a scanner: it holds nothing but the scanner, and a program reads it once a scanline.
            op("currentfile", i => i.Operands.Push(
                i.Scanner != null ? PsObject.File(i.CurrentFile ??= PsFile.FromScanner(i.Scanner)) : PsObject.Null));

            op("setcolor", SetColor);
            op("currentcolorspace", CurrentColorSpace);
            op("currentcolor", CurrentColor);
        }

        /// <summary>
        /// <c>SubFileDecode</c>'s parameters: <c>EODString</c> and <c>EODCount</c> from the operands
        /// above the source when given there, else from the dictionary below them (PLRM §3.8.4, the
        /// Level 3 form) — a count absent is 0, as other interpreters take it; a
        /// negative one is a <c>rangecheck</c>.
        /// </summary>
        private static PsDict SubFileOperands(PsInterpreter i)
        {
            var parameters = new PsDict(2);
            i.ChargeAllocation(2 * PsAllocationBudget.BytesPerDictEntry, "filter");
            if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.String)
            {
                parameters.PutName("EODString", i.Operands.Pop());
                if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Integer)
                    parameters.PutName("EODCount", i.Operands.Pop());
            }
            if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Dictionary)
            {
                PsDict given = i.Operands.Pop().AsDict();
                foreach (string key in new[] { "EODString", "EODCount" })
                    if (!parameters.TryGetName(key, out _) && given.TryGetName(key, out PsObject value))
                        parameters.PutName(key, value);
            }
            if (parameters.TryGetName("EODCount", out PsObject count) && count.IsNumber && count.Num < 0)
                throw new PsErrorException(PsErrorKind.RangeCheck, "filter", "a negative /EODCount");
            return parameters;
        }

        /// <summary>
        /// <c>source [dict] /ReusableStreamDecode filter</c> (PLRM §3.8.4): the source's whole data —
        /// a file the program's own bytes come through, through the dictionary's <c>/Filter</c> (a name
        /// or an array, with <c>/DecodeParms</c>) first, or a string — read now, charged, and held in a
        /// file <c>resetfile</c> and <c>setfileposition</c> move about in. A source with no data this
        /// converter can read is reported and answered with null, as any unusable filter is.
        /// </summary>
        private static PsObject Reusable(PsInterpreter i, PsObject source, PsDict parameters)
        {
            var stages = new List<(string Name, PsDict Parameters)>();
            if (parameters != null && parameters.TryGetName("Filter", out PsObject filters))
            {
                parameters.TryGetName("DecodeParms", out PsObject decodeParms);
                PsArray names = filters.Type == PsType.Array ? filters.AsArray() : null;
                PsArray parms = decodeParms.Type == PsType.Array ? decodeParms.AsArray() : null;
                int n = names?.Length ?? 1;
                if (n > PsLimits.MaxFilterChainDepth)
                    throw new PsErrorException(PsErrorKind.LimitCheck, "filter",
                        $"a /Filter array of more than {PsLimits.MaxFilterChainDepth} filters (PsLimits.MaxFilterChainDepth)");
                for (int k = 0; k < n; k++)
                {
                    PsObject name = names != null ? names[k] : filters;
                    PsObject parm = parms != null ? (k < parms.Length ? parms[k] : PsObject.Null) : decodeParms;
                    if (name.Type != PsType.Name) throw new PsErrorException(PsErrorKind.TypeCheck, "filter", "a /Filter entry is not a name");
                    stages.Add((name.AsName(), parm.Type == PsType.Dictionary ? parm.AsDict() : null));
                }
            }

            byte[] data = null;
            bool jpeg = false;
            if (source.Type == PsType.File)
            {
                PsFile file = source.AsFile();
                foreach (var (name, parm) in stages) file = file.WithFilter(name, parm);
                data = file.ReadInlineBlock(i, out jpeg);
            }
            else if (source.Type == PsType.String)
            {
                data = source.AsString().ToArray();
                i.ChargeAllocation(data.Length, "filter");
                foreach (var (name, parm) in stages)
                {
                    if (jpeg || data == null) { data = null; break; }
                    if (name == Core.Constants.PdfFilterNames.DctDecode) { jpeg = true; continue; }
                    data = PsFile.Decode(i, name, parm, data);
                    if (data != null) i.ChargeAllocation(data.Length, "filter");
                }
            }

            if (data == null)
            {
                PostScriptDiagnostics.ReportUnimplemented("filter /ReusableStreamDecode (a source with no data this converter reads)");
                return PsObject.Null;
            }
            i.ChargeAllocation(PsAllocationBudget.BytesPerFile, "filter");
            return PsObject.File(PsFile.Reusable(data, jpeg));
        }

        /// <summary>
        /// <c>currentcolorspace</c> (PLRM §8.2): an array — the one <c>setcolorspace</c> was given, a
        /// family name it was given alone in an array of one, the <c>[/Pattern …]</c>
        /// <c>setpattern</c> made — or, after <c>setgray</c> and its kin, the device family of the colour.
        /// </summary>
        private static void CurrentColorSpace(PsInterpreter i)
        {
            var gs = i.Graphics?.Gs;
            PsObject space = gs?.ImageSpace != null && gs.ColorSpaceOperand != null
                ? gs.ColorSpaceOperand[0]
                : PsObject.LitName(DeviceFamily(gs?.Color.Space ?? Graphics.PsColorSpace.Gray));
            if (space.Type == PsType.Array)
            {
                i.Operands.Push(space);
                return;
            }
            i.ChargeAllocation(PsAllocationBudget.BytesPerArrayElement, "currentcolorspace");
            i.Operands.Push(PsObject.Arr(new PsArray(new[] { space })));
        }

        /// <summary>
        /// <c>currentcolor</c> (PLRM §8.2): the operands the current colour was set with in its space
        /// — a tint, an index, an uncoloured pattern's components and then the pattern — or, in a device
        /// space, the colour's own components.
        /// </summary>
        private static void CurrentColor(PsInterpreter i)
        {
            var gs = i.Graphics?.Gs;
            if (gs?.ImageSpace != null && gs.ColorOperands != null)
            {
                foreach (PsObject value in gs.ColorOperands) i.Operands.Push(value);
                return;
            }
            Graphics.PsColor c = gs?.Color ?? Graphics.PsColor.Black;
            int n = c.Space switch { Graphics.PsColorSpace.Gray => 1, Graphics.PsColorSpace.Rgb => 3, _ => 4 };
            double[] components = { c.C0, c.C1, c.C2, c.C3 };
            for (int k = 0; k < n; k++) i.Operands.Push(PsObject.Real(components[k]));
        }

        /// <summary>The device family name of <paramref name="space"/>.</summary>
        internal static string DeviceFamily(Graphics.PsColorSpace space) => space switch
        {
            Graphics.PsColorSpace.Rgb  => "DeviceRGB",
            Graphics.PsColorSpace.Cmyk => "DeviceCMYK",
            _                          => "DeviceGray",
        };

        private static PsObject[] Reals(double[] values)
        {
            var reals = new PsObject[values.Length];
            for (int k = 0; k < values.Length; k++) reals[k] = PsObject.Real(values[k]);
            return reals;
        }

        /// <summary>
        /// <c>setcolor</c> — consumes the components of the current colour space.
        ///
        /// <para>The arity table above cannot carry this one, and registering it there as
        /// <c>(0, 0)</c> — as it was — leaves every component on the operand stack. That is the
        /// precise desynchronisation the table's own documentation says it exists to prevent:
        /// <c>counttomark</c>, <c>]</c> and a <c>stopped</c> unwind all then see a stack of the wrong
        /// height, and the damage surfaces somewhere unrelated to the operator that caused it.</para>
        ///
        /// <para>A space this interpreter could not size — an <c>ICCBased</c> whose <c>/N</c> lives
        /// in a stream it never built, or a <c>Pattern</c> — falls back to popping the numbers that
        /// are actually present, at most the four of the widest device space, stopping at the first
        /// operand that is not one. That leaves a pattern object or an unrelated operand alone.</para>
        /// </summary>
        private static void SetColor(PsInterpreter i)
        {
            // In a Pattern space the operand is a pattern, an uncoloured one's components below it.
            Graphics.PsImageColorSpace current = i.Graphics?.Gs.ImageSpace;
            if (current?.Family == "Pattern" && i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Dictionary)
            {
                PsPatternOps.SetColor(i, current.PatternBase);
                return;
            }

            int n = i.CurrentColorComponents;
            if (n >= 0)
            {
                var components = new double[n];
                bool numbers = true;
                for (int k = n - 1; k >= 0; k--)
                {
                    PsObject value = i.Operands.Pop();
                    if (value.IsNumber) components[k] = value.Num; else numbers = false;
                }
                // A non-finite component is a rangecheck (PLRM): NaN or ±∞ must not reach a colour-space
                // conversion. A CIEBased Decode table indexes (int)pos, and (int)NaN is a runtime-dependent
                // value — int.MinValue on the netstandard2.0 leg, which throws IndexOutOfRangeException and
                // (uncaught by PsInterpreter.Run) blanks the whole document. The components are already
                // popped, so the stack stays consistent whichever way this returns.
                if (numbers)
                    for (int k = 0; k < n; k++)
                        if (double.IsNaN(components[k]) || double.IsInfinity(components[k]))
                            throw new PsErrorException(PsErrorKind.RangeCheck, "setcolor", "a non-finite colour component");
                Graphics.PsImageColorSpace space = i.Graphics?.Gs.ImageSpace;
                if (numbers && space != null) i.Graphics.Gs.ColorOperands = Reals(components);
                if (!numbers || space == null || !ApplySpaceColor(i, space, components))
                    PostScriptDiagnostics.ReportUnimplemented("setcolor");
                return;
            }

            PostScriptDiagnostics.ReportUnimplemented("setcolor");
            for (int k = 0; k < 4 && i.Operands.Count > 0 && i.Operands.Peek().IsNumber; k++)
                i.Operands.Pop();
        }

        /// <summary>
        /// Makes the colour <paramref name="components"/> stand for in <paramref name="space"/> the current
        /// one, leaving the space as it is — unlike <c>setgray</c> and its kin, which replace it — or
        /// false where <see cref="Graphics.PsSpaceColor"/> cannot paint the space. A separation drawn in its
        /// alternate space is said once per conversion.
        /// </summary>
        private static bool ApplySpaceColor(PsInterpreter i, Graphics.PsImageColorSpace space, double[] components)
        {
            if (i.Graphics == null) return true;   // a tint transform's own child: nothing to paint
            if (!Graphics.PsSpaceColor.TryColor(i, space, components, out var color, out bool approximated)) return false;
            i.Graphics.Gs.Color = color;
            if (approximated && !i.SpotColourApproximationReported)
            {
                i.SpotColourApproximationReported = true;
                PostScriptDiagnostics.Report("PsSystemDict.setcolor",
                    $"a /{space.Family} colour is painted in its alternate space, through its tint transform; " +
                    "the separation itself is not written");
            }
            return true;
        }


        private static PsString Latin1(string s)
        {
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            return new PsString(bytes);
        }
    }
}
