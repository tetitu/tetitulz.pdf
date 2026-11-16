// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>Options for <see cref="PdfLinearizer"/>.</summary>
    public sealed class LinearizationOptions
    {
        /// <summary>
        /// Linearize a signed document anyway. Off by default: linearization gives every object a
        /// new number and a new place in the file, so a signature's <c>/ByteRange</c> would name
        /// offsets that no longer exist, and the output would still look signed while verifying as
        /// corrupt.
        /// </summary>
        public bool AllowSignatureInvalidation { get; set; }
    }
}
