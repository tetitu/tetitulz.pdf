// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// One clip a graphics state has taken, after those it took before (PLRM §4.4: each <c>clip</c>
    /// intersects the region with a path) — a persistent list, so a state saved by <c>gsave</c>,
    /// <c>clipsave</c> or <c>gstate</c> shares the clips it inherited, and two regions' common part
    /// is found by identity. The path is in device space, as PLRM's clipping path is: the CTM moves
    /// and the clip does not.
    ///
    /// <para><b>Why the history is kept.</b> PDF can narrow a clip, never widen one: the only way
    /// back is a <c>Q</c> past the <c>q</c> the clip was taken under. <c>cliprestore</c>,
    /// <c>initclip</c> and <c>setgstate</c> widen the clip with no <c>grestore</c>, so the engine
    /// closes emitted levels until the region they leave in force is one the wanted region extends,
    /// and takes the rest of the wanted clips again (<c>PsGraphicsEngine.Reconcile</c>). The region is
    /// the list, not a path computed from it — PDF intersects the clips itself.</para>
    /// </summary>
    internal sealed class PsClip
    {
        public PsClip(PsClip previous, PsSegment[] deviceSegments, bool evenOdd, double[] quad, bool exact)
        {
            Previous = previous;
            DeviceSegments = deviceSegments;
            EvenOdd = evenOdd;
            Quad = quad;
            Exact = exact;
            Count = (previous?.Count ?? 0) + 1;
        }

        /// <summary>The clips taken before this one, or null for the page's own region.</summary>
        public PsClip Previous { get; }

        /// <summary>The path this clip intersected the region with, in device space.</summary>
        public PsSegment[] DeviceSegments { get; }

        public bool EvenOdd { get; }

        /// <summary>
        /// The region's four device-space corners, or the box around it — what <c>clippath</c> reads
        /// (see <see cref="PsGraphicsState.ClipQuadDevice"/>); null when no corner could be found.
        /// </summary>
        public double[] Quad { get; }

        /// <summary>True when <see cref="Quad"/> is the region itself rather than a box around it.</summary>
        public bool Exact { get; }

        /// <summary>How many clips the region is the intersection of.</summary>
        public int Count { get; }

        /// <summary>
        /// Whether <paramref name="region"/> is <paramref name="prefix"/> with more clips taken after
        /// it, or <paramref name="prefix"/> itself; null, the page's region, is every region's prefix.
        /// </summary>
        public static bool Extends(PsClip region, PsClip prefix)
        {
            if (prefix == null) return true;
            for (var c = region; c != null && c.Count >= prefix.Count; c = c.Previous)
                if (ReferenceEquals(c, prefix)) return true;
            return false;
        }

        /// <summary>Regions <see cref="After"/> has walked on the calling thread: the evidence that a capped stream walks none.</summary>
        [System.ThreadStatic] internal static long ChainSteps;

        /// <summary>The clips <paramref name="region"/> took after <paramref name="prefix"/>, in the order they were taken.</summary>
        public static List<PsClip> After(PsClip region, PsClip prefix)
        {
            var taken = new List<PsClip>();
            for (var c = region; c != null && !ReferenceEquals(c, prefix); c = c.Previous) { taken.Add(c); ChainSteps++; }
            taken.Reverse();
            return taken;
        }

        /// <summary><paramref name="segments"/> carried through <paramref name="m"/>, their kinds kept.</summary>
        public static PsSegment[] Transformed(IReadOnlyList<PsSegment> segments, in PsMatrix m)
        {
            var result = new PsSegment[segments.Count];
            for (int k = 0; k < segments.Count; k++) result[k] = segments[k].TransformedBy(m);
            return result;
        }
    }

    /// <summary>
    /// The clipping path stack <c>clipsave</c> pushes onto (PLRM §8.2) — part of the graphics state,
    /// so a <c>grestore</c> puts back the stack its <c>gsave</c> saw. Persistent, like the regions.
    /// </summary>
    internal sealed class PsClipSave
    {
        public PsClipSave(PsClip region, PsClipSave below) { Region = region; Below = below; }

        /// <summary>The region saved, null for the page's.</summary>
        public PsClip Region { get; }

        public PsClipSave Below { get; }
    }
}
