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
        /// <summary>Lists all embedded file attachments in the PDF.</summary>
        public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(string inputPath)
            => PdfAttachmentManager.ListAttachments(inputPath);

        /// <summary>Lists all embedded file attachments in the PDF stream.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(Stream input)
            => ListAttachments(input, null);

        /// <summary>
        /// Lists all embedded file attachments in the PDF stream, decrypting first when the document
        /// is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">
        /// The user password; ignored for an unencrypted document. Both the file-specification name
        /// and the embedded stream are encrypted in an encrypted file.
        /// </param>
        public static IReadOnlyList<PdfAttachmentInfo> ListAttachments(Stream input, string password)
            => WithDecrypted(input, password, PdfAttachmentManager.ListAttachments);

        /// <summary>Embeds a file as an attachment (incremental update).</summary>
        public static void AddAttachment(string inputPath, string outputPath,
            string filename, byte[] data, string mimeType = null)
            => PdfAttachmentManager.AddAttachment(inputPath, outputPath, filename, data, mimeType);

        /// <summary>Embeds a file as an attachment (incremental update).</summary>
        public static void AddAttachment(Stream input, Stream output,
            string filename, byte[] data, string mimeType = null)
            => PdfAttachmentManager.AddAttachment(input, output, filename, data, mimeType);

        /// <summary>Extracts an embedded file attachment by display name.</summary>
        public static byte[] ExtractAttachment(string inputPath, string filename)
            => PdfAttachmentManager.ExtractAttachment(inputPath, filename);

        /// <summary>Extracts an embedded file attachment by display name.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter — see the binary-compatibility note on
        /// <see cref="ListAttachments(Stream)"/>.
        /// </remarks>
        public static byte[] ExtractAttachment(Stream input, string filename)
            => ExtractAttachment(input, filename, null);

        /// <summary>
        /// Extracts an embedded file attachment by display name, decrypting first when the document
        /// is encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="filename">The attachment's display name.</param>
        /// <param name="password">The user password; ignored for an unencrypted document.</param>
        public static byte[] ExtractAttachment(Stream input, string filename, string password)
            => WithDecrypted(input, password, s => PdfAttachmentManager.ExtractAttachment(s, filename));

        /// <summary>Removes an embedded file attachment by display name (incremental update).</summary>
        public static void RemoveAttachment(string inputPath, string outputPath, string filename)
            => PdfAttachmentManager.RemoveAttachment(inputPath, outputPath, filename);

        /// <summary>Removes an embedded file attachment by display name (incremental update).</summary>
        public static void RemoveAttachment(Stream input, Stream output, string filename)
            => PdfAttachmentManager.RemoveAttachment(input, output, filename);
    }
}
