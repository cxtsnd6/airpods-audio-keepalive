#Requires -Version 5.1

$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The 64-bit Windows .NET Framework C# compiler was not found.'
}

$source = Join-Path $PSScriptRoot 'AirPodsAudioKeepAlive.cs'
$outputDirectory = Join-Path $PSScriptRoot 'bin'
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
$output = Join-Path $outputDirectory 'AirPodsAudioKeepAlive.exe'

$compilerArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:x64'
    '/optimize+'
    '/utf8output'
    '/reference:System.Windows.Forms.dll'
    "/out:$output"
    $source
)
& $compiler @compilerArguments
if ($LASTEXITCODE -ne 0) {
    throw "C# compilation failed with exit code $LASTEXITCODE."
}
if (!(Test-Path -LiteralPath $output -PathType Leaf)) {
    throw 'The compiler did not produce an executable.'
}

Write-Output $output
