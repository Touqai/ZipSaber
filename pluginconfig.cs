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
        public virtual bool DeleteOnClose { get; set; } = false; // Default: Do not delete

        public virtual void OnReload() { }
        public virtual void Changed() { }
        public virtual void CopyFrom(PluginConfig other)
        {
            // Manually copy virtual properties if needed
            DeleteOnClose = other.DeleteOnClose;
        }
    }
}