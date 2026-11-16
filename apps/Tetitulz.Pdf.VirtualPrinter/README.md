# Tetitulz PDF — a virtual printer

A printer every Windows program can print to, each job saved as a PDF by this library's PostScript
plugin. No third-party driver and no native code: the printer is Windows' own **Microsoft PS Class
Driver** on a local port whose name is a **named pipe**, and this app serves the pipe.

```
program ──print──▶ spooler ──PostScript──▶ \\.\pipe\TetitulzPdfPrinter ──▶ TetitulzPdfPrinter listen ──▶ PDF
```

**Want a save dialog for each print — PDF/A, a password, a signature?** That is the tray app,
[`Tetitulz.Pdf.PrinterTray`](../Tetitulz.Pdf.PrinterTray/README.md), which shares this app's listener
and converter. Use one or the other: one listener serves the printer at a time.

## Use

```powershell
# Build once into a folder of its own, so the listener does not run from a checkout:
dotnet publish apps/Tetitulz.Pdf.VirtualPrinter -c Release -o "$env:LOCALAPPDATA\Programs\TetitulzPdfPrinter"
cd "$env:LOCALAPPDATA\Programs\TetitulzPdfPrinter"

.\TetitulzPdfPrinter install --autostart   # adds the printer; asks for elevation once
.\TetitulzPdfPrinter listen                # or log off and on, with --autostart
```

`autostart` (and `autostart --off`) sets the logon start alone, without elevation — after moving the
program, run it from the new folder.

Then print to **Tetitulz PDF** from any program. Each job becomes a PDF in `Documents\Tetitulz PDF`,
named from the document's title and the time — `Invoice 42 2026-10-31 102445.pdf` — and numbered
when that name is taken, so no job overwrites another. The PDF is written as `….pdf.partial` and
renamed when complete, so a program watching the folder never opens half a file.

| Command | |
|---|---|
| `install [--name N] [--autostart]` | Adds the driver (from Windows' driver store), the pipe port and the printer, through the consent prompt. `--autostart` starts the listener, windowless, at every logon. |
| `listen [--out DIR] [--open] [--keep-ps]` | Receives jobs. `--open` opens each PDF; `--keep-ps` keeps the job's PostScript beside it. |
| `convert JOB.ps [OUT.pdf]` | Converts one PostScript file the same way — for a job kept with `--keep-ps`, or any `.ps`. |
| `uninstall [--name N]` | Removes the printer, its port and the autostart entry. The driver stays: it is Windows' own. |
| `autostart [--off]` | Starts this program's listener at every logon, windowless — or stops doing so. No elevation. |

Logs are in `%LOCALAPPDATA%\TetitulzPdfPrinter\logs`: each job's page count, and when it is not
clean, what the conversion reported. A job that draws nothing, or whose conversion fails, saves no
PDF and keeps its PostScript in `%LOCALAPPDATA%\TetitulzPdfPrinter\failed` for fourteen days — it
is the document that was printed, in full, so it is not kept for good. The tests are in
`tests/Tetitulz.Pdf.VirtualPrinter.Tests` (and the tray app's in `tests/Tetitulz.Pdf.PrinterTray.Tests`).

## What to know

- **The listener must be running when you print.** Without it the spooler cannot open the port; the
  job waits in the queue with an error and prints once the listener starts.
- **One listener at a time.** The pipe's name is machine-wide; a second listener refuses to start.
  So does a listener that finds the pipe already there: it makes the pipe's first instance or
  none, so no other process that made the name first receives prints beside it.
- **Who may connect.** The pipe admits the spooler (LocalSystem) and the user running the listener —
  no other account can write a job into your folder or read one.
- **Whose prints are kept.** The printer is the machine's, so any account can print to it and the
  spooler writes every job into the one pipe. A job the spooler says another account submitted is
  dropped — not saved, its document not named in the log. One whose submitter the spooler did not
  say in time is kept, so on a machine several people use at once, install the printer for one user.
- **The printer's name is data.** `install --name` reaches an elevated PowerShell as a quoted
  literal inside an encoded command, whatever characters it holds.
- **Jobs up to 1 GB** are converted; a larger one is dropped and logged.
- **Text stays text.** The driver sends each TrueType font as its glyphs alone, with nothing that
  says which character a glyph is; the converter finds the installed font each one came from (the
  same `head`, the same glyph bytes) and reads its `cmap`, so the PDF's text can be searched and
  copied — Hebrew and Arabic included, Arabic's shaped forms read as their letters. A glyph no
  installed font matches is drawn, and its text is said to be unknown in the log. Pointed Hebrew
  (niqqud) extracts with each point on its letter, as Word's own PDF export does.
- **Landscape pages come out landscape**: the driver draws them turned on portrait paper, and the
  converter turns each page back by its text (`AutoRotatePages` `PageByPage`).
- **Sized for documents.** The converter's ceilings are set for print jobs, not for the untrusted
  PostScript a PDF can embed: about a minute of conversion (a four-page Word document takes 1.2 s).
- **What converts** is the PostScript plugin's: [its README](../../src/plugins/Tetitulz.Pdf.Plugins.PostScript/README.md)
  has the table.
