// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Page-transition metadata from a page's /Trans dict (ISO 32000-1 §12.4.4).
    /// The library does not animate transitions — it's a library, not a viewer —
    /// but it exposes the parsed fields so downstream presentation tools can.
    /// All properties are nullable so callers can distinguish "absent" from a
    /// spec default.
    /// </summary>
    public sealed class PdfTransition
    {
        private readonly PdfDictionary _dict;

        internal PdfTransition(PdfDictionary dict) { _dict = dict; }

        /// <summary>Raw backing dictionary.</summary>
        public PdfDictionary Dictionary => _dict;

        /// <summary>
        /// /S — transition style name.  Valid values per spec §12.4.4 Table 164:
        /// Split, Blinds, Box, Wipe, Dissolve, Glitter, R (replace, default),
        /// Fly, Push, Cover, Uncover, Fade.
        /// </summary>
        public string Style => ReadName("S") ?? "R";

        /// <summary>/D — duration of the transition effect in seconds.  Default 1.</summary>
        public double Duration
            => _dict?[PdfName.D] is PdfNumber n ? n.DoubleValue : 1.0;

        /// <summary>/Dm — dimension ("H" horizontal or "V" vertical).  Default "H".</summary>
        public string Dimension => ReadName("Dm") ?? "H";

        /// <summary>/M — motion ("I" inward or "O" outward).  Default "I".</summary>
        public string Motion => ReadName("M") ?? "I";

        /// <summary>
        /// /Di — direction of motion.  Either an integer (0, 90, 180, 270, 315)
        /// or the name "None".  Exposed as an int? (null when /Di is the name
        /// "None" or the key is absent).  Default 0 per spec.
        /// </summary>
        public int? Direction
        {
            get
            {
                if (_dict == null) return null;
                var entry = _dict[PdfName.Di];
                // IntValue saturates; an (int)LongValue cast would wrap 2^32 + 270 to 270, a
                // legal-looking direction from a number that is none.
                if (entry is PdfNumber dn) return dn.IntValue;
                if (entry is PdfName name && name.Value == "None") return null;
                return null;
            }
        }

        /// <summary>
        /// /SS — starting or ending scale for the Fly transition.  Default 1.
        /// Only meaningful when Style == "Fly".
        /// </summary>
        public double Scale
            => _dict?[PdfName.SS] is PdfNumber sn ? sn.DoubleValue : 1.0;

        /// <summary>
        /// /B — true when the area that will be flown in is rectangular and
        /// opaque (Fly transitions only).  Default false.
        /// </summary>
        public bool IsRectangular
            => _dict?[PdfName.B] is PdfBoolean bn && bn.Value;

        // PdfDictionary's string-keyed indexer: the backing store is keyed by the raw name
        // string, so a constant key needs no PdfName wrapper allocated around it.
        private string ReadName(string key)
        {
            if (_dict == null) return null;
            return (_dict[key] as PdfName)?.Value;
        }
    }
}
