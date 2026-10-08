<#
.SYNOPSIS
    Renders all PlantUML files in a folder to images.

.DESCRIPTION
    The script uses plantuml.jar and a Java runtime. If the jar is not found, the script downloads the latest release from GitHub.
    If Graphviz (dot.exe) is not on PATH, the script uses the Smetana layout engine that is built into PlantUML.
    The images are written next to the .puml files. The script works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
    .\Render-Diagrams.ps1 -Path "$env:USERPROFILE\.claude\pr-uml\Shop\feature-payments"
#>
[CmdletBinding()]
param(
    # The folder that contains the .puml files.
    [Parameter(Mandatory = $true)]
    [string] $Path,

    # The image format: png or svg.
    [ValidateSet('png', 'svg')]
    [string] $Format = 'png',

    # The location of plantuml.jar.
    [string] $JarPath = (Join-Path $env:LOCALAPPDATA 'plantuml\plantuml.jar')
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    throw 'Java was not found. Install a Java runtime (version 17 or later) and make sure java.exe is on PATH.'
}

if (-not (Test-Path -LiteralPath $JarPath)) {
    Write-Host "Downloading plantuml.jar to $JarPath"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $JarPath) | Out-Null

    # Windows PowerShell 5.1 does not always enable TLS 1.2. GitHub requires it.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/plantuml/plantuml/releases/latest/download/plantuml.jar' -OutFile $JarPath
}

[string[]] $files = @(Get-ChildItem -LiteralPath $Path -Filter '*.puml' -File | ForEach-Object { $_.FullName })

if ($files.Count -eq 0) {
    throw "No .puml files were found in $Path."
}

[string[]] $arguments = @('-jar', $JarPath, "-t$Format", '-charset', 'UTF-8')

if (-not (Get-Command dot -ErrorAction SilentlyContinue)) {
    $arguments += '-Playout=smetana'
}

& java @arguments @files
[int] $exitCode = $LASTEXITCODE

Get-ChildItem -LiteralPath $Path -Filter "*.$Format" -File | ForEach-Object { $_.FullName }

if ($exitCode -ne 0) {
    # PlantUML writes an image that shows the syntax error. Fix the .puml file and run the script again.
    throw "PlantUML returned exit code $exitCode. At least one diagram has a syntax error."
}
