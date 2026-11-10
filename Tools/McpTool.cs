using MCP.Core.Models.Tools;
using System.Text.Json.Nodes;

namespace MCP.Core.Tools
{
    public class McpTool
    {
        private readonly McpToolDefinition _definition;
        private readonly Func<JsonObject, CancellationToken, Task<McpToolResult>> _execute;
        private readonly Func<JsonObject, McpToolCallContext, CancellationToken, Task<McpToolResult>> _executeWithContext;

        public McpTool(McpToolDefinition definition, Func<JsonObject, CancellationToken, Task<McpToolResult>> execute)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(execute);
            _definition = CopyDefinition(definition);
            _execute = execute;
        }

        public McpTool(McpToolDefinition definition, Func<JsonObject, McpToolCallContext, CancellationToken, Task<McpToolResult>> execute)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(execute);
            _definition = CopyDefinition(definition);
            _executeWithContext = execute;
        }

        private static McpToolDefinition CopyDefinition(McpToolDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(definition.Name) == true)
            {
                throw new ArgumentException("The MCP tool name must contain 1 to 128 ASCII letters, digits, underscores, hyphens or dots.", nameof(definition));
            }
            if (definition.Name.Length > 128)
            {
                throw new ArgumentException("The MCP tool name must contain 1 to 128 ASCII letters, digits, underscores, hyphens or dots.", nameof(definition));
            }
            foreach (char character in definition.Name)
            {
                if (char.IsAsciiLetterOrDigit(character) == true)
                {
                    continue;
                }
                if (character == '_')
                {
                    continue;
                }
                if (character == '-')
                {
                    continue;
                }
                if (character == '.')
                {
                    continue;
                }
                throw new ArgumentException("The MCP tool name must contain 1 to 128 ASCII letters, digits, underscores, hyphens or dots.", nameof(definition));
            }
            if (definition.Description == null)
            {
                throw new ArgumentException("An MCP tool requires a description and an input schema.", nameof(definition));
            }
            if (definition.InputSchema == null)
            {
                throw new ArgumentException("An MCP tool requires a description and an input schema.", nameof(definition));
            }
            McpToolSchema.ValidateDefinition(definition.InputSchema);
            return definition.Copy();
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

        internal Task<McpToolResult> ExecuteAsync(JsonObject arguments, CancellationToken token)
        {
            return ExecuteAsync(arguments, null, token);
        }

        internal async Task<McpToolResult> ExecuteAsync(JsonObject arguments, McpToolCallContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            McpToolSchema.ValidateArguments(_definition.InputSchema, arguments, "arguments");
            McpToolResult result;
            if (_executeWithContext != null)
            {
                ArgumentNullException.ThrowIfNull(context);
                result = await Task.Run(() => _executeWithContext(arguments, context, token), token).ConfigureAwait(false);
            }
            else
            {
                result = await Task.Run(() => _execute(arguments, token), token).ConfigureAwait(false);
            }
            if (result == null)
            {
                throw new InvalidOperationException("The MCP tool returned no result: " + Name);
            }
            return result;
        }
    }
}
