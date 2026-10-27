# Verify the declared MIT projects form an independent project-reference closure.
# This guard intentionally fails closed: an added community project must be explicitly allowed.
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$solutionPath = Join-Path $root 'MyAgents.Community.slnx'
if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "Community solution missing: $solutionPath"
}

[xml]$solution = Get-Content -LiteralPath $solutionPath -Raw
$listed = @($solution.SelectNodes('//Project') | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') })
$expected = @(
    'MyAgents.Core/MyAgents.Core.csproj',
    'MyAgents.Agents/MyAgents.Agents.csproj',
    'MyAgents.Runtime.Chat/MyAgents.Runtime.Chat.csproj',
    'MyAgents.Memory/MyAgents.Memory.csproj',
    'MyAgents.Mcp/MyAgents.Mcp.csproj',
    'MyAgents.Testing/MyAgents.Testing.csproj',
    'community/tests/MyAgents.Community.Tests/MyAgents.Community.Tests.csproj',
    'community/samples/ChatWithTools/ChatWithTools.csproj'
)
$extras = @($listed | Where-Object { $_ -notin $expected })
$missing = @($expected | Where-Object { $_ -notin $listed })
if ($extras.Count -gt 0 -or $missing.Count -gt 0) {
    throw "Community manifest mismatch. Extra: $($extras -join ', '); missing: $($missing -join ', ')"
}

$allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $expected) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not (Test-Path -LiteralPath $absolute)) {
        throw "Missing community project: $relative"
    }
    [void]$allowed.Add($absolute)
}
[void]$allowedFiles.Add([IO.Path]::GetFullPath((Join-Path $root 'Directory.Build.props')))
[void]$allowedFiles.Add([IO.Path]::GetFullPath((Join-Path $root 'Directory.Packages.props')))

function Test-WithinCommunity([string]$path) {
    $fullPath = [IO.Path]::GetFullPath($path)
    return $allowed.Contains($fullPath) -or $allowedFiles.Contains($fullPath) -or
        $fullPath.StartsWith((Join-Path $root 'community') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        @($expected | Where-Object {
            $project = [IO.Path]::GetFullPath((Join-Path $root $_))
            $directory = (Split-Path -Parent $project) + [IO.Path]::DirectorySeparatorChar
            $fullPath.StartsWith($directory, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
}

function Visit-BuildFile([string]$filePath, [Collections.Generic.HashSet[string]]$visited) {
    $fullPath = [IO.Path]::GetFullPath($filePath)
    if (-not $visited.Add($fullPath)) { return }
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Open-core boundary violation: imported build file is missing: $fullPath"
    }
    if (-not (Test-WithinCommunity $fullPath)) {
        throw "Open-core boundary violation: build file is outside Community: $fullPath"
    }

    [xml]$buildFile = Get-Content -LiteralPath $fullPath -Raw
    $buildDirectory = Split-Path -Parent $fullPath
    foreach ($reference in $buildFile.SelectNodes('//ProjectReference')) {
        $include = $reference.GetAttribute('Include')
        if ([string]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) { continue }
        $dependency = [IO.Path]::GetFullPath((Join-Path $buildDirectory $include))
        if (-not $allowed.Contains($dependency)) {
            throw "Open-core boundary violation: $fullPath references a project outside Community: $include"
        }
        Visit-BuildFile $dependency $visited
    }

    foreach ($import in $buildFile.SelectNodes('//Import[@Project]')) {
        $include = $import.GetAttribute('Project')
        if ([string]::IsNullOrWhiteSpace($include) -or $include.Contains('$(')) { continue }
        $dependency = [IO.Path]::GetFullPath((Join-Path $buildDirectory $include))
        Visit-BuildFile $dependency $visited
    }
}

foreach ($relative in $expected) {
    $projectPath = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $projectDirectory = Split-Path -Parent $projectPath
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    Visit-BuildFile $projectPath $visited

    $licensePath = if ($relative.StartsWith('community/')) {
        Join-Path $root 'community/LICENSE'
    } else {
        Join-Path $projectDirectory 'LICENSE'
    }
    if (-not (Test-Path -LiteralPath $licensePath)) {
        throw "MIT license file missing for $relative at $licensePath"
    }
    $licenseText = Get-Content -LiteralPath $licensePath -Raw
    if (-not $licenseText.TrimStart().StartsWith('MIT License', [StringComparison]::Ordinal)) {
        throw "Expected MIT license for $relative"
    }

    $sourceRoot = $projectDirectory
    $sources = Get-ChildItem -Path $sourceRoot -Filter '*.cs' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[/\\](obj|bin)[/\\]' }
    foreach ($source in $sources) {
        if ((Get-Content -LiteralPath $source.FullName -Raw) -match '(?m)^\s*using\s+MyAgents\.Runtime\.(?!Chat\b)') {
            throw "Runtime namespace outside Community imported by source: $($source.FullName)"
        }
    }
}

Write-Host "PASS: all $($expected.Count) Community projects have MIT grants and no proprietary project or imported build-file dependencies."
