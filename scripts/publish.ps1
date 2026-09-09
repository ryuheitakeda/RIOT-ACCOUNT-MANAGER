$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/RiotAccounts-win-x64'
$archive = Join-Path $root 'artifacts/RiotAccounts-win-x64.zip'
Push-Location $root
try {
    dotnet test tests/RiotAccounts.Core.Tests -c Release --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    dotnet publish src/RiotAccounts.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $output --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item README.md $output
    Copy-Item docs $output -Recurse -Force
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Get-FileHash $archive -Algorithm SHA256
}
finally { Pop-Location }
