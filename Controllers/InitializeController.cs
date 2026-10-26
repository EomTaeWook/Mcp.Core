using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core
{
    [Controller("initialize")]
    internal class InitializeController : McpControllerBase
    {
        private readonly McpServerOptions _options;

        public InitializeController(McpServerOptions options)
        {
            _options = options;
        }

        public override Task<JsonObject> ExecuteAsync(McpRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            JsonObject parameters = request.Parameters;
            if (parameters["protocolVersion"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32602, "initialize requires protocolVersion as a string.");
            }
            if (parameters["clientInfo"] is not JsonObject client)
            {
                throw new McpProtocolException(-32602, "initialize requires clientInfo as an object.");
            }
            if (client["name"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32602, "clientInfo requires a nonempty name.");
            }
            if (string.IsNullOrWhiteSpace(client["name"].GetValue<string>()) == true)
            {
                throw new McpProtocolException(-32602, "clientInfo requires a nonempty name.");
            }
            if (client["version"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32602, "clientInfo requires a nonempty version.");
            }
            if (string.IsNullOrWhiteSpace(client["version"].GetValue<string>()) == true)
            {
                throw new McpProtocolException(-32602, "clientInfo requires a nonempty version.");
            }
            if (parameters["capabilities"] is not JsonObject)
            {
                throw new McpProtocolException(-32602, "initialize requires capabilities as an object.");
            }
            string version = parameters["protocolVersion"].GetValue<string>();
            if (McpProtocol.IsLegacyVersion(version) == false)
            {
                version = McpProtocol.LegacyVersion;
            }
            return Task.FromResult(new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = McpProtocol.ToolCapabilities(),
                ["serverInfo"] = McpProtocol.ServerInfo(_options),
                ["instructions"] = _options.Instructions
            });
        }
    }
}
