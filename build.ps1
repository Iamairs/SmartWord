[CmdletBinding()]
param(
    [switch]$Core,
    [switch]$WordIntegration,
    [switch]$AddIn,
    [switch]$All
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionPath = Join-Path $repoRoot 'SmartWord.sln'
$testProject = Join-Path $repoRoot 'tests\SmartWord.OfficeIntegration.Tests\SmartWord.OfficeIntegration.Tests.csproj'
$addinProject = Join-Path $repoRoot 'src\SmartWord.AddIn\SmartWord.AddIn.csproj'

function Invoke-CheckedCommand {
    param(
        [string]$FilePath,
        [string[]]$ArgumentList
    )

    Write-Host ("> " + $FilePath + " " + ($ArgumentList -join ' ')) -ForegroundColor Cyan
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "命令失败，退出码 $LASTEXITCODE：$FilePath"
    }
}

function Invoke-CoreValidation {
    Invoke-CheckedCommand 'dotnet' @('build', (Join-Path $repoRoot 'src\SmartWord.Application\SmartWord.Application.csproj'))
    Invoke-CheckedCommand 'dotnet' @('test', (Join-Path $repoRoot 'tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj'), '--no-restore')
    Invoke-CheckedCommand 'dotnet' @('test', $testProject, '--no-restore')
}

function Invoke-WordIntegrationValidation {
    $wordCandidates = @(
        'C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE',
        'C:\Program Files (x86)\Microsoft Office\root\Office16\WINWORD.EXE'
    )
    $wordPath = $wordCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $wordPath) {
        throw '未找到 WINWORD.EXE。真实 Word 集成测试需要安装桌面版 Microsoft Word。'
    }

    $msbuild = Find-MSBuild
    $msbuildRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $msbuild))
    $vstoTargets = Get-ChildItem -Path $msbuildRoot -Filter 'Microsoft.VisualStudio.Tools.Office.targets' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $vstoTargets) {
        throw '未找到 Microsoft.VisualStudio.Tools.Office.targets。请安装 Visual Studio Office/SharePoint 工具或 VSTO workload。'
    }

    $oldIntegrationFlag = [Environment]::GetEnvironmentVariable('SMARTWORD_RUN_WORD_INTEGRATION', 'Process')
    try {
        $env:SMARTWORD_RUN_WORD_INTEGRATION = '1'
        Write-Host ("Word: " + $wordPath) -ForegroundColor Green
        Write-Host ("VSTO targets: " + $vstoTargets.FullName) -ForegroundColor Green
        Invoke-CheckedCommand 'dotnet' @('test', $testProject, '--no-restore', '--logger', 'console;verbosity=normal')
    }
    finally {
        if ($null -eq $oldIntegrationFlag) {
            Remove-Item Env:SMARTWORD_RUN_WORD_INTEGRATION -ErrorAction SilentlyContinue
        }
        else {
            $env:SMARTWORD_RUN_WORD_INTEGRATION = $oldIntegrationFlag
        }
    }
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $installationPath = & $vswhere -latest -products * -property installationPath
        if ($installationPath) {
            $candidate = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
            if (Test-Path -LiteralPath $candidate) {
                return $candidate
            }
        }
    }

    $knownCandidates = @(
        'D:\softwares\VisualStudio\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe'
    )
    $found = $knownCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $found) {
        throw '未找到 VS MSBuild.exe。请安装 Visual Studio 2022 或 Build Tools。'
    }

    return $found
}

function Invoke-AddInValidation {
    $msbuild = Find-MSBuild
    $msbuildRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $msbuild))
    $vstoTargets = Get-ChildItem -Path $msbuildRoot -Filter 'Microsoft.VisualStudio.Tools.Office.targets' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $vstoTargets) {
        throw '未找到 Microsoft.VisualStudio.Tools.Office.targets，无法构建 VSTO AddIn。'
    }

    Invoke-CheckedCommand $msbuild @($addinProject, '/restore', '/t:Build', '/p:Configuration=Debug', '/p:Platform=AnyCPU', '/m:1', '/v:minimal')
}

$selected = @($Core, $WordIntegration, $AddIn, $All) | Where-Object { $_ } | Measure-Object | Select-Object -ExpandProperty Count
if ($selected -eq 0) {
    $Core = $true
}

if ($Core -or $All) {
    Invoke-CoreValidation
}
if ($WordIntegration -or $All) {
    Invoke-WordIntegrationValidation
}
if ($AddIn -or $All) {
    Invoke-AddInValidation
}

Write-Host 'SmartWord 验证完成。' -ForegroundColor Green
