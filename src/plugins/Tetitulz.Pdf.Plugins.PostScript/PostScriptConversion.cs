// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Plugins.PostScript.Document;
using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// The plugin's public entry point: PostScript in, a PDF content stream out.
    ///
    /// <para><b>What converts.</b> PostScript as print drivers, illustration programs and typesetters
    /// write it: paths, painting and clipping, the graphics-state stack, every Level 3 colour space,
    /// shadings, patterns and forms, images from every kind of data source, the filters, and
    /// <b>text</b> in the standard 14 and in a document's own fonts — Type 1, Type 3, Type 42, CFF
    /// font sets and the CID-keyed kinds, each embedded with its widths — with <c>charpath</c>
    /// outlines. The plugin's README has the full table.</para>
    ///
    /// <para><b>What does not convert</b> is reported, never skipped quietly: through
    /// <see cref="PostScriptConversionResult.UnimplementedOperators"/> and
    /// <see cref="PostScriptConversionResult.LastError"/>. By design, halftones are accepted and ignored
    /// and file I/O beyond <c>currentfile</c> is not done.</para>
    /// </summary>
    public static class PostScriptConversion
    {
        /// <summary>
        /// Converts one PostScript fragment — a <c>/Subtype /PS</c> XObject's stream or an inline
        /// <c>PS</c> operator's string.
        /// </summary>
        /// <param name="postScript">The PostScript program.</param>
        /// <param name="options">Bounds and behaviour; <see cref="PostScriptConversionOptions.Default"/> when null.</param>
        public static PostScriptConversionResult ConvertFragment(
            byte[] postScript, PostScriptConversionOptions options = null)
        {
            // Per-thread storage is not per-document scope: clear on EVERY path in, or a later
            // conversion reads an earlier one's verdict. See PostScriptDiagnostics.Clear.
            ResetPerConversionState();

            options ??= PostScriptConversionOptions.Default;
            ValidateOptions(options);
            Emit.PsStandardOutlines.Reset(options.OutlineFontProvider);
            var result = new PostScriptConversionResult();

            if (postScript == null || postScript.Length == 0)
            {
                PostScriptDiagnostics.Report("PostScriptConversion.ConvertFragment",
                    "the PostScript program was empty");
                return Finish(result);
            }

            var alloc   = new PsAllocationBudget(options.MaxCompositeAllocationBytes);
            PsVm previousVm = PsVm.Current;
            PsVm.Current = new PsVm(alloc);
            var dsc     = new DscScanner();
            var scanner = new PsScanner(postScript, alloc, dsc);
            var parser  = new PsParser(scanner, alloc);

            var emitter = new ContentStreamEmitter(options.MaxEmittedContentBytes);
            var engine  = new PsGraphicsEngine(emitter, PsMatrix.FromArray(options.BaseMatrix));
            var interpreter = new PsInterpreter(options, alloc) { Graphics = engine, Scanner = scanner };
            parser.Dicts = interpreter.Dicts;

            try
            {
                interpreter.Run(StreamProgram(parser, scanner, interpreter, engine, dsc));

                result.Content = emitter.ToBytes();
                result.ContentTruncated = emitter.Truncated;
                result.Resources = engine.Resources.Build();

                // The declared box when the source has one, else the extent of the marks — the
                // documented contract, so a fragment with no DSC comment still answers a box a host
                // can size a form's /BBox with.
                var box = dsc.EffectiveBoundingBox is { IsValid: true } declared
                    ? declared
                    : engine.DrawnExtent;
                if (box is { IsValid: true } extent)
                    result.BoundingBox = new[]
                    {
                        extent.LowerLeftX, extent.LowerLeftY,
                        extent.UpperRightX, extent.UpperRightY
                    };
            }
            catch (Exception ex)
            {
                // A conversion must never take the render down: the caller is a renderer that has a
                // placeholder to fall back to, and an escaping exception would lose the whole page
                // rather than one XObject.
                PostScriptDiagnostics.Report("PostScriptConversion.ConvertFragment",
                    "conversion failed; no content was produced", ex);
            }
            finally
            {
                PsVm.Current = previousVm;
            }

            result.SkippedBinarySection = scanner.SkippedBinarySection;
            result.BudgetExhausted      = alloc.Exhausted || interpreter.BudgetExhausted;
            return Finish(result);
        }

        /// <summary>
        /// Converts a whole PostScript or EPS <b>document</b> to a PDF written to
        /// <paramref name="pdfOutput"/>.
        ///
        /// <para>The difference from <see cref="ConvertFragment"/> is what <c>showpage</c> means:
        /// here it closes the current page and starts another, <b>keeping the VM</b>, because
        /// PostScript preserves <c>userdict</c> across pages and a prologue's procedures are used on
        /// every one of them. Resetting the dictionaries between pages would break every multi-page
        /// document that has a prologue — which is essentially all of them.</para>
        ///
        /// <para>A print driver's job wrapped in HP's Printer Job Language is converted from the
        /// PostScript its <c>ENTER LANGUAGE = POSTSCRIPT</c> introduces; a job in another printer
        /// language is refused, said in <see cref="PostScriptDocumentResult.LastError"/>.</para>
        /// </summary>
        /// <param name="source">The <c>.ps</c> / <c>.eps</c> content.</param>
        /// <param name="pdfOutput">Stream the PDF is written to.</param>
        /// <param name="options">
        /// Bounds and behaviour. <see cref="PostScriptConversionOptions.ShowPage"/> is forced to
        /// <see cref="ShowPageBehavior.NewPage"/> — a document conversion is what that setting
        /// exists for, and honouring a caller's <c>ResetOnly</c> here would silently collapse every
        /// page onto one.
        /// </param>
        public static PostScriptDocumentResult ConvertDocument(
            Stream source, Stream pdfOutput, PostScriptConversionOptions options = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (pdfOutput == null) throw new ArgumentNullException(nameof(pdfOutput));

            ResetPerConversionState();

            // Copy before forcing: the caller's object is theirs, and a host that reuses one
            // instance would otherwise get document semantics on its next fragment conversion.
            options = (options ?? PostScriptConversionOptions.Default).Copy();
            options.ShowPage = ShowPageBehavior.NewPage;
            ValidateOptions(options);
            Emit.PsStandardOutlines.Reset(options.OutlineFontProvider);

            var result = new PostScriptDocumentResult();

            // Rewinds a seekable source before reading. A bare CopyTo starts at the CURRENT
            // position, so a host that had already sniffed the `%!PS` marker would hand the
            // interpreter a headerless tail — and a caller that had read to the end would hand it
            // nothing at all, which converts as an empty document rather than failing.
            byte[] bytes = StreamBytes.ReadAll(source);

            // A print driver's job is wrapped in PJL; the program is what its ENTER introduces.
            if (PjlJob.IsPjl(bytes))
            {
                bytes = PjlJob.PostScriptOf(bytes, out string refusal, out string ignored);
                if (bytes == null)
                {
                    PostScriptDiagnostics.Report("PostScriptConversion.ConvertDocument", refusal);
                    bytes = Array.Empty<byte>();
                }
                else if (ignored != null)
                {
                    PostScriptDiagnostics.Report("PostScriptConversion.ConvertDocument", ignored);
                }
            }

            var alloc     = new PsAllocationBudget(options.MaxCompositeAllocationBytes);
            PsVm previousVm = PsVm.Current;
            PsVm.Current = new PsVm(alloc);
            var dsc       = new DscScanner();
            var scanner   = new PsScanner(bytes, alloc, dsc);
            var parser    = new PsParser(scanner, alloc);
            var collector = new PostScriptPageCollector();

            var emitter = new ContentStreamEmitter(options.MaxEmittedContentBytes);
            var engine  = new PsGraphicsEngine(emitter, PsMatrix.FromArray(options.BaseMatrix),
                                               options.MaxEmittedContentBytes);
            var interpreter = new PsInterpreter(options, alloc)
            {
                Graphics = engine,
                PageSink = collector,
                Scanner  = scanner
            };
            parser.Dicts = interpreter.Dicts;

            try
            {
                interpreter.Run(StreamProgram(parser, scanner, interpreter, engine, dsc));

                // A document that never called showpage — every EPS, by definition — still has one
                // page's worth of marks waiting in the emitter.
                var (trailing, extent) = engine.EndPage();
                result.BlankPageOnly = trailing.Length == 0 && collector.Pages.Count == 0;
                if (trailing.Length > 0 || collector.Pages.Count == 0)
                    collector.Add(trailing, extent, media: engine.PageMedia,
                                  rotate: options.AutoRotatePages == PostScriptAutoRotate.PageByPage ? engine.EndedPageRotation : 0);

                // Resources accumulate on the engine across the whole conversion rather than per
                // page, so every page gets the same dictionary. That over-declares — page 2 lists a
                // font only page 1 used — which is legal and costs a few bytes, where splitting
                // them per page would mean tracking which page first named each resource.
                collector.AttachResources(engine.Resources.Build());

                result.PageCount = PostScriptDocumentWriter.Write(
                    collector.Pages, pdfOutput, dsc.EffectiveBoundingBox);
            }
            catch (Exception ex)
            {
                PostScriptDiagnostics.Report("PostScriptConversion.ConvertDocument",
                    "conversion failed", ex);

                // Still produce a readable PDF: the caller asked for a file, and handing back a
                // truncated or empty stream would be worse than handing back a blank page plus a
                // diagnostic saying why.
                result.BlankPageOnly = true;
                try { result.PageCount = PostScriptDocumentWriter.Write(null, pdfOutput, null); }
                catch (Exception writeEx)
                {
                    PostScriptDiagnostics.Report("PostScriptConversion.ConvertDocument",
                        "the fallback blank page could not be written either", writeEx);
                }
            }
            finally
            {
                PsVm.Current = previousVm;
            }

            var box = dsc.EffectiveBoundingBox;
            if (box.HasValue)
                result.DeclaredBoundingBox = new[]
                {
                    box.Value.LowerLeftX, box.Value.LowerLeftY,
                    box.Value.UpperRightX, box.Value.UpperRightY
                };

            result.IsEps                 = dsc.IsEps;
            result.PageLimitReached      = collector.LimitReached;
            result.SkippedBinarySection  = scanner.SkippedBinarySection;
            result.BudgetExhausted       = alloc.Exhausted || interpreter.BudgetExhausted;
            result.LastError             = PostScriptDiagnostics.LastError;
            result.DiagnosticCount       = PostScriptDiagnostics.ReportCount;
            result.UnimplementedOperators = PostScriptDiagnostics.UnimplementedOperators;
            return result;
        }

        /// <summary>
        /// Clears every per-conversion one-shot and cache — the diagnostics and each operator family's
        /// thread-static state — on both entry points alike: a list written out at each would let a family added to
        /// one and not the other carry one conversion's state into the next.
        /// </summary>
        private static void ResetPerConversionState()
        {
            PostScriptDiagnostics.Clear();
            Operators.PsTextOps.ResetPerConversionState();
            Operators.PsCMapOps.ResetPerConversionState();
            Operators.PsArithmeticOps.ResetPerConversionState();
            Operators.PsControlOps.ResetPerConversionState();
            Operators.PsMiscOps.ResetPerConversionState();
            Emit.PsType3GlyphBuilder.ResetPerConversionState();
            Operators.PsImageOps.ResetPerConversionState();
            Operators.PsPatternOps.ResetPerConversionState();
            Operators.PsFormOps.ResetPerConversionState();
            Operators.PsTransferOps.ResetPerConversionState();
            Emit.PsCells.ResetPerConversionState();
        }

        /// <summary>
        /// Refuses a caller's numbers before any of them reaches a content stream (CLAUDE.md: one
        /// check, <c>Core.Util.NumericArguments</c>, naming the parameter and the options member).
        ///
        /// <para><c>BaseMatrix</c> is the one numeric option read straight into every emitted
        /// coordinate. A NaN in it made every number the emitter wrote come out as <c>0</c>, reported
        /// as a non-finite coordinate — a blank drawing returned as a successful conversion for what
        /// is an argument error. Thrown, not reported: the facade's catch-all below is for the
        /// program's failures, and an argument error is the caller's.</para>
        /// </summary>
        private static void ValidateOptions(PostScriptConversionOptions options)
        {
            var matrix = options.BaseMatrix;
            if (matrix == null) return;

            if (matrix.Length != 6)
                throw new ArgumentException(
                    $"options.{nameof(PostScriptConversionOptions.BaseMatrix)} must hold six numbers, " +
                    $"not {matrix.Length}.", "options");

            for (int k = 0; k < matrix.Length; k++)
                Core.Util.NumericArguments.RequireFinite(matrix[k], "options",
                    $"{nameof(PostScriptConversionOptions.BaseMatrix)}[{k}]");
        }

        /// <summary>
        /// Gives the engine the page extent <c>clippath</c> answers with before anything has
        /// clipped.
        ///
        /// <para>Refreshed before every top-level object, from <see cref="StreamProgram"/>: the DSC
        /// comments are read by the <i>scanner</i>, which runs only as far ahead as the object being
        /// executed, so <c>%%BoundingBox</c> is known once the scanner has passed it — and a
        /// <c>(atend)</c> one only at the end. <c>clippath</c> can be the first operator a program
        /// runs, so the box is set as soon as it is known rather than once.</para>
        /// </summary>
        private static void SetDefaultClip(PsGraphicsEngine engine, DscScanner dsc)
        {
            var box = dsc.EffectiveBoundingBox;
            if (box.HasValue && box.Value.IsValid) engine.DefaultClipBox = box.Value;
        }

        /// <summary>
        /// The program, pulled one top-level object at a time so the scanner stays exactly as far
        /// ahead as the object being executed.
        ///
        /// <para><b>This is what makes a filtered data source possible</b>: <c>currentfile</c> means
        /// "the bytes after the operator that is running", and pre-tokenising the whole program
        /// would leave the scanner at end of input before the first object runs, with any inline
        /// image data already shredded into garbage names.</para>
        ///
        /// <para>Two things happen <i>as it goes</i> rather than once before execution, and both
        /// would fail silently if done once:</para>
        /// <list type="bullet">
        ///   <item><b>Captured font programs.</b> The scanner captures an <c>eexec</c> section while
        ///     tokenising, and the program's <c>findfont</c> — by the header's <c>/FontName</c> —
        ///     or a <c>definefont</c> reaches it later. Draining the list only once, before
        ///     execution, would drain it while empty, and <c>/FontFile</c> embedding would stop dead with
        ///     <b>nothing going red</b>: a substituted standard face still draws text.</item>
        ///   <item><b>The DSC bounding box</b> reaches <c>clippath</c> through
        ///     <see cref="PsGraphicsEngine.DefaultClipBox"/>, which is refreshed here rather
        ///     than set once — <c>%%BoundingBox: (atend)</c> is not known until the end.</item>
        /// </list>
        /// </summary>
        private static IEnumerable<PsObject> StreamProgram(PsParser parser, PsScanner scanner,
                                                           PsInterpreter interpreter,
                                                           PsGraphicsEngine engine, DscScanner dsc)
        {
            int drained = 0;

            while (parser.TryNext(out var obj))
            {
                drained = RegisterCapturedFonts(scanner, interpreter, drained);
                SetDefaultClip(engine, dsc);
                yield return obj;
            }

            // A capture in the tail of the file — after the last executable object — still belongs
            // to this conversion.
            RegisterCapturedFonts(scanner, interpreter, drained);
        }

        /// <summary>
        /// Registers font programs the scanner has captured since the last call, returning the new
        /// high-water mark.
        ///
        /// <para><b>Incremental, because execution is now streamed.</b> The scanner captures an
        /// <c>eexec</c> section as it tokenises past it, and the <c>findfont</c> or
        /// <c>definefont</c> that reaches the program comes later in the same stream — so a single
        /// pre-run drain would find the list empty and every <c>/FontFile</c> embedding would
        /// silently stop.</para>
        ///
        /// <para>A program is registered under its header's <c>/FontName</c>, which is how a real
        /// document reaches it: a Type 1 program's own <c>definefont</c> is inside the encrypted
        /// section the scanner skips, so <c>/CMR10 findfont</c> is the only clear-text request for
        /// it (<c>PsTextOps.FindFont</c>). <see cref="PsInterpreter.LastEmbeddedFont"/> serves a
        /// header that declared no name, which only the next <c>definefont</c> can claim.</para>
        /// </summary>
        private static int RegisterCapturedFonts(PsScanner scanner, PsInterpreter interpreter,
                                                 int alreadyRegistered)
        {
            var captured = scanner.CapturedFontPrograms;
            for (int index = alreadyRegistered; index < captured.Count; index++)
            {
                var bytes = captured[index];
                var font = PsEmbeddedFont.FromProgram(
                    bytes.Program, bytes.Length1, bytes.Length2, bytes.Length3, bytes.TrailerFound);

                if (font == null || !font.IsWellFormed)
                {
                    // The two refusals are named apart, because they mean different things to the
                    // person holding the file: bad arithmetic is a producer bug, and a missing
                    // terminator usually means the input itself is short.
                    string why = font != null && !font.TrailerFound
                        ? "its encrypted section is not terminated — the scan reached the end of " +
                          "the input without finding the 512-zero trailer or 'cleartomark', so the " +
                          "program is cut mid-charstring"
                        : "its three section lengths do not add up";

                    PostScriptDiagnostics.Report("PostScriptConversion",
                        $"a captured font program is not well formed ({why}), so it was not " +
                        "embedded — a /FontFile a reader cannot parse renders nothing at all, " +
                        "which is worse than substituting a standard face");
                    continue;
                }

                interpreter.LastEmbeddedFont = font;
                if (!string.IsNullOrEmpty(font.FontName))
                    interpreter.EmbeddedFonts[font.FontName] = font;

                PostScriptDiagnostics.Report("PostScriptConversion",
                    $"embedded the document's own font program '{font.FontName ?? "(unnamed)"}' " +
                    $"({font.Program.Length} bytes) verbatim as a /FontFile");
            }

            return captured.Count;
        }

        /// <summary>Copies the thread-static diagnostics onto the result.</summary>
        private static PostScriptConversionResult Finish(PostScriptConversionResult result)
        {
            result.LastError              = PostScriptDiagnostics.LastError;
            result.DiagnosticCount        = PostScriptDiagnostics.ReportCount;
            result.UnimplementedOperators = PostScriptDiagnostics.UnimplementedOperators;
            return result;
        }
    }
}
