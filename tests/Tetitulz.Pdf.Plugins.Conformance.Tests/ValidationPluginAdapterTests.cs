// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The scaffolding the PDF/A, PDF/X and PDF/UA-1 plugins share (<see cref="ValidationPluginAdapter"/>)
/// and the tail the two PDF/A repairs share (<see cref="RepairRevision"/>). Neither copy of either
/// had a test for its null handling or for what the revision's trailer carries.
/// </summary>
public sealed class ValidationPluginAdapterTests
{
    [Fact]
    public void A_null_code_or_message_is_written_as_empty()
    {
        var noCode = ValidationPluginAdapter.Entry(null, "m");
        var noMessage = ValidationPluginAdapter.Entry("A01", null);

        Assert.Equal(("", "m"), (noCode.Code, noCode.Message));
        Assert.Equal(("A01", ""), (noMessage.Code, noMessage.Message));
    }

    [Fact]
    public void A_validator_returning_null_is_refused_naming_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ValidationPluginAdapter.RunAt<object>(new MemoryStream(new byte[4]), _ => null, "TheValidator"));

        Assert.Contains("TheValidator", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The control: a validator's answer is handed back, the stream where it was.</summary>
    [Fact]
    public void A_validators_answer_is_returned_and_the_stream_put_back()
    {
        var stream = new MemoryStream(new byte[16]) { Position = 5 };
        var answer = new object();

        var got = ValidationPluginAdapter.RunAt(stream, s => { s.Position = 12; return answer; }, "V");

        Assert.Same(answer, got);
        Assert.Equal(5, stream.Position);
    }

    /// <summary>
    /// The appended revision's own trailer carries the input's /Root, /Info and /ID — read from the
    /// bytes after the input, since a parser merges an earlier trailer's keys into what it reports.
    /// </summary>
    [Fact]
    public void A_repairs_revision_carries_the_inputs_root_info_and_id()
    {
        byte[] input = Pdf();
        using var doc = PdfDocument.Open(new MemoryStream(input));
        var updates = new Dictionary<int, IndirectObject>
        {
            [3] = new IndirectObject(3, 0, new PdfDictionary { [PdfName.Title] = new PdfString("revised") }),
        };

        var output = new MemoryStream();
        RepairRevision.Write(input, output, doc, updates);

        byte[] written = output.ToArray();
        Assert.Equal(input, written[..input.Length]);
        string revision = Encoding.Latin1.GetString(written, input.Length, written.Length - input.Length);
        Assert.Contains("(revised)", revision, StringComparison.Ordinal);
        int trailer = revision.LastIndexOf("trailer", StringComparison.Ordinal);
        string afterTrailer = trailer >= 0 ? revision[trailer..] : revision[revision.LastIndexOf("/Type /XRef", StringComparison.Ordinal)..];
        Assert.Contains("/Root 1 0 R", afterTrailer, StringComparison.Ordinal);
        Assert.Contains("/Info 3 0 R", afterTrailer, StringComparison.Ordinal);
        Assert.Contains("/ID", afterTrailer, StringComparison.Ordinal);
    }

    private static byte[] Pdf()
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Title (original) >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R /Info 3 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
