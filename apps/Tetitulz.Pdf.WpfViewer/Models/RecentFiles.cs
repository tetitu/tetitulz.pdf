// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text.Json;

namespace Tetitulz.Pdf.WpfViewer.Models;

/// <summary>Persists recent file paths to a JSON file in AppData.</summary>
public sealed class RecentFiles
{
    private const int MaxItems = 10;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TetituPdfViewer", "recent.json");

    public List<string> Items { get; private set; } = new();

    public void Add(string path)
    {
        Items.Remove(path);
        Items.Insert(0, path);
        if (Items.Count > MaxItems) Items.RemoveAt(Items.Count - 1);
        Save();
    }

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { Items = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items));
        }
        catch { }
    }
}
