namespace Jiangyu.Acp;

/// <summary>
/// Represents an error returned by the ACP agent or a protocol-level failure.
/// </summary>
public sealed class AcpException : Exception
{
    public int? ErrorCode { get; }

    public AcpException(string message, int? errorCode = null)
        : base(message)
    {
        ErrorCode = errorCode;
    }
}
