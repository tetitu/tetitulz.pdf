// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfObjectIndex"/> maps each indirect object to its own number, even when two of them
/// are equal by value.
/// </summary>
/// <remarks>
/// <para><c>PdfName</c> and <c>PdfReference</c> override <c>Equals</c> with value semantics, which
/// is why the index is keyed by reference identity. Nothing tested that: every fixture's indirect
/// objects were dictionaries, which compare by reference anyway, so keying the index by value was a
/// surviving mutation. Two indirect objects that are both the name <c>/X</c> share one value, and a
/// value-keyed index gives the second one the first one's number.</para>
/// </remarks>
public class PdfObjectIndexIdentityTests
{
    [Fact]
    public void TwoIndirectObjectsEqualByValueKeepTheirOwnNumbers()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            "/X",
            "/X");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfObject four = doc.GetObject(4), five = doc.GetObject(5);
        Assert.Equal(four, five);                      // the premise: equal by value
        Assert.False(ReferenceEquals(four, five));     // and two objects

        var index = PdfObjectIndex.Build(doc);
        Assert.Equal(4, index.GetObjectNumber(four));
        Assert.Equal(5, index.GetObjectNumber(five));
    }

    /// <summary>
    /// An object the index did not record answers by the number stamped on it only when the
    /// cross-reference table holds that number: a copy stamped 3 is object 3, one stamped 99 is
    /// nothing the file holds (−1), and so is one with no stamp. Accessibility's
    /// <c>StagedObject.ObjectNumberOf</c> forwards here, so this is the one test of the rule.
    /// </summary>
    [Fact]
    public void AnUnrecordedObject_AnswersByItsStampOnlyWhenTheTableHoldsIt()
    {
        byte[] pdf = ActionTargetAgreementTests.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>");

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var index = PdfObjectIndex.Build(doc);

        Assert.Equal(3, index.GetObjectNumber(new PdfDictionary { ObjectNumber = 3 }));
        Assert.Equal(-1, index.GetObjectNumber(new PdfDictionary { ObjectNumber = 99 }));
        Assert.Equal(-1, index.GetObjectNumber(new PdfDictionary()));
    }
}
