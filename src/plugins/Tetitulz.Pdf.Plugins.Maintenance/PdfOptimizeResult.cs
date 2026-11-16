// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>Metrics returned by <see cref="PdfOptimizerWithMetrics"/>.</summary>
    public sealed class PdfOptimizeResult
    {
        public long BeforeBytes { get; }
        public long AfterBytes { get; }
        public long BytesSaved => Math.Max(0, BeforeBytes - AfterBytes);
        public float ReductionPct =>
            BeforeBytes == 0 ? 0f : (float)(BeforeBytes - AfterBytes) / BeforeBytes * 100f;

        public PdfOptimizeResult(long before, long after)
        {
            BeforeBytes = before;
            AfterBytes = after;
        }
    }

    /// <summary>Wraps <see cref="PdfOptimizer"/> with before/after byte metrics.</summary>
    public static class PdfOptimizerWithMetrics
    {
        public static PdfOptimizeResult OptimizeWithMetrics(Stream input, Stream output, OptimizationOptions options = null)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            using var bufIn = new MemoryStream();
            input.CopyTo(bufIn);
            long before = bufIn.Length;

            using var bufOut = new MemoryStream();
            bufIn.Position = 0;
            PdfOptimizer.Optimize(bufIn, bufOut, options);
            long after = bufOut.Length;

            bufOut.Position = 0;
            bufOut.CopyTo(output);
            return new PdfOptimizeResult(before, after);
        }
    }
}
