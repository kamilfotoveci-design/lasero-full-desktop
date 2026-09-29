namespace Lasero.Core.BackgroundRemoval;

/// <summary>Why a background-removal run failed. The UI shows the exception's Czech message; callers
/// that pick a fallback provider branch on this value instead of parsing text.</summary>
public enum BackgroundRemovalFailure
{
    /// <summary>The user is not signed in, or the session token could not be obtained (HTTP 401 too).</summary>
    SignInRequired,
    /// <summary>The remote service (or the local model) is not set up for this installation.</summary>
    NotConfigured,
    /// <summary>The account is not allowed to use the service (HTTP 403).</summary>
    AccessDenied,
    /// <summary>Too many requests or the model is overloaded (HTTP 429).</summary>
    RateLimited,
    /// <summary>The service answered with a server error (HTTP 5xx).</summary>
    ServiceUnavailable,
    /// <summary>The request never reached the service.</summary>
    Network,
    /// <summary>The service did not answer in time.</summary>
    Timeout,
    /// <summary>The source file is unreadable, unsupported or was rejected by the service.</summary>
    InvalidImage,
    /// <summary>The source is over the size limit of the service.</summary>
    TooLarge,
    /// <summary>The service answered but the result cannot be used safely.</summary>
    InvalidResult,
    /// <summary>The on-device model failed.</summary>
    LocalFailed,
    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>A background-removal failure with a short, user-facing Czech message (neutral form, no
/// question or exclamation marks). Never carries the auth token or the server response body.</summary>
public sealed class BackgroundRemovalException : Exception
{
    public BackgroundRemovalException(BackgroundRemovalFailure failure, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
    }

    public BackgroundRemovalFailure Failure { get; }
}
