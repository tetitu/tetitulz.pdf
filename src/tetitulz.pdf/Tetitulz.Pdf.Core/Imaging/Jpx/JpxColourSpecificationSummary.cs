// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// One <c>colr</c> box of a JP2 header as <see cref="JpxHeader.TryReadColourSpecifications"/>
    /// reports it: the fields a rule about the file reads (ISO 19005-2 §6.2.8.3), not a colour.
    /// </summary>
    public readonly struct JpxColourSpecificationSummary
    {
        internal JpxColourSpecificationSummary(int method, int precedence, int approximation, long enumeratedSpace, string iccDataColourSpace)
        {
            Method = method;
            Precedence = precedence;
            Approximation = approximation;
            EnumeratedSpace = enumeratedSpace;
            IccDataColourSpace = iccDataColourSpace;
        }

        /// <summary><c>METH</c>: 1 enumerated, 2 restricted ICC, 3 any ICC (T.801 Table M.22), 4 vendor, 5 parameterised.</summary>
        public int Method { get; }

        /// <summary><c>PREC</c>, the precedence a reader ranks the boxes by.</summary>
        public int Precedence { get; }

        /// <summary><c>APPROX</c>: 0 in a JP2, 1 to 4 in a JPX (T.801 Table M.23).</summary>
        public int Approximation { get; }

        /// <summary><c>EnumCS</c> for <see cref="Method"/> 1 (16 sRGB, 17 greyscale, 18 sYCC, 12 CMYK, 19 CIEJab, …), or −1.</summary>
        public long EnumeratedSpace { get; }

        /// <summary>The ICC profile's data colour space signature (<c>"RGB "</c>, <c>"GRAY"</c>, …) for an ICC method, or null.</summary>
        public string IccDataColourSpace { get; }
    }
}
