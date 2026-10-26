using System.Text.Json.Nodes;

namespace MCP.Core
{
    public class McpTool
    {
        private readonly McpToolDefinition _definition;
        private readonly Func<JsonObject, CancellationToken, Task<McpToolResult>> _execute;

        public McpTool(McpToolDefinition definition, Func<JsonObject, CancellationToken, Task<McpToolResult>> execute)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(execute);
            if (string.IsNullOrWhiteSpace(definition.Name) == true || definition.Name.Length > 128
                || definition.Name.Any(character => char.IsAsciiLetterOrDigit(character) == false
                    && character != '_' && character != '-' && character != '.') == true)
            {
                throw new ArgumentException("The MCP tool name must contain 1 to 128 ASCII letters, digits, underscores, hyphens or dots.", nameof(definition));
            }
            if (definition.Description == null || definition.InputSchema == null)
            {
                throw new ArgumentException("An MCP tool requires a description and an input schema.", nameof(definition));
            }
            McpToolSchema.ValidateDefinition(definition.InputSchema);
            _definition = definition.Copy();
            _execute = execute;
        }

        internal string Name
        {
            get
            {
                return _definition.Name;
            }
        }

        internal JsonObject Describe()
        {
            return new JsonObject
            {
                ["name"] = _definition.Name,
                ["description"] = _definition.Description,
                ["inputSchema"] = _definition.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = _definition.ReadOnly,
                    ["destructiveHint"] = _definition.Destructive,
                    ["idempotentHint"] = _definition.Idempotent,
                    ["openWorldHint"] = _definition.OpenWorld
                }
            };
        }

        internal async Task<McpToolResult> ExecuteAsync(JsonObject arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            McpToolSchema.ValidateArguments(_definition.InputSchema, arguments, "arguments");
            McpToolResult result = await Task.Run(() => _execute(arguments, token), token).ConfigureAwait(false);
            if (result == null)
            {
                throw new InvalidOperationException("The MCP tool returned no result: " + Name);
            }
            return result;
        }
    }
}
