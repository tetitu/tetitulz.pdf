// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;

namespace Tetitulz.Pdf.Plugins.PostScript.Emit
{
    /// <summary>
    /// Builds the PDF content stream.
    ///
    /// <para><b>The only type in this plugin that writes PDF operator text.</b> Every impedance
    /// decision therefore has exactly one home: deferred colour, deferred clip, path re-emission,
    /// <c>q</c>/<c>Q</c> balance and the byte cap.</para>
    ///
    /// <para><b>Nothing is emitted eagerly.</b> Path segments live in <see cref="PsPath"/> and
    /// colours in <see cref="PsGraphicsState"/>; both are flushed only by a painting or clipping
    /// operator. That single rule is what makes the hard cases fall out instead of needing
    /// special-casing:</para>
    /// <list type="bullet">
    ///   <item><c>clip</c> is immediate in PostScript and deferred in PDF (<c>W</c> marks a pending
    ///     clip that the next painting operator applies). Owning the path means <c>clip</c> can emit
    ///     it followed by <c>W n</c>, and a later <c>fill</c> can emit the identical path
    ///     again.</item>
    ///   <item>PostScript has <i>one</i> colour; PDF has separate fill and stroke colours. Deferring
    ///     means <c>fill</c> emits <c>rg</c> and <c>stroke</c> emits <c>RG</c>, each only when its
    ///     own tracked colour actually changed.</item>
    /// </list>
    ///
    /// <para>The CTM is <b>never baked into coordinates</b>. Every transform change is emitted as a
    /// <c>cm</c> and path coordinates go out exactly as the program wrote them, because PDF derives
    /// line width, dash phase and non-uniform scaling from the CTM the same way PostScript does.
    /// Baking would silently destroy all three.</para>
    /// </summary>
    internal sealed class ContentStreamEmitter
    {
        private readonly StringBuilder _ops = new();
        private readonly int _maxBytes;

        // What the PDF graphics state currently holds, so a colour operator that would be a no-op
        // is never emitted. One entry per open `q`, because `Q` restores PDF colour too.
        private readonly List<EmittedState> _saveStack = new();

        private PsColor? _lastFill;
        private PsColor? _lastStroke;
        private double? _lastLineWidth;
        private int? _lastLineCap;
        private int? _lastLineJoin;
        private double? _lastMiterLimit;
        private string _lastDash;

        // The clip region, the transform and the transfer function the stream holds, rolled back by
        // each `Q` with the rest.
        private PsClip _clip;
        private PsMatrix _ctm = PsMatrix.Identity;
        private Tetitulz.Pdf.Core.Objects.PdfObject _transfer;
        private PsDeviceParameters _device = PsDeviceParameters.Initial;

        private readonly record struct EmittedState(
            PsColor? Fill, PsColor? Stroke, double? LineWidth, int? LineCap, int? LineJoin,
            double? MiterLimit, string Dash, PsClip Clip, PsMatrix Ctm, Tetitulz.Pdf.Core.Objects.PdfObject Transfer,
            PsDeviceParameters Device);

        /// <summary>The transfer function the stream holds (null for the identity), rolled back by each <c>Q</c>.</summary>
        public Tetitulz.Pdf.Core.Objects.PdfObject EmittedTransfer => _transfer;

        /// <summary>The overprint, black generation and other device parameters the stream holds, rolled back by each <c>Q</c>.</summary>
        public PsDeviceParameters EmittedDevice => _device;

        /// <summary>
        /// Names the <c>/ExtGState</c> that takes the stream from the first parameters to the second —
        /// only the entries that differ — in the resources of the stream this emitter writes, or answers
        /// null past the cap; set by the engine that owns both.
        /// </summary>
        public System.Func<PsDeviceParameters, PsDeviceParameters, string> NameDevice { get; set; }

        /// <summary>
        /// Writes <c>/GSn gs</c> taking the stream to <paramref name="wanted"/> when it holds other
        /// parameters — set as the program sets them, as a transfer function is.
        /// </summary>
        public void SetDevice(PsDeviceParameters wanted)
        {
            if (wanted.Equals(_device)) return;
            if (NameDevice?.Invoke(_device, wanted) is not string name) return;
            if (Op($"/{name} {PdfOperators.SetExtGState}")) _device = wanted;
        }

        /// <summary>
        /// Names the <c>/ExtGState</c> that sets a transfer function, in the resources of the stream this
        /// emitter writes, or answers null; set by the engine that owns both.
        /// </summary>
        public System.Func<Tetitulz.Pdf.Core.Objects.PdfObject, string> NameTransfer { get; set; }

        /// <summary>
        /// Writes <c>/GSn gs</c> for the graphics state whose <c>/TR</c> is <paramref name="transfer"/>
        /// (<c>/Identity</c> for null) when the stream holds another — set as the program sets it, as a
        /// transform is, since every paint after it is transferred.
        /// </summary>
        public void SetTransfer(Tetitulz.Pdf.Core.Objects.PdfObject transfer)
        {
            if (ReferenceEquals(transfer, _transfer)) return;
            if (NameTransfer?.Invoke(transfer) is not string name) return;
            if (Op($"/{name} {PdfOperators.SetExtGState}")) _transfer = transfer;
        }

        /// <summary>
        /// The clipping region the stream holds: the engine's clips written since the open levels
        /// began, each <c>Q</c> rolling it back as it rolls back the real one.
        /// </summary>
        public PsClip EmittedClip => _clip;

        /// <summary>The transform the stream holds: the one it is drawn under and every <c>cm</c> since, rolled back by each <c>Q</c>.</summary>
        public PsMatrix EmittedCtm => _ctm;

        /// <summary>Starts the tracked transform at the one the stream is drawn under.</summary>
        public void StartCtm(in PsMatrix baseMatrix) => _ctm = baseMatrix;

        // The bytes of text shown along each quarter turn of the page: the baseline pointing right,
        // up, left, down — the text matrix's x axis through the stream's transform.
        private readonly long[] _textByDirection = new long[4];

        private void CountTextDirection(in PsMatrix textMatrix, int bytes)
        {
            var (dx, dy) = _ctm.ApplyDelta(textMatrix.A, textMatrix.B);
            if (double.IsNaN(dx) || double.IsNaN(dy) || (dx == 0 && dy == 0)) return;
            int quarter = Math.Abs(dx) >= Math.Abs(dy) ? (dx >= 0 ? 0 : 2) : (dy > 0 ? 1 : 3);
            _textByDirection[quarter] += bytes;
        }

