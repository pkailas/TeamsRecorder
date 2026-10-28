# TeamsRecorder GPU transcription service.
#
# Same Python + CUDA stack proven on BEAUTY in ~/tr-test (RTX PRO 4000 Blackwell,
# sm_120): python:3.13-slim + torch cu128. Keep the two RUN lines below
# byte-identical to ~/tr-test/Dockerfile so the build reuses that layer cache.
# ctranslate2 must use float16 — int8/int8_float16 crash with
# CUBLAS_STATUS_NOT_SUPPORTED on sm_120 (transcribe.py hard-codes float16).
FROM python:3.13-slim
RUN pip install --no-cache-dir torch==2.11.0 torchaudio==2.11.0 --index-url https://download.pytorch.org/whl/cu128
RUN pip install --no-cache-dir faster-whisper==1.2.1 ctranslate2==4.8.2 pyannote.audio==4.0.7 soundfile==0.14.0 numpy==2.5.2 nvidia-cublas-cu12 nvidia-cudnn-cu12

# HTTP layer.
RUN pip install --no-cache-dir fastapi==0.162.0 uvicorn==0.54.0 python-multipart==0.0.32

ENV PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
# cuBLAS 12 / cuDNN 9 ship inside the pip wheels, not in the system library
# path — ctranslate2 and torch load them from site-packages/nvidia/*/lib
# (the same LD_LIBRARY_PATH ~/tr-test/run.sh passed to docker run).
ENV LD_LIBRARY_PATH=/usr/local/lib/python3.13/site-packages/nvidia/cublas/lib:/usr/local/lib/python3.13/site-packages/nvidia/cudnn/lib

# Non-root uid 1000 = BEAUTY's pkailas uid: the read-only model tree is
# readable, /tmp is writable, and GPU access is by device permissions, not by
# root, so CUDA works fine as this user.
RUN useradd --create-home --uid 1000 trsvc

WORKDIR /app
# Build context is the repo root (see service/docker-compose.yml), so the
# sidecar is available next to the service.
COPY service/app.py /app/app.py
COPY sidecar/transcribe.py /app/sidecar/transcribe.py
COPY sidecar/meeting_template.html /app/sidecar/meeting_template.html
COPY sidecar/library_template.html /app/sidecar/library_template.html

USER trsvc
EXPOSE 8095
CMD ["python", "-u", "/app/app.py"]
