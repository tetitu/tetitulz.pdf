// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

// The plugin contracts moved here from Tetitulz.Pdf.Plugins.SDK, whose csproj set
// <Nullable>annotations</Nullable>. Core does not enable nullable globally (that would mark
// its ~1000 existing public members non-nullable in metadata, which is not true), so the
// setting is preserved per-file instead.
#nullable enable annotations

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>Result returned by <see cref="IRepairPlugin.Repair"/>.</summary>
    public sealed class PluginRepairResult
    {
        /// <summary><c>true</c> if the PDF was successfully repaired and written to the output stream.</summary>
        public bool Succeeded { get; }

        /// <summary>Number of indirect objects recovered during repair.</summary>
        public int RecoveredObjectCount { get; }

        /// <summary>Non-fatal errors encountered during repair (skipped objects, parse failures, etc.).</summary>
        public IReadOnlyList<string> Errors { get; }

        public PluginRepairResult(bool succeeded, int recoveredObjectCount, IReadOnlyList<string> errors)
        {
            Succeeded            = succeeded;
            RecoveredObjectCount = recoveredObjectCount;
            Errors               = errors ?? new string[0];
        }

        public override string ToString()
            => Succeeded
                ? $"Repaired: {RecoveredObjectCount} objects recovered, {Errors.Count} warning(s)"
                : "Repair failed: PDF is unrecoverable";
    }
}
