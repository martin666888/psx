using System.Text.Json;

namespace PSX.Services;

/// <summary>
/// Structured JSON-RPC 2.0 error raised when an ACP request returns an
/// <c>error</c> object. Unlike a plain <see cref="InvalidOperationException"/>,
/// this preserves the numeric <see cref="Code"/> (e.g. <c>-32000</c> for
/// "authentication required") and any structured <see cref="ErrorData"/> payload
/// so the session layer can branch on it — for example, to enter a recoverable
/// <c>auth_required</c> state instead of a permanent transcript-only fallback.
/// </summary>
public sealed class AcpJsonRpcException : Exception
{
    /// <summary>
    /// The ACP "authentication required" code. The Kimi/ACP agent returns this
    /// from <c>session/new</c>, <c>session/load</c>, or <c>session/prompt</c>
    /// when no valid credentials are present.
    /// </summary>
    public const int AuthRequiredCode = -32000;

    public int Code { get; }

    /// <summary>Raw JSON of the error's <c>data</c> field, when present.</summary>
    public string? ErrorData { get; }

    public AcpJsonRpcException(int code, string message, string? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    /// <summary>True when this is the standard ACP authentication-required error.</summary>
    public bool IsAuthRequired => Code == AuthRequiredCode;

    /// <summary>
    /// Builds an exception from a JSON-RPC <c>error</c> element. Falls back to
    /// the internal-error code when <c>code</c> is absent or malformed.
    /// </summary>
    public static AcpJsonRpcException FromErrorElement(JsonElement error)
    {
        var code = -32603;
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
            && codeElement.TryGetInt32(out var parsedCode))
        {
            code = parsedCode;
        }

        var message = error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("message", out var messageElement)
            && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString() ?? error.GetRawText()
                : error.GetRawText();

        string? data = null;
        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("data", out var dataElement))
        {
            data = dataElement.GetRawText();
        }

        return new AcpJsonRpcException(code, message, data);
    }
}
