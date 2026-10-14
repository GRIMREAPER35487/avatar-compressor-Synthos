# Changelog

All notable changes to Synthos Texture Analysis (`com.synthos.texture-analysis`). It is released together with Avatar Compressor (Synthos Edition) and always has the same version number. Versions follow [Semantic Versioning](https://semver.org/).

## [1.3.1] - 2026-10-13

No changes; released with Avatar Compressor 1.3.1.

## [1.3.0] - 2026-10-13

First release as its own package.

### Added

- **Shared texture code** for Avatar Compressor (Synthos Edition) and Synthos Scene Optimizer, moved out of Avatar Compressor 1.2.0 unchanged: detail scoring (CPU and GPU, separate normal map scoring), area-average resizing (CPU and GPU), format choice (DXT/BC5/BC7 on PC, ASTC on Quest) with the banding and blocky-edge check, normal map layout conversion, and loading the original image file instead of the imported copy. VCC installs it automatically with either tool.

### Fixed

- **Normal maps scored almost no detail** - Imported normal maps are stored packed (X in alpha, Y in green, red always full), but the detail score decoded them as plain RGB: X was ignored and every normal leaned the same way, so normal maps scored near 0% and always got the largest divisor. They are now decoded the way Unity's shaders do (packed, BC5 and RGB layouts), on the CPU and the GPU.
- **Detail under transparent pixels is no longer ignored when alpha is not transparency** - Pixels with alpha near 0 were left out of the detail score on every texture. Now that only happens when the caller says alpha is transparency (the main texture of a cutout or transparent material). On other textures alpha usually holds smoothness, a mask or other data while the color shows in full, so every pixel is scored.
