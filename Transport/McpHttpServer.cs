using Dignus.Actor.Core;
using Dignus.Actor.Core.DeadLetter;
using Dignus.DependencyInjection;
using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Models.Tools;
using MCP.Core.Protocol;
using MCP.Core.Tools;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MCP.Core.Transport
{
    public class McpHttpServer : IAsyncDisposable
    {
        private readonly McpServerOptions _options;
        private readonly McpRequestReader _reader;
        private readonly McpDispatcher _dispatcher;
        private readonly Action<Exception> _onError;
        private readonly SemaphoreSlim _requestSlots;
        private readonly SemaphoreSlim _transportSlots;
        private readonly string _authenticationScope;
        private readonly ConcurrentDictionary<string, McpLegacySession> _legacySessions = new ConcurrentDictionary<string, McpLegacySession>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, McpLegacySamplingRequest> _legacySamplingRequests = new ConcurrentDictionary<string, McpLegacySamplingRequest>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<long, McpRequestExecution> _requests = new();
        private ActorSystem _actorSystem;
        private IActorRef[] _requestActors = Array.Empty<IActorRef>();
        private HttpListener _listener;
        private CancellationTokenSource _lifetime;
        private Task _acceptTask = Task.CompletedTask;
        private long _nextRequest;
        private long _nextRequestActor;
        private int _legacySessionCount;
        private int _running;
        private bool _disposed;

        public event Action StateChanged;

        public McpHttpServer(McpServerOptions options, IReadOnlyList<McpTool> tools, Action<Exception> onError = null, Assembly controllerAssembly = null, Action<ServiceContainer> configureServices = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(tools);
            options.Validate();
            _options = options.Copy();
            _onError = onError;
            _requestSlots = new SemaphoreSlim(_options.MaximumConcurrentRequests, _options.MaximumConcurrentRequests);
            _transportSlots = new SemaphoreSlim(_options.MaximumConcurrentRequests, _options.MaximumConcurrentRequests);
            _authenticationScope = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_options.AccessToken)));
            _reader = new McpRequestReader(_options);
            _dispatcher = new McpDispatcher(_options, tools, ReportError, CancelRequestAsync, controllerAssembly, configureServices);
        }

        public Uri Endpoint
        {
            get
            {
                return _options.Endpoint;
            }
        }

        public bool IsRunning
        {
            get
            {
                return Volatile.Read(ref _running) == 1;
            }
        }

        public async Task StartAsync(CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (IsRunning == false)
            {
                if (_listener != null)
                {
                    await StopInternalAsync().ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            HttpListener listener = new();
            listener.Prefixes.Add(_options.Endpoint.GetLeftPart(UriPartial.Authority) + "/");
            ActorSystem actorSystem = null;
            try
            {
                listener.Start();
                actorSystem = new ActorSystem(_options.MaximumConcurrentRequests);
                actorSystem.OnDeadLetterDetected += OnDeadLetterDetected;
                IActorRef[] requestActors = new IActorRef[_options.MaximumConcurrentRequests];
                for (int actorIndex = 0; actorIndex < requestActors.Length; actorIndex++)
                {
                    requestActors[actorIndex] = actorSystem.Spawn(() => new McpRequestActor(_dispatcher));
                }
                _requestActors = requestActors;
            }
            catch
            {
                try
                {
                    actorSystem?.Dispose();
                }
                finally
                {
                    listener.Close();
                }
                throw;
            }
            _actorSystem = actorSystem;
            _listener = listener;
            _lifetime = new CancellationTokenSource();
            SetRunning(true);
            _acceptTask = AcceptAsync(listener, _lifetime);
        }

        private async Task AcceptAsync(HttpListener listener, CancellationTokenSource lifetime)
        {
            try
            {
                while (lifetime.IsCancellationRequested == false)
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    if (lifetime.IsCancellationRequested == true)
                    {
                        context.Response.Abort();
                        break;
                    }
                    if (_transportSlots.Wait(0) == false)
                    {
                        context.Response.StatusCode = 429;
                        context.Response.Headers["Retry-After"] = "1";
                        context.Response.Close();
                        continue;
                    }
                    long id = Interlocked.Increment(ref _nextRequest);
                    McpRequestExecution request = new(lifetime.Token, _options.RequestTimeout);
                    _requests.TryAdd(id, request);
                    request.Completion = Task.Run(() => HandleAsync(context, request, lifetime.Token));
                    _ = request.Completion.ContinueWith(completed =>
                    {
                        _requests.TryRemove(id, out _);
                        request.Dispose();
                        if (completed.IsFaulted == true)
                        {
                            ReportError(completed.Exception);
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
            catch (Exception error) when (lifetime.IsCancellationRequested == true
                && (error is HttpListenerException || error is ObjectDisposedException))
            {
            }
            catch (Exception error)
            {
                SetRunning(false);
                ReportError(error);
                await lifetime.CancelAsync().ConfigureAwait(false);
                listener.Stop();
            }
        }

        private async Task CancelRequestAsync(McpRequest notification, JsonNode requestId)
        {
            foreach (McpRequestExecution request in _requests.Values)
            {
                string sessionId = request.Request?.SessionId ?? string.Empty;
                if (sessionId != notification.SessionId)
                {
                    continue;
                }
                if (request.Matches(requestId) == true)
                {
                    await request.CancelFromClientAsync().ConfigureAwait(false);
                }
            }
        }

        private void OnDeadLetterDetected(DeadLetterMessage message)
        {
            if (message.Message is McpRequestExecution execution)
            {
                if (message.Reason == DeadLetterReason.MailboxFull)
                {
                    execution.DispatchCompletion.TrySetException(new McpProtocolException(-32000, "The MCP request queue is full.", 429));
                    return;
                }
                execution.DispatchCompletion.TrySetException(new InvalidOperationException("The MCP request actor rejected the request: " + message.Reason));
                return;
            }
            if (message.Message is ActorExceptionMessage failure)
            {
                ReportError(failure.Exception);
            }
        }

        private async Task HandleAsync(HttpListenerContext context, McpRequestExecution execution, CancellationToken lifetime)
        {
            McpRequest request = null;
            CancellationToken token = execution.Token;
            bool ownsTransportSlot = true;
            bool ownsRequestSlot = false;
            try
            {
                _reader.ValidateConnection(context.Request);
                request = await _reader.ReadAsync(context.Request, token).ConfigureAwait(false);
                _reader.ValidateVersion(context.Request, request);
                McpLegacySession session = ValidateLegacySession(context.Request, request);
                execution.Request = request;
                if (request.IsResponse == true)
                {
                    AcceptLegacySamplingResponse(request, session);
                    context.Response.StatusCode = 202;
                    return;
                }
                if (request.Method != "initialize")
                {
                    execution.Identify(request.Id);
                }
                if (request.IsNotification == true)
                {
                    if (request.Method.StartsWith("notifications/", StringComparison.Ordinal) == false)
                    {
                        throw new McpProtocolException(-32600, "An MCP operation requires a request id.");
                    }
                    await _dispatcher.DispatchAsync(request, token).ConfigureAwait(false);
                    if (request.Method == "notifications/initialized")
                    {
                        session?.Initialize();
                    }
                    context.Response.StatusCode = 202;
                    return;
                }
                if (_requestSlots.Wait(0) == false)
                {
                    throw new McpProtocolException(-32000, "The MCP request limit is reached.", 429);
                }
                ownsRequestSlot = true;
                _transportSlots.Release();
                ownsTransportSlot = false;
                JsonObject response = await DispatchAsync(request, execution).ConfigureAwait(false);
                if (request.Method == "initialize")
                {
                    CreateLegacySession(context.Response, request, response);
                }
                if (request.IsModern == false)
                {
                    response = await FulfillLegacyInputAsync(context.Response, request, execution, session, response).ConfigureAwait(false);
                }
                await WriteAsync(context.Response, 200, response, token).ConfigureAwait(false);
            }
            catch (McpProtocolException error)
            {
                JsonNode requestId = request?.Id;
                if (error.RequestId != null)
                {
                    requestId = error.RequestId;
                }
                if (error.HttpStatus == 405)
                {
                    context.Response.Headers["Allow"] = "POST";
                }
                if (error.HttpStatus == 401)
                {
                    context.Response.Headers["WWW-Authenticate"] = "Bearer";
                }
                if (error.HttpStatus == 429)
                {
                    context.Response.Headers["Retry-After"] = "1";
                }
                await TryWriteErrorAsync(context.Response, error.HttpStatus,
                    McpProtocol.Error(requestId, error.Code, error.Message, error.Details), lifetime).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await TryWriteErrorAsync(context.Response, 400,
                    McpProtocol.Error(request?.Id, -32700, "Invalid JSON."), lifetime).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested == true)
            {
                if (execution.IsClientCancelled == true)
                {
                    context.Response.Abort();
                }
                if (lifetime.IsCancellationRequested == false)
                {
                    if (execution.IsClientCancelled == false)
                    {
                        await TryWriteErrorAsync(context.Response, 408,
                            McpProtocol.Error(request?.Id, -32000, "The MCP request timed out."), lifetime).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception error)
            {
                if (lifetime.IsCancellationRequested == false)
                {
                    ReportError(error);
                    await TryWriteErrorAsync(context.Response, 500,
                        McpProtocol.Error(request?.Id, -32603, "Internal server error."), lifetime).ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        context.Request.InputStream.Close();
                    }
                    catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException || error is IOException)
                    {
                    }
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception error) when (error is HttpListenerException || error is ObjectDisposedException || error is IOException)
                    {
                    }
                    finally
                    {
                        if (ownsRequestSlot == true)
                        {
                            _requestSlots.Release();
                        }
                        if (ownsTransportSlot == true)
                        {
                            _transportSlots.Release();
                        }
                    }
                }
            }
        }

        private async Task<JsonObject> DispatchAsync(McpRequest request, McpRequestExecution execution)
        {
            execution.Token.ThrowIfCancellationRequested();
            execution.PrepareDispatch(request);
            int actorIndex = (int)((Interlocked.Increment(ref _nextRequestActor) & long.MaxValue) % _requestActors.Length);
            _requestActors[actorIndex].Post(execution);
            return await execution.DispatchCompletion.Task.ConfigureAwait(false);
        }

        private McpLegacySession ValidateLegacySession(HttpListenerRequest http, McpRequest request)
        {
            string sessionId = http.Headers["MCP-Session-Id"] ?? string.Empty;
            if (string.IsNullOrEmpty(sessionId) == true)
            {
                if (request.IsResponse == true)
                {
                    throw new McpProtocolException(-32600, "A sampling response requires its initialized MCP session.");
                }
                return null;
            }
            if (request.IsModern == true)
            {
                throw new McpProtocolException(-32600, "Modern MCP requests must not use a legacy session.");
            }
            if (request.Method == "initialize")
            {
                throw new McpProtocolException(-32600, "Initialization must not reuse an MCP session.");
            }
            if (_legacySessions.TryGetValue(sessionId, out McpLegacySession session) == false)
            {
                throw new McpProtocolException(-32600, "The MCP session is not available. Initialize a new session.", 404);
            }
            if (session.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                RemoveLegacySession(sessionId);
                throw new McpProtocolException(-32600, "The MCP session expired. Initialize a new session.", 404);
            }
            if (session.AuthenticationScope != _authenticationScope)
            {
                throw new McpProtocolException(-32600, "The MCP session does not belong to this authenticated caller.", 403);
            }
            string version = http.Headers["MCP-Protocol-Version"];
            if (string.IsNullOrEmpty(version) == false)
            {
                if (version != session.ProtocolVersion)
                {
                    throw new McpProtocolException(-32600, "The protocol version differs from the initialized session.");
                }
            }
            if (session.IsInitialized == false)
            {
                if (request.Method != "notifications/initialized")
                {
                    throw new McpProtocolException(-32600, "The MCP initialization handshake is not complete.");
                }
            }
            request.SessionId = sessionId;
            if (session.IsInitialized == true)
            {
                request.LegacyClientCapabilities = (JsonObject)session.Capabilities.DeepClone();
            }
            return session;
        }

        private void CreateLegacySession(HttpListenerResponse http, McpRequest request, JsonObject response)
        {
            if (response["result"] is not JsonObject result)
            {
                return;
            }
            if (request.Parameters["capabilities"] is not JsonObject capabilities)
            {
                return;
            }
            if (capabilities["sampling"] is not JsonObject)
            {
                return;
            }
            foreach (KeyValuePair<string, McpLegacySession> existing in _legacySessions)
            {
                if (existing.Value.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                {
                    RemoveLegacySession(existing.Key);
                }
            }
            if (Interlocked.Increment(ref _legacySessionCount) > _options.MaximumConcurrentRequests)
            {
                Interlocked.Decrement(ref _legacySessionCount);
                throw new McpProtocolException(-32000, "The MCP legacy sampling session limit is reached.", 429);
            }
            string sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            McpLegacySession session = new McpLegacySession(result["protocolVersion"].GetValue<string>(), _authenticationScope, capabilities);
            if (_legacySessions.TryAdd(sessionId, session) == false)
            {
                Interlocked.Decrement(ref _legacySessionCount);
                throw new InvalidOperationException("A unique MCP session could not be created.");
            }
            http.Headers["MCP-Session-Id"] = sessionId;
        }

        private void RemoveLegacySession(string sessionId)
        {
            if (_legacySessions.TryRemove(sessionId, out _) == true)
            {
                Interlocked.Decrement(ref _legacySessionCount);
            }
        }

        private void AcceptLegacySamplingResponse(McpRequest request, McpLegacySession session)
        {
            if (session == null)
            {
                throw new McpProtocolException(-32600, "A sampling response requires an initialized session.");
            }
            string responseId = request.Id.ToJsonString();
            if (_legacySamplingRequests.TryGetValue(responseId, out McpLegacySamplingRequest pending) == false)
            {
                throw new McpProtocolException(-32600, "The sampling request is no longer pending.");
            }
            if (pending.SessionId != request.SessionId)
            {
                throw new McpProtocolException(-32600, "The sampling response belongs to another MCP session.", 403);
            }
            if (pending.CancellationToken.IsCancellationRequested == true)
            {
                throw new McpProtocolException(-32600, "The sampling request has been cancelled or expired.");
            }
            if (request.Response["result"] is JsonObject result)
            {
                ValidateSamplingResult(result);
            }
            if (_legacySamplingRequests.TryRemove(responseId, out _) == false)
            {
                throw new McpProtocolException(-32600, "The sampling response has already been accepted.");
            }
            if (pending.Completion.TrySetResult((JsonObject)request.Response.DeepClone()) == false)
            {
                throw new McpProtocolException(-32600, "The sampling response has already been accepted.");
            }
        }

        private void ValidateSamplingResult(JsonObject result)
        {
            if (result["role"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32600, "A sampling result requires a role string.");
            }
            string role = result["role"].GetValue<string>();
            if (role != "assistant")
            {
                if (role != "user")
                {
                    throw new McpProtocolException(-32600, "A sampling result role must be assistant or user.");
                }
            }
            if (result["model"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32600, "A sampling result requires a model string.");
            }
            if (result["content"] is not JsonObject content)
            {
                throw new McpProtocolException(-32600, "A legacy sampling result requires one content object.");
            }
            if (content["type"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32600, "Sampling content requires a type string.");
            }
            string contentType = content["type"].GetValue<string>();
            if (contentType == "text")
            {
                if (content["text"]?.GetValueKind() != JsonValueKind.String)
                {
                    throw new McpProtocolException(-32600, "Sampling text content requires a text string.");
                }
                return;
            }
            if (contentType != "image")
            {
                if (contentType != "audio")
                {
                    throw new McpProtocolException(-32600, "This sampling request accepts text, image or audio content.");
                }
            }
            if (content["data"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32600, "Sampling media content requires data as a string.");
            }
            if (content["mimeType"]?.GetValueKind() != JsonValueKind.String)
            {
                throw new McpProtocolException(-32600, "Sampling media content requires mimeType as a string.");
            }
        }

        private async Task<JsonObject> FulfillLegacyInputAsync(HttpListenerResponse http, McpRequest request, McpRequestExecution execution, McpLegacySession session, JsonObject response)
        {
            int rounds = 0;
            while (response["result"] is JsonObject result)
            {
                if (result["resultType"]?.GetValueKind() != JsonValueKind.String)
                {
                    return response;
                }
                if (result["resultType"].GetValue<string>() != "input_required")
                {
                    return response;
                }
                if (session == null)
                {
                    throw new McpProtocolException(-32602, "Legacy input requests require an initialized sampling session.");
                }
                if (rounds >= 8)
                {
                    return McpProtocol.Response(request.Id, new McpToolResult("The sampling input round limit was reached.", true).ToJson());
                }
                rounds++;
                TimeSpan remaining = session.ExpiresAtUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return McpProtocol.Response(request.Id, new McpToolResult("The MCP sampling session expired.", true).ToJson());
                }
                using CancellationTokenSource roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
                roundCancellation.CancelAfter(remaining);
                JsonObject inputResponses = new JsonObject();
                try
                {
                    foreach (KeyValuePair<string, JsonNode> inputRequest in result["inputRequests"].AsObject())
                    {
                        JsonObject requestedInput = inputRequest.Value.AsObject();
                        string samplingId = "sampling-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                        JsonNode samplingRequestId = JsonValue.Create(samplingId);
                        string pendingKey = samplingRequestId.ToJsonString();
                        McpLegacySamplingRequest pending = new McpLegacySamplingRequest(request.SessionId, roundCancellation.Token);
                        if (_legacySamplingRequests.TryAdd(pendingKey, pending) == false)
                        {
                            throw new InvalidOperationException("A unique sampling request could not be created.");
                        }
                        try
                        {
                            JsonObject samplingRequest = new JsonObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["id"] = samplingRequestId,
                                ["method"] = "sampling/createMessage",
                                ["params"] = requestedInput["params"].DeepClone()
                            };
                            await WriteEventAsync(http, samplingRequest, roundCancellation.Token).ConfigureAwait(false);
                            JsonObject samplingResponse = await pending.Completion.Task.WaitAsync(roundCancellation.Token).ConfigureAwait(false);
                            if (samplingResponse.ContainsKey("error") == true)
                            {
                                return McpProtocol.Response(request.Id, new McpToolResult("The client declined or failed the sampling request.", true).ToJson());
                            }
                            inputResponses[inputRequest.Key] = samplingResponse["result"].DeepClone();
                        }
                        finally
                        {
                            _legacySamplingRequests.TryRemove(pendingKey, out _);
                        }
                    }
                }
                catch (OperationCanceledException) when (execution.Token.IsCancellationRequested == false)
                {
                    return McpProtocol.Response(request.Id, new McpToolResult("The MCP sampling session expired.", true).ToJson());
                }
                request.IsInputContinuation = true;
                request.InputResponses = inputResponses;
                request.RequestState = string.Empty;
                if (result["requestState"] != null)
                {
                    request.RequestState = result["requestState"].GetValue<string>();
                }
                response = await DispatchAsync(request, execution).ConfigureAwait(false);
            }
            return response;
        }

        private async Task WriteEventAsync(HttpListenerResponse response, JsonObject message, CancellationToken token)
        {
            if (response.ContentType != "text/event-stream")
            {
                response.StatusCode = 200;
                response.ContentType = "text/event-stream";
                response.Headers["Cache-Control"] = "no-store";
                response.SendChunked = true;
            }
            byte[] bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + message.ToJsonString() + "\n\n");
            await response.OutputStream.WriteAsync(bytes.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
            await response.OutputStream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task TryWriteErrorAsync(HttpListenerResponse response, int status, JsonObject error, CancellationToken token)
        {
            try
            {
                await WriteAsync(response, status, error, token).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpListenerException || failure is IOException
                || failure is ObjectDisposedException || failure is OperationCanceledException || failure is InvalidOperationException)
            {
            }
        }

        private async Task WriteAsync(HttpListenerResponse response, int status, JsonObject message, CancellationToken token)
        {
            if (response.ContentType == "text/event-stream")
            {
                await WriteEventAsync(response, message, token).ConfigureAwait(false);
                return;
            }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes.AsMemory(), token).AsTask().WaitAsync(token).ConfigureAwait(false);
        }

        private void ReportError(Exception error)
        {
            try
            {
                _onError?.Invoke(error);
            }
            catch
            {
            }
        }

        private void SetRunning(bool running)
        {
            int value = 0;
            if (running == true)
            {
                value = 1;
            }
            if (Interlocked.Exchange(ref _running, value) != value)
            {
                try
                {
                    StateChanged?.Invoke();
                }
                catch (Exception error)
                {
                    ReportError(error);
                }
            }
        }

        public Task StopAsync()
        {
            return StopInternalAsync();
        }

        private async Task StopInternalAsync()
        {
            SetRunning(false);
            if (_listener == null)
            {
                return;
            }
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
                _listener.Stop();
                await _acceptTask.ConfigureAwait(false);
                await Task.WhenAll(_requests.Values.Select(request => request.Completion)).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    _actorSystem.Dispose();
                }
                finally
                {
                    _actorSystem = null;
                    _requestActors = Array.Empty<IActorRef>();
                    _listener.Close();
                    _listener = null;
                    _lifetime.Dispose();
                    _lifetime = null;
                    _acceptTask = Task.CompletedTask;
                    _legacySessions.Clear();
                    Interlocked.Exchange(ref _legacySessionCount, 0);
                    _legacySamplingRequests.Clear();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed == true)
            {
                return;
            }
            _disposed = true;
            await StopInternalAsync().ConfigureAwait(false);
            _requestSlots.Dispose();
            _transportSlots.Dispose();
        }
    }
}

