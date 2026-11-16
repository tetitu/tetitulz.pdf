// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// RenderCli — renders PDF pages to PNG / SVG using Tetitulz.Pdf.Base
// Usage:
//   RenderCli <pdf-path> <out-dir> [--dpi <n>] [--method png|svg|tile] [--pages <spec>]
//
//   --pages spec formats:
//     --pages 3         single page (1-based)
//     --pages 2-5       inclusive range (1-based)
//     --pages 1,3,7     comma-separated list (1-based)
//
//   Writes: out-dir/page_<NNN>_<method>_<dpi>dpi.png  (or .svg)
//   Exits 0 on success, 1 on error.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Constants;

string pdfPath  = "";
string outDir   = "";
int    dpi      = 150;
string method   = "png";
string pagesArg = "";   // raw --pages value; empty = all pages

// Parse args
var argv = Environment.GetCommandLineArgs();  // [0] = exe
for (int i = 1; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--dpi":    dpi      = int.Parse(argv[++i]); break;
        case "--method": method   = argv[++i]; break;
        case "--pages":  pagesArg = argv[++i]; break;
        default:
            if (string.IsNullOrEmpty(pdfPath)) pdfPath = argv[i];
            else                               outDir  = argv[i];
            break;
    }
}

if (string.IsNullOrEmpty(pdfPath) || string.IsNullOrEmpty(outDir))
{
    Console.Error.WriteLine(
        "Usage: RenderCli <pdf> <out-dir> [--dpi N] [--method png|svg|tile] [--pages <spec>]");
    Console.Error.WriteLine("  --pages spec: \"3\"  \"2-5\"  \"1,3,7\"  (1-based page numbers)");
    return 1;
}

if (!File.Exists(pdfPath)) { Console.Error.WriteLine($"File not found: {pdfPath}"); return 1; }
Directory.CreateDirectory(outDir);

int pageCount = PdfOperations.GetPageCount(pdfPath);

// Resolve the set of 0-based page indices to render.
IReadOnlyList<int> pageIndices = ResolvePageIndices(pagesArg, pageCount);
if (pageIndices == null) return 1;   // parse error already printed

Console.WriteLine($"pages={pageCount} rendering={pageIndices.Count} method={method} dpi={dpi}");

var errors = new List<string>();

foreach (int p in pageIndices)
{
    string stem = $"page_{p + 1:D3}_{method}_{dpi}dpi";
    try
    {
        switch (method)
        {
            case "png":
            {
                byte[] data = PdfOperations.RenderPageToPng(pdfPath, p, dpi);
                File.WriteAllBytes(Path.Combine(outDir, stem + ".png"), data);
                Console.WriteLine($"  ok page {p + 1} -> {stem}.png  ({data.Length} bytes)");
                break;
            }
            case "svg":
            {
                string svg = PdfOperations.RenderPageToSvg(pdfPath, p) ?? "";
                File.WriteAllText(Path.Combine(outDir, stem + ".svg"), svg);
                Console.WriteLine($"  ok page {p + 1} -> {stem}.svg  ({svg.Length} chars)");
                break;
            }
            case "tile":
            {
                // Render 4 quarter-page tiles at 2× DPI.
                int tileDpi = dpi * 2;
                byte[] full = PdfOperations.RenderPageToPng(pdfPath, p, tileDpi);
                using var ms  = new MemoryStream(full);
                using var bmp = System.Drawing.Image.FromStream(ms);
                int w = bmp.Width, h = bmp.Height;
                float ptW = w * PdfUnits.PointsPerInch / tileDpi;
                float ptH = h * PdfUnits.PointsPerInch / tileDpi;

                int[][] tiles = {
                    [0, 0], [1, 0],
                    [0, 1], [1, 1]
                };
                foreach (var t in tiles)
                {
                    float clipX = t[0] * ptW / 2;
                    float clipY = t[1] * ptH / 2;
                    float clipW = ptW / 2;
                    float clipH = ptH / 2;
                    byte[] tile = PdfOperations.RenderPageTile(pdfPath, p, clipX, clipY, clipW, clipH, tileDpi);
                    string name = $"page_{p + 1:D3}_tile_{t[0]}{t[1]}_{tileDpi}dpi.png";
                    File.WriteAllBytes(Path.Combine(outDir, name), tile);
                    Console.WriteLine($"  ok page {p + 1} tile({t[0]},{t[1]}) -> {name}");
                }
                break;
            }
            default:
                Console.Error.WriteLine($"Unknown method: {method}");
                return 1;
        }
    }
    catch (Exception ex)
    {
        string msg = $"  ERROR page {p + 1}: {ex.Message}";
        Console.Error.WriteLine(msg);
        errors.Add(msg);
    }
}

if (errors.Count > 0)
{
    Console.Error.WriteLine($"\n{errors.Count} page(s) failed.");
    return 1;
}

Console.WriteLine("done");
return 0;

// -----------------------------------------------------------------------
//  Helpers
// -----------------------------------------------------------------------

/// <summary>
/// Parses a user-supplied page spec (1-based) and returns 0-based indices.
/// Supported formats: ""  "3"  "2-5"  "1,3,7"
/// Returns null on parse error (error message already written to stderr).
/// </summary>
static IReadOnlyList<int>? ResolvePageIndices(string spec, int pageCount)
{
    if (string.IsNullOrWhiteSpace(spec))
    {
        // All pages.
        return Enumerable.Range(0, pageCount).ToList();
    }

    var result = new SortedSet<int>();

    foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        string trimmed = part.Trim();
        int dash = trimmed.IndexOf('-');

        if (dash <= 0)
        {
            // Single page.
            if (!int.TryParse(trimmed, out int pg) || pg < 1 || pg > pageCount)
            {
                Console.Error.WriteLine($"--pages: invalid page number '{trimmed}' (valid: 1–{pageCount})");
                return null;
            }
            result.Add(pg - 1);
        }
        else
        {
            // Range "N-M".
            string fromStr = trimmed[..dash].Trim();
            string toStr   = trimmed[(dash + 1)..].Trim();
            if (!int.TryParse(fromStr, out int from) || !int.TryParse(toStr, out int to)
                || from < 1 || to > pageCount || from > to)
            {
                Console.Error.WriteLine(
                    $"--pages: invalid range '{trimmed}' (valid: 1–{pageCount}, from ≤ to)");
                return null;
            }
            for (int i = from; i <= to; i++)
                result.Add(i - 1);
        }
    }

    if (result.Count == 0)
    {
        Console.Error.WriteLine("--pages: no pages selected");
        return null;
    }

    return result.ToList();
}
