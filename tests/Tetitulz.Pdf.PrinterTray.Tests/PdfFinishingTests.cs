// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;

namespace Tetitulz.Pdf.PrinterTray.Tests;

/// <summary>
/// The converted print made into what the save dialog asked for: PDF/A-2b, then a password, then a
/// signature; the destination written only when every step succeeded, and every step's file and the
/// converted print removed either way.
/// </summary>
public sealed class PdfFinishingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tray-tests-" + Guid.NewGuid().ToString("N"));
    private string Work => Path.Combine(_root, "work");
    private string Destination => Path.Combine(_root, "saved", "Report.pdf");

    public PdfFinishingTests()
    {
        Directory.CreateDirectory(Work);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SaveRequest Request(bool pdfA = false, string? password = null, string? thumbprint = null)
        => new(Destination, "Report", OpenAfterSaving: false, pdfA, password, thumbprint, Reason: null);

    /// <summary>Nothing asked for: the print is saved as it came, and the converted file is gone.</summary>
    [Fact]
    public void NothingAskedFor_SavesThePrintAsItCame()
    {
        string converted = Converted();
        byte[] bytes = File.ReadAllBytes(converted);

        Assert.Empty(PdfFinishing.Finish(converted, Request()));
        Assert.Equal(bytes, File.ReadAllBytes(Destination));
        Assert.Empty(Directory.GetFiles(Work));
    }

    /// <summary>PDF/A: the saved file says PDF/A-2b in its metadata, and no note says it could not be made so.</summary>
    [Fact]
    public void PdfA_SavesAPdfA2b()
    {
        Assert.Empty(PdfFinishing.Finish(Converted(), Request(pdfA: true)));

        using var doc = PdfDocument.Open(Destination);
        string xmp = Encoding.UTF8.GetString(doc.Catalog.GetMetadataStream().GetDecodedData());
        Assert.Matches(new Regex(@"pdfaid:part(=""|>)2\b"), xmp);
        Assert.Matches(new Regex(@"pdfaid:conformance(=""|>)B\b"), xmp);
        Assert.Empty(Directory.GetFiles(Work));
    }

    /// <summary>A password: the saved file opens with it and refuses another.</summary>
    [Fact]
    public void APassword_LocksTheSavedFile()
    {
        PdfFinishing.Finish(Converted(), Request(password: "s3cret"));

        string opened = Path.Combine(_root, "opened.pdf");
        PdfEncryptionOperations.RemovePassword(Destination, opened, "s3cret");
        using (var doc = PdfDocument.Open(opened)) Assert.Single(doc.Pages);

        var refused = Assert.Throws<PdfException>(
            () => PdfEncryptionOperations.RemovePassword(Destination, Path.Combine(_root, "refused.pdf"), "wrong"));
        Assert.Contains("Invalid password", refused.Message);
        Assert.Empty(Directory.GetFiles(Work));
    }

    /// <summary>
    /// A certificate no longer in the store stops the save: nothing is written to the destination, and
    /// the steps already made (PDF/A here) and the converted print are removed.
    /// </summary>
    [Fact]
    public void AMissingCertificate_SavesNothing_AndLeavesNoStep()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => PdfFinishing.Finish(Converted(), Request(pdfA: true, thumbprint: new string('0', 40))));

        Assert.Contains("no longer in your certificate store", refused.Message);
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Work));
    }

    /// <summary>A one-page PDF of a filled rectangle — no font, so nothing stands between it and PDF/A.</summary>
    private string Converted()
    {
        const string content = "0 0 1 rg 100 100 200 200 re f";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        string path = Path.Combine(Work, "job.pdf");
        File.WriteAllText(path, sb.ToString(), Encoding.Latin1);
        return path;
    }
}
