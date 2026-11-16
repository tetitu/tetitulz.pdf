// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// The one rule the three whole-file writers — <see cref="PdfDecryptor"/>, <see cref="PdfEncryptor"/> and
    /// <see cref="PdfPublicKeyEncryptor"/> — follow for an object's generation number: <b>keep it</b>, in the
    /// <c>N G obj</c> header, in the cross-reference entry and under the per-object key.
    /// </summary>
    /// <remarks>
    /// <para>ISO 32000-2 §7.3.10 makes the generation part of the identity: "Together, the combination of an object
    /// number and a generation number shall uniquely identify an indirect object." A reference <c>N G R</c> names
    /// exactly that identifier, and "An indirect reference to an undefined object shall not be considered an error by
    /// a PDF processor; it shall be treated as a reference to the null object." §7.5.4 gives a reused object number a
    /// new generation so that references to the freed object cannot alias the new one.</para>
    /// <para>Writing every object as <c>N 0 obj</c> with a generation-0 cross-reference entry while leaving every
    /// <c>N G R</c> in the file as it was would make each such reference name an identifier the output no longer
    /// defines, and a conforming reader would resolve it to null. The other consistent rule, rewriting every
    /// reference to generation 0, would resurrect the stale references §7.5.4 exists to retire; keeping what was read
    /// is the only rule under which the output means what the input meant.</para>
    /// <para>The key follows the header: Algorithm 1 mixes the
    /// generation into the per-object key, so what is keyed must be what is written. An object stored in an object
    /// stream is at generation 0 by §7.5.7 ("The generation number of an object stream and of any compressed object
    /// shall be zero"), and that is what the parser stamps on one.</para>
    /// </remarks>
    internal static class ObjectIdentity
    {
        /// <summary>
        /// The generation <paramref name="source"/> is written at, keyed under and entered with: the one its header
        /// stated, which <c>PdfParser</c> stamps on every object it reads.
        /// </summary>
        internal static int GenerationOf(PdfObject source) => source.GenerationNumber;
    }
}
