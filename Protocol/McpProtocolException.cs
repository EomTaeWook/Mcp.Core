using System.Text.Json.Nodes;

namespace MCP.Core
{
    internal class McpProtocolException : Exception
    {
        public int Code { get; private set; }
        public int HttpStatus { get; private set; }
        public JsonObject Details { get; private set; }
        public JsonNode RequestId { get; set; }

        public McpProtocolException(int code, string message, int httpStatus = 400, JsonObject details = null)
            : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
            Details = details;
        }
    }
}
