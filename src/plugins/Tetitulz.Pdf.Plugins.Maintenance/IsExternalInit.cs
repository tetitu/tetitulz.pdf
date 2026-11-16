// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

// Polyfill required for `init` property setters on netstandard2.0 / net4.x.
// The compiler looks for this type in System.Runtime.CompilerServices; if it is
// absent the compiler emits CS0518. Declaring it here satisfies the lookup.
//
// Unconditional, matching Tetitulz.Pdf.Base's copy: the type in the current assembly wins over the
// BCL one on net10.0, so both legs compile from the same source with no #if.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
