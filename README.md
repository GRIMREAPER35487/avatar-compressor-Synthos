<div align="center">
  <img src="https://raw.githubusercontent.com/GRIMREAPER35487/avatar-compressor-Synthos/main/.github/banner.png" alt="Avatar Compressor (Synthos Edition)" width="100%" />
</div>

<br/>

# Avatar Compressor (Synthos Edition)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![GitHub release](https://img.shields.io/github/v/release/GRIMREAPER35487/avatar-compressor-Synthos)](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos/releases/latest)
[![GitHub Downloads (latest)](https://img.shields.io/github/downloads/GRIMREAPER35487/avatar-compressor-Synthos/latest/total?label=downloads%40latest)](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos/releases/latest)
[![GitHub Downloads (total)](https://img.shields.io/github/downloads/GRIMREAPER35487/avatar-compressor-Synthos/total?label=downloads%40total)](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos/releases)

A non-destructive avatar optimization utility for VRChat. Create lightweight avatars that more players can see.

This is the Synthos Edition of [Limitex's Avatar Compressor](https://github.com/Limitex/avatar-compressor), rebuilt to run on the VRChat SDK build pipeline with VRCFury instead of NDMF.

## Features

### Texture Compressor

Analyzes and compresses avatar textures based on their complexity.

- **Complexity-based analysis** - Textures are analyzed to determine optimal compression levels
- **Multiple analysis strategies** - Fast, HighAccuracy, Perceptual, and Combined modes
- **Preset configurations** - Quick setup with 5 built-in presets
- **Texture type awareness** - Specialized handling for normal maps, emission maps, and more
- **Platform-specific formats** - Automatic format selection for PC (DXT/BC) and Quest (ASTC)
- **High-quality compression** - BC7/ASTC_4x4 for high complexity textures
- **Memory estimation** - Preview estimated VRAM usage before building
- **Shared texture optimization** - Shared textures are processed once and reused

## Requirements

- Unity 2022.3.22f1 (VRChat specified version)
- VRChat SDK Avatars 3.10.0 or later
- [VRCFury](https://vrcfury.com/) 1.1298.0 or later
- Synthos Texture Analysis (installed automatically, same version as this package)

This edition does not use NDMF. It runs as a VRChat SDK build hook, after VRCFury has merged its clothing, props and controllers.

VRCFury is installed automatically as a dependency, but its package repository must be added to VCC/ALCOM first: `https://vcc.vrcfury.com`

## Installation

### Via ALCOM (Recommended)

1. Open [ALCOM](https://vrc-get.anatawa12.com/alcom/)
2. Add the VRCFury repository: `https://vcc.vrcfury.com`
3. Add the repository: `https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json`
4. Add **Avatar Compressor (Synthos Edition)** to your project

### Via VRChat Creator Companion

1. Open VRChat Creator Companion
2. Add the VRCFury repository: `https://vcc.vrcfury.com`
3. Add the repository: `https://grimreaper35487.github.io/Synthos-VRC-Packages/index.json`
4. Add **Avatar Compressor (Synthos Edition)** to your project

### Manual Installation

Install [VRCFury](https://vrcfury.com/download) first, then download the latest release from [GitHub Releases](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos/releases). Each release has two `.zip` files: `com.synthos.avatar-compressor` and `com.synthos.texture-analysis`. Import both into your Unity project.

## Usage

Add optimization components to your avatar's root GameObject, configure settings, and build your avatar. All optimizations are applied automatically when the avatar is built or uploaded, in VRCFury Play Mode, and in VRCFury's "Build an Editor Test Copy". Your scene avatar and project assets are never modified: generated textures, materials and animation copies are written to `Assets/_AvatarCompressorTemp` and cleaned up automatically once nothing uses them.

## Source Textures

By default the compressor works from a full-quality copy of each texture's image file, so every texture is compressed exactly once. The copies are made the first time a texture is built and cached in `Library/AvatarCompressorCache`; your import settings are never changed. You can turn this off or clear the cache in **Edit > Preferences > Avatar Compressor**.

Two options in the component's **Source & Quality** section decide how much the avatar's import settings matter:

- **Respect import Max Size** (on): the texture's import Max Size is the starting size. Turn off to start from the source file instead (fit to Max Resolution). The imported size stays the default: a texture only comes out larger than it was imported when it has real detail and, where its on-avatar size can be measured, the imported size is below the target sharpness for that surface. The build log says why each texture grew or stayed. The size filters check the source file's size too; turn off **Size filters check source size** to have them check the imported size instead, which skips textures imported small and leaves them exactly as imported. To keep those textures at their imported size but still pick their format, turn on **Still optimize format of filtered textures** under the size filters.
- **Respect import format** (on): keep the compression format each texture was imported with. Turn off to let the compressor pick the format from each texture's detail and alpha.

With both off, results no longer depend on how the textures were imported.

The same section has **Avoid banding and blocky edges** (on). When the compressor would pick DXT1/DXT5 (ASTC 6x6/8x8 on Quest), it test-compresses the texture's smooth, clean areas first; if gradients would show stair-stepping or clean edges would turn blocky, it uses BC7 instead. On Quest it steps through finer ASTC block sizes (8x8, 6x6, 5x5, 4x4) and uses the first one that stays clean, so a texture only pays for as much quality as it needs. Textures that get upgraded use more memory but stay clean.

## On-Avatar Size

**Limit by on-avatar size** (on) looks at how large the surface each texture covers is on the avatar and shrinks textures that are sharper than anyone could see, like a 2048 texture on an earring. Each triangle using the texture is measured in the avatar's current pose, and the texture keeps enough pixels for:

- the head (face, eyes, hair, anything the Head bone moves) viewed from **Face view distance** (15 cm: a face pressed up to a mirror), and
- the rest of the avatar viewed from **Body view distance** (30 cm: looking at your hands or a close-up photo).

On its own it only ever makes a texture smaller than the detail analysis would, and frozen textures keep their settings. **Keep detail where it is visible** (on) adds the other direction: a detailed texture whose surface is already less sharp than it needs (usually skin and clothing, which score low on detail because they are smooth) is not shrunk further by the detail analysis. Textures that score below the low-detail threshold keep visible detail in proportion to their score (a texture just under it keeps nearly all of it), so only flat colors and similar textures shrink freely. Min Divisor still applies. Matcaps, ramps, gradients, decals, flipbooks, textures on other UV sets and textures only used through animations are not limited. The preview shows each texture's pixels per cm before and after.

## Recommended

For Quest avatars, the **Quality** preset with **Respect import format** off is a good starting point: in testing, a full avatar went from 55 MB to about 13 MB of texture memory while body textures stayed sharp up close.

Use this together with VRCFury: Avatar Compressor runs after VRCFury in the build, so textures on VRCFury-merged clothing and props, and materials swapped by VRCFury toggles, are compressed too.

## Credits & License

* **Original Library:** Developed by [Limitex](https://github.com/Limitex/avatar-compressor) under the [MIT License](LICENSE).
* **NDMF Removal & VRCFury Integration:** Maintained by [Synthos](https://github.com/GRIMREAPER35487).
