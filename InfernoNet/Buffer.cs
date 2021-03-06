using System;
using System.Diagnostics;
using System.Text;

namespace InfernoNet
{
    /// <summary>
    /// Dynamic byte buffer.
    /// </summary>
    public class Buffer
    {
        private byte[] _data;
        private long _size;
        private long _offset;

        /// <summary>
        /// Is the buffer empty?.
        /// </summary>
        public bool IsEmpty => (this._data == null) || (this._size == 0);

        /// <summary>
        /// Bytes memory buffer.
        /// </summary>
        public byte[] Data => this._data;

        /// <summary>
        /// Bytes memory buffer capacity.
        /// </summary>
        public long Capacity => this._data.Length;

        /// <summary>
        /// Bytes memory buffer size.
        /// </summary>
        public long Size => this._size;

        /// <summary>
        /// Bytes memory buffer offset.
        /// </summary>
        public long Offset => this._offset;

        /// <summary>
        /// Buffer indexer operator.
        /// </summary>
        public byte this[int index] => this._data[index];

        /// <summary>
        /// Initializes a new instance of the <see cref="Buffer"/> class.
        /// Initialize a new expandable buffer with zero capacity.
        /// </summary>
        public Buffer()
        {
            this._data = new byte[0]; this._size = 0; this._offset = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Buffer"/> class.
        /// Initialize a new expandable buffer with the given capacity.
        /// </summary>
        public Buffer(long capacity)
        {
            this._data = new byte[capacity]; this._size = 0; this._offset = 0;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Buffer"/> class.
        /// Initialize a new expandable buffer with the given data.
        /// </summary>
        public Buffer(byte[] data)
        {
            this._data = data; this._size = data.Length; this._offset = 0;
        }

        #region Memory buffer methods

        /// <summary>
        /// Get string from the current buffer.
        /// </summary>
        public override string ToString()
        {
            return this.ExtractString(0, this._size);
        }

        // Clear the current buffer and its offset
        public void Clear()
        {
            this._size = 0;
            this._offset = 0;
        }

        /// <summary>
        /// Extract the string from buffer of the given offset and size.
        /// </summary>
        public string ExtractString(long offset, long size)
        {
            Debug.Assert(((offset + size) <= this.Size), "Invalid offset & size!");
            if ((offset + size) > this.Size)
                throw new ArgumentException("Invalid offset & size!", nameof(offset));

            return Encoding.UTF8.GetString(this._data, (int)offset, (int)size);
        }

        /// <summary>
        /// Remove the buffer of the given offset and size.
        /// </summary>
        public void Remove(long offset, long size)
        {
            Debug.Assert(((offset + size) <= this.Size), "Invalid offset & size!");
            if ((offset + size) > this.Size)
                throw new ArgumentException("Invalid offset & size!", nameof(offset));

            Array.Copy(this._data, offset + size, this._data, offset, this._size - size - offset);
            this._size -= size;
            if (this._offset >= (offset + size))
                this._offset -= size;
            else if (this._offset >= offset)
            {
                this._offset -= this._offset - offset;
                if (this._offset > this.Size)
                    this._offset = this.Size;
            }
        }

        /// <summary>
        /// Reserve the buffer of the given capacity.
        /// </summary>
        public void Reserve(long capacity)
        {
            Debug.Assert((capacity >= 0), "Invalid reserve capacity!");
            if (capacity < 0)
                throw new ArgumentException("Invalid reserve capacity!", nameof(capacity));

            if (capacity > this.Capacity)
            {
                byte[] data = new byte[Math.Max(capacity, 2 * this.Capacity)];
                Array.Copy(this._data, 0, data, 0, this._size);
                this._data = data;
            }
        }

        // Resize the current buffer
        public void Resize(long size)
        {
            this.Reserve(size);
            this._size = size;
            if (this._offset > this._size)
                this._offset = this._size;
        }

        // Shift the current buffer offset
        public void Shift(long offset)
        {
            this._offset += offset;
        }
        // Unshift the current buffer offset
        public void Unshift(long offset)
        {
            this._offset -= offset;
        }

        #endregion

        #region Buffer I/O methods

        /// <summary>
        /// Append the given buffer.
        /// </summary>
        /// <param name="buffer">Buffer to append.</param>
        /// <returns>Count of append bytes.</returns>
        public long Append(byte[] buffer)
        {
            this.Reserve(this._size + buffer.Length);
            Array.Copy(buffer, 0, this._data, this._size, buffer.Length);
            this._size += buffer.Length;
            return buffer.Length;
        }

        /// <summary>
        /// Append the given buffer fragment.
        /// </summary>
        /// <param name="buffer">Buffer to append.</param>
        /// <param name="offset">Buffer offset.</param>
        /// <param name="size">Buffer size.</param>
        /// <returns>Count of append bytes.</returns>
        public long Append(byte[] buffer, long offset, long size)
        {
            this.Reserve(this._size + size);
            Array.Copy(buffer, offset, this._data, this._size, size);
            this._size += size;
            return size;
        }

        /// <summary>
        /// Append the given text in UTF-8 encoding.
        /// </summary>
        /// <param name="text">Text to append.</param>
        /// <returns>Count of append bytes.</returns>
        public long Append(string text)
        {
            this.Reserve(this._size + Encoding.UTF8.GetMaxByteCount(text.Length));
            long result = Encoding.UTF8.GetBytes(text, 0, text.Length, this._data, (int)this._size);
            this._size += result;
            return result;
        }

        #endregion
    }
}
