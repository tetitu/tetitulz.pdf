// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Redaction over an acyclic fan-out of forms is refused by a walk budget, not walked to completion.
/// </summary>
/// <remarks>
/// <para><b>The depth cap fails closed on a cycle only because a cycle reaches it on its first
/// branch.</b> A chain of fifteen forms each drawing the next ten times never exceeds
/// <c>MaxFormDepth</c> (16) and is ~10^15 content walks. The redaction runs on its own thread with a
/// generous join, so a regression reports "did not return" rather than hanging the suite, and the
/// assertion is the refusal's own reason.</para>
/// </remarks>
public class RedactionFormFanOutTests
{
    private static byte[] FanOutPdf(int levels, int perLevel)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /F0 5 0 R >> >> >>",
            "<< /Length 7 >>\nstream\n/F0 Do\nendstream",
        };
        for (int level = 0; level < levels; level++)
        {
            string content = string.Concat(Enumerable.Repeat($"/F{level + 1} Do ", perLevel));
            bodies.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] " +
                       $"/Resources << /XObject << /F{level + 1} {6 + level} 0 R >> >> /Length {content.Length} >>\n" +
                       $"stream\n{content}\nendstream");
        }
        const string leaf = "0 0 5 5 re f";
        bodies.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Length {leaf.Length} >>\nstream\n{leaf}\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void AnAcyclicFormFanOutUnderTheDepthCapIsRefusedByTheWalkBudget()
    {
        byte[] pdf = FanOutPdf(levels: 15, perLevel: 10);
        Exception thrown = null;

        var worker = new Thread(() =>
        {
            try
            {
                PdfRedactionEngine.Redact(new MemoryStream(pdf), new MemoryStream(), new[]
                {
                    new PdfRedactionArea { PageIndex = 0, X = 0, Y = 0, Width = 50, Height = 50 },
                });
            }
            catch (Exception ex) { thrown = ex; }
        }) { IsBackground = true };
        worker.Start();

        Assert.True(worker.Join(BoundedWalkTimeout.Milliseconds), "redaction did not return");
        var refusal = Assert.IsType<PdfRedactionException>(thrown);
        Assert.Contains("too many to redact safely", refusal.Message);
    }
}
