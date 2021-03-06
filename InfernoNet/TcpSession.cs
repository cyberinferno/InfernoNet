using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace InfernoNet
{
    /// <summary>
    /// TCP session is used to read and write data from the connected TCP client.
    /// </summary>
    /// <remarks>Thread-safe.</remarks>
    public class TcpSession : IDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TcpSession"/> class.
        /// Initialize the session with a given server.
        /// </summary>
        /// <param name="server">TCP server.</param>
        public TcpSession(TcpServer server)
        {
            this.Id = Guid.NewGuid();
            this.Server = server;
            this.OptionReceiveBufferSize = server.OptionReceiveBufferSize;
            this.OptionSendBufferSize = server.OptionSendBufferSize;
        }

        /// <summary>
        /// Session Id.
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Server.
        /// </summary>
        public TcpServer Server { get; }

        /// <summary>
        /// Socket.
        /// </summary>
        public Socket Socket { get; private set; }

        /// <summary>
        /// Number of bytes pending sent by the session.
        /// </summary>
        public long BytesPending { get; private set; }

        /// <summary>
        /// Number of bytes sending by the session.
        /// </summary>
        public long BytesSending { get; private set; }

        /// <summary>
        /// Number of bytes sent by the session.
        /// </summary>
        public long BytesSent { get; private set; }

        /// <summary>
        /// Number of bytes received by the session.
        /// </summary>
        public long BytesReceived { get; private set; }

        /// <summary>
        /// Option: receive buffer size.
        /// </summary>
        public int OptionReceiveBufferSize { get; set; } = 8192;

        /// <summary>
        /// Option: send buffer size.
        /// </summary>
        public int OptionSendBufferSize { get; set; } = 8192;

        #region Connect/Disconnect session

        /// <summary>
        /// Is the session connected?.
        /// </summary>
        public bool IsConnected { get; private set; }

        /// <summary>
        /// Connect the session.
        /// </summary>
        /// <param name="socket">Session socket.</param>
        internal void Connect(Socket socket)
        {
            this.Socket = socket;

            // Update the session socket disposed flag
            this.IsSocketDisposed = false;

            // Setup buffers
            this._receiveBuffer = new Buffer();
            this._sendBufferMain = new Buffer();
            this._sendBufferFlush = new Buffer();

            // Setup event args
            this._receiveEventArg = new SocketAsyncEventArgs();
            this._receiveEventArg.Completed += this.OnAsyncCompleted;
            this._sendEventArg = new SocketAsyncEventArgs();
            this._sendEventArg.Completed += this.OnAsyncCompleted;

            // Apply the option: keep alive
            if (this.Server.OptionKeepAlive)
                this.Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            // Apply the option: no delay
            if (this.Server.OptionNoDelay)
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

            // Call the session connecting handler
            this.OnConnecting();

            // Call the session connecting handler in the server
            this.Server.OnConnectingInternal(this);

            // Update the connected flag
            this.IsConnected = true;

            // Try to receive something from the client
            this.TryReceive();

            // Check the socket disposed state: in some rare cases it might be disconnected while receiving!
            if (this.IsSocketDisposed)
                return;

            // Call the session connected handler
            this.OnConnected();

            // Call the session connected handler in the server
            this.Server.OnConnectedInternal(this);

            // Call the empty send buffer handler
            if (this._sendBufferMain.IsEmpty)
                this.OnEmpty();
        }

        /// <summary>
        /// Disconnect the session.
        /// </summary>
        /// <returns>'true' if the section was successfully disconnected, 'false' if the section is already disconnected.</returns>
        public virtual bool Disconnect()
        {
            if (!this.IsConnected)
                return false;

            // Reset event args
            this._receiveEventArg.Completed -= this.OnAsyncCompleted;
            this._sendEventArg.Completed -= this.OnAsyncCompleted;

            // Call the session disconnecting handler
            this.OnDisconnecting();

            // Call the session disconnecting handler in the server
            this.Server.OnDisconnectingInternal(this);

            try
            {
                try
                {
                    // Shutdown the socket associated with the client
                    this.Socket.Shutdown(SocketShutdown.Both);
                }
                catch (SocketException) { }

                // Close the session socket
                this.Socket.Close();

                // Dispose the session socket
                this.Socket.Dispose();

                // Dispose event arguments
                this._receiveEventArg.Dispose();
                this._sendEventArg.Dispose();

                // Update the session socket disposed flag
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

            // Call the session disconnected handler
            this.OnDisconnected();

            // Call the session disconnected handler in the server
            this.Server.OnDisconnectedInternal(this);

            // Unregister session
            this.Server.UnregisterSession(this.Id);

            return true;
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
        /// Send data to the client (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <returns>Size of sent data.</returns>
        public virtual long Send(byte[] buffer)
        {
            return this.Send(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Send data to the client (synchronous).
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

            // Sent data to the client
            long sent = this.Socket.Send(buffer, (int)offset, (int)size, SocketFlags.None, out SocketError ec);
            if (sent > 0)
            {
                // Update statistic
                this.BytesSent += sent;
                Interlocked.Add(ref this.Server._bytesSent, size);

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
        /// Send text to the client (synchronous).
        /// </summary>
        /// <param name="text">Text string to send.</param>
        /// <returns>Size of sent data.</returns>
        public virtual long Send(string text)
        {
            return this.Send(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Send data to the client (asynchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the session is not connected.</returns>
        public virtual bool SendAsync(byte[] buffer)
        {
            return this.SendAsync(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Send data to the client (asynchronous).
        /// </summary>
        /// <param name="buffer">Buffer to send.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>'true' if the data was successfully sent, 'false' if the session is not connected.</returns>
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
        /// Send text to the client (asynchronous).
        /// </summary>
        /// <param name="text">Text string to send.</param>
        /// <returns>'true' if the text was successfully sent, 'false' if the session is not connected.</returns>
        public virtual bool SendAsync(string text)
        {
            return this.SendAsync(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>
        /// Receive data from the client (synchronous).
        /// </summary>
        /// <param name="buffer">Buffer to receive.</param>
        /// <returns>Size of received data.</returns>
        public virtual long Receive(byte[] buffer)
        {
            return this.Receive(buffer, 0, buffer.Length);
        }

        /// <summary>
        /// Receive data from the client (synchronous).
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

            // Receive data from the client
            long received = this.Socket.Receive(buffer, (int)offset, (int)size, SocketFlags.None, out SocketError ec);
            if (received > 0)
            {
                // Update statistic
                this.BytesReceived += received;
                Interlocked.Add(ref this.Server._bytesReceived, received);

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
        /// Receive text from the client (synchronous).
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
        /// Receive data from the client (asynchronous).
        /// </summary>
        public virtual void ReceiveAsync()
        {
            // Try to receive data from the client
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
        /// This method is invoked when an asynchronous receive operation completes.
        /// </summary>
        private bool ProcessReceive(SocketAsyncEventArgs e)
        {
            if (!this.IsConnected)
                return false;

            long size = e.BytesTransferred;

            // Received some data from the client
            if (size > 0)
            {
                // Update statistic
                this.BytesReceived += size;
                Interlocked.Add(ref this.Server._bytesReceived, size);

                // Call the buffer received handler
                this.OnReceived(this._receiveBuffer.Data, 0, size);

                // If the receive buffer is full increase its size
                if (this._receiveBuffer.Capacity == size)
                    this._receiveBuffer.Reserve(2 * size);
            }

            this._receiving = false;

            // Try to receive again if the session is valid
            if (e.SocketError == SocketError.Success)
            {
                // If zero is returned from a read operation, the remote end has closed the connection
                if (size > 0)
                    return true;
                else
                    this.Disconnect();
            }
            else
            {
                this.SendError(e.SocketError);
                this.Disconnect();
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

            // Send some data to the client
            if (size > 0)
            {
                // Update statistic
                this.BytesSending -= size;
                this.BytesSent += size;
                Interlocked.Add(ref this.Server._bytesSent, size);

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

            // Try to send again if the session is valid
            if (e.SocketError == SocketError.Success)
                return true;
            else
            {
                this.SendError(e.SocketError);
                this.Disconnect();
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
        /// Notification is called when another chunk of buffer was received from the client.
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
        /// Notification is called when another chunk of buffer was sent to the client.
        /// This handler could be used to send another buffer to the client for instance when the pending size is zero.
        /// </remarks>
        protected virtual void OnSent(long sent, long pending)
        {
        }

        /// <summary>
        /// Handle empty send buffer notification.
        /// </summary>
        /// <remarks>
        /// Notification is called when the send buffer is empty and ready for a new data to send.
        /// This handler could be used to send another buffer to the client.
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
        /// Session socket disposed flag.
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
                    this.Disconnect();
                }

                // Dispose unmanaged resources here...

                // Set large fields to null here...

                // Mark as disposed.
                this.IsDisposed = true;
            }
        }

        // Use C# destructor syntax for finalization code.
        ~TcpSession()
        {
            // Simply call Dispose(false).
            this.Dispose(false);
        }

        #endregion
    }
}
