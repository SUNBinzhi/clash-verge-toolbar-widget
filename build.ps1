$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $repoRoot 'src'
$flagsDir = Join-Path $repoRoot 'assets\flags'
$outputDir = Join-Path $repoRoot 'dist'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$output = Join-Path $outputDir 'ClashLeftWidget.exe'

$arguments = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    '/main:ClashLeftWidget.Program',
    ('/win32manifest:' + (Join-Path $sourceDir 'app.manifest')),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    ('/out:' + $output)
)

foreach ($flag in Get-ChildItem -LiteralPath $flagsDir -Filter '*.png') {
    $code = [System.IO.Path]::GetFileNameWithoutExtension($flag.Name).ToLowerInvariant()
    $arguments += '/resource:' + $flag.FullName + ',flags.' + $code + '.png'
}

$arguments += Join-Path $sourceDir 'Program.cs'
$arguments += Join-Path $sourceDir 'PipeHttp.cs'

& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Host "Built: $output"
