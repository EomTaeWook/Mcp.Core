using System.Text.Json.Nodes;

namespace MCP.Core.Protocol
{
    internal class McpProtocolException(int code, string message, int httpStatus = 400, JsonObject details = null) : Exception(message)
    {
        public int Code { get; private set; } = code;
        public int HttpStatus { get; private set; } = httpStatus;
        public JsonObject Details { get; private set; } = details;
        public JsonNode RequestId { get; set; }
    }
}
