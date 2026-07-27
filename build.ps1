[CmdletBinding()]
param(
    [switch]$Core,
    [switch]$Frontend,
    [switch]$WordIntegration,
    [switch]$AddIn,
    [switch]$All,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$applicationProject = Join-Path $repoRoot 'src\SmartWord.Application\SmartWord.Application.csproj'
$applicationTestProject = Join-Path $repoRoot 'tests\SmartWord.Application.Tests\SmartWord.Application.Tests.csproj'
$wordTestProject = Join-Path $repoRoot 'tests\SmartWord.OfficeIntegration.Tests\SmartWord.OfficeIntegration.Tests.csproj'
$addinProject = Join-Path $repoRoot 'src\SmartWord.AddIn\SmartWord.AddIn.csproj'
$webClientDirectory = Join-Path $repoRoot 'web\SmartWord.WebClient'
$netFrameworkReference = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2\mscorlib.dll'

function Assert-CommandAvailable
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$InstallHint
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command)
    {
        throw "未找到命令 '$Name'。$InstallHint"
    }

    return $command.Source
}

function Invoke-CheckedCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$ArgumentList,
        [string]$WorkingDirectory = $repoRoot
    )

    Write-Host ("> " + $FilePath + " " + ($ArgumentList -join ' ')) -ForegroundColor Cyan
    Push-Location $WorkingDirectory
    try
    {
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0)
        {
            throw "命令失败，退出码 $LASTEXITCODE：$FilePath"
        }
    }
    finally
    {
        Pop-Location
    }
}

