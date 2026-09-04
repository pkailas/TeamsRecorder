# =============================================================================
# TeamsRecorder sidecar setup — idempotent.
#
#   1. Creates sidecar\.venv (Python 3.13 on PATH, falls back to 3.12).
#   2. Upgrades pip.
#   3. Installs torch + torchaudio from the CUDA 12.8 wheel index.
#   4. Verifies torch sees CUDA and that sm_120 is in the arch list
#      (required for RTX PRO 6000 Blackwell); falls back to the cu130
#      nightly wheel if the cu128 build lacks sm_120.
#   5. Installs the rest from requirements.txt
#      (faster-whisper, pyannote.audio, nvidia-cublas-cu12, nvidia-cudnn-cu12).
#   6. Runs transcribe.py --selftest (loads both models on the GPU).
#
# Model weights land under G:\models (HF_HOME=G:\models\hf,
# whisper download_root=G:\models\whisper).
# =============================================================================

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Sidecar  = $PSScriptRoot
$Venv     = Join-Path $Sidecar ".venv"
$VenvPy   = Join-Path $Venv "Scripts\python.exe"

# Route HF downloads to G: for anything this script itself downloads.
$env:HF_HOME = "G:\models\hf"

function Say($m) { Write-Host "[setup] $m" -ForegroundColor Cyan }
function Ok($m)  { Write-Host "[setup] OK: $m" -ForegroundColor Green }
function Bad($m) { Write-Host "[setup] FAIL: $m" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# 1. venv
# ---------------------------------------------------------------------------
if (Test-Path $VenvPy) {
    Say "venv already exists at $Venv - reusing."
} else {
    $py = (Get-Command python -ErrorAction SilentlyContinue).Source
    if ($py) { Say "Creating venv with: $py" } else { Say "Creating venv with: py -3.12" }
    if ($py) {
        & python -m venv $Venv
    } else {
        & py -3.12 -m venv $Venv
    }
    if ($LASTEXITCODE -ne 0) { throw "venv creation failed." }
    Ok "venv created."
}

$pyv = & $VenvPy --version
Say "venv Python: $pyv"

# ---------------------------------------------------------------------------
# 2. pip upgrade
# ---------------------------------------------------------------------------
Say "Upgrading pip..."
& $VenvPy -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }

# ---------------------------------------------------------------------------
# 3. torch + torchaudio (cu128), then sm_120 check (cu130 nightly fallback)
# ---------------------------------------------------------------------------
function Test-TorchArch {
    # Returns $true when the venv's torch build supports sm_120 on CUDA.
    # (A function's return value is what's written to $Success; relying on
    # $LASTEXITCODE here would break if it was already 0 from an earlier cmdlet.)
    $check = @'
import sys, torch
assert torch.cuda.is_available(), "CUDA unavailable"
archs = torch.cuda.get_arch_list()
print(" ".join(archs))
sys.exit(0 if "sm_120" in archs else 1)
'@
    $Success = & $VenvPy -c $check
    return ($Success -eq $null) -or ($Success -eq 0)
}

Say "Installing torch + torchaudio from cu128 index (large download)..."
& $VenvPy -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
if ($LASTEXITCODE -ne 0) { throw "torch install from cu128 index failed." }

if (Test-TorchArch) {
    Ok "torch cu128 build supports sm_120."
} else {
    Say 'cu128 build lacks sm_120 - trying cu130 nightly build (large download)...'
    & $VenvPy -m pip uninstall -y torch torchaudio | Out-Null
    & $VenvPy -m pip install --pre torch torchaudio --index-url https://download.pytorch.org/whl/nightly/cu130
    if ($LASTEXITCODE -ne 0) { throw "torch install from cu130 nightly index failed." }
    if (Test-TorchArch) {
        Ok "torch cu130 nightly build supports sm_120."
    } else {
        Bad 'Neither cu128 nor cu130 nightly torch has sm_120 in get_arch_list(). STOP.'
        & $VenvPy -c @'
import torch
print(torch.__version__, torch.cuda.get_arch_list())
'@
        exit 1
    }
}

# ---------------------------------------------------------------------------
# 4. Everything else
# ---------------------------------------------------------------------------
Say "Installing requirements.txt (faster-whisper, pyannote, cuBLAS/cuDNN)..."
& $VenvPy -m pip install -r (Join-Path $Sidecar "requirements.txt")
if ($LASTEXITCODE -ne 0) {
    # If a package lacks a wheel for this Python, try rebuilding the venv
    # with 3.12 (only possible if we created a 3.13 venv).
    $has312 = & py -0 2>$null | Select-String "3\.12"
    if ($pyv -like "*3.13*" -and $has312) {
        Say 'A wheel may be missing for Python 3.13 - rebuilding venv with py -3.12...'
        Remove-Item -Recurse -Force $Venv
        & py -3.12 -m venv $Venv
        & $VenvPy -m pip install --upgrade pip | Out-Null
        & $VenvPy -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128 | Out-Null
        if (-not (Test-TorchArch)) {
            & $VenvPy -m pip uninstall -y torch torchaudio | Out-Null
            & $VenvPy -m pip install --pre torch torchaudio --index-url https://download.pytorch.org/whl/nightly/cu130
        }
        & $VenvPy -m pip install -r (Join-Path $Sidecar "requirements.txt")
        if ($LASTEXITCODE -ne 0) { throw "requirements install failed on Python 3.12 venv too." }
    } else {
        throw "requirements.txt install failed (and no 3.12 fallback was possible)."
    }
}
Ok "requirements installed."

# ---------------------------------------------------------------------------
# 5. Selftest
# ---------------------------------------------------------------------------
Say "Running selftest (downloads large-v3 ~3GB + pyannote models on first run)..."
& $VenvPy (Join-Path $Sidecar "transcribe.py") --selftest
if ($LASTEXITCODE -eq 0) {
    Ok "SELFTEST PASS"
    exit 0
} else {
    Bad "SELFTEST FAIL (exit $LASTEXITCODE)"
    exit 1
}
