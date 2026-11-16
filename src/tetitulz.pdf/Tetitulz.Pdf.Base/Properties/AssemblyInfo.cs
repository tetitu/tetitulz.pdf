// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tetitulz.Pdf.Base.Tests")]
[assembly: InternalsVisibleTo("FontDiag")]

// The performance harness. LayoutAnalyzerBenchmarks measures PdfLayoutAnalyzer.DetectColumns, whose
// input is PdfPageTextLayout.Chars — internal, because per-character data is an extractor detail
// rather than public API. Building the fixture through the parser instead would bury a ~1 ms grid
// pass under tens of milliseconds of parsing and extraction, which is the "fixture never reaches
// the feature" hazard benchmarks/…/README.md exists to prevent.
[assembly: InternalsVisibleTo("Tetitulz.Pdf.Benchmarks")]
