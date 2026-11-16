// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <c>CleanContentStreams</c> must reach a page whose <c>/Contents</c> is an <b>indirect reference
/// to an array</b>.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>CollectContentStreamObjectNumbers</c> branched on the
/// <i>unresolved</i> value, so <c>/Contents 5 0 R</c> where object 5 is an array collected object
/// <b>5</b> — the array — as though it were a stream. The caller then drops it at
/// <c>!(obj is PdfStream)</c>, so the page's real content streams were never visited and the pass
/// did nothing at all. It is the quietest member of this defect family: no exception, no warning,
/// and an <c>OptimizationResult</c> reporting a successful run over a document it never touched —
/// the class CLAUDE.md calls "omissions the caller asked for by name".</para>
///
/// <para><b>Read the decoded page, not the file bytes.</b> Cleaning a stream re-encodes it with
/// <c>FlateDecode</c> — <c>CompressStreams = false</c> governs whether streams are compressed for
/// size, not whether a rewritten one is re-encoded. A substring search over the output therefore
/// cannot find the operators of the very stream that was cleaned, and fails identically for the
/// working control. That is what the first version of this test did, and both cases failing
/// together is what gave it away.</para>
///
/// <para>The direct-array case is the control: it always worked, so a fixture built on the common
/// spelling could never have gone red.</para>
/// </remarks>
public class IndirectContentsArrayOptimizerTests
{
    [Theory]
    [InlineData(true)]   // /Contents 5 0 R  ->  [3 0 R 4 0 R]   — the shape that was skipped
    [InlineData(false)]  // /Contents [3 0 R 4 0 R]              — the control that always worked
    public void CleanContentStreams_ReachesThePage_WhicheverWayContentsIsSpelled(bool indirect)
    {
        using var input = new MemoryStream(TwoPartPagePdf(indirect));
        using var output = new MemoryStream();

        PdfOptimizer.Optimize(input, output, new OptimizationOptions
        {
            CleanContentStreams = true,
            CompressStreams = false,
        });

        string content = DecodedPageContent(output.ToArray());

        // Both parts survived the pass...
        Assert.Contains("33 44 re", content);
        Assert.Contains("77 88 re", content);

        // ...and the empty save/restore pair CleanContentStreams exists to remove is gone. This is
        // the discriminating assertion: under the defect the indirect page was never visited, so
        // its q/Q pair came through untouched while the control's was removed.
        Assert.DoesNotContain("q\nQ", content);
    }

    // ------------------------------------------------------------------

    /// <summary>The page's content as the reader assembles it, with each part decoded.</summary>
    private static string DecodedPageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        byte[] data = doc.Pages[0].GetContentStreamData();
        Assert.NotNull(data);
        return Encoding.Latin1.GetString(data);
    }

    /// <summary>
    /// A page with two content parts, the first carrying an empty <c>q…Q</c> pair. When
    /// <paramref name="indirect"/> the array is its own object and the page points at it.
    /// </summary>
    private static byte[] TwoPartPagePdf(bool indirect) => Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [6 0 R] /Count 1 >>",
        StreamObj("q\nQ\n11 22 33 44 re\nS\n"),
        StreamObj("55 66 77 88 re\nS\n"),
        indirect ? "[3 0 R 4 0 R]" : "null",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents " +
            (indirect ? "5 0 R" : "[3 0 R 4 0 R]") + " >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
