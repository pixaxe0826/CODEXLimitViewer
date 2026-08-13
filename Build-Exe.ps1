$ErrorActionPreference = 'Stop'

$compilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$compiler = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if ($null -eq $compiler) {
    throw '.NET Framework C# 컴파일러(csc.exe)를 찾지 못했습니다.'
}

$sourcePath = Join-Path $PSScriptRoot 'Bootstrap.cs'
$manifestPath = Join-Path $PSScriptRoot 'app.manifest'
$iconPath = Join-Path $PSScriptRoot 'assets\codex-quota-icon.ico'
$distDirectory = Join-Path $PSScriptRoot 'dist'
$outputPath = Join-Path $distDirectory 'CodexQuotaOverlay.exe'

foreach ($requiredPath in @($sourcePath, $manifestPath, $iconPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "필수 빌드 파일을 찾지 못했습니다: $requiredPath"
    }
}

if (-not (Test-Path -LiteralPath $distDirectory)) {
    New-Item -ItemType Directory -Path $distDirectory -Force | Out-Null
}

$compilerArguments = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    ('/out:' + $outputPath),
    ('/win32manifest:' + $manifestPath),
    ('/win32icon:' + $iconPath),
    '/reference:System.dll',
    '/reference:System.Core.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Web.Extensions.dll',
    $sourcePath
)

& $compiler $compilerArguments
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
    throw "EXE 빌드에 실패했습니다. csc.exe 종료 코드: $LASTEXITCODE"
}

$output = Get-Item -LiteralPath $outputPath
Write-Host ('Built: {0} ({1:N0} bytes)' -f $output.FullName, $output.Length)
