// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The paragraph direction supplied to <see cref="UnicodeBidi.Reorder(string, BaseDirection)"/>
    /// and <see cref="TextRunComposer"/>.
    ///
    /// <para>UAX #9 §3.3.1 allows a "higher-level protocol" to determine the paragraph embedding
    /// level in place of rules P2/P3. That is exactly what a form field, a table cell or a template
    /// slot is: the direction is a property of the container, and the value inside it may begin
    /// with a digit, a Latin word or punctuation without meaning the container is left-to-right.
    /// </para>
    /// </summary>
    public enum BaseDirection
    {
        /// <summary>
        /// Detect the paragraph level from the first strong character (P2/P3). This is the
        /// behaviour of the single-argument <see cref="UnicodeBidi.Reorder(string)"/>, and the
        /// right choice for running text whose direction is not known from context.
        /// </summary>
        Auto = 0,

        /// <summary>Force an even (left-to-right) paragraph embedding level.</summary>
        LeftToRight = 1,

        /// <summary>Force an odd (right-to-left) paragraph embedding level.</summary>
        RightToLeft = 2,
    }
}
