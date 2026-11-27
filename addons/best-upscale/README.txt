Best quality add-on (optional, NVIDIA CUDA, 12 GB VRAM or more).

Runs SeedVR2 (Apache-2.0) and, once its licence is verified at the pin, FlashVSR through PyTorch.
FlashVSR stays disabled in the Host until its licence field in models.json is verified.
Protocol: one JSON object per line on stdin (commands) and stdout (events). Logs go to stderr.
Commands: probe, upscale, cancel, quit. Events: ready, probe, progress, segment_done, cancelled, error.
The worker only writes new files (never overwrites) and deletes only its own *.png.part temp files.
