// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// The engine answers the probes as Acrobat does. Each <c>Fixtures/Acrobat/AcrobatProbe*.pdf</c> is a
/// probe document Acrobat DC 26.2 ran and saved; it is opened here as a viewer opens it
/// (<see cref="PdfOperations.ExecuteOpenScripts(string, PdfJsExecutionContext)"/>, its attachments
/// handed over), and every answer the probe writes is compared with the one Acrobat wrote.
/// </summary>
/// <remarks>
/// The answers this engine gives otherwise are listed, each with its reason, in the round's
/// <c>known-disagreements*.tsv</c> — a <c>*</c> matches any run of characters, and a third column
/// limits a row to the cases where Acrobat answered so. The list must match exactly: a row no
/// difference matches fails, so the list only shrinks.
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class AcrobatParityTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Acrobat");
    private readonly JintJavaScriptPlugin _plugin = new();

    public AcrobatParityTests() => PdfPluginHost.Instance.Register(_plugin);

    public void Dispose() => PdfPluginHost.Instance.Unregister(_plugin.Name);

    [Theory]
    [InlineData("AcrobatProbe.pdf", "answers-26.2.tsv", "known-disagreements.tsv", 500)]
    [InlineData("AcrobatProbe2.pdf", "answers2-26.2.tsv", "known-disagreements2.tsv", 350)]
    public void TheEngineAnswersTheProbeAsAcrobatDoes_ExceptTheListedDisagreements(
        string probe, string answers, string disagreements, int atLeast)
    {
        Dictionary<string, string> acrobat = ReadAnswers(File.ReadAllLines(Path.Combine(Fixtures, answers)));
        Dictionary<string, string> ours = ReadAnswers(RunProbe(probe).Split('\n'));
        var known = new List<(Regex Id, Regex? Answer, string Line)>();
        foreach (string line in File.ReadAllLines(Path.Combine(Fixtures, disagreements)))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] cells = line.Split('\t');
            known.Add((Glob(cells[0]), cells.Length > 2 ? Glob(cells[2]) : null, line));
        }

        // Potency: the probe ran to its end here, and Acrobat's file is the full answer set.
        Assert.True(acrobat.Count >= atLeast, $"{acrobat.Count} Acrobat answers");
        Assert.True(ours.Count >= acrobat.Count, $"the probe wrote {ours.Count} answers here, Acrobat {acrobat.Count}");

        var unexpected = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in acrobat)
        {
            ours.TryGetValue(kv.Key, out string? mine);
            if (mine == kv.Value) continue;
            if (kv.Key == "I.creationDate" && mine == InThisMachinesZone(kv.Value)) continue;
            var row = known.FirstOrDefault(k => k.Id.IsMatch(kv.Key) && (k.Answer == null || k.Answer.IsMatch(kv.Value)));
            if (row.Line != null) used.Add(row.Line);
            else unexpected.Add($"{kv.Key}\n    Acrobat: {kv.Value}\n    engine:  {mine ?? "(none)"}");
        }
        var nowAgree = known.Where(k => !used.Contains(k.Line)).Select(k => k.Line).ToList();
        Assert.True(unexpected.Count == 0, "answers that differ from Acrobat's:\n" + string.Join("\n", unexpected));
        Assert.True(nowAgree.Count == 0, "listed disagreements no answer differs by; remove their rows:\n" + string.Join("\n", nowAgree));
    }

    /// <summary>Opens a probe document as a viewer does, and returns what it wrote into <c>results</c>.</summary>
    private static string RunProbe(string probe)
    {
        var ctx = new PdfJsExecutionContext { ExposeAttachments = true };
        PdfOperations.ExecuteOpenScripts(Path.Combine(Fixtures, probe), ctx);
        Assert.True(ctx.Error == null, "the probe stopped: " + ctx.Error);
        Assert.True(ctx.ModifiedFieldValues.TryGetValue("results", out string? results), "the probe wrote no results");
        return results!;
    }

    /// <summary>
    /// Acrobat's answer for a date's local fields, as this machine's zone gives them. The probe
    /// prints a date's year, month, day, hour and minute in local time, and Acrobat ran it in
    /// Israel; a host in another zone reads the same instant at another hour.
    /// </summary>
    private static string InThisMachinesZone(string acrobatAnswer)
    {
        Match m = Regex.Match(acrobatAnswer, @"^string:Date:(\d+)-(\d+)-(\d+) (\d+):(\d+)$",
                              RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!m.Success) return acrobatAnswer;
        int Part(int i) => int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture);

        TimeZoneInfo israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        var there = new DateTime(Part(1), Part(2), Part(3), Part(4), Part(5), 0, DateTimeKind.Unspecified);
        DateTime here = TimeZoneInfo.ConvertTime(there, israel, TimeZoneInfo.Local);
        return FormattableString.Invariant($"string:Date:{here.Year}-{here.Month}-{here.Day} {here.Hour}:{here.Minute}");
    }

    private static Regex Glob(string pattern) =>
        new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>
    /// One answer per row: a case id a probe writes more than once (a loop over a field's widgets)
    /// gets <c>#2</c>, <c>#3</c>… on its repeats, on both sides alike — both files are in probe
    /// order, so the pairing is exact and every row is compared, not the last of each id.
    /// </summary>
    private static Dictionary<string, string> ReadAnswers(IEnumerable<string> lines)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string l = line.TrimEnd('\r');
            int tab = l.IndexOf('\t');
            if (tab <= 0) continue;
            string id = l.Substring(0, tab);
            seen.TryGetValue(id, out int times);
            seen[id] = times + 1;
            answers[times == 0 ? id : id + "#" + (times + 1)] = l.Substring(tab + 1);
        }
        return answers;
    }
}
