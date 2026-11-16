<#
  Builds an MSI for StampSign, the PDF printer's tray app and the PDF viewer, with the self-compiled WiX 7
  (official WiX builds from 6.0 on fall under the Open Source Maintenance Fee; compiling from
  source does not).

  Usage (from anywhere):
    .\build.ps1                         # every app, version = today as yy.mm.dd (e.g. 26.11.5)
    .\build.ps1 -App StampSign          # one app
    .\build.ps1 -App Viewer -Arch x86   # the PDF viewer, 32-bit only (default: both architectures)
    .\build.ps1 -Version 1.2.3
    .\build.ps1 -WixBuild <folder>      # the self-compiled WiX elsewhere (default d:\projectsNew\wix7\src\build)

  Each app is published into obj\<App>[-<arch>]\files with the repository's licence texts and notices
  beside it, then built into out\<App>[-<arch>]_<version>.msi. The PDF viewer is built for x64 and
  x86 from Viewer.wxs (its file associations and Explorer preview); the others are x64 from App.wxs.
  hilaPdf has its own repository and installer.
  Test each MSI on a VM, an upgrade from the previous one included, before it goes out: WiX's own
  tests are skipped in that build.

  Install silently:  msiexec /i out\StampSign_26.11.5.msi /qn /l*v install.log
#>
param(
    [ValidateSet('All', 'StampSign', 'PrinterTray', 'Viewer')]
    [string]$App = 'All',
    [ValidateSet('Both', 'x64', 'x86')]
    [string]$Arch = 'Both',
    [string]$Version,
    [string]$WixBuild = 'd:\projectsNew\wix7\src\build'
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path "$here\..\..").Path

if (-not $Version) {
    $d = Get-Date
    $Version = '{0}.{1}.{2}' -f ($d.Year % 100), $d.Month, $d.Day
}
if ($Version -notmatch '^\d{1,3}\.\d{1,3}\.\d{1,5}$') { throw "Version must look like 26.11.5 (major<=255, minor<=255, build<=65535)" }

$wix = "$WixBuild\wix\Release\publish\wix\wix.exe"
$extUI = "$WixBuild\UI.wixext\Release\netstandard2.0\WixToolset.UI.wixext.dll"
$extUtil = "$WixBuild\Util.wixext\Release\netstandard2.0\WixToolset.Util.wixext.dll"
foreach ($f in $wix, $extUI, $extUtil) { if (-not (Test-Path $f)) { throw "$f not found - compile WiX 7 first" } }

# Per app: the project, how it is published, the name, folder, program, icon, upgrade code and the
# licence texts it ships under (PrinterTray carries the PostScript plugin, under the Attribution License).
$apps = @{
    StampSign = @{
        Project     = "$repo\apps\Tetitulz.Pdf.StampSign\Tetitulz.Pdf.StampSign.csproj"
        Publish     = @('-c', 'Release')
        Name        = 'Tetitulz Stamp and Sign'
        Folder      = 'Stamp and Sign'
        Exe         = 'tetitulz-stampsign.exe'
        Icon        = "$repo\apps\Tetitulz.Pdf.StampSign\stampsign.ico"
        UpgradeCode = '6821387B-0C06-4C01-A1EE-D05F545D3CB3'
        Licences    = @('LICENSE')
    }
    PrinterTray = @{
        Project     = "$repo\apps\Tetitulz.Pdf.PrinterTray\Tetitulz.Pdf.PrinterTray.csproj"
        # Self-contained, so the printer works on a machine without .NET 10.
        Publish     = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true')
        Name        = 'Tetitulz PDF Printer'
        Folder      = 'PDF Printer'
        Exe         = 'TetitulzPdfPrinterTray.exe'
        Icon        = "$repo\apps\Tetitulz.Pdf.PrinterTray\printertray.ico"
        UpgradeCode = '770710CD-3342-423F-A57B-694E0977FA8E'
        Licences    = @('LICENSE', 'LICENSE-ATTRIBUTION.txt')
    }
    # The PDF viewer: self-contained per architecture; its names, upgrade code and registry are in
    # Viewer.wxs. It carries Plugins.Maintenance and Plugins.PageEdits, under the Attribution License.
    Viewer = @{
        Project     = "$repo\apps\Tetitulz.Pdf.WpfViewer\Tetitulz.Pdf.WpfViewer.csproj"
        Publish     = @('-c', 'Release', '--self-contained', 'true')
        Name        = 'Tetitulz PDF Viewer'
        Icon        = "$repo\apps\Tetitulz.Pdf.WpfViewer\viewer.ico"
        Wxs         = "$here\Viewer.wxs"
        Arches      = @('x64', 'x86')
        Licences    = @('LICENSE', 'LICENSE-ATTRIBUTION.txt')
    }
}