function Get-VisualStudioInstallationPaths
{
    # vswhere 能识别自定义安装盘，避免把开发机盘符写死在仓库中。
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ([string]::IsNullOrWhiteSpace($programFilesX86))
    {
        throw '未找到 ProgramFiles(x86) 环境变量，当前系统不支持 VSTO 构建。'
    }

    $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere))
    {
        throw '未找到 vswhere.exe。请通过 Visual Studio Installer 安装 Visual Studio 2022 或 Build Tools。'
    }

    $installationPaths = @(& $vswhere -all -products * -property installationPath)
    if ($LASTEXITCODE -ne 0)
    {
        throw "vswhere.exe 执行失败，退出码：$LASTEXITCODE"
    }

    return @($installationPaths | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Find-VisualStudioBuildEnvironment
{
    # 可能同时安装多个 VS 实例，只选择真正具备 MSBuild 和 VSTO targets 的实例。
    $installationPaths = @(Get-VisualStudioInstallationPaths)
    if ($installationPaths.Count -eq 0)
    {
        throw '未发现 Visual Studio 安装。请安装 Visual Studio 2022，并选择“Office/SharePoint 开发”和“.NET 桌面开发”工作负载。'
    }

    $instancesWithMSBuild = @()
    foreach ($installationPath in $installationPaths)
    {
        $msbuild = Join-Path $installationPath 'MSBuild\Current\Bin\MSBuild.exe'
        if (-not (Test-Path -LiteralPath $msbuild))
        {
            continue
        }

        $instancesWithMSBuild += $installationPath
        $visualStudioTargetsRoot = Join-Path $installationPath 'MSBuild\Microsoft\VisualStudio'
        $vstoTargets = Get-ChildItem -LiteralPath $visualStudioTargetsRoot -Filter 'Microsoft.VisualStudio.Tools.Office.targets' -File -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($vstoTargets)
        {
            return [PSCustomObject]@{
                InstallationPath = $installationPath
                MSBuildPath = $msbuild
                VstoTargetsPath = $vstoTargets.FullName
            }
        }
    }

    if ($instancesWithMSBuild.Count -gt 0)
    {
        $detectedInstances = $instancesWithMSBuild -join '；'
        throw ('已找到 Visual Studio MSBuild，但缺少 Microsoft.VisualStudio.Tools.Office.targets。请在 Visual Studio Installer 中为对应实例安装“Office/SharePoint 开发”工作负载。已检查：{0}' -f $detectedInstances)
    }

    throw '已发现 Visual Studio，但未找到 MSBuild.exe。请在 Visual Studio Installer 中安装 MSBuild 和“.NET 桌面开发”工作负载。'
}

function Assert-AddInBuildPrerequisites
{
    param(
        [Parameter(Mandatory = $true)]
        [PSCustomObject]$BuildEnvironment
    )

    # VSTO targets 存在不代表目标框架和 Office Tools 引用程序集已经完整安装。
    if (-not (Test-Path -LiteralPath $netFrameworkReference))
    {
        throw '缺少 .NET Framework 4.7.2 reference assemblies。请在 Visual Studio Installer 的“单个组件”中安装“.NET Framework 4.7.2 targeting pack”和 SDK。'
    }

    $vstoReference = Join-Path $BuildEnvironment.InstallationPath 'Common7\IDE\ReferenceAssemblies\v4.0\Microsoft.Office.Tools.dll'
    if (-not (Test-Path -LiteralPath $vstoReference))
    {
        throw '缺少 Microsoft.Office.Tools 引用程序集。请在 Visual Studio Installer 中安装“Office/SharePoint 开发”工作负载。'
    }

    $officeInteropFound = Test-OfficeInteropReferences $BuildEnvironment.InstallationPath
    if (-not $officeInteropFound)
    {
        throw '缺少 Office Primary Interop Assemblies。请在 Visual Studio Installer 中安装“Office/SharePoint 开发”工作负载，或修复 Microsoft Office 安装。'
    }
}

function Test-OfficeInteropReferences
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallationPath
    )

    # Shared 目录相对 VS 实例的位置会因默认安装或自定义安装盘而不同，逐级检查祖先目录。
    $piaDirectories = @()
    $currentDirectory = Get-Item -LiteralPath $InstallationPath
    for ($level = 0; $level -lt 4 -and $currentDirectory; $level++)
    {
        $piaDirectories += Join-Path $currentDirectory.FullName 'Shared\Visual Studio Tools for Office\PIA\Office15'
        $currentDirectory = $currentDirectory.Parent
    }

    $piaDirectories += Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Shared\Visual Studio Tools for Office\PIA\Office15'
    $piaDirectories += Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Shared\Visual Studio Tools for Office\PIA\Office15'

    foreach ($piaDirectory in ($piaDirectories | Select-Object -Unique))
    {
        $officeAssembly = Join-Path $piaDirectory 'Office.dll'
        $wordAssembly = Join-Path $piaDirectory 'Microsoft.Office.Interop.Word.dll'
        if ((Test-Path -LiteralPath $officeAssembly) -and (Test-Path -LiteralPath $wordAssembly))
        {
            return $true
        }
    }

    return $false
}

function Find-WordExecutable
{
    $registryKeys = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\WINWORD.EXE',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\WINWORD.EXE'
    )

    foreach ($registryKey in $registryKeys)
    {
        if (Test-Path -LiteralPath $registryKey)
        {
            $wordPath = (Get-ItemProperty -LiteralPath $registryKey).'(default)'
            if ($wordPath -and (Test-Path -LiteralPath $wordPath))
            {
                return $wordPath
            }
        }
    }

    $wordCandidates = @(
        (Join-Path $env:ProgramFiles 'Microsoft Office\root\Office16\WINWORD.EXE'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Office\root\Office16\WINWORD.EXE')
    )

    return $wordCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}

function Invoke-CoreValidation
{
    $dotnet = Assert-CommandAvailable 'dotnet' '请安装支持 .NET Framework 项目构建的 .NET SDK。'
    Invoke-CheckedCommand $dotnet @('build', $applicationProject, '--configuration', $Configuration)
    Invoke-CheckedCommand $dotnet @('test', $applicationTestProject, '--configuration', $Configuration)
    Invoke-CheckedCommand $dotnet @('test', $wordTestProject, '--configuration', $Configuration)
}

