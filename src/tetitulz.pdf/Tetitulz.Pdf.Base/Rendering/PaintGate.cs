// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Which content-stream operations may paint into one band or tile — the hits the page's <see cref="DisplayListIndex"/> gave for its rectangle —
    /// and how many paints it suppressed.
    /// </summary>
    /// <remarks>
    /// <b>The count is the gate's liveness.</b> A gated render and an ungated one draw the same
    /// pixels — the gate only skips ink that could not land — so no assertion about the output can
    /// see a gate that was never applied. <see cref="RenderedPage.SuppressedPaints"/> carries the
    /// count out, the way <c>CMapCodec.UsesCidRangeIndex</c> pins its index (CLAUDE.md, Testing,
    /// blind spot 2).
    /// </remarks>
    internal sealed class PaintGate
    {
        private readonly HashSet<int> _painted;

        internal PaintGate(IEnumerable<int> painted) => _painted = new HashSet<int>(painted);

        /// <summary>How many paint calls <see cref="Suppresses"/> has turned away.</summary>
        internal int Suppressed { get; private set; }

        /// <summary>Whether operation <paramref name="opIndex"/>'s paint is skipped: true when its bound missed the rectangle.</summary>
        internal bool Suppresses(int opIndex)
        {
            if (_painted.Contains(opIndex)) return false;
            Suppressed++;
            return true;
        }
    }
}
