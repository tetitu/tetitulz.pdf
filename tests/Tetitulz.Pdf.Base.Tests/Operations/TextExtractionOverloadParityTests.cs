// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The file-path text-extraction overloads do what their <see cref="Stream"/> siblings do.
///
/// <para><b>Why this exists.</b> <c>ExtractText(string)</c> and <c>ExtractText(string, int)</c>
/// opened the file directly, and so skipped three things the stream path performs: decryption, the
/// text-extraction plugin hook, and the reset of
/// <see cref="PdfOperations.LastExtractionError"/> — whose contract is that it is reset "at the
/// start of every call, on every path". This is the third place the <c>Stream</c>/<c>string</c>
/// seam has drifted here, after batch-render parallelism and the render hook.</para>
///
/// <para><b>These assert PARITY between the two overloads, not a fixed outcome, and that is
/// deliberate.</b> What an encrypted document does depends on whether a decryptor is registered —
/// which is process-wide state another test class may have set. An earlier version of this file
/// asserted the no-decryptor outcome directly; it passed alone and failed in the full run with
/// "Invalid password", because by then a neighbour had registered the real decryptor. Comparing the
/// two overloads is true under either registration, and is what the defect was actually about.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class TextExtractionOverloadParityTests
{
    /// <summary>
    /// Whole-document extraction: the file overload and the stream overload agree, both on what
    /// they return (or throw) and on the diagnostic they leave behind.
    /// </summary>
    [Fact]
    public void ExtractText_FromAFilePath_MatchesTheStreamOverload_OnAnEncryptedDocument()
    {
        byte[] pdf = OnePagePdf(encrypted: true);

        var viaStream = Capture(() => PdfOperations.ExtractText(new MemoryStream(pdf)));
        var viaFile   = WithTempFile(pdf, path => Capture(() => PdfOperations.ExtractText(path)));

        Assert.Equal(viaStream, viaFile);
    }

    /// <summary>Per-page extraction: the same parity, through the page-number overloads.</summary>
    [Fact]
    public void ExtractTextPerPage_FromAFilePath_MatchesTheStreamOverload_OnAnEncryptedDocument()
    {
        byte[] pdf = OnePagePdf(encrypted: true);

        var viaStream = Capture(() => PdfOperations.ExtractText(new MemoryStream(pdf), 1));
        var viaFile   = WithTempFile(pdf, path => Capture(() => PdfOperations.ExtractText(path, 1)));

        Assert.Equal(viaStream, viaFile);
    }

    /// <summary>
    /// A successful read through either overload leaves no diagnostic behind, and returns the same
    /// text — the control, so the two tests above cannot pass by both overloads having become
    /// uniformly broken.
    /// </summary>
    [Fact]
    public void ExtractText_OnAPlainDocument_AgreesAndReportsNoDiagnostic()
    {
        byte[] pdf = OnePagePdf(encrypted: false);

        var viaStream = Capture(() => PdfOperations.ExtractText(new MemoryStream(pdf)));
        var viaFile   = WithTempFile(pdf, path => Capture(() => PdfOperations.ExtractText(path)));

        Assert.Equal(viaStream, viaFile);
        Assert.Null(viaFile.Diagnostic);
        Assert.Contains("Hello", viaFile.Result);
    }

    /// <summary>The plugin hook fires for a file-path extraction, as it does for a stream one.</summary>
    [Fact]
    public void TextExtractedHook_FiresForTheFilePathOverloads()
    {
        var host   = PdfPluginHost.Instance;
        var plugin = new CountingTextPlugin();
        byte[] pdf = OnePagePdf(encrypted: false);

        try
        {
            host.Register(plugin);

            PdfOperations.ExtractText(new MemoryStream(pdf));
            int afterStream = plugin.Count;

            WithTempFile(pdf, path => { PdfOperations.ExtractText(path); return 0; });
            int afterFile = plugin.Count;

            Assert.True(afterStream > 0, "the stream overload should notify");
            Assert.True(afterFile > afterStream, "the file overload should notify too");
        }
        finally { host.Unregister(plugin.Name); }
    }

    // ------------------------------------------------------------------

    /// <summary>What an extraction call did: its result, or the exception type, plus the diagnostic.</summary>
    private readonly record struct Outcome(string Result, string ExceptionType, string Diagnostic);

    /// <summary>
    /// Runs <paramref name="call"/> and records its outcome, after first driving
    /// <see cref="PdfOperations.LastExtractionError"/> back to <c>null</c> with a read that is
    /// known to succeed.
    /// </summary>
    /// <remarks>
    /// <b>The reset is what makes the diagnostic comparison mean anything.</b> Without it, a call
    /// that fails to reset the slot simply inherits the previous call's value — so an overload with
    /// exactly the defect under test reports the same diagnostic as the correct one and the
    /// comparison passes; without the reset, a mutation leaving the slot unreset survives here.
    /// </remarks>
    private static Outcome Capture(Func<string> call)
    {
        PdfOperations.ExtractText(new MemoryStream(OnePagePdf(encrypted: false)));
        Assert.Null(PdfOperations.LastExtractionError);

        try
        {
            string text = call();
            return new Outcome(text, null, PdfOperations.LastExtractionError);
        }
        catch (Exception ex)
        {
            return new Outcome(null, ex.GetType().FullName, PdfOperations.LastExtractionError);
        }
    }

    private static T WithTempFile<T>(byte[] bytes, Func<string, T> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tetitulz-extract-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        try { return body(path); }
        finally { File.Delete(path); }
    }

    private sealed class CountingTextPlugin : IPdfPlugin, ITextExtractionHook
    {
        public string  Name        => "Test.TextExtractParity";
        public string  Description => "Counts text-extraction notifications";
        public Version Version     => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown()   { }

        private int _count;
        public int Count => System.Threading.Volatile.Read(ref _count);

        public void OnTextExtracted(int page, string text,
            System.Threading.CancellationToken ct = default)
            => System.Threading.Interlocked.Increment(ref _count);
    }

    private static byte[] OnePagePdf(bool encrypted)
    {
        const string content = "BT /F1 12 Tf 20 100 Td (Hello) Tj ET";

        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        if (encrypted)
            Obj("6 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -1 "
                + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
                + "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>\nendobj\n");

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        string enc = encrypted ? " /Encrypt 6 0 R /ID [<0102> <0102>]" : "";
        Emit($"trailer\n<< /Size {size} /Root 1 0 R{enc} >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
