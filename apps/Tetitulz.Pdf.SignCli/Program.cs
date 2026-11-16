// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.SignCli
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
#if NETFRAMEWORK
            const string tool = "tetitulz-sign";
#else
            const string tool = "tetitulz-sign10";
#endif
            return new SignCommand(tool, Console.Out, Console.Error, Console.In).Run(args);
        }
    }
}
