# Third Party Notices

This package contains third-party code, uses third-party packages that the Unity Package Manager installs separately,
and calls third-party models as remote services. Each is listed below with its license or terms.

## 1. Adapted source code

### aras-p/UnityGaussianSplatting

- Project: https://github.com/aras-p/UnityGaussianSplatting
- License: MIT (full text below)
- Copyright (c) 2023 Aras Pranckevičius

Adapted into this package (renamed types and namespace, modified; each file keeps its SPDX header and an
"Adapted from aras-p/UnityGaussianSplatting (MIT)" note):

| File in this package | Adapted from (upstream `package/`) | Notes |
|---|---|---|
| `Runtime/Rendering/IO/SplatInputData.cs` | `Editor/Utils/GaussianFileReader.cs` (`InputSplatData`) | Same 62-float layout; moved to a runtime assembly |
| `Runtime/Rendering/IO/SplatFileReader.cs` | `Editor/Utils/GaussianFileReader.cs` | PLY only; fixed a native-buffer leak on error paths |
| `Runtime/Rendering/IO/SplatPlyReader.cs` | `Editor/Utils/PLYFileReader.cs` | |
| `Runtime/Rendering/RuntimeSplatAssetFactory.cs` | `Editor/GaussianSplatAssetCreator.cs` | Data layout, Morton reorder and color-texture swizzle, rebuilt for runtime use through the public `GaussianSplatAsset` API |
| `Runtime/Shaders/SplatPressoSplatDepth.shader` | `Shaders/RenderGaussianSplats.shader` | Splat quad vertex math reused for a depth-only pass; includes `GaussianSplatting.hlsl` from the installed renderer package at compile time |

The renderer package itself (`org.nesnausk.gaussian-splatting`) is **not** redistributed. It is installed into the user's
project from its own repository (or from OpenUPM) under its own license, and this package uses it without modification.

```
MIT License

Copyright (c) 2023 Aras Pranckevičius

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## 2. Packages installed by the Unity Package Manager (not redistributed)

These are declared as dependencies in `package.json`, or installed on request, and are downloaded by the Unity
Package Manager under their own licenses:

- Unity packages: Universal Render Pipeline, Newtonsoft Json (`com.unity.nuget.newtonsoft-json`), Burst, Collections,
  Mathematics, and the built-in modules (audio, imageconversion, unitywebrequest, imgui).
- Optional: glTFast (`com.unity.cloud.gltfast`) for Mesh mode; Input System (`com.unity.inputsystem`) if the project uses it.
- `org.nesnausk.gaussian-splatting` (see section 1).

NativeWebSocket is **not** used. The optional OpenAI Realtime voice backend uses .NET's built-in
`System.Net.WebSockets.ClientWebSocket`.

## 3. Remote services (called over the network, not redistributed)

The package sends requests to these models through the GenPresso API (`https://genpresso.ai/api/v1`) or, when configured,
directly through fal.ai (`https://queue.fal.run`) or OpenAI. No model code or weights are included in this package.
Use of the services and of the content they generate is subject to the terms of GenPresso and of the respective providers.

| Model | Used for |
|---|---|
| TripoSplat (VAST / Tripo, `tripo3d/triposplat`) | Image to 3D Gaussian Splat |
| Rodin v2.5 (Hyper3D) | Image / text to 3D mesh (Mesh mode) |
| nano-banana (Google) | Image edit, object image enhancement, text to image |
| SAM-3 (Meta) | Object segmentation |
| BiRefNet | Background removal (segmentation fallback) |
| Depth Anything V2 | Relative depth of the edited image |
| Gemini (Google) | Speech understanding, placement decisions, verification |
| OpenAI Realtime (optional) | Spoken voice replies |
