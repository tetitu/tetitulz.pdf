// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// A stream's own crypt filter — ISO 32000-2 §7.4.10's <c>/Crypt</c> entry in its <c>/Filter</c>
    /// array, which names the crypt filter that stream is encrypted with in place of the document's
    /// <c>/StmF</c> (or <c>/EFF</c>), <c>/Identity</c> for a stream left in the clear.
    /// </summary>
    /// <remarks>
    /// <para>"If the Crypt filter is present, it shall be the first filter in the Filter array", and
    /// its <c>/DecodeParms</c> entry's <c>/Name</c> names the filter, <c>/Identity</c> by default.</para>
    /// <para><b>Public because the Encryption assembly asks it</b> — the decryptor, the whole-file
    /// encryptor — as <see cref="Writing.PdfWriter"/> does, and Core's <c>InternalsVisibleTo</c>
    /// does not name Encryption. One reading, so the three cannot disagree about which streams a
    /// document left in the clear.</para>
    /// </remarks>
    public static class StreamCryptFilter
    {
        /// <summary>
        /// True when <paramref name="stream"/>'s first filter is <c>/Crypt</c>, with the crypt filter
        /// it names — <c>/Identity</c> when its parameters name none.
        /// </summary>
        public static bool TryGetName(PdfStream stream, out PdfName name)
        {
            name = null;
            if (stream == null) return false;
            PdfObject filter = stream[PdfName.Filter];
            PdfObject parms = stream[PdfName.DecodeParms];
            if (filter is PdfArray filters)
            {
                if (filters.Count == 0 || !(filters[0] is PdfName first) || first != PdfName.Crypt) return false;
                parms = parms is PdfArray all && all.Count > 0 ? all[0] : null;
            }
            else if (!(filter is PdfName only) || only != PdfName.Crypt) return false;

            name = (parms as PdfDictionary)?[PdfName.Name] as PdfName ?? PdfName.Identity;
            return true;
        }

        /// <summary>
        /// Takes the <c>/Crypt</c> filter and its parameters out of <paramref name="stream"/>'s
        /// <c>/Filter</c> and <c>/DecodeParms</c> — for a stream written decrypted, which no crypt
        /// filter describes any more. A stream without one is left as it is.
        /// </summary>
        public static void Remove(PdfStream stream)
        {
            if (!TryGetName(stream, out _)) return;
            if (stream[PdfName.Filter] is PdfArray filters)
            {
                var rest = new PdfArray();
                for (int i = 1; i < filters.Count; i++) rest.Add(filters[i]);
                if (rest.Count == 0) stream.Remove(PdfName.Filter); else stream.Set(PdfName.Filter, rest);

                if (stream[PdfName.DecodeParms] is PdfArray parms)
                {
                    var restParms = new PdfArray();
                    for (int i = 1; i < parms.Count; i++) restParms.Add(parms[i]);
                    if (restParms.Count == 0) stream.Remove(PdfName.DecodeParms); else stream.Set(PdfName.DecodeParms, restParms);
                }
                return;
            }
            stream.Remove(PdfName.Filter);
            stream.Remove(PdfName.DecodeParms);
        }
    }
}
