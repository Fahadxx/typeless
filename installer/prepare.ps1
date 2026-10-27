# Run by Setup before it copies files: frees the app folder of the previous install.
# The surrogate dllhost holds TypelessShell.dll until the package is removed, and an old Typeless.exe
# may not know --unregister, so this does not rely on the installed exe.
param([Parameter(Mandatory = $true)][string]$App)

$prefix = $App.TrimEnd('\') + '\'

try {
    Get-AppxPackage -Name Typeless | Remove-AppxPackage
} catch {
    Write-Warning "Could not remove the Typeless package: $($_.Exception.Message)"
}

Get-Process Typeless -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue
