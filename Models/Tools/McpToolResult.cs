using System.Text.Json.Nodes;

namespace MCP.Core.Models.Tools
{
    public class McpToolResult
    {
        private readonly string _pngData;
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

        internal JsonObject ToJson()
        {
            JsonObject result = new()
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
