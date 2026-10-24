param(
    [switch]$RemoveCert
)

$ErrorActionPreference = 'Stop'

$typelessDir = Join-Path $env:LOCALAPPDATA 'Typeless'
$appDir = Join-Path $typelessDir 'app'
$exe = Join-Path $appDir 'Typeless.exe'

$pkg = Get-AppxPackage -Name Typeless -ErrorAction SilentlyContinue
if ($pkg) {
    $pkg | Remove-AppxPackage
    Write-Host "Removed the Typeless package (top-level menu)"
}

if (Test-Path $exe) {
    Start-Process -FilePath $exe -ArgumentList '--uninstall' -Wait
}

Get-Process Typeless -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $appDir) {
    try {
        Remove-Item -Recurse -Force $appDir
    } catch {
        # The surrogate dllhost may hold TypelessShell.dll for a moment after package removal.
        Start-Sleep -Seconds 1
        Remove-Item -Recurse -Force $appDir
    }
}

$pkgDir = Join-Path $typelessDir 'pkg'
if (Test-Path $pkgDir) { Remove-Item -Recurse -Force $pkgDir }
$msix = Join-Path $typelessDir 'Typeless.msix'
if (Test-Path $msix) { Remove-Item -Force $msix }

if ($RemoveCert) {
    $certs = @(Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Typeless Dev' })
    $trusted = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq 'CN=Typeless Dev' })
    if ($trusted.Count -gt 0) {
        $removeCmd = "Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { `$_.Subject -eq 'CN=Typeless Dev' } | Remove-Item"
        Write-Host "An admin (UAC) prompt will appear to remove the Typeless Dev certificate from LocalMachine\TrustedPeople."
        try {
            Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $removeCmd
        } catch {
            Write-Warning "Elevation was declined or failed: $($_.Exception.Message)"
        }
        $left = @(Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -eq 'CN=Typeless Dev' })
        if ($left.Count -gt 0) {
            Write-Warning "The trusted Typeless Dev certificate is still in LocalMachine\TrustedPeople."
        } else {
            Write-Host "Removed the trusted Typeless Dev certificate"
        }
    }
    foreach ($c in $certs) {
        Remove-Item -Path "Cert:\CurrentUser\My\$($c.Thumbprint)" -DeleteKey
    }
    if ($certs.Count -gt 0) { Write-Host "Removed the Typeless Dev signing certificate from CurrentUser\My" }
    $cer = Join-Path $typelessDir 'TypelessDev.cer'
    if (Test-Path $cer) { Remove-Item -Force $cer }
}

Write-Host "Typeless removed from $appDir"
