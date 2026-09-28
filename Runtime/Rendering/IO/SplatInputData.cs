// SPDX-License-Identifier: MIT
// Adapted from aras-p/UnityGaussianSplatting (MIT), Editor/Utils/GaussianFileReader.cs (InputSplatData).
// Upstream keeps this type in its editor-only assembly, so players cannot use it; this is a runtime copy
// under a distinct name (a same-named type would be ambiguous next to forks that moved it to Runtime).

using UnityEngine;

namespace SplatPresso.Rendering.IO
{
    /// <summary>
    /// One gaussian splat as read from a splat file. The layout is 62 floats (248 bytes) and must not change:
    /// <see cref="SplatFileReader"/> copies file attributes into it by float offset, and the SH reorder
    /// hard-codes the SH block at float offset 9.
    /// </summary>
    /// <remarks>
    /// After <see cref="SplatFileReader.ReadFile"/> the values are linearized: <c>rot</c> is packed to the
    /// smallest-3 0..1 form, <c>scale</c> is linear, <c>dc0</c> is a color and <c>opacity</c> is 0..1.
    /// </remarks>
    public struct SplatInputData
    {
        /// <summary>Number of floats in the struct (the file-attribute mapping relies on it).</summary>
        public const int kFloatCount = 62;

        /// <summary>Position (file space).</summary>
        public Vector3 pos;
        /// <summary>Normal (unused by the renderer).</summary>
        public Vector3 nor;
        /// <summary>Base color (SH band 0); linearized to 0..1 color.</summary>
        public Vector3 dc0;
        /// <summary>Higher-order SH coefficients as RGB triplets (zero when the file has none).</summary>
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        /// <summary>Opacity; linearized through a sigmoid to 0..1.</summary>
        public float opacity;
        /// <summary>Scale; linearized (exp) to linear units.</summary>
        public Vector3 scale;
        /// <summary>Rotation; linearized to the packed smallest-3 0..1 form.</summary>
        public Quaternion rot;
    }
}