function Invoke-FrontendValidation
{
    $node = Assert-CommandAvailable 'node' '请安装 Node.js LTS。'
    $npm = Assert-CommandAvailable 'npm' '请安装包含 npm 的 Node.js LTS。'
    $packageJson = Join-Path $webClientDirectory 'package.json'
    if (-not (Test-Path -LiteralPath $packageJson))
    {
        throw "未找到前端 package.json：$packageJson"
    }

    Write-Host ("Node: " + (& $node --version)) -ForegroundColor Green
    $nodeModules = Join-Path $webClientDirectory 'node_modules'
    $viteCommand = Join-Path $nodeModules '.bin\vite.cmd'
    $vuePackage = Join-Path $nodeModules 'vue\package.json'
    if (-not (Test-Path -LiteralPath $viteCommand) -or -not (Test-Path -LiteralPath $vuePackage))
    {
        # 使用工作区专属缓存，避免多个工作区共享全局 npm 缓存时发生文件锁冲突。
        $cacheDirectoryName = 'npm-cache-' + (Split-Path $repoRoot -Leaf)
        $npmCache = Join-Path ([System.IO.Path]::GetTempPath()) (Join-Path 'SmartWord' $cacheDirectoryName)
        New-Item -ItemType Directory -Path $npmCache -Force | Out-Null
        Invoke-CheckedCommand $npm @('ci', '--cache', $npmCache, '--no-audit', '--no-fund') $webClientDirectory
    }

    Invoke-CheckedCommand $npm @('run', 'build') $webClientDirectory
}

function Invoke-WordIntegrationValidation
{
    $dotnet = Assert-CommandAvailable 'dotnet' '请安装支持 .NET Framework 项目构建的 .NET SDK。'
    $wordPath = Find-WordExecutable
    if (-not $wordPath)
    {
        throw '未找到 WINWORD.EXE。真实 Word 集成测试需要安装桌面版 Microsoft Word，并完成首次启动配置。'
    }

    # 测试默认跳过真实 Word；仅在此显式入口中临时开启，并在结束后恢复调用方环境。
    $oldIntegrationFlag = [Environment]::GetEnvironmentVariable('SMARTWORD_RUN_WORD_INTEGRATION', 'Process')
    try
    {
        $env:SMARTWORD_RUN_WORD_INTEGRATION = '1'
        Write-Host ("Word: " + $wordPath) -ForegroundColor Green
        Invoke-CheckedCommand $dotnet @('test', $wordTestProject, '--configuration', $Configuration, '--logger', 'console;verbosity=normal')
    }
    finally
    {
        if ($null -eq $oldIntegrationFlag)
        {
            Remove-Item Env:SMARTWORD_RUN_WORD_INTEGRATION -ErrorAction SilentlyContinue
        }
        else
        {
            $env:SMARTWORD_RUN_WORD_INTEGRATION = $oldIntegrationFlag
        }
    }
}

function Invoke-AddInValidation
{
    # AddIn 必须使用完整 .NET Framework MSBuild，不能使用 dotnet SDK 自带的 MSBuild。
    $buildEnvironment = Find-VisualStudioBuildEnvironment
    Assert-AddInBuildPrerequisites $buildEnvironment

    Write-Host ("Visual Studio: " + $buildEnvironment.InstallationPath) -ForegroundColor Green
    Write-Host ("MSBuild: " + $buildEnvironment.MSBuildPath) -ForegroundColor Green
    Write-Host ("VSTO targets: " + $buildEnvironment.VstoTargetsPath) -ForegroundColor Green
    Invoke-CheckedCommand $buildEnvironment.MSBuildPath @(
        $addinProject,
        '/restore',
        '/t:Build',
        "/p:Configuration=$Configuration",
        '/p:Platform=AnyCPU',
        '/m',
        '/v:minimal'
    )
}

$selected = @($Core, $Frontend, $WordIntegration, $AddIn, $All) |
    Where-Object { $_ } |
    Measure-Object |
    Select-Object -ExpandProperty Count

if ($selected -eq 0)
{
    $Core = $true
}

if ($Core -or $All)
{
    Invoke-CoreValidation
}

if ($Frontend -or $All)
{
    Invoke-FrontendValidation
}

if ($AddIn -or $All)
{
    Invoke-AddInValidation
}

if ($WordIntegration -or $All)
{
    Invoke-WordIntegrationValidation
}

Write-Host 'SmartWord 验证完成。' -ForegroundColor Green
