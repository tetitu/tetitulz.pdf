// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// A signature field's lock dictionary (ISO 32000-2 §12.7.5.5, Table 236), read when the field is
    /// signed, and the FieldMDP signature reference it becomes (§12.8.2.4, Table 259).
    /// </summary>
    /// <remarks>
    /// <para><b>§12.8.2.4:</b> "When the recipient signs the field, the signature, signature reference,
    /// and transform parameters dictionaries shall be created. The Action and Fields entries in the
    /// transform parameters dictionary shall be copied from the corresponding fields in the signature
    /// field lock dictionary." The copy is because a signature dictionary with a byte range holds only
    /// direct values, so the parameters cannot point at the lock dictionary. <c>PdfSigner</c> adopted a
    /// locked field and wrote no reference at all, so a document author's "these fields may not change
    /// once this person has signed" was dropped at the moment it was meant to take effect.</para>
    ///
    /// <para><b><c>/P</c> (PDF 2.0) is the DocMDP level the lock asks for.</b> Table 236 defines it with
    /// Table 257's values — 1, no changes; 2, form filling and signing; 3, annotations too — and a FieldMDP
    /// transform has no <c>/P</c> of its own, so the signature carries it as a DocMDP reference: the
    /// signature certifies the document at that level. A caller asking for a different level gets a
    /// refusal, not one of the two silently.</para>
    /// </remarks>
    internal sealed class SignatureFieldLock
    {
        private SignatureFieldLock(string action, IReadOnlyList<PdfString> fields, int permission)
        {
            Action = action;
            Fields = fields;
            Permission = permission;
        }

        /// <summary><c>All</c>, <c>Include</c> or <c>Exclude</c>.</summary>
        internal string Action { get; }

        /// <summary>The <c>/Fields</c> strings as written, so they are copied byte for byte.</summary>
        internal IReadOnlyList<PdfString> Fields { get; }

        /// <summary>The lock's <c>/P</c> — 1 to 3 — or 0 when it has none.</summary>
        internal int Permission { get; }

        /// <summary>
        /// The lock <paramref name="field"/> carries, or <c>null</c> when it carries none.
        /// </summary>
        /// <exception cref="PdfSigningException">
        /// The lock cannot be honoured as written — no valid <c>/Action</c>, no <c>/Fields</c> where
        /// Table 236 requires one, or a <c>/P</c> outside 1–3. Signing a locked field while dropping its
        /// lock is the outcome this refuses.
        /// </exception>
        internal static SignatureFieldLock? Read(PdfDictionary field, Func<PdfObject?, PdfObject?> resolve)
        {
            if (!(resolve(field[PdfName.Lock]) is PdfDictionary lockDict)) return null;

            string? action = (resolve(lockDict[PdfName.Action]) as PdfName)?.Value;
            if (action != PdfName.All.Value && action != PdfName.Include.Value && action != PdfName.Exclude.Value)
                throw new PdfSigningException(
                    "The signature field's /Lock dictionary " +
                    (action == null ? "has no /Action" : $"has /Action /{action}") +
                    "; ISO 32000-2 Table 236 requires /All, /Include or /Exclude, so the lock cannot be " +
                    "carried into the signature and the field is refused rather than signed without it.");

            var fields = new List<PdfString>();
            if (action != PdfName.All.Value)
            {
                if (!(resolve(lockDict[PdfName.Fields]) is PdfArray array))
                    throw new PdfSigningException(
                        $"The signature field's /Lock dictionary has /Action /{action} and no /Fields array, " +
                        "which ISO 32000-2 Table 236 requires for it; the lock cannot be carried into the " +
                        "signature and the field is refused rather than signed without it.");
                foreach (var item in array)
                    if (resolve(item) is PdfString name) fields.Add(name);
            }

            int permission = 0;
            if (resolve(lockDict[PdfName.P]) is PdfNumber p)
            {
                permission = p.IntValue;   // saturating, so an out-of-range /P is refused below rather than wrapped into 1–3
                if (permission < 1 || permission > 3)
                    throw new PdfSigningException(
                        $"The signature field's /Lock dictionary has /P {p}; ISO 32000-2 Table 236 allows 1, 2 " +
                        "or 3, so the lock cannot be honoured and the field is refused rather than signed " +
                        "without it.");
            }

            return new SignatureFieldLock(action, fields, permission);
        }

        /// <summary>
        /// The DocMDP level this signature is made at: the lock's <c>/P</c> when it has one and the
        /// caller asked for none, the caller's otherwise.
        /// </summary>
        /// <exception cref="PdfSigningException">The caller asked for a level the lock does not name.</exception>
        internal DocMdpPermission Reconcile(DocMdpPermission requested)
        {
            if (Permission == 0 || (int)requested == Permission) return requested;
            if (requested == DocMdpPermission.None) return (DocMdpPermission)Permission;

            throw new PdfSigningException(
                $"The signature field's /Lock dictionary asks for DocMDP /P {Permission} and " +
                $"SignatureParameters.DocMdpPermission asks for /P {(int)requested}. The document's author " +
                "chose the lock's level; sign with DocMdpPermission.None to take it, or sign a different field.");
        }

        /// <summary>
        /// Writes the FieldMDP signature reference (Table 258) with the parameters copied from the lock
        /// (Table 259). <paramref name="catalogObjectNumber"/> is the <c>/Data</c> the analysis is
        /// performed on — the whole document, as for DocMDP — which Table 258 requires to be indirect,
        /// at <paramref name="catalogGeneration"/>: a reference naming the catalog at 0 when it is
        /// at 1 names no object (§7.3.10), and the lock's analysis would have nothing to run on.
        /// </summary>
        internal void WriteSigRef(PdfWriter writer, int catalogObjectNumber, int catalogGeneration)
        {
            // The raw form is kept beside SigRefDictionary rather than written through it: it
            // differs by one space inside /Fields, and PdfSigner's raw revision is pinned byte for
            // byte. FieldLockSigRefParityTests parses this output against that dictionary, so the
            // two are a checked fact rather than a comment.
            writer.WriteRaw("<< /Type /SigRef /TransformMethod /FieldMDP" +
                            " /TransformParams << /Type /TransformParams /Action /" + Action);
            if (Action != PdfName.All.Value)
            {
                writer.WriteRaw(" /Fields [");
                foreach (var name in Fields)
                {
                    writer.WriteObject(name);
                    writer.WriteRaw(" ");
                }
                writer.WriteRaw("]");
            }
            writer.WriteRaw($" /V /1.2 >> /Data {catalogObjectNumber} {catalogGeneration} R /DigestMethod /SHA256 >>");
        }

        /// <summary>
        /// The same FieldMDP signature reference (Tables 258–259) as an object, for a signature
        /// planned as objects rather than raw bytes (<see cref="PdfSigner.Plan"/>).
        /// </summary>
        internal PdfDictionary SigRefDictionary(int catalogObjectNumber, int catalogGeneration)
        {
            var parameters = new PdfDictionary();
            parameters.Set(PdfName.Type, PdfName.TransformParams);
            parameters.Set(PdfName.Action, new PdfName(Action));
            if (Action != PdfName.All.Value)
            {
                var fields = new PdfArray();
                foreach (var name in Fields) fields.Add(name);
                parameters.Set(PdfName.Fields, fields);
            }
            parameters.Set(PdfName.V, new PdfName("1.2"));

            var sigRef = new PdfDictionary();
            sigRef.Set(PdfName.Type, PdfName.SigRef);
            sigRef.Set(PdfName.TransformMethod, PdfName.FieldMDP);
            sigRef.Set(PdfName.TransformParams, parameters);
            sigRef.Set(PdfName.Data, new PdfReference(catalogObjectNumber, catalogGeneration));
            sigRef.Set(PdfName.DigestMethod, new PdfName("SHA256"));
            return sigRef;
        }
    }
}
