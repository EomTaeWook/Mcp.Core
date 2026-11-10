using MCP.Core.Attributes;
using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Models.Tools;
using MCP.Core.Protocol;
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
        private readonly McpServerOptions _options;

        public ToolsCallController(IReadOnlyDictionary<string, McpTool> tools, Action<Exception> reportError, McpServerOptions options)
        {
            _tools = tools;
            _reportError = reportError;
            _options = options;
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
            McpToolCallContext context = new McpToolCallContext(request, _options);
            try
            {
                McpToolResult result = await tool.ExecuteAsync(arguments, context, token).ConfigureAwait(false);
                JsonObject response = result.ToJson();
                if (response["resultType"]?.GetValueKind() == JsonValueKind.String)
                {
                    if (response["resultType"].GetValue<string>() == "input_required")
                    {
                        ValidateInputRequired(response, context);
                    }
                }
                return response;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested == true)
            {
                throw;
            }
            catch (McpProtocolException)
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

        private void ValidateInputRequired(JsonObject response, McpToolCallContext context)
        {
            if (context.IsModern == false)
            {
                if (string.IsNullOrEmpty(context.SessionId) == true)
                {
                    throw new McpProtocolException(-32602, "Input-required results require a modern request or an initialized legacy sampling session.");
                }
            }
            JsonObject clientCapabilities = context.ClientCapabilities;
            foreach (KeyValuePair<string, JsonNode> inputRequest in response["inputRequests"].AsObject())
            {
                JsonObject requestedInput = inputRequest.Value.AsObject();
                if (requestedInput["method"].GetValue<string>() != "sampling/createMessage")
                {
                    throw new McpProtocolException(-32602, "This server supports only sampling/createMessage input requests.");
                }
                if (clientCapabilities["sampling"] is not JsonObject)
                {
                    throw new McpProtocolException(-32602, "The client did not declare sampling capability for this request.");
                }
            }
        }
    }
}
