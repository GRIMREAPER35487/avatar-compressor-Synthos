<div align="center">
  <img src="https://raw.githubusercontent.com/GRIMREAPER35487/avatar-compressor-Synthos/main/.github/banner.png" alt="Avatar Compressor (Synthos Edition)" width="100%" />
</div>

<br/>

# Avatar Compressor (Synthos Edition)

A non-destructive avatar texture optimization utility for VRChat. Create lightweight avatars that drastically reduce VRAM usage and download size so more players can see you.

This repository is a customized edition of [Limitex's Avatar Compressor (LAC)](https://github.com/Limitex/avatar-compressor) (under the MIT License), tailored to run **completely independent of NDMF (*Non-Destructive Modular Framework*)** with native **VRCFury** and standard **VRCSDK** build pipeline support.

---

## What Was Changed?

1. **NDMF Dependency Completely Removed**
   - **In Stock Avatar Compressor:** You could only use it through NDMF (`nadena.dev.ndmf`), requiring extra third-party frameworks and forcing avatars through the NDMF build lifecycle.
   - **In Synthos Edition:** NDMF is completely stripped. It runs natively using VRChat's official avatar build pipeline callbacks (`IVRCSDKPreprocessAvatarCallback`).

2. **Native VRCFury Integration**
   - Implements `AvatarCompressorVrcfuryBuildHook` running at `callbackOrder => 10000`.
   - VRCFury executes at `callbackOrder => -10000`, so all modular clothing, toggles, props, and merged animators are already assembled before Avatar Compressor analyzes and compresses textures.

3. **Direct Animator & Controller Scanning**
   - Replaced NDMF's internal `AnimatorServicesContext` with direct avatar hierarchy and `VRCAvatarDescriptor` controller scanning.
   - Collects all animation clips (including FX, Gesture, Action layers and sub-state machines) to protect animated textures and update object curves to point to compressed textures and cloned materials.

4. **Self-Contained Localization & UI**
   - Bundled translations (English, Japanese, Korean, Simplified Chinese, Traditional Chinese) are parsed directly from PO files without requiring NDMF's localization system.
   - Works immediately in any Unity Editor project.

5. **Manual Editor Compression Tool**
   - Added `Tools > Avatar Compressor > Compress Selected Avatar` to allow creators to preview and test compression in the Editor without needing a full VRChat upload.

---

## What Is No Longer Needed?

- **NDMF (`nadena.dev.ndmf`) is NO LONGER required:** You do not need NDMF installed in your project.
- Works seamlessly with **VRCFury** and standard **VRCSDK3**.

---

## Features

### Texture Compressor
Analyzes and compresses avatar textures based on their complexity:
- **Complexity-based analysis** - Textures are analyzed to determine optimal compression levels.
- **Multiple analysis strategies** - Fast, HighAccuracy, Perceptual, and Combined modes.
- **Preset configurations** - Quick setup with 5 built-in presets (High Quality, Quality, Balanced, Aggressive, Maximum, Custom).
- **Texture type awareness** - Specialized handling for normal maps, emission maps, and alpha masks.
- **lilToon integration** - Optional baking of color adjustments, alpha masks, and pruning of unused texture slots.
- **Platform-specific formats** - Automatic format selection for PC (DXT/BC) and Quest (ASTC).
- **GPU Accelerated** - Uses GPU compute shaders for ultra-fast texture analysis and area-averaging resize with automatic CPU fallback.
- **VRAM estimation** - Inspect and preview estimated VRAM usage directly in the inspector.

---

## Requirements

- Unity 2022.3 (VRChat specified version)
- VRChat SDK Avatars 3.10.0 or later
- *(Optional)* VRCFury

---

## Installation via VPM (VRChat Creator Companion / ALCOM)

Add the Synthos package repository to VCC / ALCOM:
```
https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json
```
Then add **Avatar Compressor (Synthos Edition)** to your project.

---

## Usage

1. Add the `TextureCompressor` component to your avatar's root GameObject.
2. Select your desired compression preset (e.g. *Balanced* or *Quality*).
3. Test or upload your avatar:
   - **Automatic:** When uploading via the VRChat SDK (or testing in Play Mode with VRCFury), textures are automatically compressed and applied during the pre-upload process without modifying your original project files.
   - **Manual:** Use `Tools > Avatar Compressor > Compress Selected Avatar` from the top menu bar to test on an avatar instance in your scene.

---

## License

[MIT License](LICENSE)

## Original Author

- Original LAC tool by [Limitex](https://github.com/Limitex)
- Decoupled and modified for VRCFury by [Synthos](https://github.com/GRIMREAPER35487)
