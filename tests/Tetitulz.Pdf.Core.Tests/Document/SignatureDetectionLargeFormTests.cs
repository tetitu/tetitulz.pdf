// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfSignatureDetection.HasSignature"/> on a large form. The field walk stopped at
/// 16 384 nodes and answered false. A form whose signature field came after that many nodes was
/// reported unsigned, and the rewriters this guards would have broken the signature. The walk now
/// runs to the document's object ceiling, and a walk that stops early answers true, because
/// stopping is not evidence of absence.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class SignatureDetectionLargeFormTests
{
    [Fact]
    public void A_signature_after_twenty_thousand_fields_is_detected()
        => Assert.True(HasSignature(Form(20_000, signed: true)));

    /// <summary>The control: the same form without the signature is unsigned.</summary>
    [Fact]
    public void Twenty_thousand_unsigned_fields_are_unsigned()
        => Assert.False(HasSignature(Form(20_000, signed: false)));

    /// <summary>A walk cut short by the ceiling has no evidence, and answers signed.</summary>
    [Fact]
    public void Adversarial_field_tree_past_the_ceiling_counts_as_signed()
    {
        try
        {
            PdfLimits.MaxXrefEntries = 10;
            Assert.True(HasSignature(Form(20, signed: false)));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// A trailer whose catalog's form holds <paramref name="fields"/> text fields, each a field with
    /// a widget kid, then (when <paramref name="signed"/>) a signature field with a value.
    /// </summary>
    private static PdfDictionary Form(int fields, bool signed)
    {
        var array = new PdfArray();
        for (int i = 0; i < fields; i++)
        {
            var widget = new PdfDictionary { [PdfName.Type] = PdfName.Annot, [PdfName.Subtype] = new PdfName("Widget") };
            array.Add(new PdfDictionary
            {
                [PdfName.FT] = new PdfName("Tx"),
                [PdfName.T] = new PdfString("f" + i),
                [PdfName.Kids] = new PdfArray { widget },
            });
        }
        if (signed)
            array.Add(new PdfDictionary
            {
                [PdfName.FT] = new PdfName("Sig"),
                [PdfName.T] = new PdfString("sig"),
                [PdfName.V] = new PdfDictionary { [PdfName.Type] = new PdfName("Sig") },
            });

        var catalog = new PdfDictionary
        {
            [PdfName.Type] = PdfName.Catalog,
            [PdfName.AcroForm] = new PdfDictionary { [PdfName.Fields] = array },
        };
        return new PdfDictionary { [PdfName.Root] = catalog };
    }

    private static bool HasSignature(PdfDictionary trailer)
        => PdfSignatureDetection.HasSignature(trailer, _ => null);
}
