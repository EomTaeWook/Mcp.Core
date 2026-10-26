namespace MCP.Core
{
    public class McpServerOptions
    {
        public Uri Endpoint { get; set; } = new Uri("http://127.0.0.1:8190/mcp");
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = "0.1.0";
        public string Instructions { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public int MaximumRequestBytes { get; set; } = 1024 * 1024;
        public int MaximumConcurrentRequests { get; set; } = 8;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

        public McpServerOptions()
        {
        }

        internal McpServerOptions Copy()
        {
            return new McpServerOptions
            {
                Endpoint = Endpoint,
                Name = Name,
                Version = Version,
                Instructions = Instructions,
                AccessToken = AccessToken,
                MaximumRequestBytes = MaximumRequestBytes,
                MaximumConcurrentRequests = MaximumConcurrentRequests,
                RequestTimeout = RequestTimeout
            };
        }

        internal void Validate()
        {
            if (Endpoint == null || Endpoint.IsAbsoluteUri == false || Endpoint.Scheme != Uri.UriSchemeHttp)
            {
                throw new ArgumentException("The MCP endpoint must be an absolute HTTP URL.", nameof(Endpoint));
            }
            if (Endpoint.IsLoopback == false)
            {
                throw new ArgumentException("The MCP endpoint must use a loopback address.", nameof(Endpoint));
            }
            if (Endpoint.Host != "localhost" && System.Net.IPAddress.TryParse(Endpoint.DnsSafeHost, out _) == false)
            {
                throw new ArgumentException("The MCP endpoint must use localhost or a loopback IP address.", nameof(Endpoint));
            }
            if (string.IsNullOrEmpty(Endpoint.UserInfo) == false || string.IsNullOrEmpty(Endpoint.Query) == false
                || string.IsNullOrEmpty(Endpoint.Fragment) == false || Endpoint.AbsolutePath == "/")
            {
                throw new ArgumentException("The MCP endpoint must have a path and no credentials, query or fragment.", nameof(Endpoint));
            }
            if (string.IsNullOrWhiteSpace(Name) == true || string.IsNullOrWhiteSpace(Version) == true)
            {
                throw new ArgumentException("MCP server name and version are required.");
            }
            if (Instructions == null || AccessToken == null)
            {
                throw new ArgumentException("MCP instructions and access token cannot be null.");
            }
            if (AccessToken.Any(character => character < 0x21 || character > 0x7E) == true)
            {
                throw new ArgumentException("The MCP access token must contain visible ASCII characters.", nameof(AccessToken));
            }
            if (MaximumRequestBytes < 1 || MaximumRequestBytes > 32 * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumRequestBytes));
            }
            if (MaximumConcurrentRequests < 1 || MaximumConcurrentRequests > 128)
            {
                throw new ArgumentOutOfRangeException(nameof(MaximumConcurrentRequests));
            }
            if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromHours(1))
            {
                throw new ArgumentOutOfRangeException(nameof(RequestTimeout));
            }
        }
    }
}
