# YuCap 自分専用モデル: 集めた絵で学習して、モデルを作ります（Windows PowerShell 5.1）。
#   powershell -ExecutionPolicy Bypass -File tools\train_model.ps1 [-Name mine] [-Profile <プロファイル名>] [-Minutes 30] [-Arch M|L|S] [-Loss edge|texture]
#   -Arch  M（既定）: ふつうの大きさ。L: より大きいネット（精度は上がるが、遊ぶときの GPU の負荷が M の約 1.5 倍）。S: 小さいネット。
#   -Loss  edge（既定）: 輪郭を強く合わせる。texture: 絵の細かい模様（ざらつき・ハッチング・布目）を保つ損失（SSIM と高周波の項）。
#   -Profile を省くと、data の下にある絵のフォルダーを全部使います（プロファイルごとのフォルダーと starter）。
# 先に tools\setup_train_env.ps1 で学習環境を作り、YuCap の オプション → 自分専用モデル → 学習用の絵を集める で絵を集めておきます。
# 絵: %LOCALAPPDATA%\YuCapTrain\data\<プロファイル名>   できたモデル: %LOCALAPPDATA%\YuCapTrain\models\<名前>.hlsl
# YuCap の 映像 → 拡大の画質 →「自分専用モデル（名前）」で選べます（開き直す必要はありません）。
param(
    [string]$Name = 'mine',
    [string]$Profile = '',
    [double]$Minutes = 30,
    [ValidateSet('M', 'L', 'S')][string]$Arch = 'M',
    [ValidateSet('edge', 'texture')][string]$Loss = 'edge'
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = if ($env:YUCAP_TRAIN_DIR) { $env:YUCAP_TRAIN_DIR } else { Join-Path $env:LOCALAPPDATA 'YuCapTrain' }
$vp = Join-Path $root 'venv\Scripts\python.exe'
if (-not (Test-Path $vp)) { Write-Host '学習環境がありません。先に tools\setup_train_env.ps1 を実行してください。'; exit 1 }

$dataRoot = Join-Path $root 'data'
if ($Profile) {
    $dirs = @(Join-Path $dataRoot $Profile)
    if (-not (Test-Path $dirs[0])) { Write-Host "絵のフォルダーがありません: $($dirs[0])"; exit 1 }
} else {
    # Every folder under data that holds pictures (one per profile, plus the starter set).
    $dirs = @(Get-ChildItem -Path $dataRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Get-ChildItem -Path $_.FullName -Filter *.png -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 } |
        ForEach-Object { $_.FullName })
    if ($dirs.Count -eq 0) {
        Write-Host "絵がまだありません: $dataRoot"
        Write-Host 'YuCap の オプション → 自分専用モデル → 学習用の絵を集める をオンにして遊んでください。'
        exit 1
    }
}

$n = 0
foreach ($d in $dirs) {
    $c = (Get-ChildItem -Path $d -Filter *.png -Recurse -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "絵: $c 枚 ($d)"
    $n += $c
}
Write-Host "合計: $n 枚"
if ($n -lt 40) { Write-Host '絵が少なすぎます（目安: 数百枚以上、いろいろな場面）。少ないと効果が出ないか、逆に悪くなります。続けます。' }

$script = Join-Path $PSScriptRoot 'train_upscaler.py'
& $vp $script train --name $Name --data $dirs --minutes $Minutes --arch $Arch --loss $Loss
exit $LASTEXITCODE
