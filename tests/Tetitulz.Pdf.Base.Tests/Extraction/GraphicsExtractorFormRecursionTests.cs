// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// <see cref="PdfGraphicsExtractor"/> on forms that recurse or fan out: bounded by a branch-scoped
/// cycle guard and a per-page walk budget, as <c>PdfStructuredTextExtractor</c> already was.
/// </summary>
/// <remarks>
/// <para><b>It had only a depth cap of 8</b>, and a depth cap bounds one branch, never the number of
/// branches: a form drawing itself ten times is 10⁹ walks under it, and so is an acyclic chain of
/// forms each drawing the next ten times. Each test runs the extraction on its own thread with a
/// generous join, so a regression reports "did not return" instead of hanging the suite, and asserts
/// the guard's own evidence — the reason on <see cref="PdfGraphicsExtractor.LastError"/>.</para>
/// </remarks>
public class GraphicsExtractorFormRecursionTests
{
    private static byte[] Pdf(IReadOnlyList<string> forms)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /F0 5 0 R >> >> >>",
            "<< /Length 7 >>\nstream\n/F0 Do\nendstream",
        };
        bodies.AddRange(forms);

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

    private static string Form(string resources, string content)
        => $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << {resources} >> " +
           $"/Length {content.Length} >>\nstream\n{content}\nendstream";

    private static (PdfPageGraphics? Page, string? Error) ExtractOnAThread(byte[] pdf)
    {
        PdfPageGraphics? page = null;
        string? error = null;
        var worker = new Thread(() =>
        {
            page = PdfGraphicsExtractor.Extract(new MemoryStream(pdf))[0];
            error = PdfGraphicsExtractor.LastError;
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(BoundedWalkTimeout.Milliseconds), "graphics extraction did not return");
        return (page, error);
    }

    [Fact]
    public void AFormThatDrawsItselfIsACycleStoppedAtTheFirstRepeat()
    {
        string content = string.Concat(Enumerable.Repeat("/F0 Do ", 10)) + "2 w 100 300 m 400 300 l S";
        byte[] pdf = Pdf(new[] { Form("/XObject << /F0 5 0 R >>", content) });

        var (page, error) = ExtractOnAThread(pdf);

        Assert.Contains("draws itself", error);
        Assert.Equal(300, Assert.Single(page.HorizontalRulings).Y, 1.0);
    }

    [Fact]
    public void AnAcyclicFanOutOfFormsIsBoundedByTheWalkBudget()
    {
        const int Levels = 8, PerLevel = 10;
        var forms = new List<string>();
        for (int level = 0; level < Levels; level++)
        {
            int self = 5 + level, next = self + 1;
            forms.Add(Form($"/XObject << /F{level + 1} {next} 0 R >>",
                           string.Concat(Enumerable.Repeat($"/F{level + 1} Do ", PerLevel))));
        }
        forms.Add(Form("", "2 w 100 300 m 400 300 l S"));
        byte[] pdf = Pdf(forms);

        var (_, error) = ExtractOnAThread(pdf);

        Assert.Contains("invocations exceeded", error);
    }
}
