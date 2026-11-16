// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Diagnostics;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Runs every developer-local <c>.ps</c> / <c>.eps</c> sample through the converter.
///
/// <para>The synthetic tests elsewhere in this project prove individual operators; this proves the
/// converter survives files nobody wrote for it — real Illustrator and the reference renderer output, with the
/// prologues, binary sections and Level 2 idioms that come with them. Real samples are not
/// committed (licensing), so these <b>skip</b> when the corpus is absent rather than silently
/// passing.</para>
///
/// <para>The assertions are deliberately about <i>robustness</i>, not fidelity: no exception, a
/// readable PDF, and a bounded running time. What a given EPS should look like is not something a
/// test can assert without a reference renderer, but "it did not hang, throw, or emit a file that
/// cannot be opened" is exactly what a shakedown is for — and it is the property most likely to
/// break when the interpreter changes.</para>
/// </summary>
public sealed class EpsCorpusShakedownTests
{
    /// <summary>
    /// Every sample converts to an openable PDF without throwing.
    ///
    /// <para>Failures are collected and reported together: with a corpus of any size, failing on the
    /// first bad file hides how many others are affected, which is the number that tells you whether
    /// a change broke one edge case or everything.</para>
    /// </summary>
    [EpsCorpusFact]
    public void EveryCorpusSample_ConvertsToAnOpenablePdf()
    {
        var failures = new List<string>();

        foreach (string path in EpsCorpus.Files())
        {
            try
            {
                using var source = File.OpenRead(path);
                using var output = new MemoryStream();

                var result = PostScriptConversion.ConvertDocument(source, output);

                if (result.PageCount < 1)
                {
                    failures.Add($"{Path.GetFileName(path)}: produced {result.PageCount} pages");
                    continue;
                }

                using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
                if (doc == null || doc.Pages.Count < 1)
                    failures.Add($"{Path.GetFileName(path)}: the written PDF would not open");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// No sample takes an unreasonable time. The operation budget is what guarantees this, so a
    /// regression here means the budget stopped being ticked somewhere — a class of bug that is
    /// otherwise invisible until a real file hangs a server.
    /// </summary>
    [EpsCorpusFact]
    public void NoCorpusSample_TakesLongerThanTheBudgetAllows()
    {
        var slow = new List<string>();

        foreach (string path in EpsCorpus.Files())
        {
            using var source = File.OpenRead(path);
            using var output = new MemoryStream();

            var sw = Stopwatch.StartNew();
            try { PostScriptConversion.ConvertDocument(source, output); }
            catch { /* the conversion test above owns correctness; this one owns time */ }
            sw.Stop();

            // Generous: the default 20 M operation budget is worth a few seconds on slow hardware,
            // and this is a ceiling for "something stopped terminating", not a benchmark.
            if (sw.Elapsed > TimeSpan.FromSeconds(60))
                slow.Add($"{Path.GetFileName(path)}: {sw.Elapsed.TotalSeconds:F1}s");
        }

        Assert.Empty(slow);
    }

    /// <summary>
    /// Reports what the corpus exercised, so a developer with samples can see which operators the
    /// converter is missing on real files rather than inferring it from output.
    ///
    /// <para>Asserts the run happened at all — the diagnostic surface has to be populated for the
    /// summary to mean anything — and prints the rest.</para>
    /// </summary>
    [EpsCorpusFact]
    public void CorpusRun_ReportsWhichOperatorsAreMissing()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int converted = 0;

        foreach (string path in EpsCorpus.Files())
        {
            using var source = File.OpenRead(path);
            using var output = new MemoryStream();

            var result = PostScriptConversion.ConvertDocument(source, output);
            converted++;

            foreach (string op in result.UnimplementedOperators) missing.Add(op);
        }

        Assert.True(converted > 0, "the corpus reported files but none were converted.");

        // Not a failure — an unimplemented operator is a documented limit, not a defect. The
        // message is the deliverable.
        if (missing.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{converted} sample(s) used {missing.Count} unimplemented operator(s):");
            foreach (string op in missing) sb.AppendLine("  " + op);
            Console.WriteLine(sb.ToString());
        }
    }
}
