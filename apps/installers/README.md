# Installers

MSIs for the apps people install rather than run from a build: **Tetitulz PDF Viewer**
(`apps/Tetitulz.Pdf.WpfViewer`, a 32-bit and a 64-bit MSI), **Tetitulz Stamp and Sign**
(`apps/Tetitulz.Pdf.StampSign`) and **Tetitulz PDF Printer**, the tray app
(`apps/Tetitulz.Pdf.PrinterTray`). The viewer has its own template, `Viewer.wxs`; the other two share
`App.wxs`. `build.ps1` fills them in.

```powershell
apps\installers\build.ps1                       # all of them, version yy.mm.dd from today
apps\installers\build.ps1 -App StampSign -Version 1.0.0
apps\installers\build.ps1 -App Viewer -Arch x86 # the viewer's 32-bit MSI only (default: both)
```

## Tetitulz PDF Viewer

- **Two MSIs, one product.** `Viewer-x64_<version>.msi` and `Viewer-x86_<version>.msi`, each the viewer
  published self-contained for its architecture (no .NET on the machine needed). They share an
  upgrade code, so either replaces the other: install the 32-bit one on 32-bit Windows, and either on
  64-bit Windows. Into Program Files (Program Files (x86) for the 32-bit one), per machine.
- **The Custom Setup page** offers, all on by default:
  - **Open PDF files with it** — a PDF file type that opens in the viewer, the viewer in Open with and
    in Default apps, and File Explorer's preview through the viewer while it is the PDF app.
  - **Make it the default PDF app** — on each user's first start the viewer asks once, unless it
    already is, and on yes opens Windows' Default apps at its entry. No program can make itself the
    default: Windows keeps that choice for the user. The ribbon's Home → Default app does the same at
    any time.
  - **Preview PDF files in File Explorer** — the viewer becomes `.pdf`'s preview handler whichever app
    opens PDFs, in place of the one `.pdf` had (usually Edge's). Uninstalling removes it, and Explorer
    shows no PDF preview until another program registers one again (an Edge update does).
- **The preview handler is the viewer itself**, an out-of-process COM server: COM starts
  `TetitulzPdfViewer.exe -Embedding`, which serves previews and exits after two idle minutes. So the
  32-bit build previews for 64-bit Explorer too, and no runtime is loaded into Explorer's preview host.
  It runs at the user's integrity level, like the viewer opening the same file. A preview is
  read-only: no links followed, no printing, no form filling.
- **On a VM, beyond the checks below:** Explorer's preview pane in light and dark mode; Open with and
  Default apps listing the viewer; the first-start offer; switching from the 64-bit to the 32-bit MSI
  and back; and, for the 32-bit MSI on 64-bit Windows, that Default apps lists the viewer — its
  `RegisteredApplications` entry is written to the 32-bit registry view there.

## hilaPdf

hilaPdf has its own repository, with its own installer (`installer/build.ps1`),
built on the Tetitulz.Pdf packages; its printer's machinery is the `Tetitulz.Pdf.Printing` package.

## Stamp and Sign, PDF Printer

- Built with **WiX 7.0.1 compiled from source** (`d:\projectsNew\wix7`, or `-WixBuild <folder>`), not
  the `wix` on PATH: official WiX builds from 6.0 on fall under the Open Source Maintenance Fee.
  Only its UI and Util extensions are used.
- **Per machine, 64-bit**, into `Program Files\Tetitulz\<app>`, with a Start menu shortcut, the app's
  icon in Apps, the licence page, and "Start …" on the finish page of a new install. A newer MSI of
  the same app replaces the installed one (a fixed upgrade code per app); an older one is refused.
- The licence texts and notices (`LICENSE`, `LICENSE-ATTRIBUTION.txt`, `NOTICE`, `NOTICE.md`,
  `THIRD-PARTY.md`, `licenses\`) are installed beside each app. The printer's licence page shows the
  Attribution License too, since it carries the PostScript plugin.
- **Stamp and Sign** is the .NET Framework 4.8 build, so it needs nothing else. **The PDF Printer** is
  published self-contained, so it needs no .NET 10 on the machine.
- **The printer itself is not installed by the MSI.** The tray app installs it (right-click → Install
  the printer…, one elevation prompt) and turns on its own start at logon. Uninstalling the MSI
  leaves the printer: remove it first from the tray menu, or with `uninstall-printer.ps1` beside the
  app.

**Before an MSI goes out, test it on a VM**: a fresh install, a start from the finish page and the
Start menu, an upgrade from the previous MSI with the app's settings kept, and an uninstall. WiX's
own tests are skipped in the self-compiled build.

Outputs go to `apps\installers\out`, work files to `apps\installers\obj`; neither is committed.
