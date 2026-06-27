using Microsoft.AspNetCore.Diagnostics;
using Orleans.Transactions;

namespace ActorBank.Api.Infrastructure;

/// <summary>
/// Translates domain failures into RFC 7807 problem responses. When a transaction aborts,
/// Orleans wraps the original exception, so we scan the whole inner-exception chain.
/// </summary>
public sealed class BankExceptionHandler(ILogger<BankExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var domain = FindDomainException(exception);
        var (status, title) = Map(domain);
        if (status is null)
            return false; // not a known domain error — let the default handler return 500

        BankExceptionLog.DomainError(logger, domain?.Message ?? exception.Message);

        var problem = Results.Problem(
            title: title,
            detail: domain?.Message ?? exception.Message,
            statusCode: status.Value);

        await problem.ExecuteAsync(httpContext);
        return true;
    }

    private static (int? Status, string? Title) Map(Exception? domain) =>
        domain switch
        {
            InsufficientFundsException => (StatusCodes.Status409Conflict, "Insufficient funds"),
            AccountNotOpenException => (StatusCodes.Status404NotFound, "Account not open"),
            InvalidAmountException => (StatusCodes.Status400BadRequest, "Invalid amount"),
            InvalidCredentialsException => (StatusCodes.Status401Unauthorized, "Invalid credentials"),
            UserAlreadyExistsException => (StatusCodes.Status409Conflict, "User already exists"),
            InvalidOperationException => (StatusCodes.Status409Conflict, "Operation not allowed"),
            OrleansTransactionException => (StatusCodes.Status503ServiceUnavailable, "Transaction could not complete — please retry"),
            TimeoutException => (StatusCodes.Status503ServiceUnavailable, "The operation timed out — please retry"),
            _ => (null, null)
        };

    /// <summary>
    /// Walks the inner-exception chain for a recognized error, preferring a specific domain error
    /// (e.g. insufficient funds) over the generic transaction wrapper Orleans puts around it.
    /// </summary>
    private static Exception? FindDomainException(Exception? exception)
    {
        Exception? transactionFailure = null;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is BankException or InvalidOperationException)
                return current;
            transactionFailure ??= current as OrleansTransactionException;
            transactionFailure ??= current as TimeoutException;
        }
        return transactionFailure;
    }
}

/// <summary>Source-generated log messages for <see cref="BankExceptionHandler"/>.</summary>
internal static partial class BankExceptionLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Domain error: {message}")]
    public static partial void DomainError(ILogger logger, string message);
}
