using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Protocol;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Transport
{
    internal class McpRequestReader
    {
        private readonly McpServerOptions _options;
        private readonly byte[] _tokenHash;

        internal McpRequestReader(McpServerOptions options)
        {
            _options = options;
            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.AccessToken));
        }

        internal void ValidateConnection(HttpListenerRequest request)
        {
            if (request.RemoteEndPoint == null || IPAddress.IsLoopback(request.RemoteEndPoint.Address) == false
                || request.Url == null || request.Url.Port != _options.Endpoint.Port
                || request.Url.Host.Equals(_options.Endpoint.Host, StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new McpProtocolException(-32600, "Only the configured loopback endpoint is accepted.", 403);
            }
            string origin = request.Headers["Origin"];
            if (string.IsNullOrEmpty(origin) == false)
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out Uri address) == false
                    || address.GetLeftPart(UriPartial.Authority).Equals(_options.Endpoint.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase) == false
                    || address.AbsolutePath != "/" || string.IsNullOrEmpty(address.Query) == false
                    || string.IsNullOrEmpty(address.Fragment) == false || string.IsNullOrEmpty(address.UserInfo) == false)
                {
                    throw new McpProtocolException(-32600, "The request Origin is not allowed.", 403);
                }
            }
            if (string.IsNullOrEmpty(_options.AccessToken) == false)
            {
                string authorization = request.Headers["Authorization"] ?? string.Empty;
                if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == false
                    || CryptographicOperations.FixedTimeEquals(_tokenHash,
                        SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Substring(7)))) == false)
                {
                    throw new McpProtocolException(-32600, "A valid bearer token is required.", 401);
                }
            }
            if (request.Url.AbsolutePath.TrimEnd('/') != _options.Endpoint.AbsolutePath.TrimEnd('/')
                || string.IsNullOrEmpty(request.Url.Query) == false)
            {
                throw new McpProtocolException(-32600, "MCP endpoint not found.", 404);
            }
            if (request.HttpMethod != "POST")
            {
                throw new McpProtocolException(-32600, "The MCP endpoint accepts POST requests.", 405);
            }
            if (MediaTypeHeaderValue.TryParse(request.ContentType, out MediaTypeHeaderValue contentType) == false
                || contentType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new McpProtocolException(-32600, "Content-Type must be application/json.", 415);
            }
            if (string.IsNullOrEmpty(contentType.CharSet) == false
                && contentType.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase) == false)
            {
                throw new McpProtocolException(-32600, "JSON-RPC messages must use UTF-8.", 415);
            }
            string accept = request.Headers["Accept"] ?? string.Empty;
            if (Accepts(accept, "application/json") == false || Accepts(accept, "text/event-stream") == false)
            {
                throw new McpProtocolException(-32600, "Accept must allow application/json and text/event-stream.", 406);
            }
            if (request.ContentLength64 > _options.MaximumRequestBytes)
            {
                throw new McpProtocolException(-32600, "The MCP request body is too large.", 413);
            }
        }

        private bool Accepts(string header, string expected)
        {
            foreach (string field in header.Split(','))
            {
                if (MediaTypeWithQualityHeaderValue.TryParse(field.Trim(), out MediaTypeWithQualityHeaderValue value) == true
                    && (value.Quality == null || value.Quality > 0)
                    && (value.MediaType.Equals(expected, StringComparison.OrdinalIgnoreCase) == true || value.MediaType == "*/*"))
                {
                    return true;
                }
            }
            return false;
        }

        internal async Task<McpRequest> ReadAsync(HttpListenerRequest request, CancellationToken token)
        {
            using MemoryStream body = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await request.InputStream.ReadAsync(buffer.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }
                if (body.Length + count > _options.MaximumRequestBytes)
                {
                    throw new McpProtocolException(-32600, "The MCP request body is too large.", 413);
                }
                body.Write(buffer, 0, count);
            }
            using JsonDocument document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
            RejectDuplicateProperties(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new McpProtocolException(-32600, "A single JSON-RPC message object is required. Batches are not supported.");
            }
            JsonObject message = JsonNode.Parse(document.RootElement.GetRawText()).AsObject();
            if (message["jsonrpc"]?.GetValueKind() != JsonValueKind.String || message["jsonrpc"].GetValue<string>() != "2.0"
                || message["method"]?.GetValueKind() != JsonValueKind.String
                || string.IsNullOrWhiteSpace(message["method"].GetValue<string>()) == true
                || message.ContainsKey("result") == true || message.ContainsKey("error") == true)
            {
                throw new McpProtocolException(-32600, "A valid JSON-RPC 2.0 request or notification is required.");
            }
            McpRequest parsed = new McpRequest
            {
                Method = message["method"].GetValue<string>(),
                IsNotification = message.ContainsKey("id") == false
            };
            if (parsed.IsNotification == false)
            {
                JsonNode id = message["id"];
                if (id == null || (id.GetValueKind() != JsonValueKind.String
                    && (id.GetValueKind() != JsonValueKind.Number || long.TryParse(id.ToJsonString(), NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture, out _) == false)))
                {
                    throw new McpProtocolException(-32600, "A request id must be a string or integer.");
                }
                parsed.Id = id.DeepClone();
            }
            if (message.ContainsKey("params") == true)
            {
                if (message["params"] is not JsonObject parameters)
                {
                    throw new McpProtocolException(-32602, "Request params must be an object.") { RequestId = parsed.Id };
                }
                parsed.Parameters = parameters;
            }
            return parsed;
        }

        private void RejectDuplicateProperties(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (names.Add(property.Name) == false)
                    {
                        throw new McpProtocolException(-32600, "JSON objects must not contain duplicate property names.");
                    }
                    RejectDuplicateProperties(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    RejectDuplicateProperties(item);
                }
            }
        }

        internal void ValidateVersion(HttpListenerRequest http, McpRequest request)
        {
            string version = http.Headers["MCP-Protocol-Version"];
            if (request.Method == "initialize")
            {
                return;
            }
            if (string.IsNullOrEmpty(version) == true)
            {
                version = "2025-03-26";
            }
            JsonObject metadata = request.Parameters["_meta"] as JsonObject;
            if (request.Parameters.ContainsKey("_meta") == true && metadata == null)
            {
                throw new McpProtocolException(-32602, "Request metadata must be an object.");
            }
            string bodyVersion = null;
            if (metadata?["io.modelcontextprotocol/protocolVersion"]?.GetValueKind() == JsonValueKind.String)
            {
                bodyVersion = metadata["io.modelcontextprotocol/protocolVersion"].GetValue<string>();
            }
            if (version != McpProtocol.CurrentVersion && McpProtocol.IsLegacyVersion(version) == false)
            {
                throw new McpProtocolException(-32022, "Unsupported protocol version.", 400,
                    new JsonObject { ["supported"] = McpProtocol.SupportedVersions(), ["requested"] = version });
            }
            if (version == McpProtocol.CurrentVersion || bodyVersion == McpProtocol.CurrentVersion)
            {
                request.IsModern = true;
            }
            if (bodyVersion != null && bodyVersion != McpProtocol.CurrentVersion && McpProtocol.IsLegacyVersion(bodyVersion) == false)
            {
                throw new McpProtocolException(-32022, "Unsupported protocol version.", 400,
                    new JsonObject { ["supported"] = McpProtocol.SupportedVersions(), ["requested"] = bodyVersion });
            }
            if (bodyVersion != null && bodyVersion != version)
            {
                throw new McpProtocolException(-32020, "The MCP protocol version header does not match request metadata.");
            }
            if (request.IsModern == true)
            {
                if (metadata == null || bodyVersion == null
                    || metadata["io.modelcontextprotocol/clientCapabilities"] is not JsonObject)
                {
                    throw new McpProtocolException(-32602, "Modern MCP requests require protocolVersion and clientCapabilities in params._meta.");
                }
                if (version != bodyVersion || http.Headers["MCP-Protocol-Version"] == null
                    || http.Headers["Mcp-Method"] != request.Method)
                {
                    throw new McpProtocolException(-32020, "Required MCP request headers are missing or do not match the body.");
                }
                if (request.Method == "tools/call")
                {
                    string name = DecodeHeader(http.Headers["Mcp-Name"]);
                    if (request.Parameters["name"]?.GetValueKind() != JsonValueKind.String
                        || name != request.Parameters["name"].GetValue<string>())
                    {
                        throw new McpProtocolException(-32020, "Mcp-Name does not match the requested tool.");
                    }
                }
            }
        }

        private string DecodeHeader(string value)
        {
            if (string.IsNullOrEmpty(value) == true)
            {
                throw new McpProtocolException(-32020, "A required MCP name header is missing.");
            }
            if (value.StartsWith("=?base64?", StringComparison.Ordinal) == true && value.EndsWith("?=", StringComparison.Ordinal) == true)
            {
                try
                {
                    return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value.Substring(9, value.Length - 11)));
                }
                catch (Exception error) when (error is FormatException || error is DecoderFallbackException)
                {
                    throw new McpProtocolException(-32020, "The MCP name header is not valid encoded UTF-8.");
                }
            }
            if (value.Any(character => character < 0x20 || character > 0x7E) == true || value.Trim() != value)
            {
                throw new McpProtocolException(-32020, "The MCP name header must use the required value encoding.");
            }
            return value;
        }
    }
}