        /// <summary>
        /// The clockwise turn, in degrees, that makes the stream's predominant text read left to right —
        /// 90 for text running up the page, 180 upside down, 270 running down; 0 for upright text, no
        /// text, or a tie with upright — Distiller's <c>AutoRotatePages /PageByPage</c>.
        /// </summary>
        public int DominantTextRotation()
        {
            int best = 0;
            for (int q = 1; q < 4; q++)
                if (_textByDirection[q] > _textByDirection[best]) best = q;
            return 90 * best;
        }

        public ContentStreamEmitter(int maxBytes = -1)
            => _maxBytes = maxBytes < 0 ? PsLimits.MaxEmittedContentBytes : maxBytes;

        /// <summary>How many <c>q</c> operators are currently open.</summary>
        public int SaveDepth { get; private set; }

        /// <summary>True once the byte cap stopped emission.</summary>
        public bool Truncated { get; private set; }

        /// <summary>True when anything that paints has been emitted.</summary>
        public bool HasMarks { get; private set; }

        /// <summary>
        /// Formats one PDF number, refusing anything that is not one.
        ///
        /// <para><c>Infinity</c>, <c>-Infinity</c> and <c>NaN</c> are not PDF numbers, and they used
        /// to be written verbatim — <c>Infinity Infinity m</c> really did reach the content stream,
        /// and <c>/MediaBox</c>, where this repo's own parser reads them back as null. Overflow is
        /// easy to reach from legal PostScript (<c>1e200 1e200 mul</c>), so this is guarded at the
        /// <b>write boundary</b> rather than at each producer: a future operator cannot reintroduce
        /// it. Substituting 0 keeps the operator syntactically valid; the accompanying report is
        /// what tells the caller the drawing is wrong.</para>
        /// </summary>
        private string F(double v)
        {
            if (PsNumeric.IsFinite(v)) return PdfContentStreamBuilder.FormatNumber(v);
            ReportNonFinite(v);
            return "0";
        }

        /// <summary>Says, once a conversion, that a non-finite number was written as 0 — both number paths.</summary>
        private void ReportNonFinite(double v)
        {
            if (_nonFiniteReported) return;
            _nonFiniteReported = true;
            PostScriptDiagnostics.Report("ContentStreamEmitter",
                $"a non-finite coordinate ({v}) was replaced with 0: it is not a PDF number and " +
                "writing it would make the stream unparseable. The drawing is wrong at that " +
                "point. Reported once per conversion.");
        }

        private bool _nonFiniteReported;

        // ------------------------------------------------------------------
        //  Raw emission
        // ------------------------------------------------------------------

        /// <summary>
        /// Appends one operator line, unless the cap has been reached.
        ///
        /// <para>On overflow the emitter closes every open <c>q</c> and stops. The result is a
        /// <b>valid truncated</b> stream — never a half-written operator, and never one the renderer
        /// would reject for unbalanced <c>q</c>/<c>Q</c>.</para>
        /// </summary>
        private bool Op(string text) => TryEmit(text, SaveDepth);

        /// <summary>The exact cost of the <c>Q</c> that closes one open <c>q</c>: operator + newline.</summary>
        private static readonly int CloseCostPerSave = PdfOperators.Restore.Length + 1;

        /// <summary>
        /// Appends <paramref name="text"/> while keeping the reservation invariant
        /// <c>_ops.Length + CloseCostPerSave * SaveDepth &lt;= _maxBytes</c>.
        ///
        /// <para><b>Why every append reserves the cost of closing.</b> <see cref="CloseAllSaves"/>
        /// writes straight to the buffer, bypassing this check — which it must, because a <c>Q</c>
        /// that balances a <c>q</c> already in the stream has to be written even past the cap. So
        /// the cap alone did not bound the output: the emitter stopped at <c>_maxBytes</c> and then
        /// appended two more bytes per open level, up to
        /// <c>PdfLimits.MaxGraphicsStateStackDepth</c> (4096) of them. The host rejects at
        /// <c>&gt; PdfLimits.MaxPluginContentStreamBytes</c> — the same 8 MiB this caps at — so the
        /// "valid truncated stream" was discarded whole and the renderer drew its placeholder
        /// instead of the partial artwork. Measured at 5290 bytes against a 4096-byte cap.</para>
        ///
        /// <para>The reservation is recomputed on <i>every</i> append from the depth in force after
        /// it, so there is no one-shot reservation to go stale when the depth changes: a normal
        /// operator pays for the current depth, a <c>q</c> funds its own <c>Q</c> as it is written,
        /// and a <c>Q</c> spends the reservation it already made. The invariant holds at an empty
        /// buffer and is preserved by all three, so <see cref="CloseAllSaves"/> is provably
        /// affordable and <see cref="ToBytes"/> is <c>&lt;= _maxBytes</c> unconditionally.</para>
        /// </summary>
        /// <param name="depthAfter">
        /// <c>q</c> levels still open once <paramref name="text"/> is in the buffer — the <c>Q</c>s
        /// this append must leave room for.
        /// </param>
        /// <param name="text">The content-stream fragment to append.</param>
        /// <returns>False when the cap refused it, so a caller that must pair an operator knows.</returns>
        private bool TryEmit(string text, int depthAfter)
        {
            if (Truncated) return false;

            if (_ops.Length + text.Length + 1 + depthAfter * CloseCostPerSave > _maxBytes)
            {
                Truncated = true;
                CloseAllSaves();
                PostScriptDiagnostics.Report("ContentStreamEmitter",
                    $"emitted content reached the {_maxBytes}-byte cap; the stream was truncated " +
                    "at the last complete operator and all open 'q' were closed");
                return false;
            }

            _ops.Append(text).Append('\n');
            return true;
        }

