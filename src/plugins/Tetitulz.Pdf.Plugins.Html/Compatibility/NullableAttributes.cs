// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// Compiled into the netstandard2.0 leg ONLY — see the conditional <Compile> item in
// Tetitulz.Pdf.Builder.csproj. On net10.0 the real BCL types are used.
//
// Why a csproj condition rather than an #if: the same effect, but the build states which leg gets
// the file instead of the source carrying a branch. Declaring these unconditionally would work too
// (a type in the current assembly wins over an imported one) at the cost of a CS0436 warning per
// use site on net10.0, which is noise for something that is pure compiler metadata.
//
// These are attributes the nullable analysis reads and the runtime never sees, so the two legs
// cannot diverge in behaviour — the hazard that rules out conditional compilation elsewhere in this
// repository does not apply.

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>
    /// The argument is non-null when the method returns the given <c>bool</c>.
    /// </summary>
    /// <remarks>
    /// Arrived in .NET Core 3.0; netstandard2.0's reference assemblies do not carry it. Without it
    /// a <c>Try*</c> method cannot tell callers that its <c>out</c> parameter is set on success, and
    /// every call site then warns on the dereference — the exact shape CLAUDE.md records for
    /// <c>string.IsNullOrEmpty</c>, which lacks the annotation on this leg.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue)
        {
            ReturnValue = returnValue;
        }

        public bool ReturnValue { get; }
    }
}
