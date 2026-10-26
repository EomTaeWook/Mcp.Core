using System.Text.Json.Nodes;

namespace MCP.Core.Models.Tools
{
    public class McpToolResult
    {
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

        internal JsonObject ToJson()
        {
            JsonObject result = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = Text }
                },
                ["isError"] = IsError
            };
            if (StructuredContent != null)
            {
                result["structuredContent"] = StructuredContent.DeepClone();
            }
            return result;
        }
    }
}
