param(
    [Parameter(Mandatory = $true)]
    [string]$Platform,

    [Parameter(Mandatory = $true)]
    [string]$RefName
)

$ErrorActionPreference = "Stop"

[xml]$versionProps = Get-Content "AIrhythm.Version.props"
$version = [string]$versionProps.Project.PropertyGroup.AIrhythmVersion

$packageRoot = "package"
$stage = Join-Path $packageRoot "AI-rhythm_v$version"
$zip = Join-Path $packageRoot "AI-rhythm_v$version.zip"

if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item "AIrhythm.BasicPlugin" $stage -Recurse
Copy-Item "TvAIrPlugin" $stage -Recurse

Copy-Item "AIrhythm.BasicPlugin.sln" $stage
Copy-Item "AIrhythm.Version.props" $stage
Copy-Item "Directory.Build.props" $stage
Copy-Item "README.md" $stage
Copy-Item "README.txt" $stage
Copy-Item "LICENSE" $stage
Copy-Item "THIRD_PARTY_NOTICES.txt" $stage
Copy-Item ".gitignore" $stage

Get-ChildItem $stage -Recurse -Directory |
    Where-Object {
        $_.Name -in @("bin", "obj", ".vs")
    } |
    Remove-Item -Recurse -Force

if (Test-Path $zip) {
    Remove-Item $zip -Force
}

Compress-Archive -Path "$stage\*" -DestinationPath $zip -Force
