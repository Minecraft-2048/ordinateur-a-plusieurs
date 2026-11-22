# Compile MultiSouris.exe avec le compilateur C# livré avec Windows (.NET Framework 4, C# 5).
#   .\construire.ps1            -> compile MultiSouris.exe (l'appli ne doit pas tourner)
#   .\construire.ps1 -Relancer  -> arrête l'appli si elle tourne, compile et la relance
param([switch]$Relancer)

$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $ici 'MultiSouris.exe'
$icone = Join-Path $ici 'assets\multi-souris.ico'
$sources = Get-ChildItem (Join-Path $ici 'src\*.cs') | ForEach-Object { $_.FullName }

function Compiler([string]$sortie, [bool]$avecIcone) {
    $options = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
        "/out:$sortie", "/win32manifest:$(Join-Path $ici 'src\app.manifest')",
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    if ($avecIcone) { $options += "/win32icon:$icone" }
    & $csc @options @sources
    if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }
}

if ($Relancer -and (Get-Process MultiSouris -ErrorAction SilentlyContinue)) {
    & $exe --quitter
    Start-Sleep -Milliseconds 1500
}
if (-not (Test-Path $icone)) {
    # L'icône est dessinée par l'appli elle-même : une première compilation sans icône sert à la fabriquer.
    Compiler $exe $false
    & $exe --icone $icone | Out-Null
}
Compiler $exe $true
if ($Relancer) { Start-Process $exe }
"Compilé : $exe"
