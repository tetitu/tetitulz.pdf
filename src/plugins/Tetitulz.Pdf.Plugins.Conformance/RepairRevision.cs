// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// How a PDF/A repair writes what it revised: nothing revised, the input copied through
    /// unchanged; otherwise one incremental revision of the objects in object-number order, its
    /// trailer carrying the input's <c>/Root</c>, <c>/Info</c> and <c>/ID</c>. The annotation and
    /// embedded-file repairs each wrote this tail.
    /// </summary>
    internal static class RepairRevision
    {
        internal static void Write(byte[] input, Stream output, PdfDocument doc,
            IReadOnlyDictionary<int, IndirectObject> updates)
        {
            if (updates.Count == 0)
            {
                output.Write(input, 0, input.Length);
                return;
            }

            var list = new List<IndirectObject>(updates.Values);
            list.Sort((a, b) => a.ObjectNumber.CompareTo(b.ObjectNumber));
            var trailer = new PdfDictionary();
            foreach (var key in new[] { PdfName.Root, PdfName.Info, PdfName.ID })
                if (doc.Trailer?[key] is PdfObject value) trailer.Set(key, value);
            IncrementalUpdateWriter.WriteUpdate(input, output, list,
                new IncrementalUpdateOptions { Document = doc, OriginalTrailer = trailer });
        }
    }
}
