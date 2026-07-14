# Yan-K Avatar Toolbox (YAT)

A collection of Unity editor tools for streamlining VRChat avatar authoring.

> This repository was previously **Yan-K Material Editor (YME)** and has been renamed to **Yan-K Avatar Toolbox (YAT)** to reflect its expanded scope.

<table>
  <tr>
    <td><img src="demo1.png" width="400"></td>
    <td><img src="demo2.png" width="400"></td>
  </tr>
  <tr>
    <td><img src="demo3.png" width="400"></td>
    <td><img src="demo4.png" width="400"></td>
  </tr>
</table>

## Tools Included

### Yan-K Material Editor (YME)

Edit Materials and Textures in bulk.

- **Bulk Material & Texture Management** — List, replace, clone, and reset materials and textures across all child renderers
- **Search & Filter** — Quickly find materials by name or shader, and textures by name or property
- **Batch Operations** — Select multiple items and clone, replace, or reset them all at once
- **Modified Indicator** — Visual highlight on items that have been changed from their original
- **Include Inactive** — Optionally scan inactive GameObjects, with persistent toggle across sessions
- **Confirmation Dialogs** — Destructive batch resets require confirmation to prevent accidents
- **Undo Support** — All operations are fully undoable

### Yan-K Blendshape Editor (YBE)

Drive 600+ blendshapes without losing your mind. 

- **Auto Group Detection** — Parses separator rows like `===Eye===` / `---Mouth---` into wrapping, clickable group tabs
- **Search & Filter** — Instantly narrow down a long blendshape list by substring
- **Batch Value Slider** — Real-time drive every selected blendshape with one slider, collapsed into a single Undo step
- **Shift-Click Range Select** — Explorer-style range selection on the row checkboxes
- **Reset to Zero / Default** — One-click reset with confirmation
- **Export as AnimationClip** — Export all / non-zero / custom-selected blendshapes to a reusable `.anim`
- **Import AnimationClip (4 modes)** — Overlay / Reset Zero / Reset Default / Custom, with live preview before committing
- **Remap Missing Blendshapes** — Searchable, grouped dropdown plus fuzzy-name auto-match for clips authored on a different avatar
- **Undo Support** — Every commit registers proper Undo

### Yan-K Scene Controller (YSC)

Control avatar, camera, lighting, and post-processing from a dedicated editor window — works in both Edit Mode and Play Mode.

- **Avatar Control** — Move avatar on X/Y/Z sliders or use Auto Move (ping-pong / circle paths)
- **Camera Modes** — Orbit mode (pivot around the avatar bone) and Free Fly mode (6-DOF); right-click to aim, WASD/QE to move
- **Custom Cameras** — Save named camera positions as scene or global presets; custom cameras follow the avatar automatically
- **Scene Control** — Flat colour or Custom skybox; Fog controls
- **Directional Light** — Horizontal / Vertical angle sliders with built-in presets (Normal, Backlight, Frontlight)
- **Rotating Point Lights** — 5 individually coloured point lights orbiting the avatar
- **Post Processing** — Volume auto-attached to the active camera; browsable profile list with add / clone / remove
- **Undo Support** — All operations are fully undoable

### Yan-K Smart Package (YSP)

Dependency-aware `.unitypackage` exporter and importer in one tabbed window.

- **Dependency-Aware Export** — Tri-state asset tree built from `AssetDatabase` dependencies, with missing-reference detection
- **Filters & Exclusions** — Search, sort, type filter, exclude by extension or .NET Regex name patterns
- **Folder Collection Modes** — KeepStructure / AutoOrganize / SingleFolder / Custom, in either Non-Destructive (path remap on export) or Destructive (project move + restore manifest) mode
- **Custom Bucket Overrides** — Per-folder or per-asset bucket assignment with inheritance, batch tools, and persistent overrides
- **Multi-File Importer** — Drag-and-drop multiple `.unitypackage` files, preview entries, partial import with original GUIDs preserved
- **Conflict Detection** — Per-entry New / Update / Path conflict / GUID conflict status with Overwrite / Skip / Ask policy

### NonToon Converter

- **Convert lilToon shader to NonToon**

### Shared

- **Localization** — English, 简体中文, 繁體中文, 日本語, 한국어
- **Theme Aware** — Adapts to both dark and light editor themes

## Installation

- Add to VCC via [VPM Listing from Explosive Theorem Lab.](https://xtlcdn.github.io/vpm/).
- Download .unitypackage from [Release](https://github.com/Yan-K/AvatarToolbox/releases) and import to Unity.

## Changelog

### v0.1.0 - 2024/11/27

Inital Release.

### v0.2.0 - 2026/04/06

Added Clone, Reset, Batch Selection, Renderer Foldout.

### v0.3.0 - 2026/04/07

Added Texture Mode.

### v0.3.1 - 2026/04/10

Added Total Number for Materials and Textures.

### v0.4.0 - 2026/04/10

UX Overhaul.

### v0.4.1 - 2026/04/10

Fixed suffix in clone.

### v0.4.2 - 2026/04/11

Fixed modified list card style.

### v1.0.0 - 2026/04/11

UI/UX Unified, overall cleanup, changed language format.

### v1.1.0 - 2026/04/22

Repository renamed from **Yan-K Material Editor** to **Yan-K Avatar Toolbox**.
Added **Yan-K Blendshape Editor (YBE)**.

### v1.2.0 - 2026/04/25

Added **Yan-K Scene Controller (YSC)**

### v1.3.0 - 2026/05/04

YSC Change: PP profiles copied to `Assets/Yan-K/PostProcessingProfiles` and survive package updates. Added Reflection Probe section. Middle mouse camera pan.

YME Bug fix: Material merge internally while list card is not.

### v1.3.1 - 2026/05/04

YSC Bug fix: Fix default free fly camera settings.

### v1.4.0 - 2026/05/21

Added **Yan-K Smart Package (YSP)**.

### v1.4.1 - 2026/05/21

YSP Change: Default export package name fixed.

### v1.5.0 - 2026/06/05

YSC Change: Added external camera script blocker.
YSP Change: Better conflict display, UI QoL update, Multi-thread import.

### v1.5.1 - 2026/06/07

YSC Change: Fixed import count error.

### v1.5.2 - 2026/06/07

YSC Change: Importer icon fix. Exporter dependency toggle.

### v1.5.3 - 2026/06/28

YSC Bug fix: GUI Null Exception after import.

### v1.6.0 - 2026/07/11

Added **NonToon Converter**

### v1.6.1 - 2026/07/12

YNC Bug fix: Fix MatCap Add/Multiply detection.

### v1.7.0 - 2026/07/14

Added online update check.
YNC Bug fix: better emission mask and matcap detection, fix drag and drop ui height.

### v1.7.1 - 2026/07/14

YNC Change: added Fur convert toggle.

## Credit

- Yan-K ([@YanKMW](https://github.com/Yan-K))
- Vistanz ([@JLChnToZ](https://github.com/JLChnToZ)) for VPM Listing
