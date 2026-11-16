// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Forms
{
    /// <summary>
    /// The child dictionaries a form-tree node's array names — the form's <c>/Fields</c>
    /// (§12.7.3, Table 218) and a field's <c>/Kids</c> (Table 220) — following an indirect
    /// reference at the array and at every element.
    /// </summary>
    /// <remarks>
    /// <para><b>The two arrivals are one question, and the file said so.</b>
    /// <c>PdfFormField.LoadKids</c> carried this comment: "<c>/Kids</c> is frequently written as an
    /// indirect reference, and <c>GetArray</c> does NOT follow one — <b>the identical defect
    /// <c>PdfAcroForm.LoadFields</c> documents for <c>/Fields</c>, fixed there and not here, 74
    /// lines away in the same folder</b>." A comment asserting that two implementations agree is a
    /// duplication report, so it became this.</para>
    ///
    /// <para><b>What each copy got wrong, in turn, is the argument for one.</b> Reading the array
    /// without resolving made an indirect <c>/Fields</c> look like a form with no fields — hiding
    /// every signature from <c>GetSignatureFields</c> and everything built on it — and made an
    /// indirect <c>/Kids</c> look like a <i>terminal</i> field, so a radio group found no
    /// <c>/AP</c> on-states and a signature group's <c>/FT</c> never resolved. Same omission, two
    /// different silent wrong answers, fixed months apart.</para>
    ///
    /// <para><b>A null <c>document</c> is tolerated rather than assumed away.</b>
    /// <c>PdfFormField</c>'s constructor is public and takes the document as a plain parameter, so
    /// a caller can hand it none and read a hand-built graph; <c>PdfAcroForm</c>'s constructor
    /// rejects null, so the direct fallbacks below simply never fire for it. Resolving is the
    /// superset — <c>PdfParser.ResolveObject</c> returns a direct object unchanged — so the two
    /// arrivals need no difference here beyond that.</para>
    /// </remarks>
    internal static class FormTreeChildren
    {
        /// <summary>
        /// The dictionaries named by <paramref name="parent"/>'s <paramref name="key"/> array, in
        /// order. Empty when the key is absent, is not an array, or names nothing resolvable —
        /// never null.
        /// </summary>
        internal static List<PdfDictionary> Resolve(
            PdfDictionary parent, PdfName key, PdfDocument document)
        {
            var children = new List<PdfDictionary>();
            if (parent == null) return children;

            var array = document?.ResolveObject(parent[key]) as PdfArray
                        ?? parent.GetArray(key);
            if (array == null) return children;

            for (int i = 0; i < array.Count; i++)
            {
                var child = document?.ResolveObject(array[i]) as PdfDictionary
                            ?? array[i] as PdfDictionary;
                if (child != null) children.Add(child);
            }
            return children;
        }
    }
}
