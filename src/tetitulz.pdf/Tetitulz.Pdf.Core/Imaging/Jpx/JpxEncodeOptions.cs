// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>What a caller of <see cref="JpxEncoder"/> chooses; every default is a plain, widely decoded codestream.</summary>
    internal sealed class JpxEncodeOptions
    {
        /// <summary>N<sub>L</sub>, the decomposition levels (T.800 Table A.15: 0 to 32).</summary>
        internal int Levels { get; set; } = 5;

        /// <summary>xcb, the code-block width exponent: 2 to 10, and with <see cref="CodeBlockHeightExponent"/> at most 12 (Table A.18).</summary>
        internal int CodeBlockWidthExponent { get; set; } = 6;

        /// <summary>ycb, the code-block height exponent.</summary>
        internal int CodeBlockHeightExponent { get; set; } = 6;

        /// <summary>
        /// Whether components 0 to 2 go through the multiple component transformation (the RCT with the
        /// reversible path, the ICT with the irreversible one). Meaningless, and ignored, below three components.
        /// </summary>
        internal bool MultipleComponentTransform { get; set; } = true;

        /// <summary>A COM marker segment's Latin text (Table A.44, Rcom 1) in the main header; null for none.</summary>
        internal string Comment { get; set; }

        /// <summary>
        /// The irreversible path's quantisation step Δ<sub>b</sub> (T.800 E-3), the same for every
        /// band, in the units of the transformed samples: 1 keeps an 8-bit image near lossless, 8 is
        /// visibly lossy. Each band's step is rounded to the nearest (ε<sub>b</sub>, μ<sub>b</sub>) pair
        /// a QCD carries, and that rounded step is the one used. Without a <see cref="TargetBytes"/>
        /// quality is this step alone; with one, the step bounds the quality the target can reach.
        /// </summary>
        internal double QuantizationStep { get; set; } = 1;

        /// <summary>
        /// Quality layers (Table A.14: 1 to 65535). Each layer adds, from every code-block, the
        /// coding passes that remove the most distortion per byte; the layers before the last take
        /// halving shares of the size — of <see cref="TargetBytes"/>, or of the whole codestream
        /// without one — and the last takes everything left.
        /// </summary>
        internal int Layers { get; set; } = 1;

        /// <summary>
        /// The size the whole codestream is fitted to, in bytes, or 0 for none: coding passes are
        /// dropped, least distortion removed per byte first, until the codestream fits; the
        /// headers are never dropped, so a target below them is met as nearly as empty packets
        /// allow. A lossless codestream under a target is lossless no longer.
        /// </summary>
        internal long TargetBytes { get; set; }

        /// <summary>The order the packets take (Table A.16).</summary>
        internal JpxProgressionOrder ProgressionOrder { get; set; } = JpxProgressionOrder.Lrcp;

        /// <summary>
        /// Precinct exponents per resolution level, r = 0 first, each as (PPx, PPy) — 0 to 15, and
        /// above level 0 at least 1 (Table A.21). Null for the default 2<sup>15</sup>, one precinct per
        /// resolution level. Fewer entries than levels + 1 repeat the last.
        /// </summary>
        internal (int X, int Y)[] Precincts { get; set; }

        /// <summary>SOP marker segments before every packet (§A.8.1).</summary>
        internal bool Sop { get; set; }

        /// <summary>An EPH marker after every packet header (§A.8.2).</summary>
        internal bool Eph { get; set; }

        /// <summary>The tile size on the reference grid; 0 for one tile holding the whole image.</summary>
        internal int TileWidth { get; set; }

        /// <summary>See <see cref="TileWidth"/>.</summary>
        internal int TileHeight { get; set; }

        /// <summary>
        /// HT code-blocks (ITU-T T.814): one HT set per block (<see cref="JpxHtBlockEncoder"/>,
        /// <see cref="JpxHtRefinementEncoder"/>), the codestream HTONLY with SIZ's Rsiz bit 14 and a CAP
        /// marker. Layers and a size target are <see cref="JpxHtRateControl"/>'s: a set per block — a
        /// cleanup at some plane, and under a target maybe its SigProp and MagRef passes — the layers
        /// deciding when it arrives. Without a target every block is one cleanup pass down to plane 0.
        /// </summary>
        internal bool HighThroughput { get; set; }

        /// <summary>
        /// Whether a size target may choose an HT set with refinement passes (§7.4, §7.5) — a third of a
        /// plane at a time — rather than whole cleanup planes only. On by default; off measures what they gain.
        /// </summary>
        internal bool HtRefinementPasses { get; set; } = true;

        /// <summary>
        /// Vertically causal context formation (T.800 §D.7, code-block style bit 3; for HT blocks the SigProp
        /// neighbourhood of T.814 §7.4): a stripe's coding reads nothing of the stripe below. Off by default.
        /// </summary>
        internal bool VerticallyCausal { get; set; }

        /// <summary>
        /// The encode's thread budget, as the decoder's <c>maxThreads</c>: 0 chooses (<see cref="JpxParallel.Resolve"/>),
        /// 1 is the calling thread alone, N at most N. The codestream is the same whatever it is.
        /// </summary>
        internal int MaxThreads { get; set; }

        /// <summary>The workers to use in place of <see cref="MaxThreads"/>' — a test's, to read <see cref="JpxParallel.MostWorkers"/> after; null for the resolved ones.</summary>
        internal JpxParallel Workers { get; set; }
    }
}
