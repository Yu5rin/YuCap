# YuCap 自分専用モデル: 集めた絵で学習して、モデルを作ります（Windows PowerShell 5.1）。
#   powershell -ExecutionPolicy Bypass -File tools\train_model.ps1 [-Name mine] [-Profile default] [-Minutes 30]
# 先に tools\setup_train_env.ps1 で学習環境を作り、YuCap の オプション → 自分専用モデル → 学習用の絵を集める で絵を集めておきます。
# 絵: %LOCALAPPDATA%\YuCapTrain\data\<プロファイル名>   できたモデル: %LOCALAPPDATA%\YuCapTrain\models\<名前>.hlsl
# YuCap の 映像 → 拡大の画質 →「自分専用モデル（名前）」で選べます（開き直す必要はありません）。
param(
    [string]$Name = 'mine',
    [string]$Profile = 'default',
    [double]$Minutes = 30
)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = if ($env:YUCAP_TRAIN_DIR) { $env:YUCAP_TRAIN_DIR } else { Join-Path $env:LOCALAPPDATA 'YuCapTrain' }
$vp = Join-Path $root 'venv\Scripts\python.exe'
if (-not (Test-Path $vp)) { Write-Host '学習環境がありません。先に tools\setup_train_env.ps1 を実行してください。'; exit 1 }
$data = Join-Path $root ("data\" + $Profile)
if (-not (Test-Path $data)) { Write-Host "絵のフォルダーがありません: $data"; exit 1 }
$n = (Get-ChildItem -Path $data -Filter *.png -ErrorAction SilentlyContinue | Measure-Object).Count
Write-Host "絵: $n 枚 ($data)"
if ($n -lt 40) { Write-Host '絵が少なすぎます（目安: 数百枚以上、いろいろな場面）。少ないと効果が出ないか、逆に悪くなります。続けます。' }
$script = Join-Path $PSScriptRoot 'train_upscaler.py'
& $vp $script train --name $Name --data $data --minutes $Minutes
exit $LASTEXITCODE
