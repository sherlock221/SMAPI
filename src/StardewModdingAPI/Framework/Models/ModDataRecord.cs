using System.Linq;
using Newtonsoft.Json;
using StardewModdingAPI.Framework.Serialisation;

namespace StardewModdingAPI.Framework.Models
{
    /// <summary>Metadata about a mod from SMAPI's internal data.</summary>
    internal class ModDataRecord
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The unique mod identifier.</summary>
        [JsonConverter(typeof(SFieldConverter))]
        public ModDataID ID { get; set; }

        /// <summary>The mod name.</summary>
        public string Name { get; set; }

        /// <summary>The URLs the user can check for a newer version.</summary>
        public string[] UpdateUrls { get; set; }

        /// <summary>The compatibility of given mod versions (if any).</summary>
        [JsonConverter(typeof(SFieldConverter))]
        public ModCompatibility[] Compatibility { get; set; } = new ModCompatibility[0];


        /*********
        ** Public methods
        *********/
        /// <summary>Get the compatibility record for a given version, if any.</summary>
        /// <param name="version">The mod version to check.</param>
        public ModCompatibility GetCompatibility(ISemanticVersion version)
        {
            return this.Compatibility.FirstOrDefault(p => p.MatchesVersion(version));
        }
    }
}
