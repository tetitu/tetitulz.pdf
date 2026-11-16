// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The stream rules, A76: <c>/Length</c> is the data's, no <c>/F</c>, <c>/FFilter</c> or
/// <c>/FDecodeParms</c>, and before PDF/A-4 <c>stream</c> followed by CRLF or LF and <c>endstream</c>
/// preceded by an EOL — ISO 19005-1 6.1.7, 19005-2/3 6.1.7.1, 19005-4 6.1.6.1, the published profile's rules of
/// those numbers. Object 5 is the stream under test, written byte for byte.
/// </summary>
public class PdfAStreamRuleTests
{
    private static List<PdfAViolation> Coded(string streamObject, int part, string extra = "")
        => PdfAXObjectRuleTests.Coded(PdfAXObjectRuleTests.Document("", "", "", streamObject, extra), part, "A76");

    private static void AssertFinding(List<PdfAViolation> found, string? expected)
    {
        if (expected == null)
            Assert.Empty(found);
        else
            Assert.Contains(expected, Assert.Single(found).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<< /Length 3 >>\nstream\nabc\nendstream", null)]
    [InlineData("<< /Length 3 >>\nstream\r\nabc\r\nendstream", null)]
    [InlineData("<< /Length 3 >>\nstream\nabc\rendstream", null)]
    [InlineData("<< /Length 0 >>\nstream\n\nendstream", null)]
    [InlineData("<< /Length 6 0 R >>\nstream\nabc\nendstream", null)]
    [InlineData("<< /Length 5 >>\nstream\nabc\nendstream", "object 5: /Length 5 does not end where the data does")]
    [InlineData("<< /Length 2 >>\nstream\nabc\nendstream", "object 5: /Length 2 does not end where the data does")]
    [InlineData("<< /Length 4 >>\nstream\nabc\nendstream", "object 5: /Length 4 does not end where the data does")]   // the EOL counted
    [InlineData("<< /Length 99999 >>\nstream\nabc\nendstream", "object 5: /Length 99999 runs past the file")]
    public void A76_TheLength_IsTheData(string streamObject, string? expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
            AssertFinding(Coded(streamObject, part, "3"), expected);
    }

    [Theory]
    [InlineData("<< /Length 3 >>\nstream\rabc\nendstream", 1, "object 5: stream not followed by CRLF or LF")]
    [InlineData("<< /Length 3 >>\nstream\rabc\nendstream", 2, "object 5: stream not followed by CRLF or LF")]
    [InlineData("<< /Length 3 >>\nstream\rabc\nendstream", 4, null)]
    [InlineData("<< /Length 3 >>\nstream abc\nendstream", 3, "object 5: stream not followed by CRLF or LF")]
    [InlineData("<< /Length 3 >>\nstream\nabcendstream", 2, "object 5: no EOL before endstream")]
    [InlineData("<< /Length 3 >>\nstream\nabcendstream", 4, null)]
    public void A76_BeforePdfA4_TheKeywordsEols(string streamObject, int part, string? expected)
        => AssertFinding(Coded(streamObject, part), expected);

    [Theory]
    [InlineData("/F (data.bin)", "object 5 /F")]
    [InlineData("/FFilter /FlateDecode", "object 5 /FFilter")]
    [InlineData("/FDecodeParms << >>", "object 5 /FDecodeParms")]
    public void A76_AnExternalFileKey_IsEveryPartsFinding(string key, string expected)
    {
        foreach (int part in new[] { 1, 2, 4 })
            AssertFinding(Coded("<< /Length 3 " + key + " >>\nstream\nabc\nendstream", part), expected);
    }
}
