using System.Runtime.CompilerServices;
using IPA.Config.Stores;
using IPA.Config.Stores.Attributes;
using IPA.Config.Stores.Converters;

// Allow config to be generated in project dir
[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]
namespace ZipSaber
{
    internal class PluginConfig
    {
        public static PluginConfig Instance { get; set; }

        [NonNullable]
        /// <summary>Delete WIP maps imported this session when the game closes.</summary>
        public virtual bool DeleteOnClose { get; set; } = false;

        [NonNullable]
        /// <summary>Show a prompt asking whether to put dropped maps in CustomWipLevels or CustomLevels.</summary>
        public virtual bool ShowDestinationPrompt { get; set; } = true;

        /// <summary>Mod Manager sort order (one of the ModManagerViewController.Sort* labels).</summary>
        public virtual string ModSortMode { get; set; } = "Name (A-Z)";

        /// <summary>Mod Manager accent colour as #RRGGBB.</summary>
        public virtual string AccentColor { get; set; } = "#5FA8E8";

        /// <summary>Custom folder for dropped WIP maps. Empty = Beat Saber_Data/CustomWipLevels.</summary>
        public virtual string CustomWipPath { get; set; } = "";

        public virtual void OnReload() { }
        public virtual void Changed() { }
        public virtual void CopyFrom(PluginConfig other)
        {
            DeleteOnClose = other.DeleteOnClose;
            ShowDestinationPrompt = other.ShowDestinationPrompt;
            ModSortMode = other.ModSortMode;
            AccentColor = other.AccentColor;
            CustomWipPath = other.CustomWipPath;
        }
    }
}
