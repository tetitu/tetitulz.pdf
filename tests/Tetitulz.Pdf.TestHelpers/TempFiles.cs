// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Tetitulz.Pdf.TestHelpers;

public static class TempFiles
{
    public static string NewPath(string extension = ".pdf")
    {
        if (string.IsNullOrEmpty(extension))
            extension = string.Empty;
        else if (extension[0] != '.')
            extension = "." + extension;
        return Path.Combine(Path.GetTempPath(),
            "tetitulz_" + Guid.NewGuid().ToString("N") + extension);
    }
}
