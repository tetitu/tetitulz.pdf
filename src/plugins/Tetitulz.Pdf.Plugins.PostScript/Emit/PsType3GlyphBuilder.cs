// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// Runs a Type 3 glyph procedure and turns what it drew into a PDF CharProc stream.
    ///
    /// <para>See <see cref="PsInterpreter"/>'s child constructor for why this needs a second
    /// interpreter rather than a re-entry of the step loop.</para>
    /// </summary>
    internal static class PsType3GlyphBuilder
    {
        /// <summary>
        /// Aggregate emitted glyph bytes for the conversion. <c>[ThreadStatic]</c> and reset per
        /// conversion, like every other slot in this assembly.
        /// </summary>
        [ThreadStatic] private static long _totalGlyphBytes;
        [ThreadStatic] private static bool _totalReported;

        internal static void ResetPerConversionState()
        {
            _totalGlyphBytes = 0;
            _totalReported   = false;
        }

        /// <summary>
        /// The glyph for <paramref name="code"/>, built once and memoised, or null when the font
        /// cannot supply one — having reported why.
        /// </summary>
        public static PsBuiltGlyph Build(PsInterpreter parent, PsType3Font font, int code)
        {
            if (font.Built.TryGetValue(code, out var cached)) return cached;

            if (font.Built.Count >= PsLimits.MaxType3Glyphs)
            {
                PostScriptDiagnostics.Report("PsType3GlyphBuilder",
                    $"this Type 3 font reached the {PsLimits.MaxType3Glyphs}-glyph cap " +
                    $"({nameof(PsLimits.MaxType3Glyphs)}); further glyphs are not built");
                return null;
            }

            int depth = (parent.GlyphBuild?.Depth ?? 0) + 1;
            if (depth > PsLimits.MaxType3NestingDepth)
            {
                PostScriptDiagnostics.Report("PsType3GlyphBuilder",
                    $"a Type 3 glyph procedure nested past {nameof(PsLimits.MaxType3NestingDepth)} " +
                    $"({PsLimits.MaxType3NestingDepth}); the glyph was not built. A glyph that shows " +
                    "text in its own font recurses");
                return null;
            }

            if (_totalGlyphBytes >= PsLimits.MaxType3TotalGlyphBytes)
            {
                if (!_totalReported)
                {
                    _totalReported = true;
                    PostScriptDiagnostics.Report("PsType3GlyphBuilder",
                        $"Type 3 glyph content reached {nameof(PsLimits.MaxType3TotalGlyphBytes)} " +
                        "in aggregate; further glyphs are not built. Reported once.");
                }
                return null;
            }

            var procedure = SelectProcedure(font, code, out bool viaCharProcs);
            if (procedure.Type != PsType.Array)
            {
                PostScriptDiagnostics.Report("PsType3GlyphBuilder",
                    $"Type 3 code {code} ('{font.GlyphNameFor(code)}') has no /BuildGlyph, " +
                    "/BuildChar or /CharProcs entry; nothing was drawn for it");
                return null;
            }

            var built = RunProcedure(parent, font, code, procedure, viaCharProcs, depth);
            font.Built[code] = built;
            return built;
        }

        /// <summary>
        /// PLRM's order: <c>/BuildGlyph</c> (it takes a glyph <i>name</i>, and is what a
        /// computed-glyph font uses), then <c>/BuildChar</c>, and only then the <c>/CharProcs</c>
        /// entry directly — the conventional <c>{ exch begin CharProcs exch get exec end }</c>
        /// BuildChar reaches CharProcs by itself.
        /// </summary>
        private static PsObject SelectProcedure(PsType3Font font, int code, out bool viaCharProcs)
        {
            viaCharProcs = false;

            if (font.BuildGlyph.Type == PsType.Array) return font.BuildGlyph;
            if (font.BuildChar.Type  == PsType.Array) return font.BuildChar;

            if (font.CharProcs != null &&
                font.CharProcs.TryGetName(font.GlyphNameFor(code), out var proc) &&
                proc.Type == PsType.Array)
            {
                viaCharProcs = true;
                return proc;
            }

            return PsObject.Null;
        }

        private static PsBuiltGlyph RunProcedure(PsInterpreter parent, PsType3Font font, int code,
                                                 PsObject procedure, bool viaCharProcs, int depth)
        {
            var emitter = new ContentStreamEmitter(PsLimits.MaxType3GlyphContentBytes);

            // Identity, NOT the conversion's base matrix: a CharProc stream is in GLYPH space, and
            // the renderer applies FontMatrix × text scale × Tm × CTM itself. Seeding the base
            // matrix here would apply it twice.
            var engine = new PsGraphicsEngine(emitter, PsMatrix.Identity,
                                              PsLimits.MaxType3GlyphContentBytes);
            // The font's collector, shared by all its glyphs: the font has one /Resources for every
            // CharProc. A collector a glyph restarted names at Im1 and F1, and the font's resources
            // became the last glyph's alone — an earlier glyph's /Im1 then drew the later one's image.
            engine.UseResources(font.GlyphCollector ??= new PdfResourceCollector());

            var build = new PsType3Build { Depth = depth };

            var child = new PsInterpreter(parent.Options, parent.Allocation,
                                          parent.SharedBudget, parent.Dicts)
            {
                Graphics   = engine,
                GlyphBuild = build,

                // The font state travels with it. PLRM keeps the font in the graphics state, so a
                // glyph procedure that shows text does so in the font that was current — including
                // its OWN font, which is how a composite glyph draws its base.
                //
                // Without this the child has no font, `show` inside a glyph reports "no font set"
                // and substitutes Helvetica — and, worse, MaxType3NestingDepth becomes unreachable,
                // because the recursion it guards can never start. A guard that cannot fire is
                // worse than no guard: it reads as protection that was never tested.
                CurrentType3Font   = parent.CurrentType3Font,
                CurrentFontIsType3 = parent.CurrentFontIsType3,
                CurrentFontMatrix  = parent.CurrentFontMatrix,
                CurrentFontEncoding = parent.CurrentFontEncoding,
                CurrentFontCMap    = parent.CurrentFontCMap,
                CurrentFontDict    = parent.CurrentFontDict,
                CurrentEmbeddedFont = parent.CurrentEmbeddedFont
            };

            // The font resource name lives on the graphics state, which the child engine has its
            // own of.
            child.Graphics.Gs.FontResourceName = parent.Graphics?.Gs.FontResourceName;

            // Nothing to park around the run any more: a font dictionary names its own embedded
            // program (PsTextOps.KeyEmbedded), so a glyph procedure's findfont leaves no thread-wide
            // "pending program" for the parent's next setfont to pick up.

            int dictDepth = parent.Dicts.Count;
            try
            {
                // PLRM runs a glyph procedure with the font dictionary on the dictionary stack.
                parent.Dicts.Begin(font.FontDict);

                if (!viaCharProcs)
                {
                    child.Operands.Push(PsObject.Dict(font.FontDict));
                    child.Operands.Push(font.BuildGlyph.Type == PsType.Array
                        ? PsObject.LitName(font.GlyphNameFor(code))
                        : PsObject.Int(code));
                }

                // The procedure's BODY is run, one object at a time — not the procedure object.
                //
                // Two wrong ways to do this, both of which produce an empty CharProc that declares
                // no width and looks exactly like a font whose glyph draws nothing:
                //   Run(new[]{ procedure })  — a procedure literal reaching the step loop as a
                //     top-level object is PUSHED, not run. That is the rule the whole language
                //     turns on: `/double { 2 mul } def` has to reach `def` as an operand.
                //   ExecuteResolvedValue(procedure) then Run(empty) — Run drains the exec stack
                //     inside its loop body, so an empty program never drains the frame.
                // Running the elements is what `Execute` itself documents as "an element of a
                // procedure that is running", and Run drains after each one.
                var body = procedure.AsArray();
                var program = new List<PsObject>(body.Length);
                for (int k = 0; k < body.Length; k++) program.Add(body[k]);

                child.Run(program);
            }
            finally
            {
                // Truncate rather than End(): a procedure that left `begin`s open must not leave the
                // parent's dictionary stack deeper than it found it.
                parent.Dicts.TruncateTo(dictDepth);
            }

            // The font's resources — its own collector, never the page's (see UseOwnResources) —
            // holding every glyph's so far: the font carries one /Resources for all its CharProcs.
            var glyphResources = engine.Resources.Build();
            if (glyphResources != null) font.GlyphResources = glyphResources;

            ReportIfNoWidth(build, font, code);

            // Through the EMITTER, so the numbers go through its one formatter and its non-finite
            // guard. Formatting them here meant a second number formatter in this assembly with a
            // different rounding rule and no guard — which is what it was until the duplication
            // audit, and exactly what ContentStreamEmitter.F's own doc warns a producer must not do.
            emitter.SetGlyphMetrics(build.Wx, build.Wy, build.ShapeOnly ? build.CacheBox : null);
            byte[] content = emitter.ToBytes();

            _totalGlyphBytes += content.Length;

            // The allocation budget never sees a ContentStreamEmitter's buffer, so the glyph bytes
            // are charged explicitly.
            parent.ChargeAllocation(content.Length, "Type 3 glyph");

            return new PsBuiltGlyph
            {
                Content       = content,
                Wx            = build.Wx,
                WidthDeclared = build.WidthDeclared
            };
        }

        /// <summary>
        /// Reports a glyph whose procedure declared no width. PLRM requires one; an invented
        /// advance would pile every following glyph on the same point just as silently.
        /// </summary>
        private static void ReportIfNoWidth(PsType3Build build, PsType3Font font, int code)
        {
            if (build.WidthDeclared) return;

            PostScriptDiagnostics.Report("PsType3GlyphBuilder",
                $"Type 3 code {code} ('{font.GlyphNameFor(code)}') declared no width — its " +
                "procedure called neither setcachedevice nor setcharwidth — so an advance of 0 " +
                "was written, and every glyph after it draws at the same point");
        }
    }
}
