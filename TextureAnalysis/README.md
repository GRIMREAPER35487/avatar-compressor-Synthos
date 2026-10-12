# Synthos Texture Analysis

Shared texture code used by [Avatar Compressor (Synthos Edition)](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos) and [Synthos Scene Optimizer](https://github.com/GRIMREAPER35487/SynSceneOptimiser). You don't need to install it yourself: VCC adds it when you install either tool.

It contains:

- **Detail scoring**: how much detail a texture has, on the CPU or GPU, with separate handling for normal maps.
- **Resizing**: area-average downscaling on the CPU or GPU.
- **Format choice**: DXT/BC7 for PC and ASTC for Quest, including the check that moves a texture to a safer format when the cheap one would band.
- **Source files**: reading the original image file instead of the imported copy, so textures can be rebuilt from full quality.

It is released together with Avatar Compressor and always has the same version number.

Based on [avatar-compressor](https://github.com/Limitex/avatar-compressor) by Limitex, MIT licensed. See [LICENSE](LICENSE).
