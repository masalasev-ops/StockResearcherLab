using System.Net.Sockets;

namespace StockResearcherLab.Data;

/// <summary>
/// What a transport fault is classified as, before any layer decides what to do
/// about it.
/// </summary>
public enum SocketVerdict
{
    /// <summary>No socket error anywhere in the chain, so the caller's own rule decides.</summary>
    None,

    /// <summary>A fault that asking again can plausibly clear.</summary>
    Transient,

    /// <summary>A fault that will answer the same way every time. Retrying burns the attempts.</summary>
    Permanent,
}

/// <summary>
/// One rule for whether a transport fault is worth asking about again, shared by the
/// database layer and the provider client [D-100].
///
/// **The distinction is in <see cref="SocketException.SocketErrorCode"/>, not in the
/// exception type.** Retrying every <see cref="SocketException"/> is as wrong as
/// retrying none: <c>HostNotFound</c> is a configuration error and answers identically
/// three times while the backoff is spent for nothing, and a connection reset is the
/// commonest transient fault there is. Both arrive as the same type.
///
/// **The socket error decides wherever it sits in the chain.** Npgsql throws a bare
/// <see cref="SocketException"/> out of the connect phase and wraps one in an
/// <c>NpgsqlException</c> during the handshake; <see cref="System.Net.Http.HttpClient"/>
/// wraps one in an <c>HttpRequestException</c>. A predicate reading only the outermost
/// type would treat those three differently for one fault, which is how the database
/// layer came to retry a wrapped host-not-found while missing an unwrapped reset.
///
/// **Two layers, one rule, because two separately reasoned rules for one distinction
/// drift.** Each layer adds its own vocabulary on top: the provider client also retries
/// four HTTP statuses, and the database layer also retries the timeouts Npgsql raises
/// without a socket error behind them.
/// </summary>
public static class TransientFault
{
    /// <summary>
    /// The retryable socket errors, named here and nowhere else [D-100].
    ///
    /// <c>TimedOut</c>, <c>ConnectionReset</c>, <c>ConnectionRefused</c>,
    /// <c>HostUnreachable</c>, <c>NetworkUnreachable</c> and <c>TryAgain</c>. Everything
    /// else fails immediately, <c>HostNotFound</c> included.
    ///
    /// <c>TryAgain</c> is the resolver saying it could not answer this time rather than
    /// that there is nothing to answer, which is the one DNS failure that is transient.
    /// </summary>
    private static readonly SocketError[] Retryable =
    [
        SocketError.TimedOut,
        SocketError.ConnectionReset,
        SocketError.ConnectionRefused,
        SocketError.HostUnreachable,
        SocketError.NetworkUnreachable,
        SocketError.TryAgain,
    ];

    /// <summary>
    /// The verdict on an exception's socket error, or <see cref="SocketVerdict.None"/>
    /// where it carries none.
    ///
    /// The first socket error found walking outward-in decides. A chain carrying two is
    /// not a case that arises, and if it did the outer one is the one the caller saw.
    /// </summary>
    public static SocketVerdict ClassifySocket(Exception? exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                return Array.IndexOf(Retryable, socket.SocketErrorCode) >= 0
                    ? SocketVerdict.Transient
                    : SocketVerdict.Permanent;
            }
        }

        return SocketVerdict.None;
    }
}
