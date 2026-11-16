# Tetitulz PDF Compare

A developer's tool: a folder of PDFs, each shown in the library's own viewer beside Adobe Acrobat
Reader's, page for page, to look for rendering differences. Windows, .NET 10.

```powershell
dotnet run --project apps/Tetitulz.Pdf.CompareViewer -- path\to\folder
```

- **Open Folder** lists every PDF under the folder (subfolders included); ◀ ▶ move between files,
  the page buttons (or ← →, Page Up/Down) between pages. Acrobat follows the left pane's page.
- The left pane is the full viewer: forms can be filled, comments and signatures are shown, and its
  own toolbar finds, saves and prints. Leaving a file with unsaved form values asks first.
- The right pane needs **64-bit Adobe Acrobat Reader** installed (its `AcroPDF.PDF` control); without
  it the pane says so and the left pane still works.

Logs: `%LOCALAPPDATA%\TetitulzCompare\logs`.
