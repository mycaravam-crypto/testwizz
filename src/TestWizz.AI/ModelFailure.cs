namespace TestWizz.AI;

/// <summary>Sanitized failure categories. Messages never contain API keys or raw response bodies.</summary>
public enum ModelFailure
{
    EndpointNotPrivate,
    DnsFailed,
    ConnectionFailed,
    TlsFailed,
    Timeout,
    Cancelled,
    RedirectRejected,
    MissingApiKey,
    AuthFailed,
    ModelNotFound,
    HttpError,
    EmptyResponse,
    InvalidResponse,
}

public class ModelClientException(ModelFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ModelFailure Failure { get; } = failure;

    /// <summary>Stable snake_case identifier, e.g. <c>endpoint_not_private</c>.</summary>
    public string Code => FailureCode(Failure);

    /// <summary>True when the problem is in configuration or environment rather than the model server.</summary>
    public bool IsConfigurationError => IsConfigurationFailure(Failure);

    public static bool IsConfigurationFailure(ModelFailure failure) =>
        failure is ModelFailure.EndpointNotPrivate or ModelFailure.MissingApiKey;

    public static string FailureCode(ModelFailure failure) => failure switch
    {
        ModelFailure.EndpointNotPrivate => "endpoint_not_private",
        ModelFailure.DnsFailed => "dns_failed",
        ModelFailure.ConnectionFailed => "connection_failed",
        ModelFailure.TlsFailed => "tls_failed",
        ModelFailure.Timeout => "timeout",
        ModelFailure.Cancelled => "cancelled",
        ModelFailure.RedirectRejected => "redirect_rejected",
        ModelFailure.MissingApiKey => "missing_api_key",
        ModelFailure.AuthFailed => "auth_failed",
        ModelFailure.ModelNotFound => "model_not_found",
        ModelFailure.HttpError => "http_error",
        ModelFailure.EmptyResponse => "empty_response",
        ModelFailure.InvalidResponse => "invalid_response",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}
