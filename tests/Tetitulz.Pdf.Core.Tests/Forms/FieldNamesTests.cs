// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Forms;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Forms;

/// <summary>
/// <see cref="FieldNames.Qualify"/>: a parent's fully qualified name joined with a partial name
/// (§12.7.4.2).
/// </summary>
public class FieldNamesTests
{
    [Theory]
    [InlineData(null, "a", "a")]
    [InlineData("", "a", "a")]
    [InlineData("p", "a", "p.a")]
    [InlineData("p.q", "a", "p.q.a")]
    [InlineData("p", "", "p")]
    [InlineData("p", null, "p")]
    [InlineData(null, null, null)]
    [InlineData(null, "", null)]
    [InlineData("", null, "")]
    public void QualifySkipsAnEmptySegmentOnEitherSide(string parent, string partial, string expected)
        => Assert.Equal(expected, FieldNames.Qualify(parent, partial));

    /// <summary><see cref="FieldNames.Leaf"/>: the text after the last period, the whole name when there is none.</summary>
    [Theory]
    [InlineData("form1.page1.name", "name")]
    [InlineData("name", "name")]
    [InlineData("a.", "")]
    [InlineData(".a", "a")]
    [InlineData("", "")]
    public void LeafIsTheTextAfterTheLastPeriod(string qualified, string expected)
        => Assert.Equal(expected, FieldNames.Leaf(qualified));
}
