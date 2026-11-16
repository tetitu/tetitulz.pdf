// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Import;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// An XFA image's base64 body loses its white space by a loop, not a pattern — the body is the file's,
/// and a regular expression over it would need a timeout — held to the pattern it replaced.
/// </summary>
public class XfaWhiteSpaceStripTests
{
    [Theory]
    [InlineData("QUJD\nREVG")]
    [InlineData("  QU JD \t\r\n ")]
    [InlineData("")]
    [InlineData("QUJD  REVG")]
    [InlineData("nothing-to-strip")]
    public void TheStrip_AgreesWithThePattern(string body)
        => Assert.Equal(Regex.Replace(body, @"\s+", ""), XfaTemplate.WithoutWhiteSpace(body));
}
