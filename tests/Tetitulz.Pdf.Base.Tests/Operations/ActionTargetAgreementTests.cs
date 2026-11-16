// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfActionDispatcher"/> (which describes an action) and <see cref="PdfActionRunner"/>
/// (which raises it to the host) say the same thing about the same action.
/// </summary>
/// <remarks>
/// <para><b>The runner's parse said it "mirrored" the dispatcher's, and it did not.</b> The two
/// switches disagreed on six of the eleven action types. A <c>Named</c> action had no target in the
/// dispatcher and its name in the runner. A <c>/URI</c> written as a name had one in the dispatcher
/// and none in the runner. A file specification dictionary was read for its <c>/F</c> by one and
/// printed as a dictionary by the other. Neither read <c>/UF</c> first (Table 43), a Windows launch
/// target (Table 208) or a <c>SubmitForm</c> URL, and the runner documents its form event's target
/// as that URL.</para>
/// <para>Each case asks both and expects one answer. A test that asked only one side could not see
/// the two drift apart again.</para>
/// </remarks>
public class ActionTargetAgreementTests
{
    /// <summary>
    /// Each action type's target, the same from the dispatcher and the runner.
    /// </summary>
    [Theory]
    [InlineData("<< /S /Named /N /NextPage >>", "NextPage")]
    [InlineData("<< /S /URI /URI /mailto:someone@example.test >>", "mailto:someone@example.test")]
    [InlineData("<< /S /Launch /F << /Type /Filespec /F (report.doc) "
                + "/UF <FEFF0072006100700070006F00720074002E0064006F0063> >> >>", "rapport.doc")]
    [InlineData("<< /S /Launch /Win << /F (cmd.exe) /P (/c calc) >> >>", "cmd.exe")]
    [InlineData("<< /S /GoTo /D /Chapter1 >>", "Chapter1")]
    [InlineData("<< /S /GoTo /D [3 0 R /Fit] >>", "[array:2]")]
    [InlineData("<< /S /GoToR /F (other.pdf) /D (Chapter2) >>", "other.pdf:Chapter2")]
    [InlineData("<< /S /GoToR /F << /Type /Filespec /F (other.pdf) >> /D [0 /Fit] >>", "other.pdf:[array:2]")]
    [InlineData("<< /S /SubmitForm /F << /FS /URL /F (https://example.test/submit) >> >>", "https://example.test/submit")]
    [InlineData("<< /S /ImportData /F (data.fdf) >>", "data.fdf")]
    public void BothSidesDescribeTheActionAlike(string action, string expected)
    {
        byte[] pdf = OpenActionPdf(action);

        Assert.Equal(expected, DispatcherTarget(pdf));
        Assert.Equal(expected, RunnerTarget(pdf));
    }

    /// <summary>
    /// The acceptance control: the shapes the two already agreed on still agree, unchanged.
    /// </summary>
    [Theory]
    [InlineData("<< /S /URI /URI (https://example.test/a) >>", "https://example.test/a")]
    [InlineData("<< /S /Launch /F (report.doc) >>", "report.doc")]
    [InlineData("<< /S /GoTo /D (Chapter1) >>", "Chapter1")]
    public void WhatBothAlreadyAgreedOnIsUnchanged(string action, string expected)
    {
        byte[] pdf = OpenActionPdf(action);

        Assert.Equal(expected, DispatcherTarget(pdf));
        Assert.Equal(expected, RunnerTarget(pdf));
    }

    /// <summary>
    /// <c>/F</c> is the launch target when both it and <c>/Win</c> are present (Table 207: <c>/F</c>
    /// is the platform-neutral entry, <c>/Win</c> the deprecated alternative).
    /// </summary>
    [Fact]
    public void ALaunchWithBothFAndWinNamesF()
    {
        byte[] pdf = OpenActionPdf("<< /S /Launch /F (neutral.exe) /Win << /F (windows.exe) >> >>");

        Assert.Equal("neutral.exe", DispatcherTarget(pdf));
        Assert.Equal("neutral.exe", RunnerTarget(pdf));
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    internal static string DispatcherTarget(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return Assert.Single(PdfActionDispatcher.ExtractActions(doc)).Target;
    }

    /// <summary>The target of whichever event the runner raised for the document's open action.</summary>
    internal static string RunnerTarget(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        var runner = new PdfActionRunner(doc);

        var seen = new List<string>();
        runner.UriRequested += (_, e) => seen.Add(e.Uri);
        runner.NavigationRequested += (_, e) => seen.Add(e.Destination);
        runner.NamedActionRequested += (_, e) => seen.Add(e.Name);
        runner.FormActionRequested += (_, e) => seen.Add(e.Target);
        runner.LaunchRequested += (_, e) => seen.Add(e.Target);

        Assert.NotNull(runner.RunCatalogOpenAction());
        return Assert.Single(seen);
    }

    /// <summary>A one-page document whose catalog <c>/OpenAction</c> is <paramref name="action"/>.</summary>
    internal static byte[] OpenActionPdf(string action, params string[] extraObjects)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> >>",
            action,
        };
        objects.AddRange(extraObjects);
        return Build(objects.ToArray());
    }

    /// <summary>
    /// A classic-xref PDF of the given objects, numbered from 1. Each object is Latin-1, so a stream
    /// body carries arbitrary bytes by way of <see cref="Encoding.Latin1"/>.
    /// </summary>
    internal static byte[] Build(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    /// <summary>A stream object whose body is <paramref name="body"/>, byte for byte.</summary>
    internal static string Stream(byte[] body, string extraEntries = "")
        => "<< /Length " + body.Length + extraEntries + " >>\nstream\n"
           + Encoding.Latin1.GetString(body) + "\nendstream";
}
