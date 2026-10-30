using System.IO;
using System.Net;
using System.Net.Sockets;

namespace ConnectIPN.Net;

internal sealed class LinkServer : IDisposable
{
    private TcpListener? _listener;

    public string Token { get; }
    public int Port { get; private set; }

    public LinkServer(string token)
    {
        Token = token;
    }

    public void Start(int preferredPort)
    {
        for (int port = preferredPort; port < preferredPort + 25; port++)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                return;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        throw new IOException("no free TCP port in range");
    }

    public RemoteClient Accept(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            throw new InvalidOperationException("listener is not started");
        }
        return RemoteClient.Accept(_listener, Token, cancellationToken);
    }

    public void Dispose()
    {
        _listener?.Stop();
        _listener = null;
    }
}
