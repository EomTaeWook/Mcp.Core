using MCP.Core.Models.Protocol;
using MCP.Core.Tools;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Controllers
{
    [Controller("tools/call")]
    internal class ToolsCallController : McpControllerBase
    {
        private readonly IReadOnlyDictionary<string, McpTool> _tools;
        private readonly Action<Exception> _reportError;

        public ToolsCallController(IReadOnlyDictionary<string, McpTool> tools, Action<Exception> reportError)
        {
            _tools = tools;
            _reportError = reportError;
        }

        public override async Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken token)
        {
            JsonObject parameters = request.Parameters;
            if (parameters["name"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32602, "tools/call requires a tool name.");
            }
            string name = parameters["name"].GetValue<string>();
            if (_tools.TryGetValue(name, out McpTool tool) == false)
            {
                throw new McpProtocolException(-32602, "Unknown tool: " + name);
            }
            JsonObject arguments = new JsonObject();
            if (parameters.ContainsKey("arguments") == true)
            {
                if (parameters["arguments"] is not JsonObject supplied)
                {
                    throw new McpProtocolException(-32602, "Tool arguments must be an object.");
                }
                arguments = supplied;
            }
            try
            {
                return (await tool.ExecuteAsync(arguments, token).ConfigureAwait(false)).ToJson();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested == true)
            {
                throw;
            }
            catch (ArgumentException error)
            {
                return new McpToolResult(error.Message, true).ToJson();
            }
            catch (Exception error)
            {
                _reportError(error);
                return new McpToolResult("Tool execution failed. See the server log for details.", true).ToJson();
            }
        }
    }
}
