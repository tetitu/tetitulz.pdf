// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Maintenance
{
    /// <summary>
    /// <see cref="IRepairPlugin"/> that rebuilds the cross-reference table of a corrupt PDF
    /// using the built-in <see cref="PdfRepair"/>.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new RepairPlugin());
    /// </code>
    /// </summary>
    public sealed class RepairPlugin : IRepairPlugin
    {
        public string Name        => "Tetitulz.Plugins.Repair";
        public string Description => "Repairs malformed PDFs by rebuilding the cross-reference table.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        public PluginRepairResult Repair(Stream input, Stream output)
        {
            if (input == null)  throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            var result = PdfRepair.Repair(input, output);
            if (result == null)
                return new PluginRepairResult(false, 0, new[] { "PdfRepair returned no result." });
            // Repair succeeded when at least one object was recovered.
            // IsClean indicates no errors occurred; either way the output PDF is usable
            // as long as RecoveredObjectCount > 0.
            return new PluginRepairResult(result.RecoveredObjectCount > 0,
                result.RecoveredObjectCount, result.Errors);
        }
    }
}
