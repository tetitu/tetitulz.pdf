// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// Polyfill required for `init` property setters on netstandard2.0 / net4.x.
// The compiler looks for this type in System.Runtime.CompilerServices; if it is
// absent the compiler emits CS0518.  Declaring it here satisfies the lookup.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