        // ------------------------------------------------------------------
        //  Allocation-free operator emission
        // ------------------------------------------------------------------
        //
        // An operator built as an interpolated string, then copied into _ops and dropped, costs
        // one string for the operator plus, through F(), up to three per number. A stroke carries
        // about seven numbers, and a 2000-stroke program measured 3.77 MB that way.
        //
        // These append straight into _ops and roll the partial operator back if it does not fit.
        // The cap test is the SAME predicate TryEmit applies, moved after the append: TryEmit asks
        // whether `_ops.Length + text.Length + 1 + reservation` exceeds the cap before writing, and
        // once the operands are in the buffer `_ops.Length` IS `_ops.Length + text.Length`. The
        // rollback is what keeps them equivalent - without `_ops.Length = mark` a refused operator
        // would be left half-written, which is precisely the "never a half-written operator"
        // guarantee TryEmit's own summary makes.

        /// <summary>
        /// Appends one §7.3.3 number, allocation-free. See <see cref="PdfRealFormat.AppendTo"/>, which
        /// writes a non-finite value as 0 — said here as <see cref="F"/> says it, since path
        /// coordinates, colours, line widths and the text matrices all come this way.
        /// </summary>
        private void Num(double v)
        {
            if (!PsNumeric.IsFinite(v)) ReportNonFinite(v);
            PdfRealFormat.AppendTo(_ops, v);
        }

        /// <summary>
        /// Finishes an operator begun at <paramref name="mark"/>, applying the cap and rolling the
        /// partial operator back rather than leaving it half-written.
        /// </summary>
        private bool CommitOp(int mark, int depthAfter)
        {
            if (_ops.Length + 1 + depthAfter * CloseCostPerSave > _maxBytes)
            {
                _ops.Length = mark;          // discard the partial operator
                Truncated = true;
                CloseAllSaves();
                PostScriptDiagnostics.Report("ContentStreamEmitter",
                    $"emitted content reached the {_maxBytes}-byte cap; the stream was truncated " +
                    "at the last complete operator and all open 'q' were closed");
                return false;
            }

            _ops.Append('\n');
            return true;
        }

