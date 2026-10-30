# The stamp of the hooks and loc sources, as git hashes them (line endings normalized). prebuilt/client/source.sha256
# holds the stamp of the sources its DLLs were built from.
#
# usage: pwsh tools/release/client-stamp.ps1          prints the stamp of the current sources
#        pwsh tools/release/client-stamp.ps1 -Check   fails when prebuilt/client was built from other sources
param([switch]$Check)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

Push-Location $root
try {
    # Include linked shared code as well: either rules file changes what the hooks DLL does.
    $files = @(git ls-files src/Ro3Emu.ClientHooks src/Ro3Emu.Loc src/Ro3Emu.Server/Game/F2PRules.cs src/Ro3Emu.Server/Data/Config/SupporterIdentityRules.cs src/Ro3Emu.Server/Data/Config/CharacterAppearanceRules.cs src/Ro3Emu.Server/Data/Config/SupporterShopRules.cs |
        Where-Object { $_ -notmatch '/(bin|obj)/' })
    $hashes = @($files | git hash-object --stdin-paths)
    $text = (0..($files.Count - 1) | ForEach-Object { "$($files[$_]) $($hashes[$_])" }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))
    $current = [Convert]::ToHexStringLower($sha)
} finally { Pop-Location }

if (-not $Check) { return $current }
$stamp = (Get-Content (Join-Path $root 'prebuilt\client\source.sha256') -Raw).Trim()
if ($stamp -ne $current) {
    throw "prebuilt/client was built from other hooks/loc sources ($stamp, now $current). On a machine with the current client run: pwsh tools/release/pack.ps1 -UpdatePrebuiltClientDlls, then commit prebuilt/client."
}
Write-Host "prebuilt/client matches the hooks and loc sources ($current)"
