// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tetitulz.Pdf.Core.Objects
{
    /// <summary>
    /// Keys a dictionary or set by <b>reference identity</b> rather than value equality.
    /// </summary>
    /// <remarks>
    /// <para>The BCL's <c>ReferenceEqualityComparer</c> does exactly this, but it arrived in .NET 5
    /// and several assemblies here also ship a <c>netstandard2.0</c> leg, where it does not exist.</para>
    /// <para><b>It is public because a plugin has to reach it.</b> Core's <c>InternalsVisibleTo</c>
    /// names no <c>Plugins.*</c> assembly, so an internal helper would leave every plugin writing its
    /// own — the same reasoning <see cref="Text.Latin1Text"/> states for itself.</para>
    /// <para><b>Not behind an <c>#if</c>, deliberately.</b> A conditional shim gives the two legs two
    /// implementations of one idea, which is the shape this repository has been bitten by before:
    /// <c>ISignatureVerifyPlugin</c> was once compiled *out* of the netstandard2.0 target and nothing
    /// noticed.</para>
    /// <para><b>Typed <c>IEqualityComparer&lt;object&gt;</c>, matching the BCL.</b>
    /// <c>IEqualityComparer&lt;in T&gt;</c> is contravariant, so this one instance serves
    /// <c>HashSet&lt;PdfObject&gt;</c>, <c>Dictionary&lt;PdfDictionary, T&gt;</c> and any other
    /// reference-keyed collection with no type argument spelled at the call site.</para>
    /// <para><see cref="RuntimeHelpers.GetHashCode(object)"/> is the identity hash — the same one the
    /// BCL comparer uses. The key's own <c>GetHashCode</c> must not be consulted, or two structurally
    /// identical objects would collide into one entry, which is precisely what callers use this to
    /// prevent.</para>
    /// </remarks>
    public sealed class PdfReferenceEquality : IEqualityComparer<object>
    {
        /// <summary>The single shared instance; the type holds no state.</summary>
        public static readonly PdfReferenceEquality Instance = new PdfReferenceEquality();

        private PdfReferenceEquality()
        {
        }

        /// <summary>True only when both arguments are the same object.</summary>
        /// <param name="x">First object.</param>
        /// <param name="y">Second object.</param>
        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        /// <summary>The runtime's identity hash for <paramref name="obj"/>.</summary>
        /// <param name="obj">The object to hash; may be null.</param>
        public int GetHashCode(object obj)
        {
            return RuntimeHelpers.GetHashCode(obj);
        }
    }
}
