using System;
using Newtonsoft.Json;

namespace BannerlordInspector
{
    /// <summary>
    /// JSON output.
    ///
    /// Everything handed to this is already a plain anonymous object or dictionary built by hand.
    /// TaleWorlds types are never serialized directly - a Hero references its Clan, which
    /// references its Heroes, and a general-purpose serializer walks that cycle until it dies or
    /// produces megabytes. What crosses the wire is only ever what a handler chose to expose.
    /// </summary>
    public static class Json
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Include,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            MaxDepth = 12
        };

        public static string Serialize(object value)
        {
            try
            {
                return JsonConvert.SerializeObject(value, Settings);
            }
            catch (Exception ex)
            {
                // Never let a serialization problem become an unhandled exception on a socket thread.
                return "{\"error\":\"serialization failed\",\"detail\":"
                       + JsonConvert.SerializeObject(ex.Message) + "}";
            }
        }
    }
}
