// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Which of a source document's objects are its file layout rather than its content — the objects a whole-file
    /// rewrite (<see cref="PdfDecryptor"/>, <see cref="PdfEncryptor"/>, <see cref="PdfPublicKeyEncryptor"/>) does not
    /// carry into the file it writes.
    /// </summary>
    /// <remarks>
    /// <para>A cross-reference stream (<c>/Type /XRef</c>, ISO 32000-1 §7.5.8) describes the source's byte offsets,
    /// which the rewrite invalidates; it is never encrypted (§7.5.8.2). An object stream (<c>/Type /ObjStm</c>,
    /// §7.5.7) is a container: the parser hands its members over as objects of their own, so the container itself
    /// is a second copy of them that nothing references.</para>
    /// <para><b>One rule, for the decryptor and both encryptors.</b> An encryptor enumerating every
    /// cross-reference entry would write the containers and the stale cross-reference stream back,
    /// encrypted and unreferenced, beside the members they hold — a compressed input coming out larger
    /// than it went in.</para>
    /// </remarks>
    internal static class FileLayoutObjects
    {
        /// <summary>Whether <paramref name="obj"/> is a cross-reference stream.</summary>
        internal static bool IsCrossReferenceStream(PdfObject obj)
            => obj is PdfStream stream && stream.GetTypeName() == PdfName.XRef;

        /// <summary>Whether <paramref name="obj"/> is an object stream (a container of other objects).</summary>
        internal static bool IsObjectStream(PdfObject obj)
            => obj is PdfStream stream && stream.GetTypeName() == PdfName.ObjStm;

        /// <summary>Whether <paramref name="obj"/> is layout, not content: a cross-reference or object stream.</summary>
        internal static bool IsLayout(PdfObject obj) => IsCrossReferenceStream(obj) || IsObjectStream(obj);
    }
}
