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

        public virtual void OnReload() { }
        public virtual void Changed() { }
        public virtual void CopyFrom(PluginConfig other)
        {
            DeleteOnClose = other.DeleteOnClose;
            ShowDestinationPrompt = other.ShowDestinationPrompt;
        }
    }
}
