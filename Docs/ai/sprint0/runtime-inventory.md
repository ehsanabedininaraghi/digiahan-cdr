# Sprint 0 Runtime Inventory

## Host

| Item | Measured value |
|---|---|
| OS | Microsoft Windows 10 Pro 10.0.19045 |
| CPU | Intel Core i3-6100 @ 3.70 GHz |
| Logical processors | 4 |
| RAM | 15.9 GB |
| GPU | Intel HD Graphics 530, approximately 1 GB shared/adapter memory |
| C: free | 6.56 GB of 110.96 GB |
| D: free | 97.71 GB of 931.51 GB |

## Toolchain

| Tool | Status |
|---|---|
| .NET SDK | 8.0.423 and 10.0.302 installed |
| .NET 8 runtime | 8.0.29 installed |
| Python | Microsoft Store launcher present; usable runtime not installed/verified |
| CUDA / NVIDIA GPU | Not available |
| `nvidia-smi` | Not found |
| Docker | Not found |
| `ffmpeg` / `ffprobe` | Not found |

## Suitability finding

This host is suitable for the current CDR service and discovery work. It is **not approved as a local Whisper large-v3/pyannote production host**. CPU-only processing on this older four-thread CPU must be benchmarked before any claim, and C: has insufficient safety margin for model caches or audio staging.

Sprint 1/2 planning must identify a dedicated AI host or explicitly accept a measured low-throughput CPU mode.
