// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Required to <b>call</b> an <c>init</c> accessor — <c>PdfRenderOptions.Password</c> is one.
    /// </summary>
    /// <remarks>
    /// An <c>init</c> setter carries a <c>modreq</c> on this type, so the compilation that assigns
    /// to it needs the type to resolve. It arrived in .NET 5 and net48 has no copy;
    /// <c>Tetitulz.Pdf.Base</c> declares its own but <c>internal</c>, so this assembly cannot see
    /// it. Declaring one here is the standard arrangement and costs nothing at run time.
    /// </remarks>
    internal static class IsExternalInit
    {
    }
}
