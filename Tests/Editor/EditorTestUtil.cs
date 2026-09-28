using System;
using System.IO;
using NUnit.Framework;

namespace SplatPresso.Tests
{
    /// <summary>Shared helpers of the EditMode tests: fixture lookup and scratch folders.</summary>
    internal static class EditorTestUtil
    {
        const string kPackageName = "com.splatpresso.voice-to-3dgs";

        /// <summary>Absolute path of <c>Tests/Runtime/Fixtures</c> of this package, or null when it cannot be found.</summary>
        public static string FixturesDir
        {
            get
            {
                string root = null;
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(EditorTestUtil).Assembly);
                if (info != null && !string.IsNullOrEmpty(info.resolvedPath))
                    root = info.resolvedPath;
                else if (Directory.Exists(Path.Combine("Packages", kPackageName)))
                    root = Path.GetFullPath(Path.Combine("Packages", kPackageName));
                if (root == null)
                    return null;
                string dir = Path.Combine(root, "Tests", "Runtime", "Fixtures");
                return Directory.Exists(dir) ? dir : null;
            }
        }

        /// <summary>Path of a fixture file; marks the test inconclusive when it is missing.</summary>
        public static string RequireFixture(string fileName)
        {
            string dir = FixturesDir;
            if (dir == null)
                Assert.Inconclusive($"Fixtures folder of {kPackageName} not found (Tests/Runtime/Fixtures).");
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path))
                Assert.Inconclusive($"Fixture {fileName} is missing. Regenerate the fixtures with: python Tools~/make_fixtures.py");
            return path;
        }

        /// <summary>Creates a fresh scratch folder under the OS temp directory.</summary>
        public static string NewTempDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SplatPressoTests", tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Best-effort recursive delete.</summary>
        public static void DeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // a locked temp file must not fail the test
            }
        }
    }
}
