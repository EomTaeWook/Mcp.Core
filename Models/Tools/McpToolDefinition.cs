using System.Text.Json.Nodes;

namespace MCP.Core.Models.Tools
{
    public class McpToolDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public JsonObject InputSchema { get; set; } = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false
        };
        public bool ReadOnly { get; set; }
        public bool Destructive { get; set; }
        public bool Idempotent { get; set; }
        public bool OpenWorld { get; set; } = true;

        public McpToolDefinition()
        {
        }

        public McpToolDefinition(string name, string description, JsonObject inputSchema = null)
        {
            Name = name;
            Description = description;
            if (inputSchema != null)
            {
                InputSchema = inputSchema;
            }
        }

        internal McpToolDefinition Copy()
        {
            return new McpToolDefinition
            {
                Name = Name,
                Description = Description,
                InputSchema = (JsonObject)InputSchema.DeepClone(),
                ReadOnly = ReadOnly,
                Destructive = Destructive,
                Idempotent = Idempotent,
                OpenWorld = OpenWorld
            };
        }
    }
}
