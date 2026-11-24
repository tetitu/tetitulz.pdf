// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// The UR transform parameters (ISO 32000-2 §12.8.2.3, Table 258) in both directions: the
    /// <see cref="UsageRights"/> flags a signer writes as names, and the flags a reader takes back
    /// from them.
    /// </summary>
    /// <remarks>
    /// <para><b>One table for the writer and the reader</b> (<c>PdfSigner</c> and
    /// <c>PdfUsageRightsReader</c>): with two, a right renamed on one side would write a grant
    /// the other could not read back.</para>
    ///
    /// <para>The mapping: <c>/FillIn</c> is form fill-in,
    /// <c>/Import</c> and <c>/Export</c> are form-data exchange, <c>/SubmitStandalone</c> is the
    /// form-save right, any <c>/Annots</c> array is the annotation right, and <c>/Document
    /// [/FullSave]</c> is document save.</para>
    /// </remarks>
    internal static class UsageRightsTransform
    {
        /// <summary>
        /// Whether <paramref name="method"/> is the usage-rights transform: <c>/UR</c>, as Table 256
        /// names it, or <c>/UR3</c>, which is what viewers — and this library's signer — write.
        /// </summary>
        internal static bool IsUsageRightsMethod(PdfName method)
            => method == PdfName.UR || method == PdfName.UR3;

        /// <summary>
        /// The rights entries of a transform parameters dictionary for <paramref name="rights"/>,
        /// as PDF syntax with a leading space — <c>" /Form [/FillIn] /Document [/FullSave]"</c> —
        /// or an empty string for <see cref="UsageRights.None"/>.
        /// </summary>
        /// <remarks>
        /// One <c>/Form</c> array however many form rights are granted (Table 258). Each form right
        /// once appended a <c>/Form</c> of its own, so granting two wrote the key twice — undefined
        /// under §7.3.7, and whichever copy a reader keeps, the other grant is lost.
        /// </remarks>
        internal static string Entries(UsageRights rights)
        {
            var sb = new StringBuilder();

            var form = new StringBuilder();
            if ((rights & UsageRights.FormFillIn) != 0) Append(form, PdfName.FillIn);
            if ((rights & UsageRights.FormImportExport) != 0) { Append(form, PdfName.Import); Append(form, PdfName.Export); }
            if ((rights & UsageRights.FormSave) != 0) Append(form, PdfName.SubmitStandalone);
            if (form.Length > 0)
                sb.Append(" /").Append(PdfName.Form.Value).Append(" [").Append(form.ToString(1, form.Length - 1)).Append(']');

            if ((rights & UsageRights.Annotation) != 0)
            {
                var annots = new StringBuilder();
                foreach (var name in new[] { PdfName.Create, PdfName.Modify, PdfName.Copy, PdfName.Import, PdfName.Export })
                    Append(annots, name);
                sb.Append(" /").Append(PdfName.Annots.Value).Append(" [").Append(annots.ToString(1, annots.Length - 1)).Append(']');
            }

            if ((rights & UsageRights.DocumentSave) != 0)
                sb.Append(" /").Append(PdfName.Document.Value).Append(" [/").Append(PdfName.FullSave.Value).Append(']');

            return sb.ToString();
        }

        /// <summary>
        /// The rights a transform parameters dictionary grants. Every value is resolved, the array
        /// elements included, since §7.3.10 lets any of them be an indirect reference.
        /// </summary>
        internal static UsageRights Read(PdfDictionary transformParams, Func<PdfObject, PdfObject> resolve)
        {
            UsageRights rights = UsageRights.None;
            if (transformParams == null) return rights;

            if (resolve(transformParams[PdfName.Form]) is PdfArray form)
            {
                for (int i = 0; i < form.Count; i++)
                {
                    var name = resolve(form[i]) as PdfName;
                    if (name == PdfName.FillIn) rights |= UsageRights.FormFillIn;
                    if (name == PdfName.Import || name == PdfName.Export) rights |= UsageRights.FormImportExport;
                    if (name == PdfName.SubmitStandalone) rights |= UsageRights.FormSave;
                }
            }

            if (resolve(transformParams[PdfName.Annots]) is PdfArray)
                rights |= UsageRights.Annotation;

            if (resolve(transformParams[PdfName.Document]) is PdfArray document)
            {
                for (int i = 0; i < document.Count; i++)
                    if ((resolve(document[i]) as PdfName) == PdfName.FullSave)
                        rights |= UsageRights.DocumentSave;
            }

            return rights;
        }

        private static void Append(StringBuilder sb, PdfName name) => sb.Append(" /").Append(name.Value);
    }
}
