// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.PostScript.Document;
using Tetitulz.Pdf.Plugins.PostScript.Emit;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// Owns the graphics state stack, the current path and the emitter, and holds the
    /// PostScript-to-PDF translation decisions that are not the emitter's business.
    ///
    /// <para>Split from <see cref="ContentStreamEmitter"/> on purpose: the emitter knows how to
    /// <i>write</i> PDF operators, this knows what PostScript <i>means</i>. The two impedance cases
    /// that need explaining both live here.</para>
    /// </summary>
    internal sealed class PsGraphicsEngine
    {
        private readonly List<PsGraphicsState> _stack = new();
        private readonly List<SaveMarker> _saveMarkers = new();
        private int _nextSaveId = 1;

        /// <summary>
        /// What <c>save</c> recorded, so <c>restore</c> can put the emitted stream back.
        ///
        /// <para><b><c>save</c>/<c>restore</c> are not <c>gsave</c>/<c>grestore</c>.</b> They also
        /// roll back <i>VM</i> — every dictionary, array and string allocated since. Doing that
        /// properly needs copy-on-write on every composite object, a large cost for a case that
        /// essentially never matters: over 95% of real EPS uses <c>save</c>/<c>restore</c> as a
        /// scoping bracket around a whole illustration and never relies on the VM half. So the
        /// graphics half is honoured exactly and the VM part is <b>reported once</b> rather than
        /// silently skipped. The dictionary stack is left alone, as PLRM specifies — <c>restore</c>
        /// does not pop it, it only forbids dictionaries newer than the save on it — and a
        /// comment here once claimed the opposite of what the code did.</para>
        /// </summary>
        private readonly record struct SaveMarker(int Id, int GraphicsDepth, int EmittedSaveDepth);

        private readonly int _maxEmittedBytes;

        public PsGraphicsEngine(ContentStreamEmitter emitter, PsMatrix baseMatrix,
                                int maxEmittedBytes = -1)
        {
            Emitter = emitter;
            Path = new PsPath();
            _stack.Add(new PsGraphicsState { Ctm = baseMatrix });
            BaseMatrix = baseMatrix;
            emitter.StartCtm(baseMatrix);
            emitter.NameColorSpace = NameColorSpace;
            emitter.NameTransfer = NameTransfer;
            emitter.NameDevice = NameDevice;
            _maxEmittedBytes = maxEmittedBytes < 0 ? PsLimits.MaxEmittedContentBytes : maxEmittedBytes;
        }

        /// <summary>
        /// Charges work this engine does for one operator beyond the operator's own tick — a clip
        /// region taken again after a widening — to the interpreter it draws for, which sets it.
        /// </summary>
        public Action<long> Work { get; set; }

        /// <summary>Charges the memory a clip or a clip save holds, naming the operator; set by the interpreter.</summary>
        public Action<long, string> Allocate { get; set; }

        /// <summary>What a clip region's entry is charged beyond its path's segments.</summary>
        private const int BytesPerClip = 64;
        /// <summary>
        /// The current page's emitter. Settable because a page break in document mode swaps in a
        /// fresh one; a fragment conversion never replaces it.
        /// </summary>
        public ContentStreamEmitter Emitter { get; private set; }
        public PsPath Path { get; }
        public PsMatrix BaseMatrix { get; }

        /// <summary>
        /// Fonts and image XObjects the emitted stream names.
        ///
        /// <para>Replaceable only through <see cref="UseOwnResources"/>, for a Type 3 glyph build.
        /// A glyph's CharProc needs its <b>own</b> collector: pointing it at the page's would make
        /// the font's <c>/Resources</c> the very dictionary that contains the font — a cycle — and
        /// <c>PostScriptPageCollector.AttachResources</c> hands one dictionary instance to every
        /// page, so glyph resources written there would attach to every page at once.</para>
        /// </summary>
        public PdfResourceCollector Resources { get; private set; } = new();

        /// <summary>Gives this engine a private resource collector. See <see cref="Resources"/>.</summary>
        public void UseOwnResources() => Resources = new PdfResourceCollector();

        /// <summary>
        /// Gives this engine a collector it shares with others of its kind — a Type 3 font's, for
        /// every glyph of the font, whose CharProcs share one <c>/Resources</c>. See <see cref="Resources"/>.
        /// </summary>
        public void UseResources(PdfResourceCollector shared) => Resources = shared;

        /// <summary>A colour space named in the resources this engine's stream uses, whichever they are now.</summary>
        private string NameColorSpace(Tetitulz.Pdf.Core.Objects.PdfObject space) => Resources.AddColorSpace(space);

        /// <summary>The graphics state setting a transfer function, named in the resources this engine's stream uses.</summary>
        private string NameTransfer(Tetitulz.Pdf.Core.Objects.PdfObject transfer) => Resources.AddTransferState(transfer);

        /// <summary><c>settransfer</c> and <c>setcolortransfer</c>: the state's transfer function, written as it is set.</summary>
        public void SetTransfer(Tetitulz.Pdf.Core.Objects.PdfObject transfer)
        {
            Gs.Transfer = transfer;
            Emitter.SetTransfer(transfer);
        }

        /// <summary>The graphics state taking the stream between two sets of device parameters, named in the resources this engine's stream uses.</summary>
        private string NameDevice(PsDeviceParameters from, PsDeviceParameters to) => Resources.AddDeviceState(from, to);

        /// <summary><c>setoverprint</c>, <c>setflat</c> and the rest: the state's device parameters, written as they are set.</summary>
        public void SetDevice(PsDeviceParameters device)
        {
            Gs.Device = device;
            Emitter.SetDevice(device);
        }

        /// <summary>
        /// The metric box a run of standard-14 text is taken to cover, as fractions of the em —
        /// the ascender above the baseline, the descender below it. One pair, shared with
        /// <c>charpath</c>'s box (<c>PsTextOps.CharPath</c>).
        /// </summary>
        internal const double TextAscender = 0.72, TextDescender = -0.21;

        /// <summary>
        /// Grows the drawn extent by a run of text at the pen position: <paramref name="along"/>
        /// text-space units of baseline with the metric box across it, laid through the font
        /// matrix and then the CTM. Four corners, because a rotated font's box is not axis-aligned
        /// — two opposite corners of a quarter-turned run span a box of zero width.
        /// </summary>
        public void NoteTextExtent(double x, double y, double along, in PsMatrix fontMatrix)
        {
            var fm = fontMatrix;
            Corner(0, TextDescender); Corner(along, TextDescender);
            Corner(along, TextAscender); Corner(0, TextAscender);

            void Corner(double u, double v)
            {
                var (dx, dy) = fm.ApplyDelta(u, v);
                var (px, py) = Gs.Ctm.Apply(x + dx, y + dy);
                Include(px, py);
            }
        }

        /// <summary>Grows the drawn extent by a form's <paramref name="bbox"/> under its <paramref name="matrix"/> and the CTM.</summary>
        public void NoteFormExtent(double[] bbox, in PsMatrix matrix)
        {
            var m = matrix.Concat(Gs.Ctm);
            var (ax, ay) = m.Apply(bbox[0], bbox[1]);
            var (bx, by) = m.Apply(bbox[2], bbox[1]);
            var (cx, cy) = m.Apply(bbox[2], bbox[3]);
            var (dx, dy) = m.Apply(bbox[0], bbox[3]);
            Include(ax, ay); Include(bx, by); Include(cx, cy); Include(dx, dy);
        }

        /// <summary>Grows the drawn extent by an image placed under <paramref name="placement"/>.</summary>
        public void NoteImageExtent(in PsMatrix placement)
        {
            var m = placement.Concat(Gs.Ctm);
            var (ax, ay) = m.Apply(0, 0);
            var (bx, by) = m.Apply(1, 0);
            var (cx, cy) = m.Apply(1, 1);
            var (dx, dy) = m.Apply(0, 1);
            Include(ax, ay); Include(bx, by); Include(cx, cy); Include(dx, dy);
        }

        /// <summary>
        /// Grows the drawn extent by a <c>shfill</c>: the clip in force, which is what it paints, or —
        /// with none — the shading's own <paramref name="bbox"/> in user space. With neither the fill
        /// is unbounded and the page keeps the extent the other marks give it.
        /// </summary>
        public void NoteShadingExtent(double[] bbox)
        {
            double[] clip = Gs.ClipQuadDevice;
            if (clip != null)
            {
                for (int k = 0; k + 1 < clip.Length; k += 2) Include(clip[k], clip[k + 1]);
                return;
            }
            if (bbox == null || bbox.Length != 4) return;
            var (ax, ay) = Gs.Ctm.Apply(bbox[0], bbox[1]);
            var (bx, by) = Gs.Ctm.Apply(bbox[2], bbox[1]);
            var (cx, cy) = Gs.Ctm.Apply(bbox[2], bbox[3]);
            var (dx, dy) = Gs.Ctm.Apply(bbox[0], bbox[3]);
            Include(ax, ay); Include(bx, by); Include(cx, cy); Include(dx, dy);
        }

        /// <summary>The current graphics state.</summary>
        public PsGraphicsState Gs => _stack[_stack.Count - 1];


        // One-shot, like every other cap report in this assembly: a `{ 1e200 1e200 mul dup scale }
        // loop` would otherwise compose a message and fire every OnEvent subscriber per iteration.
        private bool _nonFiniteMatrixReported;

        // ------------------------------------------------------------------
        //  gsave / grestore
        // ------------------------------------------------------------------

        /// <summary>
        /// Maximum graphics-state stack depth, the base state included.
        ///
        /// <para>Core's number rather than a new <c>PsLimits</c> entry, which is the rule that
        /// table's own header states: where Core already owns a value — graphics-state depth is its
        /// worked example — the code reads <c>PdfLimits</c>. It is also the depth
        /// <see cref="ContentStreamEmitter.Save"/> stops emitting <c>q</c> at, so the engine can no
        /// longer retain a level the emitter would refuse to write.</para>
        /// </summary>
        private static int MaxStackDepth => PdfLimits.MaxGraphicsStateStackDepth;

        /// <summary>
        /// Pushes a copy of the current state, refusing past <see cref="MaxStackDepth"/>.
        ///
        /// <para>The clone was the one composite <c>gsave</c> could grow with <b>nothing</b>
        /// watching. <see cref="ContentStreamEmitter.Save"/> caps the emitted <c>q</c> and returns
        /// quietly, which bounds the <i>output</i> and not the engine: <c>{ gsave } loop</c> went on
        /// cloning a ~170-byte state per iteration until <c>MaxTotalOps</c> ran out — millions of
        /// live objects, none of them visible to <c>PsAllocationBudget</c>, and one undeduplicated
        /// diagnostic per iteration on top.</para>
        ///
        /// <para><c>limitcheck</c> rather than a silent drop, matching <c>PsPath.Append</c>: an
        /// error is catchable by <c>stopped</c> and recoverable at top level, where dropping the
        /// push would leave the matching <c>grestore</c> popping a level that was never opened.</para>
        /// </summary>
        private void PushState(string op)
        {
            if (_stack.Count >= MaxStackDepth)
                throw new Interpreter.PsErrorException(
                    Interpreter.PsErrorKind.LimitCheck, op,
                    $"the graphics-state stack exceeds {MaxStackDepth} levels");

            // The live path goes with the state being saved (PLRM §4.2), to come back when it does.
            Gs.SavedPath = Path.Snapshot();
            _stack.Add(Gs.Clone());
        }

        /// <summary>
        /// Puts back the path the now-current state was saved with. It is in that state's user
        /// space, so no re-expression follows.
        /// </summary>
        private void RestoreSavedPath()
        {
            if (Gs.SavedPath is { } saved) Path.RestoreFrom(saved);
        }

        public void GSave()
        {
            int floor = Emitter.SaveDepth;
            PushState("gsave");
            Gs.EmittedFloor = floor;
            Emitter.Save();
        }

        public void GRestore()
        {
            // PLRM: grestore with nothing to restore is a no-op — the path stays as it is.
            if (_stack.Count <= 1) return;
            if (AtSaveLevel) { RestoreWithoutPopping("grestore"); return; }
            PopState();
            RestoreSavedPath();
            Reconcile();
        }

        /// <summary>
        /// Whether no <c>gsave</c> has run since the innermost unmatched <c>save</c>: the live state is
        /// the one the save pushed, and the save's own snapshot is below it.
        /// </summary>
        private bool AtSaveLevel
            => _saveMarkers.Count > 0 && _saveMarkers[_saveMarkers.Count - 1].GraphicsDepth + 1 == _stack.Count;

        /// <summary>
        /// PLRM <c>grestore</c> at a save level: "restores the graphics state from the top of the
        /// stack, but does not pop the stack". The live state is replaced by a copy of the save's
        /// snapshot, so a later <c>restore</c> still finds the snapshot where it left it — popping it
        /// made the snapshot the live state, and what followed changed it.
        /// </summary>
        private void RestoreWithoutPopping(string op)
        {
            PopState();
            RestoreSavedPath();
            int floor = Emitter.SaveDepth;
            PushState(op);
            Gs.EmittedFloor = floor;
            Emitter.Save();
            Reconcile();
        }

        /// <summary>
        /// Pops one graphics state and the emitted levels it opened — its <c>q</c>, a clip's bracket,
        /// one a widening re-opened. The CTM change is the caller's to carry to the path, and the
        /// stream's clip and transform the caller's to reconcile.
        /// </summary>
        private void PopState()
        {
            // PLRM: grestore with nothing to restore is a no-op, not an error.
            if (_stack.Count <= 1) return;
            int floor = Gs.EmittedFloor;
            _stack.RemoveAt(_stack.Count - 1);
            CloseTo(floor);
        }

        /// <summary>Closes emitted levels down to <paramref name="depth"/>, returning how many it closed.</summary>
        private int CloseTo(int depth)
        {
            int closed = 0;
            while (Emitter.SaveDepth > depth)
            {
                int before = Emitter.SaveDepth;
                Emitter.Restore();
                if (Emitter.SaveDepth == before) break;   // a stream past its cap closes nothing more
                closed++;
            }
            return closed;
        }

        /// <summary><c>grestoreall</c> — unwind to the innermost <c>save</c>, or to the bottom.</summary>
        public void GRestoreAll()
        {
            // Down to the state the innermost save pushed, which grestoreall restores without popping
            // (PLRM), as grestore does at a save level; with no save, down to the bottom.
            int floor = _saveMarkers.Count > 0 ? _saveMarkers[_saveMarkers.Count - 1].GraphicsDepth + 1 : 1;
            if (_stack.Count < floor || (_stack.Count == floor && !AtSaveLevel)) return;
            while (_stack.Count > floor) PopState();
            if (AtSaveLevel) { RestoreWithoutPopping("grestoreall"); return; }
            RestoreSavedPath();
            Reconcile();
        }

        /// <summary>
        /// Keeps the current path where it was drawn when the CTM changes under it.
        ///
        /// <para>PLRM §4.4: the current path is in <b>device</b> space — a segment, once appended,
        /// does not move when the CTM does. This engine keeps the path in user space so the emitter
        /// can write it after the <c>cm</c> in force when it is painted, and that is right only while
        /// the CTM stands still. So every CTM change re-expresses the open path in the new user
        /// space, <c>old × new⁻¹</c>, which is exact for any affine change. Without it the Blue Book
        /// ellipse — <c>matrix currentmatrix … translate … scale … arc setmatrix stroke</c> — came
        /// out as a unit circle at the origin, a line built before <c>2 2 scale</c> came out twice as
        /// long, and <c>rectstroke</c>'s pen matrix moved the rectangle it was meant to stroke.</para>
        ///
        /// <para>O(segments), charged by the operators that change the CTM — <c>PsGraphicsOps</c>
        /// ticks <c>Path.Count</c> before calling in — like <c>clip</c> and <c>pathbbox</c>.</para>
        /// </summary>
        private void ReExpressPath(in PsMatrix oldCtm, in PsMatrix newCtm)
        {
            if (Path.IsEmpty || oldCtm.Equals(newCtm)) return;

            // Every CTM this engine accepts is finite and non-singular, so the inverse exists; a
            // base matrix the host supplied is the one exception, and the path is left as it is.
            var inverse = newCtm.Invert();
            if (inverse == null) return;

            Path.Transform(oldCtm.Concat(inverse.Value));
        }

        public int Save()
        {
            // The depths the marker records are the ones in force BEFORE the push, so they are read
            // first — but the push happens before the marker is recorded, because it can raise
            // limitcheck. A marker left behind for a save that never completed is unreachable: only
            // a matching `restore` removes one, and the program never received the save object to
            // restore with. `save` counts against the same stack as `gsave`, which is what bounds
            // `_saveMarkers` too.
            int graphicsDepth = _stack.Count;
            int emittedDepth  = Emitter.SaveDepth;

            // A save brackets the graphics state as well, so it emits a q like gsave does.
            PushState("save");
            Gs.EmittedFloor = emittedDepth;

            int id = _nextSaveId++;
            _saveMarkers.Add(new SaveMarker(id, graphicsDepth, emittedDepth));
            Emitter.Save();
            return id;
        }

        /// <summary>Returns false when the id does not name an open <c>save</c> (invalidrestore).</summary>
        public bool Restore(int id)
        {
            int index = _saveMarkers.FindLastIndex(m => m.Id == id);
            if (index < 0) return false;

            var marker = _saveMarkers[index];
            _saveMarkers.RemoveRange(index, _saveMarkers.Count - index);

            while (_stack.Count > marker.GraphicsDepth) _stack.RemoveAt(_stack.Count - 1);
            CloseTo(marker.EmittedSaveDepth);
            RestoreSavedPath();
            Reconcile();
            return true;
        }

        // ------------------------------------------------------------------
        //  Transformations
        // ------------------------------------------------------------------

        /// <summary>Prepends <paramref name="m"/> to the CTM and emits the matching <c>cm</c>.</summary>
        public void Concat(in PsMatrix m)
        {
            // Refused for the same reason a singular matrix is, and reported separately because the
            // cause is different: `1e200 1e200 mul dup scale` produces an INFINITE matrix, which
            // `IsSingular` cannot see (Math.Abs(∞) < 1e-9 is false) and which the emitter's write
            // boundary then flattened to `0 0 0 0 0 0 cm` — blanking every mark after it while
            // reporting only that "a coordinate was replaced with 0".
            if (!m.IsFinite)
            {
                if (!_nonFiniteMatrixReported)
                {
                    _nonFiniteMatrixReported = true;
                    PostScriptDiagnostics.Report("PsGraphicsEngine.Concat",
                        "a non-finite matrix was refused; the transform was ignored and drawing " +
                        "continues under the previous one. Reported once per page.");
                }
                return;
            }

            if (m.IsSingular)
            {
                // A singular cm makes everything after it invisible in most readers, which is a
                // worse outcome than ignoring one operator.
                PostScriptDiagnostics.Report("PsGraphicsEngine.Concat",
                    "a singular matrix was not applied; the transform was ignored");
                return;
            }

            var old = Gs.Ctm;
            Gs.Ctm = m.Concat(Gs.Ctm);
            Emitter.ConcatMatrix(m, Gs.Ctm);
            ReExpressPath(old, Gs.Ctm);
        }

        /// <summary>
        /// <c>setmatrix</c> — <b>absolute</b>, where PDF's <c>cm</c> is relative.
        ///
        /// <para>The delta is <c>inverse(current) × target</c>. This is the reason the matrix type
        /// carries doubles: a chain of inverses accumulates error, and single precision shows it as
        /// visible drift.</para>
        /// </summary>
        public void SetMatrix(in PsMatrix target)
        {
            if (!target.IsFinite)
            {
                if (!_nonFiniteMatrixReported)
                {
                    _nonFiniteMatrixReported = true;
                    PostScriptDiagnostics.Report("PsGraphicsEngine.SetMatrix",
                        "a non-finite matrix was refused; the transform was left unchanged. " +
                        "Reported once per page.");
                }
                return;
            }

            if (target.IsSingular)
            {
                PostScriptDiagnostics.Report("PsGraphicsEngine.SetMatrix",
                    "a singular matrix was refused; the transform was left unchanged");
                return;
            }

            var inverse = Gs.Ctm.Invert();
            if (inverse == null)
            {
                PostScriptDiagnostics.Report("PsGraphicsEngine.SetMatrix",
                    "the current transform is singular and cannot be inverted; setmatrix was ignored");
                return;
            }

            var delta = target.Concat(inverse.Value);
            var old = Gs.Ctm;
            Gs.Ctm = target;
            Emitter.ConcatMatrix(delta, target);
            ReExpressPath(old, Gs.Ctm);
        }

        // ------------------------------------------------------------------
        //  Painting
        // ------------------------------------------------------------------

        public void Fill(bool evenOdd)
        {
            // `strokepath fill` paints the stroke outline's interior, which is exactly the region
            // PDF's `S` paints from this same path. See PsPath.StrokeOutlinePending for why the
            // outline is never built. The even-odd variant is the one place the identity is not
            // total: `eofill` over a self-overlapping outline leaves the overlaps unpainted, where
            // `S` fills them.
            if (Path.StrokeOutlinePending)
            {
                if (evenOdd)
                    PostScriptDiagnostics.Report("PsGraphicsEngine.Fill",
                        "'strokepath eofill' was emitted as a stroke; the even-odd rule would leave " +
                        "a self-overlapping outline's overlaps unpainted and a stroke fills them");

                Stroke();
                return;
            }

            NoteMarkExtent();
            Emitter.Fill(Path, Gs, evenOdd);
            Path.Clear();
        }

        public void Stroke()
        {
            if (Path.StrokeOutlinePending)
            {
                // `strokepath stroke` outlines the outline — a double contour this cannot express.
                // Emitting the single stroke draws the shape the program is tracing, at the wrong
                // weight, which is the closest thing available and is reported as such.
                PostScriptDiagnostics.Report("PsGraphicsEngine.Stroke",
                    "'strokepath stroke' was emitted as a single stroke; stroking the outline of a " +
                    "stroke would need the outline as a real path, which this converter does not build");
            }

            NoteMarkExtent(Gs.LineWidth / 2);
            Emitter.Stroke(Path, Gs);
            Path.Clear();
        }

        /// <summary>
        /// Grows the drawn extent by the current path, transformed into output space.
        ///
        /// <para>This is what supplies a page's <c>/MediaBox</c> when the source declared no
        /// <c>%%BoundingBox</c> — the alternative would be defaulting to Letter and cropping
        /// whatever fell outside it.</para>
        /// </summary>
        /// <param name="pad">Half the line width, so a stroke's extent includes its own thickness.</param>
        private void NoteMarkExtent(double pad = 0)
        {
            var box = Path.BoundingBox();
            if (box == null) return;

            // Four corners, because a rotation makes the transformed box larger than the
            // transform of two opposite corners.
            var (ax, ay) = Gs.Ctm.Apply(box.Value.X0 - pad, box.Value.Y0 - pad);
            var (bx, by) = Gs.Ctm.Apply(box.Value.X1 + pad, box.Value.Y0 - pad);
            var (cx, cy) = Gs.Ctm.Apply(box.Value.X1 + pad, box.Value.Y1 + pad);
            var (dx, dy) = Gs.Ctm.Apply(box.Value.X0 - pad, box.Value.Y1 + pad);

            Include(ax, ay); Include(bx, by); Include(cx, cy); Include(dx, dy);
        }

        private void Include(double x, double y)
        {
            // A non-finite coordinate would poison the extent and reach /MediaBox, where a page
            // with no numeric width or height is unopenable. Ignored here so DrawnExtent stays
            // finite and PageSize falls through to its declared box or Letter, exactly as it
            // already does when nothing was drawn; the emitter reports the coordinate itself.
            if (!PsNumeric.IsFinite(x) || !PsNumeric.IsFinite(y)) return;

            if (!_hasMarks)
            {
                _minX = _maxX = x;
                _minY = _maxY = y;
                _hasMarks = true;
                return;
            }
            if (x < _minX) _minX = x;
            if (y < _minY) _minY = y;
            if (x > _maxX) _maxX = x;
            if (y > _maxY) _maxY = y;
        }

        private bool _hasMarks;
        private double _minX, _minY, _maxX, _maxY;

        /// <summary>The extent of everything drawn on the current page, or null if nothing was.</summary>
        public PsBoundingBox? DrawnExtent =>
            _hasMarks ? new PsBoundingBox(_minX, _minY, _maxX, _maxY) : null;

        /// <summary>
        /// Ends the current page: returns its content stream and starts a fresh emitter and
        /// graphics state.
        ///
        /// <para>The <b>VM is deliberately untouched</b> — the interpreter's dictionaries, and
        /// therefore every procedure a prologue defined, survive into the next page. PostScript
        /// specifies exactly that, and a converter that reset the dictionaries would break every
        /// multi-page document whose pages call prologue procedures.</para>
        ///
        /// <para><b>So is the font.</b> <c>showpage</c> resets the graphics state the way
        /// <c>initgraphics</c> does, and PLRM lists what that touches — transform, path, clip,
        /// colour, line parameters — and not the font. A prologue that sets the font once draws
        /// every page in it; the fresh state built here carried no font, so page 2 drew in Helvetica
        /// at size 12 and reported that no font was set.</para>
        /// </summary>
        public (byte[] Content, PsBoundingBox? Extent) EndPage()
        {
            byte[] content = Emitter.ToBytes();
            var extent = DrawnExtent;
            EndedPageRotation = Emitter.DominantTextRotation();

            Emitter = new ContentStreamEmitter(_maxEmittedBytes);
            Emitter.StartCtm(BaseMatrix);
            Emitter.NameColorSpace = NameColorSpace;
            Emitter.NameTransfer = NameTransfer;
            Emitter.NameDevice = NameDevice;
            ResetCurrentState();
            _hasMarks = false;

            return (content, extent);
        }

        /// <summary>
        /// What <c>showpage</c> does to the graphics state (PLRM: <c>initgraphics</c> on the current
        /// state): the live state is replaced by a fresh one, the font kept, and the path cleared.
        /// <b>The states below it stay</b> — <c>initgraphics</c> touches no stack, so
        /// <c>gsave showpage grestore</c> brings back the transform, colour and clip the <c>gsave</c>
        /// saved; discarding them left the next page in the default state. The new emitter opens at
        /// depth 0, so every kept state's bracket floor is 0: a <c>grestore</c> to one closes nothing
        /// and <see cref="Reconcile"/> writes the transform and clip it wants. The save markers are
        /// cleared, as the documented save/restore choice across a page boundary requires.
        /// </summary>
        private void ResetCurrentState()
        {
            _stack[_stack.Count - 1] = FreshState();
            foreach (var state in _stack) state.EmittedFloor = 0;
            _saveMarkers.Clear();
            Path.Clear();
        }

        /// <summary>The clockwise turn that reads the page <see cref="EndPage"/> last ended by its predominant text (<c>ContentStreamEmitter.DominantTextRotation</c>).</summary>
        public int EndedPageRotation { get; private set; }

        /// <summary>The initial graphics state for a new page, keeping the font in force (see <see cref="EndPage"/>).</summary>
        private PsGraphicsState FreshState() =>
            new() { Ctm = BaseMatrix, FontResourceName = Gs.FontResourceName, Font = Gs.Font };

        /// <summary>
        /// <c>clip</c> — PostScript leaves the path intact afterwards, so the path is <b>not</b>
        /// cleared here even though the emitted <c>n</c> clears PDF's.
        ///
        /// <para><b>The clip is bracketed by a <c>q</c> when nothing else brackets it.</b> PDF can
        /// only escape a clip by restoring past a <c>q</c>, and PostScript's <c>clip</c> opens no
        /// scope of its own. Unbracketed, a clip taken at save depth 0 would be permanent for the
        /// rest of the stream: <see cref="InitClip"/> would find nothing to close, so the <c>W n</c>
        /// would stay in force. The <c>gsave … clip … grestore</c> idiom needs no bracket, because
        /// there the <c>gsave</c> supplies it.</para>
        ///
        /// <para>One level is enough, and the depth test is what keeps it to one: a second clip at
        /// the same depth <i>intersects</i> with the first, which is exactly what <c>clip</c> means,
        /// and the same <c>Q</c> drops both. Opening a bracket per clip would instead march a
        /// clipping loop into <c>PdfLimits.MaxGraphicsStateStackDepth</c>.</para>
        ///
        /// <para>The clip becomes the state's region's last entry (<see cref="PsClip"/>), its path in
        /// device space, charged by its segments: the history a widening takes the region again from.</para>
        /// </summary>
        public void Clip(bool evenOdd)
        {
            // Match the emitter's own guard: an empty path emits nothing, so it must not be
            // bracketed either, or the q would be left with no clip to justify it.
            if (Path.IsEmpty) return;

            if (Path.StrokeOutlinePending)
            {
                // PDF has no way to clip to a stroke outline, and the path itself is NOT a usable
                // stand-in: for a thin line it encloses almost no area, so clipping to it would
                // blank everything drawn afterwards. Skipping leaves more visible than intended,
                // which is the recoverable direction.
                PostScriptDiagnostics.Report("PsGraphicsEngine.Clip",
                    "'strokepath clip' was skipped: PDF cannot clip to a stroke outline, and " +
                    "clipping to the un-outlined path would hide everything drawn after it. " +
                    "Subsequent marks are NOT clipped");
                return;
            }

            Allocate?.Invoke(BytesPerClip + (long)Path.Count * PsPath.BytesPerSegment, evenOdd ? "eoclip" : "clip");
            Reconcile();
            var (quad, exact) = ClipGeometry(Gs.Clip);
            var region = new PsClip(Gs.Clip, PsClip.Transformed(Path.Segments, Gs.Ctm), evenOdd, quad, exact);

            if (Emitter.SaveDepth == 0) Emitter.Save();

            Emitter.Clip(Path, evenOdd, region);
            Gs.Clip = region;
        }

        /// <summary>
        /// <c>clipsave</c> (PLRM §8.2): the clipping region pushed onto the clipping path stack, which
        /// is part of the graphics state. Nothing is written: the region is the state's until a
        /// <c>cliprestore</c> asks for it back.
        /// </summary>
        public void ClipSave()
        {
            Allocate?.Invoke(BytesPerClip, "clipsave");
            Gs.ClipStack = new PsClipSave(Gs.Clip, Gs.ClipStack);
        }

        /// <summary>
        /// <c>cliprestore</c>: the region on top of the clipping path stack made current and popped;
        /// with the stack empty, the region the innermost <c>gsave</c> saved — the page's at the bottom
        /// — which is how interpreters read it. A wider region than the one in
        /// force is brought into the stream by <see cref="Reconcile"/>.
        /// </summary>
        public void ClipRestore()
        {
            if (Gs.ClipStack != null)
            {
                Gs.Clip = Gs.ClipStack.Region;
                Gs.ClipStack = Gs.ClipStack.Below;
            }
            else
                Gs.Clip = _stack.Count > 1 ? _stack[_stack.Count - 2].Clip : null;
            Reconcile();
        }

        /// <summary><c>gstate</c> and <c>currentgstate</c>: the current state and path copied into <paramref name="target"/>.</summary>
        public void CopyStateTo(PsGStateObject target)
        {
            target.State = Gs.Clone();
            target.Path = Path.Snapshot();
        }

        /// <summary>
        /// <c>setgstate</c> (PLRM §8.2): <paramref name="saved"/>'s state made current — transform,
        /// colour, line style, font, path, clip and clipping path stack — on the current level, which
        /// keeps its own bracket and the path its <c>gsave</c> saved. The transform and the clip are
        /// brought into the stream by <see cref="Reconcile"/>; the colour and the line style are
        /// written by the next paint, as after any change.
        /// </summary>
        public void SetGState(PsGStateObject saved)
        {
            if (saved.State == null) return;
            var next = saved.State.Clone();
            next.EmittedFloor = Gs.EmittedFloor;
            next.SavedPath = Gs.SavedPath;
            _stack[_stack.Count - 1] = next;
            Path.RestoreFrom(saved.Path);
            Reconcile();
        }

        /// <summary>
        /// Brings the stream's clip and transform to the current state's after a change PDF has no
        /// operator for: a wider region (<c>cliprestore</c>, <c>initclip</c>, <c>setgstate</c>), or a
        /// return to a level whose bracket an earlier widening closed. Emitted levels are closed until
        /// the region the stream holds is one the state's extends — each <c>Q</c> rolling back the
        /// colour, line style, transform and clip, which the emitter tracks — the transform's
        /// difference is written as one <c>cm</c>, and the clips the state took beyond that region are
        /// taken again, under a bracket of their own at depth 0 so they can be widened in turn. The
        /// common case, a <c>grestore</c> whose <c>Q</c> already put everything back, finds both equal
        /// and writes nothing. Charged a unit a level closed and a unit a segment taken again.
        /// </summary>
        private void Reconcile()
        {
            // A stream past its cap writes nothing more, so there is nothing to bring into step: each
            // paint after the cap walked and copied the whole clip chain (PsClip.After) and every
            // clip it tried to take was refused, uncharged.
            if (Emitter.Truncated) return;

            PsClip wanted = Gs.Clip;
            long work = 0;
            while (Emitter.SaveDepth > 0 && !PsClip.Extends(wanted, Emitter.EmittedClip))
            {
                int before = Emitter.SaveDepth;
                Emitter.Restore();
                if (Emitter.SaveDepth == before) break;   // a stream past its cap closes nothing more
                work++;
            }

            var held = Emitter.EmittedCtm;
            if (!held.Equals(Gs.Ctm) && held.Invert() is PsMatrix toHeld)
                Emitter.ConcatMatrix(Gs.Ctm.Concat(toHeld), Gs.Ctm);
            if (!ReferenceEquals(Emitter.EmittedTransfer, Gs.Transfer)) Emitter.SetTransfer(Gs.Transfer);
            Emitter.SetDevice(Gs.Device);

            if (!ReferenceEquals(wanted, Emitter.EmittedClip) && PsClip.Extends(wanted, Emitter.EmittedClip))
            {
                if (Emitter.SaveDepth == 0) Emitter.Save();
                foreach (var clip in PsClip.After(wanted, Emitter.EmittedClip))
                    work += Emitter.TakeClip(clip);
            }
            if (work > 0) Work?.Invoke(work);
        }

        // ------------------------------------------------------------------
        //  clippath
        // ------------------------------------------------------------------

        /// <summary>
        /// The page's default clipping path, in <b>default user space</b> — the box
        /// <c>clippath</c> answers with before anything has clipped.
        ///
        /// <para>Set from the document's <c>%%BoundingBox</c> when it declared one, because
        /// <c>clippath pathbbox</c> is overwhelmingly written to discover exactly that. With no
        /// declaration it stays null and US Letter is used, which is what a printer's imageable area
        /// would have been; either way the answer is reported so a program that sizes content to it
        /// is not silently laid out against a guess.</para>
        /// </summary>
        public PsBoundingBox? DefaultClipBox { get; set; }

        /// <summary>
        /// The medium <c>setpagedevice</c>'s <c>/PageSize</c> named, from the origin, or null when no
        /// page device was set — each page ended takes the one in force, ahead of a declared
        /// <c>%%BoundingBox</c> and the drawn extent, since it names the sheet itself.
        /// </summary>
        public PsBoundingBox? PageMedia { get; set; }

        private bool _defaultClipReported;
        private bool _inexactClipReported;

        /// <summary>What a clip of the current path leaves the region covering, after <paramref name="previous"/>, for <c>clippath</c> to read back.</summary>
        private (double[] Quad, bool Exact) ClipGeometry(PsClip previous)
        {
            double[] quad = DeviceQuadOfCurrentPath();
            if (quad == null) return (previous?.Quad, previous?.Exact == true);
            if (previous?.Quad == null) return (quad, _lastQuadWasExact);

            // A second clip INTERSECTS the first. Intersecting two quadrilaterals is a polygon
            // clip; intersecting their bounding boxes is not, and is an over-estimate — see
            // PsGraphicsState.ClipQuadDevice for why erring large is the safe direction here.
            var a = BoxOf(previous.Quad);
            var b = BoxOf(quad);

            double x0 = Math.Max(a.X0, b.X0), y0 = Math.Max(a.Y0, b.Y0);
            double x1 = Math.Min(a.X1, b.X1), y1 = Math.Min(a.Y1, b.Y1);

            // Disjoint clips leave nothing. An empty box is the honest answer and stays valid.
            if (x1 < x0) x1 = x0;
            if (y1 < y0) y1 = y0;

            return (new[] { x0, y0, x1, y0, x1, y1, x0, y1 }, false);
        }

        private bool _lastQuadWasExact;

        /// <summary>
        /// The current path's four device-space corners: exactly, when it is a four-point
        /// straight-edged subpath, and its bounding box otherwise.
        /// </summary>
        private double[] DeviceQuadOfCurrentPath()
        {
            var points = new List<(double X, double Y)>();
            bool straight = true;
            int subpaths = 0;

            foreach (var s in Path.Segments)
            {
                switch (s.Kind)
                {
                    case PsSegmentKind.Move:
                        subpaths++;
                        points.Add((s.X3, s.Y3));
                        break;
                    case PsSegmentKind.Line:
                        points.Add((s.X3, s.Y3));
                        break;
                    case PsSegmentKind.Curve:
                        straight = false;
                        points.Add((s.X1, s.Y1));
                        points.Add((s.X2, s.Y2));
                        points.Add((s.X3, s.Y3));
                        break;
                }
            }

            if (points.Count == 0) return null;

            for (int k = 0; k < points.Count; k++)
            {
                var (dx, dy) = Gs.Ctm.Apply(points[k].X, points[k].Y);
                if (!PsNumeric.IsFinite(dx) || !PsNumeric.IsFinite(dy)) return null;
                points[k] = (dx, dy);
            }

            // A closing point that repeats the start is the same corner written twice.
            if (points.Count == 5 && Near(points[0], points[4])) points.RemoveAt(4);

            _lastQuadWasExact = straight && subpaths == 1 && points.Count == 4;
            if (_lastQuadWasExact)
                return new[] { points[0].X, points[0].Y, points[1].X, points[1].Y,
                               points[2].X, points[2].Y, points[3].X, points[3].Y };

            double x0 = double.MaxValue, y0 = double.MaxValue;
            double x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (x, y) in points)
            {
                if (x < x0) x0 = x;
                if (y < y0) y0 = y;
                if (x > x1) x1 = x;
                if (y > y1) y1 = y;
            }
            return new[] { x0, y0, x1, y0, x1, y1, x0, y1 };
        }

        private static bool Near((double X, double Y) a, (double X, double Y) b) =>
            Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

        private static (double X0, double Y0, double X1, double Y1) BoxOf(double[] quad)
        {
            double x0 = quad[0], y0 = quad[1], x1 = quad[0], y1 = quad[1];
            for (int k = 2; k < quad.Length; k += 2)
            {
                if (quad[k]     < x0) x0 = quad[k];
                if (quad[k + 1] < y0) y0 = quad[k + 1];
                if (quad[k]     > x1) x1 = quad[k];
                if (quad[k + 1] > y1) y1 = quad[k + 1];
            }
            return (x0, y0, x1, y1);
        }

        /// <summary>
        /// <c>clippath</c> — replaces the current path with the current clipping path, expressed in
        /// the <b>current</b> user space.
        /// </summary>
        public void ClipPath()
        {
            double[] device = Gs.ClipQuadDevice;

            if (device == null)
            {
                var box = DefaultClipBox ?? new PsBoundingBox(0, 0, LetterWidth, LetterHeight);

                // The default clip lives in default user space, so it reaches device space through
                // the base matrix — the same one every emitted coordinate passes through.
                var (ax, ay) = BaseMatrix.Apply(box.LowerLeftX,  box.LowerLeftY);
                var (bx, by) = BaseMatrix.Apply(box.UpperRightX, box.LowerLeftY);
                var (cx, cy) = BaseMatrix.Apply(box.UpperRightX, box.UpperRightY);
                var (dx, dy) = BaseMatrix.Apply(box.LowerLeftX,  box.UpperRightY);
                device = new[] { ax, ay, bx, by, cx, cy, dx, dy };

                if (!_defaultClipReported)
                {
                    _defaultClipReported = true;
                    PostScriptDiagnostics.Report("PsGraphicsEngine.ClipPath",
                        DefaultClipBox.HasValue
                            ? "'clippath' with no clip in force answered with the document's declared " +
                              "%%BoundingBox; this converter has no device page, so that is the closest " +
                              "thing to a default clipping path. Reported once."
                            : "'clippath' with no clip in force answered with US Letter: nothing has " +
                              "clipped and the document declared no %%BoundingBox, so there is no page " +
                              "extent to report. Reported once.");
                }
            }
            else if (!Gs.ClipIsExact && !_inexactClipReported)
            {
                _inexactClipReported = true;
                PostScriptDiagnostics.Report("PsGraphicsEngine.ClipPath",
                    "'clippath' answered with the bounding box of the clipping path rather than its " +
                    "outline: the clip is curved, many-sided, or the intersection of several. " +
                    "'clippath pathbbox' is unaffected — a bounding box is what it asks for — but " +
                    "filling or clipping to this path covers more than the real clip. Reported once.");
            }

            // Back into the space the program is drawing in.
            var inverse = Gs.Ctm.Invert();
            if (inverse == null)
            {
                PostScriptDiagnostics.Report("PsGraphicsEngine.ClipPath",
                    "'clippath' could not express the clip in user space: the current transform is " +
                    "singular and cannot be inverted; the path was left unchanged");
                return;
            }

            Path.Clear();
            for (int k = 0; k < 8; k += 2)
            {
                var (ux, uy) = inverse.Value.Apply(device[k], device[k + 1]);
                if (k == 0) Path.MoveTo(ux, uy); else Path.LineTo(ux, uy);
            }
            Path.ClosePath();
        }

        // US Letter in points. Only ever reached when nothing clipped AND the document declared no
        // %%BoundingBox, and reported when it is.
        private const double LetterWidth  = 612;
        private const double LetterHeight = 792;

        /// <summary>
        /// <c>initclip</c> — the page's own region again. PDF cannot widen a clip, so the emitted levels
        /// the clips were taken under are closed and what they held put back (<see cref="Reconcile"/>);
        /// the <c>gsave</c> levels themselves survive, each <c>grestore</c> reconciling in turn, so the
        /// state stack keeps its depth.
        /// </summary>
        public void InitClip()
        {
            if (Gs.Clip == null) return;
            Gs.Clip = null;
            Reconcile();
        }

        // ------------------------------------------------------------------
        //  showpage
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>showpage</c> in fragment mode: reset to the initial graphics state and emit nothing
        /// that paints.
        ///
        /// <para>PLRM's <c>showpage</c> implies <c>erasepage</c>, which would white out the host
        /// PDF page this XObject is drawn on. That is certainly not what a document embedding a PS
        /// XObject means, so the erase is dropped and reported.</para>
        /// </summary>
        public void ShowPageReset()
        {
            Emitter.CloseAllSaves();
            ResetCurrentState();

            // A transform written at depth 0 is not closed by any Q, so it is undone here.
            Reconcile();

            PostScriptDiagnostics.Report("PsGraphicsEngine.ShowPageReset",
                "'showpage' reset the graphics state; its implied 'erasepage' was NOT performed, " +
                "because erasing would blank the host PDF page this PostScript is drawn on");
        }
    }
}
