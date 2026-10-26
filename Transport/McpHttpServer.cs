using Dignus.Actor.Core;
using Dignus.Actor.Core.DeadLetter;
using Dignus.DependencyInjection;
using MCP.Core.Models.Protocol;
using MCP.Core.Models.Server;
using MCP.Core.Tools;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
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
        private readonly ConcurrentDictionary<long, McpRequestExecution> _requests = new ConcurrentDictionary<long, McpRequestExecution>();
        private ActorSystem _actorSystem;
        private IActorRef[] _requestActors = Array.Empty<IActorRef>();
        private HttpListener _listener;
        private CancellationTokenSource _lifetime;
        private Task _acceptTask = Task.CompletedTask;
        private long _nextRequest;
        private long _nextRequestActor;
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
            HttpListener listener = new HttpListener();
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
                    if (_requestSlots.Wait(0) == false)
                    {
                        context.Response.StatusCode = 429;
                        context.Response.Headers["Retry-After"] = "1";
                        context.Response.Close();
                        continue;
                    }
                    long id = Interlocked.Increment(ref _nextRequest);
                    McpRequestExecution request = new McpRequestExecution(lifetime.Token, _options.RequestTimeout);
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

        private async Task CancelRequestAsync(JsonNode requestId)
        {
            foreach (McpRequestExecution request in _requests.Values)
            {
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
            try
            {
                _reader.ValidateConnection(context.Request);
                request = await _reader.ReadAsync(context.Request, token).ConfigureAwait(false);
                _reader.ValidateVersion(context.Request, request);
                if (request.IsModern == false)
                {
                    if (request.Method != "initialize")
                    {
                        execution.Identify(request.Id);
                    }
                }
                if (request.IsNotification == true)
                {
                    if (request.Method.StartsWith("notifications/", StringComparison.Ordinal) == false)
                    {
                        throw new McpProtocolException(-32600, "An MCP operation requires a request id.");
                    }
                    await _dispatcher.DispatchAsync(request, token).ConfigureAwait(false);
                    context.Response.StatusCode = 202;
                    return;
                }
                execution.Request = request;
                int actorIndex = (int)((Interlocked.Increment(ref _nextRequestActor) & long.MaxValue) % _requestActors.Length);
                _requestActors[actorIndex].Post(execution);
                JsonObject response = await execution.DispatchCompletion.Task.ConfigureAwait(false);
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
                        _requestSlots.Release();
                    }
                }
            }
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
        }
    }
}

