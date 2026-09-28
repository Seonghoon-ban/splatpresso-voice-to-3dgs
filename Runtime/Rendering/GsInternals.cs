// SPDX-License-Identifier: MIT

using System.Reflection;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace SplatPresso.Rendering
{
    /// <summary>
    /// The only place that touches non-public members of aras-p/UnityGaussianSplatting. Written against
    /// commit 2c6fed37 (v1.1.1 + composite fix), the version the package installs.
    /// </summary>
    /// <remarks>
    /// The capture's splat-depth pass needs each renderer's per-splat view data (<c>m_GpuView</c>, internal,
    /// StructuredBuffer of 40-byte SplatViewData filled by the GS pass for the camera being rendered). Nothing
    /// else is needed: the capture owns its quad index buffer and does not need the sort order (depth with
    /// ZTest is order-independent). IL2CPP keeps field metadata; the editor also emits a link.xml that
    /// preserves GaussianSplatRenderer so managed stripping cannot remove it.
    /// </remarks>
    public static class GsInternals
    {
        /// <summary>The GaussianSplatting commit this code was verified against.</summary>
        public const string VerifiedGsCommit = "2c6fed37da67a217367261fcfcd3316d34c73e76";

        const BindingFlags kInstNonPublic = BindingFlags.Instance | BindingFlags.NonPublic;

        // GaussianSplatRenderer.cs:262  internal GraphicsBuffer m_GpuView  (StructuredBuffer<SplatViewData>, stride 40)
        // (immutable reflection cache, safe across play sessions)
        static readonly FieldInfo s_GpuView = typeof(GaussianSplatRenderer).GetField("m_GpuView", kInstNonPublic);

        /// <summary>Checks that the installed GaussianSplatting exposes the internals this package relies on.</summary>
        /// <param name="error">Human-readable reason on failure, else null.</param>
        public static bool Validate(out string error)
        {
            if (s_GpuView == null || s_GpuView.FieldType != typeof(GraphicsBuffer))
            {
                error = "GaussianSplatting version mismatch: GaussianSplatRenderer.m_GpuView (internal GraphicsBuffer) not found. " +
                        "SplatPresso needs org.nesnausk.gaussian-splatting at commit " + VerifiedGsCommit.Substring(0, 8) +
                        " (SplatPresso > Install or Repair Dependencies).";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// The renderer's per-splat view data buffer, or null if unavailable. Only valid for the camera currently
        /// being rendered, after the GS render pass (BeforeRenderingTransparents) of that camera.
        /// </summary>
        public static GraphicsBuffer GetViewBuffer(GaussianSplatRenderer r)
        {
            if (r == null || s_GpuView == null)
                return null;
            return s_GpuView.GetValue(r) as GraphicsBuffer;
        }
    }
}
