using Microsoft.AspNetCore.Diagnostics;

namespace PreciousLists.Infrastructure;

public sealed class AuthExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellation
    )
    {
        if (!context.Request.Path.StartsWithSegments("/v1/auth"))
            return false;
        // The framework logs the exception server-side; do not expose stack traces or tokens.
        var status = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
        await Results
            .Problem(
                statusCode: status,
                title: status == StatusCodes.Status400BadRequest
                    ? "The authentication request is invalid. Check required parameters and JSON fields."
                    : "The authentication request could not be completed."
            )
            .ExecuteAsync(context);
        return true;
    }
}