# Plain text as RTF for the wizard's licence page: backslashes and braces escaped, every character
# beyond ASCII as \uN?, paragraphs kept.
function ConvertTo-Rtf([string[]]$texts) {
    $sb = [System.Text.StringBuilder]::new('{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\fs16 ')
    foreach ($text in $texts) {
        foreach ($ch in $text.ToCharArray()) {
            switch ($ch) {
                '\' { [void]$sb.Append('\\') }
                '{' { [void]$sb.Append('\{') }
                '}' { [void]$sb.Append('\}') }
                "`n" { [void]$sb.Append("\par`n") }
                "`r" { }
                default {
                    if ([int]$ch -lt 128) { [void]$sb.Append($ch) }
                    else { [void]$sb.Append('\u' + [int][int16]$ch + '?') }
                }
            }
        }
        [void]$sb.Append('\par\par ')
    }
    [void]$sb.Append('}')
    return $sb.ToString()
}

$names = if ($App -eq 'All') { @('StampSign', 'PrinterTray', 'Viewer') } else { @($App) }
# One build per app and architecture: the viewer's two, the others' one (x64, unnamed).
$targets = foreach ($n in $names) {
    if ($apps[$n].Arches) {
        foreach ($x in $apps[$n].Arches) { if ($Arch -eq 'Both' -or $Arch -eq $x) { [pscustomobject]@{ Name = $n; Arch = $x } } }
    } else { [pscustomobject]@{ Name = $n; Arch = $null } }
}
New-Item -ItemType Directory -Force "$here\out" | Out-Null

foreach ($target in $targets) {
    $name = $target.Name
    $a = $apps[$name]
    $label = if ($target.Arch) { "$name-$($target.Arch)" } else { $name }
    $work = "$here\obj\$label"
    $files = "$work\files"
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
    New-Item -ItemType Directory -Force $files | Out-Null

    Write-Host "Publishing $label..."
    $publish = @($a.Publish)
    if ($target.Arch) { $publish += @('-r', "win-$($target.Arch)") }
    # An app of several programs publishes each into the one folder.
    foreach ($project in @($a.Project)) {
        # MinVerVersionOverride as well: MinVer sets the version from the git tags and would put its own
        # (0.0.0-alpha on an untagged commit) over -p:Version in the programs' file and product versions.
        & dotnet publish $project @publish -o $files -p:Version=$Version -p:MinVerVersionOverride=$Version -nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish $label failed ($LASTEXITCODE)" }
    }
    # Debug symbols stay out of the installed folder.
    Get-ChildItem $files -Recurse -Filter *.pdb | Remove-Item

    # Every redistribution carries the licences and notices (Apache 2.0 section 4, MIT, BSD, OFL).
    foreach ($f in @('LICENSE', 'LICENSE-ATTRIBUTION.txt', 'NOTICE', 'NOTICE.md', 'THIRD-PARTY.md')) {
        if (Test-Path "$repo\$f") { Copy-Item "$repo\$f" $files }
    }
    if (Test-Path "$repo\licenses") { Copy-Item "$repo\licenses" "$files\licenses" -Recurse }

    $rtf = "$work\License.rtf"
    $texts = $a.Licences | ForEach-Object { [IO.File]::ReadAllText("$repo\$_") }
    [IO.File]::WriteAllText($rtf, (ConvertTo-Rtf $texts), [System.Text.Encoding]::ASCII)

    $count = (Get-ChildItem $files -Recurse -File).Count
    $out = "$here\out\${label}_$Version.msi"
    Write-Host "Building $($a.Name) $Version from $count files -> $out"
    if ($a.Wxs) {
        $display = if ($target.Arch -eq 'x86') { "$($a.Name) (32-bit)" } else { $a.Name }
        & $wix build $a.Wxs -arch $target.Arch `
            -d ProductVersion=$Version -d "AppName=$display" -d "IconFile=$($a.Icon)" -d "PayloadDir=$files" -d "LicenseRtf=$rtf" `
            -ext $extUI -ext $extUtil -intermediatefolder "$work\wixobj" -o $out
    } else {
        & $wix build "$here\App.wxs" -arch x64 `
            -d ProductVersion=$Version -d "AppName=$($a.Name)" -d "AppFolder=$($a.Folder)" -d "ExeName=$($a.Exe)" `
            -d "UpgradeCode=$($a.UpgradeCode)" -d "IconFile=$($a.Icon)" -d "PayloadDir=$files" -d "LicenseRtf=$rtf" `
            -ext $extUI -ext $extUtil -intermediatefolder "$work\wixobj" -o $out
    }
    if ($LASTEXITCODE -ne 0) { throw "wix build $label failed ($LASTEXITCODE)" }
    Remove-Item "$here\out\*.wixpdb" -ErrorAction SilentlyContinue
    Write-Host ("Done: {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length / 1MB))
}
