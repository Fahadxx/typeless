param(
    [ValidateSet('none', 'patch', 'minor', 'major')]
    [string]$Bump = 'none',
    [switch]$NoInstall,
    [switch]$NoPackage,
    # Build output only (CI and installer builds): app plus unsigned Typeless.msix in out\<arch> (or -OutDir).
    [switch]$Package,
    [ValidateSet('x64', 'arm64')]
    [string]$Arch,
    [switch]$SelfContained,
    [string]$Publisher = 'CN=Typeless Dev',
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$versionFile = Join-Path $root 'VERSION'
$changelog = Join-Path $root 'CHANGELOG.md'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$version = ([IO.File]::ReadAllText($versionFile)).Trim()

if ($Bump -ne 'none') {
    $parts = $version.Split('.')
    if ($parts.Length -ne 3) { throw "VERSION is not semver: $version" }
    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    switch ($Bump) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    $version = "$major.$minor.$patch"
    [IO.File]::WriteAllText($versionFile, "$version`n", $utf8NoBom)

    $entry = "## $version`n`n- (describe changes)`n`n"
    $old = ''
    if (Test-Path $changelog) { $old = [IO.File]::ReadAllText($changelog) }
    $idx = $old.IndexOf('## ')
    if ($idx -ge 0) {
        $new = $old.Substring(0, $idx) + $entry + $old.Substring($idx)
    } else {
        $new = $entry + $old
    }
    [IO.File]::WriteAllText($changelog, $new, $utf8NoBom)
    Write-Host "Bumped version to $version"
}

# The machine's own architecture: an emulated x64 shell on ARM64 reports AMD64 in its environment.
$hostArch = 'x64'
$nativeArch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Environment').PROCESSOR_ARCHITECTURE
if ($nativeArch -eq 'ARM64') { $hostArch = 'arm64' }
# $Arch and $arch are the same variable (PowerShell names ignore case).
if (-not $Arch) { $Arch = $hostArch }

if ($Package) { $NoInstall = $true }
if (-not $Package -and $Publisher -ne 'CN=Typeless Dev') { throw "-Publisher needs -Package; local installs are signed with CN=Typeless Dev." }

$typelessDir = Join-Path $env:LOCALAPPDATA 'Typeless'
$project = Join-Path $root 'src\Typeless\Typeless.csproj'

if ($NoInstall) {
    # Never touch the installed copy.
    if ($OutDir) { $appDir = [IO.Path]::GetFullPath($OutDir) } else { $appDir = Join-Path $root "out\$arch" }
    if (Test-Path $appDir) { Remove-Item -Recurse -Force $appDir }
} else {
    $appDir = Join-Path $typelessDir 'app'
    Get-Process Typeless -ErrorAction SilentlyContinue | Stop-Process -Force

    # Remove a previous package first so the surrogate dllhost releases TypelessShell.dll.
    $existingPkg = Get-AppxPackage -Name Typeless -ErrorAction SilentlyContinue
    if ($existingPkg) {
        Write-Host "Removing previous Typeless package"
        $existingPkg | Remove-AppxPackage
    }
    # Older builds kept these next to the app folder.
    foreach ($old in @((Join-Path $typelessDir 'Typeless.msix'), (Join-Path $typelessDir 'pkg'))) {
        if (Test-Path $old) { Remove-Item -Recurse -Force $old }
    }
    # Start from an empty folder: a self-contained install leaves hostfxr.dll and the runtime behind, which
    # breaks a framework-dependent exe. A dev build replaces an installer install, so drop its Apps entry too.
    if (Test-Path $appDir) {
        try {
            Remove-Item -Recurse -Force $appDir
        } catch {
            Start-Sleep -Seconds 1
            Remove-Item -Recurse -Force $appDir
        }
    }
    $setupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{80258A07-5649-4B6B-BE32-71B949A51C74}_is1'
    if (Test-Path $setupKey) { Remove-Item -Recurse -Force $setupKey }
}

$sc = 'false'
if ($SelfContained) { $sc = 'true' }
dotnet publish $project -c Release -r "win-$arch" --self-contained $sc -o $appDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$exe = Join-Path $appDir 'Typeless.exe'

# Native shell extension DLL
Push-Location $root
try {
    cmd /c "shell\build.cmd $arch 2>&1" | Out-Host
    $shellRc = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($shellRc -ne 0) {
    Write-Error "shell\build.cmd $arch failed with exit code $shellRc"
    exit $shellRc
}
$dllSrc = Join-Path $root 'shell\out\TypelessShell.dll'
$dllDst = Join-Path $appDir 'TypelessShell.dll'
try {
    Copy-Item -Path $dllSrc -Destination $dllDst -Force
} catch {
    Write-Host "TypelessShell.dll is in use, retrying in 1 s"
    Start-Sleep -Seconds 1
    Copy-Item -Path $dllSrc -Destination $dllDst -Force
}

if ($NoInstall -and -not $Package) {
    Write-Host "Typeless $version published to $appDir"
    exit 0
}

if ($NoPackage) {
    Start-Process -FilePath $exe -ArgumentList '--install' -Wait
    Write-Host "Typeless $version installed to $appDir (classic menu only, -NoPackage)"
    exit 0
}

function Skip-Package([string]$reason) {
    if ($Package) { throw $reason }
    Start-Process -FilePath $exe -ArgumentList '--install' -Wait
    Write-Warning "$reason Top-level menu skipped; the classic 'Show more options' entry stays."
    Write-Host "Typeless $version installed to $appDir"
    exit 0
}

# Package layout, staged outside the app folder
$pkgDir = Join-Path ([IO.Path]::GetTempPath()) "typeless-pkg-$arch"
if (Test-Path $pkgDir) { Remove-Item -Recurse -Force $pkgDir }
$assetsDir = Join-Path $pkgDir 'Assets'
New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

# The menu extensions come from the app itself. An arm64 exe cannot run on an x64 host and a
# framework-dependent x64 exe needs the x64 runtime, so those build a throwaway host copy to ask.
$listExe = $exe
if ($arch -ne $hostArch -and ($hostArch -eq 'x64' -or -not $SelfContained)) {
    $listDir = Join-Path ([IO.Path]::GetTempPath()) 'typeless-list'
    if (Test-Path $listDir) { Remove-Item -Recurse -Force $listDir }
    dotnet publish $project -c Release -r "win-$hostArch" --self-contained false -p:PublishReadyToRun=false -o $listDir | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Host publish for --list-extensions failed with exit code $LASTEXITCODE" }
    $listExe = Join-Path $listDir 'Typeless.exe'
}
$exts = (& $listExe --list-extensions | Out-String) -split "\r?\n" | Where-Object { $_.Trim() }
if (-not $exts) { throw "Typeless.exe --list-extensions returned nothing" }
$itemLines = @()
foreach ($e in $exts) {
    $ext = $e.Trim()
    $itemLines += "            <desktop5:ItemType Type=`"$ext`"><desktop5:Verb Id=`"TypelessConvert`" Clsid=`"7D3A5B1E-4C2F-4E8B-9A61-2F0C8D4E7B19`" /></desktop5:ItemType>"
}

$template = [IO.File]::ReadAllText((Join-Path $root 'package\AppxManifest.template.xml'))
$manifest = $template.Replace('{VERSION}', "$version.0").Replace('{ARCH}', $arch).Replace('{PUBLISHER}', [Security.SecurityElement]::Escape($Publisher)).Replace('{ITEMTYPES}', ($itemLines -join "`r`n"))
[IO.File]::WriteAllText((Join-Path $pkgDir 'AppxManifest.xml'), $manifest, $utf8NoBom)

# Logo assets from the app icon
Add-Type -AssemblyName System.Drawing
$icoPath = Join-Path $root 'src\Typeless\typeless.ico'
$icon = New-Object System.Drawing.Icon($icoPath, 256, 256)
$srcBmp = $icon.ToBitmap()
$assets = @(
    @{ Name = 'StoreLogo.png'; Size = 50 },
    @{ Name = 'Square150x150Logo.png'; Size = 150 },
    @{ Name = 'Square44x44Logo.png'; Size = 44 }
)
foreach ($a in $assets) {
    $bmp = New-Object System.Drawing.Bitmap($a.Size, $a.Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($srcBmp, 0, 0, $a.Size, $a.Size)
    $g.Dispose()
    $bmp.Save((Join-Path $assetsDir $a.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
$srcBmp.Dispose()
$icon.Dispose()

# Windows SDK tools (host binaries)
$kitsBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$sdkDirs = @()
if (Test-Path $kitsBin) {
    $sdkDirs = @(Get-ChildItem -Path $kitsBin -Directory -Filter '10.*' | Sort-Object { [version]$_.Name } -Descending)
}
$makeappx = $null
$signtool = $null
foreach ($candidateArch in @($hostArch, 'x64')) {
    foreach ($d in $sdkDirs) {
        $m = Join-Path $d.FullName "$candidateArch\makeappx.exe"
        $s = Join-Path $d.FullName "$candidateArch\signtool.exe"
        if ((Test-Path $m) -and (Test-Path $s)) { $makeappx = $m; $signtool = $s; break }
    }
    if ($makeappx) { break }
}
if (-not $makeappx) { Skip-Package "Windows SDK makeappx.exe/signtool.exe not found under $kitsBin." }

$msix = Join-Path $appDir 'Typeless.msix'
& $makeappx pack /o /nv /d $pkgDir /p $msix | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "makeappx pack failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
Remove-Item -Recurse -Force $pkgDir

if ($Package) {
    Write-Host "Typeless $version ($arch) built to $appDir with an unsigned Typeless.msix (publisher $Publisher)"
    exit 0
}

# Signing certificate
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=Typeless Dev' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host "Creating self-signed code-signing certificate CN=Typeless Dev"
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Typeless Dev' -KeyUsage DigitalSignature `
        -FriendlyName 'Typeless Dev' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') `
        -NotAfter (Get-Date).AddYears(5)
}
$thumb = $cert.Thumbprint
$cer = Join-Path $typelessDir 'TypelessDev.cer'
Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null

& $signtool sign /fd SHA256 /sha1 $thumb /s My $msix
if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool sign failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Trust (one-time, elevated)
$trustedPath = "Cert:\LocalMachine\TrustedPeople\$thumb"
if (-not (Test-Path $trustedPath)) {
    $importCmd = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    Write-Host ""
    Write-Host "The Typeless Dev certificate is not trusted yet. Windows needs it in LocalMachine\TrustedPeople"
    Write-Host "to install the package that adds the top-level context menu entry."
    Write-Host "A one-time admin (UAC) prompt will now appear to run:"
    Write-Host "  $importCmd"
    Write-Host ""
    try {
        Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $importCmd
    } catch {
        Write-Warning "Elevation was declined or failed: $($_.Exception.Message)"
    }
    if (-not (Test-Path $trustedPath)) {
        Skip-Package "The Typeless Dev certificate is not trusted."
    }
}

# Classic verbs, then the sparse package, which replaces them once it registers.
$reg = & $exe --register | Out-String
if ($reg -match 'menu=top-level') {
    Write-Host "Top-level menu registered"
} else {
    Write-Warning "The package did not register (see $typelessDir\typeless.log). Keeping the classic 'Show more options' entry."
}

Write-Host "Typeless $version installed to $appDir"
