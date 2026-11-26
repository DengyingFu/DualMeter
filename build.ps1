# Build with Windows PowerShell 5.1 or PowerShell 7 on Windows.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sourceDir = Join-Path $PSScriptRoot 'src'
$outputDir = Join-Path $PSScriptRoot 'dist'
$outputFile = Join-Path $outputDir 'DualMeter.exe'

$frameworkDir = $null
foreach ($architecture in 'Framework64', 'Framework') {
    $candidate = Join-Path $env:WINDIR "Microsoft.NET\$architecture\v4.0.30319"
    if (Test-Path -LiteralPath (Join-Path $candidate 'csc.exe') -PathType Leaf) {
        $frameworkDir = $candidate
        break
    }
}
if (-not $frameworkDir) {
    throw 'The Windows .NET Framework 4.x C# compiler (csc.exe) was not found.'
}

$references = foreach ($name in 'System.Core', 'System.Xaml', 'System.Drawing', 'System.Windows.Forms', 'System.Web.Extensions') {
    '/reference:' + (Join-Path $frameworkDir "$name.dll")
}
$references += foreach ($name in 'WindowsBase', 'PresentationCore', 'PresentationFramework', 'UIAutomationTypes') {
    '/reference:' + (Join-Path $frameworkDir "WPF\$name.dll")
}

$sources = @(Get-ChildItem -LiteralPath $sourceDir -File -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.Name })
if ($sources.Count -eq 0) {
    throw "No C# files found in $sourceDir."
}

[IO.Directory]::CreateDirectory($outputDir) | Out-Null
Push-Location -LiteralPath $sourceDir
try {
    # Relative resource names also work when the checkout path contains spaces or commas.
    $options = @(
        '/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
        '/out:..\dist\DualMeter.exe',
        '/resource:Dashboard.xaml,Dashboard.xaml',
        '/resource:CodexMark.png,CodexMark.png',
        '/resource:AntigravityMark.png,AntigravityMark.png',
        '/win32icon:dual.ico',
        '/win32manifest:DualMeter.manifest'
    )
    $compiler = Join-Path $frameworkDir 'csc.exe'
    & $compiler @options @references @sources
    if ($LASTEXITCODE -ne 0) { throw "DualMeter compilation failed (csc exit code $LASTEXITCODE)." }
}
finally {
    Pop-Location
}
Write-Output "Built $outputFile"
