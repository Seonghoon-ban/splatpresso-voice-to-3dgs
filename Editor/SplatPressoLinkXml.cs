using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.UnityLinker;

namespace SplatPresso.EditorTools
{
    /// <summary>
    /// Preserves what SplatPresso reaches by reflection or by name when managed code stripping is on (IL2CPP /
    /// Stripping Level High): the capture reads the internal <c>GaussianSplatRenderer.m_GpuView</c> field (see
    /// GsInternals), GaussianSplatURPFeature is internal and only referenced from renderer assets, and every JSON
    /// type in SplatPresso's own assemblies (decision, verification, voice turn, ledger, path cache...) is created
    /// only through Newtonsoft reflection. Without this, High stripping removed the parameterless constructor of
    /// VoiceTurnResponse in a player build and every voice turn failed to deserialize.
    /// </summary>
    /// <remarks>
    /// A link.xml inside a package is not reliably picked up, so it is emitted here. It is written to a physical
    /// path under Library/: <c>Packages/&lt;name&gt;/...</c> is virtual for git installs (they live in
    /// Library/PackageCache/&lt;name&gt;@&lt;hash&gt;).
    /// </remarks>
    internal sealed class SplatPressoLinkXml : IUnityLinkerProcessor
    {
        const string kLinkXml =
            "<linker>\n" +
            "  <assembly fullname=\"GaussianSplatting\">\n" +
            "    <type fullname=\"GaussianSplatting.Runtime.GaussianSplatRenderer\" preserve=\"all\"/>\n" +
            "    <type fullname=\"GaussianSplatting.Runtime.GaussianSplatURPFeature\" preserve=\"all\"/>\n" +
            "  </assembly>\n" +
            "  <assembly fullname=\"SplatPresso.Runtime\" preserve=\"all\"/>\n" +
            "  <assembly fullname=\"SplatPresso.Mesh\" ignoreIfMissing=\"1\" preserve=\"all\"/>\n" +
            "</linker>\n";

        public int callbackOrder => 0;

        public string GenerateAdditionalLinkXmlFile(BuildReport report, UnityLinkerBuildPipelineData data)
        {
            string path = Path.GetFullPath(Path.Combine("Library", "SplatPresso", "link.xml"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, kLinkXml);
            return path;
        }
    }
}
