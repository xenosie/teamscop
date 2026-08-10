namespace Teamscop.Api.Errors;

/// <summary>
/// The addressed resource does not exist. Mapped to 404 by <see cref="ExceptionHandlingMiddleware"/>.
/// Kept distinct from <see cref="InvalidOperationException"/> (400) so that "no such id" does not
/// read to the desktop app as "you sent a malformed request".
/// </summary>
public sealed class NotFoundException(string message) : Exception(message);
