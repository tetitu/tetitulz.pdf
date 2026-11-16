// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Recognises, inside an object being written back out, an indirect object that <c>PdfParser</c> resolved into
    /// place while reading — so the writer can put back the reference the file had there.
    /// </summary>
    /// <remarks>
    /// <para>The parser replaces a stream's indirect <c>/Filter</c> and <c>/DecodeParms</c>, the elements of a
    /// <c>/Filter</c> or <c>/DecodeParms</c> array, and every indirect entry inside a decode-parameter dictionary with
    /// the object itself, so the in-memory graph holds object N inline where the file held <c>N G R</c>. A writer
    /// that serialises the graph as it finds it writes an inline <i>copy</i>: a nested stream keeps only its dictionary
    /// (<c>PdfWriter</c> can give it no data), the reference to the object that still holds the data is gone, and an
    /// encrypting writer keys the copy under the object it was resolved from although every reader — this assembly's
    /// own decryptor included — deciphers what sits inside object M under M's key (ISO 32000-2 §7.6.3.2, Algorithm 1
    /// step (a)). A <c>/JBIG2Globals</c> stream lost that way loses the image.</para>
    /// <para><b>Only a container or a string can be told apart this way.</b> The parser creates those afresh for every
    /// object it parses, so one that sits inside another object and yet carries an object number can only have been
    /// stamped as a top-level object. <c>true</c>, <c>false</c> and <c>null</c> are shared instances that the parser
    /// stamps with whichever object number last held one at the top level, so a nested one's number says nothing about
    /// where it came from — and written inline it is correct anyway, as is any other scalar.</para>
    /// </remarks>
    internal static class ParserInlinedObjects
    {
        /// <summary>
        /// When <paramref name="value"/>, found inside another object, is an indirect object the parser resolved into
        /// place, the reference that stood there in the file.
        /// </summary>
        internal static bool TryReference(PdfObject value, out PdfReference reference)
        {
            if (value != null && value.ObjectNumber > 0
                && (value is PdfDictionary || value is PdfArray || value is PdfString))
            {
                reference = new PdfReference(value.ObjectNumber, value.GenerationNumber);
                return true;
            }

            reference = null;
            return false;
        }

        /// <summary>
        /// Puts the file's references back into every object of <paramref name="objects"/> before a writer
        /// sees the graph: each nested container or string the parser resolved into place (one carrying
        /// another object's number) becomes the <c>N G R</c> that stood there. One pass for every layout
        /// and every encryptor — the compressed layout's writer, the one that keeps the original
        /// encryption and the public-key one write the graph as they find it, and each wrote a resolved
        /// <c>/JBIG2Globals</c> as a bare dictionary, losing the image.
        /// </summary>
        internal static void RestoreReferences(Dictionary<int, PdfObject> objects)
        {
            foreach (var kvp in objects) Restore(kvp.Value, kvp.Key, 0);
        }

        private static void Restore(PdfObject container, int owner, int depth)
        {
            if (depth > PdfLimits.MaxObjectGraphDepth) return;
            switch (container)
            {
                case PdfDictionary dictionary:   // a stream's dictionary too
                    foreach (var entry in dictionary.ToList())
                    {
                        if (IsInlined(entry.Value, owner, out PdfReference reference)) dictionary.Set(entry.Key, reference);
                        else Restore(entry.Value, owner, depth + 1);
                    }
                    break;
                case PdfArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        if (IsInlined(array[i], owner, out PdfReference reference)) array[i] = reference;
                        else Restore(array[i], owner, depth + 1);
                    }
                    break;
            }
        }

        /// <summary>An inlined object of another number than the one being written; the owner's own number marks nothing nested.</summary>
        private static bool IsInlined(PdfObject value, int owner, out PdfReference reference)
        {
            if (TryReference(value, out reference) && reference.ReferenceObjectNumber != owner) return true;
            reference = null;
            return false;
        }
    }
}
