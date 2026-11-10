using System.Text.Json.Nodes;

namespace MCP.Core.Models.Server
{
    internal class McpLegacySession
    {
        private int _initialized;

        internal McpLegacySession(string protocolVersion, string authenticationScope, JsonObject capabilities)
        {
            ProtocolVersion = protocolVersion;
            AuthenticationScope = authenticationScope;
            Capabilities = (JsonObject)capabilities.DeepClone();
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30);
        }

        internal string ProtocolVersion { get; private set; }
        internal string AuthenticationScope { get; private set; }
        internal JsonObject Capabilities { get; private set; }
        internal DateTimeOffset ExpiresAtUtc { get; private set; }

        internal bool IsInitialized
        {
            get
            {
                return Volatile.Read(ref _initialized) == 1;
            }
        }

        internal void Initialize()
        {
            Interlocked.Exchange(ref _initialized, 1);
        }
    }
}
