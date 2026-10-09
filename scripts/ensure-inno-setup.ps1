[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $repoRoot 'artifacts/release/_tooling/innosetup-6.7.3'
$compiler = Get-ChildItem -LiteralPath $toolRoot -Filter ISCC.exe -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $compiler) {
    $downloadRoot = Join-Path $repoRoot 'artifacts/release/_tooling/downloads'
    $null = New-Item -ItemType Directory -Path $downloadRoot -Force
    $setupFile = Join-Path $downloadRoot 'innosetup-6.7.3.exe'
    $setupUri = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe'
    Invoke-WebRequest -Uri $setupUri -OutFile $setupFile

    $signature = Get-AuthenticodeSignature -FilePath $setupFile
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
        Remove-Item -LiteralPath $setupFile -Force
        throw 'Inno Setup 安装程序签名校验失败；已删除下载文件。'
    }

    $null = New-Item -ItemType Directory -Path $toolRoot -Force
    $installArguments = @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/PORTABLE=1', "/DIR=$toolRoot"
    )
    & $setupFile @installArguments
    $compiler = Get-ChildItem -LiteralPath $toolRoot -Filter ISCC.exe -File -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $compiler) {
        $exitCode = if ($null -eq $LASTEXITCODE) { '未知' } else { [string]$LASTEXITCODE }
        throw "Inno Setup 便携安装失败，退出码 $exitCode，且未生成 ISCC.exe。"
    }
}

if (-not $compiler) { throw "未在本机工具目录中找到 ISCC.exe：$toolRoot" }
Write-Output $compiler
