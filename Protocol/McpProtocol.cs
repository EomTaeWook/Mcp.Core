using System.Text.Json.Nodes;

namespace MCP.Core
{
    internal class McpProtocol
    {
        internal const string CurrentVersion = "2026-07-28";
        internal const string LegacyVersion = "2025-11-25";

        public McpProtocol()
        {
        }

        internal static bool IsLegacyVersion(string version)
        {
            return version == LegacyVersion || version == "2025-06-18" || version == "2025-03-26";
        }

        internal static JsonArray SupportedVersions()
        {
            return new JsonArray(CurrentVersion, LegacyVersion, "2025-06-18", "2025-03-26");
        }

        internal static JsonObject ToolCapabilities()
        {
            return new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } };
        }

        internal static JsonObject ServerInfo(McpServerOptions options)
        {
            return new JsonObject { ["name"] = options.Name, ["version"] = options.Version };
        }

        internal static JsonObject Response(JsonNode id, JsonObject result)
        {
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        }

        internal static JsonObject Error(JsonNode id, int code, string message, JsonObject details = null)
        {
            JsonObject error = new JsonObject { ["code"] = code, ["message"] = message };
            if (details != null)
            {
                error["data"] = details.DeepClone();
            }
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error };
        }
    }
}
