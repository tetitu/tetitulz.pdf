// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Returns metadata for all signature fields found in the PDF AcroForm.
        /// Fields that have not yet been signed are included with <see cref="PdfSignatureInfo.HasSignature"/> = false.
        /// For cryptographic verification use <c>Tetitulz.Pdf.Signing.PdfSigningOperations.VerifySignatures</c>
        /// (a separate assembly — Base deliberately does not reference the signing stack).
        /// </summary>
        /// <param name="pdfPath">Path to the PDF file.</param>
        /// <remarks>
        /// <b>This overload takes no password</b>; it forwards a null one. Call
        /// <see cref="GetSignatures(string,string)"/> for an encrypted document.
        /// <para>Opening the file directly would never decrypt it, with no way for a caller holding
        /// the password to supply it: an encrypted signed document would answer <i>not signed</i>, a
        /// wrong answer to a security
        /// question, not a missing feature.</para>
        /// </remarks>
        public static IReadOnlyList<PdfSignatureInfo> GetSignatures(string pdfPath)
            => GetSignatures(pdfPath, null);

        /// <summary>
        /// Returns metadata for all signature fields, decrypting with <paramref name="password"/>.
        /// </summary>
        /// <remarks>
        /// A separate overload rather than an optional parameter on the one above: adding a
        /// defaulted parameter to a shipped method changes its signature and breaks callers already
        /// compiled against it.
        /// </remarks>
        public static IReadOnlyList<PdfSignatureInfo> GetSignatures(string pdfPath, string password)
        {
            if (pdfPath == null) throw new ArgumentNullException(nameof(pdfPath));
            using var fs = File.OpenRead(pdfPath);
            return GetSignatures(fs, password);
        }

        /// <summary>Returns metadata for all signature fields found in the PDF AcroForm.</summary>
        /// <param name="input">The PDF stream.</param>
        /// <param name="password">
        /// The user password. Ignored for an unencrypted document; an empty password is what most
        /// "owner-password only" files need.
        /// </param>
        /// <remarks>
        /// <para><b>An encrypted document does not report zero signatures</b> — that would be a wrong
        /// answer to a security question, not a missing feature: a caller asking "is this signed?"
        /// told <i>no</i> for an encrypted signed document.</para>
        /// <para>It was left out of the object-graph conversion on the grounds that a signature's
        /// <c>/ByteRange</c> indexes the file as stored, so a decrypted re-serialisation would
        /// digest bytes that never existed. That is true, and it is an argument about
        /// <b>verification</b> — which happens in <c>Tetitulz.Pdf.Signing</c>, against its own
        /// stream. This method only <i>lists</i>: <see cref="PdfSignatureInfo"/> carries no
        /// <c>/ByteRange</c>, no offsets and no digest, so there is nothing here for a
        /// re-serialisation to invalidate. Listing and verifying never shared a stream.</para>
        /// </remarks>
        public static IReadOnlyList<PdfSignatureInfo> GetSignatures(Stream input, string password = null)
            => WithDecrypted(input, password, s =>
            {
                using (var doc = PdfDocument.Open(s))
                    return ReadSignatureInfos(doc);
            });

        private static IReadOnlyList<PdfSignatureInfo> ReadSignatureInfos(PdfDocument doc)
        {
            var result = new List<PdfSignatureInfo>();
            if (doc.AcroForm == null) return result;

            foreach (PdfSignatureField field in doc.AcroForm.GetSignatureFields())
            {
                // The walk is permissive by design, so the filtering happens here — and it is
                // ASYMMETRIC on purpose. A node carrying its own /V is reported whatever else is
                // true of it, because this method answers "is this signed?" and hiding a signature
                // is the one failure that must not happen: on a /Kids cycle every node is
                // non-terminal, so a symmetric filter would call a signed document unsigned. Only
                // an UNSIGNED node has to earn its place by being addressable, which is what keeps
                // a widget kid — inheriting /FT /Sig, carrying no /T of its own — from arriving
                // here as a second, null-named entry for one signature field.
                if (!field.HasSignature && !field.IsAddressableField) continue;

                result.Add(new PdfSignatureInfo
                {
                    FieldName    = field.ReportedName,
                    HasSignature = field.HasSignature,
                    Reason       = field.Reason,
                    Location     = field.Location,
                    SigningTime  = field.SigningTime,
                    Filter       = field.Filter,
                    SubFilter    = field.SubFilter
                });
            }

            return result;
        }
    }
}
