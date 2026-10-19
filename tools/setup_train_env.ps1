# YuCap 自分専用モデル: 学習環境を作ります（Windows PowerShell 5.1 で動きます）。
#   powershell -ExecutionPolicy Bypass -File tools\setup_train_env.ps1
# %LOCALAPPDATA%\YuCapTrain\venv に Python の仮想環境を作り、PyTorch（NVIDIA GPU 用）・numpy・pillow を入れます。
# 入るのはその場所だけです（リポジトリ・システムの Python には何も入れません）。消すときは %LOCALAPPDATA%\YuCapTrain を削除してください。
# 通信先: https://download.pytorch.org （PyTorch 本体、約 2 GB）と https://pypi.org （numpy・pillow）。YuCap 本体はここでは通信しません。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$root = if ($env:YUCAP_TRAIN_DIR) { $env:YUCAP_TRAIN_DIR } else { Join-Path $env:LOCALAPPDATA 'YuCapTrain' }
$venv = Join-Path $root 'venv'
$logs = Join-Path $root 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs 'setup.log'
function Say($m) { Write-Host $m; Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format 's'), $m) -Encoding UTF8 }

# 1. Python 3.10-3.12 を探す（無ければ入れずに案内して終わります）
$py = $null
foreach ($v in '3.12', '3.11', '3.10') {
    $p = & py "-$v" -c "import sys; print(sys.executable)" 2>$null
    if ($LASTEXITCODE -eq 0 -and $p) { $py = $p.Trim(); break }
}
if (-not $py) {
    Say 'Python 3.10 - 3.12 が見つかりません。https://www.python.org/downloads/ から入れてから、もう一度実行してください。'
    exit 1
}
Say "Python: $py"

# 2. NVIDIA ドライバーに合う CUDA 版 PyTorch を選ぶ
$smi = & nvidia-smi 2>$null | Out-String
if ($LASTEXITCODE -ne 0 -or $smi -notmatch 'CUDA\s+(?:UMD\s+)?Version:\s*(\d+)\.(\d+)') {
    Say 'NVIDIA の GPU（nvidia-smi）が見つかりません。学習には NVIDIA の GPU が必要です。'
    exit 1
}
$cuda = [double]("{0}.{1}" -f $Matches[1], $Matches[2])
$tag = if ($cuda -ge 13.0) { 'cu130' } elseif ($cuda -ge 12.8) { 'cu128' } else { 'cu126' }
Say "ドライバーの CUDA $cuda -> PyTorch の $tag 版を入れます"

# 3. 仮想環境
if (-not (Test-Path (Join-Path $venv 'Scripts\python.exe'))) {
    & $py -m venv $venv
    if ($LASTEXITCODE -ne 0) { Say '仮想環境を作れませんでした'; exit 1 }
}
$vp = Join-Path $venv 'Scripts\python.exe'

# 4. インストール（PyTorch は cu 版の置き場所だけから取る: 他の置き場所の CPU 版が選ばれないように）
Say 'PyTorch を入れています（約 2 GB。数分かかります）...'
& $vp -m pip install --upgrade pip 2>&1 | Out-File -Append -Encoding utf8 $log
& $vp -m pip install torch torchvision --index-url "https://download.pytorch.org/whl/$tag" 2>&1 | Out-File -Append -Encoding utf8 $log
if ($LASTEXITCODE -ne 0) { Say "PyTorch を入れられませんでした。$log を見てください"; exit 1 }
& $vp -m pip install numpy pillow 2>&1 | Out-File -Append -Encoding utf8 $log
if ($LASTEXITCODE -ne 0) { Say "numpy / pillow を入れられませんでした。$log を見てください"; exit 1 }

# 5. 確認
$chk = & $vp -c "import torch; print(torch.__version__, torch.version.cuda, torch.cuda.is_available(), torch.cuda.get_device_name(0) if torch.cuda.is_available() else '-')"
Say "確認: $chk"
if ($chk -notmatch 'True') { Say 'GPU が使えません。ドライバーを更新してください。'; exit 1 }
Say "できました。学習: powershell -ExecutionPolicy Bypass -File tools\train_model.ps1"
