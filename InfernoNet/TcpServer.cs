using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InfernoNet
{
    /// <summary>
    /// TCP server is used to connect, disconnect and manage TCP sessions.
    /// </summary>
    /// <remarks>Thread-safe.</remarks>
    public class TcpServer : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpServer"/> class.
        /// Initialize TCP server with a given IP address and port number.
        /// </summary>
        /// <param name="address">IP address.</param>
        /// <param name="port">Port number.</param>
        public TcpServer(IPAddress address, int port)
            : this(new IPEndPoint(address, port))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpServer"/> class.
        /// Initialize TCP server with a given IP address and port number.
        /// </summary>
        /// <param name="address">IP address.</param>
        /// <param name="port">Port number.</param>
        public TcpServer(string address, int port)
            : this(new IPEndPoint(IPAddress.Parse(address), port))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpServer"/> class.
        /// Initialize TCP server with a given IP endpoint.
        /// </summary>
        /// <param name="endpoint">IP endpoint.</param>
        public TcpServer(IPEndPoint endpoint)
        {
            this.Id = Guid.NewGuid();
            this.Endpoint = endpoint;
        }

        /// <summary>
        /// Server Id.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// IP endpoint.
        /// </summary>
        public IPEndPoint Endpoint { get; private set; }

        /// <summary>
        /// Number of sessions connected to the server.
        /// </summary>
        public long ConnectedSessions
        {
            get { return this.Sessions.Count; }
        }

        /// <summary>
        /// Number of bytes pending sent by the server.
        /// </summary>
        public long BytesPending
        {
            get { return this._bytesPending; }
        }

        /// <summary>
        /// Number of bytes sent by the server.
        /// </summary>
        public long BytesSent
        {
            get { return this._bytesSent; }
        }

        /// <summary>
        /// Number of bytes received by the server.
        /// </summary>
        public long BytesReceived
        {
            get { return this._bytesReceived; }
        }

        /// <summary>
        /// Option: acceptor backlog size.
        /// </summary>
        /// <remarks>
        /// This option will set the listening socket's backlog size.
        /// </remarks>
        public int OptionAcceptorBacklog { get; set; } = 1024;

        /// <summary>
        /// Option: dual mode socket.
        /// </summary>
        /// <remarks>
        /// Specifies whether the Socket is a dual-mode socket used for both IPv4 and IPv6.
        /// Will work only if socket is bound on IPv6 address.
        /// </remarks>
        public bool OptionDualMode { get; set; }

        /// <summary>
        /// Option: keep alive.
        /// </summary>
        /// <remarks>
        /// This option will setup SO_KEEPALIVE if the OS support this feature.
        /// </remarks>
        public bool OptionKeepAlive { get; set; }

        /// <summary>
        /// Option: no delay.
        /// </summary>
        /// <remarks>
        /// This option will enable/disable Nagle's algorithm for TCP protocol.
        /// </remarks>
        public bool OptionNoDelay { get; set; }

        /// <summary>
        /// Option: reuse address.
        /// </summary>
        /// <remarks>
        /// This option will enable/disable SO_REUSEADDR if the OS support this feature.
        /// </remarks>
        public bool OptionReuseAddress { get; set; }

        /// <summary>
        /// Option: enables a socket to be bound for exclusive access.
        /// </summary>
        /// <remarks>
        /// This option will enable/disable SO_EXCLUSIVEADDRUSE if the OS support this feature.
        /// </remarks>
        public bool OptionExclusiveAddressUse { get; set; }

        /// <summary>
        /// Option: receive buffer size.
        /// </summary>
        public int OptionReceiveBufferSize { get; set; } = 8192;

        /// <summary>
        /// Option: send buffer size.
        /// </summary>
        public int OptionSendBufferSize { get; set; } = 8192;

        #region Start/Stop server

        // Server acceptor
        private Socket _acceptorSocket;
        private SocketAsyncEventArgs _acceptorEventArg;

        // Server statistic
        internal long _bytesPending;
        internal long _bytesSent;
        internal long _bytesReceived;

        /// <summary>
        /// Is the server started?.
        /// </summary>
        public bool IsStarted { get; private set; }

        /// <summary>
        /// Is the server accepting new clients?.
        /// </summary>
        public bool IsAccepting { get; private set; }

        /// <summary>
        /// Create a new socket object.
        /// </summary>
        /// <remarks>
        /// Method may be override if you need to prepare some specific socket object in your implementation.
        /// </remarks>
        /// <returns>Socket object.</returns>
        protected virtual Socket CreateSocket()
        {
            return new Socket(this.Endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        }

        /// <summary>
        /// Start the server.
        /// </summary>
        /// <returns>'true' if the server was successfully started, 'false' if the server failed to start.</returns>
        public virtual bool Start()
        {
            Debug.Assert(!this.IsStarted, "TCP server is already started!");
            if (this.IsStarted)
                return false;

            // Setup acceptor event arg
            this._acceptorEventArg = new SocketAsyncEventArgs();
            this._acceptorEventArg.Completed += this.OnAsyncCompleted;

            // Create a new acceptor socket
            this._acceptorSocket = this.CreateSocket();

            // Update the acceptor socket disposed flag
            this.IsSocketDisposed = false;

            // Apply the option: reuse address
            this._acceptorSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, this.OptionReuseAddress);
            // Apply the option: exclusive address use
            this._acceptorSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, this.OptionExclusiveAddressUse);
            // Apply the option: dual mode (this option must be applied before listening)
            if (this._acceptorSocket.AddressFamily == AddressFamily.InterNetworkV6)
                this._acceptorSocket.DualMode = this.OptionDualMode;

            // Bind the acceptor socket to the IP endpoint
            this._acceptorSocket.Bind(this.Endpoint);
            // Refresh the endpoint property based on the actual endpoint created
            this.Endpoint = (IPEndPoint)this._acceptorSocket.LocalEndPoint;

            // Call the server starting handler
            this.OnStarting();

            // Start listen to the acceptor socket with the given accepting backlog size
            this._acceptorSocket.Listen(this.OptionAcceptorBacklog);

            // Reset statistic
            this._bytesPending = 0;
            this._bytesSent = 0;
            this._bytesReceived = 0;

            // Update the started flag
            this.IsStarted = true;

            // Call the server started handler
            this.OnStarted();

            // Perform the first server accept
            this.IsAccepting = true;
            this.StartAccept(this._acceptorEventArg);

            return true;
        }

        /// <summary>
        /// Stop the server.
        /// </summary>
        /// <returns>'true' if the server was successfully stopped, 'false' if the server is already stopped.</returns>
        public virtual bool Stop()
        {
            Debug.Assert(this.IsStarted, "TCP server is not started!");
            if (!this.IsStarted)
                return false;

            // Stop accepting new clients
            this.IsAccepting = false;

            // Reset acceptor event arg
            this._acceptorEventArg.Completed -= this.OnAsyncCompleted;

            // Call the server stopping handler
            this.OnStopping();

            try
            {
                // Close the acceptor socket
                this._acceptorSocket.Close();

                // Dispose the acceptor socket
                this._acceptorSocket.Dispose();

                // Dispose event arguments
                this._acceptorEventArg.Dispose();

                // Update the acceptor socket disposed flag
                this.IsSocketDisposed = true;
            }
            catch (ObjectDisposedException) { }

            // Disconnect all sessions
            this.DisconnectAll();

            // Update the started flag
            this.IsStarted = false;

            // Call the server stopped handler
            this.OnStopped();

            return true;
        }

        /// <summary>
        /// Restart the server.
        /// </summary>
        /// <returns>'true' if the server was successfully restarted, 'false' if the server failed to restart.</returns>
        public virtual bool Restart()
        {
            if (!this.Stop())
                return false;

            while (this.IsStarted)
                Thread.Yield();

            return this.Start();
        }

        #endregion

        #region Accepting clients

        /// <summary>
        /// Start accept a new client connection.
        /// </summary>
        private void StartAccept(SocketAsyncEventArgs e)
        {
            // Socket must be cleared since the context object is being reused
            e.AcceptSocket = null;

            // Async accept a new client connection
            if (!this._acceptorSocket.AcceptAsync(e))
                this.ProcessAccept(e);
        }

        /// <summary>
        /// Process accepted client connection.
        /// </summary>
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            if (e.SocketError == SocketError.Success)
            {
                // Create a new session to register
                var session = this.CreateSession();

                // Register the session
                this.RegisterSession(session);

                // Connect new session
                session.Connect(e.AcceptSocket);
            }
            else
                this.SendError(e.SocketError);

            // Accept the next client connection
            if (this.IsAccepting)
                this.StartAccept(e);
        }

        /// <summary>
        /// This method is the callback method associated with Socket.AcceptAsync()
        /// operations and is invoked when an accept operation is complete.
        /// </summary>
        private void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (this.IsSocketDisposed)
                return;

            this.ProcessAccept(e);
        }

        #endregion

        #region Session factory

        /// <summary>
        /// Create TCP session factory method.
        /// </summary>
        /// <returns>TCP session.</returns>
        protected virtual TcpSession CreateSession()
        {
            return new TcpSession(this);
        }

        #endregion

        #region Session management

        // Server sessions
        protected readonly ConcurrentDictionary<Guid, TcpSession> Sessions = new ConcurrentDictionary<Guid, TcpSession>();

        /// <summary>
        /// Disconnect all connected sessions.
        /// </summary>
        /// <returns>'true' if all sessions were successfully disconnected, 'false' if the server is not started.</returns>
        public virtual bool DisconnectAll()
        {
            if (!this.IsStarted)
                return false;

            // Disconnect all sessions
            foreach (var session in this.Sessions.Values)
                session.Disconnect();

            return true;
        }

        /// <summary>
        /// Find a session with a given Id.
        /// </summary>
        /// <param name="id">Session Id.</param>
        /// <returns>Session with a given Id or null if the session it not connected.</returns>
        public TcpSession FindSession(Guid id)
        {
            // Try to find the required session
            return this.Sessions.TryGetValue(id, out TcpSession result) ? result : null;
        }

        /// <summary>
        /// Register a new session.
        /// </summary>
        /// <param name="session">Session to register.</param>
        internal void RegisterSession(TcpSession session)
        {
            // Register a new session
            this.Sessions.TryAdd(session.Id, session);
        }

        /// <summary>
        /// Unregister session by Id.
        /// </summary>
        /// <param name="id">Session Id.</param>
        internal void UnregisterSession(Guid id)
        {
            // Unregister session by Id
            this.Sessions.TryRemove(id, out TcpSession temp);
        }

        #endregion

        #region Multicasting

        /// <summary>
        /// Multicast data to all connected sessions.
        /// </summary>
        /// <param name="buffer">Buffer to multicast.</param>
        /// <returns>'true' if the data was successfully multicasted, 'false' if the data was not multicasted.</returns>
        public virtual bool Multicast(byte[] buffer)
        {
            return this.Multicast(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Multicast data to all connected clients.
        /// </summary>
        /// <param name="buffer">Buffer to multicast.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>'true' if the data was successfully multicasted, 'false' if the data was not multicasted.</returns>
        public virtual bool Multicast(byte[] buffer, long offset, long size)
        {
            if (!this.IsStarted)
                return false;

            if (size == 0)
                return true;

            // Multicast data to all sessions
            foreach (var session in this.Sessions.Values)
                session.SendAsync(buffer, offset, size);

            return true;
        }

        /// <summary>
        /// Multicast text to all connected clients.
        /// </summary>
        /// <param name="text">Text string to multicast.</param>
        /// <returns>'true' if the text was successfully multicasted, 'false' if the text was not multicasted.</returns>
        public virtual bool Multicast(string text)
        {
            return this.Multicast(Encoding.UTF8.GetBytes(text));
        }

        #endregion

        #region Server handlers

        /// <summary>
        /// Handle server starting notification.
        /// </summary>
        protected virtual void OnStarting()
        {
        }

        /// <summary>
        /// Handle server started notification.
        /// </summary>
        protected virtual void OnStarted()
        {
        }

        /// <summary>
        /// Handle server stopping notification.
        /// </summary>
        protected virtual void OnStopping()
        {
        }

        /// <summary>
        /// Handle server stopped notification.
        /// </summary>
        protected virtual void OnStopped()
        {
        }

        /// <summary>
        /// Handle session connecting notification.
        /// </summary>
        /// <param name="session">Connecting session.</param>
        protected virtual void OnConnecting(TcpSession session)
        {
        }

        /// <summary>
        /// Handle session connected notification.
        /// </summary>
        /// <param name="session">Connected session.</param>
        protected virtual void OnConnected(TcpSession session)
        {
        }

        /// <summary>
        /// Handle session disconnecting notification.
        /// </summary>
        /// <param name="session">Disconnecting session.</param>
        protected virtual void OnDisconnecting(TcpSession session)
        {
        }

        /// <summary>
        /// Handle session disconnected notification.
        /// </summary>
        /// <param name="session">Disconnected session.</param>
        protected virtual void OnDisconnected(TcpSession session)
        {
        }

        /// <summary>
        /// Handle error notification.
        /// </summary>
        /// <param name="error">Socket error code.</param>
        protected virtual void OnError(SocketError error)
        {
        }

        internal void OnConnectingInternal(TcpSession session)
        {
            this.OnConnecting(session);
        }
        internal void OnConnectedInternal(TcpSession session)
        {
            this.OnConnected(session);
        }
        internal void OnDisconnectingInternal(TcpSession session)
        {
            this.OnDisconnecting(session);
        }
        internal void OnDisconnectedInternal(TcpSession session)
        {
            this.OnDisconnected(session);
        }

        #endregion

        #region Error handling

        /// <summary>
        /// Send error notification.
        /// </summary>
        /// <param name="error">Socket error code.</param>
        private void SendError(SocketError error)
        {
            // Skip disconnect errors
            if ((error == SocketError.ConnectionAborted) ||
                (error == SocketError.ConnectionRefused) ||
                (error == SocketError.ConnectionReset) ||
                (error == SocketError.OperationAborted) ||
                (error == SocketError.Shutdown))
                return;

            this.OnError(error);
        }

        #endregion

        #region IDisposable implementation

        /// <summary>
        /// Disposed flag.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Acceptor socket disposed flag.
        /// </summary>
        public bool IsSocketDisposed { get; private set; } = true;

        // Implement IDisposable.
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposingManagedResources)
        {
            // The idea here is that Dispose(Boolean) knows whether it is
            // being called to do explicit cleanup (the Boolean is true)
            // versus being called due to a garbage collection (the Boolean
            // is false). This distinction is useful because, when being
            // disposed explicitly, the Dispose(Boolean) method can safely
            // execute code using reference type fields that refer to other
            // objects knowing for sure that these other objects have not been
            // finalized or disposed of yet. When the Boolean is false,
            // the Dispose(Boolean) method should not execute code that
            // refer to reference type fields because those objects may
            // have already been finalized."

            if (!this.IsDisposed)
            {
                if (disposingManagedResources)
                {
                    // Dispose managed resources here...
                    this.Stop();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                this.IsDisposed = true;
            }
        }

        // Use C# destructor syntax for finalization code.
        ~TcpServer()
        {
            // Simply call Dispose(false).
            this.Dispose(false);
        }

        #endregion
    }
}
