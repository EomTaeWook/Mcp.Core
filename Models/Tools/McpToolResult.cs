using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Models.Tools
{
    public class McpToolResult
    {
        private readonly string _pngData;
        private JsonObject _inputRequests;
        private string _requestState;
        public string Text { get; private set; }
        public JsonObject StructuredContent { get; private set; }
        public bool IsError { get; private set; }

        public McpToolResult(string text, bool isError = false)
        {
            ArgumentNullException.ThrowIfNull(text);
            Text = text;
            IsError = isError;
        }

        public McpToolResult(JsonObject content)
        {
            ArgumentNullException.ThrowIfNull(content);
            StructuredContent = (JsonObject)content.DeepClone();
            Text = StructuredContent.ToJsonString();
        }

        public McpToolResult(JsonObject content, byte[] pngBytes) : this(content)
        {
            ArgumentNullException.ThrowIfNull(pngBytes);
            _pngData = Convert.ToBase64String(pngBytes);
        }

        public static McpToolResult InputRequired(JsonObject inputRequests, string requestState)
        {
            ArgumentNullException.ThrowIfNull(inputRequests);
            if (inputRequests.Count == 0)
            {
                throw new ArgumentException("At least one input request is required.", nameof(inputRequests));
            }
            foreach (KeyValuePair<string, JsonNode> inputRequest in inputRequests)
            {
                if (inputRequest.Value is not JsonObject requestedInput)
                {
                    throw new ArgumentException("Each input request must be an object.", nameof(inputRequests));
                }
                if (requestedInput["method"]?.GetValueKind() != JsonValueKind.String)
                {
                    throw new ArgumentException("Each input request requires a method string.", nameof(inputRequests));
                }
                if (requestedInput["params"] is not JsonObject)
                {
                    throw new ArgumentException("Each input request requires a params object.", nameof(inputRequests));
                }
            }
            return new McpToolResult(string.Empty)
            {
                _inputRequests = (JsonObject)inputRequests.DeepClone(),
                _requestState = requestState
            };
        }

        internal JsonObject ToJson()
        {
            if (_inputRequests != null)
            {
                JsonObject inputRequired = new JsonObject
                {
                    ["resultType"] = "input_required",
                    ["inputRequests"] = _inputRequests.DeepClone()
                };
                if (_requestState != null)
                {
                    inputRequired["requestState"] = _requestState;
                }
                return inputRequired;
            }
            JsonObject result = new JsonObject()
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = Text }
                },
                ["isError"] = IsError
            };
            if (_pngData != null)
            {
                ((JsonArray)result["content"]).Add(new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = _pngData });
            }
            if (StructuredContent != null)
            {
                result["structuredContent"] = StructuredContent.DeepClone();
            }
            return result;
        }
    }
}
