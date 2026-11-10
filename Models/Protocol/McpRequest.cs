using System.Text.Json.Nodes;

namespace MCP.Core.Models.Protocol
{
    public class McpRequest
    {
        public JsonNode Id { get; set; }
        public string Method { get; set; } = string.Empty;
        public JsonObject Parameters { get; set; } = new JsonObject();
        public bool IsNotification { get; set; }
        public bool IsModern { get; set; }
        internal bool IsResponse { get; set; }
        internal JsonObject Response { get; set; }
        internal string SessionId { get; set; } = string.Empty;
        internal JsonObject LegacyClientCapabilities { get; set; }
        internal bool IsInputContinuation { get; set; }
        internal JsonObject InputResponses { get; set; }
        internal string RequestState { get; set; } = string.Empty;

        public McpRequest()
        {
        }
    }
}
