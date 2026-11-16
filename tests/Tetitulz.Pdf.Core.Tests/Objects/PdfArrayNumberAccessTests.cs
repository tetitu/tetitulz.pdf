// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Objects;

/// <summary>
/// An array element that is not a number is the file's content, so asking for one as a number is
/// refused with a <see cref="PdfException"/>, the type a host's <c>catch (PdfException)</c> is written
/// for. It was an <see cref="InvalidOperationException"/>. An index outside the array stays an
/// argument error: that one is the caller's.
/// </summary>
public sealed class PdfArrayNumberAccessTests
{
    private static PdfArray Mixed() => new PdfArray { new PdfNumber(7), new PdfName("Seven") };

    [Fact]
    public void A_name_asked_for_as_a_number_is_refused_as_pdf_content()
    {
        var array = Mixed();

        Assert.Contains("index 1", Assert.Throws<PdfException>(() => array.GetInteger(1)).Message, StringComparison.Ordinal);
        Assert.Contains("index 1", Assert.Throws<PdfException>(() => array.GetLong(1)).Message, StringComparison.Ordinal);
        Assert.Contains("index 1", Assert.Throws<PdfException>(() => array.GetDouble(1)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_is_read_and_an_index_outside_the_array_is_the_callers_error()
    {
        var array = Mixed();

        Assert.Equal(7, array.GetInteger(0));
        Assert.Equal(7L, array.GetLong(0));
        Assert.Equal(7.0, array.GetDouble(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => array.GetInteger(2));
    }
}
