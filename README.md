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

Install [VRCFury](https://vrcfury.com/download) first, then download the latest release from [GitHub Releases](https://github.com/GRIMREAPER35487/avatar-compressor-Synthos/releases) and import the `.zip` file into your Unity project.

## Usage

Add optimization components to your avatar's root GameObject, configure settings, and build your avatar. All optimizations are applied automatically when the avatar is built or uploaded, in VRCFury Play Mode, and in VRCFury's "Build an Editor Test Copy". Your scene avatar and project assets are never modified: generated textures, materials and animation copies are written to `Assets/_AvatarCompressorTemp` and cleaned up automatically once nothing uses them.

## Recommended

Use this together with VRCFury: Avatar Compressor runs after VRCFury in the build, so textures on VRCFury-merged clothing and props, and materials swapped by VRCFury toggles, are compressed too.

## Credits & License

* **Original Library:** Developed by [Limitex](https://github.com/Limitex/avatar-compressor) under the [MIT License](LICENSE).
* **NDMF Removal & VRCFury Integration:** Maintained by [Synthos](https://github.com/GRIMREAPER35487).
