using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Protocol;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Models.Tools
{
    public class McpToolCallContext
    {
        private readonly JsonObject _clientCapabilities;
        private readonly JsonObject _inputResponses;
        private readonly JsonNode _requestId;

        internal McpToolCallContext(McpRequest request, McpServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(options);
            IsModern = request.IsModern;
            IsInputContinuation = request.IsInputContinuation;
            SessionId = request.SessionId;
            _requestId = request.Id?.DeepClone();
            _clientCapabilities = new JsonObject();
            _inputResponses = new JsonObject();
            RequestState = string.Empty;
            AuthenticationScope = "anonymous";
            if (string.IsNullOrEmpty(options.AccessToken) == false)
            {
                AuthenticationScope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(options.AccessToken)));
            }
            JsonObject parameters = request.Parameters;
            if (IsModern == false)
            {
                if (parameters.ContainsKey("inputResponses") == true)
                {
                    throw new McpProtocolException(-32602, "Input responses require MCP 2026-07-28.");
                }
                if (parameters.ContainsKey("requestState") == true)
                {
                    throw new McpProtocolException(-32602, "Request state requires MCP 2026-07-28.");
                }
                if (request.LegacyClientCapabilities != null)
                {
                    _clientCapabilities = (JsonObject)request.LegacyClientCapabilities.DeepClone();
                }
                if (IsInputContinuation == true)
                {
                    _inputResponses = (JsonObject)request.InputResponses.DeepClone();
                    RequestState = request.RequestState;
                }
                return;
            }
            if (parameters["_meta"] is not JsonObject metadata)
            {
                throw new McpProtocolException(-32602, "Modern tool calls require request metadata.");
            }
            if (metadata["io.modelcontextprotocol/clientCapabilities"] is not JsonObject clientCapabilities)
            {
                throw new McpProtocolException(-32602, "Client capabilities must be an object.");
            }
            if (clientCapabilities.ContainsKey("sampling") == true)
            {
                if (clientCapabilities["sampling"] is not JsonObject)
                {
                    throw new McpProtocolException(-32602, "Sampling capability must be an object.");
                }
            }
            _clientCapabilities = (JsonObject)clientCapabilities.DeepClone();
            if (parameters.ContainsKey("inputResponses") == true)
            {
                if (parameters["inputResponses"] is not JsonObject inputResponses)
                {
                    throw new McpProtocolException(-32602, "Input responses must be an object.");
                }
                _inputResponses = (JsonObject)inputResponses.DeepClone();
            }
            if (parameters.ContainsKey("requestState") == true)
            {
                if (parameters["requestState"]?.GetValueKind() != JsonValueKind.String)
                {
                    throw new McpProtocolException(-32602, "Request state must be a string.");
                }
                RequestState = parameters["requestState"].GetValue<string>();
            }
        }

        public bool IsModern { get; private set; }
        public bool IsInputContinuation { get; private set; }
        public string SessionId { get; private set; }
        public string RequestState { get; private set; }
        public string AuthenticationScope { get; private set; }

        public JsonObject ClientCapabilities
        {
            get
            {
                return (JsonObject)_clientCapabilities.DeepClone();
            }
        }

        public JsonObject InputResponses
        {
            get
            {
                return (JsonObject)_inputResponses.DeepClone();
            }
        }

        public JsonNode RequestId
        {
            get
            {
                return _requestId?.DeepClone();
            }
        }
    }
}
