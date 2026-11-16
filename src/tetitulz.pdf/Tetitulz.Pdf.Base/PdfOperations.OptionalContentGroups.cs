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
using Tetitulz.Pdf.Base.Operations.Structure;
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
        /// <summary>Returns all Optional Content Groups (layers) in the document.</summary>
        /// <remarks>
        /// Kept as a distinct overload rather than widened with an optional <c>password</c>
        /// parameter: C# bakes optional-argument defaults in at the call site, so widening a shipped
        /// signature is a <b>binary</b> break for an already-compiled consumer. See "Password
        /// overloads are additive, never widened" on <c>PdfOperations.WithDecrypted</c>.
        /// </remarks>
        public static IReadOnlyList<OcgInfo> ListLayers(Stream input)
            => ListLayers(input, null);

        /// <summary>
        /// Returns all Optional Content Groups (layers) in the document, decrypting first when it is
        /// encrypted and a decryptor is registered.
        /// </summary>
        /// <param name="input">The PDF stream to read.</param>
        /// <param name="password">
        /// The user password; ignored for an unencrypted document. A layer's <c>/Name</c> is a PDF
        /// string, so it is ciphertext in an encrypted file.
        /// </param>
        public static IReadOnlyList<OcgInfo> ListLayers(Stream input, string password)
            => WithDecrypted(input, password, PdfOcgManager.ListLayers);

        /// <summary>Sets the default visibility of a layer by name.</summary>
        public static int SetLayerVisibility(Stream input, Stream output, string layerName, bool visible)
            => PdfOcgManager.SetLayerVisibility(input, output, layerName, visible);

        /// <summary>Renames a layer.</summary>
        public static int RenameLayer(Stream input, Stream output, string oldName, string newName)
            => PdfOcgManager.RenameLayer(input, output, oldName, newName);

        /// <summary>
        /// Creates a layer and returns its object number, which
        /// <see cref="AssignObjectToLayer"/> takes.
        /// </summary>
        /// <remarks>
        /// The group, the catalog's inventory of groups and the default configuration are written in
        /// one appended revision: a group missing from <c>/OCProperties /OCGs</c> is invisible to a
        /// viewer's layer panel, so writing it alone would produce a layer nobody can turn off.
        /// </remarks>
        public static int CreateLayer(Stream input, Stream output, string layerName,
                                      bool visible = true)
            => PdfOcgManager.CreateLayer(input, output, layerName, visible);

        /// <summary>
        /// Places an existing image or form XObject, or an annotation, under a layer.
        /// </summary>
        /// <remarks>
        /// <b>Whole objects only, and that is the distinction from
        /// <see cref="AssignPageContentToLayer"/>.</b> This writes an <c>/OC</c> key, which covers
        /// everything a document draws <i>through a reference</i>; §8.11.3.2's other route —
        /// a <c>/OC … BDC</c> section inside a content stream — is what ink written
        /// directly into the stream needs, because it has no object to hang a key on.
        /// </remarks>
        /// <returns><c>true</c> when a revision was appended.</returns>
        public static bool AssignObjectToLayer(Stream input, Stream output,
                                               int objectNumber, int ocgObjectNumber)
            => PdfOcgManager.AssignObjectToLayer(input, output, objectNumber, ocgObjectNumber);

        /// <summary>
        /// Places a page's existing content under a layer, through a <c>/OC … BDC</c>
        /// marked-content section (§8.11.3.2).
        /// </summary>
        /// <param name="input">Source PDF stream.</param>
        /// <param name="output">Receives the document with one revision appended.</param>
        /// <param name="pageIndex">Zero-based page whose content joins the layer.</param>
        /// <param name="ocgObjectNumber">
        /// The group, from <see cref="CreateLayer"/> or <c>ListLayers</c>.
        /// </param>
        /// <returns><c>true</c> when the page was wrapped; <c>false</c> when the index names none.</returns>
        /// <remarks>
        /// <para><b>The whole page, deliberately.</b> Naming a region would mean deciding which
        /// operators fall inside it, and a content stream has no addressable sub-parts — a
        /// caller needing finer control draws through a form XObject and uses
        /// <see cref="AssignObjectToLayer"/>.</para>
        ///
        /// <para><b>The existing content is not rewritten.</b> §7.8.2 concatenates a
        /// <c>/Contents</c> array as if it were one stream, so the section's two operators go in new
        /// streams either side of it — the original bytes are untouched and a signature over
        /// them still verifies. It also composes: wrapping twice nests two sections.</para>
        /// </remarks>
        public static bool AssignPageContentToLayer(Stream input, Stream output,
                                                    int pageIndex, int ocgObjectNumber)
            => PdfOcgManager.AssignPageContentToLayer(input, output, pageIndex, ocgObjectNumber);
    }
}
