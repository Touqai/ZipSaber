using System.Collections.Generic;

namespace ZipSaber
{
    /// <summary>Data about a single installed BSIPA plugin.</summary>
    internal class ModInfo
    {
        internal string Id          { get; set; }
        internal string Name        { get; set; }  // display name (falls back to Id)
        internal string Version     { get; set; }
        internal string DllPath     { get; set; }
        internal string ManifestPath { get; set; } // companion .manifest file if present, else null

        /// <summary>IDs of mods this mod declares it needs (from its own dependsOn).</summary>
        internal List<string> DependsOn { get; set; } = new List<string>();

        /// <summary>IDs of mods that depend ON this mod (reverse map, computed by ModRegistry).</summary>
        internal List<string> RequiredBy { get; set; } = new List<string>();

        internal string DisplayLabel => string.IsNullOrEmpty(Name) ? Id : Name;
    }
}