        /// <summary>
        /// <c>i op</c> — one <b>integer</b> operand (<c>J</c>, <c>j</c>).
        /// </summary>
        /// <remarks>
        /// A separate overload rather than widening to <c>double</c>: §7.3.3 writes an integer
        /// without a decimal point, and routing a line cap through the real formatter would emit
        /// <c>0 J</c> as it happens to today but ties the byte for byte output of a Table 52
        /// integer parameter to the trailing-zero trim. <c>StringBuilder.Append(int)</c> formats
        /// invariantly and allocates nothing.
        /// </remarks>
        private bool Op(int a, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            _ops.Append(a).Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>n op</c> — one operand.</summary>
        private bool Op(double a, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            Num(a); _ops.Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>n₁ … nₖ op</c> — a colour's components in a named space, as many as it has.</summary>
        private bool OpNumbers(double[] values, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            foreach (double v in values) { Num(v); _ops.Append(' '); }
            _ops.Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>x y op</c> — the shape of every <c>m</c> and <c>l</c>.</summary>
        private bool Op(double a, double b, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            Num(a); _ops.Append(' '); Num(b); _ops.Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>r g b op</c>.</summary>
        private bool Op(double a, double b, double c, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            Num(a); _ops.Append(' '); Num(b); _ops.Append(' '); Num(c);
            _ops.Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>c m y k op</c>.</summary>
        private bool Op(double a, double b, double c, double d, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            Num(a); _ops.Append(' '); Num(b); _ops.Append(' '); Num(c); _ops.Append(' '); Num(d);
            _ops.Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        /// <summary><c>x1 y1 x2 y2 x3 y3 op</c> — a Bézier, and <c>cm</c>.</summary>
        private bool Op(double a, double b, double c, double d, double e, double f, string op)
        {
            if (Truncated) return false;
            int mark = _ops.Length;
            Num(a); _ops.Append(' '); Num(b); _ops.Append(' '); Num(c); _ops.Append(' ');
            Num(d); _ops.Append(' '); Num(e); _ops.Append(' '); Num(f);
            _ops.Append(' ').Append(op);
            return CommitOp(mark, SaveDepth);
        }

        // ------------------------------------------------------------------
        //  Graphics-state bracket
        // ------------------------------------------------------------------

        /// <summary>
        /// Opens a graphics-state bracket. Returns false when no <c>q</c> was written, so a caller
        /// that must pair it knows not to emit the <c>Q</c>.
        ///
        /// <para><see cref="SaveDepth"/> counts <c>q</c> operators <b>actually in the stream</b>,
        /// because <see cref="CloseAllSaves"/> writes exactly that many <c>Q</c>. Advancing it for a
        /// <c>q</c> that was suppressed would leave a <c>Q</c> with no partner — and since the
        /// renderer runs this stream on the page's own shared state stack, that <c>Q</c> pops the
        /// calling page's state, not ours.</para>
        /// </summary>
        private bool _depthCapReported;

        public bool Save()
        {
            // Past the cap nothing further is written, so the depth must not advance either.
            if (Truncated) return false;

            // Reuse Core's cap rather than inventing one: emitting deeper than the renderer's own
            // q/Q guard accepts would produce a stream Base refuses to draw.
            if (SaveDepth >= PdfLimits.MaxGraphicsStateStackDepth)
            {
                // One-shot, like PostScriptDiagnostics.ReportOversizedNumericToken. Report is
                // neither deduplicated nor capped, so a caller that keeps asking at the cap would
                // compose a message and fire every OnEvent subscriber on every attempt.
                // PsGraphicsEngine's own depth guard refuses `{ gsave } loop` first, which makes this
                // the second line of defence; it must still not be a diagnostic amplifier for
                // whatever reaches it next.
                if (!_depthCapReported)
                {
                    _depthCapReported = true;
                    PostScriptDiagnostics.Report("ContentStreamEmitter",
                        $"gsave nesting reached {PdfLimits.MaxGraphicsStateStackDepth}; " +
                        "no further 'q' was emitted. Reported once per stream.");
                }

                return false;
            }

            // Emitted BEFORE the state mutation, and only if it actually fit. Advancing SaveDepth
            // for a `q` the cap refused would leave a `Q` with no partner — and since the renderer
            // runs this stream on the page's own shared state stack, that `Q` pops the calling
            // page's state, not ours. This is what makes the documented "false => no q was written"
            // contract true.
            if (!TryEmit(PdfOperators.Save, SaveDepth + 1)) return false;

            _saveStack.Add(new EmittedState(_lastFill, _lastStroke, _lastLineWidth, _lastLineCap,
                                            _lastLineJoin, _lastMiterLimit, _lastDash, _clip, _ctm, _transfer,
                                            _device));
            SaveDepth++;
            return true;
        }

        public void Restore()
        {
            if (SaveDepth == 0) return;   // never emit an unbalanced Q

            // Under the reservation invariant a `Q` can never be refused, so this guard is about a
            // different hazard: Op() on overflow runs CloseAllSaves(), which zeroes SaveDepth, so
            // calling it here and then decrementing would leave the depth at -1. A negative depth
            // silently disables PsGraphicsEngine.Clip's `SaveDepth == 0` bracket test for the rest
            // of the stream.
            if (!TryEmit(PdfOperators.Restore, SaveDepth - 1)) return;

            PopShadowState();
            SaveDepth--;
        }

        /// <summary>
        /// Closes every open <c>q</c>. Used by truncation, by <c>initclip</c>, by <c>showpage</c>
        /// and at end of stream.
        ///
        /// <para>Writes straight to the buffer, deliberately: a <c>Q</c> that balances a <c>q</c>
        /// already in the stream must be written even when the byte cap has been reached, or the
        /// truncated output is malformed. It still <b>restores the tracked state</b> for each level
        /// it closes — <c>Q</c> restores the real PDF colour and line style, so a shadow left
        /// describing the innermost level would make the next paint skip a colour operator it needs
        /// and draw in the enclosing colour.</para>
        /// </summary>
        public void CloseAllSaves()
        {
            while (SaveDepth > 0)
            {
                _ops.Append(PdfOperators.Restore).Append('\n');
                PopShadowState();
                SaveDepth--;
            }
        }

        /// <summary>
        /// Rolls the tracked PDF state back one level, matching what an emitted <c>Q</c> does to the
        /// real one. The two must stay in step or the "unchanged, so skip it" test lies.
        /// </summary>
        private void PopShadowState()
        {
            if (_saveStack.Count == 0) return;

            var s = _saveStack[_saveStack.Count - 1];
            _saveStack.RemoveAt(_saveStack.Count - 1);

            _lastFill       = s.Fill;
            _lastStroke     = s.Stroke;
            _lastLineWidth  = s.LineWidth;
            _lastLineCap    = s.LineCap;
            _lastLineJoin   = s.LineJoin;
            _lastMiterLimit = s.MiterLimit;
            _lastDash       = s.Dash;
            _clip           = s.Clip;
            _ctm            = s.Ctm;
            _transfer       = s.Transfer;
            _device         = s.Device;
        }

        /// <summary>
        /// Writes <c>cm</c> for <paramref name="m"/>, after which the stream's transform is
        /// <paramref name="result"/> — the engine's own product, taken as it is so the two never drift
        /// apart by a rounding.
        /// </summary>
        public void ConcatMatrix(in PsMatrix m, in PsMatrix result)
        {
            // A `cm` written straight after the open text object may yet be taken back by the next
            // show (ContinueText): only `cm`s stand between them.
            bool afterText = _openText != null && _ops.Length == _openText.CmEnd;
            if (!m.IsIdentity) Op(m.A, m.B, m.C, m.D, m.Tx, m.Ty, PdfOperators.ConcatMatrix);   // a no-op cm is noise
            _ctm = result;
            if (afterText) _openText.CmEnd = _ops.Length;
        }

        // ------------------------------------------------------------------
        //  Painting
        // ------------------------------------------------------------------

        public void Fill(PsPath path, PsGraphicsState gs, bool evenOdd)
        {
            if (path.IsEmpty) return;
            EmitFillColorIfChanged(gs.Color);
            EmitPath(path.Segments);
            Op(evenOdd ? PdfOperators.FillEvenOdd : PdfOperators.Fill);
            HasMarks = true;
        }

        public void Stroke(PsPath path, PsGraphicsState gs)
        {
            if (path.IsEmpty) return;
            EmitStrokeColorIfChanged(gs.Color);
            EmitLineStyleIfChanged(gs);
            EmitPath(path.Segments);
            Op(PdfOperators.Stroke);
            HasMarks = true;
        }

        /// <summary>
        /// PostScript <c>clip</c>: intersect the clip with the current path, leaving the path
        /// intact. PDF spells that <c>W n</c> — <c>W</c> marks the pending clip and <c>n</c> is the
        /// no-paint painter that applies it and clears the path. <paramref name="region"/> is the
        /// region it leaves in force.
        /// </summary>
        public void Clip(PsPath path, bool evenOdd, PsClip region)
        {
            if (path.IsEmpty) return;
            EmitPath(path.Segments);
            if (Op((evenOdd ? PdfOperators.ClipEvenOdd : PdfOperators.Clip) + " " + PdfOperators.EndPath))
                _clip = region;
        }

        /// <summary>
        /// Takes <paramref name="region"/>'s last clip again — its device-space path written in the
        /// stream's current user space — after which the region is in force. Returns the segments
        /// written, for the caller to charge.
        /// </summary>
        public int TakeClip(PsClip region)
        {
            if (Truncated) return 0;
            if (_ctm.Invert() is not PsMatrix toUser) return 0;
            EmitPath(PsClip.Transformed(region.DeviceSegments, toUser));
            if (Op((region.EvenOdd ? PdfOperators.ClipEvenOdd : PdfOperators.Clip) + " " + PdfOperators.EndPath))
                _clip = region;
            return region.DeviceSegments.Length;
        }

        private void EmitPath(IReadOnlyList<PsSegment> segments)
        {
            // Past the cap every Op() is a no-op, but the interpolated strings below are built by
            // the CALLER of Op — C# evaluates an argument before the callee's early return — so a
            // truncated stream would go on formatting the whole path on every later paint: most of
            // the cost of the quadratic clip case, work whose output is discarded.
            if (Truncated) return;

            foreach (var s in segments)
            {
                switch (s.Kind)
                {
                    case PsSegmentKind.Move:
                        Op(s.X3, s.Y3, PdfOperators.MoveTo);
                        break;
                    case PsSegmentKind.Line:
                        Op(s.X3, s.Y3, PdfOperators.LineTo);
                        break;
                    case PsSegmentKind.Curve:
                        Op(s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3, PdfOperators.CurveTo);
                        break;
                    case PsSegmentKind.Close:
                        Op(PdfOperators.ClosePath);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------
        //  Colour: PostScript has one, PDF has two
        // ------------------------------------------------------------------

        /// <summary>
        /// True for an uncoloured pattern's cell, whose content specifies no colour (ISO 32000-2
        /// §8.7.3.3): the colour is the one the pattern is painted with.
        /// </summary>
        public bool ColourSuppressed { get; set; }

        /// <summary>
        /// Names a colour space a colour is painted in, in the resources of the stream this emitter
        /// writes, or answers null where it cannot (the cap); set by the engine that owns both.
        /// </summary>
        public System.Func<Tetitulz.Pdf.Core.Objects.PdfObject, string> NameColorSpace { get; set; }

        private void EmitFillColorIfChanged(in PsColor c)
        {
            if (ColourSuppressed) return;
            if (_lastFill.HasValue && _lastFill.Value.Equals(c)) return;
            _lastFill = c;
            EmitColor(c, stroking: false);
        }

        private void EmitStrokeColorIfChanged(in PsColor c)
        {
            if (ColourSuppressed) return;
            if (_lastStroke.HasValue && _lastStroke.Value.Equals(c)) return;
            _lastStroke = c;
            EmitColor(c, stroking: true);
        }

        /// <summary>
        /// Writes the colour operator straight into the buffer, never through an interpolated
        /// string; the operand count is the only thing that varies, so the switch selects an
        /// overload rather than building text.
        /// </summary>
        private void EmitColor(in PsColor c, bool stroking)
        {
            // A colour in its own space — ICC, Cal, Lab — written there; past the cap, in its device
            // approximation below.
            if (c.PatternName == null && c.NamedSpace != null && NameColorSpace?.Invoke(c.NamedSpace) is string spaceName)
            {
                Op($"/{spaceName} {(stroking ? PdfOperators.SetColorSpaceStroke : PdfOperators.SetColorSpaceFill)}");
                OpNumbers(c.NamedComponents, stroking ? PdfOperators.SetColorStrokeN : PdfOperators.SetColorFillN);
                return;
            }

            if (c.PatternName != null)
            {
                // A coloured pattern in /Pattern itself; an uncoloured one in its [/Pattern base]
                // resource, its base colour's components before the pattern's name (§8.7.3.3).
                string space = stroking ? PdfOperators.SetColorSpaceStroke : PdfOperators.SetColorSpaceFill;
                string set = $"/{c.PatternName} {(stroking ? PdfOperators.SetColorStrokeN : PdfOperators.SetColorFillN)}";
                if (c.PatternSpaceName == null)
                {
                    Op($"/{PdfColorSpaceNames.Pattern} {space}");
                    Op(set);
                    return;
                }
                Op($"/{c.PatternSpaceName} {space}");
                switch (c.Space)
                {
                    case PsColorSpace.Gray: Op(c.C0, set); break;
                    case PsColorSpace.Rgb: Op(c.C0, c.C1, c.C2, set); break;
                    default: Op(c.C0, c.C1, c.C2, c.C3, set); break;
                }
                return;
            }

            switch (c.Space)
            {
                case PsColorSpace.Gray:
                    Op(c.C0, stroking ? PdfOperators.SetGrayStroke : PdfOperators.SetGrayFill);
                    break;
                case PsColorSpace.Rgb:
                    Op(c.C0, c.C1, c.C2,
                       stroking ? PdfOperators.SetRgbStroke : PdfOperators.SetRgbFill);
                    break;
                default:
                    Op(c.C0, c.C1, c.C2, c.C3,
                       stroking ? PdfOperators.SetCmykStroke : PdfOperators.SetCmykFill);
                    break;
            }
        }

        // ------------------------------------------------------------------
        //  Line style — only ever emitted before a stroke that needs it
        // ------------------------------------------------------------------

        private void EmitLineStyleIfChanged(PsGraphicsState gs)
        {
            if (!_lastLineWidth.HasValue || _lastLineWidth.Value != gs.LineWidth)
            {
                _lastLineWidth = gs.LineWidth;
                Op(gs.LineWidth, PdfOperators.SetLineWidth);
            }

            if (!_lastLineCap.HasValue || _lastLineCap.Value != gs.LineCap)
            {
                _lastLineCap = gs.LineCap;
                Op(gs.LineCap, PdfOperators.SetLineCap);
            }

            if (!_lastLineJoin.HasValue || _lastLineJoin.Value != gs.LineJoin)
            {
                _lastLineJoin = gs.LineJoin;
                Op(gs.LineJoin, PdfOperators.SetLineJoin);
            }

            if (!_lastMiterLimit.HasValue || _lastMiterLimit.Value != gs.MiterLimit)
            {
                _lastMiterLimit = gs.MiterLimit;
                Op(gs.MiterLimit, PdfOperators.SetMiterLimit);
            }

            string dash = FormatDash(gs);
            if (_lastDash != dash)
            {
                _lastDash = dash;
                Op(dash);
            }
        }

        private string FormatDash(PsGraphicsState gs)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < gs.DashArray.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(F(gs.DashArray[i]));
            }
            sb.Append("] ").Append(F(gs.DashPhase)).Append(' ').Append(PdfOperators.SetDash);
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Text
        // ------------------------------------------------------------------

        /// <summary>
        /// Emits one show as a complete text object, or as one more string of the text object
        /// written just before it when it continues that object's line (<see cref="ContinueText"/>).
        ///
        /// <para><b>A whole <c>BT … ET</c> per show, rather than one text object spanning the
        /// page.</b> PostScript has no text-object concept: <c>show</c> paints at the current point
        /// and any graphics operator may intervene. Keeping one text object open across them would
        /// mean tracking which PDF operators are legal inside <c>BT</c>/<c>ET</c> and splitting
        /// whenever one is not — for output that renders identically. The cost is a few extra bytes
        /// per show.</para>
        ///
        /// <para>The <c>Tm</c> carries the pen position <i>and</i> the font's scale, so a
        /// <c>makefont</c> skew survives; <c>Tf</c> then uses size 1 and lets the matrix do the
        /// scaling, which is the only way to express a non-uniform text scale in PDF.</para>
        ///
        /// <para><b>All five text-state parameters are emitted every time, including as their
        /// defaults.</b> <c>Tc</c>, <c>Tw</c>, <c>Tz</c>, <c>Tr</c> and <c>Ts</c> are graphics-state
        /// parameters, not text-object ones — <c>BT</c> resets only <c>Tm</c> and <c>Tlm</c>
        /// (§9.4.1), and Table 104 lists all five as text state — so a value set for one <c>ashow</c>
        /// would otherwise survive into every later plain <c>show</c>, which asked for no letter
        /// spacing at all. Writing them unconditionally is also what makes each text object
        /// independent of whatever the host page left in the state before invoking this stream:
        /// a form XObject inherits the graphics state in force at the <c>Do</c> (§8.10.1), so a page
        /// that stamped an invisible OCR layer with <c>3 Tr</c>, or left <c>50 Tz</c> in force,
        /// would otherwise render every converted <c>show</c> invisible or at half width — while the
        /// interpreter's own pen advance (<c>PsTextOps.Paint</c>) goes on assuming 100 and full
        /// visibility, so anything positioned after the run would land in the wrong place. All five
        /// belong in the same append.</para>
        /// </summary>
        /// <param name="fontResourceName">The font's name in the stream's <c>/Font</c> resources.</param>
        /// <param name="textMatrix">The pen position and the font matrix, written as <c>Tm</c>.</param>
        /// <param name="winAnsiBytes">The codes shown, as the font's encoding reads them.</param>
        /// <param name="color">The fill colour the text paints in.</param>
        /// <param name="charSpacing">The letter spacing in text space (<c>Tc</c>).</param>
        /// <param name="wordSpacing">The word spacing in text space (<c>Tw</c>).</param>
        /// <param name="advance">
        /// How far a reader moves the pen along the text space's x axis showing these bytes — the
        /// font's own widths in em plus the spacing (§9.4.4) — when the caller knows it exactly, or
        /// null. Only a show with an advance continues the text object before it
        /// (<see cref="ContinueText"/>), or can be continued.
        /// </param>
        public void ShowText(string fontResourceName, in PsMatrix textMatrix, byte[] winAnsiBytes,
                             in PsColor color, double charSpacing, double wordSpacing,
                             double? advance = null)
        {
            if (string.IsNullOrEmpty(fontResourceName) || winAnsiBytes == null ||
                winAnsiBytes.Length == 0)
                return;

            EmitFillColorIfChanged(color);

            if (Truncated) return;
            CountTextDirection(textMatrix, winAnsiBytes.Length);

            if (advance is double continued &&
                ContinueText(fontResourceName, textMatrix, winAnsiBytes, charSpacing, wordSpacing, continued))
            {
                HasMarks = true;
                return;
            }
            _openText = null;

            // The whole text object goes out as ONE unit, so the cap cannot split it. Emitted
            // operator by operator, a cap reached anywhere after `BT` would swallow the rest —
            // including `ET` — and CloseAllSaves only writes `Q`, so the truncated stream would end
            // inside an unterminated text object, with q/Q balanced and BT/ET not.
            //
            // ATOMICITY COMES FROM THE ROLLBACK MARK, NOT FROM A TEMPORARY BUFFER. Appending
            // straight into `_ops` and discarding back to `mark` gives the all-or-nothing guarantee
            // without a buffer, its ToString, or a string per number — and `CommitOp` restores the
            // length BEFORE CloseAllSaves, so the `Q`s land on a buffer with no partial text object
            // in it.
            int mark = _ops.Length;

            _ops.Append(PdfOperators.BeginText).Append('\n');
            _ops.Append('/').Append(fontResourceName).Append(" 1 ").Append(PdfOperators.SetFont).Append('\n');
            Num(charSpacing); _ops.Append(' ').Append(PdfOperators.SetCharSpacing).Append('\n');
            Num(wordSpacing); _ops.Append(' ').Append(PdfOperators.SetWordSpacing).Append('\n');

            // The other three text-state parameters, at their §9.3 defaults. PostScript has no
            // equivalent of any of them, so the converted text always wants the default — and
            // without these the host page's last `Tz`/`Tr`/`Ts` is inherited through the `Do`.
            _ops.Append("100 ").Append(PdfOperators.SetHorizontalScaling).Append('\n');
            _ops.Append("0 ").Append(PdfOperators.SetTextRenderMode).Append('\n');
            _ops.Append("0 ").Append(PdfOperators.SetTextRise).Append('\n');
            Num(textMatrix.A); _ops.Append(' '); Num(textMatrix.B); _ops.Append(' ');
            Num(textMatrix.C); _ops.Append(' '); Num(textMatrix.D); _ops.Append(' ');
            Num(textMatrix.Tx); _ops.Append(' '); Num(textMatrix.Ty); _ops.Append(' ');
            _ops.Append(PdfOperators.SetTextMatrix).Append('\n');
            int stringStart = _ops.Length;
            AppendEscapedString(_ops, winAnsiBytes);
            int stringsEnd = _ops.Length;
            _ops.Append(' ').Append(PdfOperators.ShowText).Append('\n');
            _ops.Append(PdfOperators.EndText);

            if (!CommitOp(mark, SaveDepth)) return;
            HasMarks = true;

            if (advance is double pen && PsNumeric.IsFinite(pen))
                _openText = new OpenTextObject
                {
                    Font = fontResourceName, Matrix = textMatrix, Ctm = _ctm,
                    CharSpacing = charSpacing, WordSpacing = wordSpacing,
                    StringStart = stringStart, StringsEnd = stringsEnd, CmEnd = _ops.Length, Pen = pen, Left = 0,
                };
        }

        /// <summary>
        /// The text object last written, while the next show may still join it: where its strings
        /// begin and end in the buffer, where the buffer ended after it and the <c>cm</c>s written
        /// since, where a reader's pen stands in its text space, and where its leftmost glyph begins.
        /// </summary>
        private sealed class OpenTextObject
        {
            public string Font;
            public PsMatrix Matrix;
            public PsMatrix Ctm;
            public double CharSpacing;
            public double WordSpacing;
            public int StringStart;
            public int StringsEnd;
            public bool IsArray;
            public int CmEnd;
            public double Pen;
            public double Left;
        }

        private OpenTextObject _openText;

        /// <summary>How far, in em, a show may stand off the open object's baseline and still join it.</summary>
        private const double JoinBaselineToleranceEm = 1e-3;

        /// <summary>The widest gap, in em, between the pen and the next show that still joins it — beyond it is another column.</summary>
        private const double JoinMaxGapEm = 1.0;

        /// <summary>How far, in em, a show may stand behind the pen (a kerned pair) and still join it.</summary>
        private const double JoinMaxOverlapEm = 0.5;

        /// <summary>
        /// Adds a show to the text object written just before it, as one more string of a <c>TJ</c>
        /// array, the gap from the pen written as the number before it (§9.4.3) — or answers false
        /// and writes nothing.
        ///
        /// <para>A driver that places each glyph itself (<c>cshow</c>, one-glyph <c>show</c>s) would
        /// otherwise write a text object a letter, and a reader that selects or edits by text object
        /// sees letters, not words. The show joins only what it continues exactly: nothing but
        /// <c>cm</c>s written since, which together leave the transform as it was (they are taken
        /// out); the same font, glyph matrix and spacing; the same baseline; a gap within
        /// <see cref="JoinMaxOverlapEm"/> behind to <see cref="JoinMaxGapEm"/> ahead of the pen — or,
        /// for a line a driver lays out leftwards, glyph before glyph, the show's end within the same
        /// of the object's left edge, the number then moving the pen back over what is drawn. The pen
        /// is tracked as a reader moves it — the rounded number included — so a long line does not
        /// drift.</para>
        /// </summary>
        private bool ContinueText(string font, in PsMatrix tm, byte[] bytes, double charSpacing,
                                  double wordSpacing, double advance)
        {
            OpenTextObject open = _openText;
            if (open == null || Truncated || _ops.Length != open.CmEnd || !PsNumeric.IsFinite(advance)) return false;
            if (open.Font != font || open.CharSpacing != charSpacing || open.WordSpacing != wordSpacing) return false;
            PsMatrix was = open.Matrix;
            if (tm.A != was.A || tm.B != was.B || tm.C != was.C || tm.D != was.D) return false;
            PsMatrix ctm = open.Ctm;
            if (_ctm.A != ctm.A || _ctm.B != ctm.B || _ctm.C != ctm.C || _ctm.D != ctm.D ||
                _ctm.Tx != ctm.Tx || _ctm.Ty != ctm.Ty) return false;

            // The new origin in the open object's text space.
            double det = was.A * was.D - was.B * was.C;
            if (det == 0 || !PsNumeric.IsFinite(det)) return false;
            double dx = tm.Tx - was.Tx, dy = tm.Ty - was.Ty;
            double u = (was.D * dx - was.C * dy) / det;
            double v = (was.A * dy - was.B * dx) / det;
            if (!PsNumeric.IsFinite(u) || !PsNumeric.IsFinite(v) || Math.Abs(v) > JoinBaselineToleranceEm) return false;
            double gap = u - open.Pen;
            double leftGap = open.Left - (u + advance);
            bool ahead = gap >= -JoinMaxOverlapEm && gap <= JoinMaxGapEm;
            bool before = leftGap >= -JoinMaxOverlapEm && leftGap <= JoinMaxGapEm;
            if (!ahead && !before) return false;
            double number = Math.Round(-gap * 1000, 1);

            // Room for the longest form the continuation takes, the bracket, the number and the
            // closing operators — refused before anything changes, so a cap leaves the object whole.
            int longest = 1 + 24 + 2 + bytes.Length * 4 + 2 + PdfOperators.ShowTextArray.Length + 2 + PdfOperators.EndText.Length + 1;
            if (open.StringsEnd + longest + SaveDepth * CloseCostPerSave > _maxBytes) return false;

            _ops.Length = open.StringsEnd;
            if (!open.IsArray)
            {
                _ops.Insert(open.StringStart, '[');
                open.IsArray = true;
            }
            if (number != 0)
            {
                _ops.Append(' ');
                Num(number);
            }
            _ops.Append(' ');
            AppendEscapedString(_ops, bytes);
            open.StringsEnd = _ops.Length;
            _ops.Append("] ").Append(PdfOperators.ShowTextArray).Append('\n')
                .Append(PdfOperators.EndText).Append('\n');
            open.CmEnd = _ops.Length;
            open.Pen += -number / 1000 + advance;
            open.Left = Math.Min(open.Left, u);
            return true;
        }

        /// <summary>
        /// A PDF literal string: <c>(</c>…<c>)</c> with backslash, parentheses and non-printable
        /// bytes escaped.
        ///
        /// <para>Parentheses are escaped even when balanced. §7.3.4.2 permits balanced ones
        /// unescaped, which is exactly why a test fixture using them proves nothing — escaping
        /// unconditionally is one rule instead of two and cannot be got wrong.</para>
        /// </summary>
        private static void AppendEscapedString(StringBuilder sb, byte[] bytes)
        {
            sb.Append('(');

            foreach (byte b in bytes)
            {
                switch (b)
                {
                    case (byte)'(':  sb.Append(@"\("); break;
                    case (byte)')':  sb.Append(@"\)"); break;
                    case (byte)'\\': sb.Append(@"\\"); break;
                    case 10: sb.Append(@"\n"); break;
                    case 13: sb.Append(@"\r"); break;
                    case 9:  sb.Append(@"\t"); break;
                    case 8:  sb.Append(@"\b"); break;
                    case 12: sb.Append(@"\f"); break;
                    default:
                        // Anything outside printable ASCII goes out as a three-digit octal escape,
                        // which keeps the emitted stream 7-bit clean whatever encoding it is read
                        // back through.
                        if (b < 32 || b > 126)
                            sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                        else
                            sb.Append((char)b);
                        break;
                }
            }

            sb.Append(')');
        }

        // ------------------------------------------------------------------
        //  Images
        // ------------------------------------------------------------------

        /// <summary>
        /// Places a registered image XObject under <paramref name="placement"/>.
        ///
        /// <para>The matrix maps the PDF unit square onto the image's position, which is the
        /// convention <c>Do</c> requires — the caller derives it from the inverse of PostScript's
        /// image matrix.</para>
        /// </summary>
        /// <param name="stencilColor">
        /// The colour to paint an <c>/ImageMask</c> stencil in, or null for an ordinary image.
        ///
        /// <para>PLRM §4.10 and PDF §8.9.6.2 both say a stencil is painted with the current
        /// non-stroking colour, and none was being emitted — so every <c>imagemask</c> rendered in
        /// whatever fill colour happened to be in the PDF state: black at the start of a stream, or
        /// worse, some earlier unrelated fill's colour. Emitted <b>inside</b> the Save/Restore
        /// bracket below, so <see cref="PopShadowState"/> rolls the tracked colour back with it and
        /// a later fill in the original colour still re-emits correctly.</para>
        /// </param>
        /// <param name="xobjectResourceName">
        /// The image XObject's name in the emitted <c>/Resources /XObject</c> dictionary.
        /// </param>
        /// <param name="placement">
        /// The matrix mapping the image's unit square onto the page, as PostScript's <c>image</c>
        /// operator defines it.
        /// </param>
        public void DrawImage(string xobjectResourceName, in PsMatrix placement,
                              PsColor? stencilColor = null)
        {
            if (string.IsNullOrEmpty(xobjectResourceName)) return;

            // Bracket through Save/Restore rather than writing q/Q directly. A `q` written behind
            // SaveDepth's back is one CloseAllSaves cannot close, so a byte cap reached between the
            // `q` and the `Q` would leave it open for the rest of the stream.
            if (!Save()) return;

            if (stencilColor.HasValue) EmitFillColorIfChanged(stencilColor.Value);

            Op(placement.A, placement.B, placement.C, placement.D,
               placement.Tx, placement.Ty, PdfOperators.ConcatMatrix);
            Op($"/{xobjectResourceName} {PdfOperators.InvokeXObject}");
            Restore();
            HasMarks = true;
        }

        /// <summary>
        /// A form XObject drawn with <c>Do</c> — its own <c>/Matrix</c> and <c>/BBox</c> place and clip
        /// it, and it changes no state, so nothing is bracketed. The form inherits the state it is
        /// drawn in, and this emitter writes colour and line style only when something paints, so
        /// both are brought up to date first: a form that sets no colour paints in
        /// <paramref name="gs"/>'s, not in whatever the stream last wrote.
        /// </summary>
        public void DrawForm(string formResourceName, PsGraphicsState gs)
        {
            if (string.IsNullOrEmpty(formResourceName)) return;
            EmitFillColorIfChanged(gs.Color);
            EmitStrokeColorIfChanged(gs.Color);
            EmitLineStyleIfChanged(gs);
            Op($"/{formResourceName} {PdfOperators.InvokeXObject}");
            HasMarks = true;
        }

        /// <summary>
        /// Takes <paramref name="gs"/>'s colour and line style as already in force — for a stream that
        /// inherits the state it is drawn in (a form), so it writes only what its procedure changes.
        /// </summary>
        public void AssumeState(PsGraphicsState gs)
        {
            _lastFill = gs.Color;
            _lastStroke = gs.Color;
            _lastLineWidth = gs.LineWidth;
            _lastLineCap = gs.LineCap;
            _lastLineJoin = gs.LineJoin;
            _lastMiterLimit = gs.MiterLimit;
            _lastDash = FormatDash(gs);
        }

        /// <summary>
        /// PostScript <c>shfill</c> as PDF's <c>sh</c>: the shading painted over the current clip,
        /// its coordinates in the current user space — which the stream's own <c>cm</c>s keep equal to
        /// the program's CTM, so no placement is written and nothing needs a bracket: <c>sh</c>
        /// changes no state (ISO 32000-2 §8.7.4.2).
        /// </summary>
        public void PaintShading(string shadingResourceName)
        {
            if (string.IsNullOrEmpty(shadingResourceName)) return;
            Op($"/{shadingResourceName} {PdfOperators.ShadingFill}");
            HasMarks = true;
        }

        // ------------------------------------------------------------------
        //  Result
        // ------------------------------------------------------------------

        /// <summary>
        /// The <c>d0</c>/<c>d1</c> line a Type 3 CharProc must <b>open</b> with (§9.6.5,
        /// Table 113), held until <see cref="ToBytes"/> because this emitter only appends and a
        /// glyph procedure may legally declare its width late.
        /// </summary>
        private string _glyphMetricsPrefix;

        /// <summary>
        /// Records the glyph metrics line for a Type 3 CharProc.
        ///
        /// <para><b>Here rather than in the caller, so the numbers go through <see cref="F"/>.</b>
        /// That method's own doc says the non-finite guard belongs at the <i>write boundary</i>
        /// "rather than at each producer: a future operator cannot reintroduce it" — and a caller
        /// that formatted this line itself would be exactly that producer, with its own rounding
        /// rule and no guard, so a non-finite <c>wx</c> would put an unparseable token at the head
        /// of the stream.</para>
        /// </summary>
        /// <param name="wx">Horizontal advance, in glyph space.</param>
        /// <param name="wy">Vertical advance; 0 for horizontal writing.</param>
        /// <param name="cacheBox">
        /// The <c>d1</c> bounding box, or null for <c>d0</c>. <c>d1</c> means the glyph describes
        /// shape only and paints in the text fill colour; <c>d0</c> that it carries its own.
        /// </param>
        public void SetGlyphMetrics(double wx, double wy, double[] cacheBox)
        {
            _glyphMetricsPrefix = cacheBox == null || cacheBox.Length < 4
                ? $"{F(wx)} {F(wy)} d0\n"
                : $"{F(wx)} {F(wy)} {F(cacheBox[0])} {F(cacheBox[1])} " +
                  $"{F(cacheBox[2])} {F(cacheBox[3])} d1\n";
        }

        /// <summary>
        /// The finished content stream. Closes any still-open <c>q</c> first, so the result is
        /// always balanced even when the program ended mid-<c>gsave</c>.
        /// </summary>
        public byte[] ToBytes()
        {
            CloseAllSaves();

            // Latin-1: a PDF content stream is bytes, and every character the emitter produces is
            // ASCII. Encoding as UTF-8 would be identical here but would silently corrupt any
            // high byte a future stage (a Tj string) adds.
            string text = (_glyphMetricsPrefix ?? string.Empty) + _ops.ToString();
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }

        /// <summary>The emitted text, for tests and diagnostics.</summary>
        public override string ToString() => _ops.ToString();
    }
}
