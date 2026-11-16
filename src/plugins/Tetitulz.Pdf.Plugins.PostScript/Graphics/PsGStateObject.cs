// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// A graphics state as an object (PLRM §4.2, §8.2): what <c>gstate</c> makes and
    /// <c>currentgstate</c> fills — a copy of the state and of the current path, which is part of
    /// it — and what <c>setgstate</c> makes current. Composite, so every reference to one object
    /// sees what <c>currentgstate</c> last copied into it.
    /// </summary>
    internal sealed class PsGStateObject
    {
        /// <summary>The state copied, or null for an object made with no graphics engine attached.</summary>
        public PsGraphicsState State;

        /// <summary>The current path when the state was copied, in that state's user space.</summary>
        public PsPath.Snapshotted Path;
    }
}
