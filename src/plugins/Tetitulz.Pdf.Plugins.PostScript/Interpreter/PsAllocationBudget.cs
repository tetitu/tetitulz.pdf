// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript.Interpreter
{
    /// <summary>
    /// Aggregate accounting for every composite object one conversion allocates.
    ///
    /// <para><b>Why an aggregate budget and not just per-allocation caps.</b>
    /// <c>PsLimits.MaxSingleStringLength</c> refuses <c>1000000000 string</c>, but it cannot refuse
    /// <c>{ 1000000 string pop } loop</c> — every allocation there is legal and the total is
    /// unbounded. This is the same argument <c>Core.Util.WorkBudget</c> makes for operation counts,
    /// applied to memory: per-item limits do not compose.</para>
    ///
    /// <para>Charges are <b>never refunded</b>. PostScript has no free, garbage is reclaimed only by
    /// <c>restore</c> (which this interpreter does not implement as VM rollback), and tracking real
    /// liveness would need a collector. The budget therefore measures allocation <i>traffic</i>, not
    /// peak footprint — which is the quantity an attacker controls.</para>
    /// </summary>
    internal sealed class PsAllocationBudget
    {
        // Per-element overheads, sized to the CLR's real cost rather than to the PostScript
        // datum: a PsObject is 24 bytes, and a Dictionary entry costs roughly twice its payload
        // once the bucket and hash-code arrays are counted.
        internal const int BytesPerArrayElement = 24;
        internal const int BytesPerDictEntry    = 48;

        /// <summary>
        /// One cloned <c>PsGraphicsState</c>, charged by <c>gsave</c> and <c>save</c>: a 16-byte
        /// object header plus ~152 bytes of fields (a 48-byte <c>PsMatrix</c>, a 40-byte
        /// <c>PsColor</c> and the scalars), plus the 8-byte slot it takes in the engine's stack.
        ///
        /// <para><c>PsGraphicsEngine</c> caps the stack's <i>depth</i>, which is what bounds what is
        /// retained. This is what makes the traffic <b>visible</b>, so a program cycling through
        /// hundreds of thousands of legal <c>gsave</c>/<c>grestore</c> pairs is accounted for the
        /// same way one allocating strings is — the depth cap alone cannot see that, because the
        /// stack is never deep.</para>
        /// </summary>
        internal const int BytesPerGraphicsState = 176;

        /// <summary>
        /// One open <c>save</c> level in <c>PsVm</c>: the level object (a header, two ints, a list
        /// reference), its empty undo list and its slot in the VM's list. Charged by every save, an
        /// engine attached or not, so a tint transform's child interpreter cannot open levels for free.
        /// </summary>
        internal const int BytesPerSaveLevel = 72;

        /// <summary>One <c>PsFile</c> a filter makes: its fields and the object it hangs from.</summary>
        internal const int BytesPerFile = 128;

        /// <summary>One <c>PdfNumber</c> a shading's array becomes, and its slot in the <c>PdfArray</c>.</summary>
        internal const int BytesPerPdfNumber = 48;

        private readonly long _limit;
        private long _charged;

        public PsAllocationBudget(long limit = -1)
            => _limit = limit < 0 ? PsLimits.MaxCompositeAllocationBytes : limit;

        /// <summary>Total charged so far, for the conversion result.</summary>
        public long Charged => _charged;

        /// <summary>
        /// Headroom left, for a caller that must bound something <i>before</i> charging it.
        ///
        /// <para>Needed by the filtered data source: Core's <c>StreamFilter.MaxOutputBytes</c>
        /// defaults to <c>PdfLimits.MaxDecompressedBytes</c> (512 MB), eight times this whole
        /// budget, so a filter left at its default makes the budget decorative for filtered input —
        /// RunLength expands ~128×, and a stream of <c>z</c> is 4× through ASCII85.</para>
        ///
        /// <para>Nothing once <see cref="Exhausted"/>, whatever was left when a charge was refused: a
        /// decode bounded by a figure no later charge can reach could be repeated for nothing.</para>
        /// </summary>
        public long Remaining => !Exhausted && _limit > _charged ? _limit - _charged : 0;

        /// <summary>True once the budget is spent; the interpreter then raises <c>limitcheck</c>.</summary>
        public bool Exhausted { get; private set; }

        /// <summary>
        /// Charges <paramref name="bytes"/> and reports whether the allocation may proceed.
        /// <b>Call before allocating, not after</b> — the point is that
        /// <c>1000000000 array</c> never reaches <c>new PsObject[…]</c>.
        /// </summary>
        public bool TryCharge(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (Exhausted || _charged + bytes > _limit)
            {
                Exhausted = true;
                return false;
            }
            _charged += bytes;
            return true;
        }

        public bool TryChargeArray(long elements) => TryCharge(elements * BytesPerArrayElement);
        public bool TryChargeDict(long entries)   => TryCharge(entries * BytesPerDictEntry);

        // No TryChargeString: a string literal is charged exactly once, by PsScanner, where the
        // bytes are materialised. A named helper here would invite a second charge of the same
        // array (PsParser.MakeString's doc comment says why it charges nothing).
    }
}
