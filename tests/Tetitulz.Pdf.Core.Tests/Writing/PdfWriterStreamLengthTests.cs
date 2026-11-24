// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="PdfWriter.WriteIndirectObject"/> must emit a correct /Length for a stream even when
/// the caller did not pre-set one — set only after the dictionary is serialised, it would leave a
/// stream object with no /Length (rejected by strict readers, and unreadable on round-trip).
/// </summary>
public class PdfWriterStreamLengthTests
{
    [Fact]
    public void WriteIndirectObject_StreamWithoutPresetLength_EmitsCorrectLength()
    {
        var stream = new PdfStream { RawData = Encoding.ASCII.GetBytes("hello world") }; // no /Length set

        using var ms = new MemoryStream();
        new PdfWriter(ms).WriteIndirectObject(5, 0, stream);

        string s = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/Length 11", s);
        Assert.Contains("stream\nhello world\nendstream", s);
    }

    [Fact]
    public void WriteIndirectObject_StreamRoundTrips_ThroughParser()
    {
        byte[] payload = Encoding.ASCII.GetBytes("revocation bytes \x00\x01\x02");
        var stream = new PdfStream { RawData = payload };

        using var ms = new MemoryStream();
        new PdfWriter(ms).WriteIndirectObject(7, 0, stream);

        // The written /Length must equal the data length so a reader can recover the bytes.
        string header = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains($"/Length {payload.Length}", header);
    }
}
