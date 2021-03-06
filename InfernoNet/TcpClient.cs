using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InfernoNet
{
    /// <summary>
    /// TCP client is used to read/write data from/into the connected TCP server.
    /// </summary>
    /// <remarks>Thread-safe.</remarks>
    public class TcpClient : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpClient"/> class.
        /// Initialize TCP client with a given server IP address and port number.
        /// </summary>
        /// <param name="address">IP address.</param>
        /// <param name="port">Port number.</param>
        public TcpClient(IPAddress address, int port)
            : this(new IPEndPoint(address, port))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpClient"/> class.
        /// Initialize TCP client with a given server IP address and port number.
        /// </summary>
        /// <param name="address">IP address.</param>
        /// <param name="port">Port number.</param>
        public TcpClient(string address, int port)
            : this(new IPEndPoint(IPAddress.Parse(address), port))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TcpClient"/> class.
        /// Initialize TCP client with a given IP endpoint.
        /// </summary>
        /// <param name="endpoint">IP endpoint.</param>
        public TcpClient(IPEndPoint endpoint)
        {
            this.Id = Guid.NewGuid();
            this.Endpoint = endpoint;
        }

        /// <summary>
        /// Client Id.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// IP endpoint.
        /// </summary>
        public IPEndPoint Endpoint { get; private set; }

        /// <summary>
        /// Socket.
        /// </summary>
        public Socket Socket { get; private set; }

        /// <summary>
        /// Number of bytes pending sent by the client.
        /// </summary>
        public long BytesPending { get; private set; }

        /// <summary>
        /// Number of bytes sending by the client.
        /// </summary>
        public long BytesSending { get; private set; }

        /// <summary>
        /// Number of bytes sent by the client.
        /// </summary>
        public long BytesSent { get; private set; }

        /// <summary>
        /// Number of bytes received by the client.
        /// </summary>
        public long BytesReceived { get; private set; }

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
        /// Option: receive buffer size.
        /// </summary>
        public int OptionReceiveBufferSize { get; set; } = 8192;

        /// <summary>
        /// Option: send buffer size.
        /// </summary>
        public int OptionSendBufferSize { get; set; } = 8192;

        #region Connect/Disconnect client

        private SocketAsyncEventArgs _connectEventArg;

        /// <summary>
        /// Is the client connecting?.
        /// </summary>
        public bool IsConnecting { get; private set; }

        /// <summary>
        /// Is the client connected?.
        /// </summary>
        public bool IsConnected { get; private set; }

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
        /// Connect the client (synchronous).
        /// </summary>
        /// <remarks>
        /// Please note that synchronous connect will not receive data automatically!
        /// You should use Receive() or ReceiveAsync() method manually after successful connection.
        /// </remarks>
        /// <returns>'true' if the client was successfully connected, 'false' if the client failed to connect.</returns>
        public virtual bool Connect()
        {
            if (this.IsConnected || this.IsConnecting)
                return false;

            // Setup buffers
            this._receiveBuffer = new Buffer();
            this._sendBufferMain = new Buffer();
            this._sendBufferFlush = new Buffer();

            // Setup event args
            this._connectEventArg = new SocketAsyncEventArgs();
            this._connectEventArg.RemoteEndPoint = this.Endpoint;
            this._connectEventArg.Completed += this.OnAsyncCompleted;
            this._receiveEventArg = new SocketAsyncEventArgs();
            this._receiveEventArg.Completed += this.OnAsyncCompleted;
            this._sendEventArg = new SocketAsyncEventArgs();
            this._sendEventArg.Completed += this.OnAsyncCompleted;

            // Create a new client socket
            this.Socket = this.CreateSocket();

            // Update the client socket disposed flag
            this.IsSocketDisposed = false;

            // Apply the option: dual mode (this option must be applied before connecting)
            if (this.Socket.AddressFamily == AddressFamily.InterNetworkV6)
                this.Socket.DualMode = this.OptionDualMode;

            // Call the client connecting handler
            this.OnConnecting();

            try
            {
                // Connect to the server
                this.Socket.Connect(this.Endpoint);
            }
            catch (SocketException ex)
            {
                // Call the client error handler
                this.SendError(ex.SocketErrorCode);

                // Reset event args
                this._connectEventArg.Completed -= this.OnAsyncCompleted;
                this._receiveEventArg.Completed -= this.OnAsyncCompleted;
                this._sendEventArg.Completed -= this.OnAsyncCompleted;

                // Call the client disconnecting handler
                this.OnDisconnecting();

                // Close the client socket
                this.Socket.Close();

                // Dispose the client socket
                this.Socket.Dispose();

                // Dispose event arguments
                this._connectEventArg.Dispose();
                this._receiveEventArg.Dispose();
                this._sendEventArg.Dispose();

                // Call the client disconnected handler
                this.OnDisconnected();

                return false;
            }

            // Apply the option: keep alive
            if (this.OptionKeepAlive)
                this.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Apply the option: no delay
            if (this.OptionNoDelay)
                this.Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

            // Prepare receive & send buffers
            this._receiveBuffer.Reserve(this.OptionReceiveBufferSize);
            this._sendBufferMain.Reserve(this.OptionSendBufferSize);
            this._sendBufferFlush.Reserve(this.OptionSendBufferSize);

            // Reset statistic
            this.BytesPending = 0;
            this.BytesSending = 0;
            this.BytesSent = 0;
            this.BytesReceived = 0;

            // Update the connected flag
            this.IsConnected = true;

            // Call the client connected handler
            this.OnConnected();

            // Call the empty send buffer handler
            if (this._sendBufferMain.IsEmpty)
                this.OnEmpty();

            return true;
        }

        /// <summary>
        /// Disconnect the client (synchronous).
        /// </summary>
        /// <returns>'true' if the client was successfully disconnected, 'false' if the client is already disconnected.</returns>
        public virtual bool Disconnect()
        {
            if (!this.IsConnected && !this.IsConnecting)
                return false;

            // Cancel connecting operation
            if (this.IsConnecting)
                Socket.CancelConnectAsync(this._connectEventArg);

            // Reset event args
            this._connectEventArg.Completed -= this.OnAsyncCompleted;
            this._receiveEventArg.Completed -= this.OnAsyncCompleted;
            this._sendEventArg.Completed -= this.OnAsyncCompleted;

            // Call the client disconnecting handler
            this.OnDisconnecting();

            try
            {
                try
                {
                    // Shutdown the socket associated with the client
                    this.Socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException) { }

                // Close the client socket
                this.Socket.Close();

                // Dispose the client socket
                this.Socket.Dispose();

                // Dispose event arguments
                this._connectEventArg.Dispose();
                this._receiveEventArg.Dispose();
                this._sendEventArg.Dispose();

                // Update the client socket disposed flag
                this.IsSocketDisposed = true;
            }
            catch (ObjectDisposedException) { }

            // Update the connected flag
            this.IsConnected = false;

            // Update sending/receiving flags
            this._receiving = false;
            this._sending = false;

            // Clear send/receive buffers
            this.ClearBuffers();

            // Call the client disconnected handler
            this.OnDisconnected();

            return true;
        }

        /// <summary>
        /// Reconnect the client (synchronous).
        /// </summary>
        /// <returns>'true' if the client was successfully reconnected, 'false' if the client is already reconnected.</returns>
        public virtual bool Reconnect()
        {
            if (!this.Disconnect())
                return false;

            return this.Connect();
        }

        /// <summary>
        /// Connect the client (asynchronous).
        /// </summary>
        /// <returns>'true' if the client was successfully connected, 'false' if the client failed to connect.</returns>
        public virtual bool ConnectAsync()
        {
            if (this.IsConnected || this.IsConnecting)
                return false;

            // Setup buffers
            this._receiveBuffer = new Buffer();
            this._sendBufferMain = new Buffer();
            this._sendBufferFlush = new Buffer();

            // Setup event args
            this._connectEventArg = new SocketAsyncEventArgs();
            this._connectEventArg.RemoteEndPoint = this.Endpoint;
            this._connectEventArg.Completed += this.OnAsyncCompleted;
            this._receiveEventArg = new SocketAsyncEventArgs();
            this._receiveEventArg.Completed += this.OnAsyncCompleted;
            this._sendEventArg = new SocketAsyncEventArgs();
            this._sendEventArg.Completed += this.OnAsyncCompleted;

            // Create a new client socket
            this.Socket = this.CreateSocket();

            // Update the client socket disposed flag
            this.IsSocketDisposed = false;

            // Apply the option: dual mode (this option must be applied before connecting)
            if (this.Socket.AddressFamily == AddressFamily.InterNetworkV6)
                this.Socket.DualMode = this.OptionDualMode;

            // Update the connecting flag
            this.IsConnecting = true;

            // Call the client connecting handler
            this.OnConnecting();

            // Async connect to the server
            if (!this.Socket.ConnectAsync(this._connectEventArg))
                this.ProcessConnect(this._connectEventArg);

            return true;
        }

        /// <summary>
        /// Disconnect the client (asynchronous).
        /// </summary>
        /// <returns>'true' if the client was successfully disconnected, 'false' if the client is already disconnected.</returns>
        public virtual bool DisconnectAsync()
        {
            return this.Disconnect();
        }

        /// <summary>
        /// Reconnect the client (asynchronous).
        /// </summary>
        /// <returns>'true' if the client was successfully reconnected, 'false' if the client is already reconnected.</returns>
        public virtual bool ReconnectAsync()
        {
            if (!this.DisconnectAsync())
                return false;

            while (this.IsConnected)
                Thread.Yield();

            return this.ConnectAsync();
        }

        #endregion

        #region Send/Recieve data

        // Receive buffer
        private bool _receiving;
        private Buffer _receiveBuffer;
        private SocketAsyncEventArgs _receiveEventArg;
        // Send buffer
        private readonly object _sendLock = new object();
        private bool _sending;
        private Buffer _sendBufferMain;
        private Buffer _sendBufferFlush;
        private SocketAsyncEventArgs _sendEventArg;
        private long _sendBufferFlushOffset;

        /// <summary>
        /// Send data to the server (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <returns>Size of sent data.</returns>
        public virtual long Send(byte[] buffer)
        {
            return this.Send(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Send data to the server (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>Size of sent data.</returns>
        public virtual long Send(byte[] buffer, long offset, long size)
        {
            if (!this.IsConnected)
                return 0;

            if (size == 0)
                return 0;

            // Sent data to the server
            long sent = this.Socket.Send(buffer, (int)offset, (int)size, SocketFlags.None, out SocketError ec);
            if (sent > 0)
            {
                // Update statistic
                this.BytesSent += sent;

                // Call the buffer sent handler
                this.OnSent(sent, this.BytesPending + this.BytesSending);
            }

            // Check for socket error
            if (ec != SocketError.Success)
            {
                this.SendError(ec);
                this.Disconnect();
            }

            return sent;
        }

        /// <summary>
        /// Send text to the server (synchronous).
        /// </summary>
        /// <param name="text">Text string to send.</param>
        /// <returns>Size of sent text.</returns>
        public virtual long Send(string text)
        {
            return this.Send(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Send data to the server (asynchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the client is not connected.</returns>
        public virtual bool SendAsync(byte[] buffer)
        {
            return this.SendAsync(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Send data to the server (asynchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the client is not connected.</returns>
        public virtual bool SendAsync(byte[] buffer, long offset, long size)
        {
            if (!this.IsConnected)
                return false;

            if (size == 0)
                return true;

            lock (this._sendLock)
            {
                // Fill the main send buffer
                this._sendBufferMain.Append(buffer, offset, size);

                // Update statistic
                this.BytesPending = this._sendBufferMain.Size;

                // Avoid multiple send handlers
                if (this._sending)
                    return true;
                else
                    this._sending = true;

                // Try to send the main buffer
                this.TrySend();
            }

            return true;
        }

        /// <summary>
        /// Send text to the server (asynchronous).
        /// </summary>
        /// <param name="text">Text string to send.</param>
        /// <returns>'true' if the text was successfully sent, 'false' if the client is not connected.</returns>
        public virtual bool SendAsync(string text)
        {
            return this.SendAsync(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Receive data from the server (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to receive.</param>
        /// <returns>Size of received data.</returns>
        public virtual long Receive(byte[] buffer)
        {
            return this.Receive(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Receive data from the server (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to receive.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>Size of received data.</returns>
        public virtual long Receive(byte[] buffer, long offset, long size)
        {
            if (!this.IsConnected)
                return 0;

            if (size == 0)
                return 0;

            // Receive data from the server
            long received = this.Socket.Receive(buffer, (int)offset, (int)size, SocketFlags.None, out SocketError ec);
            if (received > 0)
            {
                // Update statistic
                this.BytesReceived += received;

                // Call the buffer received handler
                this.OnReceived(buffer, 0, received);
            }

            // Check for socket error
            if (ec != SocketError.Success)
            {
                this.SendError(ec);
                this.Disconnect();
            }

            return received;
        }

        /// <summary>
        /// Receive text from the server (synchronous).
        /// </summary>
        /// <param name="size">Text size to receive.</param>
        /// <returns>Received text.</returns>
        public virtual string Receive(long size)
        {
            var buffer = new byte[size];
            var length = this.Receive(buffer);
            return Encoding.UTF8.GetString(buffer, 0, (int)length);
        }

        /// <summary>
        /// Receive data from the server (asynchronous).
        /// </summary>
        public virtual void ReceiveAsync()
        {
            // Try to receive data from the server
            this.TryReceive();
        }

        /// <summary>
        /// Try to receive new data.
        /// </summary>
        private void TryReceive()
        {
            if (this._receiving)
                return;

            if (!this.IsConnected)
                return;

            bool process = true;

            while (process)
            {
                process = false;

                try
                {
                    // Async receive with the receive handler
                    this._receiving = true;
                    this._receiveEventArg.SetBuffer(this._receiveBuffer.Data, 0, (int)this._receiveBuffer.Capacity);
                    if (!this.Socket.ReceiveAsync(this._receiveEventArg))
                        process = this.ProcessReceive(this._receiveEventArg);
                }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Try to send pending data.
        /// </summary>
        private void TrySend()
        {
            if (!this.IsConnected)
                return;

            bool empty = false;
            bool process = true;

            while (process)
            {
                process = false;

                lock (this._sendLock)
                {
                    // Is previous socket send in progress?
                    if (this._sendBufferFlush.IsEmpty)
                    {
                        // Swap flush and main buffers
                        this._sendBufferFlush = Interlocked.Exchange(ref this._sendBufferMain, this._sendBufferFlush);
                        this._sendBufferFlushOffset = 0;

                        // Update statistic
                        this.BytesPending = 0;
                        this.BytesSending += this._sendBufferFlush.Size;

                        // Check if the flush buffer is empty
                        if (this._sendBufferFlush.IsEmpty)
                        {
                            // Need to call empty send buffer handler
                            empty = true;

                            // End sending process
                            this._sending = false;
                        }
                    }
                    else
                        return;
                }

                // Call the empty send buffer handler
                if (empty)
                {
                    this.OnEmpty();
                    return;
                }

                try
                {
                    // Async write with the write handler
                    this._sendEventArg.SetBuffer(this._sendBufferFlush.Data, (int)this._sendBufferFlushOffset, (int)(this._sendBufferFlush.Size - this._sendBufferFlushOffset));
                    if (!this.Socket.SendAsync(this._sendEventArg))
                        process = this.ProcessSend(this._sendEventArg);
                }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>
        /// Clear send/receive buffers.
        /// </summary>
        private void ClearBuffers()
        {
            lock (this._sendLock)
            {
                // Clear send buffers
                this._sendBufferMain.Clear();
                this._sendBufferFlush.Clear();
                this._sendBufferFlushOffset= 0;

                // Update statistic
                this.BytesPending = 0;
                this.BytesSending = 0;
            }
        }

        #endregion

        #region IO processing

        /// <summary>
        /// This method is called whenever a receive or send operation is completed on a socket.
        /// </summary>
        private void OnAsyncCompleted(object sender, SocketAsyncEventArgs e)
        {
            if (this.IsSocketDisposed)
                return;

            // Determine which type of operation just completed and call the associated handler
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    this.ProcessConnect(e);
                    break;
                case SocketAsyncOperation.Receive:
                    if (this.ProcessReceive(e))
                        this.TryReceive();
                    break;
                case SocketAsyncOperation.Send:
                    if (this.ProcessSend(e))
                        this.TrySend();
                    break;
                default:
                    throw new ArgumentException("The last operation completed on the socket was not a receive or send");
            }

        }

        /// <summary>
        /// This method is invoked when an asynchronous connect operation completes.
        /// </summary>
        private void ProcessConnect(SocketAsyncEventArgs e)
        {
            this.IsConnecting = false;

            if (e.SocketError == SocketError.Success)
            {
                // Apply the option: keep alive
                if (this.OptionKeepAlive)
                    this.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                // Apply the option: no delay
                if (this.OptionNoDelay)
                    this.Socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);

                // Prepare receive & send buffers
                this._receiveBuffer.Reserve(this.OptionReceiveBufferSize);
                this._sendBufferMain.Reserve(this.OptionSendBufferSize);
                this._sendBufferFlush.Reserve(this.OptionSendBufferSize);

                // Reset statistic
                this.BytesPending = 0;
                this.BytesSending = 0;
                this.BytesSent = 0;
                this.BytesReceived = 0;

                // Update the connected flag
                this.IsConnected = true;

                // Try to receive something from the server
                this.TryReceive();

                // Check the socket disposed state: in some rare cases it might be disconnected while receiving!
                if (this.IsSocketDisposed)
                    return;

                // Call the client connected handler
                this.OnConnected();

                // Call the empty send buffer handler
                if (this._sendBufferMain.IsEmpty)
                    this.OnEmpty();
            }
            else
            {
                // Call the client disconnected handler
                this.SendError(e.SocketError);
                this.OnDisconnected();
            }
        }

        /// <summary>
        /// This method is invoked when an asynchronous receive operation completes.
        /// </summary>
        private bool ProcessReceive(SocketAsyncEventArgs e)
        {
            if (!this.IsConnected)
                return false;

            long size = e.BytesTransferred;

            // Received some data from the server
            if (size > 0)
            {
                // Update statistic
                this.BytesReceived += size;

                // Call the buffer received handler
                this.OnReceived(this._receiveBuffer.Data, 0, size);

                // If the receive buffer is full increase its size
                if (this._receiveBuffer.Capacity == size)
                    this._receiveBuffer.Reserve(2 * size);
            }

            this._receiving = false;

            // Try to receive again if the client is valid
            if (e.SocketError == SocketError.Success)
            {
                // If zero is returned from a read operation, the remote end has closed the connection
                if (size > 0)
                    return true;
                else
                    this.DisconnectAsync();
            }
            else
            {
                this.SendError(e.SocketError);
                this.DisconnectAsync();
            }

            return false;
        }

        /// <summary>
        /// This method is invoked when an asynchronous send operation completes.
        /// </summary>
        private bool ProcessSend(SocketAsyncEventArgs e)
        {
            if (!this.IsConnected)
                return false;

            long size = e.BytesTransferred;

            // Send some data to the server
            if (size > 0)
            {
                // Update statistic
                this.BytesSending -= size;
                this.BytesSent += size;

                // Increase the flush buffer offset
                this._sendBufferFlushOffset += size;

                // Successfully send the whole flush buffer
                if (this._sendBufferFlushOffset == this._sendBufferFlush.Size)
                {
                    // Clear the flush buffer
                    this._sendBufferFlush.Clear();
                    this._sendBufferFlushOffset = 0;
                }

                // Call the buffer sent handler
                this.OnSent(size, this.BytesPending + this.BytesSending);
            }

            // Try to send again if the client is valid
            if (e.SocketError == SocketError.Success)
                return true;
            else
            {
                this.SendError(e.SocketError);
                this.DisconnectAsync();
                return false;
            }
        }

        #endregion

        #region Session handlers

        /// <summary>
        /// Handle client connecting notification.
        /// </summary>
        protected virtual void OnConnecting()
        {
        }

        /// <summary>
        /// Handle client connected notification.
        /// </summary>
        protected virtual void OnConnected()
        {
        }

        /// <summary>
        /// Handle client disconnecting notification.
        /// </summary>
        protected virtual void OnDisconnecting()
        {
        }

        /// <summary>
        /// Handle client disconnected notification.
        /// </summary>
        protected virtual void OnDisconnected()
        {
        }

        /// <summary>
        /// Handle buffer received notification.
        /// </summary>
        /// <param name="buffer">Received buffer.</param>
        /// <param name="offset">Received buffer offset.</param>
        /// <param name="size">Received buffer size.</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was received from the server.
        /// </remarks>
        protected virtual void OnReceived(byte[] buffer, long offset, long size)
        {
        }

        /// <summary>
        /// Handle buffer sent notification.
        /// </summary>
        /// <param name="sent">Size of sent buffer.</param>
        /// <param name="pending">Size of pending buffer.</param>
        /// <remarks>
        /// Notification is called when another chunk of buffer was sent to the server.
        /// This handler could be used to send another buffer to the server for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(long sent, long pending)
        {
        }

        /// <summary>
        /// Handle empty send buffer notification.
        /// </summary>
        /// <remarks>
        /// Notification is called when the send buffer is empty and ready for a new data to send.
        /// This handler could be used to send another buffer to the server.
        /// </remarks>
        protected virtual void OnEmpty()
        {
        }

        /// <summary>
        /// Handle error notification.
        /// </summary>
        /// <param name="error">Socket error code.</param>
        protected virtual void OnError(SocketError error)
        {
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
        /// Client socket disposed flag.
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
                    this.DisconnectAsync();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                this.IsDisposed = true;
            }
        }

        // Use C# destructor syntax for finalization code.
        ~TcpClient()
        {
            // Simply call Dispose(false).
            this.Dispose(false);
        }

        #endregion
    }
}
