function Get-FrameTrailSourceProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$UpstreamBaseSha,
        [switch]$RequireGitCheckout
    )
    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git checkout 构建需要 Git。' }
        $topLevel = [string](& git -C $root rev-parse --show-toplevel 2>$null)
        if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($topLevel).TrimEnd('\', '/') -ne $root) {
            throw 'Git 根目录与 FrameTrail 源码目录不一致，拒绝读取父仓库提交。'
        }
        $head = [string](& git -C $root rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or $head -notmatch '^[0-9a-f]{40}$') { throw '无法读取 FrameTrail 仓库 HEAD。' }
        & git -C (Join-Path $root 'upstream/dogsc') cat-file -e "$UpstreamBaseSha^{commit}" 2>$null
        if ($LASTEXITCODE -ne 0) { throw "DogSC 上游基线不存在：$UpstreamBaseSha" }
        $status = @(& git -C $root status --porcelain 2>$null)
        if ($LASTEXITCODE -ne 0) { throw '无法读取仓库工作区状态。' }
        return [pscustomobject]@{ Head=$head; Kind='git checkout'; WorktreeState=$(if ($status.Count -eq 0) { 'clean' } else { 'dirty' }) }
    }
    if ($RequireGitCheckout) { throw '源码归档只能从自身带有 Git 元数据的 FrameTrail 仓库生成。' }
    # An extracted source archive may be inside an unrelated Git checkout. Never
    # let Git's parent-directory discovery substitute that checkout's identity.
    $metadataPath = Join-Path $root 'SOURCE-METADATA.json'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { throw '源码目录没有自身的 Git 元数据或 SOURCE-METADATA.json。' }
    try { $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json }
    catch { throw "源码归档元数据 JSON 无效：$metadataPath" }
    if ($metadata.formatVersion -ne 1 -or
        [string]$metadata.frameTrailCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$metadata.upstreamDogScCommit -ne $UpstreamBaseSha -or
        [string]$metadata.worktreeStateAtArchive -ne 'clean') {
        throw "源码归档元数据版本、提交、上游基线或工作区标记无效：$metadataPath"
    }
    return [pscustomobject]@{ Head=[string]$metadata.frameTrailCommit; Kind='source archive'; WorktreeState='源码归档（无 Git 元数据）' }
}
