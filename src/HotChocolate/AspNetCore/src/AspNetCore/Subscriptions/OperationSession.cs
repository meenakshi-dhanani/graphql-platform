using HotChocolate.Language;

namespace HotChocolate.AspNetCore.Subscriptions;

internal sealed class OperationSession : IOperationSession
{
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _ct;
    private readonly ISocketSession _session;
    private readonly ISocketSessionInterceptor _interceptor;
    private readonly IErrorHandler _errorHandler;
    private readonly IRequestExecutor _executor;
    private bool _disposed;

    public event EventHandler? Completed;

    public OperationSession(
        ISocketSession session,
        ISocketSessionInterceptor interceptor,
        IErrorHandler errorHandler,
        IRequestExecutor executor,
        string id)
    {
        _ct = _cts.Token;
        _session = session;
        _interceptor = interceptor;
        _errorHandler = errorHandler;
        _executor = executor;
        Id = id;
    }

    public string Id { get; }

    public bool IsCompleted { get; private set; }

    public void BeginExecute(GraphQLRequest request, CancellationToken cancellationToken)
        => Task.Factory.StartNew(
            () => SendResultsAsync(request, cancellationToken),
            default,
            TaskCreationOptions.None,
            TaskScheduler.Default);

    private async Task SendResultsAsync(GraphQLRequest request, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ct);
        CancellationToken ct = cts.Token;
        var completeTry = false;

        try
        {
            IQueryRequestBuilder requestBuilder = CreateRequestBuilder(request);
            await _interceptor.OnRequestAsync(_session, Id, requestBuilder, ct);
            IExecutionResult result = await _executor.ExecuteAsync(requestBuilder.Create(), ct);

            switch (result)
            {
                case IQueryResult queryResult:
                    if (queryResult.Data is null && queryResult.Errors is { Count: > 0 })
                    {
                        await _session.Protocol.SendErrorMessageAsync(
                            _session,
                            Id,
                            queryResult.Errors,
                            ct);
                    }
                    else
                    {
                        await SendResultMessageAsync(queryResult, ct);
                    }
                    break;

                case IResponseStream responseStream:
                    await foreach (IQueryResult item in
                        responseStream.ReadResultsAsync().WithCancellation(ct))
                    {
                        await SendResultMessageAsync(item, ct);
                    }
                    break;
            }

            // the operation is completed and we will try to send a complete message.
            // we mark completeTry true so that in case of an error we do not try to send this
            // message again.
            completeTry = true;
            await _session.Protocol.SendCompleteMessageAsync(_session, Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the operation was cancelled so we do nothings
        }
        catch (Exception ex)
        {
            // if the error happened while the operation was not yet complete we will try
            // to send an error message and complete the subscription.
            if (!completeTry)
            {
                await TrySendErrorMessageAsync(ex, ct);
            }
        }
        finally
        {
            try
            {
                // we use the original cancellation token which represents the request cancellation to
                // invoke OnCompleteAsync this allows for an easy extension point to get rid of
                // any resources that might be bound to the subscription.
                await _interceptor.OnCompleteAsync(_session, Id, cancellationToken);
            }
            catch
            {
                // we will just ignore any user exceptions here so we can graciously close
                // the subscription out.
            }

            // signal that the subscription is completed.
            Complete();
        }
    }

    private static IQueryRequestBuilder CreateRequestBuilder(GraphQLRequest request)
    {
        var requestBuilder = new QueryRequestBuilder();

        if (request.Query is not null)
        {
            requestBuilder.SetQuery(request.Query);
        }

        if (request.OperationName is not null)
        {
            requestBuilder.SetOperation(request.OperationName);
        }

        if (request.QueryId is not null)
        {
            requestBuilder.SetQueryId(request.QueryId);
        }

        if (request.QueryHash is not null)
        {
            requestBuilder.SetQueryHash(request.QueryHash);
        }

        if (request.Variables is not null)
        {
            requestBuilder.SetVariableValues(request.Variables);
        }

        if (request.Extensions is not null)
        {
            requestBuilder.SetExtensions(request.Extensions);
        }

        return requestBuilder;
    }

    private async Task SendResultMessageAsync(IQueryResult result, CancellationToken ct)
    {
        result = await _interceptor.OnResultAsync(_session, Id, result, ct);
        await _session.Protocol.SendResultMessageAsync(_session, Id, result, ct);
    }

    private async Task TrySendErrorMessageAsync(Exception exception, CancellationToken ct)
    {
        try
        {
            if (!ct.IsCancellationRequested)
            {
                IError error = _errorHandler.CreateUnexpectedError(exception).Build();
                error = _errorHandler.Handle(error);

                IReadOnlyList<IError> errors =
                    error is AggregateError aggregateError
                        ? aggregateError.Errors
                        : new[] { error };

                await _session.Protocol.SendErrorMessageAsync(_session, Id, errors, ct);
            }
        }
        catch
        {
            // if we cannot send the complete message we just go on. This mostly will happen
            // if the client is already disconnected or the operation was cancelled.
        }
    }

    private void Complete()
    {
        try
        {
            IsCompleted = true;
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // we ignore any error that might happen on invoking complete.
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            _cts.Dispose();
            _disposed = true;
        }
    }
}
